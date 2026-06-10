using System;
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

    [Fact]
    public async Task OneTransactionBatchesEveryChangeIntoASingleWrite()
    {
        using var h = await Harness.OnlineAsync();
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);

        var tx = play.BeginUpdate();
        tx.SetProgress("score", 1234)
          .UpdateProgress("kills", 3, ProgressOp.Add)
          .AddAcquisition(new JsonObject { ["id"] = "relic.cracked_core", ["kind"] = "relic" })
          .RouteArrive("boss:effigy")
          .SetOutcome("failed", "effigy")
          .Finish("2026-06-07T12:00:00.0000000Z", 2837);
        var result = await tx.CommitAsync();

        Assert.Equal(PutStatus.Published, result.Status);
        Assert.Equal(1, h.Pds.PutCount); // the play record; the stats record is a separate createRecord

        var rec = (JsonObject)h.Pds.Stored(PlaySession.Collection, Rkey)!.Value.value;
        Assert.Equal(1234, rec["progress"]!["score"]!.GetValue<int>());
        Assert.Equal(3, rec["progress"]!["kills"]!.GetValue<int>());
        Assert.Equal("relic.cracked_core", rec["acquisitions"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("boss:effigy", rec["progress"]!["route"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("failed", rec["progress"]!["outcome"]!["type"]!.GetValue<string>());
        Assert.Equal(2837, rec["duration"]!.GetValue<int>());
        Assert.Equal("1.0.0", rec["versions"]!["game"]!.GetValue<string>());
        Assert.Equal("2026-06-07T11:00:00.0000000Z", rec["startedAt"]!.GetValue<string>());
        Assert.Equal("2026-06-07T12:00:00.0000000Z", rec["updatedAt"]!.GetValue<string>()); // FixedClock
    }

    [Fact]
    public async Task StatsUriIsResolvedAndInsertedAtWrite()
    {
        using var h = await Harness.OnlineAsync();
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);

        var tx = play.BeginUpdate();
        tx.SetProgress("score", 1);
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
        tx.SetProgress("score", 1);
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
        t1.SetProgress("score", 1);
        await t1.CommitAsync();

        var t2 = play.BeginUpdate();
        t2.SetProgress("score", 2);
        await t2.CommitAsync();

        Assert.Equal(2, h.Pds.PutCount);
        var rec = (JsonObject)h.Pds.Stored(PlaySession.Collection, Rkey)!.Value.value;
        Assert.Equal(2, rec["progress"]!["score"]!.GetValue<int>());
        Assert.Equal("2026-06-07T11:00:00.0000000Z", rec["startedAt"]!.GetValue<string>()); // written once
    }

    [Fact]
    public async Task OfflineThenOnlineFlushIncrementsAgainstTheRealValue()
    {
        using var h = await Harness.OnlineAsync();
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);

        var t1 = play.BeginUpdate();
        t1.UpdateProgress("kills", 5, ProgressOp.Add);
        await t1.CommitAsync();                                   // stored kills = 5

        // Another writer pushes kills to 100 (and bumps the CID).
        h.Pds.MutateExternally(PlaySession.Collection, Rkey, v => v["progress"]!["kills"] = 100);

        // Go offline, increment by 3 — queued, not written.
        h.Auth.Set(AuthStatus.Offline, did: FakePds.TestDid);
        var t2 = play.BeginUpdate();
        t2.UpdateProgress("kills", 3, ProgressOp.Add);
        var offline = await t2.CommitAsync();
        Assert.Equal(PutStatus.Queued, offline.Status);

        // Back online: the queued delta applies to the REAL 100, not the stale 5.
        h.Auth.Set(AuthStatus.Ok, did: FakePds.TestDid);
        await h.PlayWriter.FlushAsync();

        var rec = (JsonObject)h.Pds.Stored(PlaySession.Collection, Rkey)!.Value.value;
        Assert.Equal(103, rec["progress"]!["kills"]!.GetValue<int>());
    }

    [Fact]
    public async Task OfflineFirstPlayIsCreatedWithStatsOnFlush()
    {
        using var h = await Harness.OnlineAsync(); // client authenticated; we toggle the auth status
        h.Auth.Set(AuthStatus.Offline, did: FakePds.TestDid);
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);

        var tx = play.BeginUpdate();
        tx.SetProgress("score", 7);
        var offline = await tx.CommitAsync();
        Assert.Equal(PutStatus.Queued, offline.Status);
        Assert.Null(h.Pds.Stored(PlaySession.Collection, Rkey));

        h.Auth.Set(AuthStatus.Ok, did: FakePds.TestDid);
        await h.PlayWriter.FlushAsync();

        var rec = (JsonObject)h.Pds.Stored(PlaySession.Collection, Rkey)!.Value.value;
        Assert.Equal(7, rec["progress"]!["score"]!.GetValue<int>());
        Assert.StartsWith("at://", rec["stats"]!.GetValue<string>()); // resolved + inserted at flush
    }

    [Fact]
    public void SetProgressRejectsKeysThatHaveDedicatedHelpers()
    {
        using var h = Harness.Offline();
        var tx = h.OpenPlay(PlayId, Game, StatsSource.Steam).BeginUpdate();
        Assert.Throws<ArgumentException>(() => tx.SetProgress("outcome", 1));
        Assert.Throws<ArgumentException>(() => tx.SetProgress("route", 1));
        Assert.Throws<ArgumentException>(() => tx.UpdateProgress("route", 1, ProgressOp.Add));
    }

    [Fact]
    public void AddAcquisitionValidatesIdWhenRecorded()
    {
        using var h = Harness.Offline();
        var tx = h.OpenPlay(PlayId, Game, StatsSource.Steam).BeginUpdate();
        Assert.Throws<ArgumentException>(() => tx.AddAcquisition(new JsonObject { ["kind"] = "relic" }));
    }

    [Fact]
    public async Task CommittingTwiceThrows()
    {
        using var h = await Harness.OnlineAsync();
        var tx = h.OpenPlay(PlayId, Game, StatsSource.Steam).BeginUpdate();
        tx.SetProgress("score", 1);
        await tx.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CommitAsync());
        Assert.Throws<InvalidOperationException>(() => tx.SetProgress("score", 2));
    }

    [Fact]
    public async Task PackageVersionEntryIsInjectedOnCommit()
    {
        using var h = await Harness.OnlineAsync();
        var tx = h.OpenPlay(PlayId, Game, StatsSource.Steam).BeginUpdate();
        tx.SetProgress("score", 1);
        await tx.CommitAsync();

        var additional = (JsonArray)h.Pds.Stored(PlaySession.Collection, Rkey)!.Value.value["versions"]!["additional"]!;
        Assert.Contains(additional, e => e!["name"]!.GetValue<string>() == VersionsInjector.PackageName);
    }

    [Fact]
    public async Task ForkPlayClonesValuesDropsTerminalMarkersAndLinksParent()
    {
        using var h = await Harness.OnlineAsync();
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);

        var t = play.BeginUpdate();
        t.SetProgress("score", 42).SetOutcome("failed", "boss").Finish("2026-06-07T12:00:00.0000000Z", 60);
        await t.CommitAsync();
        var parentCid = h.Pds.Stored(PlaySession.Collection, PlayId)!.Value.cid;

        const string forkId = "3kforkkey0000";
        var fork = play.ForkPlay(forkId);
        Assert.Equal(forkId, fork.Rkey);

        var ft = fork.BeginUpdate();
        ft.SetProgress("score", 50);
        Assert.Equal(PutStatus.Published, (await ft.CommitAsync()).Status);

        var rec = (JsonObject)h.Pds.Stored(PlaySession.Collection, forkId)!.Value.value;
        Assert.Equal(50, rec["progress"]!["score"]!.GetValue<int>());     // cloned then updated
        Assert.False(rec.ContainsKey("endedAt"));                          // terminal markers dropped
        Assert.False(rec["progress"]!.AsObject().ContainsKey("outcome"));

        var forkedFrom = rec["forkedFrom"]!.AsObject();
        Assert.Equal($"at://{FakePds.TestDid}/{PlaySession.Collection}/{PlayId}", forkedFrom["uri"]!.GetValue<string>());
        Assert.Equal(parentCid, forkedFrom["cid"]!.GetValue<string>());
    }

    [Fact]
    public void ForkBeforeCommitThrows()
    {
        using var h = Harness.Offline();
        var play = h.OpenPlay(PlayId, Game, StatsSource.Steam);
        Assert.Throws<InvalidOperationException>(() => play.ForkPlay());
    }
}
