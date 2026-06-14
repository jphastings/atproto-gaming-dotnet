using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core;
using Xunit;

namespace ByJP.AtprotoGaming.Sidecar.Tests;

public class ProtocolTests
{
    private const string OpenLine =
        "{\"cmd\":\"open\",\"game\":\"at://did:plc:gb/dev.cartridge.game/super-mario-land\"," +
        "\"gameVersion\":\"1.1\",\"source\":\"mgba\",\"playId\":\"run-1\"}";

    private static bool Ok(JsonObject r) => r["ok"]!.GetValue<bool>();
    private static string Error(JsonObject r) => r["error"]!.GetValue<string>();
    private static string Str(JsonObject r, string field) => r[field]!.GetValue<string>();

    private static JsonObject? State(JsonObject record, string type, string? id = null)
    {
        if (record["state"] is not JsonArray state) return null;
        foreach (var n in state)
        {
            if (n!["$type"]!.GetValue<string>() != type) continue;
            if (id == null || n["id"]?.GetValue<string>() == id) return (JsonObject)n;
        }
        return null;
    }

    private static bool HasVersion(JsonObject record, string name, string version)
    {
        if (record["versions"]?["additional"] is not JsonArray additional) return false;
        foreach (var n in additional)
            if (n!["name"]?.GetValue<string>() == name && n["version"]?.GetValue<string>() == version)
                return true;
        return false;
    }

    [Fact]
    public async Task Commands_before_hello_are_rejected()
    {
        using var h = await Harness.OnlineAsync();
        var r = await h.SendAsync(OpenLine);
        Assert.False(Ok(r));
        Assert.Equal("notReady", Error(r));
    }

    [Fact]
    public async Task Hello_ok_reports_auth_and_did()
    {
        using var h = await Harness.OnlineAsync();
        var r = await h.HelloAsync();
        Assert.True(Ok(r));
        Assert.Equal("ready", Str(r, "type"));
        Assert.Equal("ok", Str(r, "auth"));
        Assert.Equal(FakePds.TestDid, Str(r, "did"));
    }

    [Fact]
    public async Task Malformed_client_is_rejected_and_handshake_does_not_complete()
    {
        using var h = await Harness.OnlineAsync();
        var r = await h.HelloAsync(client: "no-version");
        Assert.False(Ok(r));
        Assert.Equal("invalidValue", Error(r));
        Assert.Equal("notReady", Error(await h.SendAsync(OpenLine))); // handshake didn't complete
    }

    [Fact]
    public async Task First_write_batches_opening_setup_and_records_the_client_version()
    {
        using var h = await Harness.OnlineAsync();
        await h.HelloAsync(client: "mgba-atproto/0.1");
        h.Approve();
        var rkey = Str(await h.SendAsync(OpenLine), "rkey");

        await h.SendAsync("{\"cmd\":\"setup.set\",\"character\":\"mario\"}");
        await h.SendAsync("{\"cmd\":\"metric.set\",\"name\":\"score\",\"value\":1}");
        Assert.Equal("deferred", Str(await h.SendAsync("{\"cmd\":\"commit\"}"), "status")); // batched in the initial delay
        Assert.Null(h.Pds.Stored(PlaySession.Collection, rkey));                            // nothing written yet

        h.Clock.Advance(System.TimeSpan.FromSeconds(16));
        Assert.Equal("published", Str(await h.SendAsync("{\"cmd\":\"commit\"}"), "status")); // one write: seed + setup + metric
        var value = (JsonObject)h.Pds.Stored(PlaySession.Collection, rkey)!.Value.value;
        Assert.True(HasVersion(value, "mgba-atproto", "0.1"));
        Assert.NotNull(State(value, PlaySession.SetupType));
        Assert.Equal("1", State(value, PlaySession.MetricType, "score")!["value"]!.ToJsonString());
    }

