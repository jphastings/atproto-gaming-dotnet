using System;
using System.Collections.Generic;
using System.Numerics;

namespace ByJP.AtprotoGaming.Core.Signing
{
    internal static class Base58Btc
    {
        private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

        private static readonly int[] Indices = BuildIndices();

        private static int[] BuildIndices()
        {
            var table = new int[128];
            for (var i = 0; i < table.Length; i++) table[i] = -1;
            for (var i = 0; i < Alphabet.Length; i++) table[Alphabet[i]] = i;
            return table;
        }

        public static string Encode(byte[] input)
        {
            var leadingZeros = 0;
            while (leadingZeros < input.Length && input[leadingZeros] == 0) leadingZeros++;

            var num = BigIntBytes.FromUnsignedBigEndian(input);

            var digits = new List<char>(input.Length * 2);
            while (num > 0)
            {
                num = BigInteger.DivRem(num, 58, out var rem);
                digits.Add(Alphabet[(int)rem]);
            }
            for (var i = 0; i < leadingZeros; i++) digits.Add('1');
            digits.Reverse();
            return new string(digits.ToArray());
        }

        public static byte[] Decode(string input)
        {
            var leadingZeros = 0;
            while (leadingZeros < input.Length && input[leadingZeros] == '1') leadingZeros++;

            var num = BigInteger.Zero;
            for (var i = leadingZeros; i < input.Length; i++)
            {
                var c = input[i];
                if (c >= Indices.Length || Indices[c] < 0)
                    throw new FormatException($"invalid base58btc character '{c}'");
                num = num * 58 + Indices[c];
            }

            var be = BigIntBytes.ToUnsignedBigEndianMinimal(num);
            var result = new byte[leadingZeros + be.Length];
            Buffer.BlockCopy(be, 0, result, leadingZeros, be.Length);
            return result;
        }
    }
}
