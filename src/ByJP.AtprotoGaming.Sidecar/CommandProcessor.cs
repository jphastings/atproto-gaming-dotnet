using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Sidecar;

/// <summary>A reply to send, plus whether to close the connection after sending it.</summary>
internal readonly struct ProcessResult
{
    public JsonObject Response { get; }
    public bool CloseAfter { get; }

    private ProcessResult(JsonObject response, bool closeAfter)
    {
        Response = response;
        CloseAfter = closeAfter;
    }

    public static ProcessResult Reply(JsonObject response) => new(response, false);
    public static ProcessResult Closing(JsonObject response) => new(response, true);
}

/// <summary>
/// Transport-agnostic translation of one wire request into a call on the library and
/// a reply. Mirrors <see cref="PlayUpdate"/> 1:1 (see <c>docs/wire-protocol.md</c>);
/// the replay-safe op construction, CAS write, outbox and signing all stay inside the
/// library — this only compiles intents into helper calls and shapes the reply.
/// </summary>
internal sealed class CommandProcessor
{
    private readonly AtprotoGamingClient _client;
    private readonly ApprovalService _approvals;
    private readonly IClock _clock;
    private readonly System.TimeSpan _initialDelay;
    private readonly System.TimeSpan _minPublishInterval;
    private readonly bool _signing;
    private readonly ILogSink _log;

    public CommandProcessor(AtprotoGamingClient client, ApprovalService approvals, IClock clock,
        System.TimeSpan initialDelay, System.TimeSpan minPublishInterval, bool signing, ILogSink log)
    {
        _client = client;
        _approvals = approvals;
        _clock = clock;
        _initialDelay = initialDelay;
        _minPublishInterval = minPublishInterval;
        _signing = signing;
        _log = log;
    }

    /// <summary>Processes one NDJSON request line against the connection's state.</summary>
    public async Task<ProcessResult> ProcessAsync(Connection conn, string line)
    {
        Request req;
        try { req = Request.Parse(line); }
        catch (WireException ex) { return ProcessResult.Reply(Error(null, ex.Code, ex.Message)); }

        var id = req.CorrelationId;
        try
        {
            return await DispatchAsync(conn, req, id).ConfigureAwait(false);
        }
        catch (WireException ex)
        {
            return ProcessResult.Reply(Error(id, ex.Code, ex.Message));
        }
        catch (System.ArgumentException ex)
        {
            // The library validates intents it's handed (bad AT-URI, array setting
            // value, missing id, non-SteamID64 participant); surface as invalidValue.
            return ProcessResult.Reply(Error(id, WireProtocol.Errors.InvalidValue, ex.Message));
        }
        catch (System.Exception ex)
        {
            _log.Error("sidecar command failed", ex);
            return ProcessResult.Reply(Error(id, WireProtocol.Errors.Internal, ex.Message));
        }
    }

    private async Task<ProcessResult> DispatchAsync(Connection conn, Request req, JsonNode? id)
    {
        var cmd = req.Command;
        if (cmd == "hello") return Hello(conn, req, id);

        if (!conn.Ready)
            throw new WireException(WireProtocol.Errors.NotReady, "send 'hello' before any other command");

        switch (cmd)
        {
            case "ping": return ProcessResult.Reply(Ok(id, ("type", "pong")));
            case "bye": return ProcessResult.Closing(Ok(id));
            case "open": return Open(conn, req, id);
            case "commit": return await CommitAsync(conn, id).ConfigureAwait(false);
            case "plays.list": return await PlaysListAsync(req, id).ConfigureAwait(false);
            default: return Mutate(conn, req, id, cmd);
        }
    }

