using System.Text.Json.Serialization;

namespace ByJP.AtprotoGaming.Core
{
    /// <summary>
    /// The fields the package itself reads and writes. A consumer subclasses this
    /// with its own settings and hands the subclass to
    /// <see cref="ConfigStore{T}"/>.
    /// </summary>
    public class CoreConfig
    {
        [JsonPropertyName("handle")] public string Handle { get; set; } = "";
        [JsonPropertyName("appPassword")] public string AppPassword { get; set; } = "";

        // Stamped after a successful login so an offline boot still knows which
        // DID to bucket queued records under. Invalidated implicitly when the
        // user edits Handle to something different (we re-resolve when online).
        [JsonPropertyName("cachedHandle")] public string CachedHandle { get; set; } = "";
        [JsonPropertyName("cachedDid")] public string CachedDid { get; set; } = "";
        [JsonPropertyName("cachedPds")] public string CachedPds { get; set; } = "";

        // The player's rolling-stats record's rkey, cached after first publish so
        // later runs update the same record.
        [JsonPropertyName("statsRkey")] public string StatsRkey { get; set; } = "";
    }

    /// <summary>
    /// Non-generic view of a <see cref="ConfigStore{T}"/> so package internals can
    /// read/write the <see cref="CoreConfig"/> fields and persist without knowing
    /// the consumer's concrete config type.
    /// </summary>
    public interface IConfigStore
    {
        /// <summary>The live config object, viewed as its <see cref="CoreConfig"/> base.</summary>
        CoreConfig Core { get; }

        /// <summary>Atomically persists the current config to disk.</summary>
        void Save();
    }
}
