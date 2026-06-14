using System.Collections.Generic;
using System.Text.Json.Serialization;
using ByJP.AtprotoGaming.Core;

namespace ByJP.AtprotoGaming.Sidecar;

/// <summary>
/// The sidecar's on-disk config: the core's <see cref="CoreConfig.Handle"/> /
/// <see cref="CoreConfig.AppPassword"/> (filled in interactively on first run, not by
/// hand-editing), the loopback listener's port, the set of approved clients, and
/// optional signing. Persisted as <c>config.json</c> next to the executable.
/// </summary>
public sealed class SidecarConfig : CoreConfig
{
    /// <summary>Loopback TCP port the wire protocol listens on.</summary>
    [JsonPropertyName("port")] public int Port { get; set; } = WireProtocol.DefaultPort;

    /// <summary>
    /// Minimum seconds between PDS writes for one play: rapid commits coalesce into at
    /// most one write per window (a commit carrying an outcome/finish flushes
    /// immediately). ≤0 falls back to the default.
    /// </summary>
    [JsonPropertyName("publishIntervalSeconds")] public int PublishIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Seconds to hold back a play's <b>first</b> write after <c>open</c>, so the
    /// opening setup (setup/modifiers/initial metrics) batches into one record instead
    /// of a seed write plus an immediate update. An outcome/finish still flushes
    /// immediately. 0 disables the delay (write on the first commit).
    /// </summary>
    [JsonPropertyName("initialPublishDelaySeconds")] public int InitialPublishDelaySeconds { get; set; } = 15;

    /// <summary>Clients the player has approved to publish. Delete an entry to revoke it.</summary>
    [JsonPropertyName("approvedClients")] public List<ApprovedClient> ApprovedClients { get; set; } = new();

    /// <summary>Optional P-256 private <c>did:key</c> enabling inline record signing.</summary>
    [JsonPropertyName("signingDidKey")] public string SigningDidKey { get; set; } = "";

    /// <summary>The attestation <c>$type</c> stamped on each signature (required iff <see cref="SigningDidKey"/> is set).</summary>
    [JsonPropertyName("attestationType")] public string AttestationType { get; set; } = "";
}

/// <summary>A client the player has paired with the sidecar (by its self-chosen <see cref="Id"/>).</summary>
public sealed class ApprovedClient
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("approvedAt")] public string ApprovedAt { get; set; } = "";
}
