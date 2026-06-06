# Atproto Gaming Core

A package for C#/dotnet games which can be used as a core to atproto functionality, eg. posting details of gameplay to a player's PDS. For use in mods, or in game development.

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
    // The stops along the route so far, using consistent IDs if possible
    "route": ["monster:nibbit", "monster:twig-slimes", "marketplace", "boss:bygone-effigy"],
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