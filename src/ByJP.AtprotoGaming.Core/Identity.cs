using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>The resolved identity triple for a handle or DID.</summary>
    public sealed record MiniDoc(string Did, string Handle, string Pds);

    /// <summary>
    /// Resolves an atproto handle (or DID) to a <see cref="MiniDoc"/> via
    /// Slingshot's <c>blue.microcosm.identity.resolveMiniDoc</c>.
    /// </summary>
    public sealed class IdentityResolver
    {
        private const string Endpoint =
            "https://slingshot.microcosm.blue/xrpc/blue.microcosm.identity.resolveMiniDoc";

        private readonly HttpClient _http;

        public IdentityResolver(HttpClient? http = null)
        {
            _http = http ?? new HttpClient();
        }

        /// <exception cref="AtprotoTransientException">Slingshot unreachable or malformed (caller may fall back to a cached DID).</exception>
        public async Task<MiniDoc> ResolveAsync(string identifier)
        {
            var url = $"{Endpoint}?identifier={Uri.EscapeDataString(identifier)}";
            JsonNode? body;
            try
            {
                var res = await _http.GetAsync(url).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                    throw new AtprotoTransientException(
                        $"Slingshot resolve failed: HTTP {(int)res.StatusCode} for {identifier}",
                        (int)res.StatusCode);
                body = await res.Content.ReadFromJsonAsync<JsonNode>().ConfigureAwait(false);
            }
            catch (AtprotoException) { throw; }
            catch (Exception ex)
            {
                throw new AtprotoTransientException($"Slingshot resolve failed for {identifier}", inner: ex);
            }

            var did = body?["did"]?.GetValue<string>();
            var handle = body?["handle"]?.GetValue<string>();
            var pds = body?["pds"]?.GetValue<string>();
            if (did == null || handle == null || pds == null)
                throw new AtprotoTransientException($"Slingshot response missing fields: {body}");
            return new MiniDoc(did, handle, pds);
        }
    }
}
