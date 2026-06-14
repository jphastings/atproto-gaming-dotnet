# Play-event wire protocol (v1)

A tiny line protocol that lets a **constrained emitter** — an mGBA Lua script, a
retro-console homebrew, a microcontroller, anything that can open a TCP socket but
can't speak TLS + XRPC — publish a play-through to a player's atproto PDS.

The emitter speaks this protocol to a **local sidecar**
(`ByJP.AtprotoGaming.Sidecar`) over loopback. The sidecar holds the credentials and
owns everything hard: TLS, login + session refresh, the optimistic-locking
read-modify-write, the replay-safe op model, the offline outbox, publish rate-limiting,
and optional signing. **The emitter never sees a credential and never constructs a replay-safe
op** — it emits *intents* (`metric.update score max`), and the sidecar compiles each
into the correct `state[]` op inside the library.

This document is the contract. It mirrors the `PlayUpdate` surface of
`ByJP.AtprotoGaming.Core` one-to-one; if you can call the library, you can drive this.

---

## 1. Transport & framing

- **TCP over loopback only.** The sidecar binds `127.0.0.1` (never a routable
  interface). Default port **`17872`** (configurable).
- **Framing: newline-delimited JSON (NDJSON).** Each message is exactly one UTF-8
  JSON **object** followed by a single `\n` (`0x0A`). Serialize compact — no pretty
  printing, no embedded raw newlines. Read by buffering bytes until you see `\n`,
  then parse the line as one message.
- **Request → response, one-for-one, in order.** Every request the emitter sends
  gets exactly one response line back, in the same order. The sidecar never sends an
  unsolicited message. (So a blocking `socket:receive` loop is fine; you always know
  a reply is coming.)
- Encoding is UTF-8. The protocol is case-sensitive.

## 2. Message shape

**Request** (emitter → sidecar):

```json
{"cmd":"metric.update","id":7,"name":"score","value":4200,"op":"max"}
```

| field | required | meaning |
|-------|----------|---------|
| `cmd` | yes | the command name (see §5) |
| `id`  | no  | client correlation id (number or string); echoed back as `re` |
| …     | —   | command-specific fields |

**Response** (sidecar → emitter):

```json
{"re":7,"ok":true}
```

| field | always | meaning |
|-------|--------|---------|
| `ok`  | yes | `true` if the command was accepted/processed |
| `re`  | when the request had `id` | the request's `id`, echoed |
| `type`| on lifecycle replies | `ready` / `opened` / `committed` / `pong` |
| `error` | when `ok:false` | a short machine code (see §6) |
| `message` | when `ok:false` | human-readable detail |
| `warn` | sometimes | a non-fatal advisory (the command still succeeded) |
| …     | — | command-specific fields (`rkey`, `status`, `uri`, …) |

`id` is optional but recommended — it lets you assert which reply matches which
request even though the protocol is already strictly ordered.

## 3. Session lifecycle

```
connect TCP
  └─ hello            ──► ready          (handshake; must be first)
       └─ open        ──► opened {rkey}  (begin/resume one play-through)
            ├─ metric.set / route.arrive / … ──► ok   (buffered, no network)
            ├─ …more mutations…                ──► ok
            └─ commit  ──► committed {status}  (one record write)
            ├─ …more mutations…                ──► ok
            └─ commit  ──► committed {status}
       └─ open        ──► opened {rkey}  (a second play, same connection — optional)
  close TCP   (any uncommitted buffer is DROPPED — always commit first)
```

- **`hello` must be the first message.** Any other command before a successful
  `hello` is rejected with `error:"notReady"`.
- **Mutations buffer; `commit` writes.** Mutating commands (§5.3) accumulate in
  memory and return `ok` immediately with no network I/O — exactly like calling
  `PlayUpdate` helpers before `CommitAsync`. `commit` flushes the buffer as a single
  optimistic record update and starts a fresh buffer.
- **Publishing requires one-time approval.** The first time a `clientId` actually tries
  to `commit` buffered data, the sidecar asks the player to approve it. Until approved,
  `commit` returns `status:"pending"` and **keeps your buffer**; the approval is
  remembered across restarts, so it only happens once per client (§7).
- **One active play per connection.** `open` makes a play active. A second `open`
  replaces it; if the previous play had uncommitted buffered mutations they are
  discarded and the `opened` reply carries a `warn`. Commit before re-opening if you
  care about those changes.
- **Closing the socket does not commit.** Uncommitted buffered mutations are lost on
  disconnect. Always `commit` before you close.