    private ProcessResult Hello(Connection conn, Request req, JsonNode? id)
    {
        var protocol = req.Int("protocol");
        if (protocol != WireProtocol.Version)
            return ProcessResult.Closing(Error(id, WireProtocol.Errors.UnsupportedProtocol,
                $"server speaks protocol {WireProtocol.Version}"));

        var clientId = req.Str("clientId");
        var client = req.Str("client");
        ParseClient(client); // validate name/version up front (also used in versions on open)

        conn.Ready = true;
        conn.ClientId = clientId;
        conn.ClientName = client;

        var auth = _client.Auth;
        var body = Ok(id, ("type", "ready"));
        body["protocol"] = WireProtocol.Version;
        body["approval"] = _approvals.IsApproved(conn.ClientId) ? "approved" : "pending";
        body["auth"] = AuthString(auth.Status);
        if (!string.IsNullOrEmpty(auth.Did)) body["did"] = auth.Did;
        body["signing"] = _signing;
        return ProcessResult.Reply(body);
    }

    private ProcessResult Open(Connection conn, Request req, JsonNode? id)
    {
        string? warn = null;
        if (conn.Update is not null)
        {
            conn.Update = null;
            warn = "discarded uncommitted changes from the previous play";
        }

        var game = req.Str("game");
        var gameVersion = req.Str("gameVersion");
        var source = req.Str("source");

        if (req.Has("playId") == req.Has("derive"))
            throw new WireException(WireProtocol.Errors.InvalidValue, "provide exactly one of 'playId' or 'derive'");

        var playId = req.Has("playId") ? req.Str("playId") : Derive(req.Obj("derive"));
        var additional = ReadAdditionalVersions(req, conn.ClientName);

        var session = _client.OpenPlay(playId, game, gameVersion, source, additional);
        conn.Session = session;
        conn.Update = null;
        conn.OpenedUtc = _clock.UtcNow;
        conn.LastPublishUtc = null;
        conn.PendingTerminal = false;

        // No write here: the first commit creates the record, held back by the initial
        // delay (see CommitAsync) so the opening setup batches into one record.
        var body = Ok(id, ("type", "opened"));
        body["rkey"] = session.Rkey;
        if (warn != null) body["warn"] = warn;
        return ProcessResult.Reply(body);
    }

    private static string Derive(JsonObject derive)
    {
        var startedAt = AsString(derive["startedAt"])
            ?? throw new WireException(WireProtocol.Errors.MissingField, "derive.startedAt is required");
        var seed = AsString(derive["seed"])
            ?? throw new WireException(WireProtocol.Errors.MissingField, "derive.seed is required");
        return PlaySession.DerivePlayID(ParseIso(startedAt), seed);
    }

    private async Task<ProcessResult> CommitAsync(Connection conn, JsonNode? id)
    {
        if (conn.Session is null)
            throw new WireException(WireProtocol.Errors.NoSession, "no active play; send 'open' first");

        if (conn.Update is null || conn.Update.Count == 0)
        {
            conn.Update = null;
            return ProcessResult.Reply(Ok(id, ("type", "committed"), ("status", "noop")));
        }

        // Gate the actual publish on approval — but keep the buffered ops so a retry
        // after the player approves lands everything (the prompt fires once per client).
        if (conn.ClientId is null || !_approvals.IsApproved(conn.ClientId))
        {
            _approvals.RequestApproval(conn.ClientId ?? "", conn.ClientName ?? "unknown");
            return ProcessResult.Reply(Ok(id, ("type", "committed"), ("status", "pending")));
        }

        // Throttle: the first write waits out a short initial delay (measured from
        // open) so the opening setup batches into one record; later writes are ≤1 per
        // interval. An outcome/finish always flushes immediately. A deferred commit
        // keeps accumulating into the buffer.
        var firstWrite = conn.LastPublishUtc is null;
        var since = firstWrite ? conn.OpenedUtc : conn.LastPublishUtc;
        var threshold = firstWrite ? _initialDelay : _minPublishInterval;
        var elapsed = since is null ? System.TimeSpan.MaxValue : _clock.UtcNow - since.Value;
        if (!conn.PendingTerminal && elapsed < threshold)
            return ProcessResult.Reply(Ok(id, ("type", "committed"), ("status", "deferred")));

        var update = conn.Update;
        conn.Update = null; // a PlayUpdate commits exactly once — release it before awaiting
        conn.PendingTerminal = false;
        conn.LastPublishUtc = _clock.UtcNow;
        var result = await update.CommitAsync().ConfigureAwait(false);

        var body = Ok(id, ("type", "committed"));
        body["status"] = StatusString(result.Status);
        if (result.Uri != null) body["uri"] = result.Uri;
        if (result.Status == PutStatus.Dropped) body["warn"] = "no known DID; record discarded";
        return ProcessResult.Reply(body);
    }

