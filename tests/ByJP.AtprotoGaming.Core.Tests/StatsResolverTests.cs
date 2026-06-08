using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class StatsResolverTests
{
    private const string Stats = RollingStats.StatsCollection;
    private const string GameA = "at://did:web:g/games.gamesgamesgamesgames.game/A";
    private const string GameB = "at://did:web:g/games.gamesgamesgamesgames.game/B";

    private static StatsResolver NewResolver(Harness h) =>
        new StatsResolver(h.Client, h.Config, new FixedClock());

    private static JsonObject StatsRecord(string game, string source, string? lastPlayed = null, bool signed = false)
    {
        var r = new JsonObject { ["game"] = new JsonObject { ["uri"] = game }, ["source"] = source };
        if (lastPlayed != null) r["lastPlayed"] = lastPlayed;
        if (signed) r["signatures"] = new JsonArray { new JsonObject { ["cid"] = "x" } };
        return r;
    }

    [Fact]
    public async Task FindsTheRecordMatchingGameAndSource()
    {
        using var h = await Harness.OnlineAsync();
        h.Pds.Seed(Stats, "rkA", StatsRecord(GameA, "steam"));
        h.Pds.Seed(Stats, "rkB", StatsRecord(GameB, "steam"));
        h.Pds.Seed(Stats, "rkC", StatsRecord(GameA, "gog"));

        var rkey = await NewResolver(h).EnsureRkeyAsync(FakePds.TestDid, GameA, "steam");
        Assert.Equal("rkA", rkey);
    }

    [Fact]
    public async Task PrefersUnsignedOverSigned()
    {
        using var h = await Harness.OnlineAsync();
        h.Pds.Seed(Stats, "rkSigned", StatsRecord(GameA, "steam", lastPlayed: "2026-12-01T00:00:00Z", signed: true));
        h.Pds.Seed(Stats, "rkUnsigned", StatsRecord(GameA, "steam", lastPlayed: "2026-01-01T00:00:00Z"));

        var rkey = await NewResolver(h).EnsureRkeyAsync(FakePds.TestDid, GameA, "steam");
        Assert.Equal("rkUnsigned", rkey); // unsigned wins even though older
    }

    [Fact]
    public async Task AmongUnsignedPicksMostRecentLastPlayed()
    {
        using var h = await Harness.OnlineAsync();
        h.Pds.Seed(Stats, "rkOld", StatsRecord(GameA, "steam", lastPlayed: "2026-01-01T00:00:00Z"));
        h.Pds.Seed(Stats, "rkNew", StatsRecord(GameA, "steam", lastPlayed: "2026-06-01T00:00:00Z"));

        var rkey = await NewResolver(h).EnsureRkeyAsync(FakePds.TestDid, GameA, "steam");
        Assert.Equal("rkNew", rkey);
    }

    [Fact]
    public async Task CreatesAStatsRecordWhenNoneMatch()
    {
        using var h = await Harness.OnlineAsync();
        h.Pds.Seed(Stats, "rkOther", StatsRecord(GameB, "steam"));

        var rkey = await NewResolver(h).EnsureRkeyAsync(FakePds.TestDid, GameA, "steam");

        var created = (JsonObject)h.Pds.Stored(Stats, rkey)!.Value.value;
        Assert.Equal(GameA, created["game"]!["uri"]!.GetValue<string>());
        Assert.Equal("steam", created["source"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(created["createdAt"]!.GetValue<string>()));
    }

    [Fact]
    public async Task CachesTheResolvedRkeyToAvoidRescanning()
    {
        using var h = await Harness.OnlineAsync();
        h.Pds.Seed(Stats, "rkA", StatsRecord(GameA, "steam"));
        var resolver = NewResolver(h);

        await resolver.EnsureRkeyAsync(FakePds.TestDid, GameA, "steam");
        var listsAfterFirst = h.Pds.ListCount;
        await resolver.EnsureRkeyAsync(FakePds.TestDid, GameA, "steam");

        Assert.Equal(listsAfterFirst, h.Pds.ListCount); // second call used the cached rkey
        Assert.Equal("rkA", h.Config.Current.StatsRkey);
    }
}
