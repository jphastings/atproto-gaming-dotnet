using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class RecordEditorTests
{
    private const string Collection = "games.example.thing";
    private const string Rkey = "3krecordkey00";

    private static System.Func<JsonObject> Seed() =>
        () => new JsonObject { ["$type"] = Collection, ["progress"] = new JsonObject() };

    [Fact]
    public async Task HappyPathCreatesThenSwapsWithoutRefetching()
    {
        using var h = await Harness.OnlineAsync();
        var editor = h.Records.Edit(Collection, Rkey, Seed());

        var r1 = await editor.ApplyAsync(r => ((JsonObject)r["progress"]!)["score"] = 1);
        var r2 = await editor.ApplyAsync(r => ((JsonObject)r["progress"]!)["score"] = 2);

        Assert.Equal(PutStatus.Published, r1.Status);
        Assert.Equal(PutStatus.Published, r2.Status);
        Assert.Equal(2, h.Pds.PutCount);            // one create + one swap
        Assert.Equal(1, h.Pds.GetCount);            // only the first call reads; then it's cached
        Assert.Equal(2, h.Pds.Stored(Collection, Rkey)!.Value.value["progress"]!["score"]!.GetValue<int>());
    }

    [Fact]
    public async Task SecondWriteSendsSwapRecordOfTheFirstCid()
    {
        using var h = await Harness.OnlineAsync();
        var editor = h.Records.Edit(Collection, Rkey, Seed());

        await editor.ApplyAsync(r => ((JsonObject)r["progress"]!)["score"] = 1);
        var firstCid = h.Pds.Stored(Collection, Rkey)!.Value.cid;
        await editor.ApplyAsync(r => ((JsonObject)r["progress"]!)["score"] = 2);

        Assert.Equal(firstCid, h.Pds.LastSwapRecord); // CAS guarded against the cached cid
    }

    [Fact]
    public async Task ConflictRefetchesAndReappliesPreservingBothChanges()
    {
        using var h = await Harness.OnlineAsync();
        var editor = h.Records.Edit(Collection, Rkey, Seed());

        await editor.ApplyAsync(r => ((JsonObject)r["progress"]!)["score"] = 10);

        // Another writer changes the record out from under the editor's cached cid.
        h.Pds.MutateExternally(Collection, Rkey, v => v["externalNote"] = "hi");

        var result = await editor.ApplyAsync(r => ((JsonObject)r["progress"]!)["combo"] = 7);

        Assert.Equal(PutStatus.Published, result.Status);
        var stored = h.Pds.Stored(Collection, Rkey)!.Value.value;
        Assert.Equal("hi", stored["externalNote"]!.GetValue<string>()); // external change kept
        Assert.Equal(10, stored["progress"]!["score"]!.GetValue<int>());  // earlier delta kept
        Assert.Equal(7, stored["progress"]!["combo"]!.GetValue<int>());   // new delta re-applied
    }

    [Fact]
    public async Task OfflineQueuesToOutboxWithoutHittingTheNetwork()
    {
        using var h = Harness.Offline();
        var editor = h.Records.Edit(Collection, Rkey, Seed());

        var result = await editor.ApplyAsync(r => ((JsonObject)r["progress"]!)["score"] = 5);

        Assert.Equal(PutStatus.Queued, result.Status);
        Assert.Equal(0, h.Pds.PutCount);
        var path = Path.Combine(h.Fs.OutboxRoot, "did%3Aplc%3Atesttesttesttest", Collection, Rkey + ".json");
        Assert.True(File.Exists(path));
        var queued = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(5, queued["progress"]!["score"]!.GetValue<int>());
    }

    [Fact]
    public async Task UnresolvedConflictQueuesAfterRetries()
    {
        using var h = await Harness.OnlineAsync();
        var editor = h.Records.Edit(Collection, Rkey, Seed());

        await editor.ApplyAsync(r => ((JsonObject)r["progress"]!)["score"] = 1); // create
        h.Pds.AlwaysConflict = true;

        var result = await editor.ApplyAsync(r => ((JsonObject)r["progress"]!)["score"] = 2);

        Assert.Equal(PutStatus.Queued, result.Status);
        Assert.Contains(h.Log.Warns, w => w.Contains("unresolved after"));
    }

    [Fact]
    public async Task SignedRecordKeepsExactlyOneSignatureAcrossUpdates()
    {
        using var h = await Harness.OnlineAsync(TestKeys.NewSigningKey());
        var editor = h.Records.Edit(Collection, Rkey, Seed());

        await editor.ApplyAsync(r => ((JsonObject)r["progress"]!)["score"] = 1);
        await editor.ApplyAsync(r => ((JsonObject)r["progress"]!)["score"] = 2);
        await editor.ApplyAsync(r => ((JsonObject)r["progress"]!)["score"] = 3);

        var stored = (JsonObject)h.Pds.Stored(Collection, Rkey)!.Value.value;
        var signatures = Assert.IsType<JsonArray>(stored["signatures"]);
        Assert.Single(signatures); // re-signed fresh each write, never accumulated
    }
}
