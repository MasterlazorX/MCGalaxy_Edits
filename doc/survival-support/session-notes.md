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
v2 layout (SurvivalTest **ext version 2** — sent when the client negotiated ≥ 2):
| Off | Size | Field | Source |
|---|---|---|---|
| 0 | 1 | id = 0x02 | |
| 1 | 2 | groundLevel (i16 BE) | `EdgeLevel + SidesOffset` |
| 3 | 2 | waterLevel (i16 BE) | `EdgeLevel` (map default = height/2) |
| 5 | 1 | fluid id | `HorizonBlock` (raw) |
| 6 | 1 | theme | `SurvivalTheme` |
| 7 | 1 | flags | bit0 floating (`theme == Floating`) |
| 8 | 1 | sides block | `EdgeBlock` (raw; the "bedrock" sides) |
| 9 | 1 | edge block | `HorizonBlock` (raw; the horizon water) |

The i16 promotion exists because floating maps genuinely use groundLevel −128
/ waterLevel −127 (hell −16), which v1's u8 fields clamped to 0 — visible as
a spurious dirt horizon plane under floating islands (user-diagnosed). Both
sides branch on the **negotiated** ext version, so a v1 peer still exchanges
the old u8 layout (offsets 1..7, one byte per level). The remaining
`.mclevel` env set stays deferred; env colours already reach survival clients
via the stock CPE `EnvColors` path (`SendCurrentEnv`), so they are not
duplicated in `SURV_WORLDINFO`.

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

### Fallbacks for non-survival clients (§21 env + §15.1 mob mirror) + spawner pace

`SurvivalFallbacks.cs` — strictly per-session views for clients that did NOT
negotiate SurvivalTest, on survival maps (survival clients keep the genuine
sub-protocol streams):
- **Day/night via EnvColors (§21):** the survival clock scales the level's
  sky/cloud/fog/shadow/sunlight colours (quadratic ease, floored at 0.15 so
  night stays readable), sent only when the eased sky-light level changes.
  Wire-verified: noon = the level's own colours, midnight = the darkened set;
  colours restore on `/Survival off` and any normal map join.
