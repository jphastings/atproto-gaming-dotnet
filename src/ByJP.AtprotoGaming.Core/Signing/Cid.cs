using System;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ByJP.AtprotoGaming.Core.Signing
{
    /// <summary>
    /// CIDv1 over DAG-CBOR with a SHA-256 multihash (codec 0x71) — the form
    /// atproto uses for record blocks.
    /// </summary>
    internal static class Cid
    {
        /// <summary>36-byte binary CID: <c>01 71 12 20 &lt;sha256 digest&gt;</c>.</summary>
        public static byte[] FromDagCborBytes(byte[] dagCbor)
        {
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(dagCbor);

            var cid = new byte[36];
            cid[0] = 0x01; // CIDv1
            cid[1] = 0x71; // dag-cbor
            cid[2] = 0x12; // multihash: sha2-256
            cid[3] = 0x20; // length: 32
            Buffer.BlockCopy(digest, 0, cid, 4, 32);
            return cid;
        }

        /// <summary>Multibase string form: <c>b</c> + lower base32 of the binary CID (prefix <c>bafyrei…</c>).</summary>
        public static string ToStringForm(byte[] binaryCid) => "b" + Base32Lower.Encode(binaryCid);

        /// <summary>Computes the CID of a record body as the PDS would store it (DAG-CBOR of the value).</summary>
        public static string ForRecord(JsonNode recordBody)
        {
            var cbor = DagCbor.Encode(recordBody);
            return ToStringForm(FromDagCborBytes(cbor));
        }
    }
}
