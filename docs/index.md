---
_layout: landing
---

# ByJP.AtprotoGaming.Core

Engine-agnostic [AT Protocol](https://atproto.com/) core for games. Drop it into a
Unity/BepInEx, Godot.NET, or plain .NET game or mod to publish play-throughs to a
player's [PDS](https://atproto.com/guides/glossary#personal-data-server-pds): XRPC
client, identity resolution, observable auth state, an on-disk retry outbox,
deterministic record keys, rolling stats, and optional ECDSA signing.

Targets **`netstandard2.0`**, so the same DLL loads under BepInEx / .NET Framework
4.7.2 and under .NET 9 alike. It references no game engine — you keep the engine
hooks; it does the atproto plumbing.

## Install

```sh
dotnet add package ByJP.AtprotoGaming.Core
```

## Quick start

```csharp
using ByJP.AtprotoGaming.Core;
using ByJP.AtprotoGaming.Core.Adapters;
using System.Text.Json.Nodes;

// Wire three small adapters, load config, construct the client once.
var fs = FileSystem.NextTo<MyPlugin>();
var config = ConfigStore<CoreConfig>.LoadOrCreate(fs, myLogSink);
var atproto = new AtprotoGamingClient(new AtprotoGamingOptions
{
    FileSystem = fs,
    Log = myLogSink,
    Config = config,
});

// Boot login off the main thread; react to state for your badge/health UI.
_ = System.Threading.Tasks.Task.Run(atproto.LoginAsync);
atproto.Auth.Changed += () => UpdateBadge(atproto.Auth.Status, atproto.Auth.Error);

// Publish a play-through under a stable, resume-safe record key.
var rkey = Tid.FromPlayThrough(startedAtUnixSeconds, runSeed);
await atproto.Records.PutAsync("games.gamesgamesgamesgames.experimental.actor.play", rkey, playRecord);
```

## Where to next

- **[API Reference](api/index.md)** — every public type, generated from the source.
- Key entry points: <xref:ByJP.AtprotoGaming.Core.AtprotoGamingClient>,
  <xref:ByJP.AtprotoGaming.Core.RecordPublisher>,
  <xref:ByJP.AtprotoGaming.Core.Tid>,
  <xref:ByJP.AtprotoGaming.Core.RollingStats>.
- Adapters you implement: <xref:ByJP.AtprotoGaming.Core.Adapters.ILogSink>,
  <xref:ByJP.AtprotoGaming.Core.Adapters.IFileSystem>,
  <xref:ByJP.AtprotoGaming.Core.Adapters.IClock>.
