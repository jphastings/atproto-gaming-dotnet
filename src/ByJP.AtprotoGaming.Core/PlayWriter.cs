using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core.Adapters;
using ByJP.AtprotoGaming.Core.Internal;
using ByJP.AtprotoGaming.Core.Signing;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// Writes play records: a commit persists its ops to the <see cref="PlayQueue"/>
    /// (durable) and then tries to deliver everything queued for that record —
    /// fetch the current record (or seed), apply the ops, resolve+insert
    /// <c>stats</c>, stamp <c>updatedAt</c>, and PUT with optimistic locking. Ops
    /// re-apply against the freshly-fetched record, so an offline increment resolves
    /// against the real value. Delivery happens at commit and at flush (login).
    /// </summary>
    internal sealed class PlayWriter
    {
        private const int MaxConflictRetries = 4;

        private readonly AtprotoClient _client;
        private readonly AuthState _auth;
        private readonly IClock _clock;
        private readonly ILogSink _log;
        private readonly StatsResolver _stats;
        private readonly PlayQueue _queue;
        private readonly VersionsInjector _versions;
        private readonly RecordSigner? _signer;

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
            new ConcurrentDictionary<string, SemaphoreSlim>();

        public PlayWriter(AtprotoClient client, AuthState auth, IClock clock, ILogSink log,
            StatsResolver stats, PlayQueue queue, VersionsInjector versions, RecordSigner? signer)
        {
            _client = client;
            _auth = auth;
            _clock = clock;
            _log = log;
            _stats = stats;
            _queue = queue;
            _versions = versions;
            _signer = signer;
        }

        /// <summary>Records ops durably and tries to deliver them. Returns the outcome + refreshed cache.</summary>
        public async Task<EditOutcome> CommitAsync(string rkey, JsonObject seed, string source,
            JsonArray ops, JsonObject? cachedValue, string? cachedCid)
        {
            var did = _auth.Did;
            if (string.IsNullOrEmpty(did))
            {
                _log.Warn($"dropping play update {rkey}: no DID known this session");
                return new EditOutcome(PutResult.Dropped(), cachedValue, cachedCid);
            }

            var gate = GateFor(rkey);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _queue.Append(did!, rkey, seed, source, ops);
                return await DeliverAsync(did!, rkey, cachedValue, cachedCid).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Builds a forked <see cref="PlaySession"/>: a new record whose seed is a
        /// clone of <paramref name="parentValue"/> with <c>forkedFrom</c> pointing at
        /// the parent and the terminal markers stripped.
        /// </summary>
        public PlaySession Fork(string parentRkey, JsonObject parentValue, string? parentCid, string? newId, string source)
        {
            var did = _auth.Did;
            if (string.IsNullOrEmpty(did))
                throw new InvalidOperationException("cannot fork a play before the player's identity is known");

            var parentUri = $"at://{did}/{PlaySession.Collection}/{parentRkey}";
            var cid = parentCid ?? StrongRef.ComputeCid(parentValue);
            var forkedFrom = StrongRef.Create(parentUri, cid);

            var newRkey = string.IsNullOrEmpty(newId)
                ? RecordKey.Sanitize(Tid.FromPlayThrough(_clock.UnixSeconds, parentRkey))
                : RecordKey.Sanitize(newId!);

            JsonObject Seed()
            {
                var clone = (JsonObject)parentValue.DeepClone();
                clone.Remove("signatures");
                clone.Remove("endedAt");
                clone.Remove("duration");
                if (clone["progress"] is JsonObject progress) progress.Remove("outcome");
                clone["forkedFrom"] = forkedFrom.DeepClone();
                return clone;
            }

            return new PlaySession(this, newRkey, Seed, source, _clock);
        }

        /// <summary>Delivers every queued play for the current DID (call at login / after a commit).</summary>
        public async Task FlushAsync()
        {
            var did = _auth.Did;
            if (did == null || _auth.Status != AuthStatus.Ok) return;

            foreach (var entry in _queue.List(did).ToList())
            {
                var gate = GateFor(entry.Rkey);
                await gate.WaitAsync().ConfigureAwait(false);
                try { await DeliverAsync(did, entry.Rkey, null, null).ConfigureAwait(false); }
                finally { gate.Release(); }
            }
        }

        // Caller holds the per-rkey gate. Applies all queued ops for (did, rkey)
        // against the current record and writes once, with CAS retries.
        private async Task<EditOutcome> DeliverAsync(string did, string rkey, JsonObject? cachedValue, string? cachedCid)
        {
            if (_auth.Status != AuthStatus.Ok)
                return new EditOutcome(PutResult.Queued(), cachedValue, cachedCid);

            var pending = _queue.Read(did, rkey);
            if (pending == null)
                return new EditOutcome(PutResult.Published(""), cachedValue, cachedCid);

            JsonObject? baseValue = cachedValue == null ? null : (JsonObject)cachedValue.DeepClone();
            var cid = cachedCid;

            for (var attempt = 0; ; attempt++)
            {
                if (baseValue == null)
                {
                    JsonNode? got;
                    try { got = await _client.GetRecordAsync(PlaySession.Collection, rkey).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        _log.Warn($"play {rkey}: couldn't read current record ({ex.Message}); staying queued");
                        return new EditOutcome(PutResult.Queued(), cachedValue, cachedCid);
                    }
                    if (got?["value"] is JsonObject existing) { baseValue = (JsonObject)existing.DeepClone(); cid = got["cid"]?.GetValue<string>(); }
                    else { baseValue = (JsonObject)pending.Seed.DeepClone(); cid = null; }
                }
                baseValue.Remove("signatures");

                JsonObject working;
                try
                {
                    working = (JsonObject)baseValue.DeepClone();
                    PlayOps.Apply(working, pending.Ops);
                }
                catch (Exception ex)
                {
                    _queue.Remove(did, rkey);
                    _log.Error($"play {rkey}: cannot apply queued changes — dropping", ex);
                    return new EditOutcome(PutResult.Dropped(), cachedValue, cachedCid);
                }

                if (!(working["stats"] is JsonNode))
                {
                    var statsUri = _stats.CachedUri(did);
                    var gameUri = working["game"]?.GetValue<string>();
                    if (statsUri == null && gameUri != null)
                    {
                        try { statsUri = await _stats.ResolveUriAsync(did, gameUri, pending.Source).ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            _log.Warn($"play {rkey}: couldn't resolve stats ({ex.Message}); staying queued");
                            return new EditOutcome(PutResult.Queued(), cachedValue, cachedCid);
                        }
                    }
                    if (statsUri != null) working["stats"] = statsUri;
                }

                working["updatedAt"] = _clock.UtcNow.ToUniversalTime().ToString("o");
                var prepared = RecordPrep.Prepare(working, did, _versions, _signer);

                try
                {
                    var written = await _client.PutRecordAsync(PlaySession.Collection, rkey, prepared, swapRecord: cid)
                        .ConfigureAwait(false);
                    _queue.Remove(did, rkey);
                    return new EditOutcome(PutResult.Published(written.Uri), working, written.Cid);
                }
                catch (AtprotoPermanentException ex) when (ex.ErrorName == "InvalidSwap" && attempt < MaxConflictRetries)
                {
                    _log.Warn($"play {rkey}: optimistic-lock conflict; refetching (attempt {attempt + 1})");
                    baseValue = null; // force a fresh GET and re-apply the ops
                    continue;
                }
                catch (AtprotoPermanentException ex) when (ex.ErrorName == "InvalidSwap")
                {
                    _log.Warn($"play {rkey}: conflict unresolved after {MaxConflictRetries} retries — staying queued");
                    return new EditOutcome(PutResult.Queued(), cachedValue, cachedCid);
                }
                catch (AtprotoPermanentException ex)
                {
                    _queue.Remove(did, rkey);
                    _log.Error($"PDS rejected play {rkey} — dropping", ex);
                    return new EditOutcome(PutResult.Dropped(), cachedValue, cachedCid);
                }
                catch (Exception ex)
                {
                    _log.Warn($"play {rkey}: write failed ({ex.Message}) — staying queued");
                    return new EditOutcome(PutResult.Queued(), cachedValue, cachedCid);
                }
            }
        }

        private SemaphoreSlim GateFor(string rkey) =>
            _gates.GetOrAdd(rkey, _ => new SemaphoreSlim(1, 1));
    }
}
