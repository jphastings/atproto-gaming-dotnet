using ByJP.AtprotoGaming.Core;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class TidTests
{
    [Fact]
    public void SameInputsProduceSameRkey()
    {
        var a = Tid.FromPlayThrough(1_700_000_000, "seed-XYZ");
        var b = Tid.FromPlayThrough(1_700_000_000, "seed-XYZ");
        Assert.Equal(a, b);
    }

    [Fact]
    public void NumericOverloadMatchesStringifiedSeed()
    {
        Assert.Equal(
            Tid.FromPlayThrough(1_700_000_000, "123456789"),
            Tid.FromPlayThrough(1_700_000_000, 123456789UL));
    }

    [Fact]
    public void DifferentSaltsDiverge()
    {
        var a = Tid.FromPlayThrough(1_700_000_000, "seed-A");
        var b = Tid.FromPlayThrough(1_700_000_000, "seed-B");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void OutputIsAValidThirteenCharTid()
    {
        var rkey = Tid.FromPlayThrough(1_700_000_000, "seed");
        Assert.Equal(13, rkey.Length);
        Assert.True(Tid.IsValid(rkey));
    }

    [Fact]
    public void LaterStartSortsAfterEarlierStart()
    {
        // TIDs are designed to sort chronologically as plain strings.
        var earlier = Tid.FromPlayThrough(1_700_000_000, "seed");
        var later = Tid.FromPlayThrough(1_700_000_100, "seed");
        Assert.True(string.CompareOrdinal(later, earlier) > 0);
    }

    [Theory]
    [InlineData("3jzfcijpj2z2a", true)]
    [InlineData("too-short", false)]
    [InlineData("0000000000000", false)]  // '0'/'1' aren't in the alphabet
    [InlineData("z234567abcdef", false)]  // first char must be high-bit-clear
    public void IsValidChecksShapeAndAlphabet(string value, bool expected)
    {
        Assert.Equal(expected, Tid.IsValid(value));
    }

    [Fact]
    public void FromAtUriExtractsTrailingRkey()
    {
        var rkey = Tid.FromPlayThrough(1_700_000_000, "seed");
        var uri = $"at://did:plc:abc/games.gamesgamesgamesgames.actor.play/{rkey}";
        Assert.Equal(rkey, Tid.FromAtUri(uri));
    }

    [Fact]
    public void FromAtUriRejectsNonTidTail()
    {
        Assert.Throws<System.FormatException>(
            () => Tid.FromAtUri("at://did:plc:abc/some.collection/not-a-tid"));
    }
}
