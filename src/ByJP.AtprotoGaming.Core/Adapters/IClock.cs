using System;

namespace ByJP.AtprotoGaming.Core.Adapters
{
    /// <summary>
    /// Time source, injected so tests can pin "now". Defaults to
    /// <see cref="SystemClock.Instance"/>.
    /// </summary>
    public interface IClock
    {
        DateTime UtcNow { get; }

        /// <summary>Seconds since the Unix epoch (UTC).</summary>
        long UnixSeconds { get; }
    }
}
