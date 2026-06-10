using System;
using System.Text.Json.Nodes;
using ByJP.AtprotoGaming.Core;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class PlayOpsTests
{
    private static JsonObject Apply(JsonObject record, params JsonObject[] ops)
    {
        var array = new JsonArray();
        foreach (var op in ops) array.Add(op);
        PlayOps.Apply(record, array);
        return record;
    }

    [Fact]
    public void SetProgressAndSettingWriteNestedKeys()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "setProgress", ["name"] = "score", ["value"] = 1234 },
            new JsonObject { ["op"] = "setSetting", ["name"] = "character", ["value"] = "silent" });

        Assert.Equal(1234, rec["progress"]!["score"]!.GetValue<int>());
        Assert.Equal("silent", rec["settings"]!["character"]!.GetValue<string>());
    }

    private static JsonObject UpdateProgress(string name, long value, string operation) =>
        new JsonObject { ["op"] = "updateProgress", ["name"] = name, ["value"] = value, ["operation"] = operation };

    [Fact]
    public void UpdateProgressAddStartsFromZeroAndAccumulates()
    {
        var rec = Apply(new JsonObject(), UpdateProgress("kills", 3, "add"));
        Assert.Equal(3, rec["progress"]!["kills"]!.GetValue<int>());

        Apply(rec, UpdateProgress("kills", 2, "add"));
        Assert.Equal(5, rec["progress"]!["kills"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("subtract", 10, 4, 6)]
    [InlineData("min", 10, 4, 4)]
    [InlineData("min", 4, 10, 4)]
    [InlineData("max", 10, 4, 10)]
    [InlineData("max", 4, 10, 10)]
    public void UpdateProgressAppliesTheOperation(string op, int start, int value, int expected)
    {
        var rec = new JsonObject { ["progress"] = new JsonObject { ["v"] = start } };
        Apply(rec, UpdateProgress("v", value, op));
        Assert.Equal(expected, rec["progress"]!["v"]!.GetValue<int>());
    }

    [Fact]
    public void UpdateProgressMaxOnAbsentSeedsTheValue()
    {
        var rec = Apply(new JsonObject(), UpdateProgress("highest", 42, "max"));
        Assert.Equal(42, rec["progress"]!["highest"]!.GetValue<int>());
    }

    [Fact]
    public void UpdateProgressOnNonIntegerThrows()
    {
        var rec = new JsonObject { ["progress"] = new JsonObject { ["rank"] = "gold" } };
        Assert.Throws<InvalidOperationException>(() => Apply(rec, UpdateProgress("rank", 1, "add")));
    }

    [Fact]
    public void OutcomeAndFinish()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "setOutcome", ["type"] = "failed", ["cause"] = "boss" },
            new JsonObject { ["op"] = "finish", ["endedAt"] = "2026-06-07T12:00:00Z", ["duration"] = 60 });

        Assert.Equal("failed", rec["progress"]!["outcome"]!["type"]!.GetValue<string>());
        Assert.Equal("boss", rec["progress"]!["outcome"]!["cause"]!.GetValue<string>());
        Assert.Equal("2026-06-07T12:00:00Z", rec["endedAt"]!.GetValue<string>());
        Assert.Equal(60, rec["duration"]!.GetValue<int>());
    }

    [Fact]
    public void SetAcquisitionsReplacesWhileAddAppends()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "addAcquisition", ["item"] = new JsonObject { ["id"] = "a" } },
            new JsonObject { ["op"] = "setAcquisitions", ["items"] = new JsonArray
                { new JsonObject { ["id"] = "x" }, new JsonObject { ["id"] = "y" } } });

        var acquisitions = (JsonArray)rec["acquisitions"]!;
        Assert.Equal(2, acquisitions.Count); // the appended "a" was replaced
        Assert.Equal("x", acquisitions[0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void AddAcquisitionDedupesByInstanceId()
    {
        var item = new JsonObject { ["id"] = "syringe", ["instanceId"] = "uuid-1", ["useCount"] = 1 };
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "addAcquisition", ["item"] = item.DeepClone() });
        // Same instanceId re-emitted with a changed field updates rather than dupes.
        var updated = (JsonObject)item.DeepClone();
        updated["useCount"] = 5;
        Apply(rec, new JsonObject { ["op"] = "addAcquisition", ["item"] = updated });

        var acquisitions = (JsonArray)rec["acquisitions"]!;
        Assert.Single(acquisitions);
        Assert.Equal(5, acquisitions[0]!["useCount"]!.GetValue<int>());
    }

    [Fact]
    public void RouteArriveThenLeaveByInstanceIdSetsLeftAt()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "routeArrive", ["id"] = "golemplains", ["instanceId"] = "1", ["arrivedAt"] = "t1" },
            new JsonObject { ["op"] = "routeLeave", ["id"] = "golemplains", ["instanceId"] = "1", ["leftAt"] = "t2" });

        var route = (JsonArray)rec["progress"]!["route"]!;
        Assert.Single(route);
        Assert.Equal("t1", route[0]!["arrivedAt"]!.GetValue<string>());
        Assert.Equal("t2", route[0]!["leftAt"]!.GetValue<string>());
    }

    [Fact]
    public void RouteArriveIsIdempotentByInstanceId()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "routeArrive", ["id"] = "stage", ["instanceId"] = "7", ["arrivedAt"] = "t1" });
        // Re-emit (as happens every snapshot / after a crash) — no duplicate stop.
        Apply(rec, new JsonObject { ["op"] = "routeArrive", ["id"] = "stage", ["instanceId"] = "7", ["arrivedAt"] = "t1" });

        Assert.Single((JsonArray)rec["progress"]!["route"]!);
    }

    [Fact]
    public void RouteLeaveWithoutInstanceIdMintsNoPhantomOnReapply()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "routeArrive", ["id"] = "boss", ["arrivedAt"] = "t1" },
            new JsonObject { ["op"] = "routeLeave", ["id"] = "boss", ["leftAt"] = "t2" });
        // Re-apply the same leave (as a CAS retry / offline flush would) — no phantom.
        Apply(rec, new JsonObject { ["op"] = "routeLeave", ["id"] = "boss", ["leftAt"] = "t3" });

        var route = (JsonArray)rec["progress"]!["route"]!;
        Assert.Single(route);
        Assert.Equal("t2", route[0]!["leftAt"]!.GetValue<string>()); // first close stuck; re-apply is a no-op
    }

    [Fact]
    public void RouteLeaveWithInstanceIdBeforeArriveMintsTheStop()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "routeLeave", ["id"] = "boss", ["instanceId"] = "9", ["leftAt"] = "t2" });

        var route = (JsonArray)rec["progress"]!["route"]!;
        Assert.Single(route);
        Assert.Equal("9", route[0]!["instanceId"]!.GetValue<string>());
        Assert.Equal("t2", route[0]!["leftAt"]!.GetValue<string>());
    }

    [Fact]
    public void RouteLeaveWithoutInstanceIdClosesTheLastOpenStop()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "routeArrive", ["id"] = "loop", ["arrivedAt"] = "a1" },
            new JsonObject { ["op"] = "routeArrive", ["id"] = "loop", ["arrivedAt"] = "a2" },
            new JsonObject { ["op"] = "routeLeave", ["id"] = "loop", ["leftAt"] = "z" });

        var route = (JsonArray)rec["progress"]!["route"]!;
        Assert.Equal(2, route.Count);
        Assert.False(route[0]!.AsObject().ContainsKey("leftAt")); // first revisit still open
        Assert.Equal("z", route[1]!["leftAt"]!.GetValue<string>()); // newest open one closed
    }

    [Fact]
    public void SetPlayingWithReplacesTheList()
    {
        var rec = new JsonObject { ["playingWith"] = new JsonArray { new JsonObject { ["steam"] = "old" } } };
        Apply(rec, new JsonObject
        {
            ["op"] = "setPlayingWith",
            ["participants"] = new JsonArray { new JsonObject { ["steam"] = "111" }, new JsonObject { ["steam"] = "222" } },
        });

        var playingWith = (JsonArray)rec["playingWith"]!;
        Assert.Equal(2, playingWith.Count);
        Assert.Equal("111", playingWith[0]!["steam"]!.GetValue<string>());
    }

    [Fact]
    public void OpsAreReplaySafeAcrossRecords()
    {
        // The same ops array re-applied to a fresh record (as happens on a
        // conflict refetch) must not throw on already-parented nodes.
        var ops = new JsonArray
        {
            new JsonObject { ["op"] = "addAcquisition", ["item"] = new JsonObject { ["id"] = "relic" } },
            new JsonObject { ["op"] = "setProgress", ["name"] = "score", ["value"] = 1 },
        };

        var a = new JsonObject();
        var b = new JsonObject();
        PlayOps.Apply(a, ops);
        PlayOps.Apply(b, ops);

        Assert.Equal("relic", b["acquisitions"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(1, b["progress"]!["score"]!.GetValue<int>());
    }
}
