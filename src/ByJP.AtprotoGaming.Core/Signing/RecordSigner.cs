using System.Text.Json.Nodes;

namespace ByJP.AtprotoGaming.Core.Signing
{
    /// <summary>Appends a CID-first inline attestation to a record, bound to the repo DID.</summary>
    internal sealed class RecordSigner
    {
        private readonly SigningKey _key;

        public RecordSigner(SigningKey key)
        {
            _key = key;
        }

        public void Sign(JsonObject record, string repoDid)
        {
            var metadata = new JsonObject { ["$type"] = _key.AttestationType };
            var attestation = InlineAttestation.CreateInline(
                record, metadata, repoDid, _key.Private, _key.PublicDidKey);
            InlineAttestation.Append(record, attestation);
        }
    }
}
