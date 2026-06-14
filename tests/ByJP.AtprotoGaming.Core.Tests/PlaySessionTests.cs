using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class PlaySessionTests
{
    private const string PlayId = "3kplaykey0000";
    private const string Game = "at://did:web:g/games.gamesgamesgamesgames.game/3m";
    private static string Rkey => PlayId; // valid as-is

    private static JsonObject? StateEntry(JsonObject rec, string type, string id) =>
        ((JsonArray)rec["state"]!).FirstOrDefault(n =>
            n!["$type"]!.GetValue<string>() == type && n["id"]?.GetValue<string>() == id) as JsonObject;

    private static long Metric(JsonObject rec, string id) =>
        StateEntry(rec, PlaySession.MetricType, id)!["value"]!.GetValue<long>();

    private static void SetStoredMetric(JsonObject rec, string id, long value)
    {
        var entry = StateEntry(rec, PlaySession.MetricType, id);
        if (entry != null) { entry["value"] = value; return; }
        ((JsonArray)rec["state"]!).Add(new JsonObject
        {
            ["$type"] = PlaySession.MetricType, ["id"] = id, ["value"] = value,
        });
    }

    [Fact]
    public async Task OneTransactionBatchesEveryChangeIntoASingleWrite()
    {
        using var h = await Harness.OnlineAsync();
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);

        var tx = play.BeginUpdate();
        tx.SetMetric("score", 1234)
          .UpdateMetric("kills", 3, ProgressOp.Add)
          .AddAcquisition(new JsonObject { ["id"] = "relic.cracked_core", ["kind"] = "relic" })
          .RouteArrive("boss:effigy")
          .SetOutcome("failed", "effigy")
          .Finish("2026-06-07T12:00:00.0000000Z", 2837);
        var result = await tx.CommitAsync();

        Assert.Equal(PutStatus.Published, result.Status);
        Assert.Equal(1, h.Pds.PutCount); // the play record; the stats record is a separate createRecord

        var rec = (JsonObject)h.Pds.Stored(PlaySession.Collection, Rkey)!.Value.value;
        Assert.Equal(1234, Metric(rec, "score"));
        Assert.Equal(3, Metric(rec, "kills"));
        Assert.Equal("relic.cracked_core", StateEntry(rec, PlaySession.AcquisitionType, "relic.cracked_core")!["id"]!.GetValue<string>());
        Assert.Equal("boss:effigy", StateEntry(rec, PlaySession.RouteStopType, "boss:effigy")!["id"]!.GetValue<string>());
        Assert.Equal("failed", rec["outcome"]!["type"]!.GetValue<string>());
        Assert.Equal(2837, rec["duration"]!.GetValue<int>());
        Assert.Equal("1.0.0", rec["versions"]!["game"]!.GetValue<string>());
        Assert.Equal("2026-06-07T11:00:00.0000000Z", rec["startedAt"]!.GetValue<string>());
        Assert.Equal("2026-06-07T12:00:00.0000000Z", rec["updatedAt"]!.GetValue<string>()); // FixedClock
    }

    [Fact]
    public async Task SetParticipantsRejectsNonSteamId64SteamIds()
    {
        using var h = await Harness.OnlineAsync();
        var tx = h.OpenPlay(PlayId, Game, StatsSource.Steam).BeginUpdate();

        // The mistakes the check exists to catch: SteamID2, SteamID3, bare account id.
        Assert.Throws<ArgumentException>(() =>
            tx.SetParticipants(new[] { new JsonObject { ["steam"] = "STEAM_1:1:16867251" } }));
        Assert.Throws<ArgumentException>(() =>
            tx.SetParticipants(new[] { new JsonObject { ["steam"] = "[U:1:33734503]" } }));
        Assert.Throws<ArgumentException>(() =>
            tx.SetParticipants(new[] { new JsonObject { ["steam"] = "33734503" } }));
    }

    [Fact]
    public async Task SetParticipantsAcceptsSteamId64AndAtprotoOnlyParticipants()
    {
        using var h = await Harness.OnlineAsync();
        var tx = h.OpenPlay(PlayId, Game, StatsSource.Steam).BeginUpdate();

        tx.SetParticipants(new[]
        {
            new JsonObject { ["steam"] = "76561197994000231", ["atproto"] = "did:plc:abc" },
            new JsonObject { ["atproto"] = "did:plc:xyz" }, // atproto-only (no steam) is allowed
        });

        Assert.Equal(1, tx.Count);
    }

    [Fact]
    public async Task StatsUriIsResolvedAndInsertedAtWrite()
    {
        using var h = await Harness.OnlineAsync();
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);

        var tx = play.BeginUpdate();
        tx.SetMetric("score", 1);
        await tx.CommitAsync();

        var rec = (JsonObject)h.Pds.Stored(PlaySession.Collection, Rkey)!.Value.value;
        var statsUri = rec["stats"]!.GetValue<string>();
        Assert.StartsWith($"at://{FakePds.TestDid}/games.gamesgamesgamesgames.actor.stats/", statsUri);

        // a stats record was created for this game + source
        var statsRkey = statsUri.Substring(statsUri.LastIndexOf('/') + 1);
        var stats = (JsonObject)h.Pds.Stored("games.gamesgamesgamesgames.actor.stats", statsRkey)!.Value.value;
        Assert.Equal(Game, stats["game"]!["uri"]!.GetValue<string>());
        Assert.Equal("steam", stats["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task NothingIsWrittenBeforeCommit()
    {
        using var h = await Harness.OnlineAsync();
        var tx = h.OpenPlay(PlayId, Game, StatsSource.Steam).BeginUpdate();
        tx.SetMetric("score", 1);
        tx.AddAcquisition(new JsonObject { ["id"] = "x" });

        Assert.Equal(0, h.Pds.PutCount);
        Assert.Equal(2, tx.Count);
        Assert.Null(h.Pds.Stored(PlaySession.Collection, Rkey));
    }

    [Fact]
    public async Task EachCommitIsOneWriteAndStartedAtIsPreserved()
    {
        using var h = await Harness.OnlineAsync();
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);

        var t1 = play.BeginUpdate();
        t1.SetMetric("score", 1);
        await t1.CommitAsync();

        var t2 = play.BeginUpdate();
        t2.SetMetric("score", 2);
        await t2.CommitAsync();

        Assert.Equal(2, h.Pds.PutCount);
        var rec = (JsonObject)h.Pds.Stored(PlaySession.Collection, Rkey)!.Value.value;
        Assert.Equal(2, Metric(rec, "score"));
        Assert.Equal("2026-06-07T11:00:00.0000000Z", rec["startedAt"]!.GetValue<string>()); // written once
    }

    [Fact]
    public async Task OfflineThenOnlineFlushIncrementsAgainstTheRealValue()
    {
        using var h = await Harness.OnlineAsync();
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);

        var t1 = play.BeginUpdate();
        t1.UpdateMetric("kills", 5, ProgressOp.Add);
        await t1.CommitAsync();                                   // stored kills = 5

        // Another writer pushes kills to 100 (and bumps the CID).
        h.Pds.MutateExternally(PlaySession.Collection, Rkey, v => SetStoredMetric(v, "kills", 100));

        // Go offline, increment by 3 — queued, not written.
        h.Auth.Set(AuthStatus.Offline, did: FakePds.TestDid);
        var t2 = play.BeginUpdate();
        t2.UpdateMetric("kills", 3, ProgressOp.Add);
        var offline = await t2.CommitAsync();
        Assert.Equal(PutStatus.Queued, offline.Status);

        // Back online: the queued delta applies to the REAL 100, not the stale 5.
        h.Auth.Set(AuthStatus.Ok, did: FakePds.TestDid);
        await h.PlayWriter.FlushAsync();

        var rec = (JsonObject)h.Pds.Stored(PlaySession.Collection, Rkey)!.Value.value;
        Assert.Equal(103, Metric(rec, "kills"));
    }

    [Fact]
    public async Task OfflineFirstPlayIsCreatedWithStatsOnFlush()
    {
        using var h = await Harness.OnlineAsync(); // client authenticated; we toggle the auth status
        h.Auth.Set(AuthStatus.Offline, did: FakePds.TestDid);
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);

        var tx = play.BeginUpdate();
        tx.SetMetric("score", 7);
        var offline = await tx.CommitAsync();
        Assert.Equal(PutStatus.Queued, offline.Status);
        Assert.Null(h.Pds.Stored(PlaySession.Collection, Rkey));

        h.Auth.Set(AuthStatus.Ok, did: FakePds.TestDid);
        await h.PlayWriter.FlushAsync();

        var rec = (JsonObject)h.Pds.Stored(PlaySession.Collection, Rkey)!.Value.value;
        Assert.Equal(7, Metric(rec, "score"));
        Assert.StartsWith("at://", rec["stats"]!.GetValue<string>()); // resolved + inserted at flush
    }

    [Fact]
    public void AddAcquisitionValidatesIdWhenRecorded()
    {
        using var h = Harness.Offline();
        var tx = h.OpenPlay(PlayId, Game, StatsSource.Steam).BeginUpdate();
        Assert.Throws<ArgumentException>(() => tx.AddAcquisition(new JsonObject { ["kind"] = "relic" }));
    }

    [Fact]
    public void SettingRejectsArrayValues()
    {
        using var h = Harness.Offline();
        var tx = h.OpenPlay(PlayId, Game, StatsSource.Steam).BeginUpdate();
        Assert.Throws<ArgumentException>(() => tx.SetSetting("loadout", new JsonArray { "a", "b" }));
    }

    [Fact]
    public async Task CommittingTwiceThrows()
    {
        using var h = await Harness.OnlineAsync();
        var tx = h.OpenPlay(PlayId, Game, StatsSource.Steam).BeginUpdate();
        tx.SetMetric("score", 1);
        await tx.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CommitAsync());
        Assert.Throws<InvalidOperationException>(() => tx.SetMetric("score", 2));
    }

    [Fact]
    public async Task PackageVersionEntryIsInjectedOnCommit()
    {
        using var h = await Harness.OnlineAsync();
        var tx = h.OpenPlay(PlayId, Game, StatsSource.Steam).BeginUpdate();
        tx.SetMetric("score", 1);
        await tx.CommitAsync();

        var additional = (JsonArray)h.Pds.Stored(PlaySession.Collection, Rkey)!.Value.value["versions"]!["additional"]!;
        Assert.Contains(additional, e => e!["name"]!.GetValue<string>() == VersionsInjector.PackageName);
    }

    [Fact]
    public async Task ForkPlayClonesValuesVerbatimAndLinksParent()
    {
        using var h = await Harness.OnlineAsync();
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);

        var t = play.BeginUpdate();
        t.SetMetric("score", 42)
         .SetSetting("character", "huntress")
         .AddAcquisition(new JsonObject { ["id"] = "syringe", ["instanceId"] = "1" });
        await t.CommitAsync();
        var parentCid = h.Pds.Stored(PlaySession.Collection, PlayId)!.Value.cid;

        const string forkId = "3kforkkey0000";
        var fork = play.ForkPlay(forkId);
        Assert.Equal(forkId, fork.Rkey);

        var ft = fork.BeginUpdate();
        ft.SetMetric("score", 50);
        Assert.Equal(PutStatus.Published, (await ft.CommitAsync()).Status);

        var rec = (JsonObject)h.Pds.Stored(PlaySession.Collection, forkId)!.Value.value;
        Assert.Equal(50, Metric(rec, "score"));                                                       // cloned then updated
        Assert.Equal("huntress", StateEntry(rec, PlaySession.SettingType, "character")!["value"]!.GetValue<string>()); // cloned verbatim
        Assert.Equal("syringe", StateEntry(rec, PlaySession.AcquisitionType, "syringe")!["id"]!.GetValue<string>());   // cloned verbatim
        Assert.Equal("2026-06-07T11:00:00.0000000Z", rec["startedAt"]!.GetValue<string>()); // original start kept

        var forkedFrom = rec["forkedFrom"]!.AsObject();
        Assert.Equal($"at://{FakePds.TestDid}/{PlaySession.Collection}/{PlayId}", forkedFrom["uri"]!.GetValue<string>());
        Assert.Equal(parentCid, forkedFrom["cid"]!.GetValue<string>());
    }

    [Fact]
    public async Task ForkingAPlayWithAnOutcomeThrows()
    {
        using var h = await Harness.OnlineAsync();
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);
        var t = play.BeginUpdate();
        t.SetMetric("score", 1).SetOutcome("failed", "boss");
        await t.CommitAsync();

        Assert.Throws<InvalidOperationException>(() => play.ForkPlay());
    }

    [Fact]
    public async Task ForkingAFinishedPlayThrows()
    {
        using var h = await Harness.OnlineAsync();
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);
        var t = play.BeginUpdate();
        t.SetMetric("score", 1).Finish("2026-06-07T12:00:00.0000000Z", 60);
        await t.CommitAsync();

        Assert.Throws<InvalidOperationException>(() => play.ForkPlay());
    }

    [Fact]
    public void ForkBeforeCommitThrows()
    {
        using var h = Harness.Offline();
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);
        Assert.Throws<InvalidOperationException>(() => play.ForkPlay());
    }
}