    [Fact]
    public async Task Unknown_client_is_pending_then_publishes_after_approval()
    {
        using var h = await Harness.OnlineAsync();
        var ready = await h.HelloAsync();
        Assert.Equal("pending", Str(ready, "approval"));

        var rkey = Str(await h.SendAsync(OpenLine), "rkey");
        await h.SendAsync("{\"cmd\":\"metric.set\",\"name\":\"score\",\"value\":10}");

        var pending = await h.SendAsync("{\"cmd\":\"commit\"}");
        Assert.True(Ok(pending));
        Assert.Equal("pending", Str(pending, "status"));
        Assert.Null(h.Pds.Stored(PlaySession.Collection, rkey)); // nothing written while pending

        h.Approve();
        h.Clock.Advance(System.TimeSpan.FromSeconds(16)); // approval takes real time; the initial delay has passed
        var published = await h.SendAsync("{\"cmd\":\"commit\"}"); // buffer was preserved → publishes now
        Assert.Equal("published", Str(published, "status"));
        var stored = h.Pds.Stored(PlaySession.Collection, rkey);
        Assert.NotNull(stored);
        Assert.Equal("10",
            State((JsonObject)stored!.Value.value, PlaySession.MetricType, "score")!["value"]!.ToJsonString());
    }

    [Fact]
    public async Task Open_mutations_commit_publishes_a_record_with_the_mapped_state()
    {
        using var h = await Harness.OnlineAsync();
        await h.HelloAsync();
        h.Approve();
        var rkey = Str(await h.SendAsync(OpenLine), "rkey");

        await h.SendAsync("{\"cmd\":\"metric.update\",\"name\":\"score\",\"value\":400,\"op\":\"max\"}");
        await h.SendAsync("{\"cmd\":\"route.arrive\",\"id\":\"1-1\",\"instanceId\":\"1-1@0\"}");
        await h.SendAsync("{\"cmd\":\"outcome.set\",\"type\":\"death\",\"cause\":\"fell\"}");
        await h.SendAsync("{\"cmd\":\"finish\",\"endedAt\":\"2026-06-14T12:18:42Z\",\"durationSeconds\":1122}");
        var committed = await h.SendAsync("{\"cmd\":\"commit\"}");

        Assert.True(Ok(committed));
        Assert.Equal("published", Str(committed, "status"));

        var stored = h.Pds.Stored(PlaySession.Collection, rkey);
        Assert.NotNull(stored);
        var value = (JsonObject)stored!.Value.value;
        Assert.Equal("death", value["outcome"]!["type"]!.GetValue<string>());
        Assert.Equal("1122", value["duration"]!.ToJsonString());
        Assert.Equal("400", State(value, PlaySession.MetricType, "score")!["value"]!.ToJsonString());
        Assert.NotNull(State(value, PlaySession.RouteStopType));
    }

    private static string Score(string rkey, Harness h) =>
        State((JsonObject)h.Pds.Stored(PlaySession.Collection, rkey)!.Value.value,
            PlaySession.MetricType, "score")!["value"]!.ToJsonString();

    [Fact]
    public async Task First_write_waits_out_the_initial_delay_then_throttles_per_interval()
    {
        using var h = await Harness.OnlineAsync();
        await h.HelloAsync();
        h.Approve();
        var rkey = Str(await h.SendAsync(OpenLine), "rkey");

        await h.SendAsync("{\"cmd\":\"metric.set\",\"name\":\"score\",\"value\":1}");
        Assert.Equal("deferred", Str(await h.SendAsync("{\"cmd\":\"commit\"}"), "status")); // within the initial delay
        Assert.Null(h.Pds.Stored(PlaySession.Collection, rkey));                            // no record yet

        h.Clock.Advance(System.TimeSpan.FromSeconds(16));
        await h.SendAsync("{\"cmd\":\"metric.set\",\"name\":\"score\",\"value\":2}");
        Assert.Equal("published", Str(await h.SendAsync("{\"cmd\":\"commit\"}"), "status")); // initial delay elapsed → first write
        Assert.Equal("2", Score(rkey, h));

        await h.SendAsync("{\"cmd\":\"metric.set\",\"name\":\"score\",\"value\":3}");
        Assert.Equal("deferred", Str(await h.SendAsync("{\"cmd\":\"commit\"}"), "status")); // within the interval
        Assert.Equal("2", Score(rkey, h));

        h.Clock.Advance(System.TimeSpan.FromSeconds(61));
        await h.SendAsync("{\"cmd\":\"metric.set\",\"name\":\"score\",\"value\":4}");
        Assert.Equal("published", Str(await h.SendAsync("{\"cmd\":\"commit\"}"), "status")); // interval elapsed
        Assert.Equal("4", Score(rkey, h));
    }

