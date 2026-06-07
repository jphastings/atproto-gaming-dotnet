using System.Security.Cryptography;

namespace ByJP.AtprotoGaming.Core.Signing
{
    /// <summary>
    /// Builds P-256 <see cref="ECDsa"/> instances via <c>ImportParameters</c> —
    /// the only key-import path available across both netstandard2.0 runtimes
    /// (.NET Framework's CNG needs the public point supplied, which we derive with
    /// <see cref="P256"/>).
    /// </summary>
    internal static class EcdsaP256
    {
        public static ECDsa CreateSigner(byte[] privateScalar)
        {
            var (x, y) = P256.MultiplyG(privateScalar);
            var ecdsa = ECDsa.Create();
            ecdsa.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = privateScalar,
                Q = new ECPoint { X = x, Y = y },
            });
            return ecdsa;
        }

        public static ECDsa CreateVerifier(byte[] x, byte[] y)
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y },
            });
            return ecdsa;
        }
    }
}
