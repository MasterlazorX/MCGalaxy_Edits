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
The client's respawn intent, only honoured while **dead** (health 0): the server
clears the dwell, repositions to the map spawn (`PlayerActions.Respawn`), then
restores full health — the health rise is what removes the client's Game Over
screen. A respawn intent while alive is rejected (it would otherwise be a free
teleport to spawn) and answered with an authoritative `SURV_HEALTH` echo.

### Damage / death bridge — death-screen dwell
`SurvivalNet.OnPlayerDied` is registered on `OnPlayerDiedEvent`, which fires inside
`Player.HandleDeath` — the single choke point for **all** MCGalaxy deaths (fall,
drown, lava, killer blocks, weapons, `/kill`, …), so every hazard MCGalaxy already
detects (respecting the level's `FallHeight` / `DrownTime` / `KillerBlocks` config)
flows through. For a survival player it **holds health at 0**: the genuine flow is

1. death → `SURV_HEALTH(0)` — client shows the death camera + Game Over screen;
2. `HandleDeath` **skips its auto-respawn** while `SurvivalNet.HoldsDeathScreen`
   (survival-active and dead), and `OnPlayerDying` cancels repeat deaths so the
   killing hazard ticking at the death spot (lava, drowning) can't spam;
3. revive on the client's `SURV_RESPAWN` intent — or a **30 s safety timeout**
   (counted down by the `SURV_TIME` scheduler tick) so nobody is stranded;
4. revive = reposition to spawn, then `SURV_HEALTH(20)` (the rise revives the
   client). A map change while dead restores full health (`OnJoinedLevel`) since
   the client tears down its per-map death state.

Graduated Indev damage (partial HP from fall distance, drowning/fire ticks)
remains the future refinement; MCGalaxy still only detects lethal hazards.

**Map-spawn caveat (found in live testing):** MCGalaxy's default generated spawn
sits ~16 blocks above the ground; with `/map death on` and the default
`FallHeight` 9 every (re)spawn is a lethal fall → an infinite death loop (stock
MCGalaxy loops identically, just faster). Until the phase-1 generator places
spawns, survival maps need a grounded spawn or a raised `/map fall` threshold.

### Hack permissions follow the survival config
`Hacks.MakeHackControl` overrides the MOTD-derived flags on an active survival
map: fly/speed come from the level's `SurvivalCreative` (the *same* decision as
HELLO's creative bit, so they can never disagree — the survival-test client
defers entirely to `HackControl` in MP), noclip is off, and the respawn hack is
off (death/respawn is server-owned via `SURV_RESPAWN`). Referee mode keeps its
usual all-hacks escape hatch, and the server-side `Hacks.CanUse*` checks read
the same override. `/Survival` re-sends motd+hacks so live config changes apply.

### Phase 3 — mob streaming (`SurvivalMobs.cs`)

The server runs the whole mob simulation and streams it; the client renders a
puppet pool (its `st_mobs[]`) fed by these messages. The AI/physics is a C#
port of the ClassiCube fork's verified c0.30/Indev mob sim (BasicAI /
BasicAttackAI / EntityMob lineage), ticked at 20 TPS on a dedicated scheduler.
Only levels with players are simulated.

| msg | dir | layout |
|---|---|---|
| `SURV_MOB_SPAWN` 0x10 | S→C | `[id:u16][type][pos:3×i16 fixed(×32)][yaw:u8][pitch:u8][health][flags(b0 helmet, b1 armor, b2 fur)]` |
| `SURV_MOB_MOVE` 0x11 | S→C | `[id:u16][pos:3×i16][yaw][pitch]` — sent only when the quantised pose changed |
| `SURV_MOB_STATE` 0x12 | S→C | `[id:u16][health][flags(b0 hurt, b1 fuse, b2 onFire, b3 graze, b4 dead, b5 noFur)]` — sent on change; the hurt bit is a one-tick edge |
| `SURV_MOB_DESPAWN` 0x13 | S→C | `[id:u16][reason(0 despawn / 1 death)]` |
| `SURV_ATTACK` 0x80 | C→S | `[targetKind(0 mob)][targetId:u16]` — reach-validated (6 blocks incl. latency pad), applies melee + knockback + aggro; sheep shear rules per mode |

Simulated per mob: wander/chase/attack AI (per-type: creeper 3/7-block fuse →
30-tick blast, spider light-flee + pounce, zombie 5 / default 2 Indev melee,
c0.30 damage rolls + creeper headbutt self-damage), `Mob.travel` physics with
axis-clipped AABB collision against level blocks, fall damage, drowning,
lava/fire, sheep grazing (grass→dirt through the normal block path, so every
client sees it), undead sunburn, the c0.30 spawner (initial population + capped
top-up, min-of-two-uniforms Y bias, 16-block spawn-point exclusion) and the
600-tick/1-in-800 despawn roll. Players take graduated damage (`DamagePlayer`:
the same dual-threshold invulnerability window as mobs, ticked at 20 TPS);
lethal hits route through `HandleDeath`, so the death-screen dwell applies.
Kill credit awards the c0.30 death scores in Classic mode only.