## 4. Handshake — `hello`

```json
{"cmd":"hello","protocol":1,"clientId":"a1b2c3d4e5f6a7b8","client":"supermarioland-atproto/0.1"}
```

| field | required | meaning |
|-------|----------|---------|
| `protocol` | yes | protocol version you speak. This document is **`1`**. |
| `clientId` | yes | a stable id you generate **once** and reuse on every connection (see §7). The player approves it a single time; it's then remembered. |
| `client` | yes | the emitter's **`name/version`** (e.g. `mgba-atproto/0.1`). Shown in the approval prompt, and recorded in each play's `versions.additional`. Must be exactly `name/version` with no whitespace, else `invalidValue`. |

**Success:**

```json
{"ok":true,"type":"ready","protocol":1,"approval":"pending","auth":"ok","did":"did:plc:abc…","signing":false}
```

- `approval` ∈ `approved` · `pending` — whether this `clientId` may publish yet. A
  `pending` client can still `open` and buffer mutations; **only `commit` is gated**
  (see §5.2). Once the player approves the client in the sidecar, a later `commit`
  publishes — no reconnect needed.
- `auth` ∈ `ok` · `offline` · `unconfigured` · `failed` · `checking` — the sidecar's
  login state, independent of `approval`. While `offline` writes queue; while
  `unconfigured`/`failed` they queue under a cached DID if one exists, else drop.
  Surface this so the player knows whether anything is landing.
- `did` is the player's DID when known, else omitted.
- `signing` is `true` when the sidecar will attach a signature to each record.

**Failure** (then the sidecar closes the connection):

```json
{"ok":false,"error":"unsupportedProtocol","message":"server speaks protocol 1"}
```

There is no shared secret to present — authorisation is by one-time approval of your
`clientId` (§7), not a token.

## 5. Commands

### 5.1 `open` — begin or resume a play

```json
{"cmd":"open","game":"at://did:plc:…/dev.cartridge.game/super-mario-land",
 "gameVersion":"world","source":"mgba","playId":"my-run-123"}
```

| field | required | meaning |
|-------|----------|---------|
| `game` | yes | the game's AT-URI (must be a valid `at://…`) |
| `gameVersion` | yes | the game's version string (→ `versions.game`) |
| `source` | yes | the platform string (e.g. `"mgba"`) for rolling stats |
| `playId` | one of | the play's record key. Used as-is if a valid record key, else sanitised. |
| `derive` | one of | `{"startedAt":"<iso>","seed":"<string>"}` → the sidecar computes a stable TID-shaped id (same inputs → same id on every run/participant). Use this when you don't have an id of your own. |
| `additionalVersions` | no | extra `name → version` entries for `versions.additional`, on top of the emitter (from `client`) and the package, which are added automatically |

Provide **exactly one** of `playId` or `derive`.

```json
{"ok":true,"type":"opened","rkey":"my-run-123"}
```

`open` does **not** write to the PDS. The record is created by the first `commit`,
held back by a short **initial delay** (default 15s, configurable) so the opening setup
(`setup.set`, modifiers, first metrics) batches into one record instead of a
create-then-immediately-update. The record therefore appears a few seconds into the run
— or sooner if an `outcome.set`/`finish` arrives first (those always flush). The
emitter's `client` and the package version are written into `versions.additional` on
that first write.

### 5.2 `commit` — write the buffered mutations

```json
{"cmd":"commit"}
```

```json
{"ok":true,"type":"committed","status":"published","uri":"at://did:plc:…/…/my-run-123"}
```

`status` ∈:
- `published` — written to the PDS (`uri` included);
- `queued` — offline; persisted to the outbox, will flush on next login;
- `deferred` — **batching / rate-limited**: the sidecar holds a play's first write for a
  short initial delay (default 15s) so the opening setup batches into one record, then
  writes at most once per minute thereafter (both configurable). Nothing was written this
  time and **your buffered ops are kept**; they publish on a later `commit` once the
  delay/window elapses — or **immediately** when the buffer includes an `outcome.set` or
  `finish`. Keep committing on your cadence; don't resend;
- `pending` — your `clientId` isn't approved yet: **nothing was written and the
  buffered mutations are kept**. The first `pending` commit asks the player to approve
  the client; once they do, commit again (on your normal cadence) to publish it;
- `dropped` — unqueueable (no known DID) and discarded (carries a `warn`);
- `noop` — nothing was buffered since the last commit.

