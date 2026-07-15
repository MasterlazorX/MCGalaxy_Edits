# Survival-support session notes

**Branch:** `survival-support`
**Date:** 2026-07-15
**Scope:** Server-side *foundation* for the ClassiCube "survival-test" client.

Companion to the client-side docs in the ClassiCube fork:
`doc/survival-handshake.md` and `doc/networking-plan.md`
(branch `survival-test`). The wire contract is `src/SurvivalNet.h` in that repo.

---

## Goal

Let survival-test ClassiCube clients connect to MCGalaxy, negotiate a survival
capability, and receive a per-map survival handshake — **without** changing
anything for stock Classic / CPE-only clients, who must keep connecting and
playing exactly as before.

This landed the **foundation only**, deliberately mirroring what the client
foundation implements: capability negotiation + the handshake send path +
inbound receive/validate/log. The actual state appliers (mobs, inventory,
drops, health, day/night) and the client's simulation "mode-flip" are
**deferred** — see `roadmap.md`. Every message id is reserved up front so the
remaining work is fill-in against a fixed contract, not new protocol design.

---

## The one idea: capability vs. activation are two layers

| Layer | Scope | Set by | Why |
|---|---|---|---|
| **Capability** (`hasSurvival`) | whole connection | CPE `SurvivalTest` ext, negotiated once at login | "Can this client speak survival?" |
| **Activation** (`SurvivalMode`) | per map | level config, sent via `SURV_HELLO` | "Is *this* map survival?" |

MCGalaxy is a **multi-level** server: a player `/goto`s between a plain Classic
build map and a survival map on the *same connection*. The CPE handshake fires
once and cannot re-fire per map, so it can only answer the capability question.
Whether a given map is survival changes on every level switch, so it rides a
per-map message. Folding both into one flag would make it impossible to turn
survival on for one level and off for the next.

Consequence: survival traffic is sent **iff** `hasSurvival && SurvivalMode != Off`.

---

## Transport

Survival messages ride the existing CPE **PluginMessages** extension
(opcode `0x35`) on a single channel:

```
SURVNET_CHANNEL = 0xB0
```

Each message is `[id:1][fields...]` inside the fixed 64-byte PluginMessage
payload (66 bytes on the wire: `[0x35][channel][payload:64]`). Multi-byte
fields are big-endian; unused tail bytes are zero and ignored. MCGalaxy already
had `Packet.PluginMessage(...)` and `OnPluginMessageReceivedEvent`, so no new
opcode or framing was needed — this is the sanctioned escape hatch for custom
payloads over a known, negotiated opcode.

`0xB0` sits high on purpose to avoid clashing with a channel a plugin might
casually pick. **Do not reuse `0xB0` for anything else.**

---

## Changes, file by file

| File | Change |
|---|---|
| `MCGalaxy/Network/CPESupport.cs` | New `CpeExt.SurvivalTest` constant; advertise `SurvivalTest` v1 in `CpeExtension.All` so the server both offers it and recognises the client's `ExtEntry`. |
| `MCGalaxy/Network/IGameSession.cs` | New per-session fast-path flag `public bool hasSurvival;`. |
| `MCGalaxy/Network/ClassicProtocol.cs` | In `AddExtension`, set `hasSurvival = true` when the client negotiates `SurvivalTest`. |
| `MCGalaxy/Levels/LevelConfig.cs` | Per-map survival options (see "Config" below). `SurvivalMode` doubles as the activation gate. |
| `MCGalaxy/Network/SurvivalNet.cs` | **New.** The whole survival protocol surface: channel + message-id contract, `SURV_HELLO` / `SURV_WORLDINFO` builders, and the inbound dispatch (bounds-checked, validated, logged; appliers deferred). |
| `MCGalaxy/CorePlugin/MiscHandlers.cs` | `HandleSentMap` calls `SurvivalNet.SendHandshake(p, level)` alongside the other per-map CPE sends (textures, block permissions). |
| `MCGalaxy/CorePlugin/CorePlugin.cs` | Register/unregister `SurvivalNet.HandlePluginMessage` on `OnPluginMessageReceivedEvent`. |
| `MCGalaxy/CorePlugin/ConnectHandler.cs` | On connect, call `SurvivalNet.AnnounceClient(p)` — a **test aid** (see below). |
| `MCGalaxy/MCGalaxy_.csproj` | Add `Network\SurvivalNet.cs` to the explicit compile list (the classic msbuild project lists every file; the SDK/standalone projects glob). |

### Test aid: connect announcement

`SurvivalNet.AnnounceClient(p)` runs from `ConnectHandler.HandleConnect` (which
fires after CPE negotiation completes, so `hasSurvival` is already known). It
tells the joining player, and logs to the server console, whether the client was
detected as survival or normal:

- survival: `Connected via the survival client (handshake verified)`
- normal: `Connected via a normal client (no survival handshake)`

This is purely diagnostic — it is the only place that announces detection, so it
is trivial to gate behind a config flag or remove once wire testing is done.