    [Fact]
    public async Task Outcome_flushes_immediately_within_the_window()
    {
        using var h = await Harness.OnlineAsync();
        await h.HelloAsync();
        h.Approve();
        var rkey = Str(await h.SendAsync(OpenLine), "rkey");

        await h.SendAsync("{\"cmd\":\"metric.set\",\"name\":\"score\",\"value\":1}");
        await h.SendAsync("{\"cmd\":\"commit\"}"); // first publish

        await h.SendAsync("{\"cmd\":\"metric.set\",\"name\":\"score\",\"value\":5}");
        Assert.Equal("deferred", Str(await h.SendAsync("{\"cmd\":\"commit\"}"), "status"));

        await h.SendAsync("{\"cmd\":\"outcome.set\",\"type\":\"death\"}"); // terminal — bypasses throttle
        var committed = await h.SendAsync("{\"cmd\":\"commit\"}");
        Assert.Equal("published", Str(committed, "status"));

        var value = (JsonObject)h.Pds.Stored(PlaySession.Collection, rkey)!.Value.value;
        Assert.Equal("death", value["outcome"]!["type"]!.GetValue<string>());
        Assert.Equal("5", State(value, PlaySession.MetricType, "score")!["value"]!.ToJsonString());
    }

    [Fact]
    public async Task Setting_with_an_array_value_is_invalid_but_the_session_survives()
    {
        using var h = await Harness.OnlineAsync();
        await h.HelloAsync();
        await h.SendAsync(OpenLine);

        var bad = await h.SendAsync("{\"cmd\":\"setting.set\",\"name\":\"loadout\",\"value\":[1,2,3]}");
        Assert.False(Ok(bad));
        Assert.Equal("invalidValue", Error(bad));

        var good = await h.SendAsync("{\"cmd\":\"metric.set\",\"name\":\"score\",\"value\":10}");
        Assert.True(Ok(good));
    }

    [Fact]
    public async Task Non_camelCase_key_warns_but_succeeds()
    {
        using var h = await Harness.OnlineAsync();
        await h.HelloAsync();
        await h.SendAsync(OpenLine);
        var r = await h.SendAsync("{\"cmd\":\"metric.set\",\"name\":\"Score\",\"value\":1}");
        Assert.True(Ok(r));
        Assert.NotNull(r["warn"]);
    }

    [Fact]
    public async Task Unknown_command_is_rejected()
    {
        using var h = await Harness.OnlineAsync();
        await h.HelloAsync();
        await h.SendAsync(OpenLine);
        var r = await h.SendAsync("{\"cmd\":\"frobnicate\"}");
        Assert.False(Ok(r));
        Assert.Equal("unknownCommand", Error(r));
    }

    [Fact]
    public async Task Mutation_without_open_is_no_session()
    {
        using var h = await Harness.OnlineAsync();
        await h.HelloAsync();
        var r = await h.SendAsync("{\"cmd\":\"metric.set\",\"name\":\"score\",\"value\":1}");
        Assert.False(Ok(r));
        Assert.Equal("noSession", Error(r));
    }

    [Fact]
    public async Task Commit_with_nothing_buffered_is_a_noop()
    {
        using var h = await Harness.OnlineAsync();
        await h.HelloAsync();
        await h.SendAsync(OpenLine);
        var r = await h.SendAsync("{\"cmd\":\"commit\"}");
        Assert.True(Ok(r));
        Assert.Equal("noop", Str(r, "status"));
    }

    [Fact]
    public async Task Invalid_game_uri_is_invalid_value()
    {
        using var h = await Harness.OnlineAsync();
        await h.HelloAsync();
        var r = await h.SendAsync(
            "{\"cmd\":\"open\",\"game\":\"not-a-uri\",\"gameVersion\":\"1\",\"source\":\"mgba\",\"playId\":\"x\"}");
        Assert.False(Ok(r));
        Assert.Equal("invalidValue", Error(r));
    }

