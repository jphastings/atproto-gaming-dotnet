using System;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using ByJP.AtprotoGaming.Core;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class RecordKeyTests
{
    [Theory]
    [InlineData("3jzfcijpj2z2a", true)]                 // a TID
    [InlineData("save:slot-1~v2.0_final", true)]        // all allowed punctuation
    [InlineData("A", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("has space", false)]
    [InlineData("has/slash", false)]
    [InlineData("emoji😀", false)]
    public void IsValidEnforcesTheRules(string key, bool expected) =>
        Assert.Equal(expected, RecordKey.IsValid(key));

    [Fact]
    public void RejectsKeysOver512Chars() =>
        Assert.False(RecordKey.IsValid(new string('a', 513)));

    [Fact]
    public void SanitizePassesValidKeysThrough()
    {
        Assert.Equal("3jzfcijpj2z2a", RecordKey.Sanitize("3jzfcijpj2z2a"));
        Assert.Equal("my-save_1", RecordKey.Sanitize("my-save_1"));
    }

    [Fact]
    public void SanitizeHashesInvalidKeysToValidDeterministicOnes()
    {
        var sanitized = RecordKey.Sanitize("save slot #1 (final/best)");
        Assert.True(RecordKey.IsValid(sanitized));
        Assert.Equal(43, sanitized.Length);                         // base64url of SHA-256
        Assert.Equal(sanitized, RecordKey.Sanitize("save slot #1 (final/best)")); // deterministic
        Assert.NotEqual(sanitized, RecordKey.Sanitize("a different id"));
    }
}

public class AtUriTests
{
    [Theory]
    [InlineData("at://did:web:gamesgamesgamesgames.games/games.gamesgamesgamesgames.game/3mglj4k2edl2l", true)]
    [InlineData("at://did:plc:abc", true)]
    [InlineData("at://example.com/c/r", true)]          // handle authority
    [InlineData("https://example.com", false)]
    [InlineData("at://", false)]
    [InlineData("at:///c/r", false)]                    // empty authority
    [InlineData("did:plc:abc", false)]                  // missing scheme
    [InlineData("", false)]
    public void IsValidChecksSchemeAndAuthority(string uri, bool expected) =>
        Assert.Equal(expected, AtUri.IsValid(uri));
}

public class DerivePlayIdTests
{
    [Fact]
    public void IsDeterministicAndTidShaped()
    {
        var a = PlaySession.DerivePlayID(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), "seed-X");
        var b = PlaySession.DerivePlayID(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), "seed-X");
        Assert.Equal(a, b);
        Assert.True(Tid.IsValid(a));
        Assert.True(RecordKey.IsValid(a));   // a TID is always a valid record key
    }

    [Fact]
    public void DifferentSeedsDiverge()
    {
        var a = PlaySession.DerivePlayID(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), "seed-A");
        var b = PlaySession.DerivePlayID(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), "seed-B");
        Assert.NotEqual(a, b);
    }
}

public class PlaySeedTests
{
    [Fact]
    public void BuildsRequiredFieldsWithoutStatsOrUpdatedAt()
    {
        var mods = new Dictionary<string, string> { ["my-mod"] = "1.2.3" };
        var seed = PlaySession.BuildSeed(
            "at://did:web:g/games.gamesgamesgamesgames.game/3m", "0.107.0", mods, "2026-06-08T10:00:00.0000000Z");

        Assert.Equal(PlaySession.Collection, seed["$type"]!.GetValue<string>());
        Assert.Equal("at://did:web:g/games.gamesgamesgamesgames.game/3m", seed["game"]!.GetValue<string>());
        Assert.Equal("2026-06-08T10:00:00.0000000Z", seed["startedAt"]!.GetValue<string>());
        Assert.Equal("0.107.0", seed["versions"]!["game"]!.GetValue<string>());
        var additional = (JsonArray)seed["versions"]!["additional"]!;
        Assert.Equal("my-mod", additional[0]!["name"]!.GetValue<string>());
        Assert.Equal("1.2.3", additional[0]!["version"]!.GetValue<string>());
        // stats is resolved at write time; updatedAt is stamped at commit.
        Assert.False(seed.ContainsKey("stats"));
        Assert.False(seed.ContainsKey("updatedAt"));
    }

    [Fact]
    public void ToleratesNoAdditionalVersions()
    {
        var seed = PlaySession.BuildSeed("at://did:plc:x/c/r", "1.0", null, "2026-06-08T10:00:00.0000000Z");
        Assert.Empty((JsonArray)seed["versions"]!["additional"]!);
    }
}
