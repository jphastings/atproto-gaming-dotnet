using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// Deterministic atproto TID (record key) derivation. The same
    /// <c>(unixSeconds, salt)</c> tuple yields the same 13-char rkey on every run
    /// and on every multiplayer participant — so updates target one record and
    /// participants writing to their own PDSs converge on the same key.
    /// </summary>
    /// <remarks>
    /// The tuple is the contract: derive <c>salt</c> from the game's run seed
    /// where one exists, or another stable per-play id where it doesn't, and make
    /// sure every participant passes the identical salt string.
    /// </remarks>
    public static class Tid
    {
        // base32-sortable alphabet (atproto TID encoding).
        private const string Alphabet = "234567abcdefghijklmnopqrstuvwxyz";

        // Characters whose 5-bit value is &lt; 16, i.e. the top bit of the high
        // group is clear — the only valid first character of a TID.
        private const string HighGroupAlphabet = "234567abcdefghij";

        /// <summary>
        /// Builds a TID from a play-through's start time and a stable salt.
        /// Layout (64 bits): bit 0 reserved 0, bits 1–53 microseconds since epoch,
        /// bits 54–63 ten bits derived from the salt.
        /// </summary>
        public static string FromPlayThrough(long unixSeconds, string salt)
        {
            if (salt == null) throw new ArgumentNullException(nameof(salt));

            byte[] hash;
            using (var md5 = MD5.Create())
                hash = md5.ComputeHash(Encoding.UTF8.GetBytes(salt));

            // Top 10 bits of the digest.
            int rand = (hash[0] << 2) | (hash[1] >> 6);

            long microseconds = unixSeconds * 1_000_000L;
            long tidValue = (microseconds << 10) | (uint)rand;

            var buf = new char[13];
            for (int i = 12; i >= 0; i--)
            {
                buf[i] = Alphabet[(int)(tidValue & 0x1F)];
                tidValue >>= 5;
            }
            return new string(buf);
        }

        /// <summary>Convenience overload for numeric run seeds (stringified invariantly).</summary>
        public static string FromPlayThrough(long unixSeconds, ulong seed) =>
            FromPlayThrough(unixSeconds, seed.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// Extracts the rkey from a parent play record's AT-URI, for the
        /// save-fork case where a fork inherits its parent's rkey lineage.
        /// </summary>
        /// <exception cref="FormatException">The URI's trailing segment isn't a valid TID.</exception>
        public static string FromAtUri(string atUri)
        {
            if (string.IsNullOrEmpty(atUri)) throw new ArgumentNullException(nameof(atUri));
            var rkey = atUri.Substring(atUri.LastIndexOf('/') + 1);
            if (!IsValid(rkey))
                throw new FormatException($"AT-URI does not end in a valid TID rkey: {atUri}");
            return rkey;
        }

        /// <summary>True if <paramref name="value"/> is a syntactically valid atproto TID.</summary>
        public static bool IsValid(string value)
        {
            if (value == null || value.Length != 13) return false;
            if (HighGroupAlphabet.IndexOf(value[0]) < 0) return false;
            for (int i = 1; i < value.Length; i++)
                if (Alphabet.IndexOf(value[i]) < 0) return false;
            return true;
        }
    }
}