Test aids: `/Survival spawn [zombie/skeleton/pig/creeper/spider/sheep]` (at
your feet, ground-snapped) and `/Survival mobs` (live count).

**Live-testing round 2 (user reports) — spawner + boundary fixes:**
- *"Mobs spawned once when I entered, then never again"*: the top-up spawner
  rolled map-wide random positions with a 256 cap (the client's puppet-pool
  limit), so on big maps the cap saturated with mobs nobody ever met. The
  top-up spawner now picks candidates in a ring **16–48 blocks around a
  random online survival player** (the Alpha+ spawners made the same change
  for the same reason); the c0.30 initial population stays map-wide. `area`
  floors at 1 so sub-64³ maps spawn at all.
- *"Mobs get pushed out of the map boundaries"*: `BlockAt` clamps
  out-of-bounds reads to the edge column (genuine `getBlockId` semantics),
  which reads as open air above ground — knockback punted mobs clean off the
  map. The map edge is now a wall for mob collision.
- Debug/test surface (`/Survival ...`): `time` (show or set the world clock —
  `day/noon/sunset/night/midnight/<ticks>` — pushed to all survival players
  instantly), `spawner` (tick/roll/attempt/spawn counters with per-reason
  rejection tallies + clock state), `mobs` (nearest live mobs with id/pos/
  distance/HP/target), `inv [player]` (server-side slot + cursor dump),
  `spawn [type]` (force-spawn at your feet). Natural spawns log at Debug
  level. The mob tick is wrapped in a logging try/catch.

**Live-testing round 3 — visitors play classic, untouched:**
- *"Classic players shouldn't die from falls"*: `/Survival` auto-enables
  MCGalaxy's per-level `SurvivalDeath`, whose fall/drown detection applied to
  every client on the level. On survival-mode maps hazard detection is now
  gated to survival-capable sessions — stock/plain-CPE visitors walk the map
  unharmed (mobs already ignore them: the watcher filter only targets
  survival players). Plain maps using `/map death on` keep the stock
  behaviour for everyone.
- Stock-client building on survival maps was verified working by a synthetic
  no-CPE protocol client (place + delete accepted on the wire); a classic
  player unable to build is most likely standard realm/level build
  permissions (`/os allow`, perbuild), not survival code.

### Genuine Indev player damage (`SurvivalHazards.cs`) — the death system, properly

**The problem (user-identified):** player hazards rode MCGalaxy's binary
`SurvivalDeath` system (lethal-or-nothing fall/drown), so health only ever
went 20 → 0 — graduated Indev damage never happened and the death system
didn't sync.

**Now:** a 20 TPS per-player hazard tick (on the `SurvivalMobs` scheduler,
next to the combat window countdown) simulates the genuine rules from the
same position stream `PlayerPhysics` consumed, all applied through
`DamagePlayer` so the invulnerability window + death-screen dwell hold:
- **fall**: peak-Y tracking, `ceil(dist − 3)` on landing (liquid cushions;
  teleports/respawns detected by the >8-block single-tick jump and never
  counted) — verified live: a 12-block fall dealt exactly 9 (20 → 11 HP),
  a 23-block fall killed into the dwell
- **drowning**: Indev's air counter with the −20 underflow timer (2 HP,
  first hit 320 ticks under, then every 20); c0.30's empty-air cadence
- **lava**: 10/tick through the window; **fire** (Indev): lava arms the
  600-tick burn, 1 HP per 20 ticks, water fizzes it out (fire-block contact
  waits on phase 1's custom blocks; no on-fire overlay flag exists for
  players yet — reserved-bit/`SURV_PLAYER_STATE` handoff item)
- **void**: 4/tick below y = −16 (floating maps)

The classic binary system is now fully off on survival-mode maps (visitors
untouched — re-verified with the synthetic stock client); plain maps using
`/map death on` keep stock behaviour. `OnPlayerDied` remains the bridge for
killer blocks and `/kill` only. Damage ticks log at Debug level.

**Also open from live testing:** the "classic players can't build" report —
wire-level evidence says stock building works (synthetic client transcript);
prime suspect is `/os` realm build permissions (`/os allow`). Awaiting the
exact client-side message before treating it as a survival bug.

### Visitor policy — survival worlds are survival-client-only (§16)

The networking-plan §16 invariant is now enforced: *a client that has not
negotiated `SurvivalTest` must never place or break blocks in a survival map*
(it bypasses tools, consumption, drops and physics — its edits would corrupt
the authoritative world). New per-level `SurvivalVisitors` policy
(`/Survival visitors ...`), consulted only while `SurvivalMode` is on:

- **visitor** (default) — non-survival clients may join and look; their block
  changes are cancelled + reverted, with a rate-limited explanation.
