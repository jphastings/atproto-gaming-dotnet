using System;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>Lightweight AT-URI validation.</summary>
    public static class AtUri
    {
        /// <summary>
        /// True for a plausible AT URI: <c>at://&lt;authority&gt;[/collection[/rkey]]</c>
        /// where the authority is a DID (<c>did:…</c>) or a dotted handle. Not a full
        /// grammar check, but rejects the common mistakes (wrong scheme, empty authority).
        /// </summary>
        public static bool IsValid(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            if (!uri.StartsWith("at://", StringComparison.Ordinal)) return false;

            var rest = uri.Substring("at://".Length);
            var slash = rest.IndexOf('/');
            var authority = slash < 0 ? rest : rest.Substring(0, slash);
            if (authority.Length == 0) return false;

            // DID authority, or a handle (must contain a dot).
            return authority.StartsWith("did:", StringComparison.Ordinal) || authority.Contains(".");
        }
    }
}
