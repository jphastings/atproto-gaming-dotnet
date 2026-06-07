using System.Linq;
using System.Text.Json.Nodes;
using ByJP.AtprotoGaming.Core;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class VersionsInjectorTests
{
    private static JsonArray Additional(JsonObject record) =>
        (JsonArray)record["versions"]!["additional"]!;

    [Fact]
    public void AppendsOwnEntryPreservingConsumerEntries()
    {
        var injector = new VersionsInjector("9.9.9");
        var record = new JsonObject
        {
            ["versions"] = new JsonObject
            {
                ["game"] = "0.107.0",
                ["additional"] = new JsonArray
                {
                    new JsonObject { ["name"] = "sts2-atproto", ["version"] = "0.16.1" },
                },
            },
        };

        injector.InjectInto(record);

        var add = Additional(record);
        Assert.Equal("0.107.0", record["versions"]!["game"]!.GetValue<string>());
        Assert.Contains(add, e => e!["name"]!.GetValue<string>() == "sts2-atproto");
        var self = add.Single(e => e!["name"]!.GetValue<string>() == VersionsInjector.PackageName);
        Assert.Equal("9.9.9", self!["version"]!.GetValue<string>());
    }

    [Fact]
    public void CreatesAdditionalArrayWhenAbsent()
    {
        var injector = new VersionsInjector("1.0.0");
        var record = new JsonObject { ["versions"] = new JsonObject { ["game"] = "1.0" } };

        injector.InjectInto(record);

        Assert.Single(Additional(record));
    }

    [Fact]
    public void IsIdempotentAcrossReSerialization()
    {
        var injector = new VersionsInjector("2.0.0");
        var record = new JsonObject { ["versions"] = new JsonObject { ["game"] = "1.0" } };

        injector.InjectInto(record);
        injector.InjectInto(record);

        var selfEntries = Additional(record)
            .Count(e => e!["name"]!.GetValue<string>() == VersionsInjector.PackageName);
        Assert.Equal(1, selfEntries);
    }

    [Fact]
    public void LeavesRecordsWithoutVersionsUntouched()
    {
        var injector = new VersionsInjector("1.0.0");
        var record = new JsonObject { ["$type"] = "some.stats.record", ["playtime"] = 5 };

        injector.InjectInto(record);

        Assert.False(record.ContainsKey("versions"));
    }
}
