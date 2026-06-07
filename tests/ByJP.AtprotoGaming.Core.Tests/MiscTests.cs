using System.IO;
using System.Text.Json.Nodes;
using ByJP.AtprotoGaming.Core;
using ByJP.AtprotoGaming.Core.Internal;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class DidPathTests
{
    [Fact]
    public void EncodesColonsToBeWindowsSafe()
    {
        Assert.Equal("did%3Aplc%3Aabc123", DidPath.EncodeDid("did:plc:abc123"));
    }

    [Fact]
    public void EncodedDidContainsNoReservedFilenameChars()
    {
        var encoded = DidPath.EncodeDid("did:web:example.com/path?x*y");
        foreach (var c in new[] { ':', '/', '\\', '?', '*', '"', '<', '>', '|' })
            Assert.DoesNotContain(c, encoded);
    }
}

public class AchievementDeduperTests
{
    [Fact]
    public void FirstClaimSucceedsRepeatsAreNoOps()
    {
        var d = new AchievementDeduper();
        Assert.True(d.TryClaim("ach.first-blood"));
        Assert.False(d.TryClaim("ach.first-blood"));
        Assert.False(d.TryClaim("ach.first-blood"));
    }

    [Fact]
    public void DifferentAchievementsAreIndependent()
    {
        var d = new AchievementDeduper();
        Assert.True(d.TryClaim("a"));
        Assert.True(d.TryClaim("b"));
    }
}

public class OutboxLayoutTests
{
    private static Outbox NewOutbox(TempFileSystem fs, out AuthState auth)
    {
        auth = new AuthState();
        var log = new CapturingLogSink();
        var client = new AtprotoClient(auth, new FixedClock(), log);
        return new Outbox(fs, log, auth, client);
    }

    [Fact]
    public void EnqueueWritesPayloadBucketedByDidAndCollection()
    {
        using var fs = new TempFileSystem();
        var outbox = NewOutbox(fs, out _);
        var payload = new JsonObject { ["$type"] = "c.r", ["v"] = 1 };

        outbox.Enqueue("did:plc:abc", "games.example.play", "3krkey0000abc", payload);

        var path = Path.Combine(fs.OutboxRoot, "did%3Aplc%3Aabc",
            "games.example.play", "3krkey0000abc.json");
        Assert.True(File.Exists(path));
        Assert.Equal(payload.ToJsonString(), File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp")); // atomic write left no temp
    }

    [Fact]
    public void RemoveDeletesTheQueuedFile()
    {
        using var fs = new TempFileSystem();
        var outbox = NewOutbox(fs, out _);
        var payload = new JsonObject { ["v"] = 1 };
        outbox.Enqueue("did:plc:abc", "c.r", "3kxyz0000abcd", payload);

        outbox.Remove("did:plc:abc", "c.r", "3kxyz0000abcd");

        var path = Path.Combine(fs.OutboxRoot, "did%3Aplc%3Aabc", "c.r", "3kxyz0000abcd.json");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DifferentDidsKeepSeparateBuckets()
    {
        using var fs = new TempFileSystem();
        var outbox = NewOutbox(fs, out _);
        outbox.Enqueue("did:plc:aaa", "c.r", "3kaaa0000aaaa", new JsonObject { ["v"] = 1 });
        outbox.Enqueue("did:plc:bbb", "c.r", "3kbbb0000bbbb", new JsonObject { ["v"] = 2 });

        Assert.True(File.Exists(Path.Combine(fs.OutboxRoot, "did%3Aplc%3Aaaa", "c.r", "3kaaa0000aaaa.json")));
        Assert.True(File.Exists(Path.Combine(fs.OutboxRoot, "did%3Aplc%3Abbb", "c.r", "3kbbb0000bbbb.json")));
    }
}
