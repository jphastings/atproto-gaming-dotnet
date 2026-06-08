using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// Finds (or creates) the player's <c>games.gamesgamesgamesgames.actor.stats</c>
    /// record for a given game + platform, since there's no direct way to address
    /// it: list all stats records, filter by <c>game.uri</c> and <c>source</c>, and
    /// on a tie drop signed ones and take the most-recent <c>lastPlayed</c>. The
    /// resolved rkey is cached in config so the URI is afterwards knowable offline.
    /// </summary>
    internal sealed class StatsResolver
    {
        public const string StatsCollection = "games.gamesgamesgamesgames.actor.stats";

        private readonly AtprotoClient _client;
        private readonly IConfigStore _config;
        private readonly IClock _clock;

        public StatsResolver(AtprotoClient client, IConfigStore config, IClock clock)
        {
            _client = client;
            _config = config;
            _clock = clock;
        }

        /// <summary>The stats AT-URI from the cached rkey, or null if not resolved yet (no network).</summary>
        public string? CachedUri(string did)
        {
            var rkey = _config.Core.StatsRkey;
            return string.IsNullOrEmpty(rkey) ? null : Uri(did, rkey);
        }

        /// <summary>The stats record's rkey, finding or creating it on the PDS and caching it.</summary>
        public async Task<string> EnsureRkeyAsync(string did, string gameUri, string source)
        {
            var cfg = _config.Core;
            if (!string.IsNullOrEmpty(cfg.StatsRkey)) return cfg.StatsRkey;

            var rkey = await FindRkeyAsync(gameUri, source).ConfigureAwait(false)
                       ?? await CreateAsync(gameUri, source).ConfigureAwait(false);
            cfg.StatsRkey = rkey;
            _config.Save();
            return rkey;
        }

        /// <summary>The stats AT-URI, resolving (and caching) the rkey if needed. Online only.</summary>
        public async Task<string> ResolveUriAsync(string did, string gameUri, string source) =>
            Uri(did, await EnsureRkeyAsync(did, gameUri, source).ConfigureAwait(false));

        private async Task<string?> FindRkeyAsync(string gameUri, string source)
        {
            var matches = new List<(string rkey, JsonObject value)>();
            string? cursor = null;
            do
            {
                var page = await _client.ListRecordsAsync(StatsCollection, cursor, 100).ConfigureAwait(false);
                if (page?["records"] is JsonArray records)
                {
                    foreach (var node in records)
                    {
                        if (!(node is JsonObject record) || !(record["value"] is JsonObject value)) continue;
                        var g = value["game"]?["uri"]?.GetValue<string>();
                        var s = value["source"]?.GetValue<string>();
                        if (g == gameUri && s == source)
                            matches.Add((RkeyOf(record["uri"]!.GetValue<string>()), value));
                    }
                }
                cursor = page?["cursor"]?.GetValue<string>();
            } while (!string.IsNullOrEmpty(cursor));

            if (matches.Count == 0) return null;
            if (matches.Count == 1) return matches[0].rkey;

            // More than one: prefer unsigned, then most-recent lastPlayed.
            var pool = matches.Where(m => !HasSignatures(m.value)).ToList();
            if (pool.Count == 0) pool = matches;

            var best = pool[0];
            foreach (var m in pool)
                if (string.CompareOrdinal(LastPlayed(m.value), LastPlayed(best.value)) > 0)
                    best = m;
            return best.rkey;
        }

        private async Task<string> CreateAsync(string gameUri, string source)
        {
            var record = new JsonObject
            {
                ["$type"] = StatsCollection,
                ["game"] = new JsonObject { ["uri"] = gameUri },
                ["source"] = source,
                ["createdAt"] = _clock.UtcNow.ToUniversalTime().ToString("o"),
            };
            var created = await _client.CreateRecordAsync(StatsCollection, record).ConfigureAwait(false);
            return RkeyOf(created.Uri);
        }

        private static bool HasSignatures(JsonObject value) =>
            value["signatures"] is JsonArray a && a.Count > 0;

        private static string LastPlayed(JsonObject value) =>
            value["lastPlayed"]?.GetValue<string>() ?? "";

        private static string Uri(string did, string rkey) => $"at://{did}/{StatsCollection}/{rkey}";
        private static string RkeyOf(string uri) => uri.Substring(uri.LastIndexOf('/') + 1);
    }
}
