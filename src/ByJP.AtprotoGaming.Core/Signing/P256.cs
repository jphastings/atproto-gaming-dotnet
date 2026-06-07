using System;
using System.Globalization;
using System.Numerics;

namespace ByJP.AtprotoGaming.Core.Signing
{
    /// <summary>
    /// Just enough P-256 (secp256r1 / prime256v1) curve math to derive the public
    /// point from a private scalar. netstandard2.0 has no <c>ImportECPrivateKey</c>
    /// and <c>ImportParameters</c> on .NET Framework needs the public point, so we
    /// compute Q = d·G ourselves with plain <see cref="BigInteger"/> arithmetic.
    /// </summary>
    internal static class P256
    {
        private static readonly BigInteger P = Hex(
            "FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");
        private static readonly BigInteger A = Hex(
            "FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFC"); // p - 3
        private static readonly BigInteger Gx = Hex(
            "6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296");
        private static readonly BigInteger Gy = Hex(
            "4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5");

        /// <summary>Returns the public point (X, Y) as 32-byte big-endian coordinates for d·G.</summary>
        public static (byte[] X, byte[] Y) MultiplyG(byte[] scalarBigEndian)
        {
            var d = BigIntBytes.FromUnsignedBigEndian(scalarBigEndian);
            var q = Multiply(d, new Point(Gx, Gy));
            if (q.IsInfinity)
                throw new InvalidOperationException("scalar multiplication produced the point at infinity");
            return (BigIntBytes.ToUnsignedBigEndian(q.X, 32), BigIntBytes.ToUnsignedBigEndian(q.Y, 32));
        }

        private static BigInteger Hex(string h) =>
            BigInteger.Parse("0" + h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        private static BigInteger Mod(BigInteger v) { var m = v % P; return m.Sign < 0 ? m + P : m; }

        private static BigInteger Inverse(BigInteger v) => BigInteger.ModPow(Mod(v), P - 2, P);

        private readonly struct Point
        {
            public readonly BigInteger X;
            public readonly BigInteger Y;
            public readonly bool IsInfinity;

            public Point(BigInteger x, BigInteger y) { X = x; Y = y; IsInfinity = false; }
            private Point(bool inf) { X = BigInteger.Zero; Y = BigInteger.Zero; IsInfinity = inf; }
            public static readonly Point Infinity = new Point(true);
        }

        private static Point Multiply(BigInteger k, Point p)
        {
            var result = Point.Infinity;
            var addend = p;
            while (k > 0)
            {
                if (!(k & BigInteger.One).IsZero) result = Add(result, addend);
                addend = Double(addend);
                k >>= 1;
            }
            return result;
        }

        private static Point Add(Point p, Point q)
        {
            if (p.IsInfinity) return q;
            if (q.IsInfinity) return p;

            if (p.X == q.X)
            {
                // P + (-P) = infinity; P + P = double.
                if (Mod(p.Y + q.Y).IsZero) return Point.Infinity;
                return Double(p);
            }

            var s = Mod((q.Y - p.Y) * Inverse(q.X - p.X));
            var x3 = Mod(s * s - p.X - q.X);
            var y3 = Mod(s * (p.X - x3) - p.Y);
            return new Point(x3, y3);
        }

        private static Point Double(Point p)
        {
            if (p.IsInfinity || p.Y.IsZero) return Point.Infinity;
            var s = Mod((3 * p.X * p.X + A) * Inverse(2 * p.Y));
            var x3 = Mod(s * s - 2 * p.X);
            var y3 = Mod(s * (p.X - x3) - p.Y);
            return new Point(x3, y3);
        }
    }
}