- **Mob mirror (§15.1 fallback):** the level's mobs appear to spectators as
  plain Classic entities with CPE ChangeModel set to the mob's model (stock
  ClassiCube ships all six c0.30 models; sheared sheep use `sheep_nofur`;
  pre-CPE clients see humanoids). Entity ids allocate 254 downward (far above
  MCGalaxy's low player/bot range), up to 48 mobs per viewer, positions at
  **10 Hz** - the same cadence MCGalaxy relays player positions at, so stock
  clients' own entity interpolation smooths mobs exactly like other players
  (raised from the original 5 Hz after user feedback). No bespoke animations
  (hurt flash, swell) - visible + moving is the goal. Wire-verified: 48
  models + a ~10 Hz-per-mob teleport stream.
- **The sim stays alive for classic-only maps:** the mob tick runs whenever
  ANY player is on the level - survival clients remain the only AI targets,
  hazard tickees and puppet-stream receivers, but wandering/grazing/physics,
  the despawn "is anyone near" check, and the spawner's player-ring centres
  all count classic spectators too. Previously the sim required a survival
  client, so mobs froze mid-step the moment the last one left even with
  spectators watching (user report). Mobs still freeze on maps with nobody
  on them at all (the server-cost deviation). Verified live: with only a
  stock client on the map, the spawner kept placing and 48 mirrored mobs
  streamed at 10 Hz.

**Spawner pace fix ("awfully slow"):** the old attempt rolled a fully random
Y (~97% landed underground/in air) and pre-rolled the type (the light rule
then rejected most of the rest). Now each attempt scans its column for every
standable spot (surface and caves), picks one uniformly, and the spot's
darkness picks the type pool (dark→monsters, lit→animals - the same Indev
outcome with none of the waste). Attempts per roll dropped 10 → 2 since they
nearly always land. Measured: 29 spawns in the first ~6 s of a fresh map vs
3 per 10 s before, zero rejections. The population cap (area×20, ≤256)
provides the equilibrium.

**V1 deviations (deliberate, revisit later):**
- Indev's A* creature pathfinding is not ported — both modes use the c0.30
  direct-steer chase (mobs bump into obstacles rather than pathing around).
- Skeletons melee like zombies: arrows need their own wire messages (phase 5).
- No server-side light engine: "brightness" (darkness spawn rule, spider
  light-flee, monster fast-aging, sunburn) = sky-exposure × day/night level.
- Explosions damage players (approximate linear falloff) but never blocks —
  most MCGalaxy maps are protected builds; block damage needs opt-in config.
- No drops (phase 5): mob deaths and shears yield nothing yet.
- Mobs freeze on maps with no players at all (any player - survival or
  classic - keeps the sim running) and do not persist across restarts.
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
| `SURV_CONT_CLOSE` 0x84 | C→S | refunds cursor + craft grid into the inventory (hotbar-first `storePartialItemStack` order), full resync; also closes the open container view |
| `SURV_USE_ITEM` 0x81 | C→S | `[heldSlot][x:i16][y:i16][z:i16][face]` — v1 opens container GUIs (reach-validated; chest lid-block rule; large-chest pairing); eating/tools land with items |
| `SURV_CONT_OPEN` 0x22 | S→C | `[kind][slotCount]` — 0 force-close, 1 chest(27), 2 furnace(3), 3 large chest(54), 4 workbench (3×3 over the streamed craft slots) |
| `SURV_CONT_SLOT` 0x23 | S→C | `[slot(0..53 container-relative)][id:u16][count][dmg:i16]` — client zeroes on OPEN, only occupied slots streamed, click echoes go to every viewer of the entity |
| `SURV_FURN_PROG` 0x24 | S→C | `[burn(0..12)][cook(0..24)]` pre-scaled; 0 until item smelting exists |

**Containers** (rest of the phase-4 GUI, landed): session-scoped tile
entities keyed by level+position (chest 27 / furnace 3 slots), lazily
created on first open; genuine large-chest pairing (the -X/-Z neighbour is
the upper 27 slots) and the BlockChest lid-block rule; the GuiContainer
click model resolves slots 45..98 through the player's open view. Mining a
container discards its tile entity and force-closes any viewer's screen
(CONT_OPEN kind 0). Not opened on creative maps (client-local palette
there). V1 deviations: contents vanish on destruction (scatter needs
phase-5 drops), no restart persistence, no smelting/recipes until items.

**The block bridge** (`OnBlockChangingEvent`): survival players' manual edits
feed the inventory. Mining adds the broken classic block (raw ≤ 49; liquids
yield nothing) straight to the inventory — the drop-entity hop is phase 5;
placing consumes one (held slot preferred) or is cancelled + `RevertBlock` +
resync when the player doesn't have the block. Dead players' edits are
cancelled. Creative-flag maps build free (no pickup/consume). *(Phase 1
extended the bridge past raw 49 to the Indev block set on Indev maps — see
the `SurvivalBlocks.cs` section below.)*

**V1 deviations:** no crafting recipes, no containers/furnace streaming
(0x22–0x24), no `USE_ITEM`, flat max stacks (99 c0.30 / 64 Indev — per-id
tables land with item definitions), armor slots accept nothing, death keeps
the inventory until phase-5 drops honour `SurvivalDeathDrops`, no persistence
across restarts (session-scoped like health).

### Phase 1 (step 1) — the Indev block set (`SurvivalBlocks.cs`)

Indev-mode survival maps now carry the full Indev block set as **level-scoped
BlockDefinitions** — a 1:1 port of the client fork's `IndevBlocks_Define` table
(`src/IndevTest.c`): torch 50, fire 51, water/lava source 52/53, chest 54,
gears 55, diamond ore/block 56/57, workbench 58, furnace 61 / lit 62, chest
facing views 71–74, furnace views 75–78 idle / 79–82 lit, farmland 83 / wet 84
(15/16 tall), crop stages 85–92, wall torches 94–97. Names, per-face tiles
(fronts land on the −Z/+Z/−X/+X face matching Indev metadata 2–5), collide,
sounds, lamp brightness (torch 14, fire 15, lit furnace 14) and classic
fallback ids all match the client. **No new wire messages and no ext bump** —
this rides stock CPE `DefineBlock`/`DefineBlockExt v2`/`UndefineBlock`.

The id/metadata model (networking-plan §18): the level array stores the
client's flattened **view ids** (each visible metadata state = its own id);
`SurvivalBlocks.ToIndev/FromIndev/DataMeta/ApplyDataMeta` are the ported
bijection between view ids and genuine `(id, Data nibble)` pairs — the
authoritative encoding for the upcoming map generator (step 2) and `.mclevel`
I/O (step 3).

Lifecycle: `SurvivalBlocks.Sync(lvl)` applies the set when
`SurvivalMode == Indev` and strips it otherwise — called from
`OnLevelLoadedEvent`, `SurvivalNet.Start` (already-loaded levels) and
`SurvivalNet.RefreshLevel` (live `/Survival` flips, verified: 37 undefines on
`off`, full re-apply on `indev`). The defs are **runtime-only** (never saved to
`blockdefs/lvl_*.json`); removal only strips reference-equal instances, so a
map owner's own `/lb` override at the same id survives. Pre-BlockDefs clients
get the fallback ids and a map reload on flips.

Who sees what: the **fork client** redefines these blocks locally on
`SURV_HELLO(mode=Indev)` (its `IndevTest_NetworkModeChanged` →
`IndevBlocks_Define` — genuine torch stick model, fire mesh, wall-torch tilt),
overriding the server defs, so no client change was needed. **Stock CPE
clients** render the server defs (sprite torches/crops, textured cubes).
**Pre-CPE clients** see fallbacks (torch→sapling, chest→crate, furnace→cobble,
lit→magma, farmland→dirt, diamond ore/block→iron ore/block, fire→CPE fire).
Tile indices 96+ target the fork's patched texture pack; a plain default pack
shows placeholder art there until §19 texture-pack serving lands.

The block bridge now accepts the set on Indev maps: mining a view id yields
its normalized pickup (`PickupFor` — facing views → canonical chest/furnace,
lit furnace → idle, wall torch → torch, farmland → dirt, crops/fire/sources →
nothing) and placing consumes `PlaceCost` (canonical id; unowned → revert).
**V1 deviations:** diamond ore drops itself (no diamond item yet), crops drop
nothing (seeds are an item), placement always produces the canonical facing
(placement-rotation + `SURV_BLOCKMETA` come later in phase 1), fire/sources
are obtainable only via `/Survival give`.

Debug aid: `/Survival give [block] <count> <player>` puts blocks straight into
a survival player's server inventory (names resolve against the level's custom
defs, so `torch`, `workbench`, `diamondore` work; console must name a player).

