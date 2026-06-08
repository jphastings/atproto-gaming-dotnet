using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// Speaks the slice of the atproto XRPC surface a publish-and-update workflow
    /// needs. Refreshes the access JWT proactively (never reactively on 401) and
    /// maps HTTP outcomes to <see cref="AtprotoPermanentException"/> (4xx, drop) vs
    /// <see cref="AtprotoTransientException"/> (5xx / network, queue).
    /// </summary>
    public sealed class AtprotoClient
    {
        // PDS access JWTs are typically ~2h; refreshing at 80 min leaves headroom
        // for clock skew and a long single call without a token-near-expiry race.
        private static readonly TimeSpan AccessTokenTtl = TimeSpan.FromMinutes(80);

        private readonly HttpClient _http;
        private readonly IClock _clock;
        private readonly ILogSink _log;
        private readonly AuthState _auth;
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

        private string _pdsUrl = "";
        private string? _did;
        private string? _accessJwt;
        private string? _refreshJwt;
        private DateTime _expiresAt;

        public AtprotoClient(AuthState auth, IClock clock, ILogSink log, HttpClient? http = null)
        {
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _http = http ?? new HttpClient();
        }

        public bool IsAuthenticated => _accessJwt != null;

        public string Did => _did ?? throw new InvalidOperationException("not authenticated");

        public string PdsUrl => _pdsUrl;

        /// <summary>Log in against a specific PDS with app-password credentials.</summary>
        /// <exception cref="AtprotoPermanentException">Credentials rejected (4xx).</exception>
        /// <exception cref="AtprotoTransientException">PDS unreachable or 5xx.</exception>
        public async Task LoginAsync(string pdsUrl, string identifier, string appPassword)
        {
            _pdsUrl = pdsUrl.TrimEnd('/');
            var body = await SendForJsonAsync(() => Post(
                "com.atproto.server.createSession",
                new { identifier, password = appPassword },
                bearer: null)).ConfigureAwait(false);

            _did = body?["did"]?.GetValue<string>()
                   ?? throw new AtprotoTransientException("createSession returned no did");
            ApplyTokens(body!);
        }

        public async Task<WriteResult> CreateRecordAsync(string collection, JsonNode record)
        {
            await EnsureFreshAsync().ConfigureAwait(false);
            var body = await SendForJsonAsync(() => Post(
                "com.atproto.repo.createRecord",
                new { repo = _did, collection, record })).ConfigureAwait(false);
            return ReadWriteResult(body, "createRecord");
        }

        /// <param name="swapRecord">
        /// Optional optimistic-lock guard: the CID the record is expected to have.
        /// When set, the PDS applies the write only if it still matches, otherwise
        /// it rejects with <c>InvalidSwap</c> (a 4xx <see cref="AtprotoPermanentException"/>).
        /// </param>
        public async Task<WriteResult> PutRecordAsync(string collection, string rkey, JsonNode record, string? swapRecord = null)
        {
            await EnsureFreshAsync().ConfigureAwait(false);
            var body = await SendForJsonAsync(() => Post(
                "com.atproto.repo.putRecord",
                swapRecord == null
                    ? (object)new { repo = _did, collection, rkey, record }
                    : new { repo = _did, collection, rkey, record, swapRecord })).ConfigureAwait(false);
            return ReadWriteResult(body, "putRecord");
        }

        private static WriteResult ReadWriteResult(JsonNode? body, string op)
        {
            var uri = body?["uri"]?.GetValue<string>()
                      ?? throw new AtprotoTransientException($"{op} returned no uri");
            var cid = body?["cid"]?.GetValue<string>() ?? "";
            return new WriteResult(uri, cid);
        }

        /// <summary>Fetches a record. Returns null on 4xx (e.g. not found); throws on transient failure.</summary>
        public async Task<JsonNode?> GetRecordAsync(string collection, string rkey)
        {
            await EnsureFreshAsync().ConfigureAwait(false);
            var url = $"{_pdsUrl}/xrpc/com.atproto.repo.getRecord"
                      + $"?repo={Uri.EscapeDataString(_did!)}"
                      + $"&collection={Uri.EscapeDataString(collection)}"
                      + $"&rkey={Uri.EscapeDataString(rkey)}";
            return await SendForJsonOrNullAsync(() => Get(url)).ConfigureAwait(false);
        }

        public async Task<JsonNode?> ListRecordsAsync(string collection, string? cursor = null, int limit = 50)
        {
            await EnsureFreshAsync().ConfigureAwait(false);
            var url = $"{_pdsUrl}/xrpc/com.atproto.repo.listRecords"
                      + $"?repo={Uri.EscapeDataString(_did!)}"
                      + $"&collection={Uri.EscapeDataString(collection)}"
                      + $"&limit={limit}";
            if (!string.IsNullOrEmpty(cursor)) url += $"&cursor={Uri.EscapeDataString(cursor!)}";
            return await SendForJsonOrNullAsync(() => Get(url)).ConfigureAwait(false);
        }

        public async Task DeleteRecordAsync(string collection, string rkey)
        {
            await EnsureFreshAsync().ConfigureAwait(false);
            await SendForJsonAsync(() => Post(
                "com.atproto.repo.deleteRecord",
                new { repo = _did, collection, rkey })).ConfigureAwait(false);
        }

        // ── token lifecycle ────────────────────────────────────────────────

        private async Task EnsureFreshAsync()
        {
            if (_accessJwt == null)
                throw new InvalidOperationException("AtprotoClient: not authenticated");
            if (_clock.UtcNow < _expiresAt) return;

            await _refreshLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Re-check inside the lock: a parallel caller may have refreshed.
                if (_clock.UtcNow < _expiresAt) return;
                await RefreshSessionAsync().ConfigureAwait(false);
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private async Task RefreshSessionAsync()
        {
            if (string.IsNullOrEmpty(_refreshJwt))
                throw new AtprotoTransientException("no refresh token available");

            JsonNode? body;
            try
            {
                // refreshSession authenticates with the refresh JWT, not the access JWT.
                body = await SendForJsonAsync(() => Post(
                    "com.atproto.server.refreshSession",
                    content: null,
                    bearer: _refreshJwt)).ConfigureAwait(false);
            }
            catch (AtprotoException ex)
            {
                // Refresh failure is terminal for the auth state: clear tokens and
                // surface Failed so the badge updates and we stop re-trying with a
                // dead session. The in-flight record is still preserved — the
                // transient throw makes the publisher queue it under the known DID.
                _accessJwt = null;
                _refreshJwt = null;
                _auth.Set(AuthStatus.Failed, error: ex.Message);
                if (ex is AtprotoTransientException) throw;
                throw new AtprotoTransientException(ex.Message, inner: ex);
            }

            ApplyTokens(body!);
            _log.Warn("refreshed atproto access token");
        }

        private void ApplyTokens(JsonNode body)
        {
            _accessJwt = body["accessJwt"]?.GetValue<string>()
                         ?? throw new AtprotoTransientException("session response missing accessJwt");
            _refreshJwt = body["refreshJwt"]?.GetValue<string>() ?? _refreshJwt;
            _expiresAt = _clock.UtcNow + AccessTokenTtl;
        }

        // ── HTTP plumbing (ns2.0-safe error mapping) ───────────────────────

        private HttpRequestMessage Post(string nsid, object? content, string? bearer = "")
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_pdsUrl}/xrpc/{nsid}");
            if (content != null) req.Content = JsonContent.Create(content);
            ApplyAuth(req, bearer);
            return req;
        }

        private HttpRequestMessage Get(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(req, bearer: "");
            return req;
        }

        // bearer: "" → use the default access token; null → unauthenticated; any
        // other value → use that token for this one request (refresh path).
        private void ApplyAuth(HttpRequestMessage req, string? bearer)
        {
            if (bearer == "")
            {
                if (_accessJwt != null)
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessJwt);
            }
            else if (bearer != null)
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
        }

        private async Task<JsonNode?> SendForJsonAsync(Func<HttpRequestMessage> make)
        {
            using (var res = await SendRawAsync(make).ConfigureAwait(false))
            {
                await ThrowIfFailedAsync(res).ConfigureAwait(false);
                return await ReadJsonAsync(res).ConfigureAwait(false);
            }
        }

        // GET helper for read endpoints where a 4xx (not found) is an expected
        // "no record" answer rather than a hard error.
        private async Task<JsonNode?> SendForJsonOrNullAsync(Func<HttpRequestMessage> make)
        {
            using (var res = await SendRawAsync(make).ConfigureAwait(false))
            {
                var code = (int)res.StatusCode;
                if (code >= 400 && code < 500) return null;
                await ThrowIfFailedAsync(res).ConfigureAwait(false);
                return await ReadJsonAsync(res).ConfigureAwait(false);
            }
        }

        private async Task<HttpResponseMessage> SendRawAsync(Func<HttpRequestMessage> make)
        {
            try
            {
                return await _http.SendAsync(make()).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // No status code on ns2.0's HttpRequestException — a thrown one means
                // the request never got a response (DNS, connection refused, TLS).
                throw new AtprotoTransientException("network failure reaching PDS", inner: ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new AtprotoTransientException("request to PDS timed out", inner: ex);
            }
        }

        private static async Task ThrowIfFailedAsync(HttpResponseMessage res)
        {
            if (res.IsSuccessStatusCode) return;
            var code = (int)res.StatusCode;
            string? errBody = null;
            try { errBody = await res.Content.ReadAsStringAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }

            if (code >= 400 && code < 500)
            {
                var (name, message) = ParseXrpcError(errBody);
                throw new AtprotoPermanentException(code, message ?? errBody, name);
            }
            throw new AtprotoTransientException($"PDS returned HTTP {code}", code);
        }

        // XRPC errors are { "error": "<Name>", "message": "<text>" }. Best-effort —
        // a non-JSON body just yields (null, null) and the raw body is surfaced.
        private static (string? name, string? message) ParseXrpcError(string? body)
        {
            if (string.IsNullOrEmpty(body)) return (null, null);
            try
            {
                var node = JsonNode.Parse(body!);
                return (node?["error"]?.GetValue<string>(), node?["message"]?.GetValue<string>());
            }
            catch
            {
                return (null, null);
            }
        }

        private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage res)
        {
            try
            {
                return await res.Content.ReadFromJsonAsync<JsonNode>().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new AtprotoTransientException("could not parse PDS response", inner: ex);
            }
        }
    }
}
