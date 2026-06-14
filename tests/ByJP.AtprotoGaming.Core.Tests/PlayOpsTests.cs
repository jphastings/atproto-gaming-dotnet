using System;
using System.Linq;
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

    private static JsonArray State(JsonObject rec) => (JsonArray)rec["state"]!;

    private static JsonObject? Entry(JsonObject rec, string type, string id) =>
        State(rec).FirstOrDefault(n =>
            n!["$type"]!.GetValue<string>() == type && n["id"]?.GetValue<string>() == id) as JsonObject;

    private static JsonArray EntriesOfType(JsonObject rec, string type) =>
        new JsonArray(State(rec).Where(n => n!["$type"]!.GetValue<string>() == type)
            .Select(n => n!.DeepClone()).ToArray());

    // Mirrors how PlayUpdate.SetMetric serialises the value: int-backed when it
    // fits, so callers can read GetValue<int>().
    private static JsonNode NumberNode(long value) =>
        value >= int.MinValue && value <= int.MaxValue ? (JsonNode)(int)value : value;

    private static JsonObject Metric(string id, long value) => new JsonObject
    {
        ["op"] = "state", ["type"] = PlaySession.MetricType, ["mode"] = "keyed",
        ["entry"] = new JsonObject { ["id"] = id, ["value"] = NumberNode(value) },
    };

    private static JsonObject Setting(string id, JsonNode value) => new JsonObject
    {
        ["op"] = "state", ["type"] = PlaySession.SettingType, ["mode"] = "keyed",
        ["entry"] = new JsonObject { ["id"] = id, ["value"] = value },
    };

    private static JsonObject Acquisition(JsonObject item) => new JsonObject
    {
        ["op"] = "state", ["type"] = PlaySession.AcquisitionType, ["mode"] = "instanced", ["entry"] = item,
    };

    private static JsonObject Bump(string id, long value, string operation) =>
        new JsonObject { ["op"] = "bumpMetric", ["id"] = id, ["value"] = value, ["operation"] = operation };

    [Fact]
    public void StateEntriesAreWrittenWithTheirType()
    {
        var rec = Apply(new JsonObject(),
            Metric("score", 1234),
            Setting("character", "silent"));

        Assert.Equal(1234, Entry(rec, PlaySession.MetricType, "score")!["value"]!.GetValue<int>());
        Assert.Equal("silent", Entry(rec, PlaySession.SettingType, "character")!["value"]!.GetValue<string>());
    }

    [Fact]
    public void KeyedUpsertReplacesSameIdRatherThanDuplicating()
    {
        var rec = Apply(new JsonObject(), Metric("hp", 50), Metric("hp", 30));

        Assert.Single(EntriesOfType(rec, PlaySession.MetricType));
        Assert.Equal(30, Entry(rec, PlaySession.MetricType, "hp")!["value"]!.GetValue<int>());
    }

    [Fact]
    public void BumpMetricAddStartsFromZeroAndAccumulates()
    {
        var rec = Apply(new JsonObject(), Bump("kills", 3, "add"));
        Assert.Equal(3, Entry(rec, PlaySession.MetricType, "kills")!["value"]!.GetValue<int>());

        Apply(rec, Bump("kills", 2, "add"));
        Assert.Equal(5, Entry(rec, PlaySession.MetricType, "kills")!["value"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("subtract", 10, 4, 6)]
    [InlineData("min", 10, 4, 4)]
    [InlineData("min", 4, 10, 4)]
    [InlineData("max", 10, 4, 10)]
    [InlineData("max", 4, 10, 10)]
    public void BumpMetricAppliesTheOperation(string op, int start, int value, int expected)
    {
        var rec = Apply(new JsonObject(), Metric("v", start), Bump("v", value, op));
        Assert.Equal(expected, Entry(rec, PlaySession.MetricType, "v")!["value"]!.GetValue<int>());
    }

    [Fact]
    public void BumpMetricMaxOnAbsentSeedsTheValue()
    {
        var rec = Apply(new JsonObject(), Bump("highest", 42, "max"));
        Assert.Equal(42, Entry(rec, PlaySession.MetricType, "highest")!["value"]!.GetValue<int>());
    }

    [Fact]
    public void BumpMetricOnNonIntegerThrows()
    {
        // A metric whose value was somehow written as a string can't be bumped.
        var rec = new JsonObject
        {
            ["state"] = new JsonArray
            {
                new JsonObject { ["$type"] = PlaySession.MetricType, ["id"] = "rank", ["value"] = "gold" },
            },
        };
        Assert.Throws<InvalidOperationException>(() => Apply(rec, Bump("rank", 1, "add")));
    }

    [Fact]
    public void OutcomeIsTopLevelAndFinishSetsEndedAt()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "setOutcome", ["type"] = "failed", ["cause"] = "boss" },
            new JsonObject { ["op"] = "finish", ["endedAt"] = "2026-06-07T12:00:00Z", ["duration"] = 60 });

        Assert.Equal("failed", rec["outcome"]!["type"]!.GetValue<string>());
        Assert.Equal("boss", rec["outcome"]!["cause"]!.GetValue<string>());
        Assert.Equal("2026-06-07T12:00:00Z", rec["endedAt"]!.GetValue<string>());
        Assert.Equal(60, rec["duration"]!.GetValue<int>());
    }

    [Fact]
    public void SetAcquisitionsReplacesWhileAddAppends()
    {
        var rec = Apply(new JsonObject(),
            Acquisition(new JsonObject { ["id"] = "a" }),
            new JsonObject
            {
                ["op"] = "setAcquisitions",
                ["items"] = new JsonArray { new JsonObject { ["id"] = "x" }, new JsonObject { ["id"] = "y" } },
            });

        var acquisitions = EntriesOfType(rec, PlaySession.AcquisitionType);
        Assert.Equal(2, acquisitions.Count); // the appended "a" was replaced
        Assert.Equal("x", acquisitions[0]!["id"]!.GetValue<string>());
        Assert.Equal(PlaySession.AcquisitionType, acquisitions[0]!["$type"]!.GetValue<string>());
    }

    [Fact]
    public void SetAcquisitionsLeavesOtherStateTypesUntouched()
    {
        var rec = Apply(new JsonObject(),
            Metric("score", 5),
            Acquisition(new JsonObject { ["id"] = "a" }),
            new JsonObject { ["op"] = "setAcquisitions", ["items"] = new JsonArray { new JsonObject { ["id"] = "x" } } });

        Assert.Equal(5, Entry(rec, PlaySession.MetricType, "score")!["value"]!.GetValue<int>());
        Assert.Single(EntriesOfType(rec, PlaySession.AcquisitionType));
    }

    [Fact]
    public void AddAcquisitionDedupesByInstanceId()
    {
        var item = new JsonObject { ["id"] = "syringe", ["instanceId"] = "uuid-1", ["useCount"] = 1 };
        var rec = Apply(new JsonObject(), Acquisition((JsonObject)item.DeepClone()));

        var updated = (JsonObject)item.DeepClone();
        updated["useCount"] = 5;
        Apply(rec, Acquisition(updated));

        var acquisitions = EntriesOfType(rec, PlaySession.AcquisitionType);
        Assert.Single(acquisitions);
        Assert.Equal(5, acquisitions[0]!["useCount"]!.GetValue<int>());
    }

    [Fact]
    public void SetupMergesAcrossCallsWithoutClobbering()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "setSetup", ["fields"] = new JsonObject { ["seed"] = "AXK", ["mode"] = "daily" } },
            new JsonObject { ["op"] = "setSetup", ["fields"] = new JsonObject { ["character"] = "huntress" } });

        var setup = EntriesOfType(rec, PlaySession.SetupType);
        Assert.Single(setup); // singleton
        Assert.Equal("AXK", setup[0]!["seed"]!.GetValue<string>());
        Assert.Equal("daily", setup[0]!["mode"]!.GetValue<string>());
        Assert.Equal("huntress", setup[0]!["character"]!.GetValue<string>()); // later field didn't wipe earlier
    }

    [Fact]
    public void AddModifierDedupesByIdWithinSetup()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "addModifier", ["modifier"] = new JsonObject { ["id"] = "artifact.sacrifice" } },
            new JsonObject
            {
                ["op"] = "addModifier",
                ["modifier"] = new JsonObject { ["id"] = "artifact.sacrifice", ["name"] = "Sacrifice" },
            });

        var setup = (JsonObject)State(rec).Single(n => n!["$type"]!.GetValue<string>() == PlaySession.SetupType)!;
        var modifiers = (JsonArray)setup["modifiers"]!;
        Assert.Single(modifiers);
        Assert.Equal("Sacrifice", modifiers[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void GenericSingletonReplaceKeepsOneEntryPerType()
    {
        const string type = "com.example.game.state.weather";
        JsonObject Replace(string v) => new JsonObject
        {
            ["op"] = "state", ["type"] = type, ["mode"] = "singleton",
            ["entry"] = new JsonObject { ["condition"] = v },
        };

        var rec = Apply(new JsonObject(), Replace("rain"), Replace("snow"));

        var entries = EntriesOfType(rec, type);
        Assert.Single(entries);
        Assert.Equal("snow", entries[0]!["condition"]!.GetValue<string>());
    }

    private static string[] TypeOrder(JsonObject rec) =>
        State(rec).Select(n => n!["$type"]!.GetValue<string>()).ToArray();

    [Fact]
    public void EditingAKeyedEntryMovesItToTheEndForLastEditedOrdering()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "setSetup", ["fields"] = new JsonObject { ["seed"] = "AXK" } },
            Metric("a", 1),
            Metric("b", 2));

        var metrics = EntriesOfType(rec, PlaySession.MetricType);
        Assert.Equal("a", metrics[0]!["id"]!.GetValue<string>()); // insertion order: a, b
        Assert.Equal("b", metrics[1]!["id"]!.GetValue<string>());

        Apply(rec, Metric("a", 9)); // re-editing "a" moves it after "b"

        metrics = EntriesOfType(rec, PlaySession.MetricType);
        Assert.Equal("b", metrics[0]!["id"]!.GetValue<string>());
        Assert.Equal("a", metrics[1]!["id"]!.GetValue<string>());
        Assert.Equal(9, metrics[1]!["value"]!.GetValue<int>());

        // setup, untouched since creation, stays at the front
        Assert.Equal(PlaySession.SetupType, State(rec)[0]!["$type"]!.GetValue<string>());
    }

    [Fact]
    public void EditingTheSetupSingletonMovesItToTheEnd()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "setSetup", ["fields"] = new JsonObject { ["seed"] = "AXK" } },
            Metric("a", 1));
        Assert.Equal(new[] { PlaySession.SetupType, PlaySession.MetricType }, TypeOrder(rec));

        // A later setup edit (eg. character merged once known) re-dates it to the end.
        Apply(rec, new JsonObject { ["op"] = "setSetup", ["fields"] = new JsonObject { ["character"] = "huntress" } });

        Assert.Equal(new[] { PlaySession.MetricType, PlaySession.SetupType }, TypeOrder(rec));
        var setup = (JsonObject)State(rec).Last(n => n!["$type"]!.GetValue<string>() == PlaySession.SetupType)!;
        Assert.Equal("AXK", setup["seed"]!.GetValue<string>());       // merge preserved earlier field
        Assert.Equal("huntress", setup["character"]!.GetValue<string>());
    }

    [Fact]
    public void RouteArriveThenLeaveByInstanceIdSetsLeftAt()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "routeArrive", ["id"] = "golemplains", ["instanceId"] = "1", ["arrivedAt"] = "t1" },
            new JsonObject { ["op"] = "routeLeave", ["id"] = "golemplains", ["instanceId"] = "1", ["leftAt"] = "t2" });

        var route = EntriesOfType(rec, PlaySession.RouteStopType);
        Assert.Single(route);
        Assert.Equal("t1", route[0]!["arrivedAt"]!.GetValue<string>());
        Assert.Equal("t2", route[0]!["leftAt"]!.GetValue<string>());
    }

    [Fact]
    public void RouteArriveIsIdempotentByInstanceId()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "routeArrive", ["id"] = "stage", ["instanceId"] = "7", ["arrivedAt"] = "t1" });
        Apply(rec, new JsonObject { ["op"] = "routeArrive", ["id"] = "stage", ["instanceId"] = "7", ["arrivedAt"] = "t1" });

        Assert.Single(EntriesOfType(rec, PlaySession.RouteStopType));
    }

    [Fact]
    public void RouteLeaveWithoutInstanceIdMintsNoPhantomOnReapply()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "routeArrive", ["id"] = "boss", ["arrivedAt"] = "t1" },
            new JsonObject { ["op"] = "routeLeave", ["id"] = "boss", ["leftAt"] = "t2" });
        Apply(rec, new JsonObject { ["op"] = "routeLeave", ["id"] = "boss", ["leftAt"] = "t3" });

        var route = EntriesOfType(rec, PlaySession.RouteStopType);
        Assert.Single(route);
        Assert.Equal("t2", route[0]!["leftAt"]!.GetValue<string>()); // first close stuck; re-apply is a no-op
    }

    [Fact]
    public void RouteLeaveWithInstanceIdBeforeArriveMintsTheStop()
    {
        var rec = Apply(new JsonObject(),
            new JsonObject { ["op"] = "routeLeave", ["id"] = "boss", ["instanceId"] = "9", ["leftAt"] = "t2" });

        var route = EntriesOfType(rec, PlaySession.RouteStopType);
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

        var route = EntriesOfType(rec, PlaySession.RouteStopType);
        Assert.Equal(2, route.Count);
        Assert.False(route[0]!.AsObject().ContainsKey("leftAt")); // first revisit still open
        Assert.Equal("z", route[1]!["leftAt"]!.GetValue<string>()); // newest open one closed
    }

    [Fact]
    public void SetParticipantsReplacesTheTopLevelList()
    {
        var rec = new JsonObject { ["participants"] = new JsonArray { new JsonObject { ["steam"] = "old" } } };
        Apply(rec, new JsonObject
        {
            ["op"] = "setParticipants",
            ["participants"] = new JsonArray { new JsonObject { ["steam"] = "111" }, new JsonObject { ["steam"] = "222" } },
        });

        var participants = (JsonArray)rec["participants"]!;
        Assert.Equal(2, participants.Count);
        Assert.Equal("111", participants[0]!["steam"]!.GetValue<string>());
    }

    [Fact]
    public void OpsAreReplaySafeAcrossRecords()
    {
        // The same ops array re-applied to a fresh record (as happens on a
        // conflict refetch) must not throw on already-parented nodes, and must
        // converge to the same state.
        var ops = new JsonArray
        {
            Acquisition(new JsonObject { ["id"] = "relic", ["instanceId"] = "i1" }),
            Metric("score", 1),
            Bump("kills", 2, "add"),
        };

        var a = new JsonObject();
        var b = new JsonObject();
        PlayOps.Apply(a, ops);
        PlayOps.Apply(b, ops);

        Assert.Single(EntriesOfType(b, PlaySession.AcquisitionType)); // not duplicated by re-apply
        Assert.Equal("relic", Entry(b, PlaySession.AcquisitionType, "relic")!["id"]!.GetValue<string>());
        Assert.Equal(1, Entry(b, PlaySession.MetricType, "score")!["value"]!.GetValue<int>());
        Assert.Equal(2, Entry(b, PlaySession.MetricType, "kills")!["value"]!.GetValue<int>());
    }

    [Fact]
    public void ClearOutcomeRemovesTheOutcomeAndReAppliesSafely()
    {
        // set-then-clear, applied to two records (eg. an offline flush re-applying the
        // same ops list) must converge to "no outcome" and never throw.
        var ops = new JsonArray
        {
            new JsonObject { ["op"] = "setOutcome", ["type"] = "abandoned" },
            new JsonObject { ["op"] = "clearOutcome" },
        };
        var a = new JsonObject();
        var b = new JsonObject();
        PlayOps.Apply(a, ops);
        PlayOps.Apply(b, ops);
        Assert.Null(a["outcome"]);
        Assert.Null(b["outcome"]);

        // Clearing an already-set outcome works; clearing again is a harmless no-op.
        var ended = new JsonObject();
        PlayOps.Apply(ended, new JsonArray { new JsonObject { ["op"] = "setOutcome", ["type"] = "succeeded" } });
        Assert.NotNull(ended["outcome"]);
        PlayOps.Apply(ended, new JsonArray { new JsonObject { ["op"] = "clearOutcome" } });
        PlayOps.Apply(ended, new JsonArray { new JsonObject { ["op"] = "clearOutcome" } });
        Assert.Null(ended["outcome"]);
    }
}
