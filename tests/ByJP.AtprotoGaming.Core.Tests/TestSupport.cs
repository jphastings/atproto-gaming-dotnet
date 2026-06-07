using System;
using System.Collections.Generic;
using System.IO;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Core.Tests;

internal sealed class FixedClock : IClock
{
    public DateTime UtcNow { get; set; } = new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);
    public long UnixSeconds => (long)(UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
}

internal sealed class CapturingLogSink : ILogSink
{
    public readonly List<string> Infos = new();
    public readonly List<string> Warns = new();
    public readonly List<string> Errors = new();

    public void Info(string message) => Infos.Add(message);
    public void Warn(string message) => Warns.Add(message);
    public void Error(string message, Exception? exception = null) => Errors.Add(message);
}

/// <summary>A throwaway temp directory exposed as an <see cref="IFileSystem"/>.</summary>
internal sealed class TempFileSystem : IFileSystem, IDisposable
{
    private readonly string _root;

    public TempFileSystem()
    {
        _root = Path.Combine(Path.GetTempPath(), "agc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public string ConfigDirectory => _root;
    public string OutboxRoot => Path.Combine(_root, "outbox");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}

internal static class Hex
{
    public static string Of(byte[] bytes)
    {
        var c = new char[bytes.Length * 2];
        const string h = "0123456789abcdef";
        for (int i = 0; i < bytes.Length; i++)
        {
            c[i * 2] = h[bytes[i] >> 4];
            c[i * 2 + 1] = h[bytes[i] & 0xf];
        }
        return new string(c);
    }
}
