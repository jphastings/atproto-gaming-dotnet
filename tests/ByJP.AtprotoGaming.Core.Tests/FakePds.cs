using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core;
using ByJP.AtprotoGaming.Core.Adapters;
using ByJP.AtprotoGaming.Core.Signing;

namespace ByJP.AtprotoGaming.Core.Tests;

/// <summary>
/// An in-memory PDS behind an <see cref="HttpMessageHandler"/>: enough of the XRPC
/// surface to exercise the publisher/editor end-to-end, with real swapRecord
/// (compare-and-swap) semantics.
/// </summary>
internal sealed class FakePds : HttpMessageHandler
{
    public const string TestDid = "did:plc:testtesttesttest";
    public const string BaseUrl = "https://pds.test";

    private readonly Dictionary<string, (JsonNode value, string cid)> _store = new();
    private int _cidSeq;

    public int PutCount { get; private set; }
    public int GetCount { get; private set; }
    public int ListCount { get; private set; }
    public string? LastSwapRecord { get; private set; }

    /// <summary>When true, every swapRecord PUT is rejected as InvalidSwap (moving target).</summary>
    public bool AlwaysConflict { get; set; }

    /// <summary>Optional hook to force a response (e.g. simulate a network error by throwing).</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    private static string Key(string c, string r) => c + "/" + r;
    private string NextCid() => "bafyreitestcid" + (++_cidSeq).ToString("D4");

    public (JsonNode value, string cid)? Stored(string collection, string rkey) =>
        _store.TryGetValue(Key(collection, rkey), out var v) ? v : ((JsonNode, string)?)null;

    /// <summary>Pre-populate a record (e.g. existing stats records for resolver tests).</summary>
    public void Seed(string collection, string rkey, JsonObject value) =>
        _store[Key(collection, rkey)] = (value.DeepClone(), NextCid());

    /// <summary>Simulate another writer changing the record: mutate the value and bump its CID.</summary>
    public void MutateExternally(string collection, string rkey, Action<JsonObject> mutate)
    {
        var cur = _store[Key(collection, rkey)];
        var v = (JsonObject)cur.value.DeepClone();
        mutate(v);
        _store[Key(collection, rkey)] = (v, NextCid());
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var forced = Intercept?.Invoke(request);
        if (forced != null) return forced;

        var path = request.RequestUri!.AbsolutePath;
        var nsid = path.Substring(path.LastIndexOf('/') + 1);
        JsonNode? body = null;
        if (request.Content != null)
            body = JsonNode.Parse(await request.Content.ReadAsStringAsync().ConfigureAwait(false));

        switch (nsid)
        {
            case "com.atproto.server.createSession":
                return Ok(new JsonObject
                {
                    ["did"] = TestDid,
                    ["handle"] = "test.bsky.social",
                    ["accessJwt"] = "access",
                    ["refreshJwt"] = "refresh",
                });

            case "com.atproto.server.refreshSession":
                return Ok(new JsonObject { ["accessJwt"] = "access2", ["refreshJwt"] = "refresh2" });

            case "com.atproto.repo.getRecord":
            {
                GetCount++;
                var c = Query(request, "collection");
                var r = Query(request, "rkey");
                if (_store.TryGetValue(Key(c, r), out var rec))
                    return Ok(new JsonObject
                    {
                        ["uri"] = $"at://{TestDid}/{c}/{r}",
                        ["cid"] = rec.cid,
                        ["value"] = rec.value.DeepClone(),
                    });
                return Err(400, "RecordNotFound", "record not found");
            }

            case "com.atproto.repo.putRecord":
            {
                PutCount++;
                var c = body!["collection"]!.GetValue<string>();
                var r = body["rkey"]!.GetValue<string>();
                var swap = body["swapRecord"]?.GetValue<string>();
                LastSwapRecord = swap;
                var exists = _store.TryGetValue(Key(c, r), out var cur);

                if (AlwaysConflict && swap != null)
                {
                    if (exists) _store[Key(c, r)] = (cur.value, NextCid()); // keep moving
                    return Err(400, "InvalidSwap", "record changed");
                }
                if (swap != null && (!exists || cur.cid != swap))
                    return Err(400, "InvalidSwap", "swapRecord mismatch");

                var newCid = NextCid();
                _store[Key(c, r)] = (body["record"]!.DeepClone(), newCid);
                return Ok(new JsonObject { ["uri"] = $"at://{TestDid}/{c}/{r}", ["cid"] = newCid });
            }

            case "com.atproto.repo.createRecord":
            {
                var c = body!["collection"]!.GetValue<string>();
                var r = "rk" + (++_cidSeq).ToString("D4");
                var newCid = NextCid();
                _store[Key(c, r)] = (body["record"]!.DeepClone(), newCid);
                return Ok(new JsonObject { ["uri"] = $"at://{TestDid}/{c}/{r}", ["cid"] = newCid });
            }

            case "com.atproto.repo.listRecords":
            {
                ListCount++;
                var c = Query(request, "collection");
                var records = new JsonArray();
                foreach (var kv in _store)
                {
                    var slash = kv.Key.IndexOf('/');
                    if (kv.Key.Substring(0, slash) != c) continue;
                    var r = kv.Key.Substring(slash + 1);
                    records.Add(new JsonObject
                    {
                        ["uri"] = $"at://{TestDid}/{c}/{r}",
                        ["cid"] = kv.Value.cid,
                        ["value"] = kv.Value.value.DeepClone(),
                    });
                }
                return Ok(new JsonObject { ["records"] = records });
            }

            default:
                return Err(404, "MethodNotImplemented", nsid);
        }
    }

