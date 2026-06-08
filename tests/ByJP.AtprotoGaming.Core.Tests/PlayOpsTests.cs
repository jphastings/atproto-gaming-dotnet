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

    [Fact]
    public void IncrementStartsFromZeroAndAccumulates()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "increment", ["name"] = "kills", ["delta"] = 3 });
        Assert.Equal(3, rec["progress"]!["kills"]!.GetValue<int>());

        Apply(rec, new JsonObject { ["op"] = "increment", ["name"] = "kills", ["delta"] = 2 });
        Assert.Equal(5, rec["progress"]!["kills"]!.GetValue<int>());
    }

    [Fact]
    public void IncrementOnNonIntegerThrows()
    {
        var rec = new JsonObject { ["progress"] = new JsonObject { ["rank"] = "gold" } };
        Assert.Throws<InvalidOperationException>(() =>
            Apply(rec, new JsonObject { ["op"] = "increment", ["name"] = "rank", ["delta"] = 1 }));
    }

    [Fact]
    public void AppendsAndOutcomeAndFinish()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "addAcquisition", ["item"] = new JsonObject { ["id"] = "relic" } },
            new JsonObject { ["op"] = "addRouteStop", ["stop"] = new JsonObject { ["id"] = "boss" } },
            new JsonObject { ["op"] = "setOutcome", ["type"] = "failed", ["cause"] = "boss" },
            new JsonObject { ["op"] = "finish", ["endedAt"] = "2026-06-07T12:00:00Z", ["duration"] = 60 });

        Assert.Equal("relic", rec["acquisitions"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("boss", rec["progress"]!["route"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("failed", rec["progress"]!["outcome"]!["type"]!.GetValue<string>());
        Assert.Equal("boss", rec["progress"]!["outcome"]!["cause"]!.GetValue<string>());
        Assert.Equal("2026-06-07T12:00:00Z", rec["endedAt"]!.GetValue<string>());
        Assert.Equal(60, rec["duration"]!.GetValue<int>());
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