Because a `commit` may be `deferred` (or `pending`) and keep accumulating, treat each
mutation as fire-and-forget: send it once, keep committing on your cadence, and let the
sidecar decide when to write. A run-ending `outcome.set` / `finish` always forces the
final write out promptly.

### 5.3 Mutating commands

All of these **buffer** (return `ok` with no network) and require an active play
(`error:"noSession"` otherwise). Each maps 1:1 to a `PlayUpdate` method.

| `cmd` | fields | maps to | notes |
|-------|--------|---------|-------|
| `metric.set` | `name`, `value` (int), `scale`? (int ≥0) | `SetMetric` | real value = `value / 10^scale` |
| `metric.update` | `name`, `value` (int), `op` ∈ `add`·`subtract`·`min`·`max` | `UpdateMetric` | resolved against the real value at write time |
| `setting.set` | `name`, `value` (string·int·bool·object) | `SetSetting` | **arrays rejected** → `invalidValue`; wrap in an object |
| `setup.set` | `mode`?, `seed`?, `character`?, `difficulty`? (int) | `SetSetup` | only the given fields are written (merge; later fills don't clobber) |
| `setup.modifier` | `id`, `name`?, `value`? | `AddModifier` | deduped by `id` |
| `acquisition.add` | `entry` (object, needs `id`; `instanceId`? for idempotent re-emit) | `AddAcquisition` | |
| `acquisition.set` | `items` (array of objects, each needs `id`) | `SetAcquisitions` | replaces all acquisitions |
| `route.arrive` | `id`, `instanceId`?, `name`?, `arrivedAt`? (iso) | `RouteArrive` | give `instanceId` to make it idempotent and let `route.leave` target it |
| `route.leave` | `id`, `instanceId`?, `leftAt`? (iso) | `RouteLeave` | closes the matching open stop |
| `outcome.set` | `type`, `cause`? | `SetOutcome` | top-level end-of-play marker |
| `participants.set` | `participants` (array of objects) | `SetParticipants` | a `steam` field must be a SteamID64 (17-digit) |
| `finish` | `endedAt` (iso), `durationSeconds` (int) | `Finish` | sets `endedAt` + `duration` |
| `state.replace` | `type` (nsid), `entry` (object) | `ReplaceState` | singleton escape hatch |
| `state.upsert` | `type` (nsid), `entry` (object, needs `id`) | `UpsertState` | keyed escape hatch |
| `state.append` | `type` (nsid), `entry` (object, needs `id`; `instanceId`?) | `AppendState` | instanced escape hatch |

Conventions:
- **Numbers:** metric `value` is an integer. For fractional metrics use `scale`
  (e.g. `metric.set pct 8734 scale 4` is `0.8734`). `difficulty` and
  `durationSeconds` are integers.
- **Timestamps:** ISO-8601 UTC strings (e.g. `2026-06-14T12:00:00Z`). Where a
  timestamp is optional and omitted, the sidecar stamps the current time.
- **`name` keys** (metric/setting) are conventionally **camelCase**. A non-camelCase
  key still succeeds but the reply carries a `warn` (the runtime counterpart of the
  library's BAG001 analyzer).

See **§10** for the full catalogue of default `state[]` types (objectives, unlocks,
discoveries, standings, party members, …) and which op writes each.

### 5.4 Utility

| `cmd` | reply | meaning |
|-------|-------|---------|
| `ping` | `{"ok":true,"type":"pong"}` | liveness check; valid any time after `hello` |
| `bye` | `{"ok":true}` then close | graceful disconnect (does **not** commit) |

## 6. Error codes

`ok:false` always carries an `error` code and a `message`. The connection stays open
for everything except handshake failures.

| code | when |
|------|------|
| `unsupportedProtocol` | `hello.protocol` not supported (connection closes) |
| `notReady` | a command arrived before a successful `hello` |
| `unknownCommand` | unrecognised `cmd` |
| `missingField` | a required field is absent or the wrong JSON type |
| `invalidValue` | a field is present but invalid (e.g. array setting value, bad AT-URI, unknown `op`) |
| `noSession` | a mutation/`commit`/`finish` arrived with no active play (no `open`) |
| `internal` | the sidecar hit an unexpected error (details in `message`) |

A bad mutating command is rejected (`ok:false`) **without** discarding the buffer or
the session — fix it and continue.

## 7. Security & configuration — pairing, not secrets

- **Loopback + one-time approval.** The sidecar binds `127.0.0.1` only. Any local
  process may connect and handshake, but **none can publish until the player approves
  its `clientId`**: the first `commit` from an unknown client raises a prompt in the
  sidecar's terminal, and on approval the `clientId` is remembered (and revocable).
  There is no shared secret to distribute or leak.
- **Generate a stable `clientId` once.** On first run, mint a random, hard-to-guess id
  (e.g. 16+ random bytes as hex) and persist it in your emitter's own storage; reuse it
  on every connection so the player only approves you once. Treat it like a credential —
  there's no cryptographic proof of possession, so a process that learns your `clientId`
  can publish as you. Don't ship one shared `clientId` baked into every install; let
  each install generate its own.
- **Discover the `port`** from the sidecar's `config.json` (key `port`, default
  `17872`) if you're co-located, or make it user-configurable. Approved clients are
  listed under `approvedClients`; deleting an entry there revokes that client.
- **Credentials never cross the wire.** The emitter only sends its `clientId` and play
  data; the player's handle / app-password stay inside the sidecar and are never
  exposed to emitters.

## 8. Worked example — a Super Mario Land run

```jsonc
// → emitter, ← sidecar.  (id fields omitted for brevity)
→ {"cmd":"hello","protocol":1,"clientId":"a1b2c3d4e5f6a7b8","client":"supermarioland-atproto/0.1"}
← {"ok":true,"type":"ready","protocol":1,"approval":"pending","auth":"ok","did":"did:plc:abc…","signing":false}

→ {"cmd":"open","game":"at://did:plc:gb/dev.cartridge.game/super-mario-land",
   "gameVersion":"jue-1.1","source":"mgba","derive":{"startedAt":"2026-06-14T12:00:00Z","seed":"sml"}}
← {"ok":true,"type":"opened","rkey":"3ku7a…"}             // open doesn't write; the first commit creates the record

→ {"cmd":"setup.set","character":"mario"}
← {"ok":true}
→ {"cmd":"route.arrive","id":"1-1","instanceId":"1-1@0"}
← {"ok":true}
→ {"cmd":"metric.update","name":"score","value":400,"op":"max"}
← {"ok":true}
→ {"cmd":"commit"}
← {"ok":true,"type":"committed","status":"pending"}        // first publish — awaiting approval; buffer kept

// …the sidecar's terminal shows a one-time approval prompt; the player types y…

→ {"cmd":"commit"}                                          // retried on the usual cadence
← {"ok":true,"type":"committed","status":"published","uri":"at://did:plc:abc…/…/3ku7a…"}

// …player progresses; the script watches WRAM and emits edges…
→ {"cmd":"route.leave","id":"1-1","instanceId":"1-1@0"}
← {"ok":true}
→ {"cmd":"route.arrive","id":"1-2","instanceId":"1-2@0"}
← {"ok":true}
→ {"cmd":"metric.update","name":"coins","value":12,"op":"add"}
← {"ok":true}
→ {"cmd":"metric.set","name":"lives","value":3}
← {"ok":true}
→ {"cmd":"commit"}
← {"ok":true,"type":"committed","status":"published","uri":"at://…"}

// …lives hit zero…
→ {"cmd":"outcome.set","type":"failed","cause":"fell"}
← {"ok":true}
→ {"cmd":"finish","endedAt":"2026-06-14T12:18:42Z","durationSeconds":1122}
← {"ok":true}
→ {"cmd":"commit"}
← {"ok":true,"type":"committed","status":"published","uri":"at://…"}
→ {"cmd":"bye"}
← {"ok":true}
// sidecar closes the socket
```

## 9. Implementer's checklist (emitter side)

1. On first run, generate a stable `clientId` and persist it in your own storage; reuse
   it on every connection.
2. Connect to `127.0.0.1:<port>` (read `port` from the sidecar's config, or make it
   configurable). Send `hello` with your `clientId` and `client` (`name/version`); wait
   for `ready`. Abort on `ok:false`.
3. If `ready.approval` is `pending`, tell the player to approve this client in the
   sidecar's terminal — it's a one-time step and publishing waits until they do.
4. On new run detected: send `open` (with `playId` or `derive`). `open` itself doesn't
   write; your first `commit` creates the record a few seconds in (batching the opening
   setup), or immediately on `outcome.set`/`finish`.
5. Translate observed state changes into the §5.3 mutating commands. Prefer
   `metric.update` with `max`/`min`/`add` over `metric.set` for noisy/resampled
   values — those ops are idempotent under replay.
6. `commit` on a cadence (e.g. every few seconds, or on each meaningful edge). On
   `status:"deferred"` or `"pending"`, keep playing and keep committing — the buffer is
   preserved (rate-limit window, or awaiting approval), and a later commit publishes it.
7. On run end: `outcome.set`, `finish`, `commit`, then `bye`.
8. Read each reply; surface `approval`, `auth`, and any `warn`/`error` to the player.

## 10. Default state types & operations (reference)

A play record's `state[]` is an **open union** of typed entries. The library ships the
default entry types below — lexicons under `games.gamesgamesgamesgames.experimental.state.`
(the tables show the suffix after that prefix). **Cardinality is implied by an entry's
shape** and decides how a write merges:

- **singleton** (declares neither `id` nor `instanceId`) — one entry per `$type`, replaced wholesale.
- **keyed** (declares `id` only) — one entry per (`$type`, `id`), upserted by `id`.
- **instanced** (declares `id` + `instanceId`) — appended in order, deduped by `instanceId`.

Types with a **typed command** are written directly. The rest use a **generic op**
(`state.replace` / `state.upsert` / `state.append`) — pass the full NSID as `type` and the
entry's fields as `entry`; the sidecar stamps `$type`, so don't put it in `entry`:

```json
{"cmd":"state.upsert","type":"games.gamesgamesgamesgames.experimental.state.objective",
 "entry":{"id":"clearWorld1","status":"completed","current":3,"target":3}}
```

Common to all keyed/instanced entries: a stable camelCase **`id`**, an optional display
**`name`**, and an optional **`extra`** array of game-specific sub-objects (rarely needed).
All timestamps are ISO-8601 UTC.

### Keyed — upsert by `id`

| type (`…state.`) | command(s) | required (besides `id`) | other fields |
|---|---|---|---|
| `metric` | `metric.set` / `metric.update` | `value` (int) | `name`; `scale` (int ≥0 — real value = `value`/10^`scale`); `unit`; `kind` ∈ `count`·`currency`·`score`·`level` |
| `setting` | `setting.set` | one of `value`/`intValue`/`boolValue`/`dataValue` (object) | `name` |
| `objective` | `state.upsert` | `status` ∈ `offered`·`active`·`completed`·`failed`·`abandoned` | `name`; `current`, `target` (int); `acceptedAt`, `resolvedAt` |
| `unlock` | `state.upsert` | — | `kind`; `name`; `unlockedAt` |
| `discovery` | `state.upsert` | — | `kind`; `name`; `discoveredAt` |
| `standing` | `state.upsert` | `value` or `tier` | `name`; `value` (int); `tier` |

### Instanced — append, dedupe by `instanceId`

| type (`…state.`) | command(s) | fields (besides `id`) |
|---|---|---|
| `acquisition` | `acquisition.add` (one) / `acquisition.set` (replace all) | `kind`; `name`; `via` ∈ `starting`·`pickup`·`purchased`·`crafted`·`reward`·`metaUnlock`; `useCount` (int); `addedAt`, `lostAt`; `instanceId` |
| `routeStop` | `route.arrive` / `route.leave` | `name`; `seq` (int); `arrivedAt`, `leftAt`; `instanceId` |
| `partyMember` | `state.append` | `name`; `role`; `level` (int); `equipment` (string[] of item ids); `joinedAt`, `leftAt`; `instanceId` |

### Singleton — replace the lone entry

| type (`…state.`) | command(s) | fields |
|---|---|---|
| `setup` | `setup.set` (`mode`, `seed`, `character`, `difficulty` (int) — merges named fields) · `setup.modifier` (`id`, `name?`, `value?` — modifiers deduped by `id`) | as listed |

### Top-level record fields (not in `state[]`)

| field | command | shape |
|---|---|---|
| `outcome` | `outcome.set` | `type` ∈ `failed`·`abandoned`·`succeeded` (free text allowed); `cause` (game-specific, e.g. the boss's id) |
| `participants` | `participants.set` | array of `{atproto`(DID)`, steam`(SteamID64 decimal string)`}` — at least one of the two per entry |
| `endedAt` + `duration` | `finish` | `endedAt` (ISO), `durationSeconds` (int) |

**Game-specific types:** mint your own `$type` and write it with the generic op whose
cardinality matches your lexicon's shape — declares `id`+`instanceId` → `state.append`;
`id` only → `state.upsert`; neither → `state.replace`.
</content>
</invoke>