    private static HttpResponseMessage Ok(JsonNode body) => Resp(200, body);

    private static HttpResponseMessage Err(int code, string error, string message) =>
        Resp(code, new JsonObject { ["error"] = error, ["message"] = message });

    private static HttpResponseMessage Resp(int code, JsonNode body) =>
        new HttpResponseMessage((HttpStatusCode)code)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

    private static string Query(HttpRequestMessage req, string key)
    {
        var query = req.RequestUri!.Query.TrimStart('?');
        foreach (var pair in query.Split('&'))
        {
            var i = pair.IndexOf('=');
            if (i > 0 && Uri.UnescapeDataString(pair.Substring(0, i)) == key)
                return Uri.UnescapeDataString(pair.Substring(i + 1));
        }
        return "";
    }
}

/// <summary>Wires a publisher + play subsystem against a <see cref="FakePds"/> with the auth state pre-set.</summary>
internal sealed class Harness : IDisposable
{
    public FakePds Pds { get; } = new();
    public AuthState Auth { get; } = new();
    public CapturingLogSink Log { get; } = new();
    public TempFileSystem Fs { get; } = new();
    public AtprotoClient Client { get; }
    public Outbox Outbox { get; }
    public RecordPublisher Records { get; }
    public ConfigStore<CoreConfig> Config { get; }
    public PlayWriter PlayWriter { get; }
    public RollingStats Stats { get; }

    private readonly HttpClient _http;

    private Harness(SigningKey? signingKey)
    {
        _http = new HttpClient(Pds);
        var clock = new FixedClock();
        Client = new AtprotoClient(Auth, clock, Log, _http);
        Outbox = new Outbox(Fs, Log, Auth, Client);
        var versions = new VersionsInjector("9.9.9");
        var signer = signingKey != null ? new RecordSigner(signingKey) : null;
        Records = new RecordPublisher(Client, Auth, Outbox, Log, versions, signer);

        Config = ConfigStore<CoreConfig>.LoadOrCreate(Fs, Log);
        var resolver = new StatsResolver(Client, Config, clock);
        PlayWriter = new PlayWriter(Client, Auth, clock, Log, resolver, new PlayQueue(Fs, Log), versions, signer);
        Stats = new RollingStats(Client, resolver, clock);
    }

    /// <summary>Opens a play session like AtprotoGamingClient.OpenPlay, with a fixed startedAt.</summary>
    public PlaySession OpenPlay(string playId, string game, string source,
        System.Collections.Generic.IReadOnlyDictionary<string, string>? mods = null)
    {
        var rkey = RecordKey.Sanitize(playId);
        System.Text.Json.Nodes.JsonObject Seed() =>
            PlaySession.BuildSeed(game, "1.0.0", mods, "2026-06-07T11:00:00.0000000Z");
        return new PlaySession(PlayWriter, rkey, Seed, source);
    }

    public static async Task<Harness> OnlineAsync(SigningKey? signingKey = null)
    {
        var h = new Harness(signingKey);
        await h.Client.LoginAsync(FakePds.BaseUrl, "test.bsky.social", "pw");
        h.Auth.Set(AuthStatus.Ok, handle: "test.bsky.social", did: FakePds.TestDid, pds: FakePds.BaseUrl);
        return h;
    }

    public static Harness Offline(SigningKey? signingKey = null)
    {
        var h = new Harness(signingKey);
        h.Auth.Set(AuthStatus.Offline, handle: "test.bsky.social", did: FakePds.TestDid, pds: FakePds.BaseUrl);
        return h;
    }

    public void Dispose()
    {
        _http.Dispose();
        Fs.Dispose();
    }
}
