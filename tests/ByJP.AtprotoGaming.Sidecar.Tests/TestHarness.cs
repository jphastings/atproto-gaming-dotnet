using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Sidecar.Tests;

/// <summary>A trimmed in-memory PDS behind an <see cref="HttpMessageHandler"/> — enough XRPC to publish plays.</summary>
internal sealed class FakePds : HttpMessageHandler
{
    public const string TestDid = "did:plc:testtesttesttest";
    public const string BaseUrl = "https://pds.test";

    private readonly Dictionary<string, (JsonNode value, string cid)> _store = new();
    private int _seq;

    private static string Key(string c, string r) => c + "/" + r;
    private string NextCid() => "bafyreitestcid" + (++_seq).ToString("D4");

    public (JsonNode value, string cid)? Stored(string collection, string rkey) =>
        _store.TryGetValue(Key(collection, rkey), out var v) ? v : null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var nsid = path.Substring(path.LastIndexOf('/') + 1);
        JsonNode? body = request.Content is null ? null
            : JsonNode.Parse(await request.Content.ReadAsStringAsync().ConfigureAwait(false));

        switch (nsid)
        {
            case "com.atproto.server.createSession":
                return Ok(new JsonObject
                {
                    ["did"] = TestDid, ["handle"] = "test.bsky.social",
                    ["accessJwt"] = "access", ["refreshJwt"] = "refresh",
                });

            case "com.atproto.server.refreshSession":
                return Ok(new JsonObject { ["accessJwt"] = "access2", ["refreshJwt"] = "refresh2" });

            case "com.atproto.repo.getRecord":
            {
                var c = Query(request, "collection");
                var r = Query(request, "rkey");
                if (_store.TryGetValue(Key(c, r), out var rec))
                    return Ok(new JsonObject
                    {
                        ["uri"] = $"at://{TestDid}/{c}/{r}", ["cid"] = rec.cid, ["value"] = rec.value.DeepClone(),
                    });
                return Err(400, "RecordNotFound", "record not found");
            }

            case "com.atproto.repo.putRecord":
            {
                var c = body!["collection"]!.GetValue<string>();
                var r = body["rkey"]!.GetValue<string>();
                var swap = body["swapRecord"]?.GetValue<string>();
                var exists = _store.TryGetValue(Key(c, r), out var cur);
                if (swap != null && (!exists || cur.cid != swap))
                    return Err(400, "InvalidSwap", "swapRecord mismatch");
                var cid = NextCid();
                _store[Key(c, r)] = (body["record"]!.DeepClone(), cid);
                return Ok(new JsonObject { ["uri"] = $"at://{TestDid}/{c}/{r}", ["cid"] = cid });
            }

            case "com.atproto.repo.createRecord":
            {
                var c = body!["collection"]!.GetValue<string>();
                var r = "rk" + (++_seq).ToString("D4");
                var cid = NextCid();
                _store[Key(c, r)] = (body["record"]!.DeepClone(), cid);
                return Ok(new JsonObject { ["uri"] = $"at://{TestDid}/{c}/{r}", ["cid"] = cid });
            }

            case "com.atproto.repo.listRecords":
                return Ok(new JsonObject { ["records"] = new JsonArray() });

            default:
                return Err(404, "MethodNotImplemented", nsid);
        }
    }

    private static HttpResponseMessage Ok(JsonNode body) => Resp(200, body);
    private static HttpResponseMessage Err(int code, string error, string msg) =>
        Resp(code, new JsonObject { ["error"] = error, ["message"] = msg });
    private static HttpResponseMessage Resp(int code, JsonNode body) =>
        new((HttpStatusCode)code) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static string Query(HttpRequestMessage req, string key)
    {
        foreach (var pair in req.RequestUri!.Query.TrimStart('?').Split('&'))
        {
            var i = pair.IndexOf('=');
            if (i > 0 && Uri.UnescapeDataString(pair.Substring(0, i)) == key)
                return Uri.UnescapeDataString(pair.Substring(i + 1));
        }
        return "";
    }
}

/// <summary>A clock the test can advance to exercise the publish throttle.</summary>
internal sealed class TestClock : IClock
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public DateTime UtcNow { get; set; } = new(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc);
    public long UnixSeconds => (long)(UtcNow - Epoch).TotalSeconds;
    public void Advance(TimeSpan delta) => UtcNow += delta;
}

/// <summary>Drives a real <see cref="CommandProcessor"/> over a single in-memory connection.</summary>
internal sealed class Harness : IDisposable
{
    public const string ClientId = "test-client-id";
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(60);

    public FakePds Pds { get; }
    public ApprovalService Approvals { get; }
    public TestClock Clock { get; }

    private readonly string _dir;
    private readonly HttpClient _http;
    private readonly Connection _conn = new();
    private readonly CommandProcessor _processor;

    private Harness(string dir, HttpClient http, FakePds pds, AtprotoGamingClient client,
        ApprovalService approvals, TestClock clock)
    {
        _dir = dir;
        _http = http;
        Pds = pds;
        Approvals = approvals;
        Clock = clock;
        _processor = new CommandProcessor(client, approvals, clock, InitialDelay, PublishInterval, signing: false, NullLogSink.Instance);
    }

    public static async Task<Harness> OnlineAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sidecar-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var fs = FileSystem.At(dir);
        var pds = new FakePds();
        var http = new HttpClient(pds);
        var configStore = ConfigStore<SidecarConfig>.LoadOrCreate(fs, NullLogSink.Instance);
        var client = new AtprotoGamingClient(new AtprotoGamingOptions
        {
            FileSystem = fs, Log = NullLogSink.Instance, Config = configStore, HttpClient = http,
        });
        await client.Client.LoginAsync(FakePds.BaseUrl, "test.bsky.social", "pw");
        client.Auth.Set(AuthStatus.Ok, handle: "test.bsky.social", did: FakePds.TestDid, pds: FakePds.BaseUrl);
        var approvals = new ApprovalService(configStore, SystemClock.Instance, NullLogSink.Instance);
        return new Harness(dir, http, pds, client, approvals, new TestClock());
    }

    public async Task<JsonObject> SendAsync(string json)
    {
        var result = await _processor.ProcessAsync(_conn, json);
        return result.Response;
    }

    public Task<JsonObject> HelloAsync(string clientId = ClientId, string client = "tests/1.0") =>
        SendAsync($"{{\"cmd\":\"hello\",\"protocol\":1,\"clientId\":\"{clientId}\",\"client\":\"{client}\"}}");

    /// <summary>Simulate the player approving the client at the console.</summary>
    public void Approve(string clientId = ClientId) => Approvals.Approve(clientId, "tests");

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