Verified live (fork client + synthetic BlockDefs client + console): all 37
defs stream on join with correct fields; give → INV echo (genuine item icons +
held torch model); in-reach place consumed a torch (x4→x3) and breaking it
picked it back up (x3→x4); placing an unowned lit furnace reverted without
consuming; a reach-rejected far place consumed nothing; classic dirt mining
still yields its pickup.

### Phase 1 (step 2) — the Indev world generator (`Generator/IndevGenerator.cs`)

A C# port of the client fork's `src/IndevGen.c` - itself a
statement-for-statement, oracle-verified port of in-20100223's
`LevelGenerator.java`. The load-bearing details the client's parity work
identified are preserved: java.util.Random's exact LCG with its **three
streams** (the generator stream; `World.random` recreated at the end of
Assembling with one burned `nextInt()`; findSpawn's own fresh Random),
MathHelper's 65536-entry float sine table built from double `Math.Sin`,
double-vs-float expression precision, and the `WR_*` World replica (clamped
out-of-range reads, interior-only `setBlock`, falling sand, still-liquid
wake-ups, flower pops that burn 4 `World.random` draws, the Assembling y-skip
quirk). Same seed/theme/type/size should reproduce the client generator's
output block-for-block (not re-verified against the Java oracle server-side -
the C port it mirrors is the verified one).

Usage: `/NewLvl <name> <w> <h> <l> indev [theme] [type] [seed]` - themes
`normal/hell/paradise/woods`, types `inland/island/floating/flat`. Width and
length must be powers of two, height ≥ 64 (the genuine size grid). Pipeline:
raise/erode heightmap (distorted noise) → soil → surface grow → cave worms →
coal/iron/gold/diamond ore worms → lava pockets → theme springs → edge-water
flood → assemble floor/borders → light+heightmap snapshot → find spawn →
generate the 7×5×7 spawn house (obsidian slab, doorway, **wall torches 94/95
mounted like genuine BlockTorch.onBlockAdded**, stored as extended custom
blocks) → grass → trees (woods ×51) → flowers/mushrooms (paradise ×10).

Generated maps come out survival-ready: `SurvivalMode=Indev`,
`SurvivalDeath=true`, the Indev block set applied, spawn inside the house
(yaw 180), and the theme environment in the level config - sky/fog/cloud
colours from the genuine constants, `EdgeLevel`=waterLevel,
`SidesOffset`=groundLevel−waterLevel, `CloudsHeight` (−16 on floating),
`HorizonBlock` water/lava + `EdgeBlock` grass/dirt as the OOB horizon-plane
approximation - which is exactly what `SURV_WORLDINFO` reads, so the client
now gets genuine per-map ground/water/fluid values.

Verified live: six worlds (normal inland / island / floating / hell /
paradise island / woods) generated in ~0.2-0.3 s each at 128×64×128;
block-histogram signatures match each theme (island ~6% ocean + beaches,
hell zero water + lava edge flood + the grass-on-beaches quirk, paradise ×10
flowers + high beaches, woods ×10 logs, floating 96% air multi-layer islands,
diamond ore present, exactly two extended-block wall torches at spawn±2);
fork-client joins show genuine-looking terrain with the mob sim populating
it, the spawn inside the house, and hell's dark red ambience.

Deviations (documented): per-theme sky brightness (hell 7, woods 12,
paradise 16 = always-day) only shapes the generation-time light snapshot and
env colours - the live server light model still uses the shared day/night
clock; `SurvivalTheme.Floating` folds the floating TYPE into the theme enum,
so a floating hell map's config reads Floating (world content is still hell);
findSpawn's 1M-attempt sky fallback drops to the sampled surface column
instead (same graceful deviation as the client port).

**Follow-up fix (same session): `SURV_WORLDINFO` v2.** Floating worlds
exposed that the v1 u8 ground/water fields clamp the genuine negative levels
to 0 (a dirt horizon plane appeared under the islands). The SurvivalTest CPE
ext is now **version 2** on both sides and `SURV_WORLDINFO` carries the
levels as i16 BE (see the wire table above); the env config the generator
writes (EdgeLevel −127 etc.) was verified correct on disk - the wire was the
only truncation. Verified live: the floating map now shows open sky + the
genuine below-island clouds (CloudsHeight −16) instead of the dirt plane.
Also fixed while there (client): `Server.SupportsSurvival` was never reset in
`Server_ResetState` on reconnect (despite a comment claiming it was) - both
it and the new `SurvivalExtVersion` now reset.

### `.mclevel` import/export (phase-1 step 3) + the furnace output guard

**Furnace output is take-only** (user-reported): `HandleSlotClick` refuses any
click on furnace container slot 2 while the cursor holds a stack (placing AND
merging), like genuine `SlotFurnace`; the client's singleplayer click path
carries the same rule. Taking from the output is the unchanged cursor-empty
pickup path. Verified live: with the furnace open, a click on the output with
coal held left the slot empty; the next click dropped the coal into the fuel
slot normally.

**`.mclevel` I/O** (`McLevelImporter` extended + new `McLevelExporter`,
`Levels/IO/`): the server now round-trips Indev's native format through the
same `SurvivalBlocks` bijection the generator uses.

- *Import* (`/Import`, files in `extra/import/`): after the stock
  Blocks/Spawn/env read, genuine ids ≥ 50 expand through
  `FromIndev` + the `Data` array's metadata **high nibble** (`ApplyDataMeta`)
  into the view-id space - chest/furnace facings 71-82, farmland 83/84, crop
  stages 85-92, wall torches 94-97 - and ids > 65 become extended custom
  blocks (`ConvertCustom`). Imported maps come out survival-ready
  (`SurvivalMode=Indev`, `SurvivalDeath=true`); the theme is recognised from
  the genuine sky colours (below-zero water level ⇒ Floating), and the OOB
  horizon planes normalise exactly like the generator (still fluid,
  grass/dirt sides).
- *Export* (`/Survival export <name> <level>` → `extra/import/<name>.mclevel`,
  immediately `/Import`-able): hand-rolled gzip NBT writer emitting the
  client's `MCLevel_Save` schema - `About`, the `Environment` set (colours,
  signed `Surrounding*Height`, always-grass `SurroundingGroundType`, genuine
  fluid id, `TimeOfDay` from the global clock), `Map` with
  `Blocks`=`ToIndev(view)` and `Data`=`DataMeta(view)<<4 | 0x0F` (full light),
  a minimal `LocalPlayer` entity at the spawn (genuine Indev expects one),
  and chest/furnace `TileEntities` with live contents via
  `SurvivalInventory.SnapshotContainers` - one entry per container *block*
  (genuine Indev NPE-crashes opening a chest with no tile entity), empty for
  never-opened containers.

