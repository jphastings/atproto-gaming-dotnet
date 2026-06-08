using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// A stateful, single-record editor that applies incremental changes with
    /// optimistic locking. Each <see cref="ApplyAsync"/> reads the current record
    /// (or its cached copy), applies your delta, and writes it back with a
    /// compare-and-swap on the record's CID — refetching and re-applying the delta
    /// if the record changed elsewhere, and queueing to the outbox when offline.
    /// </summary>
    /// <remarks>
    /// Obtain one via <see cref="RecordPublisher.Edit"/>. Hold it for the lifetime
    /// of the thing you're editing (e.g. a play-through) so the happy path is a
    /// single PUT with no extra read. Calls are serialised per record.
    /// </remarks>
    public sealed class RecordEditor
    {
        private readonly RecordPublisher _publisher;
        private readonly Func<JsonObject>? _seed;
        private readonly SemaphoreSlim _gate;

        private JsonObject? _value; // last-known record value (domain shape, unsigned)
        private string? _cid;       // its CID on the PDS, for the next swap

        public string Collection { get; }
        public string Rkey { get; }

        internal RecordEditor(RecordPublisher publisher, string collection, string rkey, Func<JsonObject>? seed)
        {
            _publisher = publisher;
            Collection = collection;
            Rkey = rkey;
            _seed = seed;
            _gate = publisher.GateFor(collection, rkey);
        }

        /// <summary>A clone of the last-known record value, or null if nothing has been read/written yet.</summary>
        public JsonObject? Current => _value == null ? null : (JsonObject)_value.DeepClone();

        /// <summary>
        /// Applies <paramref name="mutate"/> to the current record and writes it
        /// back. <paramref name="mutate"/> receives the record's value to change in
        /// place; it may be invoked more than once if a conflict forces a refetch,
        /// so keep it a pure delta (idempotent against the latest state).
        /// </summary>
        public async Task<PutResult> ApplyAsync(Action<JsonObject> mutate)
        {
            if (mutate == null) throw new ArgumentNullException(nameof(mutate));

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var outcome = await _publisher
                    .ApplyCoreAsync(Collection, Rkey, _value, _cid, _seed, mutate)
                    .ConfigureAwait(false);

                // Keep whatever the core last had on the PDS (or queued locally), so
                // the next delta builds on it. On a hard drop the cache is unchanged.
                if (outcome.Value != null)
                {
                    _value = outcome.Value;
                    _cid = outcome.Cid;
                }
                return outcome.Result;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
