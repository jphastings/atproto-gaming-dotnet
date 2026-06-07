using System;
using ByJP.AtprotoGaming.Core.Signing;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// An opt-in P-256 signing key for CID-first inline attestations
    /// (badge.blue convention). The consumer supplies the private key as a
    /// <c>did:key</c> and chooses the attestation <c>$type</c>. When present on the
    /// publisher, every record gains a <c>signatures</c> entry; when absent,
    /// records publish unsigned.
    /// </summary>
    public sealed class SigningKey
    {
        internal DidKey Private { get; }

        /// <summary>The derived public <c>did:key</c> — publish this so others can verify.</summary>
        public string PublicDidKey { get; }

        /// <summary>The attestation metadata <c>$type</c> (e.g. <c>your.game.mod.record#attestation</c>).</summary>
        public string AttestationType { get; }

        private SigningKey(DidKey priv, string publicDidKey, string attestationType)
        {
            Private = priv;
            PublicDidKey = publicDidKey;
            AttestationType = attestationType;
        }

        /// <summary>
        /// Loads a signing key from a P-256 private <c>did:key</c> and the
        /// attestation <c>$type</c> to stamp on each signature.
        /// </summary>
        /// <exception cref="FormatException">The did:key isn't a supported P-256 private key.</exception>
        public static SigningKey FromDidKey(string privateDidKey, string attestationType)
        {
            if (string.IsNullOrEmpty(privateDidKey)) throw new ArgumentNullException(nameof(privateDidKey));
            if (string.IsNullOrEmpty(attestationType)) throw new ArgumentNullException(nameof(attestationType));

            var priv = DidKey.Parse(privateDidKey);
            if (priv.Type != DidKeyType.P256Private)
                throw new FormatException("signing key must be a P-256 private did:key");
            return new SigningKey(priv, priv.DerivePublicDidKey(), attestationType);
        }
    }
}