Verified live: export gt1 → `/Import` → per-cell view-id comparison of the
two `.lvl` files = **0 of 1,048,576 cells differ**, env/survival properties
identical, the imported map re-applies the block set on load, and the client
spawns inside the round-tripped house (chest/furnace/workbench/wall-torch
views intact); a furnace loaded with coal exports its tile entity with the
stack in fuel slot 1. Deliberate v1 gaps: imported `TileEntities` contents
are NOT restored (the container registry is session-scoped - persistence is
a known deviation), player inventory/mob entities are not exported, and
`TimeOfDay` imports as nothing (global clock).

### Placement shaping + block-def polish (user request)

The server now mirrors the client's SP placement handling
(`IndevTest_BlockChanged` + `IndevTest_CanPlaceBlockAt`) in the block
bridge, so multiplayer placements come out shaped authoritatively
(`ValidateIndevPlace` in `SurvivalInventory.cs`; the canonical place is
cancelled and the directional view broadcast via `lvl.UpdateBlock`, which
also confirms - or corrects - the fork client's local guess):

- **Furnaces and chests face the placer** (BlockFurnace.setDefaultDirection):
  the placer's yaw quadrant picks Indev facing metadata 3/4/2/5 - the exact
  client formula, with yaw as the wire byte (`(RotY*4+128)>>8 & 3`).
- **Torches wall-mount** off their support (BlockTorch.onBlockAdded's
  -X/+X/-Z/+Z/floor order); an unsupported torch placement is refused
  before anything is consumed. "Normal cube" is approximated as solid
  collide + light-blocking (glass/leaves/plants/slabs excluded), matching
  genuine's isBlockNormalCube closely. Deviation: the clicked-face override
  (onBlockPlaced) needs face info the classic place packet doesn't carry -
  when several supports exist the auto pick wins and the echo corrects the
  client (quality-pass candidate: ride the face byte on a placement intent).
- **Chest triples/L-shapes are refused** (BlockChest.canPlaceBlockAt: at
  most one neighbouring chest, never one already half of a double) - stock
  clients could previously build shapes the large-chest pairing can't open.
- The shaping also runs on **creative maps** and for **permitted stock
  builders** (visitors=allow / referees), so the world stays consistent
  regardless of who builds.

Block-set finishing (`SurvivalBlocks.cs`):

- **The CPE leftovers are gone from Indev maps**: turquoise wool(59),
  ice(60), pillar(63), crate(64), stone brick(65) hold no genuine Indev
  block (the client's nonGenuine list). They are now defined with their CPE
  default appearance but hidden from the block menu (`InventoryOrder 0`,
  owner /lb defs respected), and the bridge refuses placing any 50-65 id
  that isn't part of the set - previously they placed FREE (cost 0
  passthrough) on survival maps.
- **Torch defs are proper thin columns** for stock clients: the old
  full-size X sprite is now a cube def whose bounds crop the tile to the
  genuine 2/16-wide, 10/16-tall stick (top tile 117 shows the ember),
  standing and wall views alike. The def model can't express the genuine
  wall tilt, and offsetting the column would move the crop off the torch
  pixels - so wall views render centred (the fork draws the real tilted
  geometry locally). Farmland 15/16, crops' 4/16 pick box, sources, gears
  and the container facings were audited against the client table - already
  faithful.

Verified live (fork client + gdb-driven placements on the round-tripped
map): standing torch on open floor; floating torch refused (cell reverts,
nothing consumed); furnace -> faced view 76 and chest -> 72 matching the
placer's yaw; second chest forms a double; third chest refused (the cell
reverted to the wall planks it replaced); a torch beside the placed furnace
wall-mounts onto it (94, -X first); pillar(63) refused; inventory counts
exact (refusals consume nothing); the def stream shows the torch mini
columns + the five hidden leftovers.

