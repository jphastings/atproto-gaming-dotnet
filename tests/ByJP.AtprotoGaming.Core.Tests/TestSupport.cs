using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using ByJP.AtprotoGaming.Core;
using ByJP.AtprotoGaming.Core.Adapters;
using ByJP.AtprotoGaming.Core.Signing;

namespace ByJP.AtprotoGaming.Core.Tests;

internal static class TestKeys
{
    /// <summary>Generates a fresh P-256 <see cref="SigningKey"/> for signing tests.</summary>
    public static SigningKey NewSigningKey(string attestationType = "test.game.record#attestation")
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var d = ec.ExportParameters(includePrivateParameters: true).D!;
        if (d.Length < 32)
        {
            var padded = new byte[32];
            Buffer.BlockCopy(d, 0, padded, 32 - d.Length, d.Length);
            d = padded;
        }
        var prefixed = new byte[2 + 32];
        prefixed[0] = 0x86; prefixed[1] = 0x26; // P-256 private multicodec
        Buffer.BlockCopy(d, 0, prefixed, 2, 32);
        return SigningKey.FromDidKey("did:key:z" + Base58Btc.Encode(prefixed), attestationType);
    }
}

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
