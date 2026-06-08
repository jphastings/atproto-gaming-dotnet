using System;
using System.Security.Cryptography;
using System.Text;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// atproto record-key (rkey) rules and a deterministic sanitiser for turning
    /// an arbitrary id into a valid one.
    /// </summary>
    public static class RecordKey
    {
        /// <summary>
        /// True if <paramref name="key"/> is a valid atproto record key: 1–512 of
        /// <c>A–Z a–z 0–9 . - _ : ~</c>, and not <c>.</c> or <c>..</c>.
        /// </summary>
        public static bool IsValid(string key)
        {
            if (key == null || key.Length < 1 || key.Length > 512) return false;
            if (key == "." || key == "..") return false;
            foreach (var c in key)
                if (!IsAllowed(c)) return false;
            return true;
        }

        /// <summary>
        /// Returns <paramref name="key"/> unchanged if it's already a valid record
        /// key; otherwise a deterministic stand-in: the URL-safe base64 of its
        /// SHA-256 (43 chars, always valid).
        /// </summary>
        public static string Sanitize(string key)
        {
            if (IsValid(key)) return key;

            byte[] hash;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? ""));

            return Convert.ToBase64String(hash)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static bool IsAllowed(char c) =>
            (c >= 'A' && c <= 'Z') ||
            (c >= 'a' && c <= 'z') ||
            (c >= '0' && c <= '9') ||
            c == '.' || c == '-' || c == '_' || c == ':' || c == '~';
    }
}
