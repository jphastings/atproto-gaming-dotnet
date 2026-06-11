# ByJP.AtprotoGaming.Core — working notes

Engine-agnostic **netstandard2.0** library: lets a game/mod publish play-throughs
to a player's atproto PDS. Ships as a single NuGet package (assembly = root
namespace = package id). First (and reference) consumer is the RoR2 mod in the
sibling repo `../ror2.at` — built to pressure-test this API.

## Commands
- Tests: `dotnet test -v quiet -nologo` — **154 tests, all green**. Keep them green.
- Pack: `dotnet pack src/ByJP.AtprotoGaming.Core -o ../packages` (the ror2 mod
  consumes it from that local feed).
- The ror2 mod's engine-free layer is compile-checked against this package at
  `/tmp/mapcheck` (`dotnet build` there) — see `../ror2.at/CLAUDE.md`.

## Architecture (two layers over the XRPC client)
- **Client/adapters:** `AtprotoClient` (XRPC: createSession/refresh/create/put/
  get/list/delete; proactive ≤80min refresh; 4xx→`AtprotoPermanentException`,
  5xx/network→`AtprotoTransientException`, `ErrorName` parsed from XRPC body).
  Adapters injected: `ILogSink`/`IClock`/`IFileSystem` (NF3). `AtprotoGamingClient`
  is the facade the consumer holds; `OpenPlay(...)` + `LoginAsync()`.
- **Layer 1 — generic CAS publish-or-queue:** `RecordPublisher` (Edit/ApplyCore),
  `Outbox`, optimistic locking via `swapRecord`/`WriteResult{uri,cid}`.
- **Layer 2 — play records:** `PlaySession`/`PlayUpdate`/`PlayWriter`/`PlayOps`/
  `PlayQueue`/`StatsResolver`/`RollingStats`. This is the ergonomic surface.

## ⚠️ The load-bearing invariant: ops must be REPLAY-SAFE
`PlayUpdate` records changes as **serializable JSON ops** (not mutations). `PlayOps.Apply`
applies them to a record. The same ops list is re-applied:
- on every optimistic-lock (`InvalidSwap`) conflict retry (refetch → re-apply), and
- on offline flush (persisted in `PlayQueue`, applied against the freshly-fetched record).

So **every op must be idempotent / safe to apply 2+ times against an evolving base.**
This is how an offline `UpdateProgress` resolves against the *real* value. A bar-raiser
already caught a phantom-stop bug here (`routeLeave` minting a stop on re-apply) — when
touching `PlayOps`, add a "re-apply twice" test. Append ops dedupe by a key
(`instanceId` for acquisitions/route); keyless appends are only safe because each CAS
attempt starts from a fresh base.

## Final API surface (after the RoR2 gap fixes)
- `OpenPlay(playId, game, gameVersion, source, additionalVersions?)` → `PlaySession`.
  `playId` sanitised to a valid record key (`RecordKey`); `DerivePlayID(startedAt, seed)`
  gives a TID-shaped, multiplayer-convergent id. `game` must be a valid AT-URI.
- `PlaySession`: `.Rkey`, `.BeginUpdate()`, `.ForkPlay(id?)` (clones values **verbatim**,
  inherits duration, **throws if the play has ended** — has endedAt or outcome).
- `PlayUpdate` (chainable; one commit): `SetProgress(name, AtValue)`,
  `UpdateProgress(name, long, ProgressOp{Add,Subtract,Min,Max})`,
  `SetAcquisitions(list)` / `AddAcquisition(item)` (dedupe by `instanceId`),
  `RouteArrive(id, instanceId?, name?, arrivedAt?)` / `RouteLeave(id, instanceId?, leftAt?)`,
  `SetOutcome(type, cause?)`, `SetSetting(name, AtValue)`, `SetPlayingWith(participants)`,
  `Finish(endedAtIso, durationSeconds)`, `CommitAsync()`. `SetProgress`/`UpdateProgress`
  reject the reserved names `outcome`/`route` (use the dedicated helpers).
- `AtValue`: implicit from string/int/long/bool/`JsonNode` (use a `JsonObject`/`JsonArray`
  for nested values).
- `client.Stats` (`RollingStats`): `EnsureAsync`, `EnsureAndUpdateAsync(game, source,
  durationSeconds, endedAtIso)`, `AchievementsUnlockedAsync(game, source, unlocked, total)`.
  Achievements in `actor.stats` are **counts** (`achievements:{unlocked,total}`), not
  per-achievement entries. `BuildBase` preserves cross-cutting fields so playtime and
  achievement writes don't clobber each other; achievement write no-ops when unchanged.
- `client.Steam` (`SteamDidResolver`): `LookupDid(string)` / `LookupDidAsync(string)` —
  SteamID64 as a **string** (matches the lexicon + the keytrace request).
  Uses **keytrace.dev** `dev.keytrace.reverseLookup`, NOT Slingshot (deliberate; see auto-memory).
- Roslyn analyzer (`*.Analyzers`): **BAG001** non-camelCase progress/setting keys,
  **BAG002** reserved key names — checks `SetProgress`/`UpdateProgress`/`SetSetting`.
- Optional signing: badge.blue inline P-256 attestation, opt-in via `SigningKey`.

## netstandard2.0 gotchas (already solved — don't "fix" back)
ns2.0 lacks `ImportECPrivateKey`, `DSASignatureFormat`, `SHA256.HashData`, the
`BigInteger` span ctor, and `Stream.Write(span)`; we avoid `System.Memory`. The P-256
public point is derived with a hand-rolled scalar multiply (`Signing/P256.cs`) and the
key imported via full `ECParameters` (`Signing/EcdsaP256.cs`). `SignData(data, hash)`
returns IEEE-P1363 r‖s on ns2.0; low-S normalized in `InlineAttestation`.

## Lexicons (local copies in `lexicons/`)
`games.gamesgamesgamesgames.actor.play` — `#gameItem`/`#routeStop` carry `instanceId`;
route timestamps are `arrivedAt`/`leftAt`. The `actor.stats` lexicon is upstream
(gamesgamesgamesgamesgames/lexicon on GitHub); achievements there are counts only.
