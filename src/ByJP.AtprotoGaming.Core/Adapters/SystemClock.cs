using System;

namespace ByJP.AtprotoGaming.Core.Adapters
{
    /// <summary>Wall-clock <see cref="IClock"/> backed by <see cref="DateTime.UtcNow"/>.</summary>
    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public DateTime UtcNow => DateTime.UtcNow;

        public long UnixSeconds => (long)(DateTime.UtcNow - Epoch).TotalSeconds;
    }
}
