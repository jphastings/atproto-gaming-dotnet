using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// Maintains the player's rolling <c>games.gamesgamesgamesgames.actor.stats</c>
    /// record alongside finished plays: creates it on first call (caching the rkey
    /// in config), otherwise reads it, adds the new play's minutes to
    /// <c>playtime</c>, bumps <c>lastPlayed</c> if newer, and PUTs.
    /// </summary>
    public sealed class RollingStats
    {
        // The stats lexicon lives in the public games.gamesgamesgamesgames
        // namespace; pin its shape here. Fields: game, source, playtime (minutes),
        // lastPlayed, createdAt.
        public const string StatsCollection = "games.gamesgamesgamesgames.actor.stats";

        private readonly AtprotoClient _client;
        private readonly IConfigStore _config;
        private readonly ILogSink _log;
        private readonly IClock _clock;

        public RollingStats(AtprotoClient client, IConfigStore config, ILogSink log, IClock clock)
        {
            _client = client;
            _config = config;
            _log = log;
            _clock = clock;
        }

        /// <summary>
        /// Returns the stats record's AT-URI, creating an empty record (playtime 0)
        /// if none exists yet — without rolling in any play. Use this at play-through
        /// start to fill the play record's required <c>stats</c> field; the finished
        /// play's minutes are added later by <see cref="EnsureAndUpdateAsync"/>.
        /// </summary>
        public async Task<string> EnsureAsync(JsonNode gameRef, string source)
        {
            if (gameRef == null) throw new ArgumentNullException(nameof(gameRef));
            var cfg = _config.Core;

            if (!string.IsNullOrEmpty(cfg.StatsRkey))
            {
                var existing = await _client.GetRecordAsync(StatsCollection, cfg.StatsRkey).ConfigureAwait(false);
                if (existing != null)
                    return $"at://{_client.Did}/{StatsCollection}/{cfg.StatsRkey}";
                _log.Warn($"cached statsRkey {cfg.StatsRkey} missing on PDS — creating a new one");
            }

            var nowIso = NowIso();
            var seed = BuildMerged(null, gameRef, source, deltaMinutes: 0, lastPlayed: nowIso, nowIso: nowIso);
            return await CreateAndCacheAsync(seed).ConfigureAwait(false);
        }

        /// <summary>
        /// Ensures the stats record exists and rolls in one finished play. Returns
        /// the stats record's AT-URI so the consumer can set <c>stats</c> on the
        /// play record. Tolerates a cached rkey whose record no longer exists on
        /// the PDS (creates a fresh one).
        /// </summary>
        public async Task<string> EnsureAndUpdateAsync(JsonNode gameRef, string source, int durationSeconds, string endedAtIso)
        {
            if (gameRef == null) throw new ArgumentNullException(nameof(gameRef));
            var cfg = _config.Core;
            var deltaMinutes = MinutesFor(durationSeconds);
            var nowIso = NowIso();
            var lastPlayed = string.IsNullOrEmpty(endedAtIso) ? nowIso : endedAtIso;

            if (!string.IsNullOrEmpty(cfg.StatsRkey))
            {
                var existing = await _client.GetRecordAsync(StatsCollection, cfg.StatsRkey).ConfigureAwait(false);
                if (existing != null)
                {
                    var merged = BuildMerged(existing["value"], gameRef, source, deltaMinutes, lastPlayed, nowIso);
                    await _client.PutRecordAsync(StatsCollection, cfg.StatsRkey, merged).ConfigureAwait(false);
                    return $"at://{_client.Did}/{StatsCollection}/{cfg.StatsRkey}";
                }
                _log.Warn($"cached statsRkey {cfg.StatsRkey} missing on PDS — creating a new one");
            }

            var created = BuildMerged(null, gameRef, source, deltaMinutes, lastPlayed, nowIso);
            return await CreateAndCacheAsync(created).ConfigureAwait(false);
        }

        private async Task<string> CreateAndCacheAsync(JsonObject record)
        {
            var uri = await _client.CreateRecordAsync(StatsCollection, record).ConfigureAwait(false);
            _config.Core.StatsRkey = uri.Substring(uri.LastIndexOf('/') + 1);
            _config.Save();
            return uri;
        }

        /// <summary>Whole minutes for a play, floored but never below 1 (a sub-minute play still counts).</summary>
        internal static int MinutesFor(int durationSeconds) => Math.Max(1, durationSeconds / 60);

        /// <summary>
        /// Pure merge of a prior stats <c>value</c> (or null for a fresh record)
        /// with one play's delta. <c>playtime</c> accumulates; <c>lastPlayed</c>
        /// takes the later ISO timestamp; <c>createdAt</c> is preserved.
        /// </summary>
        internal static JsonObject BuildMerged(JsonNode? priorValue, JsonNode gameRef, string source,
            int deltaMinutes, string lastPlayed, string nowIso)
        {
            int priorPlaytime = 0;
            string createdAt = nowIso;
            string priorLastPlayed = "";
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
                ["game"] = gameRef.DeepClone(),
                ["source"] = source,
                ["playtime"] = priorPlaytime + deltaMinutes,
                ["lastPlayed"] = newest,
                ["createdAt"] = createdAt,
            };
        }

        private string NowIso() => _clock.UtcNow.ToUniversalTime().ToString("o");
    }
}