    /// <summary>
    /// Lists the player's play records for a game (ended and in-progress), each with its
    /// rkey, timestamps, outcome, and an optional named metric value — so a game without
    /// seeds or save IDs can find the run to resume. With <c>value</c>, returns just the
    /// single closest play at or above that metric value (the likeliest save-state
    /// match), leaving the game to read its outcome and decide. Read-only — needs a
    /// signed-in sidecar but not client approval (play records are already public).
    /// </summary>
    private async Task<ProcessResult> PlaysListAsync(Request req, JsonNode? id)
    {
        var did = _client.Auth.Did;
        if (string.IsNullOrEmpty(did))
            return ProcessResult.Reply(Error(id, WireProtocol.Errors.Unavailable, "not signed in; can't list plays"));

        var game = req.Str("game");
        if (!AtUri.IsValid(game))
            throw new WireException(WireProtocol.Errors.InvalidValue, $"not a valid AT URI: {game}");
        var metricId = req.OptStr("metric");
        long? threshold = req.Has("value") ? req.Long("value") : null;
        if (threshold != null && metricId == null)
            throw new WireException(WireProtocol.Errors.InvalidValue, "'value' requires 'metric'");

        var found = new List<(JsonObject entry, long? metricValue)>();
        try
        {
            string? cursor = null;
            do
            {
                var page = await _client.Client.ListRecordsAsync(PlaySession.Collection, cursor, 100).ConfigureAwait(false);
                if (page?["records"] is JsonArray records)
                {
                    foreach (var node in records)
                    {
                        if (node is not JsonObject record || record["value"] is not JsonObject value) continue;
                        if (value["game"]?.GetValue<string>() != game) continue;

                        var entry = new JsonObject { ["rkey"] = RkeyOf(record["uri"]!.GetValue<string>()) };
                        if (value["startedAt"] is JsonNode startedAt) entry["startedAt"] = startedAt.DeepClone();
                        if (value["updatedAt"] is JsonNode updatedAt) entry["updatedAt"] = updatedAt.DeepClone();
                        if (value["outcome"] is JsonNode outcome) entry["outcome"] = outcome.DeepClone();

                        long? metricValue = null;
                        if (metricId != null && FindMetric(value, metricId) is JsonObject metric)
                        {
                            entry["value"] = metric["value"]!.DeepClone();
                            if (metric["scale"] is JsonNode scale) entry["scale"] = scale.DeepClone();
                            if (TryLong(metric["value"], out var mv)) metricValue = mv;
                        }
                        found.Add((entry, metricValue));
                    }
                }
                cursor = page?["cursor"]?.GetValue<string>();
            } while (!string.IsNullOrEmpty(cursor));
        }
        catch (AtprotoException ex)
        {
            return ProcessResult.Reply(Error(id, WireProtocol.Errors.Unavailable, $"couldn't reach the PDS: {ex.Message}"));
        }

        var plays = new JsonArray();
        if (threshold != null)
        {
            // Closest match at or above the threshold (smallest qualifying value),
            // tie-broken by most-recent — the run a save-state at this value belongs to.
            JsonObject? best = null;
            long bestValue = 0;
            foreach (var (entry, metricValue) in found)
            {
                if (metricValue is not long v || v < threshold.Value) continue;
                if (best == null || v < bestValue
                    || (v == bestValue && string.CompareOrdinal(When(entry), When(best)) > 0))
                {
                    best = entry;
                    bestValue = v;
                }
            }
            if (best != null) plays.Add(best);
        }
        else
        {
            found.Sort((a, b) => string.CompareOrdinal(When(b.entry), When(a.entry))); // most-recent first
            foreach (var (entry, _) in found) plays.Add(entry);
        }

        var body = Ok(id, ("type", "plays"));
        body["plays"] = plays;
        return ProcessResult.Reply(body);
    }