- **allow** — they build normally (owner's choice to accept desync).
- **deny** — they may not even join the map (`OnJoiningLevelEvent`).

Creative-flag maps stay free-build for everyone (no sim to corrupt), referees
keep their staff escape hatch, and draw commands are unaffected (the gate
covers manual changes). Verified with the synthetic stock client: place +
delete cancelled with the policy message; the survival client's mine→pickup
loop unaffected.

**V1 deviations (deliberate, revisit later):**
- Indev's A* creature pathfinding is not ported — both modes use the c0.30
  direct-steer chase (mobs bump into obstacles rather than pathing around).
- Skeletons melee like zombies: arrows need their own wire messages (phase 5).
- No server-side light engine: "brightness" (darkness spawn rule, spider
  light-flee, monster fast-aging, sunburn) = sky-exposure × day/night level.
- Explosions damage players (approximate linear falloff) but never blocks —
  most MCGalaxy maps are protected builds; block damage needs opt-in config.
- No drops (phase 5): mob deaths and shears yield nothing yet.
- Mobs freeze on playerless maps and do not persist across server restarts.
- Spawn clusters trimmed to 1–3 (genuine rolls up to 9) to tame populations.

### Phase 4 (first slice) — the server-owned inventory (`SurvivalInventory.cs`)

The server owns every inventory slot and the cursor; the client renders the
streamed view and sends click intents (echo-only, per the client plan's §27:
TCP ordering makes the server a simple deterministic sequencer — no Beta-style
transaction dance). Slot layout mirrors the client's `SurvivalTest.h` exactly:
0..35 main (0..8 hotbar), 36..44 craft, 45..98 container (reserved), 99..102
armor. State lives in `Player.Extras`, so it follows a `/goto` within a session.

| msg | dir | layout |
|---|---|---|
| `SURV_INV_FULL` 0x20 | S→C | `[baseSlot][runLen]` then runLen × `{id:u16, count:u8, dmg:i16}` (≤12/frame; main+craft then armor at handshake) |
| `SURV_INV_SLOT` 0x21 | S→C | `[slot][id:u16][count][dmg:i16]` — the echo for every mutation |
| `SURV_CURSOR` 0x25 | S→C | `[id:u16][count][dmg:i16]` — the server-owned held stack |
| `SURV_HELD_SLOT` 0x85 | C→S | `[hotbarIndex]` — tracked for place-consume preference |
| `SURV_SLOT_CLICK` 0x82 | C→S | `[slotIdx:u16][button]` — the GuiContainer click model (pickup all/half, merge to max stack, right-place-one, swap) runs on server state; container range rejected until streamed; armor accepts nothing yet |
| `SURV_RESULT_CLICK` 0x83 | C→S | validated no-op (no server-side recipes yet) |
| `SURV_CONT_CLOSE` 0x84 | C→S | refunds cursor + craft grid into the inventory (hotbar-first `storePartialItemStack` order), full resync |

**The block bridge** (`OnBlockChangingEvent`): survival players' manual edits
feed the inventory. Mining adds the broken classic block (raw ≤ 49; liquids
yield nothing) straight to the inventory — the drop-entity hop is phase 5;
placing consumes one (held slot preferred) or is cancelled + `RevertBlock` +
resync when the player doesn't have the block. Dead players' edits are
cancelled. Creative-flag maps build free (no pickup/consume).

**V1 deviations:** no crafting recipes, no containers/furnace streaming
(0x22–0x24), no `USE_ITEM`, flat max stacks (99 c0.30 / 64 Indev — per-id
tables land with item definitions), armor slots accept nothing, death keeps
the inventory until phase-5 drops honour `SurvivalDeathDrops`, no persistence
across restarts (session-scoped like health).

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
`0x04` TIME, `0x10–0x13` MOB_* (phase 3), `0x20`/`0x21`/`0x25` inventory
(phase 4 slice); client→server `0x87` RESPAWN, `0x80` ATTACK, `0x82–0x85`
inventory clicks (handled). The rest are reserved and, for inbound intents,
bounds-checked, capability-gated, and logged (handlers deferred).

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

To make a map survival: set `SurvivalMode = Indev` (or `Classic`) in that level's
properties file, **or** use the `/Survival` command live (below).

### `/Survival` command

`MCGalaxy/Commands/World/CmdSurvival.cs` (rank Operator) edits the current level's
survival settings and applies them live — it saves the config and re-sends the
handshake (or a mode-off `SURV_HELLO`) to survival-test clients on the level, so
no rejoin is needed. Registered in `Command.RegisterAllCore()`.

- `/Survival` — show this level's survival settings
- `/Survival [off/classic/indev]` — set the mode (the per-map gate)
- `/Survival theme [normal/hell/paradise/woods/floating]`
- `/Survival [enhanced/creative/pvp/deathdrops] [on/off]` — set a flag

From console (no current level) it operates on the main level.

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
