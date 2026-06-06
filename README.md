# Atproto Gaming Core

A package for C#/dotnet games which is the core behind posting details of gameplay to atproto. For use in mods, or in game development.

## Data structure

This package uses the [games.gamesgamesgamesgames lexicons](https://gamesgamesgamesgames.games), with optional extensibility for the game you're integrating with.

### Play stats & achievements

Overall play stats are posted with the [`games.gamesgamesgamesgames.actor.stats`](https://lexicon.garden/lexicon/did:web:gamesgamesgamesgames.games/games.gamesgamesgamesgames.actor.stats/docs) lexicon. It includes achievements, total play time, last played time, and similar.


### Run stats

For games which can be played over multiple 'runs' (particularly rogue-like games), statistics for a particular run are stored in a `games.gamesgamesgamesgames.actor.run` record.

> [!NOTE]
> This is a proposed lexicon not currently in the `games.gamesgamesgamesgames` suite.

The `rkey` of the record for the run has some constraints, to make updates & finding multiplayer entries easier. The `rkey` must be:
- Consistent between resumes of the same run (including save/resumes, if the game supports it)
- Consistent for each multiplayer participant
- Unique accross multiple runs of the game

This package's code has a helper for generatring a suitable `rkey` from the start time of the run, and the run's seed, as most rogue-like games both persist this data across resumes, and propagate those values between participants in multiplayer modes.

See the [.actor.run lexicon here](./lexicons/games/gamesgamesgamesgames/actor/run.json), an example for [Slay the Spire 2](https://cartridge.dev/game/slay-the-spire-ii) is below:

```jsonc
{
  "$type": "games.gamesgamesgamesgames.actor.run",
  "game": "at://did:web:gamesgamesgamesgames.games/games.gamesgamesgamesgames.game/3mglj4k2edl2l",
  "stats": "at://did:plc:ephkzpinhaqcabtkugtbzrwu/games.gamesgamesgamesgames.actor.stats/3mjrmxutfln2h",
  // Multiplayer participants
  // Atproto ID can be looked up with dev.keytrace.reverseLookup
  // Because rkey is the same across participants, lookup of other players' records is trivial
  "playingWith": [
    { "atproto": "did:plc:sy4qmi35imvto5yjhuwdeozk", "steam": "76561198009200312"}
  ],
  // The seed of the run, if using seeded play
  "seed": "AXK36RTM4T",
  // Attributes that are configured before the run begins
  "loadout":{
    // sts2
    "character": "silent",
    // Representing Ascension level 1
    "difficulty": 1
  },
  // Open ended object for single values which accumulate or alter through the game as a measure of how well you're doing
  "progress": {
    "act": 2,
    "floor": 19,
    "hp": 34,
    "hpMax": 89,
    "turns": 49,
    "gold": { "earned": 319, "spent": 241 }
  },
  // Things you've acquired inside the run.
  "acquisitions": [
    // You can use the generic type, which just requires an id, and has an optional name & useCount
    { "$type": "games.gamesgamesgamesgames.gameItem",
      "id": "card.silent.strike+/corrupted",
      "name": "Card: Strike (Upgraded, Corrupted)",
      "useCount": 40 },
    { "$type": "games.gamesgamesgamesgames.gameItem",
      "id": "relic.cracked_core",
      "name": "Relic: Cracked Core" },
    // Or you can define your own for more control over content/
    // This will be useful when WebTiles/default renderings for lexicons are more well defined/adopted.
    // Custom types should have an `id` field & an optional name/useCount
    { "$type": "com.megacrit.sts2.cards",
      "id": "silent.strike",
      "upgraded": true,
      "enchantment": "corrupted",
      "useCount": 40},
    { "$type": "com.megacrit.sts2.relic",
      "id": "cracked_core" }
  ],
  // Should always be present, and consistent (used to generate consistent rkey)
  "startedAt": "2026-04-18T13:30:44.221Z",
  // Should always be present (even if identical to startedAt); may be later than endedAt
  "updatedAt": "2026-04-18T14:18:01.222Z",
  // When the run ended (ie. when the player stopped playing)
  "endedAt": "2026-04-18T14:18:01.221Z",
  // How long has been spent actually playing the game in seconds (would be shorter than endedAt-startedAt if the game was paused)
  "duration": 2837,
  // What happened at the end of the game
  // Should be unset for in progress runs
  // Known values are: failed, abandonned, succeeded
  // Others can be any shape, or a subcategory eg. failed:time, failed:health
  "outcome": "failed"
}
```