    private static bool TryLong(JsonNode? node, out long value)
    {
        value = 0;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && long.TryParse(v.ToJsonString(), out value);
    }

    private static string When(JsonObject entry) =>
        entry["updatedAt"]?.GetValue<string>() ?? entry["startedAt"]?.GetValue<string>() ?? "";

    private static string RkeyOf(string uri) => uri.Substring(uri.LastIndexOf('/') + 1);

    private static JsonObject? FindMetric(JsonObject record, string metricId)
    {
        if (record["state"] is not JsonArray state) return null;
        foreach (var node in state)
            if (node is JsonObject e
                && e["$type"]?.GetValue<string>() == PlaySession.MetricType
                && e["id"]?.GetValue<string>() == metricId)
                return e;
        return null;
    }

    private ProcessResult Mutate(Connection conn, Request req, JsonNode? id, string cmd)
    {
        if (conn.Session is null)
            throw new WireException(WireProtocol.Errors.NoSession, "no active play; send 'open' first");

        var u = conn.Update ??= conn.Session.BeginUpdate();
        string? warn = null;

        switch (cmd)
        {
            case "metric.set":
            {
                var name = req.Str("name"); warn = CamelWarn(name);
                u.SetMetric(name, req.Long("value"), req.OptInt("scale") ?? 0);
                break;
            }
            case "metric.update":
            {
                var name = req.Str("name"); warn = CamelWarn(name);
                u.UpdateMetric(name, req.Long("value"), ParseProgressOp(req.Str("op")));
                break;
            }
            case "setting.set":
            {
                var name = req.Str("name"); warn = CamelWarn(name);
                u.SetSetting(name, req.Node("value"));
                break;
            }
            case "setup.set":
                u.SetSetup(req.OptStr("mode"), req.OptStr("seed"), req.OptStr("character"), req.OptInt("difficulty"));
                break;
            case "setup.modifier":
                u.AddModifier(req.Str("id"), req.OptStr("name"), req.OptStr("value"));
                break;
            case "acquisition.add":
                u.AddAcquisition(req.Obj("entry"));
                break;
            case "acquisition.set":
                u.SetAcquisitions(Objects(req.Arr("items"), "items"));
                break;
            case "route.arrive":
                u.RouteArrive(req.Str("id"), req.OptStr("instanceId"), req.OptStr("name"), OptIso(req, "arrivedAt"));
                break;
            case "route.leave":
                u.RouteLeave(req.Str("id"), req.OptStr("instanceId"), OptIso(req, "leftAt"));
                break;
            case "outcome.set":
                u.SetOutcome(req.Str("type"), req.OptStr("cause"));
                conn.PendingTerminal = true;
                break;
            case "outcome.clear":
                u.ClearOutcome(); // un-ends the play (e.g. resuming a save-state); not terminal
                break;
            case "participants.set":
                u.SetParticipants(Objects(req.Arr("participants"), "participants"));
                break;
            case "finish":
                u.Finish(req.Str("endedAt"), req.Int("durationSeconds"));
                conn.PendingTerminal = true;
                break;
            case "state.replace":
                u.ReplaceState(req.Str("type"), req.Obj("entry"));
                break;
            case "state.upsert":
                u.UpsertState(req.Str("type"), req.Obj("entry"));
                break;
            case "state.append":
                u.AppendState(req.Str("type"), req.Obj("entry"));
                break;
            default:
                throw new WireException(WireProtocol.Errors.UnknownCommand, $"unknown command '{cmd}'");
        }

        var body = Ok(id);
        if (warn != null) body["warn"] = warn;
        return ProcessResult.Reply(body);
    }

