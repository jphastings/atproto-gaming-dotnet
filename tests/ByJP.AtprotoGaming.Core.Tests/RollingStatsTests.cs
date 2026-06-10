using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class RollingStatsTests
{
    private const string Game = "at://did:web:g/games.gamesgamesgamesgames.game/r";

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
            priorValue: null, Game, "steam", deltaMinutes: 47,
            lastPlayed: "2026-06-07T10:00:00Z", nowIso: "2026-06-07T12:00:00Z");

        Assert.Equal("games.gamesgamesgamesgames.actor.stats", merged["$type"]!.GetValue<string>());
        Assert.Equal(Game, merged["game"]!["uri"]!.GetValue<string>());
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
            prior, Game, "steam", deltaMinutes: 13,
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
            prior, Game, "steam", deltaMinutes: 5,
            lastPlayed: "2026-06-05T08:00:00Z", nowIso: "2026-06-07T12:00:00Z");

        Assert.Equal(105, merged["playtime"]!.GetValue<int>());
        Assert.Equal("2026-06-07T11:30:00Z", merged["lastPlayed"]!.GetValue<string>()); // unchanged
    }

    [Fact]
    public void BuildWithAchievementsSetsCountsAndPreservesPlaytime()
    {
        var prior = new JsonObject
        {
            ["playtime"] = 90,
            ["lastPlayed"] = "2026-06-07T10:00:00Z",
            ["createdAt"] = "2026-06-01T09:00:00Z",
        };
        var rec = RollingStats.BuildWithAchievements(prior, Game, "steam", 12, 50, "2026-06-07T12:00:00Z");

        Assert.Equal(12, rec["achievements"]!["unlocked"]!.GetValue<int>());
        Assert.Equal(50, rec["achievements"]!["total"]!.GetValue<int>());
        Assert.Equal(90, rec["playtime"]!.GetValue<int>());                       // preserved
        Assert.Equal("2026-06-01T09:00:00Z", rec["createdAt"]!.GetValue<string>()); // preserved
    }

    [Fact]
    public void BuildMergedPreservesAchievements()
    {
        // A playtime update must not wipe achievement counts (and vice versa).
        var prior = new JsonObject
        {
            ["playtime"] = 10,
            ["achievements"] = new JsonObject { ["unlocked"] = 3, ["total"] = 20 },
        };
        var rec = RollingStats.BuildMerged(prior, Game, "steam", 5, "2026-06-07T11:00:00Z", "2026-06-07T12:00:00Z");

        Assert.Equal(15, rec["playtime"]!.GetValue<int>());
        Assert.Equal(3, rec["achievements"]!["unlocked"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(12, 50, true)]
    [InlineData(13, 50, false)]
    [InlineData(12, 51, false)]
    public void AchievementsUnchangedComparesBothCounts(int unlocked, int total, bool expected)
    {
        var prior = new JsonObject { ["achievements"] = new JsonObject { ["unlocked"] = 12, ["total"] = 50 } };
        Assert.Equal(expected, RollingStats.AchievementsUnchanged(prior, unlocked, total));
    }

    [Fact]
    public void AchievementsUnchangedIsFalseWhenAbsent() =>
        Assert.False(RollingStats.AchievementsUnchanged(new JsonObject(), 0, 0));

    [Fact]
    public async Task AchievementsUnlockedSetsCountsAndKeepsPlaytime()
    {
        using var h = await Harness.OnlineAsync();
        await h.Stats.EnsureAndUpdateAsync(Game, "steam", 600, "2026-06-07T12:00:00Z"); // playtime 10
        var uri = await h.Stats.AchievementsUnlockedAsync(Game, "steam", 12, 50);

        var rkey = uri.Substring(uri.LastIndexOf('/') + 1);
        var rec = (JsonObject)h.Pds.Stored(RollingStats.StatsCollection, rkey)!.Value.value;
        Assert.Equal(12, rec["achievements"]!["unlocked"]!.GetValue<int>());
        Assert.Equal(50, rec["achievements"]!["total"]!.GetValue<int>());
        Assert.Equal(10, rec["playtime"]!.GetValue<int>()); // preserved across the achievements write
    }

    [Fact]
    public async Task AchievementsUnlockedSkipsTheWriteWhenUnchanged()
    {
        using var h = await Harness.OnlineAsync();
        await h.Stats.AchievementsUnlockedAsync(Game, "steam", 12, 50);
        var puts = h.Pds.PutCount;
        await h.Stats.AchievementsUnlockedAsync(Game, "steam", 12, 50); // re-fire, identical counts
        Assert.Equal(puts, h.Pds.PutCount);
    }
}
