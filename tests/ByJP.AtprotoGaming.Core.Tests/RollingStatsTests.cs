using System.Text.Json.Nodes;
using ByJP.AtprotoGaming.Core;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class RollingStatsTests
{
    private static readonly JsonNode GameRef = new JsonObject { ["uri"] = "at://did:web:g/g/r" };

    [Theory]
    [InlineData(0, 1)]      // sub-minute still counts as one
    [InlineData(59, 1)]
    [InlineData(60, 1)]
    [InlineData(150, 2)]
    [InlineData(3600, 60)]
    public void MinutesForFloorsButNeverBelowOne(int seconds, int expected)
    {
        Assert.Equal(expected, RollingStats.MinutesFor(seconds));
    }

    [Fact]
    public void FreshRecordSeedsFromTheDelta()
    {
        var merged = RollingStats.BuildMerged(
            priorValue: null, GameRef, "steam", deltaMinutes: 47,
            lastPlayed: "2026-06-07T10:00:00Z", nowIso: "2026-06-07T12:00:00Z");

        Assert.Equal("games.gamesgamesgamesgames.actor.stats", merged["$type"]!.GetValue<string>());
        Assert.Equal(47, merged["playtime"]!.GetValue<int>());
        Assert.Equal("2026-06-07T10:00:00Z", merged["lastPlayed"]!.GetValue<string>());
        Assert.Equal("2026-06-07T12:00:00Z", merged["createdAt"]!.GetValue<string>());
    }

    [Fact]
    public void SubsequentPlayAccumulatesPlaytimeAndPreservesCreatedAt()
    {
        var prior = new JsonObject
        {
            ["playtime"] = 47,
            ["lastPlayed"] = "2026-06-07T10:00:00Z",
            ["createdAt"] = "2026-06-01T09:00:00Z",
        };

        var merged = RollingStats.BuildMerged(
            prior, GameRef, "steam", deltaMinutes: 13,
            lastPlayed: "2026-06-07T11:30:00Z", nowIso: "2026-06-07T12:00:00Z");

        Assert.Equal(60, merged["playtime"]!.GetValue<int>());
        Assert.Equal("2026-06-07T11:30:00Z", merged["lastPlayed"]!.GetValue<string>()); // newer wins
        Assert.Equal("2026-06-01T09:00:00Z", merged["createdAt"]!.GetValue<string>()); // preserved
    }

    [Fact]
    public void OlderPlayDoesNotRegressLastPlayed()
    {
        var prior = new JsonObject
        {
            ["playtime"] = 100,
            ["lastPlayed"] = "2026-06-07T11:30:00Z",
            ["createdAt"] = "2026-06-01T09:00:00Z",
        };

        var merged = RollingStats.BuildMerged(
            prior, GameRef, "steam", deltaMinutes: 5,
            lastPlayed: "2026-06-05T08:00:00Z", nowIso: "2026-06-07T12:00:00Z");

        Assert.Equal(105, merged["playtime"]!.GetValue<int>());
        Assert.Equal("2026-06-07T11:30:00Z", merged["lastPlayed"]!.GetValue<string>()); // unchanged
    }
}
