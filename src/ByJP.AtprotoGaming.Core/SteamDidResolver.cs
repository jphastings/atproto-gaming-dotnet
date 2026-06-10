using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// Resolves a SteamID64 to an atproto DID via keytrace's
    /// <c>dev.keytrace.reverseLookup</c>, for backfilling
    /// <c>playingWith[].atproto</c>. The id is a decimal string (matching the
    /// lexicon and the keytrace request). Only positive hits are cached, so a
    /// player who publishes their keytrace claim mid-session is picked up without a
    /// game restart.
    /// </summary>
    public sealed class SteamDidResolver
    {
        private const string Endpoint = "https://keytrace.dev/xrpc/dev.keytrace.reverseLookup";

        private readonly HttpClient _http;
        private readonly ILogSink _log;
        private readonly ConcurrentDictionary<string, string> _cache = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, byte> _inFlight = new ConcurrentDictionary<string, byte>();

        public SteamDidResolver(ILogSink log, HttpClient? http = null)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _http = http ?? new HttpClient();
        }

        /// <summary>
        /// Non-blocking lookup for the in-game hot path: returns the cached DID
        /// if known, otherwise null and kicks off a background fetch so a later
        /// call (or backfill) finds it cached.
        /// </summary>
        public string? LookupDid(string steamId64)
        {
            if (string.IsNullOrEmpty(steamId64)) return null;
            if (_cache.TryGetValue(steamId64, out var did)) return did;
            _ = StartLookupAsync(steamId64);
            return null;
        }

        /// <summary>Awaitable lookup; returns the DID or null on a miss/transient error. Shares the cache with <see cref="LookupDid"/>.</summary>
        public async Task<string?> LookupDidAsync(string steamId64)
        {
            if (string.IsNullOrEmpty(steamId64)) return null;
            if (_cache.TryGetValue(steamId64, out var cached)) return cached;
            var did = await FetchAsync(steamId64).ConfigureAwait(false);
            if (did != null) _cache[steamId64] = did;
            return did;
        }

        private async Task StartLookupAsync(string steamId64)
        {
            if (!_inFlight.TryAdd(steamId64, 0)) return;
            try
            {
                var did = await FetchAsync(steamId64).ConfigureAwait(false);
                if (did != null) _cache[steamId64] = did;
            }
            finally
            {
                _inFlight.TryRemove(steamId64, out _);
            }
        }

        private async Task<string?> FetchAsync(string steamId64)
        {
            try
            {
                var url = $"{Endpoint}?type=steam&subject={Uri.EscapeDataString(steamId64)}";
                var res = await _http.GetFromJsonAsync<ReverseLookupResponse>(url).ConfigureAwait(false);
                if (res?.Matches != null && res.Matches.Count > 0)
                    return res.Matches[0].Did;
            }
            catch (Exception ex)
            {
                _log.Warn($"keytrace lookup failed for {steamId64}: {ex.Message}");
            }
            return null;
        }

        private sealed class ReverseLookupResponse
        {
            [JsonPropertyName("total")] public int Total { get; set; }
            [JsonPropertyName("matches")] public List<Match>? Matches { get; set; }
        }

        private sealed class Match
        {
            [JsonPropertyName("did")] public string? Did { get; set; }
        }
    }
}
