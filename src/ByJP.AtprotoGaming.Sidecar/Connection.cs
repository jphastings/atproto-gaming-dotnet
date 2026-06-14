using ByJP.AtprotoGaming.Core;

namespace ByJP.AtprotoGaming.Sidecar;

/// <summary>
/// Per-connection protocol state: whether the handshake completed, the active play,
/// and its in-flight (buffered, uncommitted) update. One per accepted socket.
/// </summary>
internal sealed class Connection
{
    /// <summary>True once a valid <c>hello</c> has been accepted.</summary>
    public bool Ready { get; set; }

    /// <summary>The client's self-declared id (from <c>hello</c>), used for publish approval.</summary>
    public string? ClientId { get; set; }

    /// <summary>The client's human-friendly name (from <c>hello</c>), for the approval prompt.</summary>
    public string? ClientName { get; set; }

    /// <summary>The currently-open play, or null before the first <c>open</c>.</summary>
    public PlaySession? Session { get; set; }

    /// <summary>
    /// The buffer accumulating mutations since the last <b>publish</b>. Null when
    /// nothing is buffered; created lazily on the first mutation. A throttled
    /// (deferred) commit keeps accumulating into it rather than consuming it.
    /// </summary>
    public PlayUpdate? Update { get; set; }

    /// <summary>When this play was opened (the first write is held back briefly from here to batch opening setup).</summary>
    public System.DateTime? OpenedUtc { get; set; }

    /// <summary>When this play last published (for the per-play write throttle); null = never this session.</summary>
    public System.DateTime? LastPublishUtc { get; set; }

    /// <summary>True when the buffer holds an outcome/finish, forcing the next commit to publish immediately.</summary>
    public bool PendingTerminal { get; set; }
}
