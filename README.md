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

const string PlayCollection = "games.gamesgamesgamesgames.actor.play";
var gameRef = new JsonObject { ["uri"] = "at://did:web:gamesgamesgamesgames.games/games.gamesgamesgamesgames.game/3mglj4k2edl2l" };

string rkey = Tid.FromPlayThrough(startedAtUnixSeconds, runSeed);   // runSeed: ulong or string

// `stats` is required by the lexicon — get/create the rolling-stats record up front.
string statsUri = await atproto.Stats.EnsureAsync(gameRef, source: "steam");

var play = new JsonObject
{
    ["$type"]     = PlayCollection,
    ["game"]      = gameRef["uri"]!.DeepClone(),
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

**On significant progress**, mutate `play` (`updatedAt`, `progress`, `acquisitions`, …) and PUT again with the **same rkey** — it replaces the previous state. Throttling/dirty-bit logic is yours; the package just publishes when asked.

**At the end**, set the outcome and roll the stats:

```csharp
play["endedAt"]  = endedAtIso;
play["duration"] = durationSeconds;
((JsonObject)play["progress"]!)["outcome"] = new JsonObject { ["type"] = "failed", ["cause"] = "bygone-effigy" };

await atproto.Stats.EnsureAndUpdateAsync(gameRef, "steam", durationSeconds, endedAtIso);  // adds minutes, bumps lastPlayed
await atproto.Records.PutAsync(PlayCollection, rkey, play);
```

`PutAsync` returns a `PutResult` (`Published` / `Queued` / `Dropped`). On game crash, anything already queued is on disk and flushes next launch — there is no shutdown ceremony.

### Optional extras

- **Signed records** — pass a `SigningKey` in the options; every record then carries a badge.blue-style `signatures` entry over its CID, verifiable by the published public `did:key`. Records still publish fine unsigned.
- **Save-fork lineage** — `StrongRef.Create(uri, cid)` (or `StrongRef.FromRecordBody(uri, body)` to compute the CID) for the lexicon's `forkedFrom`.
- **Multiplayer backfill** — `atproto.Steam.LookupDidAsync(steamId64)` resolves a SteamID64 to a DID for `playingWith[].atproto`.
- **Achievements** — `atproto.Achievements.TryClaim(id)` de-dups unlock writes within a session; publish to your own NSID via `atproto.Records.PutAsync`.
- **Other records** — `Records.PutAsync` is collection-agnostic; pass any NSID (lobby records, achievement logs, your stats record, …).

## Data structure

This package uses the [games.gamesgamesgamesgames lexicons](https://gamesgamesgamesgames.games), with optional extensibility for the game you're integrating with.

### Play stats & achievements

Overall play stats are posted with the [`games.gamesgamesgamesgames.actor.stats`](https://lexicon.garden/lexicon/did:web:gamesgamesgamesgames.games/games.gamesgamesgamesgames.actor.stats/docs) lexicon. It includes achievements, total play time, last played time, and similar.


### Play-through details

Statistics for a particular play-through of a game. For a rogue-like there may be many, many of these. Stored in a `games.gamesgamesgamesgames.actor.play` record.

> [!NOTE]
> This is a proposed lexicon not currently in the `games.gamesgamesgamesgames` suite.

The `rkey` of the record for the run has some constraints, to make updates & finding multiplayer entries easier. The `rkey` must be:
- Consistent between resumes of the same play (including save-and-quits, if the game supports it)
  - For games with save slots that can fork play, this should be a lineage-based save ID
- Consistent for each multiplayer participant
- Unique across multiple runs of the game

This package's code has a helper for generatring a suitable `rkey` from the start time of a rogue-like run and its seed, as games like these often both persist this data across resumes and propagate those values between participants in multiplayer modes.

See the [.actor.play lexicon here](./lexicons/games/gamesgamesgamesgames/actor/play.json), an example for [Slay the Spire 2](https://cartridge.dev/game/slay-the-spire-ii) is below:

```jsonc
{
  "$type": "games.gamesgamesgamesgames.actor.play",
  "game": "at://did:web:gamesgamesgamesgames.games/games.gamesgamesgamesgames.game/3mglj4k2edl2l",
  "stats": "at://did:plc:ephkzpinhaqcabtkugtbzrwu/games.gamesgamesgamesgames.actor.stats/3mjrmxutfln2h",
  // Multiplayer participants
  // Atproto ID can be looked up with dev.keytrace.reverseLookup
  // Because rkey is the same across participants, lookup of other players' records is trivial
  "playingWith": [
    { "atproto": "did:plc:sy4qmi35imvto5yjhuwdeozk", "steam": "76561198009200312"},
    { "atproto": "did:plc:re253gupqudlfcugvxhdlr7v", "steam": "76561199436603652"}
  ],
  // Attributes that are configured before the run begins
  "settings": {
    // The seed of the run, if using seeded play
    "seed": "AXK36RTM4T",
    // sts2
    "character": "silent",
    // Representing Ascension level 1
    "difficulty": 1
  },
  // Open ended object for single values which accumulate or alter through the game as a measure of how well you're doing
  "progress": {
    // The stops along the route so far, each a #routeStop (game-specific id, optional name & timing)
    "route": [
      { "$type": "games.gamesgamesgamesgames.actor.play#routeStop",
        "id": "monster:nibbit", "name": "Nibbit",
        "startedAt": "2026-04-18T13:30:50.221Z", "endedAt": "2026-04-18T13:41:20.221Z" },
      { "$type": "games.gamesgamesgamesgames.actor.play#routeStop",
        "id": "monster:twig-slimes", "name": "Twig Slimes",
        "startedAt": "2026-04-18T13:41:20.221Z", "endedAt": "2026-04-18T13:53:10.221Z" },
      { "$type": "games.gamesgamesgamesgames.actor.play#routeStop",
        "id": "marketplace", "name": "Marketplace",
        "startedAt": "2026-04-18T13:53:10.221Z", "endedAt": "2026-04-18T13:58:40.221Z" },
      // endedAt is set here because the run ended at this stop; leave it unset while a stop is still current
      { "$type": "games.gamesgamesgamesgames.actor.play#routeStop",
        "id": "boss:bygone-effigy", "name": "Bygone Effigy",
        "startedAt": "2026-04-18T14:05:30.221Z", "endedAt": "2026-04-18T14:18:01.221Z" }
    ],
    // What happened at the end of the game
    // Should be unset for in-progress runs
    "outcome": {
      // The overarching outcome type: failed, abandoned, succeeded
      "type": "failed",
      // An id for the reason behind the outomce, eg. the win scenario, or boss that killed you
      "cause": "bygone-effigy"
    },
    // Other game-specific attributes
    "act": 2,
    "floor": 19,
    "hp": 34,
    "hpMax": 89,
    "turns": 49,
    "gold": { "earned": 319, "spent": 241 },
  },
  // Things you've acquired inside the play-through.
  "acquisitions": [
    // You can use the generic type, which requires an id, and has an optional name, addedAt & useCount
    { "$type": "games.gamesgamesgamesgames.actor.play#gameItem",
      "id": "silent.strike+/corrupted",
      "kind": "card",
      "name": "Strike (Upgraded, Corrupted)",
      "useCount": 40,
      "addedAt": "2026-04-18T13:38:20.221Z",
      "extra": [
        { "$type": "com.megacrit.sts2.card",
          "deck": "silent",
          "name": "strike",
          "upgraded": true,
          "enchantment": "corrupted" }]},
    { "$type": "games.gamesgamesgamesgames.actor.play#gameItem",
      "kind": "relic",
      "id": "cracked_core",
      "name": "Cracked Core",
      "addedAt": "2026-04-18T13:52:10.221Z" }
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