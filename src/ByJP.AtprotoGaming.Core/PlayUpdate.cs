using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// An open transaction over a <c>games.gamesgamesgamesgames.actor.play</c>
    /// record. The helper methods record changes as serializable ops in memory (no
    /// network); a single <see cref="CommitAsync"/> writes them all as one record
    /// update. While offline the ops are persisted and re-applied against the real
    /// record at flush, so an <see cref="UpdateProgress"/> resolves against the
    /// actual value rather than a stale one.
    /// </summary>
    /// <remarks>
    /// Open one with <see cref="PlaySession.BeginUpdate"/>. Helpers may be called
    /// across many frames/handlers before committing; calls are thread-safe and
    /// chainable. A transaction commits exactly once.
    /// </remarks>
    public sealed class PlayUpdate
    {
        private readonly Func<JsonArray, Task<PutResult>> _commit;
        private readonly IClock _clock;
        private readonly object _lock = new object();
        private readonly JsonArray _ops = new JsonArray();
        private bool _committed;

        internal PlayUpdate(Func<JsonArray, Task<PutResult>> commit, IClock clock)
        {
            _commit = commit;
            _clock = clock;
        }

        /// <summary>Number of changes recorded so far.</summary>
        public int Count { get { lock (_lock) return _ops.Count; } }

        /// <summary>Sets an open-ended <c>progress</c> indicator (e.g. <c>"score"</c>, <c>"hp"</c>, <c>"gold"</c>).</summary>
        public PlayUpdate SetProgress(string name, AtValue value)
        {
            GuardProgressName(name);
            return Record(new JsonObject { ["op"] = "setProgress", ["name"] = name, ["value"] = value.ToNode() });
        }

        /// <summary>
        /// Combines <paramref name="value"/> with an integer <c>progress</c> indicator
        /// using <paramref name="operation"/>, resolved against the real value at
        /// write time. Fails at commit/flush if the existing value isn't an integer.
        /// </summary>
        public PlayUpdate UpdateProgress(string name, long value, ProgressOp operation)
        {
            GuardProgressName(name);
            return Record(new JsonObject
            {
                ["op"] = "updateProgress",
                ["name"] = name,
                ["value"] = value,
                ["operation"] = operation.ToString().ToLowerInvariant(),
            });
        }

        /// <summary>Replaces <c>acquisitions[]</c> with the given items (each a <c>#gameItem</c>; <c>id</c> required).</summary>
        public PlayUpdate SetAcquisitions(IEnumerable<JsonObject> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            var array = new JsonArray();
            foreach (var item in items)
            {
                var snap = RequireId(item, "acquisition");
                EnsureType(snap, PlaySession.GameItemType);
                array.Add(snap);
            }
            return Record(new JsonObject { ["op"] = "setAcquisitions", ["items"] = array });
        }

        /// <summary>
        /// Appends an acquired item to <c>acquisitions[]</c> (a <c>#gameItem</c>;
        /// <c>id</c> required). If the item carries an <c>instanceId</c>, a later add
        /// with the same <c>instanceId</c> updates that entry instead of duplicating
        /// it — so a re-emit after a crash is idempotent.
        /// </summary>
        public PlayUpdate AddAcquisition(JsonObject item)
        {
            var snap = RequireId(item, "acquisition");
            EnsureType(snap, PlaySession.GameItemType);
            return Record(new JsonObject { ["op"] = "addAcquisition", ["item"] = snap });
        }

        /// <summary>
        /// Records arrival at a route stop (appends a <c>#routeStop</c> to
        /// <c>progress.route[]</c>). An <paramref name="instanceId"/> makes the arrival
        /// idempotent and lets <see cref="RouteLeave"/> target this exact stop.
        /// </summary>
        public PlayUpdate RouteArrive(string id, string? instanceId = null, string? name = null,
            DateTimeOffset? arrivedAt = null)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            var op = new JsonObject
            {
                ["op"] = "routeArrive",
                ["id"] = id,
                ["arrivedAt"] = Iso(arrivedAt),
            };
            if (instanceId != null) op["instanceId"] = instanceId;
            if (name != null) op["name"] = name;
            return Record(op);
        }

        /// <summary>
        /// Records leaving a route stop: sets <c>leftAt</c> on the matching open stop
        /// (by <paramref name="instanceId"/> if given, else the last open stop with
        /// this <paramref name="id"/>). Resolved against the real record at write time.
        /// </summary>
        public PlayUpdate RouteLeave(string id, string? instanceId = null, DateTimeOffset? leftAt = null)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            var op = new JsonObject
            {
                ["op"] = "routeLeave",
                ["id"] = id,
                ["leftAt"] = Iso(leftAt),
            };
            if (instanceId != null) op["instanceId"] = instanceId;
            return Record(op);
        }

        /// <summary>Sets <c>progress.outcome</c> (the end-of-play marker).</summary>
        public PlayUpdate SetOutcome(string type, string? cause = null)
        {
            if (string.IsNullOrEmpty(type)) throw new ArgumentNullException(nameof(type));
            var op = new JsonObject { ["op"] = "setOutcome", ["type"] = type };
            if (!string.IsNullOrEmpty(cause)) op["cause"] = cause;
            return Record(op);
        }

        /// <summary>Sets a pre-run value in <c>settings</c> (e.g. <c>"character"</c>, <c>"difficulty"</c>, <c>"seed"</c>).</summary>
        public PlayUpdate SetSetting(string name, AtValue value)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            return Record(new JsonObject { ["op"] = "setSetting", ["name"] = name, ["value"] = value.ToNode() });
        }

        /// <summary>Sets <c>playingWith[]</c> to the given participants, replacing any previous list.</summary>
        public PlayUpdate SetPlayingWith(IEnumerable<JsonObject> participants)
        {
            if (participants == null) throw new ArgumentNullException(nameof(participants));
            var array = new JsonArray();
            foreach (var participant in participants)
            {
                if (participant == null) throw new ArgumentException("participant cannot be null", nameof(participants));
                if (participant.TryGetPropertyValue("steam", out var steam) && steam != null && !IsSteamId64(steam))
                    throw new ArgumentException(
                        "participant 'steam' must be a SteamID64 — a 17-digit decimal like 76561197994000231 — " +
                        $"but got {steam.ToJsonString()}. A SteamID2 (STEAM_X:Y:Z), SteamID3 ([U:1:N]) or bare " +
                        "32-bit account id is not a SteamID64; convert it first.",
                        nameof(participants));
                array.Add((JsonObject)participant.DeepClone());
            }
            return Record(new JsonObject { ["op"] = "setPlayingWith", ["participants"] = array });
        }

        // Individual SteamID64s are a 17-digit decimal ≥ 76561197960265728. Validating the
        // shape here catches the easy mistakes (SteamID2/SteamID3/account id) at the call
        // site, where the consumer can see what went in — not as silent bad data on the PDS.
        private const ulong MinIndividualSteamId64 = 76561197960265728UL;

        private static bool IsSteamId64(JsonNode steam)
        {
            if (!(steam is JsonValue value) || !value.TryGetValue<string>(out var s) || string.IsNullOrEmpty(s))
                return false;
            foreach (var c in s) if (c < '0' || c > '9') return false;
            return ulong.TryParse(s, out var id) && id >= MinIndividualSteamId64;
        }

        /// <summary>Marks the play-through finished: sets <c>endedAt</c> and <c>duration</c>.</summary>
        public PlayUpdate Finish(string endedAtIso, int durationSeconds)
        {
            if (string.IsNullOrEmpty(endedAtIso)) throw new ArgumentNullException(nameof(endedAtIso));
            return Record(new JsonObject { ["op"] = "finish", ["endedAt"] = endedAtIso, ["duration"] = durationSeconds });
        }

        /// <summary>
        /// Writes every recorded change as one record update (online: a single PUT
        /// with optimistic locking; offline: persisted and flushed later), stamping
        /// <c>updatedAt</c>. A transaction commits exactly once.
        /// </summary>
        public Task<PutResult> CommitAsync()
        {
            JsonArray snapshot;
            lock (_lock)
            {
                if (_committed) throw new InvalidOperationException("this play update has already been committed");
                _committed = true;
                snapshot = (JsonArray)_ops.DeepClone();
            }
            return _commit(snapshot);
        }

        private PlayUpdate Record(JsonObject op)
        {
            lock (_lock)
            {
                if (_committed) throw new InvalidOperationException("this play update has already been committed");
                _ops.Add(op);
            }
            return this;
        }

        private string Iso(DateTimeOffset? when) =>
            (when ?? _clock.UtcNow).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        // progress.outcome and progress.route are structured and have dedicated
        // helpers — steer callers there rather than letting them clobber the shape.
        private static void GuardProgressName(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (name == "outcome")
                throw new ArgumentException("use SetOutcome(...) to set progress.outcome", nameof(name));
            if (name == "route")
                throw new ArgumentException("use RouteArrive(...)/RouteLeave(...) for progress.route", nameof(name));
        }

        private static void EnsureType(JsonObject node, string type)
        {
            if (node["$type"] == null) node["$type"] = type;
        }

        private static JsonObject RequireId(JsonObject node, string what)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrEmpty(node["id"]?.GetValue<string>()))
                throw new ArgumentException($"{what} requires a non-empty 'id'", nameof(node));
            return (JsonObject)node.DeepClone();
        }
    }
}