### Where the handshake is sent

`SendRawMapCore` sends the level, then fires `OnSentMapEvent` →
`MiscHandlers.HandleSentMap`, which is exactly where per-map CPE state
(textures, block permissions) is already pushed. The survival handshake is sent
there, so it goes out right after the level on every join and every `/goto`.
The send is a no-op unless `hasSurvival && SurvivalMode != Off`, so Classic play
is untouched.

---

## Wire format (v1)

### `SURV_HELLO` (0x01) — server → client
| Off | Size | Field | Source |
|---|---|---|---|
| 0 | 1 | id = 0x01 | |
| 1 | 1 | mode | `SurvivalMode` (0 off / 1 classic / 2 indev) |
| 2 | 1 | flags | bit0 enhanced, bit1 creative, bit2 pvp, bit3 deathDrops |
| 3 | 1 | protoVer | `SurvivalNet.ProtoVersion` (1) |

### `SURV_WORLDINFO` (0x02) — server → client
| Off | Size | Field | Source |
|---|---|---|---|
| 0 | 1 | id = 0x02 | |
| 1 | 1 | groundLevel | `EdgeLevel + SidesOffset` |
| 2 | 1 | waterLevel | `EdgeLevel` (map default = height/2) |
| 3 | 1 | fluid id | `HorizonBlock` (raw) |
| 4 | 1 | theme | `SurvivalTheme` |
| 5 | 1 | flags | bit0 floating (`theme == Floating`) |
| 6 | 1 | sides block | `EdgeBlock` (raw; the "bedrock" sides) |
| 7 | 1 | edge block | `HorizonBlock` (raw; the horizon water) |

Heights are one byte each in v1, matching the handshake doc's byte table. The
fuller int16 heights and the remaining `.mclevel` env set are deferred; env
colours already reach survival clients via the stock CPE `EnvColors` path
(`SendCurrentEnv`), so they are not duplicated in `SURV_WORLDINFO`.

### `SURV_TIME` (0x04) — server → client
| Off | Size | Field | Notes |
|---|---|---|---|
| 0 | 1 | id = 0x04 | |
| 1 | 2 | worldTime | big-endian u16; 0 sunrise, 6000 noon, 12000 sunset, 18000 midnight |
| 3 | 1 | skyLight | 0..15, eased across dawn/dusk |

The server owns the day/night cycle (the client must not run it locally in MP —
networking-plan §15.2/§17.4). A single clock is advanced on `Server.MainScheduler`
(20 world ticks/second → a 20-minute day) and pushed to every survival player once
per second, plus once at handshake to seed the client. The clock is shared across
survival maps in v1; a per-map clock (each Indev world keeps its own `TimeOfDay`)
is a future refinement. Lifecycle: `SurvivalNet.Start()`/`Stop()` from `CorePlugin`.

### `SURV_HEALTH` (0x03) — server → client
| Off | Size | Field | Notes |
|---|---|---|---|
| 0 | 1 | id = 0x03 | |
| 1 | 1 | health | 0..`MAX_HEALTH` (20 = 10 hearts) |
| 2 | 4 | score | big-endian i32 |

The server owns health and score (stored in `Player.Extras`, so they follow the
player across a `/goto` within one session). `SURV_HEALTH` is sent at handshake and
whenever `SetHealth` changes it. Damage sources are deferred, so today health only
changes on respawn.

### `SURV_RESPAWN` (0x87) — client → server *(handled)*
The client's respawn intent. The server validates it (must be on a survival map),
resets health to full, repositions the player to the map spawn
(`PlayerActions.Respawn`), and echoes an authoritative `SURV_HEALTH`.