**NEXT SESSION (user-requested): an in-depth bug & quality pass** over the
whole survival stack - both repos, all phases landed so far. Known candidates
to start from: buried-spawn death loops (grounding only fixes floating
spawns), the round-2 gdb multi-call inventory anomaly from the block-set
session (unreproduced), per-map skylight, container-click range checks, and
a sweep for stale comments like the `Server_ResetState` one above.

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

### Command refactor: /Survival tools split into standalone commands

The operational tools were moved out of `/Survival` into their own commands
(the per-map config - `off/classic/indev`, `theme`, `visitors`, the
`enhanced/creative/pvp/deathdrops` flags - stays on `/Survival`, which is a
config command like `/Map`). Names that collide with existing core commands
(`/Spawn`, `/Time`, `/Give`, `/Inv`) take a `Surv` prefix; the rest are bare:

| was | now |
|---|---|
| `/Survival spawn`   | `/SurvSpawn` |
| `/Survival mobs`    | `/Mobs` |
| `/Survival spawner` | `/Spawner` |
| `/Survival time`    | `/SurvTime` |
| `/Survival inv`     | `/SurvInv` |
| `/Survival give`    | `/SurvGive` |
| `/Survival export`  | `/Export` |

Each is a self-contained `Command2` in `MCGalaxy/Commands/World/` (logic moved
verbatim from `CmdSurvival`, arg indices shifted down one since the subcommand
token is gone), registered in `Command.RegisterAllCore` and listed in
`MCGalaxy_.csproj`. `/Survival` with an unknown/removed subcommand now falls
through to its help, which points at the new tool commands. All Operator+,
type World - permissions unchanged from the parent.

