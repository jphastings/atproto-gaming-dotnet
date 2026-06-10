using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// Maintains the player's rolling <c>games.gamesgamesgamesgames.actor.stats</c>
    /// record for a game + platform: finds it via <see cref="StatsResolver"/> (the
    /// same record the play links to), adds a finished play's minutes to
    /// <c>playtime</c>, bumps <c>lastPlayed</c> if newer, and writes it back.
    /// </summary>
    public sealed class RollingStats
    {
        public const string StatsCollection = "games.gamesgamesgamesgames.actor.stats";

        private readonly AtprotoClient _client;
        private readonly StatsResolver _resolver;
        private readonly IClock _clock;

        internal RollingStats(AtprotoClient client, StatsResolver resolver, IClock clock)
        {
            _client = client;
            _resolver = resolver;
            _clock = clock;
        }

        /// <summary>
        /// Returns the stats record's AT-URI for <paramref name="game"/> +
        /// <paramref name="source"/>, finding or creating it. Use it to fill a play
        /// record's <c>stats</c> field up front (the play layer does this for you).
        /// </summary>
        public async Task<string> EnsureAsync(string game, string source)
        {
            var did = _client.Did;
            var rkey = await _resolver.EnsureRkeyAsync(did, game, source).ConfigureAwait(false);
            return Uri(did, rkey);
        }

        /// <summary>
        /// Rolls one finished play into the stats record: adds its minutes to
        /// <c>playtime</c> and bumps <c>lastPlayed</c> if newer. Returns the record's AT-URI.
        /// </summary>
        public async Task<string> EnsureAndUpdateAsync(string game, string source, int durationSeconds, string endedAtIso)
        {
            var did = _client.Did;
            var rkey = await _resolver.EnsureRkeyAsync(did, game, source).ConfigureAwait(false);
            var nowIso = _clock.UtcNow.ToUniversalTime().ToString("o");
            var lastPlayed = string.IsNullOrEmpty(endedAtIso) ? nowIso : endedAtIso;

            var existing = await _client.GetRecordAsync(StatsCollection, rkey).ConfigureAwait(false);
            var merged = BuildMerged(existing?["value"], game, source, MinutesFor(durationSeconds), lastPlayed, nowIso);
            await _client.PutRecordAsync(StatsCollection, rkey, merged).ConfigureAwait(false);
            return Uri(did, rkey);
        }

        /// <summary>
        /// Sets the player's achievement progress (<c>achievements.unlocked</c> /
        /// <c>achievements.total</c>) for this game + platform, finding/creating the
        /// stats record and preserving its other fields. A no-op (no write) when the
        /// counts already match — so a game re-asserting unlocks on profile load
        /// doesn't spam the PDS. Returns the record's AT-URI.
        /// </summary>
        public async Task<string> AchievementsUnlockedAsync(string game, string source, int unlocked, int total)
        {
            if (unlocked < 0) throw new ArgumentOutOfRangeException(nameof(unlocked));
            if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));

            var did = _client.Did;
            var rkey = await _resolver.EnsureRkeyAsync(did, game, source).ConfigureAwait(false);
            var existing = await _client.GetRecordAsync(StatsCollection, rkey).ConfigureAwait(false);

            if (AchievementsUnchanged(existing?["value"], unlocked, total))
                return Uri(did, rkey);

            var nowIso = _clock.UtcNow.ToUniversalTime().ToString("o");
            var merged = BuildWithAchievements(existing?["value"], game, source, unlocked, total, nowIso);
            await _client.PutRecordAsync(StatsCollection, rkey, merged).ConfigureAwait(false);
            return Uri(did, rkey);
        }

        /// <summary>Whole minutes for a play, floored but never below 1 (a sub-minute play still counts).</summary>
        internal static int MinutesFor(int durationSeconds) => Math.Max(1, durationSeconds / 60);

        /// <summary>
        /// Pure merge of a prior stats <c>value</c> (or null for a fresh record)
        /// with one play's delta. <c>playtime</c> accumulates; <c>lastPlayed</c>
        /// takes the later ISO timestamp; <c>createdAt</c> and <c>achievements</c>
        /// are preserved.
        /// </summary>
        internal static JsonObject BuildMerged(JsonNode? priorValue, string game, string source,
            int deltaMinutes, string lastPlayed, string nowIso)
        {
            var priorPlaytime = (priorValue as JsonObject)?["playtime"]?.GetValue<int>() ?? 0;
            var priorLastPlayed = (priorValue as JsonObject)?["lastPlayed"]?.GetValue<string>() ?? "";

            var record = BuildBase(priorValue, game, source, nowIso);
            record["playtime"] = priorPlaytime + deltaMinutes;
            record["lastPlayed"] = string.CompareOrdinal(lastPlayed, priorLastPlayed) > 0 ? lastPlayed : priorLastPlayed;
            return record;
        }

        /// <summary>Pure: prior stats with achievement counts set, preserving playtime/lastPlayed/createdAt.</summary>
        internal static JsonObject BuildWithAchievements(JsonNode? priorValue, string game, string source,
            int unlocked, int total, string nowIso)
        {
            var record = BuildBase(priorValue, game, source, nowIso);
            record["achievements"] = new JsonObject { ["unlocked"] = unlocked, ["total"] = total };
            return record;
        }

        internal static bool AchievementsUnchanged(JsonNode? priorValue, int unlocked, int total)
        {
            if (priorValue is JsonObject value && value["achievements"] is JsonObject a)
                return (a["unlocked"]?.GetValue<int>() ?? -1) == unlocked
                    && (a["total"]?.GetValue<int>() ?? -1) == total;
            return false;
        }

        // The required identity fields plus whatever cross-cutting fields the prior
        // record carried (playtime, lastPlayed, achievements). Each writer overwrites
        // only the field it owns, so playtime and achievement updates don't clobber
        // each other. (signatures are intentionally dropped — a rebuilt record
        // invalidates any external attestation.)
        private static JsonObject BuildBase(JsonNode? priorValue, string game, string source, string nowIso)
        {
            var record = new JsonObject
            {
                ["$type"] = StatsCollection,
                ["game"] = new JsonObject { ["uri"] = game },
                ["source"] = source,
                ["createdAt"] = nowIso,
            };
            if (priorValue is JsonObject prior)
            {
                if (prior["createdAt"]?.GetValue<string>() is string createdAt) record["createdAt"] = createdAt;
                if (prior["playtime"] is JsonNode playtime) record["playtime"] = playtime.GetValue<int>();
                if (prior["lastPlayed"]?.GetValue<string>() is string lastPlayed) record["lastPlayed"] = lastPlayed;
                if (prior["achievements"] is JsonObject achievements) record["achievements"] = achievements.DeepClone();
            }
            return record;
        }

        private static string Uri(string did, string rkey) => $"at://{did}/{StatsCollection}/{rkey}";
    }
}