### Damage / death bridge
`SurvivalNet.OnPlayerDied` is registered on `OnPlayerDiedEvent`, which fires inside
`Player.HandleDeath` — the single choke point for **all** MCGalaxy deaths (fall,
drown, lava, killer blocks, weapons, `/kill`, …), so every hazard MCGalaxy already
detects (respecting the level's `FallHeight` / `DrownTime` / `KillerBlocks` config)
flows through. For a survival player it drops health to 0 (`SURV_HEALTH(0)`) then
restores to full, since `HandleDeath` repositions to spawn immediately after.

MCGalaxy's Classic survival is binary (lethal-or-nothing) and auto-respawns, so
health today goes 20 → 0 → 20. Graduated Indev damage (partial HP from fall
distance, drowning/fire ticks) and a death-screen dwell (suppress the auto-respawn,
wait for the client's `SURV_RESPAWN`) are future refinements gated on the client
implementing the death UI.

### Reserved message ids (`SurvivalNet.cs`)

Server → client: `HELLO 0x01`, `WORLDINFO 0x02`, `HEALTH 0x03`, `TIME 0x04`,
`MOB_SPAWN 0x10`, `MOB_MOVE 0x11`, `MOB_STATE 0x12`, `MOB_DESPAWN 0x13`,
`INV_FULL 0x20`, `INV_SLOT 0x21`, `CONT_OPEN 0x22`, `CONT_SLOT 0x23`,
`FURN_PROG 0x24`, `CURSOR 0x25`, `DROP_SPAWN 0x30`, `DROP_PICKUP 0x31`,
`DROP_REMOVE 0x32`, `BLOCKMETA 0x40`, `PLAYER_EQUIP 0x50`.

Client → server: `ATTACK 0x80`, `USE_ITEM 0x81`, `SLOT_CLICK 0x82`,
`RESULT_CLICK 0x83`, `CONT_CLOSE 0x84`, `HELD_SLOT 0x85`, `DROP_ITEM 0x86`,
`RESPAWN 0x87`.

Implemented so far: server→client `0x01` HELLO, `0x02` WORLDINFO, `0x03` HEALTH,
`0x04` TIME; client→server `0x87` RESPAWN (handled). The rest are reserved and,
for inbound intents, bounds-checked, capability-gated, and logged (handlers
deferred).

---

## Config (per level, `Survival` section of `*.properties`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| `SurvivalMode` | `Off` / `Classic` / `Indev` | `Off` | Survival mode **and** the per-map activation gate |
| `SurvivalTheme` | `Normal` / `Hell` / `Paradise` / `Woods` / `Floating` | `Normal` | Indev world theme; `Floating` also sets the WORLDINFO floating bit |
| `SurvivalEnhanced` | bool | `false` | HELLO flag bit0 |
| `SurvivalCreative` | bool | `false` | HELLO flag bit1 |
| `SurvivalPvP` | bool | `false` | HELLO flag bit2 |
| `SurvivalDeathDrops` | bool | `true` | HELLO flag bit3 |

To make a map survival: set `SurvivalMode = Indev` (or `Classic`) in that
level's properties file.

---

## Backward compatibility

- Stock Classic / CPE-only clients never advertise `SurvivalTest`, so
  `hasSurvival` stays `false` — they receive **zero** `SURV_*` bytes.
- Singleplayer / internal server never negotiates CPE exts → gate closed.
- On a non-survival map (`SurvivalMode = Off`) even a capable client gets no
  survival traffic; the map plays as plain Classic.
- Inbound: every message on `0xB0` is bounds-checked and dropped if the sender
  never negotiated `SurvivalTest`. A negotiated capability is a capability, not
  a permission — appliers, when added, must still re-validate reach / cooldown /
  slot / container access and correct the client authoritatively.

---

## Verification performed

- `dotnet build` of the core library and the CLI server: **0 errors**.
- CLI server **boots** cleanly on Linux (SQLite backend), generates + saves the
  main level, and listens on `25565` with no exceptions.
- Generated `properties/cpe.properties` shows `SurvivalTest = True` (advertised).
- Level config round-trips: setting `SurvivalMode = Indev`, `SurvivalTheme = Hell`
  parses and re-serialises with no warnings.
- **Socket-level end-to-end test** (a minimal Classic-protocol client driving the
  real running server, name verification off, main level `SurvivalMode = Indev`):
  - A **normal** client (no CPE) receives the "normal client" message and **no**
    `SURV_*` bytes.
  - A **survival** client (advertising `SurvivalTest` v1 in its CPE `ExtEntry`)
    receives the "survival client" message **and** the `SURV_HELLO` (`35 B0 01`)
    + `SURV_WORLDINFO` (`35 B0 02`) plugin messages on the wire.
  - The server console logs both connections with the correct classification.
- **Not** yet exercised: a real survival-test ClassiCube build (only a synthetic
  protocol client was used here). Pointing the actual client at the server is the
  natural next check.

---

## Build tooling added this session

- **`Makefile`** — `make` (core), `make cli`, `make all`, `make run`,
  `make clean`, `make hooks`, `make help`. Auto-detects the `dotnet` SDK.
- **`.githooks/pre-commit`** — builds the core library before each commit and
  aborts the commit on failure (skips gracefully when no SDK is present; bypass
  with `git commit --no-verify`). Install with `make hooks`
  (sets `core.hooksPath = .githooks`).
- **`.github/workflows/survival-support.yml`** — CI that runs on every push/PR to
  this branch (the stock `build.yml` only runs on master/ConsoleDriver). It
  produces the lean classic .NET Framework build via msbuild + Mono — the same
  layout as Visual Studio and the official releases (`MCGalaxy_.dll`,
  `MCGalaxy.exe`, `MCGalaxyCLI.exe` + bundled `MySql.Data.dll` /
  `System.Data.SQLite.dll`, no NuGet dependency tree) — and uploads `bin/Release`
  as an artifact.

Note on build flavors: the local `Makefile` and the pre-commit hook use the fast
`dotnet` build (net6/net8); CI uses the Framework build. Same source, two
packagings — the dotnet output additionally bundles MySql.Data 8.1.0's NuGet
dependency tree (BouncyCastle, Protobuf, LZ4, Zstd, …), which the Framework build
avoids by referencing the small bundled `MySql.Data.dll`.