Verified live: `/help` for all seven; `/SurvTime noon` sets the clock;
`/Spawner`/`/Mobs`/`/SurvSpawn zombie`/`/Export roundtrip2 gt1` run against
the console's main level; the console-arg guards fire; and the arg shift is
correct - `/SurvGive iron_pickaxe 1 CCUser` + `/SurvGive coal 32 CCUser`
landed id 257 x1 and id 263 x32 (confirmed via `/SurvInv CCUser`).

### Server-side review pass: 13 confirmed bugs fixed

An adversarial multi-agent review of the whole server survival stack (each
finding verified against the ClassiCube client oracle before it survived)
surfaced 19 real findings -> 13 distinct bugs, all fixed this pass:

**Concurrency / lifecycle**
- **Container.Slots data race (item dup/loss).** `contLock` only guarded the
  registry dictionary; the furnace tick (scheduler thread) and HandleSlotClick
  (network threads, incl. two players sharing one chest) read-modify-wrote the
  same `Container.Slots` array with no common lock. Now every container-slot
  path locks `contLock`: TickFurnaces mutates + streams under it (block flips
  collected and applied after release), HandleSlotClick's container branch does
  its read/apply/write/echo under it (click math factored into `ApplyClick`),
  StreamContainer snapshots under it. Deadlock-safe - nothing takes a send lock
  then contLock. Live-verified: a furnace still smelts end-to-end (iron ore ->
  ingot) with items loaded via the locked click path.
- **contRegistry leaked unloaded levels.** The `Dictionary<Level,...>` was never
  pruned, pinning every unloaded map's block array forever. New
  `PruneRegistry(loaded)` called from the mob tick's prune sweep.
- **Cross-level chest looting.** A player's open-container `OpenRef` survived a
  `/goto`, so a SLOT_CLICK from another level looted the container they left.
  Fixed both ways: HandleSlotClick/HandleResultClick refuse a ref whose
  `open.Lvl != p.level`, and SurvivalNet.OnJoinedLevel clears it on level change.
- **ClearMirror vs SyncMirror race.** `/Survival off` iterated a spectator's
  mirror `Ids` on the command thread while the mob tick mutated it. Added a
  per-MirrorState lock taken by both.

**Fidelity to the client oracle**
- **Chest lid used IsSolid, not the normal-cube rule** - glass/leaves/slabs
  above a chest wrongly blocked it. Now uses the shared `NormalCube` predicate.
  Live-verified: glass above -> opens, stone above -> blocked.
- **Furnace output capped at 99, oracle caps at 64** (matters for block results
  glass/stone). Now a fixed `FURNACE_OUTPUT_MAX = 64`.
- **Lava cushioned fall damage** - only water should. Reset changed to `if (inWater)`.
- **Void: -16 vs oracle -32, and applied on c0.30 maps.** VOID_Y -> -32 and the
  void branch gated on `indev`.
- **Liquid box missing the 0.4 vertical shrink** (ST_InLiquid). BoxTouches now
  insets minY/maxY by 0.4.
- **Creeper fuse latched** when it lost its target mid-swell (stayed swollen
  forever, re-detonated instantly on re-aggro). AttackAI now winds the fuse down
  every tick a creeper has no target.
- **Mining TNT gave a free TNT block** (infinite-TNT dupe, TNT is craftable).
  MiningDrops now drops nothing for TNT (the primed explosion is a phase-5 item).
