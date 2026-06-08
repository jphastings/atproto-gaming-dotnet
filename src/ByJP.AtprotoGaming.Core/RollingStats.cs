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

        /// <summary>Whole minutes for a play, floored but never below 1 (a sub-minute play still counts).</summary>
        internal static int MinutesFor(int durationSeconds) => Math.Max(1, durationSeconds / 60);

        /// <summary>
        /// Pure merge of a prior stats <c>value</c> (or null for a fresh record)
        /// with one play's delta. <c>playtime</c> accumulates; <c>lastPlayed</c>
        /// takes the later ISO timestamp; <c>createdAt</c> is preserved.
        /// </summary>
        internal static JsonObject BuildMerged(JsonNode? priorValue, string game, string source,
            int deltaMinutes, string lastPlayed, string nowIso)
        {
            var priorPlaytime = 0;
            var createdAt = nowIso;
            var priorLastPlayed = "";
            if (priorValue is JsonObject value)
            {
                priorPlaytime = value["playtime"]?.GetValue<int>() ?? 0;
                createdAt = value["createdAt"]?.GetValue<string>() ?? nowIso;
                priorLastPlayed = value["lastPlayed"]?.GetValue<string>() ?? "";
            }

            var newest = string.CompareOrdinal(lastPlayed, priorLastPlayed) > 0 ? lastPlayed : priorLastPlayed;

            return new JsonObject
            {
                ["$type"] = StatsCollection,
                ["game"] = new JsonObject { ["uri"] = game },
                ["source"] = source,
                ["playtime"] = priorPlaytime + deltaMinutes,
                ["lastPlayed"] = newest,
                ["createdAt"] = createdAt,
            };
        }

        private static string Uri(string did, string rkey) => $"at://{did}/{StatsCollection}/{rkey}";
    }
}
