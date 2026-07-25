# The SurvivalTest sub-protocol — an implementer's guide

*The complete wire specification of the survival multiplayer sub-protocol spoken
between the survival-test ClassiCube client fork and the MCGalaxy server fork,
written for anyone implementing **their own compatible server** (or client).
The reference implementations are the contract: client
`src/SurvivalNet.h` / `src/SurvivalNet.c` (ClassiCube fork), server
`MCGalaxy/Network/SurvivalNet.cs` (MCGalaxy fork). This document mirrors them;
where prose and code disagree, the code wins — and please fix the prose.*

*Companion docs (in the ClassiCube fork): `doc/survival-handshake.md` (the
original client-foundation rationale), `doc/networking-plan.md` (the full
design plan this grew from). This file exists in both repos
(`doc/survival-protocol.md` client-side, `SURVIVAL_PROTOCOL.md` server-side) —
keep them in sync when the wire contract changes.*

---

## 1. What this protocol is

A **server-authoritative** survival layer (Minecraft Classic 0.30 Survival
Test and Indev gamemodes) grafted onto the Minecraft Classic protocol via
standard CPE. The server simulates everything — mobs, health, inventory,
crafting, containers, drops, arrows, TNT, day/night, growth, fire, fluids —
and streams compact state to capable clients. The client renders that state
and sends **intents** (attack, use, click, drop, respawn), never outcomes.

Design invariants, in order of importance:

1. **A stock client must be completely unaffected.** No survival bytes are
   ever sent to a client that didn't negotiate the capability; a survival
   client on a stock server behaves exactly like stock ClassiCube.
2. **The server owns the simulation.** Client messages are requests. The
   server validates every one (reach, cooldown, slot bounds, container
   access, death state) and answers with authoritative state. There is no
   client prediction of inventory or health — echo-only.
3. **Capability and activation are separate layers** (see §3). Capability is
   per-connection; being *on a survival map* is per-map state that flips as
   the player moves between levels without reconnecting.

## 2. Transport

### 2.1 Capability: the `SurvivalTest` CPE extension

Both sides advertise the extension in the normal CPE `ExtInfo`/`ExtEntry`
exchange at login, before any map data:

```
ExtEntry  name = "SurvivalTest"   version = 3
```

The effective wire version is `min(clientVersion, serverVersion)` — standard
CPE rules. Version history (a client/server MUST accept peers on lower
versions and speak the older layouts to them):

| Ext version | Meaning |
|---|---|
| 1 | Base protocol. `SURV_WORLDINFO` carries u8 ground/water levels. |
| 2 | `SURV_WORLDINFO` ground/water become **signed i16** (floating-island maps use genuinely negative levels, e.g. ground −128 — the u8 clamp caused a spurious dirt horizon plane). |
| 3 | `SURV_CONT_OPEN` kind 5 (player-inventory viewer panel, `/Inventory` and `/Spectate`). |

If you change any byte layout, **bump the extension version on both sides**
and keep emitting the old layout to old peers. Do not invent a parallel
version number: the negotiated ext version is the authoritative one. (The
`protoVer` byte in `SURV_HELLO` is reserved for same-ext-version
sub-revisions and is currently always 1; receivers ignore it.)

### 2.2 Data: CPE PluginMessages on channel `0xB0`

All survival traffic rides the standard **PluginMessages** CPE extension
(opcode `0x35`: `[0x35][channel:1][payload:64]`, 66 bytes on the wire) on the
single reserved channel:

```
SURVNET_CHANNEL = 0xB0
```

Rules:

- Every message is `[id:1][fields…]` inside the fixed **64-byte** payload.
  Unused tail bytes are zero on send and ignored on receive.
- **Never read past byte 63.** Bounds-check every inbound frame; a message
  whose declared run/count would overflow the frame is clamped or dropped.
- Multi-byte integers are **big-endian**.
- Messages that don't fit in one frame are **chunked by design** (see
  `SURV_INV_FULL`: a base-slot + run-of-N pattern, max 12 slots per frame).
  There is no generic fragmentation layer — every message type is defined to
  fit or to chunk explicitly.
- Channel `0xB0` is a shared, unregistered namespace. Do not reuse it for
  anything else on a server that negotiates `SurvivalTest`.
- TCP gives ordering and reliability; there are no sequence numbers or acks.

### 2.3 Common field encodings

