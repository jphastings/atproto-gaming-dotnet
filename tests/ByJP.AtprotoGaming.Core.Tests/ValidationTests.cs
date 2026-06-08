using System;
using System.Security.Cryptography;
using ByJP.AtprotoGaming.Core;
using ByJP.AtprotoGaming.Core.Adapters;
using ByJP.AtprotoGaming.Core.Signing;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class ValidationTests
{
    // ── SigningKey ─────────────────────────────────────────────────────────

    [Fact]
    public void SigningKeyRejectsAPublicDidKey()
    {
        var publicDidKey = SigningKey.FromDidKey(NewPrivateDidKey(), "t#a").PublicDidKey;
        var ex = Assert.Throws<FormatException>(() => SigningKey.FromDidKey(publicDidKey, "t#a"));
        Assert.Contains("private", ex.Message);
    }

    [Fact]
    public void SigningKeyRejectsEmptyInputs()
    {
        Assert.Throws<ArgumentNullException>(() => SigningKey.FromDidKey("", "t#a"));
        Assert.Throws<ArgumentNullException>(() => SigningKey.FromDidKey(NewPrivateDidKey(), ""));
    }

    // ── DidKey.Parse ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("not-a-did-key")]  // missing did:key: scheme
    [InlineData("did:key:Qabc")]   // non-'z' (non-base58btc) multibase
    public void DidKeyParseRejectsMalformed(string didKey)
    {
        Assert.Throws<FormatException>(() => DidKey.Parse(didKey));
    }

    [Fact]
    public void DidKeyParseRejectsUnsupportedMulticodec()
    {
        var ed25519 = new byte[34];
        ed25519[0] = 0xed; ed25519[1] = 0x01; // ed25519-pub multicodec, not P-256
        var didKey = "did:key:z" + Base58Btc.Encode(ed25519);

        var ex = Assert.Throws<FormatException>(() => DidKey.Parse(didKey));
        Assert.Contains("multicodec", ex.Message);
    }

    // ── Tid ────────────────────────────────────────────────────────────────

    [Fact]
    public void TidFromPlayThroughRejectsNullSalt()
    {
        Assert.Throws<ArgumentNullException>(() => Tid.FromPlayThrough(0, (string)null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TidFromAtUriRejectsEmpty(string? uri)
    {
        Assert.Throws<ArgumentNullException>(() => Tid.FromAtUri(uri!));
    }

    // ── StrongRef ──────────────────────────────────────────────────────────

    [Fact]
    public void StrongRefCreateRejectsNulls()
    {
        Assert.Throws<ArgumentNullException>(() => StrongRef.Create(null!, "cid"));
        Assert.Throws<ArgumentNullException>(() => StrongRef.Create("at://uri", null!));
    }

    [Fact]
    public void StrongRefComputeCidRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => StrongRef.ComputeCid(null!));
    }

    // ── ConfigStore ────────────────────────────────────────────────────────

    [Fact]
    public void ConfigStoreRejectsNullArguments()
    {
        using var fs = new TempFileSystem();
        Assert.Throws<ArgumentNullException>(
            () => ConfigStore<CoreConfig>.LoadOrCreate(null!, NullLogSink.Instance));
        Assert.Throws<ArgumentNullException>(
            () => ConfigStore<CoreConfig>.LoadOrCreate(fs, null!));
    }

    private static string NewPrivateDidKey()
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
        return "did:key:z" + Base58Btc.Encode(prefixed);
    }
}
