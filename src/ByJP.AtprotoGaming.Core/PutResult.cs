namespace ByJP.AtprotoGaming.Core
{
    /// <summary>What happened to a <see cref="RecordPublisher.PutAsync"/> call.</summary>
    public enum PutStatus
    {
        /// <summary>Delivered to the PDS.</summary>
        Published,

        /// <summary>Written to the on-disk outbox for a later flush.</summary>
        Queued,

        /// <summary>Permanently rejected, or unqueueable (no known DID) — discarded.</summary>
        Dropped,
    }

    public readonly struct PutResult
    {
        public PutStatus Status { get; }

        /// <summary>The AT-URI when <see cref="Status"/> is <see cref="PutStatus.Published"/>; otherwise null.</summary>
        public string? Uri { get; }

        private PutResult(PutStatus status, string? uri)
        {
            Status = status;
            Uri = uri;
        }

        public static PutResult Published(string uri) => new PutResult(PutStatus.Published, uri);
        public static PutResult Queued() => new PutResult(PutStatus.Queued, null);
        public static PutResult Dropped() => new PutResult(PutStatus.Dropped, null);
    }
}
