using System;
using System.Text.Json.Nodes;
using ByJP.AtprotoGaming.Core.Signing;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// Helpers for <c>com.atproto.repo.strongRef</c> values (the lexicon's
    /// <c>forkedFrom</c> field). A strongRef is <c>{ uri, cid }</c>.
    /// </summary>
    public static class StrongRef
    {
        /// <summary>Builds a strongRef from a known AT-URI and CID.</summary>
        public static JsonObject Create(string uri, string cid)
        {
            if (string.IsNullOrEmpty(uri)) throw new ArgumentNullException(nameof(uri));
            if (string.IsNullOrEmpty(cid)) throw new ArgumentNullException(nameof(cid));
            return new JsonObject { ["uri"] = uri, ["cid"] = cid };
        }

        /// <summary>
        /// Computes the CID of a record body (DAG-CBOR + SHA-256, CIDv1 dag-cbor)
        /// for when you have the parent record's body but not a fetched CID.
        /// Prefer the CID returned by <c>getRecord</c>/<c>createRecord</c> when you
        /// have it; this is the fallback.
        /// </summary>
        public static string ComputeCid(JsonNode recordBody)
        {
            if (recordBody == null) throw new ArgumentNullException(nameof(recordBody));
            return Cid.ForRecord(recordBody);
        }

        /// <summary>Builds a strongRef from a URI and the record's body, computing the CID.</summary>
        public static JsonObject FromRecordBody(string uri, JsonNode recordBody) =>
            Create(uri, ComputeCid(recordBody));
    }
}
