using System;
using System.Numerics;

namespace ByJP.AtprotoGaming.Core.Signing
{
    /// <summary>
    /// Unsigned big-endian ↔ <see cref="BigInteger"/> conversions. .NET Core 2.1+
    /// has these as ctor/ToByteArray overloads, but netstandard2.0's BigInteger
    /// only exposes little-endian two's-complement, so we bridge here.
    /// </summary>
    internal static class BigIntBytes
    {
        public static BigInteger FromUnsignedBigEndian(byte[] bigEndian)
        {
            // Reverse to little-endian and append a 0x00 so the high bit is never
            // read as a sign bit — guarantees a non-negative result.
            var le = new byte[bigEndian.Length + 1];
            for (int i = 0; i < bigEndian.Length; i++)
                le[i] = bigEndian[bigEndian.Length - 1 - i];
            le[bigEndian.Length] = 0;
            return new BigInteger(le);
        }

        /// <summary>Big-endian bytes, left-padded/fitted to exactly <paramref name="length"/>. Assumes a non-negative value that fits.</summary>
        public static byte[] ToUnsignedBigEndian(BigInteger value, int length)
        {
            if (value.Sign < 0) throw new ArgumentOutOfRangeException(nameof(value), "must be non-negative");
            var le = value.ToByteArray(); // little-endian, possibly a trailing 0x00 sign byte
            var result = new byte[length];
            for (int i = 0; i < le.Length && i < length; i++)
                result[length - 1 - i] = le[i];
            return result;
        }

        /// <summary>Minimal-length big-endian bytes (no leading zeros), empty for zero.</summary>
        public static byte[] ToUnsignedBigEndianMinimal(BigInteger value)
        {
            if (value.Sign < 0) throw new ArgumentOutOfRangeException(nameof(value), "must be non-negative");
            if (value.IsZero) return Array.Empty<byte>();
            var le = value.ToByteArray();
            int len = le.Length;
            while (len > 0 && le[len - 1] == 0) len--; // drop sign/zero padding
            var result = new byte[len];
            for (int i = 0; i < len; i++)
                result[len - 1 - i] = le[i];
            return result;
        }
    }
}
