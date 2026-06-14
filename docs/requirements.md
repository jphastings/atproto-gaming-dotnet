# Requirements

What the `ByJP.AtprotoGaming.Core` package must provide to be useful to a
game mod that wants to publish play-throughs to the player's atproto PDS.

## Context

Two concrete consumers drive these requirements:

- A Risk of Rain 2 BepInEx mod. Unity Mono runtime, .NET
  Framework 4.7.2 target, MonoMod `On.*` hooks, host-authoritative
  multiplayer over Steam P2P. See
  `../ror2.at/docs/`.
- A Slay the Spire 2 mod. Godot.NET runtime, .NET 9 target,
  HarmonyX patches, per-client (no host) multiplayer. See
  `../sts2.at/mod/`.

The shared data shape is the
[`games.gamesgamesgamesgames.experimental.actor.play`](../lexicons/games/gamesgamesgamesgames/experimental/actor/play.json)
lexicon, alongside the existing public
`games.gamesgamesgamesgames.actor.stats` record.

A useful **reference implementation already exists** in sts2.at's mod
folder — roughly 60% of what this package needs is sitting there in a
game-coupled form, ready to be lifted out and generalised:

| sts2.at file              | Generalises to                                                                      |
| ------------------------- | ----------------------------------------------------------------------------------- |
| `mod/AtProtoClient.cs`    | `AtprotoClient` (essentially verbatim)                                              |
| `mod/Outbox.cs`           | `Outbox` (essentially verbatim)                                                     |
| `mod/IdentityResolver.cs` | `IdentityResolver` (essentially verbatim)                                           |
| `mod/AuthState.cs`        | `AuthState` (essentially verbatim)                                                  |
| `mod/Config.cs`           | `ConfigStore<T>` (genericised)                                                      |
| `mod/Tid.cs`              | `Tid` (essentially verbatim)                                                        |
| `mod/Signing/*`           | `Signing/*` (essentially verbatim)                                                  |
| `mod/RunPublisher.cs`     | `RecordPublisher` (split: generic publish-or-queue + game-specific record assembly) |

The implementation can lift these almost verbatim; the bulk of the
*design* work is the abstraction boundary between this package and a
consumer mod, not the atproto plumbing itself.

## Scope

**In scope.** Anything atproto-side a consumer would otherwise have to
reimplement: XRPC client, identity, auth state, queue, signing, rkey
derivation, config loading, stats-record coordination.

**Out of scope.**

- Defining the record schema. The consumer assembles payloads against
  the lexicon.
- Deciding *when* to emit. The consumer signals dirtiness or calls
  `Publish` directly.
- Rendering any UI. The consumer subscribes to `AuthState` events and
  draws what it needs.
- Game-engine hooks. The consumer uses MonoMod, HarmonyX, GDScript,
  whatever fits its target.
- Steam / EOS / platform-id extraction. The consumer reads its game's
  `NetworkUser.id.value` (RoR2) or equivalent and hands the platform
  id to this package as a string.

