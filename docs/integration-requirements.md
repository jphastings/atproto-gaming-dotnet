# Game integration requirements

What a game — or, more usually, a game **mod** — must implement to reach the same level
of atproto integration as the reference
[Risk of Rain 2 mod](https://github.com/jphastings/risk-of-rain-2-atproto).

The engine-agnostic plumbing (XRPC, identity, auth state, offline queue, rkey derivation,
signing, stats coordination, the replay-safe play-record API) is provided by
[`ByJP.AtprotoGaming.Core`](requirements.md). **This document is the checklist for the
game-specific layer the consumer still has to build, plus the behaviours the integration
as a whole must exhibit.** A conforming integration uses the package for everything in
`requirements.md` and satisfies everything below.

Keywords **MUST**, **SHOULD**, **MAY** are per RFC 2119. Requirement IDs are stable.

## Reference implementation

The RoR2 mod is the canonical example; each area maps to one or two files there:

| Area                      | RoR2 reference                                                  |
| ------------------------- | --------------------------------------------------------------- |
| Credentials + auth wiring | `mod/Config/BepInExConfigStore.cs`, `mod/Plugin.cs`             |
| Game-state extraction     | `mod/Ror2/StateExtractor.cs`                                    |
| Run lifecycle → emit loop | `mod/Ror2/RunTracker.cs`, `mod/Ror2/AchievementPatch.cs`        |
| Snapshot → record mapping | `mod/Mapping/RunSnapshot.cs`, `mod/Mapping/PlayRecordMapper.cs` |
| In-game status UI         | `mod/MenuStatusBadge.cs`                                        |
| Shipped default config    | `mod/Config/atproto-play-tracking.cfg`                          |
| Packaging + release       | `.github/workflows/release.yml`, `mod/manifest.json`            |

The split that matters: the package owns the *atproto* side; the mod owns three things —
**(a)** turning live game state into a serialisable snapshot, **(b)** mapping that snapshot
onto record ops, and **(c)** the player-facing config + UI. Everything below is one of
those three plus the cross-cutting behaviours.

---

## 1. Identity & authentication (AUTH)

- **AUTH-1** The player MUST authenticate with their atproto **handle + an app password**,
  never their account password. The UI/config MUST say so.
- **AUTH-2** The integration MUST resolve handle → DID → PDS, log in, and refresh tokens
  proactively, via the package (`AtprotoGamingClient.LoginAsync`). It MUST NOT block the
  game thread doing so.
- **AUTH-3** It MUST expose an observable auth state with at least:
  *unconfigured, checking, signed-in, rejected, offline* (`AuthState`).
- **AUTH-4** Credentials MUST be **re-validated when the player changes them**, with clear
  ✓/✗ feedback distinguishing *wrong credentials* from *offline*.
- **AUTH-5** The resolved identity (DID/PDS) SHOULD be cached so an offline launch can still
  bucket records under the right repository. The cache MUST NOT clutter the player-facing
  config (e.g. a sidecar, not editable settings).

## 2. The play record (REC)

- **REC-1** The integration MUST write **one `games.gamesgamesgamesgames.actor.play`
  record per play-through**, updated as the play proceeds and finalised at the end.
- **REC-2** The record key MUST be **deterministic and multiplayer-convergent** — derived
  from stable play facts (e.g. start time + seed) so every peer in a co-op session writes
  the *same* record (`DerivePlayID`).
- **REC-3** `game` MUST be the AT-URI of the play's title in the
  `games.gamesgamesgamesgames.game` catalogue (a registered record, not an invented slug). You can discover your game's record at [cartridge.dev](https://cartridge.dev); there's a link to the AT URI you need on the right near the bottom.
- **REC-4** The record SHOULD capture, where the game has them: the **character/loadout**
  (`settings.character` = started-as; `progress.character` = current), **run settings**
  (seed, difficulty, mode, artifacts/modifiers), **progress** (live stats, current state),
  the **route/stages** visited, **acquisitions** (items), the **outcome** (type + cause),
  **co-op participants**, **duration**, `startedAt`/`endedAt`, and the link to the stats
  record.
- **REC-5** Identifiers written to the record MUST be **stable, language-independent keys**
  (e.g. RoR2 body names like `HuntressBody`), not localised display strings — a viewer
  maps them to pretty names. Setting/progress keys MUST be camelCase and avoid the reserved
  `outcome`/`route` names (the package's analyzer enforces this).
- **REC-6 (load-bearing)** Every change MUST be expressed as an **idempotent, replay-safe
  op** via `PlayUpdate`. The same op list is re-applied on optimistic-lock retries and on
  offline flush against a freshly-fetched record, so applying it twice against an evolving
  base MUST NOT duplicate, drop, or mint phantom data. (Re-state full lists / re-arrive
  keyed by a stable `instanceId` rather than tracking deltas.) The helper functions in this package do this for you.

## 3. Lifetime stats (STAT)

- **STAT-1** On each completed play-through the integration MUST update (or create) the player's
  **`games.gamesgamesgamesgames.actor.stats`** record for this game + source (`RollingStats`):
  at minimum total **playtime**.
- **STAT-2** Where the game exposes them, it SHOULD record **achievement counts**
  (`unlocked`/`total`), updating only on a real change (no re-fire on profile load).
- **STAT-3** Stats writes MUST preserve cross-cutting fields so playtime and achievement
  updates don't clobber each other.

## 4. Offline resilience (OFF)

- **OFF-1** The integration MUST be **offline-safe**: when disconnected, updates queue to an
  on-disk outbox and flush automatically when connectivity returns (`Outbox`). This package handles this for you.
- **OFF-2** A crash, alt-F4, or power loss mid-play MUST NOT duplicate or lose published
  data — a direct consequence of REC-6.

## 5. Multiplayer & co-op (MP)

- **MP-1** Other players in the session SHOULD be recorded in `playingWith` by their
  **platform id** (e.g. SteamID64 as a decimal string), resolved to an atproto DID where
  they have one. (The [keytrace reverse index](https://keytrace.dev/xrpc/dev.keytrace.reverseLookup?type=steam&subject=76561197994000231) may help with this, see SRC-2 for a helper)
- **MP-2** The **posting player MUST be excluded** from `playingWith` (solo plays have an
  empty/absent list).
- **MP-3** Platform ids MUST be the canonical form. eg. for Steam, the SteamID64, not SteamID2/SteamID3/account-id shapes, so the integration MUST convert before
  handing them over.

## 6. Platform / source (SRC)

- **SRC-1** The integration MUST record which platform the player plays on (steam, epic,
  gog, …) and pass it as the stats `source`.
- **SRC-2** Where it resolves co-op DIDs, it MUST use the platform→DID resolver the package
  provides (e.g. `Steam.LookupDidAsync`) rather than rolling its own.

## 7. In-game feedback & UX (UX)

- **UX-1** The integration SHOULD surface an **at-a-glance status indicator** in the game's UI
  (eg. an `@` badge): connection state visible without interaction, with a clear
  not-connected treatment.
- **UX-2** The indicator SHOULD expand on demand to show **who you're signed in as** (or the
  specific problem) and the **integration version**.
- **UX-3** The indicator SHOULD be **native to the game's UI** (its fonts/canvas), shown
  where the player looks before playing (e.g. the main menu), not an always-on overlay.
- **UX-4** Configuration MUST be **editable before the first launch** — ship a default
  config file with the package so it exists on install, rather than only appearing after
  the game has run once.
- **UX-5** The player-facing config MUST show only what the player edits (credentials,
  recording prefs); managed/cache state MUST be hidden.

## 8. Provenance & integrity (PROV)

- **PROV-1** Each record MUST stamp the **game version** and the **integration/mod version**
  (`versions.game`, `versions.additional[]`).
- **PROV-2** The integration MAY support **optional inline P-256 attestation** (badge.blue)
  for record authenticity, opt-in via a signing key (`SigningKey`); it MUST default to
  unsigned.

## 9. Data ownership & privacy (PRIV)

- **PRIV-1** Records MUST be written **only to the player's own PDS**. There MUST be no
  central collection server and no telemetry back to the integration's authors.
- **PRIV-2** The only third-party calls permitted beyond the player's PDS are identity/DID
  resolution and the co-op platform→DID lookup; these MUST be disclosed (e.g. in a README
  privacy note).

## 10. Packaging & distribution (PKG)

- **PKG-1** The integration MUST be installable via the platform's mod manager / plugin
  system (for RoR2: a Thunderstore-layout package importable by r2modman). The package MUST
  include `manifest.json`, an `icon.png`, a `README.md`, the plugin + its managed
  dependencies, and the default `config/`.
- **PKG-2** The package MUST declare its loader dependency (e.g. BepInExPack) in its
  manifest.
- **PKG-3** The integration MUST have a **single source of truth for its version**, with the
  in-code plugin version, the manifest version, and the release artifacts all derived from
  it (no hand-synced copies).
- **PKG-4** If the integration is AI-generated, it SHOULD disclose that (README and/or an
  `ai-generated` category where the store supports it).

## Non-functional (NFR)

- **NFR-1** The integration MUST reuse `ByJP.AtprotoGaming.Core` for all atproto behaviour;
  only state-extraction, record-mapping, and config/UI are bespoke. Re-implementing the
  plumbing is a non-conformance.
- **NFR-2** It MUST target the game's runtime. The package's `netstandard2.0` floor loads
  under both BepInEx/.NET Framework 4.7.2 (Unity Mono) and .NET 9 (Godot.NET) — the
  integration MUST not require anything the host runtime can't load.
- **NFR-3** REC-6 (replay safety) is the load-bearing invariant; any new op or mapping
  change MUST be accompanied by an "apply the same ops twice" test.
- **NFR-4** Game-thread work MUST be bounded: extraction runs on the game loop, but network
  I/O MUST be off-thread, and UI mutation MUST be on the game's main thread.

---

## Conformance checklist

A new game integration is "RoR2-parity" when it can tick every **MUST** above. In practice
that decomposes to: wire the package's client + config (AUTH, SRC, PKG), write a
state→snapshot extractor (REC-4/5), write a replay-safe snapshot→ops mapper (REC-1/2/6,
MP, STAT), ship a status indicator + default config (UX), and stamp provenance (PROV-1).
The atproto-heavy requirements are satisfied for free by depending on the package.
