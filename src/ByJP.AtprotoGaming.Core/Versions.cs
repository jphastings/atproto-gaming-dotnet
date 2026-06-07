using System;
using System.Reflection;
using System.Text.Json.Nodes;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>A named piece of software and its version (lexicon <c>#softwareVersion</c>).</summary>
    public sealed record SoftwareVersion(string Name, string Version);

    /// <summary>
    /// Appends this package's own entry to a record's <c>versions.additional</c>
    /// array at write time, so consumers don't have to (requirements F11). The
    /// consumer supplies <c>versions.game</c> and any of their own additional
    /// entries; this fills in the core's line.
    /// </summary>
    public sealed class VersionsInjector
    {
        /// <summary>The name stamped into <c>versions.additional</c>.</summary>
        public const string PackageName = "atproto-gaming-dotnet";

        private readonly SoftwareVersion _self;

        public VersionsInjector(string? versionOverride = null)
        {
            _self = new SoftwareVersion(PackageName, versionOverride ?? ResolveOwnVersion());
        }

        /// <summary>
        /// If <paramref name="record"/> carries a <c>versions</c> object, ensures
        /// its <c>additional</c> array contains exactly one truthful entry for this
        /// package. Records without a <c>versions</c> field (stats, lobbies, …) are
        /// left untouched, keeping the publisher collection-agnostic.
        /// </summary>
        public void InjectInto(JsonObject record)
        {
            if (!(record["versions"] is JsonObject versions)) return;

            if (!(versions["additional"] is JsonArray additional))
            {
                additional = new JsonArray();
                versions["additional"] = additional;
            }

            // Drop any pre-existing entry for us, then add ours, so a re-serialized
            // record never accumulates duplicates or stale versions.
            for (int i = additional.Count - 1; i >= 0; i--)
            {
                if (additional[i] is JsonObject e && e["name"]?.GetValue<string>() == PackageName)
                    additional.RemoveAt(i);
            }
            additional.Add(new JsonObject { ["name"] = _self.Name, ["version"] = _self.Version });
        }

        private static string ResolveOwnVersion()
        {
            var info = typeof(VersionsInjector).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(info)) return "0.0.0";
            // Strip SourceLink build metadata (e.g. "0.1.0+abc123").
            var plus = info!.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }
    }
}
