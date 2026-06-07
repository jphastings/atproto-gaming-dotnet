using System;

namespace ByJP.AtprotoGaming.Core.Signing
{
    internal enum DidKeyType
    {
        P256Public,
        P256Private,
    }

    internal sealed class DidKey
    {
        public DidKeyType Type { get; }

        /// <summary>For a private key, the 32-byte scalar; for a public key, the 33-byte compressed point.</summary>
        public byte[] RawBytes { get; }

        private DidKey(DidKeyType type, byte[] rawBytes)
        {
            Type = type;
            RawBytes = rawBytes;
        }

        // Multicodec varint prefixes: P-256 0x1200 public / 0x1306 private,
        // each a 2-byte unsigned varint.
        private static readonly byte[] P256PublicPrefix = { 0x80, 0x24 };
        private static readonly byte[] P256PrivatePrefix = { 0x86, 0x26 };

        public static DidKey Parse(string didKey)
        {
            const string scheme = "did:key:";
            if (!didKey.StartsWith(scheme, StringComparison.Ordinal))
                throw new FormatException("expected did:key: prefix");
            var body = didKey.Substring(scheme.Length);
            if (body.Length == 0 || body[0] != 'z')
                throw new FormatException("only multibase base58btc ('z' prefix) supported");

            var decoded = Base58Btc.Decode(body.Substring(1));
            if (decoded.Length < 3)
                throw new FormatException("decoded did:key too short");

            if (decoded[0] == P256PrivatePrefix[0] && decoded[1] == P256PrivatePrefix[1])
            {
                if (decoded.Length != 2 + 32)
                    throw new FormatException($"P-256 private key must be 32 bytes, got {decoded.Length - 2}");
                return new DidKey(DidKeyType.P256Private, Slice(decoded, 2));
            }
            if (decoded[0] == P256PublicPrefix[0] && decoded[1] == P256PublicPrefix[1])
            {
                if (decoded.Length != 2 + 33)
                    throw new FormatException($"P-256 compressed public key must be 33 bytes, got {decoded.Length - 2}");
                return new DidKey(DidKeyType.P256Public, Slice(decoded, 2));
            }
            throw new FormatException(
                $"unsupported multicodec prefix 0x{decoded[0]:X2}{decoded[1]:X2} (only P-256 supported)");
        }

        /// <summary>Derives the P-256 public <c>did:key</c> (compressed point) from this private key.</summary>
        public string DerivePublicDidKey()
        {
            if (Type != DidKeyType.P256Private)
                throw new InvalidOperationException("only P-256 private keys can derive a public did:key");

            var (x, y) = P256.MultiplyG(RawBytes);

            var compressed = new byte[33];
            compressed[0] = (byte)(0x02 | (y[31] & 0x01)); // 0x02/0x03 by Y parity
            Buffer.BlockCopy(x, 0, compressed, 1, 32);

            var withPrefix = new byte[2 + 33];
            withPrefix[0] = P256PublicPrefix[0];
            withPrefix[1] = P256PublicPrefix[1];
            Buffer.BlockCopy(compressed, 0, withPrefix, 2, 33);

            return "did:key:z" + Base58Btc.Encode(withPrefix);
        }

        private static byte[] Slice(byte[] src, int start)
        {
            var dst = new byte[src.Length - start];
            Buffer.BlockCopy(src, start, dst, 0, dst.Length);
            return dst;
        }
    }
}
