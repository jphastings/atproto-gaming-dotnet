using System;

namespace ByJP.AtprotoGaming.Core.Adapters
{
    /// <summary>
    /// The package's only output channel. Wire it to your runtime's logger —
    /// BepInEx <c>ManualLogSource</c>, Godot <c>GD.Print</c>, or
    /// <see cref="ConsoleLogSink"/> for plain .NET.
    /// </summary>
    /// <remarks>
    /// Severity contract (requirements NF6): <see cref="Info"/> is for one-time
    /// lifecycle events only — the package never logs per-publish on the happy
    /// path. <see cref="Warn"/> is for transient failures it handled itself
    /// (queued a record, refreshed a token). <see cref="Error"/> is for things
    /// the player can fix (auth failed, config malformed, PDS rejected a payload).
    /// </remarks>
    public interface ILogSink
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message, Exception? exception = null);
    }
}
