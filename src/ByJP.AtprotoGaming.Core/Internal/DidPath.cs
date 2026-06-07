using System.Text;

namespace ByJP.AtprotoGaming.Core.Internal
{
    internal static class DidPath
    {
        // DIDs contain ':' which Windows forbids in filenames. Percent-encode the
        // handful of problematic characters; the result round-trips and stays
        // readable (did:plc:abc → did%3Aplc%3Aabc).
        public static string EncodeDid(string did)
        {
            var sb = new StringBuilder(did.Length + 8);
            foreach (var c in did)
            {
                if (c == ':' || c == '/' || c == '\\' || c == '?' || c == '*'
                    || c == '"' || c == '<' || c == '>' || c == '|')
                    sb.Append('%').Append(((int)c).ToString("X2"));
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
