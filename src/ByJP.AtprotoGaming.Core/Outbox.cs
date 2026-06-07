using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core.Adapters;
using ByJP.AtprotoGaming.Core.Internal;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// On-disk, per-DID queue of record PUTs that couldn't be delivered. Each file
    /// holds the already-prepared (and, if applicable, signed) JSON payload, ready
    /// to PUT byte-for-byte. Layout:
    /// <c>&lt;root&gt;/&lt;encoded-did&gt;/&lt;collection&gt;/&lt;rkey&gt;.json</c> — so a
    /// logged-out queue survives until the matching DID signs in again, and
    /// account switching never cross-flushes queues.
    /// </summary>
    public sealed class Outbox
    {
        private readonly IFileSystem _fs;
        private readonly ILogSink _log;
        private readonly AuthState _auth;
        private readonly AtprotoClient _client;

        private readonly object _flushLock = new object();
        private bool _flushing;

        public Outbox(IFileSystem fs, ILogSink log, AuthState auth, AtprotoClient client)
        {
            _fs = fs ?? throw new ArgumentNullException(nameof(fs));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        private string DidDir(string did) => Path.Combine(_fs.OutboxRoot, DidPath.EncodeDid(did));
        private string CollectionDir(string did, string collection) => Path.Combine(DidDir(did), collection);
        private string FilePath(string did, string collection, string rkey) =>
            Path.Combine(CollectionDir(did, collection), rkey + ".json");

        /// <summary>Atomically queues the prepared payload for (did, collection, rkey).</summary>
        public void Enqueue(string did, string collection, string rkey, JsonNode payload)
        {
            AtomicFile.WriteAllText(FilePath(did, collection, rkey), payload.ToJsonString());
            _log.Warn($"queued {collection}/{rkey} for {did} (offline/transient failure)");
        }

        /// <summary>Best-effort removal after a successful online publish so a stale snapshot isn't re-PUT later.</summary>
        public void Remove(string did, string collection, string rkey)
        {
            try
            {
                var path = FilePath(did, collection, rkey);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _log.Warn($"outbox: couldn't remove {collection}/{rkey}: {ex.Message}");
            }
        }

        /// <summary>
        /// Drains the queue for the currently-authenticated DID. Single-flight per
        /// instance. On permanent rejection a file is dropped with an error; on the
        /// first transient failure the flush stops (don't hammer the PDS) leaving
        /// the rest for next time.
        /// </summary>
        /// <param name="skipPredicate">
        /// Optional <c>(collection, rkey) → skip</c> so the caller can hold back the
        /// currently-active record the live publisher is also racing to write.
        /// </param>
        public async Task FlushAsync(Func<string, string, bool>? skipPredicate = null)
        {
            var did = _auth.Did;
            if (did == null || _auth.Status != AuthStatus.Ok) return;

            lock (_flushLock)
            {
                if (_flushing) return;
                _flushing = true;
            }

            try
            {
                var didDir = DidDir(did);
                if (!Directory.Exists(didDir)) return;

                foreach (var collectionDir in Directory.GetDirectories(didDir))
                {
                    var collection = Path.GetFileName(collectionDir);
                    foreach (var path in Directory.GetFiles(collectionDir, "*.json"))
                    {
                        var rkey = Path.GetFileNameWithoutExtension(path);
                        if (skipPredicate != null && skipPredicate(collection, rkey)) continue;

                        JsonNode? payload;
                        try
                        {
                            payload = JsonNode.Parse(File.ReadAllText(path));
                        }
                        catch (Exception ex)
                        {
                            _log.Warn($"outbox: corrupt file {path} ({ex.Message}) — discarding");
                            TryDelete(path);
                            continue;
                        }
                        if (payload == null) { TryDelete(path); continue; }

                        try
                        {
                            await _client.PutRecordAsync(collection, rkey, payload).ConfigureAwait(false);
                        }
                        catch (AtprotoPermanentException ex)
                        {
                            _log.Error($"outbox: PDS rejected queued {collection}/{rkey} — discarding", ex);
                            TryDelete(path);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            // Transient — leave on disk and stop the whole flush.
                            _log.Warn($"outbox: transient failure flushing {collection}/{rkey} ({ex.Message}); will retry");
                            return;
                        }

                        TryDelete(path);
                    }
                }
            }
            finally
            {
                lock (_flushLock) { _flushing = false; }
            }
        }

        private void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) { _log.Warn($"outbox: couldn't delete {path}: {ex.Message}"); }
        }
    }
}
