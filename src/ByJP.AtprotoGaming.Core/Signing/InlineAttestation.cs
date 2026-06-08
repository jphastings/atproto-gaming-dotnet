using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ByJP.AtprotoGaming.Core.Signing
{
    internal static class InlineAttestation
    {
        // P-256 curve order n and n/2 for low-S normalization. Parse with a "0"
        // prefix so the leading F isn't read as a sign bit.
        private static readonly BigInteger N = BigInteger.Parse(
            "0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551",
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        private static readonly BigInteger HalfN = N >> 1;

        /// <summary>
        /// Computes the content CID for (record, metadata, repositoryDid), signs
        /// the 36-byte binary CID with the P-256 private key (SHA-256 ECDSA, IEEE
        /// P1363 r‖s, low-S normalized) and returns the attestation object to
        /// append to the record's <c>signatures</c> array.
        /// </summary>
        public static JsonObject CreateInline(
            JsonObject record,
            JsonObject metadata,
            string repositoryDid,
            DidKey privateKey,
            string publicDidKey)
        {
            if (privateKey.Type != DidKeyType.P256Private)
                throw new ArgumentException("only P-256 private keys are supported", nameof(privateKey));

            // `key` participates in the content CID — add it before computing the
            // CID so the signed payload matches the reference verifier.
            var metadataForCid = (JsonObject)metadata.DeepClone();
            metadataForCid["key"] = publicDidKey;

            var cidBin = ContentCid.ComputeBinary(record, metadataForCid, repositoryDid);
            var cidStr = Cid.ToStringForm(cidBin);

            byte[] sig;
            using (var ecdsa = EcdsaP256.CreateSigner(privateKey))
            {
                // The base SignData overload returns the IEEE P1363 r‖s
                // concatenation on netstandard2.0 — 64 bytes for P-256.
                sig = ecdsa.SignData(cidBin, HashAlgorithmName.SHA256);
            }
            if (sig.Length != 64)
                throw new CryptographicException($"expected 64-byte P-256 signature, got {sig.Length}");

            NormalizeLowS(sig);

            var attestation = metadataForCid;
            attestation["cid"] = cidStr;
            attestation["signature"] = new JsonObject { ["$bytes"] = Convert.ToBase64String(sig) };
            return attestation;
        }

        /// <summary>Attaches the attestation to <c>record.signatures</c>, creating the array if needed.</summary>
        public static void Append(JsonObject record, JsonObject attestation)
        {
            if (!(record["signatures"] is JsonArray arr))
            {
                arr = new JsonArray();
                record["signatures"] = arr;
            }
            arr.Add(attestation);
        }

        private static void NormalizeLowS(byte[] sig)
        {
            var sBytes = new byte[32];
            Buffer.BlockCopy(sig, 32, sBytes, 0, 32);
            var s = BigIntBytes.FromUnsignedBigEndian(sBytes);
            if (s <= HalfN) return;

            var low = BigIntBytes.ToUnsignedBigEndian(N - s, 32);
            Buffer.BlockCopy(low, 0, sig, 32, 32);
        }
    }
}
