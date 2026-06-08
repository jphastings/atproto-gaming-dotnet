using System.Text.Json.Nodes;
using ByJP.AtprotoGaming.Core.Signing;

namespace ByJP.AtprotoGaming.Core.Internal
{
    /// <summary>Turns a record body into the exact bytes to PUT: clone, drop stale signatures, inject versions, sign.</summary>
    internal static class RecordPrep
    {
        public static JsonObject Prepare(JsonObject payload, string? repoDid, VersionsInjector versions, RecordSigner? signer)
        {
            var prepared = (JsonObject)payload.DeepClone();
            prepared.Remove("signatures"); // re-signed fresh; never accumulate
            versions.InjectInto(prepared);
            if (signer != null && !string.IsNullOrEmpty(repoDid))
                signer.Sign(prepared, repoDid!);
            return prepared;
        }
    }
}
