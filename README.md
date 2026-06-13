# Atproto Gaming Core

A package for C#/dotnet games which can be used as a core to atproto functionality, eg. posting details of gameplay to a player's PDS. For use in mods, or in game development.

The package (`ByJP.AtprotoGaming.Core`) targets **`netstandard2.0`**, so the same DLL loads under BepInEx / .NET Framework 4.7.2 (Unity Mono mods) and under .NET 9 (Godot.NET, modern games) alike. It references no game engine — you keep the engine hooks; it does the atproto plumbing.

## Using the package

You bring three tiny adapters and your record bodies; the package handles identity, login, token refresh, an on-disk retry queue, deterministic record keys, rolling stats, and (optionally) signing.

### 1. Install

```sh
dotnet add package ByJP.AtprotoGaming.Core
```

### 2. Wire three adapters

The package talks to your runtime only through these. None is more than a few lines.

```csharp
using ByJP.AtprotoGaming.Core;
using ByJP.AtprotoGaming.Core.Adapters;

// Logging — point it at your runtime's logger.
sealed class MyLog : ILogSink
{
    public void Info(string m)  => MyGameLogger.Info($"[atproto] {m}");
    public void Warn(string m)  => MyGameLogger.Warn($"[atproto] {m}");
    public void Error(string m, System.Exception? e = null) => MyGameLogger.Error($"[atproto] {m}", e);
}
ILogSink log = new MyLog();

// Where config.json + the outbox live — usually next to your plugin DLL.
IFileSystem fs = FileSystem.NextTo<MyPlugin>();   // or FileSystem.At("/some/dir")

// IClock defaults to wall-clock; you rarely need to supply one.
```

> BepInEx: wrap your `ManualLogSource`. Godot: wrap `GD.Print`/`GD.PushWarning`. Plain .NET: use the bundled `ConsoleLogSink`.

### 3. Load config and construct the client once