| Kind | Encoding |
|---|---|
| entity/mob/drop/arrow/TNT positions | `i16` fixed-point, `coordinate × 32` |
| drop velocity | `i16`, `blocks-per-second × 512` |
| arrow / TNT velocity | `i16`, `blocks-per-TICK × 1024` (they simulate per-tick) |
| yaw / pitch (mobs) | `u8`, `degrees × 256 / 360` |
| yaw / pitch (`SURV_FIRE_ARROW`) | `u16`/`i16`, `degrees × 100` (yaw normalised to [0,360)) |
| item ids | `u16`: `< 256` = block id (classic/Indev block space), `≥ 256` = item, encoded as `256 + Indev item local id` (e.g. seeds = 256+39) |
| block coordinates (`SURV_USE_ITEM`) | `i16` per axis, whole blocks; `(-1,-1,-1)` + face `0xFF` = "no target" sentinel |
| world time | `u16`, ticks `0..23999` (0 sunrise, 6000 noon, 12000 sunset, 18000 midnight) |
| health | `u8`, `0..20` (20 = 10 hearts) |
| score | `i32` BE (not i16 — an early draft said i16; the wire is i32) |

### 2.4 The inventory slot space

One flat `u16` slot index space is used by `SURV_INV_*`, `SURV_SLOT_CLICK`:

| Range | Meaning |
|---|---|
| 0–8 | hotbar |
| 9–35 | main storage (27 slots) |
| 36–44 | crafting grid (2×2 pocket uses 36–39; 3×3 workbench uses all 9) |
| 45–98 | the **open container's** cells, container-relative +45 (chest 27, large chest 54, furnace 3: input/fuel/output) |
| 99–102 | armor: 99 boots, 100 leggings, 101 chestplate, 102 helmet |

The crafting *result* is a virtual slot — never addressed by index; it is
taken via `SURV_RESULT_CLICK`.

## 3. The two-layer handshake

**Layer 1 — capability (per connection):** negotiated once via CPE at login.
The server records `hasSurvival` per session; the client records
`Server.SupportsSurvival` + the negotiated ext version. This can never change
mid-connection.

**Layer 2 — activation (per map):** MCGalaxy-style servers are multi-level; a
player `/goto`s between a plain build map and a survival map without
reconnecting. So "is *this* map survival?" rides a per-map message:
`SURV_HELLO`, sent after every level send. The client hard-resets its
activation to *off* on **every** map change and stays off until (unless) a
fresh HELLO arrives. A HELLO with mode 0 deactivates mid-map (used by live
config changes, e.g. the `/Survival` command).

The single predicate everything hangs off (client side,
`SurvivalNet_ServerDriven()`):

```
serverDriven = !singleplayer && SupportsSurvival && helloMode != 0
```

While true, the client applies streamed state and sends intents, and every
local mutation source (damage, mob AI, drops, arrows, TNT, furnace tick,
day/night advance, eating, tool wear, growth) is gated OFF. The server-side
mirror (`SurvivalNet.Active(p, lvl)`):

```
active = session.hasSurvival && level.SurvivalMode != Off
```

**The golden routing rule: never send a survival byte to a session where
`active` is false.** Both directions enforce it independently.

### 3.1 Handshake sequence

```
TCP connect
  ├─► Player identification (0x00, magic 0x42 = "I speak CPE")
  ├─► CPE ExtInfo/ExtEntry exchange  ── both advertise SurvivalTest v3
  ├─► LevelInitialize / chunks / LevelFinalize   (standard Classic)
  │     (server also pushes level-scoped BlockDefinitions for the Indev
  │      block set, standard CPE — before or around the handshake)
  ├─► SURV_HELLO       mode + flags for THIS map        ─┐
  ├─► SURV_WORLDINFO   ground/water/theme/floating       │ the per-map
  ├─► SURV_TIME        seed the clock immediately        │ handshake burst
  ├─► SURV_HEALTH      current health + score            │ (server sends all
  ├─► SURV_MOB_SPAWN × N     the level's live mobs       │  of these in one
  ├─► SURV_DROP_SPAWN × N    drops at rest               │  go on level join,
  ├─► SURV_ARROW_SPAWN/STICK × N   in-flight + stuck     │  and again on a
  ├─► SURV_TNT_SPAWN × N     primed TNT mid-fuse         │  live /Survival
  ├─► SURV_ARROW_AMMO        quiver count (c0.30 HUD)    │  config refresh)
  ├─► SURV_INV_FULL × 4 + SURV_CURSOR   inventory       ─┘ (NOT on creative
  │                                                        maps — see §6.4)
  └─► …steady state: TIME @1 Hz, mob deltas @20 TPS, event-driven the rest…
      client ─► intents (0x80–0x88) as the player acts
```

