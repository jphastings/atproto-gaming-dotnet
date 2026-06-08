using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class AtprotoClientTests
{
    [Fact]
    public async Task PutReturnsUriAndCid()
    {
        using var h = await Harness.OnlineAsync();
        var written = await h.Client.PutRecordAsync("c.x", "rk", new JsonObject { ["a"] = 1 });
        Assert.StartsWith("at://", written.Uri);
        Assert.False(string.IsNullOrEmpty(written.Cid));
    }

    [Fact]
    public async Task SwapMismatchSurfacesInvalidSwapErrorName()
    {
        using var h = await Harness.OnlineAsync();
        var ex = await Assert.ThrowsAsync<AtprotoPermanentException>(
            () => h.Client.PutRecordAsync("c.x", "rk", new JsonObject { ["a"] = 1 }, swapRecord: "nonexistent-cid"));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("InvalidSwap", ex.ErrorName);
    }

    [Fact]
    public async Task GetRecordReturnsNullWhenAbsent()
    {
        using var h = await Harness.OnlineAsync();
        Assert.Null(await h.Client.GetRecordAsync("c.x", "missing"));
    }
}
