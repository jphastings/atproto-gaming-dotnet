using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// An open transaction over a <c>games.gamesgamesgamesgames.experimental.actor.play</c>
    /// record. The helper methods record changes as serializable ops in memory (no
    /// network); a single <see cref="CommitAsync"/> writes them all as one record
    /// update. While offline the ops are persisted and re-applied against the real
    /// record at flush, so an <see cref="UpdateMetric"/> resolves against the
    /// actual value rather than a stale one.
    /// </summary>
    /// <remarks>
    /// Open one with <see cref="PlaySession.BeginUpdate"/>. Helpers may be called
    /// across many frames/handlers before committing; calls are thread-safe and
    /// chainable. A transaction commits exactly once.
    ///
    /// The typed helpers (<see cref="SetMetric"/>, <see cref="SetSetting"/>,
    /// <see cref="AddAcquisition"/>, route, setup) write entries into the record's
    /// open-union <c>state[]</c> array. For state types without a dedicated helper
    /// (objectives, unlocks, party members, or a game's own lexicons) use the
    /// generic <see cref="UpsertState"/> / <see cref="AppendState"/> /
    /// <see cref="ReplaceState"/> primitives.
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

        /// <summary>
        /// Sets a numeric metric (eg. <c>"score"</c>, <c>"hp"</c>, <c>"gold"</c>) to an
        /// absolute value, written as a <c>state.metric</c> entry keyed by
        /// <paramref name="name"/>. For fixed-point values pass <paramref name="scale"/>:
        /// the real value is <c>value / 10^scale</c> (eg. <c>SetMetric("pct", 8734, 4)</c>
        /// is 0.8734).
        /// </summary>
        public PlayUpdate SetMetric(string name, long value, int scale = 0)
        {
            GuardName(name);
            if (scale < 0) throw new ArgumentOutOfRangeException(nameof(scale), "scale must be non-negative");
            var entry = new JsonObject { ["id"] = name, ["value"] = NumberNode(value) };
            if (scale > 0) entry["scale"] = scale;
            return State(PlaySession.MetricType, "keyed", entry);
        }

        /// <summary>
        /// Combines <paramref name="value"/> with a numeric metric using
        /// <paramref name="operation"/>, resolved against the real value at write time.
        /// Fails at commit/flush if the existing value isn't an integer.
        /// </summary>
        public PlayUpdate UpdateMetric(string name, long value, ProgressOp operation)
        {
            GuardName(name);
            return Record(new JsonObject
            {
                ["op"] = "bumpMetric",
                ["id"] = name,
                ["value"] = value,
                ["operation"] = operation.ToString().ToLowerInvariant(),
            });
        }

        /// <summary>
        /// Sets an arbitrary game-specific configuration value, written as a
        /// <c>state.setting</c> entry keyed by <paramref name="name"/>. The value's
        /// type selects the storage field (string, integer, boolean, or object).
        /// Use <see cref="SetSetup"/> for the universal pre-run choices.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="value"/> is an array — wrap it in a <see cref="JsonObject"/>,
        /// or model it with a dedicated state type.
        /// </exception>
        public PlayUpdate SetSetting(string name, AtValue value)
        {
            GuardName(name);
            var entry = new JsonObject { ["id"] = name };
            var node = value.ToNode();
            switch (node.GetValueKind())
            {
                case JsonValueKind.String: entry["value"] = node; break;
                case JsonValueKind.Number: entry["intValue"] = node; break;
                case JsonValueKind.True:
                case JsonValueKind.False: entry["boolValue"] = node; break;
                case JsonValueKind.Object: entry["dataValue"] = node; break;
                default:
                    throw new ArgumentException(
                        "a setting value must be a string, integer, boolean, or object — " +
                        "wrap an array in a JsonObject, or use a dedicated state type",
                        nameof(value));
            }
            return State(PlaySession.SettingType, "keyed", entry);
        }

        /// <summary>
        /// Sets universal pre-run choices on the <c>state.setup</c> singleton. Only the
        /// non-null arguments are written, so settings that become known later (eg. a
        /// character chosen after the first snapshot) can be filled in by a later call
        /// without clobbering earlier ones.
        /// </summary>
        public PlayUpdate SetSetup(string? mode = null, string? seed = null,
            string? character = null, int? difficulty = null)
        {
            var fields = new JsonObject();
            if (mode != null) fields["mode"] = mode;
            if (seed != null) fields["seed"] = seed;
            if (character != null) fields["character"] = character;
            if (difficulty is int d) fields["difficulty"] = d;
            if (fields.Count == 0) return this;
            return Record(new JsonObject { ["op"] = "setSetup", ["fields"] = fields });
        }

        /// <summary>
        /// Adds a run-wide modifier (eg. an ascension level, daily mutator, or curse)
        /// to the <c>state.setup</c> singleton, deduped by <paramref name="id"/>.
        /// </summary>
        public PlayUpdate AddModifier(string id, string? name = null, string? value = null)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            var modifier = new JsonObject { ["id"] = id };
            if (name != null) modifier["name"] = name;
            if (value != null) modifier["value"] = value;
            return Record(new JsonObject { ["op"] = "addModifier", ["modifier"] = modifier });
        }

        /// <summary>Replaces every <c>state.acquisition</c> entry with the given items (each needs an <c>id</c>).</summary>
        public PlayUpdate SetAcquisitions(IEnumerable<JsonObject> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            var array = new JsonArray();
            foreach (var item in items)
                array.Add(RequireId(item, "acquisition"));
            return Record(new JsonObject { ["op"] = "setAcquisitions", ["items"] = array });
        }

        /// <summary>
        /// Appends an acquired item as a <c>state.acquisition</c> entry (<c>id</c>
        /// required). If the item carries an <c>instanceId</c>, a later add with the
        /// same <c>instanceId</c> updates that entry instead of duplicating it — so a
        /// re-emit after a crash is idempotent.
        /// </summary>
        public PlayUpdate AddAcquisition(JsonObject item) =>
            State(PlaySession.AcquisitionType, "instanced", RequireId(item, "acquisition"));

        /// <summary>
        /// Records arrival at a route stop (appends a <c>state.routeStop</c> entry). An
        /// <paramref name="instanceId"/> makes the arrival idempotent and lets
        /// <see cref="RouteLeave"/> target this exact stop.
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

        /// <summary>Sets the top-level <c>outcome</c> (the end-of-play marker).</summary>
        public PlayUpdate SetOutcome(string type, string? cause = null)
        {
            if (string.IsNullOrEmpty(type)) throw new ArgumentNullException(nameof(type));
            var op = new JsonObject { ["op"] = "setOutcome", ["type"] = type };
            if (!string.IsNullOrEmpty(cause)) op["cause"] = cause;
            return Record(op);
        }

        /// <summary>Sets <c>participants[]</c> to the given other players, replacing any previous list.</summary>
        public PlayUpdate SetParticipants(IEnumerable<JsonObject> participants)
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
            return Record(new JsonObject { ["op"] = "setParticipants", ["participants"] = array });
        }

        /// <summary>
        /// Replaces the singleton <c>state</c> entry of <paramref name="type"/> wholesale
        /// (creating it if absent). Use for state types whose lexicon declares neither
        /// <c>id</c> nor <c>instanceId</c>.
        /// </summary>
        public PlayUpdate ReplaceState(string type, JsonObject entry)
        {
            GuardType(type);
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            return State(type, "singleton", (JsonObject)entry.DeepClone());
        }

        /// <summary>
        /// Upserts a keyed <c>state</c> entry of <paramref name="type"/> by its <c>id</c>
        /// (<c>id</c> required). Use for state types whose lexicon declares <c>id</c> but
        /// not <c>instanceId</c>.
        /// </summary>
        public PlayUpdate UpsertState(string type, JsonObject entry)
        {
            GuardType(type);
            return State(type, "keyed", RequireId(entry, type));
        }

        /// <summary>
        /// Appends an instanced <c>state</c> entry of <paramref name="type"/>, deduped by
        /// <c>instanceId</c> when present (<c>id</c> required). Use for state types whose
        /// lexicon declares both <c>id</c> and <c>instanceId</c>.
        /// </summary>
        public PlayUpdate AppendState(string type, JsonObject entry)
        {
            GuardType(type);
            return State(type, "instanced", RequireId(entry, type));
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

        private PlayUpdate State(string type, string mode, JsonObject entry) =>
            Record(new JsonObject { ["op"] = "state", ["type"] = type, ["mode"] = mode, ["entry"] = entry });

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

        private static JsonNode NumberNode(long value) =>
            value >= int.MinValue && value <= int.MaxValue ? (JsonNode)(int)value : value;

        private static void GuardName(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
        }

        private static void GuardType(string type)
        {
            if (string.IsNullOrEmpty(type)) throw new ArgumentNullException(nameof(type));
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

        private static JsonObject RequireId(JsonObject node, string what)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrEmpty(node["id"]?.GetValue<string>()))
                throw new ArgumentException($"{what} requires a non-empty 'id'", nameof(node));
            return (JsonObject)node.DeepClone();
        }
    }
}