`SURV_PLAYER_EQUIP` for other players is *not* part of the burst — it is sent
per entity as that entity spawns into view (entity ids aren't stable at
handshake time).

## 4. Server → client messages

Byte 0 is always the id. Offsets below start at the id byte.

### 0x01 `SURV_HELLO` — per-map activation
| Off | Size | Field |
|---|---|---|
| 1 | 1 | mode: 0 off, 1 = c0.30 Survival Test, 2 = Indev |
| 2 | 1 | flags: bit0 enhanced, bit1 creative, bit2 pvp, bit3 deathDrops |
| 3 | 1 | protoVer (reserved sub-revision, currently 1; ignore) |

Client behaviour: unknown mode (> 2) is treated as **off** — never
half-activate a sim you don't understand. On mode 0 the client tears down all
per-map survival state. Mode/flags override the client's local gamemode
options for the duration of the map.

### 0x02 `SURV_WORLDINFO` — non-Classic world parameters
v2+ layout (negotiated ext ≥ 2):
| Off | Size | Field |
|---|---|---|
| 1 | 2 | groundLevel, **i16** (may be negative on floating maps) |
| 3 | 2 | waterLevel, **i16** |
| 5 | 1 | fluid block id (surface fluid, raw client id) |
| 6 | 1 | theme: 0 normal, 1 hell, 2 paradise, 3 woods, 4 floating |
| 7 | 1 | flags: bit0 floating |
| 8 | 1 | sides ("bedrock") block id |
| 9 | 1 | horizon/edge block id |

v1 layout: same fields but ground/water are u8 at offsets 1 and 2 (then
fluid 3, theme 4, flags 5, sides 6, edge 7). Environment *colours* are NOT in
this message — they ride the stock CPE `EnvColors` path.

### 0x03 `SURV_HEALTH`
| Off | Size | Field |
|---|---|---|
| 1 | 1 | health 0..20 |
| 2 | 4 | score, **i32** BE |

Client: a decrease plays the hurt tilt + sound; 0 enters the death state
(death camera + Game Over screen, no local drops — drops are server state); a
rise while dead revives (the server repositions via a normal teleport first).

### 0x04 `SURV_TIME`
| Off | Size | Field |
|---|---|---|
| 1 | 2 | worldTime u16, 0..23999 |
| 3 | 1 | skyLight 0..15 (server's eased ramp) |

Sent at 1 Hz (the reference server advances 20 world ticks per real second =
a 20-minute day; the clock is **per level** and persists with it). The
reference client uses only worldTime and computes the genuine Indev
celestial-angle lighting locally; simpler clients may use the skyLight byte.
c0.30 mode has no day/night — clients ignore TIME there.

### 0x10 `SURV_MOB_SPAWN`
| Off | Size | Field |
|---|---|---|
| 1 | 2 | mobId u16 (server-assigned, unique per level) |
| 3 | 1 | type: 0 zombie, 1 skeleton, 2 pig, 3 creeper, 4 spider, 5 sheep |
| 4 | 6 | pos: 3 × i16, coord×32 |
| 10 | 1 | yaw u8 (deg×256/360) |
| 11 | 1 | pitch u8 |
| 12 | 1 | health |
| 13 | 1 | spawn flags: bit0 helmet, bit1 armor, bit2 fur (sheep) |

### 0x11 `SURV_MOB_MOVE`
| Off | Size | Field |
|---|---|---|
| 1 | 2 | mobId |
| 3 | 6 | pos: 3 × i16 |
| 9 | 1 | yaw |
| 10 | 1 | pitch |

Sent (delta-compressed: only when the quantised pose changed) from the 20 TPS
mob tick. The client interpolates between updates.

### 0x12 `SURV_MOB_STATE`
| Off | Size | Field |
|---|---|---|
| 1 | 2 | mobId |
| 3 | 1 | health |
| 4 | 1 | flags: bit0 hurt, bit1 fuse (creeper swell), bit2 onFire, bit3 graze, bit4 dead, bit5 noFur (sheared) |

The client derives cosmetic timers (hurt flash, swell, fire overlay, graze
dip, death keel-over) from flag **edges**, not levels — send state
transitions and the client animates them.

### 0x13 `SURV_MOB_DESPAWN`
`[1] mobId u16, [3] reason u8`. The dead flag in MOB_STATE plays the death
animation; DESPAWN removes the puppet.

### 0x20 `SURV_INV_FULL` — chunked inventory sync
| Off | Size | Field |
|---|---|---|
| 1 | 1 | baseSlot |
| 2 | 1 | runLen (**max 12** — 3 + 12×5 = 63 bytes) |
| 3+ | 5 × runLen | per slot: id u16, count u8, damage i16 |

A full sync is several frames: slots 0..44 in runs of 12, then 99..102, then
`SURV_CURSOR`. Receivers MUST clamp runLen so `3 + run×5 ≤ 64`.

### 0x21 `SURV_INV_SLOT` — single-slot echo
`[1] slot u8, [2] id u16, [4] count u8, [5] damage i16`. The echo path for
every click/pickup/consume result.

### 0x22 `SURV_CONT_OPEN`
| Off | Size | Field |
|---|---|---|
| 1 | 1 | kind: 0 force-close, 1 chest (27), 2 furnace (3), 3 large chest (54), 4 workbench, 5 player-inventory (ext v3+) |
| 2 | 1 | slotCount |
| 3 | 1 | (kind 5 only) target entity id as this viewer sees it; 0xFF = not visible |
| 4 | 1 | (kind 5 only) solo: 1 = single-panel spectate view, 0 = two-panel /Inventory view |

Kind 0 is the server force-closing an open screen (container destroyed under
it) — no `SURV_CONT_CLOSE` comes back. Kind 4 opens the client's 3×3 grid;
the grid cells ride the normal craft slots 36–44, no container cells. Kind 5
proxies another player's inventory as 40 container cells (0–26 storage,
27–35 hotbar, 36–39 armor boots→helmet).