`config.json` is created on first run with a visible "not configured yet" banner in your log; the player fills in their handle + [app password](https://bsky.app/settings/app-passwords). Subclass `CoreConfig` if you want your own settings in the same file.

```csharp
var config = ConfigStore<CoreConfig>.LoadOrCreate(fs, log);

var atproto = new AtprotoGamingClient(new AtprotoGamingOptions
{
    FileSystem = fs,
    Log        = log,
    Config     = config,
    // SigningKey = SigningKey.FromDidKey("did:key:z…private", "your.game.mod#attestation"), // optional, see below
});
```

### 4. Boot login off the main thread, and react to state

```csharp
_ = System.Threading.Tasks.Task.Run(atproto.LoginAsync);   // resolves identity, logs in, drains the queue

atproto.Auth.Changed += () =>
    UpdateBadge(atproto.Auth.Status, atproto.Auth.Error);  // Unconfigured / Checking / Ok / Failed / Offline
```

The client never starts its own threads or timers — you decide when work runs. If the network is down at boot it goes `Offline` (seeded from the cached DID) and queues; queued records flush automatically on the next successful login and after each successful publish.

### 5. Publish a play-through

You assemble the record body (a `System.Text.Json.Nodes.JsonObject`) against the [lexicon](#play-through-details). Derive a **stable rkey** so resumes and every multiplayer participant converge on one record:

```csharp
using System.Text.Json.Nodes;

const string PlayCollection = "games.gamesgamesgamesgames.experimental.actor.play";
const string game = "at://did:web:gamesgamesgamesgames.games/games.gamesgamesgamesgames.game/3mglj4k2edl2l";

string rkey = Tid.FromPlayThrough(startedAtUnixSeconds, runSeed);   // runSeed: ulong or string

// `stats` is required by the lexicon — get/create the rolling-stats record up front.
string statsUri = await atproto.Stats.EnsureAsync(game, StatsSource.Steam);

var play = new JsonObject
{
    ["$type"]     = PlayCollection,
    ["game"]      = game,
    ["stats"]     = statsUri,
    ["startedAt"] = startedAtIso,
    ["updatedAt"] = startedAtIso,
    ["versions"]  = new JsonObject
    {
        ["game"]       = gameVersion,
        ["additional"] = new JsonArray { new JsonObject { ["name"] = "my-mod", ["version"] = "1.2.3" } },
        // the package appends its own atproto-gaming-dotnet entry here automatically
    },
};

await atproto.Records.PutAsync(PlayCollection, rkey, play);
```

**On significant progress**, mutate `play` (`updatedAt`, and the entries in the `state[]` array) and PUT again with the **same rkey** — it replaces the previous state. Throttling/dirty-bit logic is yours; the package just publishes when asked. (Assembling `state[]` by hand is fiddly — the [`PlaySession` transaction API below](#higher-level-declare-what-changed-transactional-optimistic-locking) builds it for you.)

**At the end**, set the outcome (a top-level field) and roll the stats:

```csharp
play["endedAt"]  = endedAtIso;
play["duration"] = durationSeconds;
play["outcome"]  = new JsonObject { ["type"] = "failed", ["cause"] = "bygone-effigy" };

await atproto.Stats.EnsureAndUpdateAsync(game, StatsSource.Steam, durationSeconds, endedAtIso);  // adds minutes, bumps lastPlayed
await atproto.Records.PutAsync(PlayCollection, rkey, play);
```

`PutAsync` returns a `PutResult` (`Published` / `Queued` / `Dropped`). On game crash, anything already queued is on disk and flushes next launch — there is no shutdown ceremony.

### Higher-level: declare what changed (transactional, optimistic locking)

The calls above hand over the *whole* record each time. If you'd rather declare
**changes** — "score is now N", "item acquired", "finished" — open a `PlaySession`
and batch them like a database transaction: `BeginUpdate`, record changes (in
memory, no network), then `CommitAsync` to write them all in **one** optimistic
read-modify-write (compare-and-swap on the record's CID, refetching and
re-applying if it changed elsewhere, queueing if offline).

```csharp
var play = atproto.OpenPlay(
    // a stable id: derive a TID from start time + run seed, or pass your own save-slot id
    playId: PlaySession.DerivePlayID(DateTimeOffset.UtcNow, runSeed),
    game: "at://did:web:gamesgamesgamesgames.games/games.gamesgamesgamesgames.game/3mglj4k2edl2l",
    gameVersion: "0.107.0",
    source: StatsSource.Steam,
    additionalVersions: new Dictionary<string, string> { ["my-mod"] = "1.2.3" });
// startedAt is captured now and written on create; stats is resolved/inserted at write time.

// A transaction gathers changes (in memory) across many event handlers / frames…
// The first commit creates the record from the seed if it doesn't exist yet.
var tx = play.BeginUpdate();
tx.SetSetup(seed: runSeed.ToString(), character: "silent", difficulty: 1)  // pre-run choices (merged)
  .SetMetric("score", 1234)                                      // absolute numeric value
  .UpdateMetric("kills", 1, ProgressOp.Add)                      // int-only delta, resolved at write
  .SetSetting("gold", new JsonObject { ["earned"] = 12, ["spent"] = 4 })  // arbitrary nested value
  .AddAcquisition(new JsonObject { ["id"] = "relic.cracked_core", ["kind"] = "relic" })
  .RouteArrive("boss:effigy", name: "Bygone Effigy");
// …then flush them as a single record write at, say, end of stage:
await tx.CommitAsync();   // one PUT, updatedAt bumped once

// At the end of the run — another transaction:
var final = play.BeginUpdate();
final.SetOutcome("failed", "effigy");
final.Finish(endedAtIso, durationSeconds);   // also call Stats.EnsureAndUpdateAsync at the end
await final.CommitAsync();
```

Helper calls are synchronous and just record the change; nothing reaches the PDS
until `CommitAsync` (one PUT per commit). The `seed` creates the record on the
first commit (or while offline). For records other than the play lexicon, drop to
the generic editor: `atproto.Records.Edit(collection, rkey, seed).ApplyAsync(r => …)`.

### Optional extras

- **Signed records** — pass a `SigningKey` in the options; every record then carries a badge.blue-style `signatures` entry over its CID, verifiable by the published public `did:key`. Records still publish fine unsigned.
- **Save-fork lineage** — `StrongRef.Create(uri, cid)` (or `StrongRef.FromRecordBody(uri, body)` to compute the CID) for the lexicon's `forkedFrom`.
- **Multiplayer backfill** — `atproto.Steam.LookupDidAsync(steamId64)` resolves a SteamID64 to a DID for `participants[].atproto`.
- **Achievements** — `atproto.Achievements.TryClaim(id)` de-dups unlock writes within a session; publish to your own NSID via `atproto.Records.PutAsync`.
- **Other records** — `Records.PutAsync` is collection-agnostic; pass any NSID (lobby records, achievement logs, your stats record, …).

## Data structure

This package uses the [games.gamesgamesgamesgames lexicons](https://gamesgamesgamesgames.games), with optional extensibility for the game you're integrating with.

### Play stats & achievements

Overall play stats are posted with the [`games.gamesgamesgamesgames.actor.stats`](https://lexicon.garden/lexicon/did:web:gamesgamesgamesgames.games/games.gamesgamesgamesgames.actor.stats/docs) lexicon. It includes achievements, total play time, last played time, and similar.


### Play-through details

Statistics for a particular play-through of a game. For a rogue-like there may be many, many of these. Stored in a `games.gamesgamesgamesgames.experimental.actor.play` record.

> [!NOTE]
> This is a proposed lexicon not currently in the `games.gamesgamesgamesgames` suite.

The `rkey` of the record for the run has some constraints, to make updates & finding multiplayer entries easier. The `rkey` must be:
- Consistent between resumes of the same play (including save-and-quits, if the game supports it)
  - For games with save slots that can fork play, this should be a lineage-based save ID
- Consistent for each multiplayer participant
- Unique across multiple runs of the game

This package's code has a helper for generatring a suitable `rkey` from the start time of a rogue-like run and its seed, as games like these often both persist this data across resumes and propagate those values between participants in multiplayer modes.

See the [.experimental.actor.play lexicon here](./lexicons/games/gamesgamesgamesgames/experimental/actor/play.json), an example for [Slay the Spire 2](https://cartridge.dev/game/slay-the-spire-ii) is below:

```jsonc
{
  "$type": "games.gamesgamesgamesgames.experimental.actor.play",
  "game": "at://did:web:gamesgamesgamesgames.games/games.gamesgamesgamesgames.game/3mglj4k2edl2l",
  "stats": "at://did:plc:ephkzpinhaqcabtkugtbzrwu/games.gamesgamesgamesgames.actor.stats/3mjrmxutfln2h",
  // Multiplayer participants (a top-level field)
  // Atproto ID can be looked up with dev.keytrace.reverseLookup
  // Because rkey is the same across participants, lookup of other players' records is trivial
  "participants": [
    { "atproto": "did:plc:sy4qmi35imvto5yjhuwdeozk", "steam": "76561198009200312" },
    { "atproto": "did:plc:re253gupqudlfcugvxhdlr7v", "steam": "76561199436603652" }
  ],
  // How the run ended (a top-level field); unset while in progress
  "outcome": {
    // The overarching outcome type: failed, abandoned, succeeded
    "type": "failed",
    // An id for the reason behind the outcome, eg. the win scenario, or boss that killed you
    "cause": "bygone-effigy"
  },
  // Everything else lives in one open-union state[] of typed entries. Each entry's
  // $type is its own lexicon; cardinality is implied by shape — id+instanceId =>
  // instanced (appended, deduped by instanceId), id only => keyed (upserted by id),
  // neither => singleton (replaced). Entries are kept in last-edited order.
  "state": [
    // setup: a singleton of choices configured before the run begins
    {
      "$type": "games.gamesgamesgamesgames.experimental.state.setup",
      "seed": "AXK36RTM4T",   // the run seed, if using seeded play
      "character": "silent",  // sts2
      "difficulty": 1         // Ascension level 1
    },
    // metric: a numeric indicator that accumulates/alters through the run, keyed by id
    { "$type": "games.gamesgamesgamesgames.experimental.state.metric", "id": "act", "value": 2 },
    { "$type": "games.gamesgamesgamesgames.experimental.state.metric", "id": "floor", "value": 19 },
    { "$type": "games.gamesgamesgamesgames.experimental.state.metric", "id": "hp", "value": 34 },
    { "$type": "games.gamesgamesgamesgames.experimental.state.metric", "id": "hpMax", "value": 89 },
    { "$type": "games.gamesgamesgamesgames.experimental.state.metric", "id": "turns", "value": 49 },
    // setting: an arbitrary game-specific value; a nested object goes in dataValue
    { "$type": "games.gamesgamesgamesgames.experimental.state.setting",
      "id": "gold", "dataValue": { "earned": 319, "spent": 241 } },
    // acquisition: something acquired in the run (instanced; instanceId dedupes
    // re-emits). Can carry game-specific detail in the open `extra` union.
    { "$type": "games.gamesgamesgamesgames.experimental.state.acquisition",
      "id": "silent.strike+/corrupted",
      "kind": "card",
      "name": "Strike (Upgraded, Corrupted)",
      "via": "pickup",
      "useCount": 40,
      "addedAt": "2026-04-18T13:38:20.221Z",
      "instanceId": "0",
      "extra": [
        { "$type": "com.megacrit.sts2.card",
          "deck": "silent",
          "name": "strike",
          "upgraded": true,
          "enchantment": "corrupted" }]},
    { "$type": "games.gamesgamesgamesgames.experimental.state.acquisition",
      "id": "cracked_core",
      "kind": "relic",
      "name": "Cracked Core",
      "via": "pickup",
      "addedAt": "2026-04-18T13:52:10.221Z",
      "instanceId": "1" },
    // routeStop: a stop along the route (instanced). arrivedAt/leftAt replace the
    // old startedAt/endedAt; leave leftAt unset while a stop is still current.
    { "$type": "games.gamesgamesgamesgames.experimental.state.routeStop",
      "id": "monster:nibbit", "name": "Nibbit", "instanceId": "0",
      "arrivedAt": "2026-04-18T13:30:50.221Z", "leftAt": "2026-04-18T13:41:20.221Z" },
    { "$type": "games.gamesgamesgamesgames.experimental.state.routeStop",
      "id": "monster:twig-slimes", "name": "Twig Slimes", "instanceId": "1",
      "arrivedAt": "2026-04-18T13:41:20.221Z", "leftAt": "2026-04-18T13:53:10.221Z" },
    { "$type": "games.gamesgamesgamesgames.experimental.state.routeStop",
      "id": "marketplace", "name": "Marketplace", "instanceId": "2",
      "arrivedAt": "2026-04-18T13:53:10.221Z", "leftAt": "2026-04-18T13:58:40.221Z" },
    { "$type": "games.gamesgamesgamesgames.experimental.state.routeStop",
      "id": "boss:bygone-effigy", "name": "Bygone Effigy", "instanceId": "3",
      "arrivedAt": "2026-04-18T14:05:30.221Z", "leftAt": "2026-04-18T14:18:01.221Z" }
  ],
  // When the play-through started
  "startedAt": "2026-04-18T13:30:44.221Z",
  // Should always be present (even if identical to startedAt); may be later than endedAt
  "updatedAt": "2026-04-18T14:18:01.222Z",
  // When the play-through ended (ie. when the player stopped playing)
  "endedAt": "2026-04-18T14:18:01.221Z",
  // How long has been spent actually playing the game in seconds (would be shorter than `endedAt - startedAt` if the game was paused)
  "duration": 2837,
  "versions": {
    "game": "0.107.0",
    "additional": [
      {"name": "sts2-atproto", "version": "0.16.1"},
      {"name": "atproto-gaming-dotnet", "version": "0.0.1"}
    ]
  }
}
```