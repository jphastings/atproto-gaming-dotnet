using System;
using System.IO;
using System.Reflection;

namespace ByJP.AtprotoGaming.Core.Adapters
{
    /// <summary>
    /// Ready-made <see cref="IFileSystem"/> implementations. The common case for
    /// a mod is <see cref="NextTo(Assembly)"/> — config and outbox live beside
    /// the plugin DLL, which is writable and survives game updates per-mod.
    /// </summary>
    public sealed class FileSystem : IFileSystem
    {
        public string ConfigDirectory { get; }
        public string OutboxRoot { get; }

        public FileSystem(string configDirectory, string outboxRoot)
        {
            ConfigDirectory = configDirectory ?? throw new ArgumentNullException(nameof(configDirectory));
            OutboxRoot = outboxRoot ?? throw new ArgumentNullException(nameof(outboxRoot));
        }

        /// <summary>Both artefacts under a single directory (outbox in an <c>outbox/</c> subfolder).</summary>
        public static FileSystem At(string directory) =>
            new FileSystem(directory, Path.Combine(directory, "outbox"));

        /// <summary>Beside the given assembly's DLL — the usual mod layout.</summary>
        public static FileSystem NextTo(Assembly assembly)
        {
            var dir = Path.GetDirectoryName(assembly.Location);
            if (string.IsNullOrEmpty(dir))
                throw new InvalidOperationException(
                    "assembly has no on-disk location; pass an explicit directory to FileSystem.At");
            return At(dir!);
        }

        /// <summary>Beside the DLL that defines <typeparamref name="T"/>.</summary>
        public static FileSystem NextTo<T>() => NextTo(typeof(T).Assembly);
    }
}