Where the line is fuzzy (see [F8](#f8-multi-collection-publishing),
[F11](#f11-versions-block-assembly)), the doc calls it out.

## Conventions

Requirements use **MUST** (required for a useful package), **SHOULD**
(strongly recommended), **MAY** (optional, nice-to-have). Each
requirement has a stable ID (`F1`, `NF2`, etc.) so we can reference
them in code review, tests, and PRs.

---

## Functional requirements

### F1. AT Protocol client

The package MUST provide an HTTP client that speaks the subset of the
atproto XRPC surface a publish-and-update workflow needs:

- `com.atproto.server.createSession` (login by `identifier` + `password`)
- `com.atproto.server.refreshSession` (refresh JWT)
- `com.atproto.repo.createRecord`
- `com.atproto.repo.putRecord`
- `com.atproto.repo.getRecord`
- `com.atproto.repo.listRecords` (with cursor)
- `com.atproto.repo.deleteRecord`

It MUST handle token expiry transparently:

- Access JWTs expire (~2h on most PDSes). The client MUST refresh
  proactively on a conservative TTL (≤80 minutes) rather than reactively
  on 401, so a single long call doesn't fail mid-flight.
- Refresh MUST be single-flight (one in-flight refresh at a time;
  concurrent callers wait on the same `SemaphoreSlim`).
- Refresh failure MUST be terminal — clear the cached tokens, set
  `AuthState` to `Failed`, surface the PDS's error message.

It MUST surface HTTP failures distinguishably:

- 4xx (validation, auth) → `PermanentRejection` → caller drops the record.
- 5xx and `HttpRequestException` with no status code (no network) →
  transient → caller queues for retry.

Reference: `sts2.at/mod/AtProtoClient.cs`.

### F2. Identity resolution

The package MUST resolve an atproto handle (or DID) to a `(did, handle,
pds)` triple via [Slingshot][slingshot]'s `blue.microcosm.identity.resolveMiniDoc`
endpoint.

It MUST handle the offline-at-boot case: if Slingshot is unreachable but
a previous successful resolution is cached in config, the package
SHOULD seed `AuthState` with the cached DID so queued records have a
bucket. The cache MUST invalidate when the user changes their handle.

It MUST also offer Steam-ID → DID resolution (for backfilling
`playingWith[].atproto`) via the same Slingshot endpoint with the
Steam-ID variant. Implementation can lift from
`sts2.at/mod/SteamDidResolver.cs`.

[slingshot]: https://slingshot.microcosm.blue/

### F3. Authentication state machine

The package MUST expose an observable auth-state singleton with these
values:

- `Unconfigured` — no handle/password in config.
- `Checking` — Slingshot / login in flight.
- `Ok` — logged in; PDS, DID, handle known.
- `Failed` — credentials rejected or refresh failed; error text
  available.
- `Offline` — network unreachable; DID known from cache; queueing.

The state MUST fire a `Changed` event on every transition so a consumer
UI (main-menu badge, installer health check) can re-render without
polling.

It MUST be thread-safe — the consumer's UI thread reads it while
background tasks mutate it.

Reference: `sts2.at/mod/AuthState.cs`.

### F4. On-disk outbox

The package MUST persist records that fail to publish so they survive
process restart.

- Files MUST be bucketed by **DID** (`outbox/<encoded-did>/<rkey>.json`).
  This means a logged-out queue stays intact until the matching DID
  signs in again, and account-switching doesn't cross-flush queues.
- Encoded DIDs MUST be Windows-filename-safe (`:` and similar →
  percent-encoded).
- Write MUST be atomic (`tmp` + rename).
- Each file MUST contain the already-prepared JSON payload, signed if
  applicable, ready to PUT byte-for-byte.

It MUST provide a flush operation that:

- Runs at most one at a time per package instance (single-flight lock).
- Iterates queued records for the currently-authenticated DID only.
- On permanent rejection: deletes the file with an error log.
- On transient failure: leaves the file and stops the flush (don't
  hammer the PDS).
- Accepts a `skipPredicate` so the caller can hold back the currently-
  active record (which the live publisher is also racing to write).

It MUST trigger automatically on:

- Successful login (drain any queue accumulated while offline).
- Each successful online publish (best-effort retry of older queued
  records).

Reference: `sts2.at/mod/Outbox.cs`.

### F5. Record publisher

The package MUST provide a publisher that wraps "publish-or-queue":

```
PutAsync(collection, rkey, payload) →
   if AuthState.Ok:
       try PUT; on success: remove from outbox; return.
       on permanent rejection: drop, log, return.
       on transient failure: enqueue, return.
   else (Unconfigured / Failed / Offline):
       enqueue if we know the DID; drop otherwise.
```

It MUST be collection-agnostic — the consumer chooses the NSID and rkey.

It MUST handle multiple in-flight publishes against the **same rkey**
correctly (multi-update of an in-progress play-through). At minimum,
serialise puts to the same `(collection, rkey)` so an out-of-order
arrival doesn't replace newer state with older.

Reference: `sts2.at/mod/RunPublisher.cs` (currently bound to a single
collection; needs un-coupling).

### F6. TID / rkey derivation

The package MUST provide a deterministic TID generator
`Tid.FromPlayThrough(unixSeconds, salt)` that produces the same rkey
across runs of the same play-through, on all multiplayer participants.

- Output MUST be a valid atproto TID (base32-sortable, 13 chars).
- The (unixSeconds, salt) tuple is the contract: derive `salt` from
  the game's run seed where one exists; from another stable per-play
  ID where it doesn't.

It MAY also provide a `Tid.FromAtUri(strongRef)` helper for the
`forkedFrom` case where a save fork inherits its parent's rkey lineage.

Reference: `sts2.at/mod/Tid.cs`.

### F7. Config loading

The package MUST provide a generic config store with these guarantees:

- Loads JSON from a consumer-supplied path; creates a template on first
  run if missing.
- The template MUST emit a clearly-visible "not configured yet" banner
  via the consumer's log sink so first-run users see what to edit.
- Save is atomic (`tmp` + rename, like the outbox).
- Re-saving on a config field change is the caller's responsibility;
  the store doesn't auto-persist mutations.

The config DTO is generic over a consumer-defined record type. The
package SHOULD ship a `CoreConfig` base type that includes:

- `Handle` (string)
- `AppPassword` (string)
- `CachedHandle`, `CachedDid`, `CachedPds` (for offline-boot seeding)
- `StatsRkey` (the player's rolling-stats record's rkey, cached after
  first publish)

Reference: `sts2.at/mod/Config.cs`.

### F8. Multi-collection publishing

The package MUST support publishing to multiple collections from the
same logged-in session:

- `games.gamesgamesgamesgames.experimental.actor.play` (the per-play-through record).
- `games.gamesgamesgamesgames.actor.stats` (the rolling stats record).
- Game-mod-specific collections (lobby records, achievement logs,
  etc.) for any NSID the consumer chooses.

The publisher MUST NOT bake the NSID of the play record into its core
loop. Each `PutAsync` takes the collection as an argument.

### F9. Rolling stats coordination

The package MUST provide a helper that maintains the `actor.stats`
record alongside finished plays:

- **First call**: if the user's PDS has no stats record for this game,
  create one; cache the rkey in config.
- **Subsequent calls**: read the current record (`GetRecordAsync`), add
  the new play's `duration / 60` minutes to `playtime`, bump
  `lastPlayed` if newer, PUT.
- The current stats record's at-uri SHOULD be returned so the consumer
  can set `stats` on the play record.

This helper MUST tolerate the case where `cfg.StatsRkey` is set but the
record no longer exists on the PDS (user nuked it): fall through to
creating a fresh one.

Reference: `sts2.at/mod/RunPublisher.cs` (`EnsureStatsRecordAsync`,
`UpdateStatsAsync`, `MergeStatsDeltaAsync`).

### F10. Achievement event support

The package SHOULD support writing achievement-unlock events as a
separate collection (the consumer chooses the NSID; e.g.
`me.byjp.pesos.ror2.achievement`).

Beyond `F5`'s generic publish, the package SHOULD offer a
**deduplication** helper: same-achievement-id-from-same-game writes
are no-ops within a single session. This avoids spamming the PDS when a
profile-load triggers re-registration of the achievement set on game
start (see ror2.at's `achievements.md` for the specific gotcha).

### F11. Versions block assembly

The `actor.play` lexicon requires a `versions` field containing the
game version plus an array of additional software (mods, the core
package).

The package MUST automatically inject its **own** entry into
`versions.additional` whenever it serialises a record, so consumers
don't have to. Pattern:

```json
"versions": {
  "game": "<consumer-supplied>",
  "additional": [
    { "name": "<consumer-mod>", "version": "<consumer-mod-version>" },
    { "name": "atproto-gaming-dotnet", "version": "<package-version>" }
  ]
}
```

The consumer provides `game` (their game's version string) and may
provide additional entries; the package appends its own at write time.

### F12. ECDSA signing (optional)

The package MAY provide an opt-in CID-first inline-attestation signer
per the [badge.blue][badgeblue] convention. When enabled, records gain a
`signatures: [{ cid, key, signature }]` field stripped during
verification. Keys are P-256, did:key encoded.

The signing key is provided by the consumer at construction time; the
package MUST be usable without it (unsigned records still publish).

Reference: `sts2.at/mod/Signing/`.

[badgeblue]: https://badge.blue/

### F13. Save-fork lineage

The lexicon's `forkedFrom` field is a `com.atproto.repo.strongRef`
(uri + cid) pointing to the previous save's play record.

The package MUST provide a strongRef helper that:

- Builds a strongRef from `(uri, cid)`.
- Computes the cid of a record body (for use when forking from a record
  the consumer has the body of but not a fetched cid). DAG-CBOR + SHA-256
  per the atproto spec.

Consumers detect the fork themselves (game-specific save logic) and
hand the parent strongRef to the publisher.

---

## Non-functional requirements

### NF1. Engine-agnostic

The package MUST NOT reference `UnityEngine.*`, `Godot.*`, `BepInEx.*`,
HarmonyX, MonoMod, or any other game-engine or mod-framework type.

This is enforced at the project level: the `.csproj` has no game DLL
references; CI fails any PR that adds one.

### NF2. Target framework

The package MUST target `netstandard2.0` so it loads under both:

- BepInEx 5 on .NET Framework 4.7.2 (the RoR2 path).
- .NET 9 (the StS2 path).

If a specific API (e.g. `System.Text.Json`) forces a higher target, the
package MUST multi-target to keep the .NET Framework consumer working.
Picking up `Microsoft.Bcl.AsyncInterfaces`, `System.Text.Json` (via
NuGet), or `System.Memory` for ns2.0 is fine.

### NF3. Adapter interfaces for runtime concerns

The package MUST inject these via small interfaces so the consumer
provides the runtime-correct implementation:

- `ILogSink { Info(msg), Warn(msg), Error(msg, ex?) }` — wired to
  BepInEx `ManualLogSource` / Godot `GD.Print` / `Console.WriteLine`.
- `IClock { UtcNow, UnixSeconds }` — overridable for tests.
- `IFileSystem { ConfigDirectory, OutboxRoot }` — typically the
  directory next to the consumer's plugin DLL.

No interfaces beyond these unless a real need surfaces.

### NF4. Thread safety

The package MUST tolerate calls from arbitrary threads:

- `AuthState` reads from the UI thread while background tasks mutate
  it (use a single internal lock).
- The outbox flush MAY be triggered from any thread.
- The publisher MUST internally serialise concurrent puts to the same
  `(collection, rkey)`.
- The HTTP client's refresh lock MUST handle concurrent calls.

It MUST NOT start its own background threads or timers — all async work
runs on `Task.Run` from a caller's invocation, so the consumer can
control task scheduling and cancellation.

### NF5. Crash-resilient

A game crash mid-play-through MUST NOT lose anything that's already on
disk. Specifically:

- Outbox writes MUST be atomic.
- Config saves MUST be atomic.
- Either MUST tolerate a partial write from a prior crashed session
  (corrupt JSON → log, discard, continue).

### NF6. Quiet on the happy path, loud on errors

Logging MUST be:

- **Info** for one-time lifecycle events (login success, mod loaded,
  signing key embedded).
- **Warn** for transient failures the package handled itself (queued
  record, refreshed token, identity cache stale).
- **Error** for things the user can fix (auth failed, config malformed,
  PDS rejected payload).

The package MUST NOT log per-publish on the happy path; a 30-stage RoR2
run shouldn't produce 30 info lines.

### NF7. No transitive game dependencies

Consumers MUST be able to add this package to a BepInEx plugin
`.csproj` without dragging in `Microsoft.Extensions.*`, `Serilog`, or
other heavyweight Microsoft libs. Dependency choices SHOULD prefer
plain `System.*` and `System.Text.Json`.

### NF8. NuGet-shippable

The package MUST build to a single `.nupkg` consumable from any
.NET project. Naming follows
[`docs/requirements.md`'s sibling decision (TBD)] — likely
`ByJP.AtprotoGaming.Core`. Assembly name = root namespace = package
id.

---

## Integration surface

To use the package, a consumer:

1. **Constructs a config store**: hands over a path, defines a config
   DTO inheriting `CoreConfig`, calls `LoadOrCreate`.
2. **Wires the adapters**: implements `ILogSink`, `IClock`,
   `IFileSystem` for the runtime.
3. **Boots auth on game load**: calls `AuthState.Set(Checking)` →
   `IdentityResolver.ResolveAsync(handle)` → `AtprotoClient.LoginAsync`
   → `AuthState.Set(Ok / Failed / Offline)`. Triggers
   `Outbox.FlushAsync` on success.
4. **Subscribes to `AuthState.Changed`** for UI updates (badge,
   installer health check).
5. **On play-through start**: derives an rkey via `Tid.FromPlayThrough`,
   assembles an initial play record body, calls `RecordPublisher.PutAsync`.
6. **On significant in-play state change**: re-assembles the play
   record body, calls `RecordPublisher.PutAsync` with the same rkey
   (which replaces the previous PUT). Throttling and dirty-bit logic
   live in the consumer; the package just publishes when asked.
7. **On play-through end**: same as 6, plus calls the
   `RollingStats.EnsureAndUpdateAsync` helper, sets `stats` on the
   final play record from the helper's return value.
8. **On game shutdown**: nothing required — partial state is in the
   outbox.

The package MUST NOT require any other lifecycle calls. It MUST NOT
have an `Initialize()` / `Shutdown()` ceremony beyond ordinary .NET
object lifetimes.

---

## Acceptance criteria

These are the user-visible behaviours the package's tests should
demonstrate end-to-end (with a real PDS or a stub):

1. **First-run flow**: empty config → template written → log banner
   visible → `AuthState.Unconfigured`. Filling in `handle` +
   `appPassword` and re-loading → `AuthState.Ok` and one record
   publishable.
2. **Multi-update play-through**: 30 puts to the same rkey → the
   PDS shows one record reflecting the 30th payload. None of the
   previous 29 are still resolvable as separate records.
3. **Offline writes survive restart**: disable network mid-publish →
   record lands in outbox. Restart the process. Re-enable network. The
   queued record publishes within one flush.
4. **Account switch**: log in as DID-A, publish a record, log out, log
   in as DID-B. DID-A's outbox (if any) remains untouched. DID-B's
   records publish to DID-B's PDS. Log back in as DID-A and the queue
   resumes.
5. **Bad password**: log in with a wrong app password → `AuthState.Failed`
   with the PDS's error text in `AuthState.Error`. Records publish to
   the outbox (bucketed under the cached DID if available, dropped with
   a log otherwise).
6. **Rolling stats**: publish two finished plays of the same game →
   `actor.stats` exists with one rkey, `playtime` equals the sum of
   their durations, `lastPlayed` matches the latest play's `endedAt`.
7. **Save-fork lineage**: build a strongRef from a parent play's
   (uri, cid), publish a new play with `forkedFrom` set → both records
   resolve and the fork's `forkedFrom` round-trips through `getRecord`.
8. **Signed record (when key embedded)**: published record contains a
   `signatures` array with a valid P-256 signature over the CID of the
   stripped body, verifiable by the published public key in
   `keys.json`.

---

## Out of scope (worth restating)

The package will not:

- Define the structure of `progress`, `settings`, or `acquisitions[]`.
  Those are game-specific; consumers serialise their own DTOs.
- Decide when to call `Publish`. The consumer's lifecycle hooks own
  that.
- Throttle, debounce, or coalesce calls. The consumer can hold a
  dirty-bit + interval timer in its own code (a thin helper might
  belong in a sibling package later; not v0).
- Render or own any UI. The badge / installer / etc. read `AuthState`
  and draw what fits the consumer's runtime.
- Replace the game's networking layer (see ror2.at's
  `matchmaking.md`).
- Discover other players or coordinate matchmaking. A future
  `ByJP.AtprotoGaming.Matchmaking` package may, but not this one.

---

## Open questions

To resolve during implementation:

- **Q1.** Package naming: `ByJP.AtprotoGaming.Core` is the working
  name. Lock in before publishing the first preview to NuGet.
- **Q2.** Should the package ship a sibling `.Throttle` helper for the
  dirty-bit + interval emission pattern both consumers want? Or leave
  every consumer to roll its own (it's ~20 lines)?
- **Q3.** When `netstandard2.0` proves limiting (likely around
  `System.Text.Json`'s source generators, or `HttpClient.PatchAsync`),
  multi-target to `net8.0` for the StS2 path? Or stay single-target
  and let the StS2 consumer take whatever .NET-Framework-flavoured
  Json experience falls out?
- **Q4.** The lexicon's `forkedFrom` uses a strongRef which needs a
  CID. Computing CIDs from a `JsonNode` requires DAG-CBOR encoding —
  pull in `Ipfs.Engine` / `PeterO.Cbor` for that, or roll a small
  encoder? Look at how the existing signing code computes the CID it
  signs — same machinery.
- **Q5.** The stats-record collection's NSID is in the public
  `games.gamesgamesgamesgames` namespace, not ours. If that lexicon
  ever changes shape, this package's helper has to track it. Pin the
  schema version somewhere visible.
- **Q6.** Achievement deduplication ([F10](#f10-achievement-event-support))
  needs a session-scoped seen-set. Where does that live — in-memory
  on the publisher, or on disk so it survives a restart? In-memory is
  enough if the consumer also filters its own re-fires (which ror2.at
  already plans to via the `isExternal` flag).
