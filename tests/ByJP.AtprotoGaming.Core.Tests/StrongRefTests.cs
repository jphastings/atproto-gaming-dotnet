using System.Text.Json.Nodes;
using ByJP.AtprotoGaming.Core;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class StrongRefTests
{
    [Fact]
    public void CreateProducesUriAndCidObject()
    {
        var sr = StrongRef.Create("at://did:plc:abc/c/r", "bafyreiabc");
        Assert.Equal("at://did:plc:abc/c/r", sr["uri"]!.GetValue<string>());
        Assert.Equal("bafyreiabc", sr["cid"]!.GetValue<string>());
    }

    [Fact]
    public void ComputeCidIsDeterministicAndDagCborShaped()
    {
        var body = new JsonObject { ["$type"] = "x.y.z", ["a"] = 1 };
        var cid1 = StrongRef.ComputeCid(body);
        var cid2 = StrongRef.ComputeCid(body.DeepClone());
        Assert.Equal(cid1, cid2);
        // CIDv1 dag-cbor + sha-256 renders with the bafyrei… multibase prefix.
        Assert.StartsWith("bafyrei", cid1);
    }

    [Fact]
    public void DifferentBodiesGiveDifferentCids()
    {
        var a = StrongRef.ComputeCid(new JsonObject { ["$type"] = "x", ["v"] = 1 });
        var b = StrongRef.ComputeCid(new JsonObject { ["$type"] = "x", ["v"] = 2 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void FromRecordBodyBuildsMatchingStrongRef()
    {
        var body = new JsonObject { ["$type"] = "x", ["v"] = 1 };
        var sr = StrongRef.FromRecordBody("at://did:plc:abc/c/r", body);
        Assert.Equal(StrongRef.ComputeCid(body), sr["cid"]!.GetValue<string>());
        Assert.Equal("at://did:plc:abc/c/r", sr["uri"]!.GetValue<string>());
    }
}