    [Fact]
    public async Task Derive_yields_a_stable_rkey()
    {
        using var h = await Harness.OnlineAsync();
        await h.HelloAsync();
        const string line =
            "{\"cmd\":\"open\",\"game\":\"at://did:plc:gb/dev.cartridge.game/super-mario-land\"," +
            "\"gameVersion\":\"1.1\",\"source\":\"mgba\",\"derive\":{\"startedAt\":\"2026-06-14T12:00:00Z\",\"seed\":\"sml\"}}";
        var a = await h.SendAsync(line);
        var b = await h.SendAsync(line);
        Assert.Equal(Str(a, "rkey"), Str(b, "rkey"));
    }

    [Fact]
    public async Task Correlation_id_is_echoed()
    {
        using var h = await Harness.OnlineAsync();
        var r = await h.SendAsync(
            "{\"cmd\":\"hello\",\"protocol\":1,\"clientId\":\"" + Harness.ClientId + "\",\"client\":\"tests/1.0\",\"id\":42}");
        Assert.Equal("42", r["re"]!.ToJsonString());
    }

    private static JsonObject? PlayByRkey(JsonArray plays, string rkey)
    {
        foreach (var p in plays)
            if (p is JsonObject o && o["rkey"]?.GetValue<string>() == rkey) return o;
        return null;
    }

    private static async Task<string> CreatePlayAsync(Harness h, string game, string playId, long score, bool ended)
    {
        var rkey = Str(await h.SendAsync(
            $"{{\"cmd\":\"open\",\"game\":\"{game}\",\"gameVersion\":\"1\",\"source\":\"mgba\",\"playId\":\"{playId}\"}}"), "rkey");
        await h.SendAsync($"{{\"cmd\":\"metric.set\",\"name\":\"score\",\"value\":{score}}}");
        if (ended)
        {
            await h.SendAsync("{\"cmd\":\"outcome.set\",\"type\":\"succeeded\"}");
            await h.SendAsync("{\"cmd\":\"finish\",\"endedAt\":\"2026-06-14T12:00:00Z\",\"durationSeconds\":60}");
        }
        else
        {
            h.Clock.Advance(System.TimeSpan.FromSeconds(16)); // past the initial delay so the commit flushes
        }
        await h.SendAsync("{\"cmd\":\"commit\"}");
        return rkey;
    }

    [Fact]
    public async Task Plays_list_returns_unended_plays_for_the_game_with_the_metric()
    {
        const string g1 = "at://did:plc:gb/dev.cartridge.game/super-mario-land";
        const string g2 = "at://did:plc:gb/dev.cartridge.game/tetris";
        using var h = await Harness.OnlineAsync();
        await h.HelloAsync();
        h.Approve();

        var a = await CreatePlayAsync(h, g1, "run-a", 100, ended: false);
        var b = await CreatePlayAsync(h, g1, "run-b", 250, ended: false);
        await CreatePlayAsync(h, g1, "run-c", 50, ended: true);   // ended → excluded
        await CreatePlayAsync(h, g2, "run-d", 999, ended: false); // other game → excluded

        var resp = await h.SendAsync($"{{\"cmd\":\"plays.list\",\"game\":\"{g1}\",\"metric\":\"score\"}}");
        Assert.True(Ok(resp));
        Assert.Equal("plays", Str(resp, "type"));
        var plays = (JsonArray)resp["plays"]!;

        Assert.Equal(2, plays.Count);
        Assert.Equal("100", PlayByRkey(plays, a)!["value"]!.ToJsonString());
        Assert.Equal("250", PlayByRkey(plays, b)!["value"]!.ToJsonString());
        Assert.Null(PlayByRkey(plays, "run-c"));
        Assert.Null(PlayByRkey(plays, "run-d"));
    }

    [Fact]
    public async Task Plays_list_without_sign_in_is_unavailable()
    {
        using var h = await Harness.UnconfiguredAsync();
        await h.HelloAsync();
        var resp = await h.SendAsync(
            "{\"cmd\":\"plays.list\",\"game\":\"at://did:plc:gb/dev.cartridge.game/super-mario-land\"}");
        Assert.False(Ok(resp));
        Assert.Equal("unavailable", Error(resp));
    }
}