- **Runtime Indev block defs could persist to blockdefs.json** via a stray /lb,
  then linger after the map went non-survival + a restart. Sync now strips any
  Indev-template-matching def from a non-survival map on load.

**Logic**
- **EnableHazards grounded the spawn under water/lava** (drown/burn respawn
  loop), and skipped the bottomless-column warning within FallHeight of the
  void. The descent now stops at the first solid OR liquid surface, warns
  (never grounds) over liquid/void, and checks that before the early return.

Six findings were adversarially REJECTED as not-real (e.g. a claimed nextMobId
cross-level race - ids segregate by level; a WORLDINFO == vs >= version gate -
no current desync at ext v2).

### Right-click item intents: hoe / seeds / food (phase-4 tail)

The `SURV_USE_ITEM` intent now carries held-ITEM uses, not just container opens:

- **Client** (`SurvivalTest.c`): `TryUseBlock`'s MP branch sends `USE_ITEM` for a
  held hoe or seeds (targeted at the clicked block), and `TryEat`'s MP branch
  sends a TARGETLESS `USE_ITEM` (sentinel x=y=z=-1, face 0xFF) for a held food.
  Both were previously no-ops in MP (deferred to the server).
- **Server** (`SurvivalInventory.HandleUseItem`): restructured around a
  `hasTarget` flag (a sentinel/out-of-reach coord = no target). With a target it
  runs container opens (unchanged), then `UseHoe` / `UseSeeds`; targetless (or
  after a non-matching target) it runs `EatFood`. New helpers, all matching the
  client oracle:
  - `UseHoe`: grass (no solid above) or dirt → farmland (`FromRaw(FARMLAND)`);
    the hoe wears 1 via `DamageHeldTool`; tilling grass has a 1/8 chance to yield
    a seed (v1: straight to inventory — the drop entity is phase 5).
  - `UseSeeds`: seeds on farmland (air above) → stage-0 crop in the cell above;
    one seed consumed.
  - `EatFood`: heal the food's `param` value (`SetHealth(cur+heal)`), consume one;
    an eaten soup leaves its empty bowl (soups don't stack).
  - `DamageHeldTool`: `ItemStack.damageItem` — shatter (empty the slot) once
    damage exceeds `32 << tier` (64 for flint&steel).
- `SurvivalItems` gained `IsHoe` / `IsFlintSteel` / `FoodHeal` / `MaxDurability`
  and the `SEEDS`/`SOUP`/`BOWL` id constants.

Deferred: **flint&steel → fire** (fire needs the phase-5 spread/burnout tick
system) and **mining-tool durability** (wear on block break, in the mine path).

Verified live (server-authoritative, via `/Export` + `/SurvInv` since gdb-injected
block placements desync the client's local view): tilling grass produced genuine
farmland (60) and cost the hoe 1 durability; planting seeds produced a genuine
crop (59) and consumed a seed (10→9); eating bread consumed the stack.

### Read-only for Classic clients on survival "visitor" maps (BlockPermissions)

User request: stop the flicker-then-revert when a non-survival (Classic) client
right/left-clicks on a survival map they can't build on - while Indev survival
clients keep full build/break.

The survival block bridge already CANCELS a visitor's edit server-side, but the
client shows the change locally first and only snaps it back when the revert
arrives (the "weird look"). The fix pushes the CPE BlockPermissions state up
front so the client never shows the edit:

- `SurvivalNet.BlocksReadOnly(p)`: true for a non-survival client on a survival
  map that isn't creative/Allow and isn't a referee - i.e. exactly the block
  bridge's "visitor" case (mirrors OnBlockChanging so wire perms and server
  enforcement agree). Indev survival clients, creative maps, Allow maps and
  referees return false (build normally).
- `Player.SendAllBlockPermissions` now forces place=delete=false for every block
  when `BlocksReadOnly` is true, so a Classic visitor gets a genuinely read-only
  map (no place, no delete) instead of the cancel-and-revert.
- `SurvivalNet.RefreshLevel` re-sends block permissions to everyone on the level,
  so toggling `/Survival off` / `visitors allow` flips the read-only state live.

No new config: it enforces the existing `visitors visitor` policy (the default)
more cleanly, client-side too. The block bridge stays the authoritative backstop.

Build-clean; live confirmation pending (the test rig was unstable this session).

---

## HANDOFF — next session pickup (write-up of in-chat plans)

Everything below was designed/decided in conversation but not yet built, so it's
recorded here as the durable bridge. Branch (both repos):
`claude/mock-survival-server-33jx1q`.

### Pending LIVE verification (code committed + build-clean, rig was down)
The test rig degraded mid-session (server boots but won't bind :25565 - an
environment fault, not our code). Two landed changes still want a live check on
a healthy rig:
- **Read-only visitor maps** (`SurvivalNet.BlocksReadOnly` + `SendAllBlockPermissions`):
  connect a NON-survival CPE client to a survival `visitors visitor` map and
  confirm it receives `SetBlockPermission` place=delete=0 for all blocks (the
  `perm_client.py` synthetic in scratchpad checks exactly this), and that an
  Indev survival client on the same map still builds/breaks.
- **`/SurvivalGive [player] [item] <amount>`**: confirm arg order + default 1
  in-game (`/survgive` alias still works).

### Command rework #2 — `/inventory <player>` GUI (APPROVED, ready to build)
A GUI inventory viewer/editor, built on the existing container-GUI plumbing
(CONT_OPEN/CONT_SLOT/SLOT_CLICK). Locked design:
- New CONT_OPEN **kind = 5 (player inventory)**, slotCount 40 = **36 main + 4 armor**.
- Server: a variant OpenRef that targets a `PlayerInv` (not a Container tile
  entity). GetContSlot/SetContSlot/EchoContSlot branch on the kind so the view
  reads/writes the TARGET's inventory; echo to every viewer of that target.
- Permission tiers (MCGalaxy ranks): **Operator (80)+ = view** (SLOT_CLICK on the
  view rejected); **Admin (100)+ = edit** via an Admin extra-perm - chest-style
  drag model (items move between the target's inventory and the admin's own;
  a leftover cursor refunds to the admin on close).
- `/inventory <player>` becomes the command; supersede `/SurvInv` (keep as alias).
  A non-survival (Classic) viewer can't render a GUI -> fall back to the text dump.
- Client: handle CONT_OPEN kind 5 (title "<player>'s inventory", 40-slot layout).
  This is the part that NEEDS the graphical rig to verify.
- Risk note: this generalizes the tested container OpenRef system, so build it
  with the rig UP and re-test chests/furnaces after.

### Command rework #3 — `/spectate <player>` (design only)
Passive follow (like `/possess` but view-only) + mirror the target's open GUI to
the spectator read-only (reuses the #2 inventory/container streaming). Open
questions to settle first: first-person vs third-person; spectator hidden/frozen
to others; whether to mirror the target's HUD (health/hotbar). Do AFTER #2 since
it reuses the inventory-view streaming. Usable by Operator+.

### Block drops (phase 5) — design approach (researched, not built)
Client already has the full SP system (`SurvivalTest.c`: `DropItem` array,
`SpawnDropAtEx` with the genuine pop velocity, `DropPhysics`, `DropTryPickup`,
5-min despawn, death/mob/TNT drops) - all SP-gated (`if (ServerDriven) return`),
and there are NO MP handlers yet (SurvivalNet.c switch ends at CURSOR 0x25).
Reserved wire (ClassiCube SurvivalNet.h): `SURV_DROP_SPAWN 0x30` (dropId, item,
count, pos, vel, rot0), `SURV_DROP_PICKUP 0x31` (dropId, pickerEntity),
`SURV_DROP_REMOVE 0x32` (dropId, reason), intent `SURV_DROP_ITEM 0x86`.
Recommended design: **server owns drops** (spawn/pickup/despawn authoritative);
send DROP_SPAWN with initial pos+velocity and let the CLIENT run its existing
deterministic DropPhysics locally (drops settle to the same resting spot, so exact
sync isn't needed) while the server sends PICKUP/REMOVE. Un-gate the client's drop
render/physics for MP but make pickup/despawn server-driven; add MP appliers
HandleDropSpawn/Pickup/Remove. Rewire the mine path (currently straight-to-
inventory) + death drops + mob drops + TNT + the hoe-grass 1/8 seed to spawn drop
entities instead. Then landed-arrow pickups reuse this for projectiles (#3 of the
big three). NEEDS the graphical rig (entity physics/rendering) to verify.

### The three big features, recommended order
right-click intents ✅ done -> **block drops** -> **projectiles (arrows)**
(projectiles reuse drops for the landed-arrow item). All need the graphical rig.
