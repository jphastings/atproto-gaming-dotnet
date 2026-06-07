using System;

namespace ByJP.AtprotoGaming.Core.Adapters
{
    /// <summary>Discards everything. Useful in tests or when logging is wired elsewhere.</summary>
    public sealed class NullLogSink : ILogSink
    {
        public static readonly NullLogSink Instance = new NullLogSink();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    /// <summary>Writes to <see cref="Console"/> with a level prefix — a reasonable default for plain .NET hosts.</summary>
    public sealed class ConsoleLogSink : ILogSink
    {
        private readonly string _prefix;

        public ConsoleLogSink(string prefix = "atproto-gaming")
        {
            _prefix = prefix;
        }

        public void Info(string message) => Console.WriteLine($"[{_prefix}] {message}");
        public void Warn(string message) => Console.WriteLine($"[{_prefix}] WARN: {message}");

        public void Error(string message, Exception? exception = null) =>
            Console.Error.WriteLine($"[{_prefix}] ERROR: {message}{(exception is null ? "" : "\n" + exception)}");
    }
}
