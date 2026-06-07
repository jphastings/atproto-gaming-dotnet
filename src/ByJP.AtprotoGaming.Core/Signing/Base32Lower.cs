using System;
using System.Text;

namespace ByJP.AtprotoGaming.Core.Signing
{
    /// <summary>Lowercase RFC 4648 base32, no padding (the multibase 'b' alphabet).</summary>
    internal static class Base32Lower
    {
        private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

        public static string Encode(byte[] input)
        {
            var sb = new StringBuilder((input.Length * 8 + 4) / 5);
            int buffer = 0, bits = 0;
            foreach (var b in input)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    sb.Append(Alphabet[(buffer >> bits) & 0x1f]);
                }
            }
            if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 0x1f]);
            return sb.ToString();
        }
    }
}