    private static IReadOnlyDictionary<string, string>? ReadAdditionalVersions(Request req, string? client)
    {
        var map = new Dictionary<string, string>();
        if (req.Has("additionalVersions"))
        {
            var obj = req.Obj("additionalVersions");
            foreach (var kv in obj)
                map[kv.Key] = AsString(kv.Value)
                    ?? throw new WireException(WireProtocol.Errors.InvalidValue,
                        $"additionalVersions['{kv.Key}'] must be a string");
        }
        // Record the emitter's own name/version (from hello's `client`) alongside any
        // it declared explicitly.
        if (client is not null)
        {
            var (name, version) = ParseClient(client);
            map[name] = version;
        }
        return map.Count == 0 ? null : map;
    }

    /// <summary>Validates and splits a <c>name/version</c> client string (e.g. <c>mgba-atproto/0.1</c>).</summary>
    private static (string name, string version) ParseClient(string client)
    {
        var parts = client.Split('/');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0 || HasWhitespace(client))
            throw new WireException(WireProtocol.Errors.InvalidValue,
                "'client' must be in name/version form, e.g. \"mgba-atproto/0.1\"");
        return (parts[0], parts[1]);
    }

    private static bool HasWhitespace(string s)
    {
        foreach (var c in s)
            if (char.IsWhiteSpace(c)) return true;
        return false;
    }

    private static IEnumerable<JsonObject> Objects(JsonArray array, string field)
    {
        var list = new List<JsonObject>();
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
                throw new WireException(WireProtocol.Errors.InvalidValue, $"every '{field}' entry must be an object");
            list.Add((JsonObject)obj.DeepClone());
        }
        return list;
    }

    private static ProgressOp ParseProgressOp(string op) => op.ToLowerInvariant() switch
    {
        "add" => ProgressOp.Add,
        "subtract" => ProgressOp.Subtract,
        "min" => ProgressOp.Min,
        "max" => ProgressOp.Max,
        _ => throw new WireException(WireProtocol.Errors.InvalidValue,
            $"unknown metric op '{op}' (expected add, subtract, min, or max)"),
    };

    private static System.DateTimeOffset? OptIso(Request req, string field) =>
        req.Has(field) ? ParseIso(req.Str(field)) : null;

    private static System.DateTimeOffset ParseIso(string value)
    {
        if (!System.DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var when))
            throw new WireException(WireProtocol.Errors.InvalidValue, $"'{value}' is not an ISO-8601 timestamp");
        return when;
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Non-camelCase keys still succeed (matching the BAG001 analyzer's advisory severity) but warn.</summary>
    private static string? CamelWarn(string key) =>
        IsCamelCase(key) ? null : $"key \"{key}\" is not camelCase (atproto convention)";

    private static bool IsCamelCase(string s)
    {
        if (s.Length == 0 || s[0] < 'a' || s[0] > 'z') return false;
        foreach (var c in s)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
                return false;
        return true;
    }

    private static string AuthString(AuthStatus status) => status switch
    {
        AuthStatus.Ok => "ok",
        AuthStatus.Offline => "offline",
        AuthStatus.Failed => "failed",
        AuthStatus.Checking => "checking",
        _ => "unconfigured",
    };

    private static string StatusString(PutStatus status) => status switch
    {
        PutStatus.Published => "published",
        PutStatus.Queued => "queued",
        _ => "dropped",
    };

    private static JsonObject Ok(JsonNode? id, params (string key, JsonNode value)[] extra)
    {
        var o = new JsonObject();
        if (id != null) o["re"] = id.DeepClone();
        o["ok"] = true;
        foreach (var (key, value) in extra) o[key] = value;
        return o;
    }

    private static JsonObject Error(JsonNode? id, string code, string message)
    {
        var o = new JsonObject();
        if (id != null) o["re"] = id.DeepClone();
        o["ok"] = false;
        o["error"] = code;
        o["message"] = message;
        return o;
    }
}
