using System.Text.Json.Nodes;
using ByJP.AtprotoGaming.Core.Signing;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class DagCborTests
{
    private static string Encode(JsonNode n) => Hex.Of(DagCbor.Encode(n));

    [Fact]
    public void SortsMapKeysByCanonicalOrder()
    {
        // Keys must come out a-before-b regardless of insertion order:
        // A2 (map,2) 61 61 (text "a") 02 61 62 (text "b") 01
        var obj = new JsonObject { ["b"] = 1, ["a"] = 2 };
        Assert.Equal("a2616102616201", Encode(obj));
    }

    [Fact]
    public void EncodesIntegersInShortestForm()
    {
        Assert.Equal("a1616e17", Encode(new JsonObject { ["n"] = 23 }));     // 23 inline
        Assert.Equal("a1616e1818", Encode(new JsonObject { ["n"] = 24 }));   // 24 needs one extra byte
    }

    [Fact]
    public void EncodesNegativeIntegers()
    {
        Assert.Equal("a1616e20", Encode(new JsonObject { ["n"] = -1 }));     // major-1, value 0
    }

    [Fact]
    public void EncodesEmptyMap()
    {
        Assert.Equal("a0", Encode(new JsonObject()));
    }

    [Fact]
    public void EncodesBooleansAndNull()
    {
        Assert.Equal("f5", Encode(JsonValue.Create(true)));
        Assert.Equal("f4", Encode(JsonValue.Create(false)));
        Assert.Equal("f6", Encode((JsonNode?)null!));
    }

    [Fact]
    public void IsDeterministicAcrossEquivalentObjects()
    {
        var a = new JsonObject { ["x"] = 1, ["y"] = "two", ["z"] = new JsonArray { 1, 2, 3 } };
        var b = new JsonObject { ["z"] = new JsonArray { 1, 2, 3 }, ["y"] = "two", ["x"] = 1 };
        Assert.Equal(Encode(a), Encode(b));
    }
}
