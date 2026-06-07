using System.IO;
using System.Text;

namespace ByJP.AtprotoGaming.Core.Internal
{
    /// <summary>
    /// Crash-resilient writes shared by the config store and the outbox: write to
    /// a sibling <c>.tmp</c>, then replace. A game crash mid-write leaves either
    /// the old file or the temp — never a half-written target (requirements NF5).
    /// </summary>
    internal static class AtomicFile
    {
        // UTF-8 without a BOM: BOMs would corrupt the byte-for-byte payloads the
        // outbox replays and trip strict JSON parsers.
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void WriteAllText(string path, string contents)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, contents, Utf8NoBom);
            // File.Move has no overwrite overload on ns2.0, so clear the target
            // first. The window is tiny and a crash inside it still leaves .tmp.
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
