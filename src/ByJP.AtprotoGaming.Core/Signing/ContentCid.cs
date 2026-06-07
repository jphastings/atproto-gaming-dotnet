using System;
using System.Text.Json.Nodes;

namespace ByJP.AtprotoGaming.Core.Signing
{
    /// <summary>
    /// The CID-first attestation content CID per the badge.blue spec: strip
    /// <c>signatures</c> from the record, build a transient <c>$sig</c> from the
    /// attestation metadata (with <c>repository</c> injected and
    /// <c>cid</c>/<c>signature</c> removed), DAG-CBOR encode, then wrap as a CIDv1.
    /// </summary>
    internal static class ContentCid
    {
        public static byte[] ComputeBinary(JsonNode record, JsonNode metadata, string repositoryDid)
        {
            if (!(record is JsonObject recObj))
                throw new ArgumentException("record must be a JSON object", nameof(record));
            if (!(metadata is JsonObject metaObj))
                throw new ArgumentException("metadata must be a JSON object", nameof(metadata));
            if (!(recObj["$type"] is JsonValue) || string.IsNullOrEmpty(recObj["$type"]!.GetValue<string>()))
                throw new ArgumentException("record must have a non-empty $type");
            if (!(metaObj["$type"] is JsonValue) || string.IsNullOrEmpty(metaObj["$type"]!.GetValue<string>()))
                throw new ArgumentException("metadata must have a non-empty $type");

            // Strip signatures from a deep clone of the record.
            var stripped = (JsonObject)recObj.DeepClone();
            stripped.Remove("signatures");

            // Prepare $sig metadata — clone, strip cid/signature, inject repository.
            var sig = (JsonObject)metaObj.DeepClone();
            sig.Remove("cid");
            sig.Remove("signature");
            sig["repository"] = repositoryDid;

            stripped["$sig"] = sig;

            return Cid.FromDagCborBytes(DagCbor.Encode(stripped));
        }

        public static string ComputeString(JsonNode record, JsonNode metadata, string repositoryDid) =>
            Cid.ToStringForm(ComputeBinary(record, metadata, repositoryDid));
    }
}