### 0x23 `SURV_CONT_SLOT`
`[1] slot u8 (container-relative 0..53), [2] id u16, [4] count u8,
[5] damage i16`. Contents follow CONT_OPEN, and every later change is echoed.

### 0x24 `SURV_FURN_PROG`
`[1] burn 0..12, [2] cook 0..24` — pre-scaled flame height and arrow width
for the open furnace GUI, sent while it changes.

### 0x25 `SURV_CURSOR`
`[1] id u16, [3] count u8, [4] damage i16` — the server-owned held stack
(what the mouse carries between clicks). Echoed after every click.

### 0x30 `SURV_DROP_SPAWN`
| Off | Size | Field |
|---|---|---|
| 1 | 2 | dropId u16 |
| 3 | 2 | itemId u16 |
| 5 | 1 | count |
| 6 | 6 | pos: 3 × i16 coord×32 |
| 12 | 6 | vel: 3 × i16 blocks/sec×512 |
| 18 | 1 | rot0 (initial spin phase, so drops don't spin in lockstep) |

The client runs the pop-arc + spin locally from pos+vel; the server keeps
the authoritative resting spot and the pickup logic.

### 0x31 `SURV_DROP_PICKUP`
`[1] dropId u16, [3] pickerEntityId u8` — remove with the fly-to-picker
animation. Picker 255 = the receiving viewer themself; 0xFF also doubles as
"not visible" (no animation, just remove).

### 0x32 `SURV_DROP_REMOVE`
`[1] dropId u16, [3] reason (0 despawn / 1 destroyed)` — remove, no animation.

### 0x33 `SURV_ARROW_SPAWN`
| Off | Size | Field |
|---|---|---|
| 1 | 2 | arrowId u16 |
| 3 | 1 | type (0 = c0.30 arrow, 1 = Indev arrow) |
| 4 | 1 | gravity × 100 (u8) |
| 5 | 6 | pos: 3 × i16 coord×32 |
| 11 | 6 | vel: 3 × i16 blocks/**tick**×1024 |

The client simulates the same deterministic c0.30 flight locally from the
spawn state; the server owns hits and sticks and corrects via STICK/REMOVE.

### 0x34 `SURV_ARROW_STICK`
`[1] arrowId u16, [3] pos 3×i16` — snap to the authoritative stuck position
and freeze.

### 0x35 `SURV_ARROW_REMOVE`
`[1] arrowId u16, [3] reason (0 despawn / 1 hit / 2 pickup)`.

### 0x36 `SURV_ARROW_AMMO`
`[1] count u16` — the receiving player's own quiver count (c0.30 HUD).

### 0x37 `SURV_TNT_SPAWN`
| Off | Size | Field |
|---|---|---|
| 1 | 2 | tntId u16 |
| 3 | 6 | pos: 3 × i16 coord×32 |
| 9 | 6 | vel: 3 × i16 blocks/tick×1024 |
| 15 | 2 | fuse ticks remaining, u16 |

The client simulates the PrimedTnt hop/smoke/flash locally.

### 0x38 `SURV_TNT_REMOVE`
`[1] tntId u16, [3] reason (0 detonate / 1 defuse)`. On detonate the client
plays the burst; the actual block destruction arrives as authoritative
standard SetBlock packets.

### 0x40 `SURV_BLOCKMETA` — reserved
Reserved for block metadata (growth stages etc.); not currently sent. The
reference implementation drives visible growth through block changes.

### 0x50 `SURV_PLAYER_EQUIP`
| Off | Size | Field |
|---|---|---|
| 1 | 1 | entityId (as the receiving viewer sees it) |
| 2 | 2 | heldId u16 |
| 4 | 8 | armor: 4 × u16, boots, leggings, chestplate, helmet |

A remote player's held item + worn armor, all as item ids — the client owns
every model/texture and renders them onto the entity. Sent when the equip
changes and when an entity spawns into view; deduplicated server-side.

## 5. Client → server intents

**Every intent is a request.** The server validates it against its own state
and replies with authoritative state (or silently corrects). Malformed or
out-of-context intents are dropped, optionally logged — never crash, never
trust. Intents from sessions that did not negotiate `SurvivalTest` are
dropped outright.

### 0x80 `SURV_ATTACK`
`[1] targetKind (0 mob / 1 player / 2 primed TNT), [2] targetId u16`

Server validation (reference implementation):
- sender alive, map active;
- **reach**: eye-to-target ≤ 4 blocks padded to 6 for latency (the target
  moved since the client swung); reject beyond that;
- kind 1 (PvP) additionally requires the map's pvp flag; target must be a
  survival player on the same map, alive;
- kind 2 defuses the TNT entity (c0.30 melee defuse) if in reach.
- damage is computed **server-side** from the server-known held item
  (c0.30: flat 4; Indev: fist 1, tools by tier, swords 4+tier×2), then armor
  absorption, invulnerability windows, knockback, aggro.
- knockback on a **landed** hit (melee, PvP, arrows, mob melee alike) is
  delivered to the victim via the standard CPE **VelocityControl** extension,
  not this channel: horizontal ADD away from the attacker + vertical SET pop,
  ~0.4 blocks/tick per axis (wire value 1.1 in VelocityControl's jump-height
  units). Hits absorbed by the invulnerability window or armor do not shove.

### 0x81 `SURV_USE_ITEM`
`[1] heldSlot u8, [2] x i16, [4] y i16, [6] z i16, [8] face u8`

Right-click. Target `(-1,-1,-1)` + face `0xFF` is the "no target" sentinel =
right-click air / eat. With a target: the server checks reach to the block,
then dispatches on the **server-known** held item and target block: open
chest/workbench/furnace GUIs (answers `SURV_CONT_OPEN` + contents), hoe
tilling, seed planting, food eating, flint & steel, bucket, etc. The client
never assumes the use succeeded — results arrive as echoes (block changes,
inventory slots, CONT_OPEN).

### 0x82 `SURV_SLOT_CLICK`
`[1] slotIdx u16 (the flat slot space, §2.4), [3] button (0 left / 1 right)`

The server runs the genuine GuiContainer click model on ITS slots + cursor:
- empty cursor: left takes the stack, right takes ceil(half);
- same id: left merges up to max-stack, right places exactly one;
- different id: swap; right-click into empty places one;
- armor slots only accept their matching piece; furnace output is take-only.

Then it **echoes** the touched slot(s) via `SURV_INV_SLOT`/`SURV_CONT_SLOT`
and the cursor via `SURV_CURSOR`. Out-of-range indices, clicks with no open
container into container space, and clicks while dead are rejected.

### 0x83 `SURV_RESULT_CLICK`
No fields. Take the crafting result onto the cursor: server re-evaluates the
recipe from ITS grid slots, applies the take (consume one of each
ingredient), echoes grid + cursor. No-op if the cursor can't accept it.

### 0x84 `SURV_CONT_CLOSE`
No fields. The client closed its container/inventory screen. The server
refunds the cursor stack and the crafting grid back into the inventory
(nothing may be eaten by a UI close), closes its open-container record, and
echoes the result.

### 0x85 `SURV_HELD_SLOT`
`[1] hotbarIndex u8 (0..8)`. Sent on every hotbar selection change. The
server tracks it for place-consume, melee damage, USE_ITEM dispatch and
`SURV_PLAYER_EQUIP` broadcasts.

### 0x86 `SURV_DROP_ITEM`
`[1] slot u8, [2] wholeStack (0 one / 1 all)`. The Q-toss: server takes from
ITS inventory slot, spawns a drop entity flung forward, echoes the slot.

### 0x87 `SURV_RESPAWN`
No fields. Only meaningful while dead: the server repositions the player to
spawn (standard teleport), then restores full health (the health rise is
what removes the client's Game Over screen). A respawn-while-alive is
answered with a corrective `SURV_HEALTH` instead of being applied — otherwise
it would be a free teleport home.

### 0x88 `SURV_FIRE_ARROW`
`[1] yaw u16 (deg×100, 0..35999), [3] pitch i16 (deg×100), [5] kind
(0 = c0.30 Tab-fire / 1 = Indev bow)`

The server checks ammo (c0.30 quiver / Indev arrows item + bow), spawns and
simulates the arrow itself, and broadcasts `SURV_ARROW_SPAWN`. The shooter's
client renders its own arrow from that broadcast like everyone else's.

## 6. Server-side rules that make it work

These are the behaviours a from-scratch server must reproduce beyond raw
message plumbing.

### 6.1 Gating and fallbacks (mixed audiences)

Every level has a survival config (mode, enhanced/creative/pvp/deathDrops
flags, visitor policy). On a survival map you will have *three* audiences
simultaneously:

1. **Survival clients** — full sub-protocol.
2. **Stock/CPE clients** — no survival bytes, ever. Give them graceful
   fallbacks: day/night as scaled `EnvColors`; mobs mirrored as ordinary
   spawned entities (standard AddEntity/teleport packets) so the world
   doesn't look empty; restore env + remove mirrors when the map stops being
   survival.
3. **The visitor policy** for stock clients' *edits*: `Visitor` (default) =
   may look but block changes are rejected server-side AND advertised via
   CPE `BlockPermissions` (place=delete=false, so the client doesn't even
   try); `Allow` = may build (map owner accepts desync); `Deny` = may not
   join. Survival clients, creative maps and referees build normally.

### 6.2 Per-map state and lifecycle

- Mob/drop/arrow/TNT ids are per-level registries; the clock is per-level.
- On level join: send the full handshake burst (§3.1). On leaving: the
  client wipes its own per-map state; the server closes any open container
  and, if the player left while dead, revives them (the death screen belongs
  to the map they died on).
- On a live config change (`/Survival …`): re-sync block defs, re-send the
  handshake to capable players on the map (or a mode-0 HELLO if survival was
  turned off), re-push block permissions to stock clients.

### 6.3 Death flow

Health 0 → hold the player: the client shows the death camera + Game Over
screen and the server suppresses further deaths and does NOT auto-respawn.
Optionally scatter the inventory as drops (the deathDrops flag). Revive on
`SURV_RESPAWN` — or after a **30 s safety timeout** so a client whose intent
was lost is never stranded. Reposition first, then send the health restore.

### 6.4 Creative maps

On creative survival maps the server tracks **no inventory** (free build, no
consume): skip `SURV_INV_FULL`/`SURV_CURSOR` in the handshake — the client
keeps its local palette hotbar — and reject container/item-use sync. The
creative flag rides HELLO bit1.

### 6.5 Echo-only inventory (no prediction)

The client never mutates inventory locally in MP. Click → intent → server
mutates → echo. This is what makes the server authoritative for real; it
also means your server MUST echo promptly (same tick) or clicks feel laggy.

### 6.6 Streaming cadence

| Stream | Cadence |
|---|---|
| `SURV_TIME` | 1 Hz |
| mob move/state | 20 TPS tick, delta-only per mob |
| inventory/container | event-driven echoes |
| drops/arrows/TNT | spawn/remove events; motion is client-simulated |
| equip | on change + on entity visibility, deduplicated |

Anti-flood: the reference server also rate-limits inbound intents implicitly
via validation (reach, cooldowns, dead-state); a hostile client spamming
intents can at worst make the server do cheap validation work.

## 7. Implementing your own server — a build order

Each step is independently shippable and testable against the reference
client; everything past step 2 is optional depending on how much of the
game you want.

1. **Capability**: advertise `ExtEntry("SurvivalTest", 3)`, record
   `hasSurvival` per session, route inbound PluginMessages on 0xB0 to a
   dispatcher that drops traffic from non-negotiated sessions.
2. **Activation**: per-level survival config; send `SURV_HELLO` +
   `SURV_WORLDINFO` after every level send (and TIME + HEALTH seeds). You
   now have a client in survival mode: survival HUD, no local sim, death
   screen on health 0. Implement `SURV_RESPAWN` + the safety timeout.
3. **Clock**: per-level time advance + 1 Hz `SURV_TIME`. (Client renders the
   whole sky/lighting presentation from it.)
4. **Health**: hook your damage sources (falls, drowning, lava…) into
   `SURV_HEALTH`, with the hold-at-zero death flow (§6.3).
5. **Mobs**: entity registry + 20 TPS tick; stream SPAWN/MOVE/STATE/DESPAWN;
   validate `SURV_ATTACK` (reach!); mob melee damages players via step 4.
6. **Inventory**: server-owned slots + cursor; `SURV_INV_FULL` chunked sync
   on join; the GuiContainer click model answering `SURV_SLOT_CLICK`
   with echoes; `SURV_HELD_SLOT` tracking; mining/placing feeds it.
7. **Items & crafting**: item id table (256+ space), recipes for
   `SURV_RESULT_CLICK`, `SURV_USE_ITEM` dispatch (eat/till/plant/ignite),
   tool durability, armor absorption.
8. **Containers**: chest/large chest/workbench/furnace tile entities,
   `SURV_CONT_OPEN/SLOT`, `SURV_FURN_PROG`, close-refund, force-close on
   destroy.
9. **Drops**: drop entities with pickup delay + collection;
   DROP_SPAWN/PICKUP/REMOVE; `SURV_DROP_ITEM`; death scatter.
10. **Arrows & TNT**: server-simulated projectiles/fuses with the
    spawn-and-let-the-client-animate pattern; `SURV_FIRE_ARROW` + ammo.
11. **World sim**: growth, leaf decay, fire, finite fluids, lighting — all
    server-side, visible to clients as ordinary block changes.
12. **Persistence**: per-level sidecar for mobs/containers/clock so state
    survives unload/restart (the reference uses `extra/survival/<level>.sur`
    plus `.mclevel` import/export); per-player inventory files.

**Non-negotiables at every step**: bounds-check every inbound frame; validate
every intent against server state; never send survival bytes to
non-negotiated sessions or non-survival maps; echo authoritatively rather
than trusting the client's view of anything.

## 8. Implementing your own client — the short version

Mirror the gates: activate only on `ExtEntry` + HELLO; hard-reset activation
on every map change; treat unknown HELLO modes as off; never mutate
survival state locally while server-driven — render streams, send intents,
and derive all animation from state *edges*. Ignore unknown message ids on
0xB0 (forward compatibility). Never read past byte 63; clamp declared runs.

## 9. Known sharp edges (learned the hard way)

- **`SURV_HEALTH.score` is i32**, not i16 — early drafts disagreed.
- **Ground/water levels go negative** on floating/hell themes; that's why
  WORLDINFO v2 exists. If you only ever make normal maps, v1 layouts work,
  but negotiate honestly.
- **`SURV_INV_FULL` runLen must be clamped to 12** by receivers — a larger
  declared run would read past the frame.
- **HELLO arrives after the level**, so client components run their map
  hooks with survival OFF first, then re-derive on HELLO. Any client state
  keyed to map load must tolerate that ordering.
- **Creative maps must not stream inventory** — it would wipe the palette
  hotbar the client just built (§6.4).
- **Respawn-while-alive must be rejected** (free teleport otherwise) — answer
  with corrective state instead.
- **Entity id 0xFF is a sentinel** ("not visible to this viewer") in
  DROP_PICKUP and CONT_OPEN kind 5 — every entity id in this protocol is
  *as seen by the receiving viewer*, since Classic entity ids are per-viewer.
- **The furnace output is take-only**; the armor slots are type-checked;
  the close path refunds cursor + grid — a UI interaction must never destroy
  or duplicate items, and only the server can enforce that.
