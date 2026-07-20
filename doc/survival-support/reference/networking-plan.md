# Networking Integration Plan — Multiplayer Indev (and c0.30‑s) over the Classic Protocol

> **Audience:** the next Claude session that will implement multiplayer for this
> fork. Read this whole file before touching any networking code. It is a *plan*,
> not finished work — the previous session did research + design only and made
> **no** network code changes.
>
> **Two repos are in play:**
> - **Client:** this repo — the ClassiCube fork with the Indev / c0.30‑Survival
>   singleplayer simulation (`src/SurvivalTest.c`, `src/IndevTest.c`, `src/IndevGen.c`).
> - **Server:** `https://github.com/UmbreoClaw/mcgalaxy` (a fork of MCGalaxy, C#).
>   It already speaks the Classic protocol + CPE. We modify it, we do **not** start fresh.
>
> **This first networking milestone focuses on Indev.** c0.30‑s comes later; the
> design must leave room for it but you only need to wire Indev end‑to‑end first.
>
> **The client handshake foundation has since landed** (extension negotiation +
> receive/log + send path). Its focused implementation record — file map, message
> flow, wire layouts, and a "is this the best way?" design review — is in
> **`doc/survival-handshake.md`**. Read that alongside §3/§4/§20/§25 here; build
> the server handshake against `src/SurvivalNet.h`, the wire contract.

---

## How to use this document

Read it top to bottom once, then work the **priority checklist** below in order.
§0–§3 are law (don't break Classic, use the gate + PluginMessages). §4–§5 are the
client/server maps. §6 is the wire format. §7 is the roadmap. §8 is your first two
concrete tasks. §12 is the per‑subsystem code map. §13 answers the "who owns what"
questions. §14 is the Indev‑creative target. **§15 is the desync bible — internalise
it before writing sync code.** §16 is the classic‑client policy. §17 is the
step‑by‑step cookbook for the fiddly bits. Update this file as decisions harden.

## Priority checklist (do in this order)

The master ordered plan. Each item names its detail section and the key
pointer. `.mclevel` handling is fully covered — format §18, save lifecycle §28.

**P0 — Foundations (server + the gate; land in sequence):**
- [ ] **Read the code.** Client `src/Protocol.c` (CPE negotiation,
      `CPE_SendPluginMessage` `:891` / `CPE_PluginMessage` `:1551`), `src/Server.c`
      (SP/MP split); server `MCGalaxy/Network/ClassicProtocol.cs` (`HandlePacket`,
      the `0x35` handler), `MCGalaxy/Generator/MapGen.cs`. — §2, §4, §5
- [ ] **Verify MCGalaxy's `0x35` payload width is 64 bytes** (one source said 256).
      Settle before designing any message. — §2.3
- [ ] **Regression baseline:** fork client → *stock* server unchanged. Re‑run after
      **every** networking change. — §9
- [ ] **Port the Indev generator into MCGalaxy** and register **one** `indev`
      generator (type+theme via `MapGenArgs`, dims from the command); it shows up in
      `/os map add` + `/newlvl`. Match the 3 RNG streams (seed / seed+1 burned /
      seed+2) → **seed parity vs SP** (fixed‑seed dump/diff). — §8.1, §31,
      `src/IndevGen.c`, `doc/indev-generation.md`
- [ ] **`SurvivalTest` CPE extension** both sides + `Server.SupportsSurvival`
      (client mirror) + `hasSurvival` (server) + the rule *never send an
      unnegotiated packet*. Prove the gate: stock server ⇒ zero change. — §3, §17.1, §20
- [ ] **Per‑level `SurvivalMode`** (`off|indevCreative|indevSurvival|c030s`) as the
      single source of truth; attach the Indev block collection **per‑level, never
      global** (ids 50–92 shadow CPE decoration only on survival maps). — §30
- [ ] **Texture pack:** bake the Indev PNGs into the client `default.zip`; build one
      Indev‑augmented pack; serve its URL on survival maps only; keep
      `BlockDefinitions` tile indices == `terrain.png` == `Block_Tex`. — §19
- [ ] **`.mclevel` I/O + save folder:** round‑trip the full schema byte‑compatibly
      with `src/Formats.c` (`MCLevel_Save` + parse callbacks); the generator
      persists into `survival/<map>/` (world + per‑player files) on save/unload/
      disconnect/shutdown; temp‑file+atomic‑rename; loaded worlds restore mobs
      (don't re‑run the initial spawn). — §18, §28

**P1 — Indev *creative* multiplayer (first playable; simplest — §14):**
- [x] **Indev Creative** SP mode — `SurvivalTest_CreativeActive()` gates no‑HUD,
      instant no‑drop building with infinite blocks, flight, mobs still spawn;
      **genuine 9‑slot palette hotbar + the Indev `GuiInventory`** (the earlier
      Beta‑1.8 picker was reverted). *(landed, SP)* — §14
- [ ] **Indev Creative** MP — **server‑dictated**: `SURV_HELLO` `bit1=creative`
      sets the resolver's effective state (client ignores its local option), fly
      allowed via `HackControl`; validate every action server‑side. — §14, §20.3, §25
- [x] **`src/SurvivalNet.c` foundation** — extension negotiated
      (`Server.SupportsSurvival`), receive dispatch gated + `SURV_HELLO`/
      `SURV_WORLDINFO` parse/log, `SurvivalNet_Send` wrapper. *(landed)*
- [x] **`src/SurvivalNet.c`** mode‑flip — flip Indev mode from `SURV_HELLO` in MP
      (options path kept in SP; `SurvivalTest_EffectiveGamemode`).
      *(landed — restored from `ed604b6` in the combined two‑repo session and
      integration‑tested against the live MCGalaxy fork server)*
- [x] **Gate the client's Indev sim OFF in MP** — `SurvivalNet_ServerDriven()`
      gates damage/mobs/drops/arrows/TNT/furnace/day‑night‑advance/spawner (+
      random ticks/fire already run only under SP block physics). *(landed)*
- [ ] **Server runs the world sim** — day/night, random ticks, mob spawner — and
      relays block changes; day/night to stock clients via **`EnvColors`**. — §21
- [x] **Mob puppet** — `SURV_MOB_*` → `st_mobs[]`, render‑only, bespoke 16‑bit ids
      (not the Classic entity list). — §15.1, §17.5, §23.1
      *(landed + integration‑tested: SurvivalTest_NetMob* appliers, puppet tick
      keeps only presentation - interpolation, cosmetic timers, sounds)*
- [x] **Classic‑client policy** — creative maps allow build; survival maps
      visitor/deny; never let a non‑survival client edit a survival world. — §16, §20.2
- [ ] **Custom blocks on stock clients** — `DefineBlock`/`DefineBlockExt` (shape +
      draw + textures + bounds), fallback ids for pure Classic. — §22
- [ ] **Milestone:** two fork clients build together in a server‑generated Indev
      world, see the same mobs + day/night, and a stock client can still join safely.

**P2 — Indev *survival* multiplayer (server authority — the hard part):**
- [ ] **Health/damage** server‑side — reconstruct fall from the position stream,
      armor absorption, 20‑tick invuln, knockback via `VelocityControl`; no Indev
      regen. — §13, §23.4
- [~] **Inventory / crafting / containers** — server owns inventory+grid+container+
      cursor; `SURV_SLOT_CLICK`/`RESULT_CLICK`/`CONT_CLOSE` in, echo
      `SURV_INV_SLOT`/`CONT_SLOT`/`CURSOR`. TCP ordering ⇒ no Beta confirm dance;
      echo‑only first, prediction later. — §27
      *(first slice landed + live‑tested: main/craft/armor slots + cursor stream
      (`INV_FULL`/`INV_SLOT`/`CURSOR`), the GuiContainer click model runs
      server‑side with echoes, CONT_CLOSE refunds, and mining/placing feeds the
      server inventory (mine→pickup, place→consume with authoritative revert).
      Still open: recipes, containers/furnace (`0x22–0x24`), `USE_ITEM`, per‑id
      max‑stack/item tables, `PLAYER_EQUIP`.)*
- [ ] **Item drops** — server spawns (RNG) + runs the **deterministic** physics;
      clients simulate from the spawn state (no per‑tick stream); server‑authoritative
      pickup + 6000t despawn. — §26
- [ ] **TNT/explosions** server‑side, sent as `BulkBlockUpdate` + drops + damage;
      client renders only. — §15.4
- [ ] Block‑metadata sync, furnace progress, use‑item (`SURV_USE_ITEM`). — §15.2, §27.5

**P3 — Extras & polish:**
- [ ] **Server admin commands** — one `/Surv` parent (spawn/give/time/god/heal/kill),
      **`defaultRank` Operator/Moderator+**, **auto‑loaded** (public `Command2`
      subclass), each authoritative + streamed. — §29
- [ ] **PvP** — optional, per‑level, **default off**; reuse the mob‑combat path with a
      player target. — §24
- [ ] **Other players' equipment** — `SURV_PLAYER_EQUIP` (armor/held), fork‑only. — §25.1
- [ ] **Interest management** — stream mobs/drops per‑player relevance (Indev is
      mob‑heavy). — §23.3
- [ ] **Then extend the whole stack to c0.30‑s.**

**Always‑on rules:** never break Classic (§0); **receive, don't compute** (§15.0);
**server wins** reconciliation; **per‑session gating** — never send an unnegotiated
packet (§20); **seed/table parity** with SP; **re‑run the regression baseline every
change** (§9).

## 0. The one rule that governs everything

**Never break vanilla Classic.** A normal Classic/CPE server (or a normal Classic
client) must keep working exactly as today. Concretely:

- All new network behaviour is **gated behind a newly‑negotiated CPE extension**
  (working name **`SurvivalTest`**, see §3). If the peer doesn't advertise it,
  the survival net path is *completely inert* and the client behaves as stock
  Classic (creative, server‑authoritative blocks only).
- Do **not** repurpose or resize existing Classic/CPE opcodes. Everything custom
  rides on the already‑standard **PluginMessages** CPE extension (opcode `0x35`),
  which both sides already support.
- Singleplayer must be untouched. The existing SP simulation (`Server.IsSinglePlayer == true`)
  keeps running locally; the net path only engages for `IsSinglePlayer == false`
  **and** the `SurvivalTest` extension negotiated.
- Gate at runtime, not compile time: `if (Server.IsSinglePlayer) { local sim }`
  vs `else if (IsSupported(survival_Ext)) { networked }` vs `else { stock classic }`.

---

## 1. Why this shape

Indev had **no** multiplayer protocol, so there is nothing to port 1:1. We take:

- **Transport** from **Classic + CPE** (what MCGalaxy and this client already speak).
- **The *set of things* that must be networked** as inspiration from **Beta 1.x**
  (the first survival multiplayer): health/damage, entity spawn/move/metadata,
  inventory windows + click transactions, held item, time‑of‑day, block metadata
  (chests/furnaces), item drops/pickups. We reproduce the *concepts*, not the
  byte formats — the byte formats are ours, carried inside PluginMessages.
- **Authority model** from Beta too: **the server is authoritative.** The server
  generates the world, runs mob AI + physics + health + containers + day/night,
  validates player actions, and streams state to clients. The client renders,
  plays sound, drives the GUI, does light prediction, and sends *intents*
  (move, attack, place/break, use‑item, craft, inventory‑click). This is exactly
  why the two server‑side tasks you're being handed (§8) are "port the Indev
  generator into MCGalaxy" and "add the server‑side checks/handling" — the
  simulation's centre of gravity moves to the server.

The current fork's `src/SurvivalTest.c` / `src/IndevTest.c` are a *complete,
source‑faithful survival simulation in C*. That is gold: it is the reference the
server‑side C# simulation must match, and much of the client code is reusable as
the **client‑prediction + rendering** layer. Do not throw it away — split it.

---

## 2. Background you must have loaded

### 2.1 Classic protocol (what already works)

Client packet handlers live in `src/Protocol.c` (`Classic_*` functions), wired in
`Classic_Reset()` via `Net_Set(opcode, handler, size)`. Opcode names are in
`src/Protocol.h` (`enum OPCODE_`). The base set:

| Opcode | Name (our enum) | Dir | Purpose |
|---|---|---|---|
| 0x00 | `OPCODE_HANDSHAKE` | both | ident / login (padding byte `0x42` ⇒ "I speak CPE") |
| 0x01 | `OPCODE_PING` | S→C | keepalive |
| 0x02 | `OPCODE_LEVEL_BEGIN` | S→C | start gzipped level stream |
| 0x03 | `OPCODE_LEVEL_DATA` | S→C | 1024‑byte level chunk + percent |
| 0x04 | `OPCODE_LEVEL_END` | S→C | dims, finalise |
| 0x05 | `OPCODE_SET_BLOCK_CLIENT` | C→S | player placed/broke a block |
| 0x06 | `OPCODE_SET_BLOCK` | S→C | authoritative block change |
| 0x07 | `OPCODE_ADD_ENTITY` | S→C | spawn player entity |
| 0x08 | `OPCODE_ENTITY_TELEPORT` | S→C | absolute pos/orient |
| 0x09–0x0B | rel pos / ori updates | S→C | movement deltas |
| 0x0C | `OPCODE_REMOVE_ENTITY` | S→C | despawn |
| 0x0D | `OPCODE_MESSAGE` | both | chat |
| 0x0E | `OPCODE_KICK` | S→C | disconnect w/ reason |
| 0x0F | `OPCODE_SET_PERMISSION` | S→C | op/user block‑bypass |

Positions are fixed‑point (5 fractional bits) unless `ExtEntityPositions` is
negotiated. Blocks are 1 byte unless `ExtendedBlocks`.

### 2.2 CPE (Classic Protocol Extension)

Negotiation (see `src/Protocol.c`, `CPE_Tick`/`CPE_ExtInfo`/`CPE_ExtEntry` around
lines 838–860 and `Net_Set(OPCODE_EXT_INFO/…)`):

1. Client login padding byte = `0x42` ⇒ "I support CPE".
2. Server → `ExtInfo` (opcode `0x10`: 64‑byte AppName + `short` count) then one
   `ExtEntry` (opcode `0x11`: 64‑byte ExtName + `int` version) per extension.
3. Client replies with its own `ExtInfo` + `ExtEntry` list
   (`cpe_clientExtensions[]` in `src/Protocol.c:103`).
4. Both sides keep the **intersection**. `IsSupported(ext)` ⇔ `ext.serverVersion > 0`.

The client already declares ~40 extensions (`cpe_clientExtensions[]`), including
the two that matter most here:

- **`PluginMessages` v1** — our transport (see §2.3).
- Plus ready‑made helpers we can lean on instead of reinventing: `HackControl`
  (fly/noclip/speed/jump gating — use it to lock survival players out of creative
  flight), `EnvWeatherType`, `MessageTypes` (status‑bar lines), `EntityProperty`
  (per‑entity rot/scale), `SetHotbar`, `VelocityControl` (server‑applied knockback!),
  `ChangeModel` (mob models), `CustomModels`, `CustomParticles`, `ExtEntityPositions`.

MCGalaxy side (`MCGalaxy/Network/ClassicProtocol.cs`): `HandlePacket` is the opcode
switch; `SendCpeExtensions` / `HandleExtInfo` / `HandleExtEntry` / `AddExtension`
run the negotiation and set `has*` capability flags.

### 2.3 PluginMessages — our custom channel (already on both sides)

This is the backbone. It is a generic, forward‑compatible side channel.

- **Packet:** `[0x35][channel:1 byte][payload:64 bytes]` = 66 bytes fixed.
- **Client send:** `CC_API void CPE_SendPluginMessage(cc_uint8 channel, cc_uint8* data)`
  (`src/Protocol.h:98`, impl `src/Protocol.c:891`). Guards on `IsSupported(pluginMessages_Ext)`.
- **Client receive:** `CPE_PluginMessage` (`src/Protocol.c:1551`) raises
  `Event_RaisePluginMessage(&NetEvents.PluginMessageReceived, channel, data+1)`.
  Subscribe to `NetEvents.PluginMessageReceived` to get `(channel, 64‑byte payload)`.
- **Server:** MCGalaxy already handles `0x35` and raises `OnPluginMessageReceivedEvent`
  (channel + payload). **Verify the server payload width** — the CPE spec is
  **64 bytes**; confirm MCGalaxy reads 64 (one fetch summary said 256 — treat as
  suspect and check `ClassicProtocol.cs`).

**Design consequence:** the whole survival sub‑protocol is a set of message types
multiplexed over PluginMessages. 64 bytes/frame is plenty for a header + a small
struct; larger payloads (full inventory, container contents) are split across
frames with a message‑id + sequence, or sent as several logical messages.

---

## 3. The master gate: a `SurvivalTest` CPE extension

Add **one** new CPE extension, negotiated the normal way, that means "both sides
speak the survival sub‑protocol". Everything else keys off it.

- **Client:** add `static struct CpeExt survival_Ext = { "SurvivalTest", 1 };` and
  put `&survival_Ext` in `cpe_clientExtensions[]` (`src/Protocol.c:103`). Add a
  `cc_bool SupportsSurvival;` to `Server` (`src/Server.h`) set from
  `IsSupported(survival_Ext)` (mirror how `SupportsNotifyAction` etc. are done).
- **Server (MCGalaxy):** advertise `SurvivalTest` in `SendCpeExtensions`, set a
  `hasSurvival` flag in `AddExtension`.
- **Meaning:** if `!Server.SupportsSurvival` (vanilla server), the client never
  enters networked survival — it stays stock Classic. This is the gate that
  satisfies "inactive on classic, active on Indev/c0.30‑s".

**Mode selection after negotiation:** the server tells the client *which* survival
mode this map is (Indev vs c0.30‑s vs off) via an initial `SURV_HELLO` plugin
message (see §6). The client then flips the existing runtime switches
(`SurvivalTest_Enabled`, `IndevTest_Enabled`) — today driven by
`SurvivalTest_Gamemode()` from `options.txt` (`src/SurvivalTest.c:7474`,
`src/IndevTest.c:2401`). Introduce a network override so those flags can be set by
the server in MP instead of by local options. **Keep SP reading options as now.**

---

## 4. Client architecture (this repo)

### 4.1 Where the SP/MP split already lives

`src/Server.c` cleanly separates the two via function pointers on the global
`Server` struct:

- `SPConnection_*` (`src/Server.c:122+`): local, `IsSinglePlayer = true`,
  `SendBlock`/`SendChat`/`SendData` are local/no‑ops.
- `MPConnection_*` (`src/Server.c:253+`): real socket; `SendData` writes to the
  socket; `Tick` reads packets and dispatches via the `Net_Handlers` table.

The survival net layer hangs off the **MP** path only.

### 4.2 Recommended new client module: `src/SurvivalNet.c` (+ `.h`)

> **STATUS (fully in tree — mode‑flip restored and integration‑tested).** The tree
> carries the foundation (CPE ext in `src/Protocol.c`, `Server.SupportsSurvival`,
> `SurvivalNet_Component`, `SurvivalNet_Send`, HELLO/WORLDINFO parse+log). The
> full **per‑map activation + sim handover** was implemented and verified
> byte‑for‑byte against the MCGalaxy fork's `Network/SurvivalNet.cs`, then
> **reverted to be re‑landed from the combined two‑repo session** where it can
> be integration‑tested against the live server — restore it with
> `git cherry-pick ed604b6` (or `git checkout ed604b6 -- src/`). What ed604b6
> contains:
> - `SURV_HELLO` **applier**: stores mode/flags, flips
>   `IndevTest_Enabled`/`SurvivalTest_Enabled`/`_Creative`/`_Enhanced` via
>   `SurvivalTest_NetworkModeChanged()` (mode 0 deactivates — the server's live
>   `/Survival` refresh). Activation is per‑map: cleared in `OnNewMap`.
> - `SURV_WORLDINFO` (v1 byte layout) applier → Indev OOB horizon planes.
> - `SURV_HEALTH` applier (`SurvivalTest_ApplyNetHealth`): score is **i32 BE**;
>   decrease → hurt presentation, 0 → death camera + Game Over, rise while dead
>   → revive (server repositions). `SURV_TIME` applier → `IndevTest_SetWorldTime`
>   (client renders the genuine celestial light from the time itself).
> - **`SurvivalNet_ServerDriven()`** gates the whole local sim in MP: damage
>   (one central gate in `SurvivalTest_Damage`), mob/drop/arrow/painting/TNT
>   ticks + spawner, day/night *advance*, furnace tick, drops/consume on block
>   change, eating/bow/tool‑wear/containers/local inventory UI, void death,
>   trampling. Hack permissions defer to the server's `HackControl` in MP.
> - **Intent senders** for all of `0x80–0x87`; `SURV_RESPAWN`, `SURV_HELD_SLOT`
>   (auto on hotbar change) and `SURV_DROP_ITEM` (Q key) are live; the rest are
>   wired up as the server's phases 3–5 land. See `doc/server-session-handoff.md`.
> Still open (server phases 4–5 + matching client appliers): mobs `0x10–0x13` ✅ (phase 3 landed),
> inventory `0x20–0x25`, drops `0x30–0x32`, blockmeta `0x40`, equip `0x50`.

Keep all networked‑survival glue in one new file so the simulation files stay
readable and the SP path is untouched. Responsibilities:

- **Subscribe** to `NetEvents.PluginMessageReceived`; if `channel` is a survival
  channel, decode the message and apply it to `SurvivalTest`/`IndevTest` state
  (health, mob table, inventory, container, time, drops…).
- **Send intents**: wrap `CPE_SendPluginMessage` in typed helpers
  (`SurvivalNet_SendAttack`, `_SendUseItem`, `_SendCraft`, `_SendInvClick`,
  `_SendContainerClick`, …). Call these from the existing input handlers instead
  of mutating local state directly, **when in MP survival**.
- **Author‑ity switch inside the sim:** in MP, the client must *not* run the
  authoritative bits locally. Add guards like
  `if (Server.IsSinglePlayer) { run local sim } else { rely on server state }`
  around: mob spawning/AI/damage (`Mob_*` in `SurvivalTest.c`), health changes
  (`SurvivalTest_Hurt`), container tick (`Furnace_Tick`), day/night
  (`Indev_TickDayNight`), block random ticks (grass/leaf/farmland/fire), and world
  generation. Client keeps rendering + prediction; server owns truth.

The wiring points to grep for in `src/SurvivalTest.c` / `src/IndevTest.c`:
`SurvivalTest_Tick` (the 20 Hz tick — gate the authoritative subsystems),
`SurvivalTest_Hurt` / `SurvivalTest_HurtFrom`, `Mob_SpawnerRun` /
`Mob_IndevSpawnerRun` / `SurvivalTest_IndevInitialSpawn`, `IndevTest_OpenContainer`
/ container slot mutation, `IndevTest_TickRandomBlocks`, `IndevGen_*`.

### 4.3 Worldgen in MP

In MP the **server** generates and streams the map over the normal Classic level
packets (`OPCODE_LEVEL_*`) — the client does **not** run `IndevGen`. But Indev
needs data the Classic level stream doesn't carry (water/ground level, theme,
floating vs inland, surroundings, per‑cell metadata for chests/furnaces/farmland/
fire). Send that as a `SURV_WORLDINFO` plugin message right after `LEVEL_END`,
before spawn (see §6). The client applies it to `Env`/`IndevTest_SetSurroundings`
etc. The existing `.mclevel` metadata fields (`src/Formats.c`) are the exact list
of what must cross the wire.

---

## 5. Server architecture (MCGalaxy fork)

### 5.1 Pointers

- `MCGalaxy/Network/ClassicProtocol.cs` — `HandlePacket` opcode switch, CPE
  negotiation, `SendLevel`, `SendBlockchange`, `MakeBulkBlockchange`,
  `SendSpawnEntity`, and the **existing `0x35` PluginMessage handler →
  `OnPluginMessageReceivedEvent`**. Hook survival receive here.
- `MCGalaxy/Generator/MapGen.cs` — generator registry.
  `Register(string theme, GenType type, MapGenFunc func, string desc)` where
  `public delegate bool MapGenFunc(Player p, Level lvl, MapGenArgs args);`.
  Add the Indev generator here (task §8.1).
- Player state / handlers: `MCGalaxy/Player/…` (`Player.cs`, `Player.Handlers.cs`).
- Events: MCGalaxy's plugin/event system (`OnPluginMessageReceivedEvent`, block‑
  change events, player‑move events) is where a **Survival plugin** subscribes.

### 5.2 Where the authoritative simulation goes

Two viable homes; recommend **a first‑party server module/plugin** ("SurvivalTest"
plugin) so it stays isolated and can be toggled per‑level:

- Per‑level survival state: player health/inventory/armor/score, mob list + AI,
  container tile‑entities, farmland/fire/grass random ticks, day/night `worldTime`.
- A 20 Hz level tick that mirrors the C client's `SurvivalTest_Tick`.
- Validation on every client intent (reach, cooldown, slot legality, LOS) —
  the "server‑side checks" task (§8.2).

The C client code in `src/SurvivalTest.c`/`src/IndevTest.c` is the **spec** for
this C# port: every constant, RNG stream, and formula there was made
source‑faithful to the decompiled Java. Reuse it as the reference.

---

## 6. Draft survival sub‑protocol (starting spec — refine in‑session)

All messages ride PluginMessages `[0x35][channel][64B]`. Reserve **channel `0xB0`**
("survival") and put a **1‑byte message id** as payload byte 0, leaving 63 bytes.
(If you outgrow one channel, allocate `0xB0..0xBF`.) Multi‑byte fields are
big‑endian to match Classic. This is a *starting point*; finalise the exact
layouts as you implement each phase.

**Server → client (state):**

| id | name | payload |
|---|---|---|
| 0x01 | `SURV_HELLO` | mode(1: 0 off/1 c0.30‑s/2 indev), **flags(1)**, protocol ver |
| 0x02 | `SURV_WORLDINFO` | groundLevel, waterLevel, fluid id, theme, floating flag, edge/sides ids (the `.mclevel` metadata set) |
| 0x03 | `SURV_HEALTH` | health(1), score(4, i32 BE) — **implemented both sides** |
| 0x04 | `SURV_TIME` | worldTime(2) or eased sky‑light level |
| 0x10 | `SURV_MOB_SPAWN` | mobId(2), type(1), x/y/z(fixed), yaw/pitch(1), health(1) |
| 0x11 | `SURV_MOB_MOVE` | mobId(2), x/y/z, yaw/pitch (or rel deltas) |
| 0x12 | `SURV_MOB_STATE` | mobId(2), flags (hurt/fuse/fire/onfire), health |
| 0x13 | `SURV_MOB_DESPAWN` | mobId(2), reason |
| 0x20 | `SURV_INV_FULL` | slot run: base slot(1), count(1), then id/count/dmg triples (chunked) |
| 0x21 | `SURV_INV_SLOT` | slot(1), id(2), count(1), dmg(2) |
| 0x22 | `SURV_CONTAINER_OPEN` | kind(1 chest/furnace/large), rows(1), title ref |
| 0x23 | `SURV_CONTAINER_SLOT` | slot(1), id(2), count(1), dmg(2) |
| 0x24 | `SURV_FURNACE_PROG` | burn(1), cook(1) |
| 0x30 | `SURV_DROP_SPAWN` | dropId(2), block/item id(2), count(1), x/y/z |
| 0x31 | `SURV_DROP_PICKUP`/`_REMOVE` | dropId(2) |
| 0x40 | `SURV_BLOCKMETA` | x/y/z, meta (farmland moisture / fire age / chest facing …) |

**Client → server (intents):**

| id | name | payload |
|---|---|---|
| 0x80 | `SURV_ATTACK` | targetMobId(2) (or block for mining handled by 0x05) |
| 0x81 | `SURV_USE_ITEM` | held slot, target block x/y/z, face |
| 0x82 | `SURV_INV_CLICK` | slot, button(L/R/shift), (cursor state server‑tracked) |
| 0x83 | `SURV_CONTAINER_CLICK` | slot, button |
| 0x84 | `SURV_CRAFT` | recipe/grid state or "take result" |
| 0x85 | `SURV_HELD_SLOT` | selected hotbar index (also mirror to `HeldBlock` CPE) |
| 0x86 | `SURV_DROP_ITEM` | slot / whole‑stack flag |
| 0x87 | `SURV_RESPAWN` / menu action | — |

**`SURV_HELLO` flags byte (bit field):** `bit0 = enhanced` (the non‑genuine
survival extras — paperdoll etc.), **`bit1 = creative`** (this session/map is
Indev creative — client hides the HUD, runs the palette‑hotbar + infinite‑place +
no‑damage UX, allows flight; **must override the client's local `OPT_INDEV_CREATIVE`**),
`bit2 = pvp`, `bit3 = deathDrops`, rest reserved (send 0). The client sets its
`SurvivalTest_Creative`‑equivalent runtime state **from this flag** in MP and stops
consulting the local option (see §14). The flag is render/UX only — **authority
stays server‑side** (§20.3): the server runs the creative sim and validates intents
regardless of what the client claims.

Reuse standard packets where they already fit — don't duplicate:
- **Player movement**: keep the normal Classic position packets (§2.1) +
  `ExtEntityPositions`. **Mobs are bespoke, not standard entities** — see §15.1
  for the decision and why (standard entities can't reproduce the Indev
  animations). Mobs stream over `SURV_MOB_*` into the existing render puppet.
- **Knockback**: `VelocityControl` CPE instead of a custom vector.
- **Status text / hearts context**: `MessageTypes` for status‑bar lines if useful.
- **Flight lockout**: `HackControl` to force no‑fly/no‑noclip on *survival* players.
  **Creative sessions are the exception** — send `HackControl` with fly/speed
  *allowed* (genuine creative flies + reach 5), matching the `SURV_HELLO` creative
  flag. Resolve HackControl from the same per‑session creative decision so the two
  never disagree.
- **Held block**: `HeldBlock` CPE (`OPCODE_HOLD_THIS`).

---

## 7. Phased roadmap (do them in this order)

Each phase is independently shippable and stays gated behind `SurvivalTest`.

- **Phase 0 — Handshake & mode.** Add the `SurvivalTest` CPE ext (client + server).
  Server sends `SURV_HELLO`; client flips into Indev mode on a *server‑provided*
  world (existing local sim still running — desync is fine at this stage). Prove
  the gate: a vanilla server ⇒ no change; our server ⇒ client shows the Indev HUD.
- **Phase 1 — Authoritative world.** Port the Indev generator into MCGalaxy (§8.1);
  send `SURV_WORLDINFO`; client stops running `IndevGen` in MP and applies
  surroundings/env from the server. Block metadata sync (`SURV_BLOCKMETA`).
- **Phase 2 — Health & damage.** Server owns health; `SURV_HEALTH` → client HUD.
  Fall/lava/fire/mob damage computed server‑side; client stops calling
  `SurvivalTest_Hurt` locally in MP. Death/respawn flow.
- **Phase 3 — Mobs.** Server runs the spawner + AI (port of `Mob_*`); streams
  spawn/move/state/despawn. Client renders + interpolates + plays sounds; sends
  `SURV_ATTACK`. Decide standard‑entity vs bespoke‑channel here.
- **Phase 4 — Inventory / crafting / containers.** Server‑authoritative inventory,
  crafting matcher, chest/furnace tile‑entities + the large‑chest combining;
  window open/slot/click transactions. Client GUIs become views of server state.
- **Phase 5 — World simulation & polish.** Day/night (`worldTime`), item drops +
  pickup, grass/leaf/farmland/fire random ticks, TNT/explosions, paintings,
  physics edge cases. Anti‑cheat validation pass (§8.2). Then start c0.30‑s.

---

## 8. The two tasks explicitly handed to the next session (server‑side, do first)

### 8.1 Port the Indev generator into MCGalaxy

- The client generator (`src/IndevGen.c`, byte‑verified faithful — see
  `doc/indev-generation.md`) is the reference. Port the noise stack
  (Perlin/Octave/Combined + the Java `Random`), the terrain pipeline, carve/ore/
  tree/flower passes, the assembling floor/border fill, `growGrassOnDirt`, and
  `findSpawn` to C# under `MCGalaxy/Generator/` and `Register("indev", GenType.Advanced,
  IndevGen.Generate, "…")` (§5.1). **Match the RNG streams exactly** (gen seed,
  `world.random` = seed+1 with the burned `nextInt`, spawn = seed+2) so worlds are
  identical to singleplayer for a given seed. Persist the Indev metadata
  (ground/water level, theme, surroundings) with the level so `SURV_WORLDINFO` can
  send it. `doc/indev-generation.md` + `src/IndevGen.c` are your spec.
- Deliverable: `/os map add indev` (or MCGalaxy's gen command) produces an Indev
  world server‑side, streamable to the client.
- Pairs with **§18** — persist generated/edited Indev worlds as `.mclevel`
  (byte‑compatible with the client) so survival state survives restarts.

### 8.2 Server‑side checks & handling

Stand up the authoritative **SurvivalTest server module/plugin** (§5.2) and make it
*validate*, not trust, the client:

- Hook `OnPluginMessageReceivedEvent` on channel `0xB0`; parse intents (§6).
- Validate every intent: reach distance on block/attack, per‑action cooldowns
  (attack delay, eat time, bow draw), slot/recipe legality (can't craft what you
  can't afford), line‑of‑sight for attacks, container access rules
  (`BlockChest.blockActivated` "solid block above blocks opening", the double‑chest
  pairing/`canPlaceBlockAt` limits). Reject → correct the client with an
  authoritative state message.
- Own the tick: health regen/damage, mob spawner + AI + combat, furnace smelting,
  farmland/crop/grass/leaf/fire random ticks, day/night, item‑drop despawn.
- Force survival constraints with CPE: `HackControl` (no fly/noclip),
  block‑permission for creative‑only blocks, etc.
- Keep it **per‑level and mode‑aware**: only levels flagged Indev/c0.30‑s run the
  sim; Classic levels are unaffected (mirror the client's gate on the server).

---

## 9. Testing strategy

- **Regression first:** connect this client to a *stock* MCGalaxy/ClassiCube server
  and confirm nothing changed (creative, blocks, chat, existing CPE). Do this after
  *every* networking change.
- **Loopback:** run the modified MCGalaxy locally; connect the modified client via
  `MPConnection` to `127.0.0.1`. The headless rig (`/tmp/cc_run`, Xvfb `:99`, gdb)
  used for the SP work can drive the client; inspect `Server.SupportsSurvival`,
  the mob table, health, etc. via gdb exactly as in prior sessions.
- **Two clients** for genuine MP checks (mob/entity sync, block races).
- **Wire logging:** add a debug dump of every `SURV_*` frame (id + hex) behind a
  flag on both ends; diff client‑predicted vs server‑authoritative state.
- **Parity harness:** for a fixed seed, assert the MCGalaxy Indev world equals the
  SP `IndevGen` world (reuse the Java‑oracle mindset from `doc/indev-generation.md`).

---

## 10. Risks / gotchas

- **PluginMessages payload width:** client sends/receives exactly 64 bytes; confirm
  MCGalaxy matches (one source claimed 256). Pad short messages; never over‑read.
- **Extension name collisions:** pick a survival ext name unlikely to clash; bump
  its version when the wire format changes and branch on `serverVersion`.
- **Fixed‑point positions:** mobs moving fast/high need `ExtEntityPositions`;
  otherwise clamp to Classic's range.
- **Block ID space:** Indev uses ids beyond the Classic 0–49 set (torch 50, fire 51,
  chest 54, furnaces, crops, …). Use `CustomBlocks`/`BlockDefinitions` CPE so the
  server can define them and the client renders them; keep the SP block table and
  the server definitions in sync.
- **Authority migration is the hard part:** moving mob AI/health/containers from
  client‑authoritative (today) to server‑authoritative without visual jank needs
  client‑side interpolation + reconciliation. Phase it; don't try to flip
  everything at once.
- **Don't regress SP:** every guard you add must have an `if (Server.IsSinglePlayer)`
  fast path that behaves exactly as today.

---

## 11. First concrete steps for the next session

1. Read `src/Protocol.c` (CPE negotiation + `CPE_SendPluginMessage`/`CPE_PluginMessage`),
   `src/Server.c` (SP/MP split), and MCGalaxy `ClassicProtocol.cs` + `MapGen.cs`.
2. Verify MCGalaxy's `0x35` payload width and its `OnPluginMessageReceivedEvent`.
3. **Server task 8.1** — port the Indev generator into MCGalaxy and register it.
4. **Server task 8.2** — scaffold the SurvivalTest server module: negotiate the
   `SurvivalTest` CPE ext, send `SURV_HELLO`/`SURV_WORLDINFO`, receive channel `0xB0`.
5. **Client Phase 0** — add `survival_Ext` to `cpe_clientExtensions[]`,
   `Server.SupportsSurvival`, and `src/SurvivalNet.c` that flips Indev mode on
   `SURV_HELLO`. Prove the gate (vanilla server unaffected).
6. Then walk the phases (§7). Update this file as the wire format solidifies.

## 12. Client code map — what to read / reuse / gate, per subsystem

These are the exact symbols the networked layer touches. In MP survival, the
authoritative logic moves to the server; the client keeps the *render/predict/
intent* half. Every "authoritative" call below needs an
`if (Server.IsSinglePlayer) { …as now… } else { …driven by server…}` split.

**Health** (`src/SurvivalTest.c`)
- State: `int SurvivalTest_Health` (`:53`, extern in `.h`), `SURVIVAL_MAX_HEALTH`
  (20), `st_score` (`:85`), `st_lastHealth`/invuln window inside `SurvivalTest_Damage`.
- Authoritative: `SurvivalTest_Damage(damage, attackerPos)` (`:~1240`) — invuln
  frames (equal-hit half-block), armor absorption call, knockback dir; public
  wrappers `SurvivalTest_Hurt` (`:1289`), `SurvivalTest_HurtFrom` (`:1290`),
  `SurvivalTest_Heal` (`:1338`). Every call site (fall/lava/fire/explosion/mob at
  `:3210,:3367,:3685,:4056`) must become server‑side in MP.
- Render/HUD: hearts drawn in `src/Screens.c` (HUDScreen). In MP the HUD just
  reflects the server's `SURV_HEALTH`.

**Mobs** (`src/SurvivalTest.c`)
- State: `struct Mob` (fields: `type,active,hasTarget,targetSlot,health,invincTicks,
  hurtTicks,attackTime,fire,fuseTicks/fuseState,graze,walkDist,hasHelmet/hasArmor,
  Base.prev/next` for interpolation), pool `st_mobs[]`, `mobTypeInfo[]` table,
  `enum MobType`.
- Authoritative: spawners `Mob_SpawnerRun`/`Mob_IndevSpawnPass`/`Mob_IndevSpawnerRun`/
  `SurvivalTest_IndevInitialSpawn`, AI `Mob_IndevCreatureAI`/`Mob_IndevCreatureUpdate`/
  `Mob_BasicAIUpdate` + pathfinder `Mob_FindPath`, combat `Mob_IndevAttackEntity`/
  `Mob_Hurt`/`Mob_Die`, factory `SurvivalTest_SpawnMobAt`, size `Mob_ApplySize`,
  ground/normal‑cube `Mob_BlockIsSolid`/`Mob_BlockIsNormalCube`.
- Render: `SurvivalTest_RenderMobs`, `Mob_UpdateBodyYaw`, model set via
  `Entity_SetModel`. This half stays client‑side (fed by `SURV_MOB_*`).
- Debug: `SurvivalTest_DebugSpawnMob` (`/client spawn`) is handy for MP testing.

**Armor** (`src/SurvivalTest.c`, `src/IndevArmor.c`, `src/IndevTest.c`)
- State: `st_armor[SURVIVAL_ARMOR_SLOTS]` (`:150`) — `[0]boots [1]legs [2]chest
  [3]helmet`, saved as `.mclevel` Slot 100+index.
- Authoritative: absorption in `SurvivalTest_Damage` (`:1210–1253`) — `remain`/
  `reduce` weighting + per‑hit +1 wear to each worn piece, piece breaks when
  `damage > IndevTest_ArmorMaxDamage`. Helpers `IndevTest_ArmorPiece`,
  `IndevTest_ArmorReduce`, `IndevTest_ArmorMaxDamage`.
- Render: `IndevArmor_Render` (`:5010`) worn plates; HUD armor bar in `Screens.c`.

**Inventory / crafting / containers** (`src/SurvivalTest.c`, `src/IndevTest.c`)
- State: `st_inv[SURVIVAL_INV_SLOTS]` (36), `st_craft[SURVIVAL_CRAFT_SLOTS]`,
  `st_cursor`, `struct SurvivalSlot {id,count,damage}` (in `.h`), unified addressing
  `SurvivalTest_SlotPtr` (`:6264`, with `SURVIVAL_{INV,CRAFT,CONTAINER,ARMOR}_BASE`
  extended‑slot ranges). Change ticker `SurvivalTest_InvVersion`/`_InvChanged`.
- Ops (make server‑authoritative): `SurvivalTest_AddItem`, `SurvivalTest_DamageHeldTool`,
  `SurvivalTest_ConsumeSelected`, crafting `IndevTest_MatchRecipe`/`SurvivalTest_CraftResult`/
  `_CraftTake`, containers `IndevTest_OpenContainer`/`_ContainerSlot`/`_ContainerSlotCount`,
  furnace `Furnace_Tick`, chest scatter `IndevTE_Scatter`.
- GUI (stays client‑side, becomes a view of server state): `SurvivalInvScreen_*`
  in `src/Screens.c`.

**World / time / metadata / tile‑entities** (`src/IndevTest.c`, `src/IndevGen.c`, `src/IndevFire.c`)
- Day/night: `indev_worldTime`, `indev_lastSkyLight`, `Indev_TickDayNight`,
  `Indev_EasedSkyLight`.
- Surroundings/env: `IndevTest_SetSurroundings`/`_ApplySurroundings`,
  `indev_surGround/surWater/surFluid`; env planes via `Env_*`.
- Tile entities: `struct IndevTE` + `indev_tes[]` (chest/furnace contents),
  `IndevTE_Find/_Create/_Scatter`.
- Block metadata: `IndevTest_BlockDataMeta(At)` / `IndevTest_ApplyDataMeta(At)`
  (chest facing, furnace lit, farmland moisture, crop stage), fire age in
  `src/IndevFire.c` (`IndevFire_Age/_SetAge`).
- Random ticks: `IndevTest_TickRandomBlocks` → grass/leaf/farmland/crop/fire.
- Worldgen: `src/IndevGen.c` (`IndevGen_*`) — **client does NOT run this in MP**.

**Serialization surface = the sync manifest** (`src/Formats.c`, MCLevel_*)
The `.mclevel` reader/writer already enumerates *every* piece of survival state
that must cross the wire: player `Inventory` (main + Slot 100+ armor), `Entities`
(mobs w/ type+health+pos), `TileEntities` (chest/furnace `Items`), `Environment`
(`Surrounding{Ground,Water}{Type,Height}`, colours), map dims/spawn. Treat
`MCLevel_ParseEnvironment`/`_CommitEntity`/`_CommitTE`/the inventory parser as the
canonical list when designing `SURV_WORLDINFO` / `SURV_INV_FULL` / `SURV_MOB_SPAWN`.

---

## 13. Server‑side handling — questions & recommended answers

Design answers for the server module (§8.2). Default stance everywhere: **the
server simulates and validates; the client only sends intents and renders.**

**Health**
- *Q: Who owns it?* Server. In MP the client's `SurvivalTest_Health` is a display
  mirror; local `_Hurt/_HurtFrom/_Heal` are suppressed and replaced by `SURV_HEALTH`.
- *Q: How is damage derived so it can't be spoofed?* Server computes it from
  server‑tracked state: fall damage from the server's view of the player's
  fall distance (never trust a client "I took fall damage" message), lava/fire
  from the block the server sees under/around the player, mob melee from the
  server's mob combat, explosion from server‑side blast falloff. Port the exact
  math + the 20‑tick invulnerability window (equal‑damage hits half‑blocked) from
  `SurvivalTest_Damage`.
- *Q: Regen?* Indev has **no** natural regeneration (no hunger system; that's
  Alpha+). Health only drops; it resets on respawn/new world. c0.30‑s is the same.
  So there is no regen packet — don't add one.
- *Q: Death?* Server detects `health <= 0`, sends a death `SURV_HEALTH`(0) +
  a death/score message; client shows the existing `GameOverScreen`. Respawn =
  server loads/regenerates and re‑streams (there is no in‑place respawn in Indev).
- *Q: Knockback?* Use the `VelocityControl` CPE packet from the server rather than
  a custom vector — the client already implements it.

**Mob + armor state**
- *Q: Bespoke mob channel vs standard Classic entities?* **Decided in §15.1:
  bespoke.** Standard CPE entities do not reproduce the Indev animations
  (creeper swell, sheep graze, hurt flash, fire overlay, size/`heightOff`,
  body‑yaw easing), which all live in `SurvivalTest_RenderMobs`. Stream mobs into
  the existing `st_mobs[]` puppet via `SURV_MOB_SPAWN`/`_MOVE`/`_STATE` and reuse
  the faithful render path with AI/physics gated off. See §15.1.
- *Q: Does the client need mob health/AI?* No AI (server‑only). It needs enough
  for rendering: type→model, `hurtTicks` flash, creeper swell, fire overlay,
  death animation. Feed those from `SURV_MOB_STATE`.
- *Q: Where does armor absorption run?* Server, inside its damage routine (port
  of `SurvivalTest_Damage` armor block). The server owns the 4 armor slots +
  their durability and wears/breaks them. The client only needs the slot contents
  + damage to draw the HUD armor bar and (optionally) worn plates — send via
  `SURV_INV_SLOT` for slots 100–103.
- *Q: Show other players' worn armor?* Defer. First pass: only the local player's
  HUD + server‑side absorption. Later, worn plates on remote players via
  `CustomModels`/a plates model.

**Inventory tracking**
- *Q: Authority + anti‑dupe?* Server owns `main[36] + armor[4] + craft grid +
  cursor + open container`. The client sends **intents** (`SURV_INV_CLICK`,
  `SURV_CONTAINER_CLICK`, `SURV_CRAFT`, `SURV_DROP_ITEM`, `SURV_USE_ITEM`,
  `SURV_HELD_SLOT`); the server validates (slot exists, recipe affordable, stack
  limits, container open + in reach) and echoes authoritative slot states. The
  cursor‑held stack is **server‑tracked** — the client renders what the server
  says, so click races can't dupe.
- *Q: Full state vs deltas over 64‑byte frames?* On open/join send a chunked
  `SURV_INV_FULL` (base slot + run of id/count/dmg triples across several frames);
  thereafter send `SURV_INV_SLOT` deltas. Same for containers
  (`SURV_CONTAINER_OPEN` then `SURV_CONTAINER_SLOT`).
- *Q: Block/tool consumption?* Server‑side only. Placing consumes 1 from the held
  slot; breaking wears the tool (`IndevTest_ToolUseWear`) and drops per
  `SurvivalTest_GetBlockDrop`/`SpawnIndevDrops`. Never let the client decrement.
- *Q: Crafting grid?* Server owns the grid; client sends place/take‑result
  intents; server runs `IndevTest_MatchRecipe` (incl. mirrored layouts) and echoes
  grid + result slot. Large‑chest 54‑slot combining (`indev_openTE2`) is a
  server concern too.

**World tracking & status**
- *Q: Who runs the world simulation?* Server. Day/night (`worldTime` +
  `updateDaylightCycle` skylight easing), all random ticks (grass spread/decay,
  leaf decay, farmland/crop growth, fire spread, sapling growth), furnace
  smelting, item‑drop despawn (6000‑tick), TNT/explosions. Results reach clients
  as normal `OPCODE_SET_BLOCK` (block id) + `SURV_BLOCKMETA` (nibble) +
  `SURV_DROP_*` (items) + `SURV_TIME`.
- *Q: How does the client learn Indev's non‑Classic world params?* `SURV_WORLDINFO`
  right after `LEVEL_END`, carrying the `.mclevel` `Environment` set (ground/water
  level, surrounding fluid/heights, theme, floating flag, colours). Client applies
  via `IndevTest_SetSurroundings` + `Env_*`.
- *Q: Block id space beyond Classic 0–49?* Torch 50, fire 51, chest 54, furnaces,
  crops, diamond, etc. must be defined to the client with `BlockDefinitions`/
  `CustomBlocks` CPE so it renders them; keep the server's definitions in lockstep
  with the client's SP block table.
- *Q: Per‑level gating?* The server must run the survival sim **only** on
  Indev/c0.30‑s levels and leave Classic levels as plain creative — the exact
  mirror of the client's `SurvivalTest_Enabled`/`IndevTest_Enabled` gate. A level
  property (e.g. `level.SurvivalMode = off|c030s|indev|indevCreative`) drives both
  the sim and the `SURV_HELLO` sent to joiners.

---

## 14. Pseudo‑creative Indev mode (research + future task)

> **STATUS (SP landed — read this, it supersedes the older prose below).** The
> singleplayer creative mode exists and is **faithful** (an earlier Beta‑1.8
> scrolling picker was built then **reverted** — do not resurrect it):
>
> - **Toggle & resolver:** `OPT_INDEV_CREATIVE` (client option) → `SurvivalTest_Creative`
>   global → **`SurvivalTest_CreativeActive()` = `Creative && IndevTest_Enabled`**.
>   *Every* creative behaviour routes through that one predicate — nothing reads the
>   raw flag. In‑game **Misc‑options → "Indev creative"** toggle, auto‑hidden unless
>   the gamemode is Indev.
> - **Behaviours (all gated on `CreativeActive()`):** no damage in/out
>   (`survivalWorld=false` equiv), instant no‑drop breaking, **no block consumed on
>   place (infinite)**, no survival HUD, reach 5, and **flight/speed**. **Mobs still
>   spawn** (genuine). Fidelity note: everything here is from the dead
>   `PlayerControllerCreative` **except flight/speed** — genuine Indev had no fly at
>   all (grep‑confirmed), so it's the one deliberate non‑genuine convenience (kept
>   by choice). Reach 5 **is** genuine (base `getBlockReachDistance`, vs SP's 4).
> - **Inventory = the genuine model** (Beta picker reverted): opening the inventory
>   shows the **Indev `GuiInventory` panel** (the same textured `SurvivalInvScreen`
>   survival uses), and blocks come from the genuine **9‑slot palette hotbar**
>   (`Session.registeredBlocksList` = stone, cobblestone, brick, dirt, planks, log,
>   leaves, torch, slab), filled on creative map‑load. This IS
>   `PlayerControllerCreative` — the only non‑genuine bit is that Indev shipped it
>   *disabled*, so reviving it at all is our call.
>
> **MP contract for THIS session (how to gate/allow it):**
> - Creative is **server‑dictated per map** (and optionally per player), carried in
>   `SURV_HELLO`'s **flags** byte — see the wire layout in §25 (`bit1 = creative`).
>   In MP the client **must ignore its local `OPT_INDEV_CREATIVE`** and obey the
>   flag; the resolver is already shaped so `SurvivalNet` sets the effective state
>   from `SURV_HELLO` and no downstream check changes.
> - **The flag is UX/render only — never trust it for authority.** A hacked client
>   that force‑sets creative gains nothing: the server keeps applying survival
>   rules (damage, dig‑time, drops, no free "give") to any non‑creative session and
>   validates every intent (§20.3). Client‑side the flag only decides what it draws
>   (HUD on/off) and predicts.
> - **Server‑side switches (MCGalaxy):** gate creative on `level.SurvivalMode == indev`
>   **AND** a per‑level `level.Creative` bool (a `/os map creative on|off` style
>   toggle), optionally overridden per player by a `hasCreative` session flag set by
>   an operator command (`/gamemode`‑style). Send `SURV_HELLO` with `bit1` set only
>   to sessions that resolve to creative; run the creative sim server‑side for those
>   (no damage, mob spawner still on, block places don't debit an inventory).
> - **Block set:** creative uses the **same Indev block set** as survival — including
>   the genuine blocks now at their real ids (`52/53` water/lava source, `55` gears,
>   `57` diamond block) and with `59/60/63/64/65` hidden as non‑genuine. That set is
>   a per‑level property (§30); creative doesn't change it, it just lets you place
>   from it freely.

**This is genuine Indev behaviour, not an invention.** Indev shipped two
controllers (`net/minecraft/client/controller/`):

- `PlayerControllerSP` = **survival** (`world.survivalWorld = true`): timed mining
  (`blockStrength`/`sendBlockRemoving`), block drops on harvest, tool wear
  (`onBlockDestroyed`), health HUD.
- `PlayerControllerCreative` = **creative** (`world.survivalWorld = false`):
  `shouldDrawHUD() = false` (no hearts / no health), **instant break** (base
  `clickBlock` destroys immediately, no mining time), an **infinite palette
  hotbar** (`onRespawn` fills the 9 hotbar slots from the registered block list,
  stack sizes forced — placing never depletes), no tool wear/consumption — **but
  `onUpdate` still runs `mobSpawner.performSpawning()`, so mobs still spawn** (you
  just have no health to lose). No flight in this era.

So "pseudo‑creative Indev" = the Indev world + block set + mob spawning + day/night,
with the *survival* layer (health/damage, mining time, item consumption, tool
wear, inventory scarcity, crafting need) switched off. It is the genuine Indev
Creative game mode.

**Why it matters for networking:** it is by far the **simplest networked Indev
target** — no health sync, no inventory transactions, no crafting/containers,
instant‑break via plain `OPCODE_SET_BLOCK`. It's the ideal Phase‑0/1 proving
ground: get the Indev *world* (server‑generated), *blocks*, *mobs*, and *day/night*
flowing over the wire before taking on the hard survival authority migration.

**Tasks (SP client work is ✅ DONE — see the STATUS box; these are the MP tasks):**
1. ~~SP creative mode~~ ✅ **done** — implemented as the `SurvivalTest_Creative` flag
   + `SurvivalTest_CreativeActive()` predicate (NOT a 4th enum mode — it's a
   modifier on the `INDEV` gamemode, so `c0.30-s` and Indev survival are untouched).
   Behaviours, palette hotbar, and the genuine `GuiInventory` are all in; verified
   vs `PlayerControllerCreative`.
2. **Server: decide+carry the mode.** Resolve per session: `INDEV` gamemode +
   per‑level `Creative` toggle (+ optional per‑player operator override) → set
   `SURV_HELLO` `bit1`. Send `HackControl` fly‑allowed to those sessions (§25).
3. **Client: obey the flag in MP.** In `SurvivalNet`, `SURV_HELLO` `bit1` sets the
   creative runtime state (the resolver already routes everything through it);
   ignore the local `OPT_INDEV_CREATIVE` while networked.
4. **Server: run the creative sim + validate.** Generate the Indev world, spawn
   mobs, tick day/night + random blocks, relay block changes; **no** health/
   inventory/container messages for creative sessions; let them place/break freely
   but still validate reach + the per‑level block set. Never trust the client's
   claimed mode (§20.3). This is the first end‑to‑end Indev MP demo.

---

## 15. Desync avoidance & fidelity (read this before writing any sync code)

Multiplayer's real difficulty is not the packets — it's keeping every client
byte‑identical to the server without visible jank. The rules below are not
optional; getting them wrong produces exactly the desyncs raised in review
(models, decay, new blocks/items, TNT).

### 15.0 The one governing principle

> **Anything RNG‑driven, multi‑block, or multi‑entity is server‑authoritative
> and is NEVER predicted on the client.** The client may optimistically predict
> only its own *single, deterministic, local* action (placing/breaking one
> block) and must accept the server's value on the next echo. Everything else —
> decay, growth, mob AI, damage, drops, explosions, inventory — is *received*,
> not *computed*, on the client.

Reconciliation rule: **server wins, always.** When a `SURV_*` or `SET_BLOCK`
echo disagrees with local prediction, snap to the server value (revert the
optimistic block, restore the inventory count, reposition the mob). Classic
already does this for blocks; extend the same discipline to survival state.

### 15.1 Mob models & animation — use bespoke entities, not standard CPE entities

**This revises the tentative "standard entities" suggestion in §6/§13.** For a
faithfulness‑first project, **stream mobs into the existing `st_mobs[]` puppet
and render with the existing `SurvivalTest_RenderMobs`.** Standard CPE entities
(`EXT_ADD_ENTITY2` + `ChangeModel`) give you the base model and interpolation
but **do not** reproduce the Indev‑specific look, which lives entirely in our
custom render/animation code:

- creeper swell scale from `fuseState`/`fuseTicks`; sheep head‑dip `graze`;
  zombie/skeleton arm sway from `ticksAlive`; the red `hurtTicks` hit‑flash; the
  burning `fire` overlay; sheared `hasFur`/`sheep_nofur` model swap; the exact
  per‑type + per‑mode `Mob_ApplySize` collision/scale; `heightOff` eye anchor;
  the body‑vs‑head yaw easing (`Mob_UpdateBodyYaw`); the prev/next interpolation
  snapshot.

None of that rides on a standard entity. So: in MP the client keeps its mob pool
as a **network‑driven puppet** — `Mob_*` AI/spawn/damage are gated off, but the
render + interpolation + animation code runs unchanged, fed by `SURV_MOB_*`.

- **`SURV_MOB_SPAWN`** carries `type` (⇒ model + size via `mobTypeInfo`/`Mob_ApplySize`),
  spawn pos/orient, initial health, `hasHelmet/hasArmor`, `hasFur`.
- **`SURV_MOB_MOVE`** carries pos + yaw/pitch (client interpolates prev→next as
  today; do **not** run local physics on puppets).
- **`SURV_MOB_STATE`** carries the animation drivers: `health`→sets `hurtTicks`
  on decrease, `fuseState`, `onFire`, `grazing`, death. The client derives the
  cosmetic timers locally from state edges (e.g. health drop ⇒ `hurtTicks = 10`).

Cost: a bespoke channel + you re‑send position for mobs. Benefit: pixel‑for‑pixel
the same Indev mobs, zero new model code. Worth it here.

### 15.2 Decay / growth / random ticks — server‑only, or you WILL desync

Grass spread + decay, leaf decay, farmland moisture, crop growth, sapling growth,
fire spread/burnout, furnace smelting, item despawn, and day/night are all
**RNG‑ and tick‑timing‑driven**. Two independent RNG streams (client vs server)
diverge on the very first roll, and tick counts drift. Therefore:

- In MP, **gate off** on `!Server.IsSinglePlayer`: `IndevTest_TickRandomBlocks`
  (grass/leaf/farmland/crop/fire), `Furnace_Tick`, `Indev_TickDayNight`
  (`worldTime`/`indev_lastSkyLight`), item‑drop despawn, sapling growth.
- The **server** runs all of it and pushes results: block id changes as normal
  `OPCODE_SET_BLOCK`, nibble/metadata as `SURV_BLOCKMETA`, time as `SURV_TIME`,
  item appear/disappear as `SURV_DROP_SPAWN`/`SURV_DROP_REMOVE`.
- Lighting stays a **local deterministic** computation (heightmap from the same
  world ⇒ same result), so the client may keep computing light for rendering —
  but it must **not** drive any state change from it (decay is server‑told, not
  light‑inferred). Keep `Lighting`/`IndevTest_LightLevel` for visuals only.
- Do not let the client "help" by decaying a leaf it thinks is orphaned — that's
  the classic double‑decay desync. Received `SET_BLOCK` is the only truth.

### 15.3 New block & item handling — one shared definition table

Desync source: a block id that means different things on each side. Prevent it
with a **single source of truth** for the Indev definitions, ported verbatim to
the server:

- **Blocks** (`IndevBlocks_Define` in `src/IndevTest.c`) — the authoritative
  Indev id map the server MUST match:
  - **Genuine ids (1:1 with in‑20100223):** torch 50, fire 51, **water source 52,
    lava source 53**, chest 54, **gears 55**, diamond ore 56, **diamond block 57**,
    workbench 58, furnace 61 / lit 62. (52/53/55/57 were recently implemented — do
    not treat them as CPE defaults.)
  - **Relocated (ClassiCube has no runtime block metadata, so multi‑state blocks
    can't share one id):** farmland 83/84, crops 85–92, chest/furnace facing
    variants 71–82, wall‑torch 94–97. The `.mclevel` save/load remaps these ↔ the
    genuine `59`/`60`+nibble, so the *save format* stays genuine (`IndevTest_BlockToIndev`/
    `_FromIndev`). The server must apply the same remap at the `.mclevel` boundary.
  - **Hidden (non‑genuine — kept off the Indev set):** 59, 60, 63, 64, 65
    (`CanPlace=false` + removed from the inventory map). The server must NOT let a
    survival client place these on an Indev map either.
  - Each has a draw type, collide, textures, hardness, sound. On connect the server
    sends `CustomBlockSupportLevel` + `BlockDefinitions`/`DefineBlock` for every
    >Classic id so *any* CPE client renders them, with sane **fallback ids** for
    clients that lack them. **Textures:** most come from the fetched Beta jar
    (`beta_tiles[]` in `src/Resources.c`); the **gears tile is embedded** there
    (Mojang hosts no Indev jar) — the server's texture pack must carry the same
    atlas. Keep client+server in lockstep; bump the `SurvivalTest` ext version if
    the table changes.
- **Items** (`indev_items[]`, ids `256 + shiftedIndex`, `ITEM_KIND_*`,
  durabilities in `src/IndevTest.c`): the Classic protocol has **no item
  concept** at all. Items exist only inside our sub‑protocol (inventory, drops,
  held). So the item table is *ours* end‑to‑end and must match byte‑for‑byte on
  both sides. Drops of items render via `SURV_DROP_SPAWN` (id ≥ 256); the held
  item uses our own path, **not** the CPE `HeldBlock` packet (that only carries
  block ids 0–255/custom, never items).
- **Placement/consumption:** the client may optimistically *show* a placed block
  (Classic behaviour), but must **not** optimistically decrement the inventory or
  wear the tool — those are server‑owned and arrive via `SURV_INV_SLOT`. If the
  server rejects the place (no item, out of reach), it reverts the block *and*
  the slot. Never trust a client "I used up item X" message.

### 15.4 TNT & explosions — fully server‑side, never predicted

Explosions are the worst desync case: one event mutates *many* blocks, spawns
RNG drops, and damages entities. Indev and c0.30 also differ (fuse gating —
note `fuseTicks >= 80` at `SurvivalTest.c:1758`, the `canExplode` hard‑block set
at `:1373`, liquid destruction, drop chance, radius, damage falloff). So:

- **The client does not run explosion physics in MP.** Gate off
  `SurvivalTest_TntPhysics`, `SurvivalTest_Explode`, `Mob_CreeperExplode`, and the
  block‑removal/drop chain (`ExplodeDropsForBlock`, `IndevTest_NotifyBlockRemoved`).
- The **server** owns the primed‑TNT entity + fuse, computes the blast, and sends:
  destroyed blocks as a **`BulkBlockUpdate`** (CPE) or a batch of `SET_BLOCK`;
  resulting drops as `SURV_DROP_SPAWN`; damage as `SURV_HEALTH` (+ knockback via
  `VelocityControl`); and the visual/audio as a small `SURV` effect trigger (or
  `CustomParticles` + a sound id).
- The client renders the primed‑TNT (a streamed mob‑style entity, or a
  told‑to‑flash block) and plays the boom on cue — it computes nothing. Creeper
  detonation takes the exact same path.
- Because the block set changes in bulk, prefer `BulkBlockUpdate` so the client
  applies them atomically in one frame (no flicker) and can't interleave a stale
  optimistic edit.

### 15.5 Quick authority checklist

| Subsystem | Client may predict? | Authority | Wire |
|---|---|---|---|
| Own block place/break | Yes (optimistic, reconcile) | Server | `SET_BLOCK_CLIENT` → `SET_BLOCK` |
| Inventory count / tool wear | **No** | Server | `SURV_INV_SLOT` |
| Health / damage / knockback | **No** | Server | `SURV_HEALTH` + `VelocityControl` |
| Mob spawn / AI / move / death | **No** (render puppet only) | Server | `SURV_MOB_*` |
| Mob animation timers (flash/swell/fire) | Derive from state edges | Server state | `SURV_MOB_STATE` |
| Grass/leaf/farmland/crop/fire/sapling | **No** | Server | `SET_BLOCK` + `SURV_BLOCKMETA` |
| Furnace smelt / day‑night / despawn | **No** | Server | `SURV_*` / `SET_BLOCK` |
| Item drops / pickups | **No** | Server | `SURV_DROP_*` |
| TNT / creeper explosion | **No** | Server | `BulkBlockUpdate` + `SURV_DROP`/`HEALTH` |
| Lighting (visual only) | Yes (deterministic) | local render | — |
| Block/item definitions | — | shared table | `BlockDefinitions` + our item table |

If in doubt: **receive, don't compute.**

## 16. Classic‑client compatibility policy

A stock Classic/ClassiCube client (one that does **not** negotiate our
`SurvivalTest` CPE ext) cannot run any survival logic: it won't see our bespoke
mobs (§15.1) or item drops, has no health/inventory, and — critically — if it
could place/break freely in a survival map it would **corrupt the authoritative
world** (bypassing tools, consumption, physics, decay). So compatibility is
decided **per map mode**, and it hinges on one hard invariant:

> **Invariant: a client that has not negotiated `SurvivalTest` must never be able
> to place or break blocks in an Indev/c0.30 *survival* map.** Enforce this
> server‑side by rejecting its `SET_BLOCK_CLIENT` and reverting the block —
> independent of rank.

Detection is clean: "survival‑capable" ⇔ the client negotiated the `SurvivalTest`
extension during the CPE handshake (server's `hasSurvival` flag for that session).

Add two per‑level properties on the server:
`SurvivalMode ∈ {off, indevCreative, indevSurvival, c030s}` and
`ClassicClientPolicy ∈ {allow, visitor, deny}`.

Policy by mode (recommended defaults):

- **Classic map (`SurvivalMode=off`)** — unchanged. Everyone builds normally. Our
  client behaves as stock Classic here (the gate is off). Full compatibility.
- **Indev *creative* map (`indevCreative`)** — building is free (no survival sim),
  so a Classic/CPE client **can join and build normally**. Custom blocks reach it
  via `BlockDefinitions` (with fallback ids); our mobs/drops are simply invisible
  to it (acceptable — they're cosmetic in creative). Default `ClassicClientPolicy=allow`.
- **Indev/c0.30 *survival* map (`indevSurvival`/`c030s`)** — a non‑survival client
  can't participate correctly. Choose per‑map:
  - **`visitor` (recommended default):** let them join but as **read‑only
    spectators** — the server forces them to a no‑build state on this level
    (MCGalaxy per‑level build access / visitor rank), rejecting every block change.
    They can walk and see the static world (+custom blocks); they don't see mobs or
    drops. Most inclusive, zero risk to world integrity.
  - **`deny`:** the server refuses the join with a clear message
    ("This map needs the Indev survival client"). Use for maps that would be
    confusing to spectate (heavy mob activity a spectator can't see).
  - **`allow` is NOT permitted for survival maps** — it would violate the invariant.

Implementation notes (server):
- On join to a survival map, branch on the session's `hasSurvival`. If false and
  policy is `visitor`, set the player's per‑level build permission to none (so the
  existing block‑change rejection path reverts their edits) and skip sending any
  `SURV_*` messages to them. If `deny`, disconnect with the message.
- Survival‑capable clients on the same map get the full `SURV_*` stream.
- Keep a chat/notice on join so users know why they're a visitor.
- The client side needs no special code for this — a visitor simply never gets
  `SURV_HELLO`, so its gate stays off and it behaves as stock Classic; its block
  edits are already reverted by the normal authoritative `SET_BLOCK` path.

Rationale: full survival parity for a non‑protocol client is **not achievable**
(it can't run or receive the sim), so we don't pretend to — we keep them safe
(visitor) or out (deny), and reserve real interop for creative maps where free
building is already the rule.

## 17. Implementation cookbook (specific how‑tos for the fiddly parts)

Concrete step lists for the things that are easy to get subtly wrong. These are
instructions, not finished code — write the code to match the surrounding style.

### 17.1 Add the `SurvivalTest` CPE extension (the master gate)

**Client (`src/Protocol.c`):**
1. Near the other `CpeExt` decls (~line 90), add
   `static struct CpeExt survival_Ext = { "SurvivalTest", 1 };`.
2. Add `&survival_Ext` to `cpe_clientExtensions[]` (~line 103).
3. In `src/Server.h`, add `cc_bool SupportsSurvival;` to the `Server` struct next
   to the other `Supports*` flags.
4. Set it where the other flags are set after negotiation (mirror
   `SupportsNotifyAction`): `Server.SupportsSurvival = IsSupported(survival_Ext);`.
5. That's the whole gate. Everything survival‑networked checks
   `Server.SupportsSurvival` (and `!Server.IsSinglePlayer`).

**Server (`ClassicProtocol.cs`):** advertise `SurvivalTest`/1 in `SendCpeExtensions`;
in `AddExtension` set a per‑session `hasSurvival` when the client's `ExtEntry`
matches. Only send `SURV_*` to sessions with `hasSurvival`.

### 17.2 Wire the receive path — `src/SurvivalNet.c` (new file)

1. New `src/SurvivalNet.c` + `.h`; register it as a game component (Init/Reset/Free)
   the same way `SurvivalTest_Component` is registered in `src/Game.c`.
2. In Init, subscribe: `Event_RegisterPluginMessage(&NetEvents.PluginMessageReceived,
   NULL, SurvivalNet_OnPluginMessage);` (match the `Event_Register*` signature used
   elsewhere).
3. `SurvivalNet_OnPluginMessage(void* obj, cc_uint8 channel, cc_uint8* data)`:
   `if (channel != SURV_CHANNEL) return;` then `switch (data[0]) { case SURV_HELLO: … }`.
   The 63 bytes after the id are your payload; **never read past 64**.
4. Guard the whole file's behaviour on `!Server.IsSinglePlayer && Server.SupportsSurvival`.

### 17.3 The mode handshake (`SURV_HELLO`)

1. Server, right after it finishes sending the level to a survival‑capable client
   (after `LEVEL_END`), sends `SURV_HELLO {mode, flags, protoVer}` — `flags`
   `bit0=enhanced`, **`bit1=creative`**, `bit2=pvp`, `bit3=deathDrops` (§25) — then
   `SURV_WORLDINFO {ground/water level, fluid, theme, floating, colours…}`.
2. Client `SURV_HELLO` handler flips runtime mode: instead of reading
   `SurvivalTest_Gamemode()` from options (the SP path at `SurvivalTest.c`,
   `IndevTest.c:2401`), set the mode from the packet. Add a
   `SurvivalTest_SetNetworkMode(mode, flags)` that sets `SurvivalTest_Enabled` /
   `IndevTest_Enabled` **and `SurvivalTest_Creative` from `bit1`** (the existing
   `SurvivalTest_CreativeActive()` resolver then does the rest — see §14). **Leave
   the SP option path exactly as is** — only override in MP, and in MP make the
   resolver read the network flag instead of `OPT_INDEV_CREATIVE`.
3. `SURV_WORLDINFO` handler calls `IndevTest_SetSurroundings(...)` + `Env_*` so the
   Indev world params the Classic stream can't carry are applied before spawn.

### 17.4 Gate the SP simulation off in MP (the big one)

For every authoritative subsystem, wrap the entry with the three‑way branch:

```
if (Server.IsSinglePlayer)              { …run the local sim exactly as today… }
else if (Server.SupportsSurvival)       { …driven by SURV_* messages; sim OFF… }
else                                    { …stock Classic; survival entirely off… }
```

Concretely, gate these so they do nothing in the MP‑survival branch (list from §12):
`SurvivalTest_Tick`'s authoritative parts, `SurvivalTest_Damage/_Hurt/_HurtFrom/_Heal`,
`Mob_SpawnerRun`/`Mob_IndevSpawnerRun`/`SurvivalTest_IndevInitialSpawn`, the AI in
`Mob_IndevCreatureUpdate`/`TickOneMob` (keep the *render/interp* half), container
`Furnace_Tick`, `Indev_TickDayNight`, `IndevTest_TickRandomBlocks`, `SurvivalTest_Explode`/
`SurvivalTest_TntPhysics`/`Mob_CreeperExplode`, inventory consumption
(`SurvivalTest_AddItem`/`DamageHeldTool`/`ConsumeSelected`) and `IndevGen` (never run
client‑side in MP). Keep rendering, sound, GUI, and interpolation running.

### 17.5 Mob puppet (turn `st_mobs[]` into a network‑driven view)

1. `SurvivalNet_MobSpawn(id,type,pos,yaw,pitch,health,flags)`: find/allocate a
   `st_mobs[]` slot keyed by the server's mob id, `SurvivalTest_SpawnMobAt`‑style
   setup for model/size, mark `active`, seed prev/next to the spawn pos.
2. `SurvivalNet_MobMove(id,pos,yaw,pitch)`: write into that slot's `next` snapshot;
   the existing interpolation renders it. Do **not** run `Mob_Travel`/AI on puppets.
3. `SurvivalNet_MobState(id,health,fuseState,onFire,grazing,dead)`: set the
   cosmetic timers from edges (e.g. on health decrease set `hurtTicks=10`), drive
   creeper swell/fire overlay/graze; on `dead` start the death animation then free.
4. Map server mob id ↔ slot in a small table so `_MOVE`/`_STATE`/despawn find it.

### 17.6 Send client intents

1. Typed wrappers over `CPE_SendPluginMessage(SURV_CHANNEL, buf)` in `SurvivalNet.c`:
   `SurvivalNet_SendAttack`, `_SendUseItem`, `_SendInvClick`, `_SendContainerClick`,
   `_SendCraft`, `_SendHeldSlot`, `_SendDropItem`.
2. Call them from the existing input handlers **only** in the MP‑survival branch;
   in SP the handlers keep mutating local state as today.
3. Never mutate authoritative local state on send — wait for the server echo
   (§15.0 server‑wins). Optimistic single‑block place/break is the only exception.

### 17.7 Block/item definition parity

1. Treat `IndevBlocks_Define` (`src/IndevTest.c`) + `indev_items[]` as the **spec**.
   The MCGalaxy port must produce byte‑identical ids/draw/collide/textures/hardness/
   sounds and the same item ids/kinds/durabilities.
2. Server sends `CustomBlockSupportLevel` then `DefineBlock`/`BlockDefinitions` for
   every >Classic block id, with a Classic **fallback id** per block so non‑fork
   CPE clients degrade gracefully.
3. Bump `survival_Ext` version whenever the shared table changes, and branch on the
   negotiated version so old/new clients don't silently mismatch.

## 18. `.mclevel` persistence & client compatibility

`.mclevel` (NBT) is Indev's native on‑disk format and **our client already
reads and writes it with the full survival state** (`src/Formats.c`,
`MCLevel_Save` / the `MCLevel_*` parse callbacks). For multiplayer, the server
must persist Indev worlds so survival state survives a restart, and those files
must round‑trip **byte‑compatibly** with the client's schema so a world saved in
singleplayer loads on the server and vice‑versa. This is also the *authoritative
state snapshot* the server loads its live sim from and re‑streams as `SURV_*`.

### 18.1 The exact schema our client uses (match it tag‑for‑tag)

`MinecraftLevel` (compound) contains:
- **`About`**: `Author`(str), `Name`(str), `CreatedOn`(i64).
- **`Environment`**: `SkyColor`/`FogColor`/`CloudColor`(i32 RGB), `SkyBrightness`(u8),
  `CloudHeight`(u16), `TimeOfDay`(i16, the day/night `worldTime`),
  `SurroundingGroundHeight`/`SurroundingWaterHeight` (**signed** i16 — floating maps
  store negative, e.g. −128), `SurroundingGroundType`/`SurroundingWaterType`
  (u8 block ids, genuine quirk: `GroundType` is always written as grass=2).
- **`Map`**: `Width`/`Height`/`Length`(u16), `Spawn`(i16[3]), `Blocks`(u8 array,
  volume), `Data`(u8 array — **metadata high nibble | light low nibble**; the
  high nibble carries chest/furnace facing, farmland moisture, crop stage, torch
  orientation, fire age via `IndevTest_BlockDataMetaAt`).
- **`Entities`** (list of compounds): the **`LocalPlayer`**
  (`id`, `Pos`/`Motion`/`Rotation` float lists, `FallDistance`, `Fire`, `Air`,
  `Health`, `HurtTime`, `DeathTime`, `AttackTime`, `Score`, and **`Inventory`** —
  a list of `{Slot(u8), id(i16), Count(u8), Damage(i16)}`, with **worn armor at
  Slot 100+index**); plus one compound per live **mob** (`id`=name string
  "Zombie"/"Skeleton"/"Pig"/"Creeper"/"Spider"/"Sheep", `Pos`, `Rotation`,
  `Health`), per **item drop** (`Item` sub‑compound with `id`/`Count`/`Damage`),
  and per **painting**.
- **`TileEntities`** (list): chests/furnaces at a position with an `Items` list
  (same item‑compound shape) and, for furnaces, burn/cook fields.

The `MCLevel_Save` writer (`src/Formats.c:1805`) and the parse callbacks
(`MCLevel_ParseMap`/`_ParseEnvironment`/`_CommitEntity`/`_CommitTileEntity`/
`_ParseItemField`) are the authoritative reference — **the MCGalaxy port must
emit identical tag names, types, nesting, and order.**

### 18.2 Block‑id spaces — the corruption trap

There are **three** id spaces; mixing them corrupts worlds:
1. **Genuine Indev on‑disk** ids (what `.mclevel` stores).
2. **Client engine** ids (what our `World.Blocks` holds at runtime).
3. **MCGalaxy internal** ids (the server's block table).

**Why (1) and (2) differ — the metadata‑nibble split.** Genuine Indev stores
`blocks[]` (id) + a parallel `data[]` byte per block, whose **high nibble is the
block metadata** (0‑15): farmland moisture, chest/furnace facing, crop growth
stage (all 8 stages are the ONE id 59), torch direction, fire age. Neither our
client nor MCGalaxy (both Classic‑protocol, id‑only — no per‑position metadata
array) can pack `id+meta`, so each `(genuine id, nibble)` becomes a **distinct
runtime id**: crops 85‑92, farmland 83/84, chest facing 71‑74, furnace 75‑82, wall
torch 94‑97 (id space is cheap — `ExtendedBlocks` gives 65535). The on‑disk `Data`
array must therefore be **rebuilt from the runtime id on save and decomposed back
on load** — that's exactly what `IndevTest_BlockToIndev`+`BlockDataMeta` /
`BlockFromIndev` do. The `data` **low** nibble (light) is not round‑tripped; both
engines recompute lighting. Fire's dynamic age lives in a side per‑position store
(`IndevTest_BlockDataMetaAt`), not an id. The server must mirror all of this.

Our client converts on I/O: `IndevTest_BlockToIndev` (engine→genuine, on save)
and `IndevTest_CanonicalBlock`/the load remap (genuine→engine, on load) — see
`src/IndevTest.c` (e.g. crate 64 → chest 54, facing furnace variants → canonical).
The server needs the **same** genuine↔internal mapping for `.mclevel` I/O, and a
separate internal↔client mapping for the wire (that's what `BlockDefinitions`
+ the shared table in §15.3/§17.7 handle). Keep all three tables derived from one
source of truth; a single off‑by‑one here silently rewrites blocks.

### 18.3 Steps for MCGalaxy

1. **Check what exists.** Look in `MCGalaxy/Levels/IO/` for an existing
   `.mclevel` importer/exporter. Genuine‑Indev import may already exist but it
   will **not** understand our survival extensions (`Inventory`, Slot 100+ armor,
   mob `Entities`, `TileEntities`, `Surrounding*`, `TimeOfDay`, the `Data`
   metadata nibble). Extend it (or add `IndevLevelImporter`/`Exporter`).
2. **Read the full schema** into the server's per‑level survival state: world +
   `Data` metadata → block‑meta store; `LocalPlayer.Inventory`/armor/health/score →
   player state; mob `Entities` → the server mob list; `TileEntities` → container
   contents; `Environment` surroundings/time → the level's Indev params.
3. **Write it back identically** on save, using genuine Indev ids
   (internal→genuine remap) and the same tag layout as §18.1. Round‑trip must be
   lossless.
4. **Persist alongside the MCGalaxy `Level`.** MCGalaxy's native `.lvl` won't hold
   survival state; either keep the Indev world *as* `.mclevel` (preferred — it's
   the shared format) or add a sidecar. Custom blocks still need MCGalaxy's
   BlockDefinitions entries for the network layer (§17.7).
5. **Feed the network layer from this state:** on join, `SURV_WORLDINFO` is the
   `Environment` subset; the block stream is `Map.Blocks` (remapped to client ids);
   mobs/inventory/tile‑entities are streamed as `SURV_*` from the loaded state.
   `.mclevel` = persistence; `SURV_*` = the live wire view of the same manifest.

### 18.4 Round‑trip parity test (do this before trusting it)

Save a world in the SP client → load on the server → save on the server → load
back in the SP client, and assert **identical**: block array + `Data` metadata,
player inventory/armor/health/score, mob list (type/pos/health), tile‑entity
contents, surroundings, and `TimeOfDay`. Diff the two `.mclevel` files directly
(they should be byte‑equal modulo `About.CreatedOn`). Reuse the parity mindset
from `doc/indev-generation.md`. Watch the known gotchas: the **signed**
`Surrounding*Height` (floating = negative), the always‑grass `SurroundingGroundType`
quirk, armor **Slot 100+** numbering, the `Data` nibble packing (meta high / light
low), and the genuine↔engine block remaps (crate/furnace/torch/chest).

## 19. Texture handling & serving

Indev survival adds textures the stock ClassiCube `default.zip` doesn't have.
They must reach every client that renders an Indev map — including vanilla
visitors (§16). Get this wrong and blocks/items/GUIs render as garbage or vanish.

### 19.1 What's new and how the client loads it

- **`terrain.png` block tiles** — the Indev blocks (torch 50, fire 51, chest 54,
  furnaces, workbench, crops 85–92, diamond ore 56, …) use **reserved atlas tiles
  (index 96+)**. Our client composes these into its terrain at first launch via
  `Resources.c`'s **`BetaPatcher`**, extracting from Mojang's b1.7.3 jar (see the
  reservation table in `SURVIVAL_TEST_NOTES.md`). Block↔tile mapping is
  `IndevBlocks_Define` / `Block_Tex`.
- **Standalone PNGs**, loaded via the standard `TextureEntry` route from the
  active pack (`src/IndevTest.c:2407+`): `items.png` (item‑icon atlas),
  `kz.png` (painting art), `inventory.png`/`crafting.png`/`furnace.png`/
  `container.png` (survival GUIs), `sun.png`/`moon.png` (celestial). Rendering
  bails gracefully while any of these is 0/missing.
- **Mob skins** — mostly stock ClassiCube model textures already in `default.zip`
  (`zombie.png`, `creeper.png`, `char.png` for the humanoid/Human mob, etc.); only
  add ones that genuinely differ.

Current state (client): these are **not yet baked into `default.zip`** — the
standalone PNGs rely on a pack supplying them; the terrain tiles rely on the
BetaPatcher having run. **Client prerequisite task:** finish auto‑provisioning the
Indev PNGs into `default.zip` via the existing `Resources.c` jar patchers so SP —
and a fork client with no server pack — always has them. Do this early; it's the
robust baseline everything else builds on.

### 19.2 How texture serving works (and the trap)

ClassiCube servers don't generate textures — they point the client at a
**texture‑pack URL** via the CPE **`EnvMapAppearance`** ext
(`OPCODE_ENV_SET_MAP_URL` → `CPE_SetMapEnvUrl` → `TexturePack_Extract(url)`,
`src/Protocol.c:1338`). Downloading a server pack **replaces the client's active
pack wholesale.** So:

> **Trap: serving a plain `default.zip` to an Indev map strips the fork client's
> Indev textures** (item/GUI/painting PNGs and the patched `terrain.png` tiles),
> rendering the new content broken. An Indev map must serve an **Indev‑augmented
> pack**, or send **no** pack URL and rely on the client's baked‑in pack (§19.1).

MCGalaxy does not parse the pixels — it just **hosts/serves a zip** (its own web
dir or a CDN) and **sends the per‑level URL**. It *does* need to know the new
files + tile indices so the pack it serves is correct and its `BlockDefinitions`
(§17.7) reference the same `terrain.png` tiles the pack contains.

### 19.3 The plan

1. **Assemble one "Indev texture pack"** = `default.zip` + the additions from
   §19.1: a `terrain.png` with the Indev tiles at the reserved indices, plus
   `items.png`, `kz.png`, the four GUI PNGs, `sun.png`, `moon.png`, and any
   Indev‑specific mob skins. This same pack is the client's baked default AND the
   server‑served pack — build it once from one source of truth.
2. **Serve it per‑map:** Indev/c0.30‑s maps set the level texture URL (MCGalaxy's
   per‑level texture property → `EnvMapAppearance`) to the Indev pack; Classic maps
   keep the normal default (or none). Bonus: this is exactly what lets **vanilla
   visitors (§16)** see the new blocks/GUIs.
3. **Keep tile indices in lockstep:** the `TextureID`s in the server's
   `BlockDefinitions`, the tiles in the served `terrain.png`, and the client's
   `Block_Tex`/reservation table must all agree — one source of truth (the notes'
   reservation table). A mismatch renders blocks with the wrong texture.
4. **Confirm in‑zip paths:** the `TextureEntry` names are bare (`"container.png"`)
   but the genuine layout uses subfolders (`gui/…`, `art/kz.png`, `terrain/…`).
   Verify how ClassiCube resolves each `TextureEntry` name inside a pack and lay
   the files out in the served pack accordingly (don't assume — check
   `TexturePack.c`).
5. **Version the pack** with the `SurvivalTest` ext / a pack‑version so a client
   with a stale cached pack re‑downloads when the textures change.

### 19.4 Checklist

- [ ] Client: bake the Indev PNGs into `default.zip` (finish the `Resources.c`
      auto‑provisioning) so SP + no‑server‑pack MP always renders correctly.
- [ ] Build the single Indev‑augmented pack (terrain tiles + PNGs); host it.
- [ ] MCGalaxy: set the Indev pack URL on Indev/c0.30 maps only; leave Classic
      maps on the default.
- [ ] Verify `BlockDefinitions` `TextureID`s == served `terrain.png` tiles ==
      client `Block_Tex` (one reservation table).
- [ ] Confirm the in‑zip paths for every `TextureEntry`; test that a fresh client
      (no local Indev textures) joining an Indev map renders items/GUIs/blocks.

## 20. Per‑client capability gating & fallbacks (never send trash to the wrong client)

Different clients on the same Indev map have different capabilities: a stock
**Classic** client (no CPE at all), a **CPE‑but‑not‑survival** client (ClassiCube
without our ext), and our **`SurvivalTest`** fork client. The server must send
each one only what it negotiated. Sending an optional packet to a client that
can't parse it is not harmless — it desyncs the byte stream and usually gets the
client **kicked** ("unhandled opcode").

### 20.0 The golden routing rule

> **Never send a packet a session did not negotiate.** Every optional/CPE/`SURV_*`
> send is gated on that session's capability flag. This is per‑*session*, not
> per‑*server* — two clients on the same level get different packet sets.

MCGalaxy already tracks per‑session capability flags in `ClassicProtocol.cs`
(`AddExtension` sets `hasCustomBlocks`, `hasTwoWayPing`, `hasBlockDefinitions`,
`hasExtEnvAppearance`, …, plus the client protocol version and
`customBlockSupportLevel`). **Add `hasSurvival`** there and gate all `SURV_*`
(and the survival texture URL, and custom Indev block defs) on it. Reuse the same
discipline the base CPE code already follows — you're extending it, not inventing it.

Required‑capability table (server must check before sending):

| Packet / data | Gate on |
|---|---|
| `0x35` PluginMessage / any `SURV_*` | `hasSurvival` **and** `hasPluginMessages` |
| `DefineBlock`/`BlockDefinitions` (Indev blocks) | `hasBlockDefinitions` |
| Custom block ids > Classic set in the level stream | `customBlockSupportLevel` ≥ needed |
| `EnvMapAppearance` texture URL | `hasExtEnvAppearance` (`mapAppearance_Ext`) |
| `ChangeModel`, `EntityProperty`, `VelocityControl`, etc. | their own ext flag |
| Base Classic (blocks, chat, entities, kick) | always OK |

If a flag is false, either **omit** the feature or **fall back** (below) — never
send the packet anyway.

### 20.1 Client tiers & fallbacks

- **Pure Classic (no CPE):** the handshake padding byte wasn't `0x42`, so no
  `ExtInfo`/`ExtEntry` — every `has*` flag is false. Send **only** base Classic.
  Custom Indev block ids in the level stream must be **remapped to a Classic
  fallback id** (each Indev block declares a fallback; a chest streams as e.g.
  wood, torch as e.g. a pillar) so the world still renders as *something* sane.
  No survival, no textures beyond default. Apply the §16 policy (visitor/deny on
  survival maps; build allowed on creative maps).
- **CPE but not `SurvivalTest`** (stock ClassiCube, other CPE clients): give them
  the world properly — `BlockDefinitions` for the Indev blocks (so they render
  correctly), the Indev texture pack URL (§19), env/colours. But send **no
  `SURV_*`** at all: they get no health, no mobs, no drops, no container GUIs.
  §16 policy still applies (visitor/deny on survival maps).
- **`SurvivalTest` fork client:** the full stream — `SURV_HELLO`, world info,
  mobs, health, inventory, containers, time, drops.

Fallback rule of thumb: **degrade the *view*, never the *world*.** A less‑capable
client may see a stand‑in block or miss the mobs, but the authoritative world on
the server is identical for everyone; the capable clients just see more of it.

### 20.2 Interactable blocks are safe for non‑survival clients — if you gate the open

A stock/CPE‑only client can't open a chest/furnace GUI (it doesn't speak our
protocol). That's **fine and non‑breaking, as long as the server never *infers* a
container interaction from a plain block action:**

- Container/inventory state lives **only** on the server and is mutated **only**
  by validated `SURV_CONTAINER_CLICK`/`SURV_INV_CLICK`/`SURV_USE_ITEM` intents,
  which a non‑survival client never sends. So a stock client **cannot** open,
  read, or corrupt a chest/furnace — to it, the block is just an opaque cube.
- The server must **not** treat a stock client's `SET_BLOCK_CLIENT` on/near a
  container as an "open" — opening is driven exclusively by the `SURV_*` intent,
  gated on `hasSurvival`. Keep the two paths separate.
- On a **survival** map, a non‑survival client is a visitor anyway (§16): its
  block edits are rejected, so it can't even break the chest.
- On a **creative** Indev map, a non‑survival client placing a block against a
  chest is just a normal, server‑validated block placement — harmless; the chest
  keeps being a plain block to it.

So: **no, interacting‑item blocks won't break for stock clients** — provided the
"open container" trigger is `hasSurvival`‑gated and never derived from a base
block packet. That is the flag that differentiates it.

### 20.3 Still validate capable clients

`hasSurvival` means "can receive/send our messages", **not** "trusted". Every
inbound `SURV_*` from a fork client is still validated server‑side (§8.2): bounds‑
check the 64‑byte payload (never over‑read), verify the action is legal (reach,
cooldown, slot/recipe legality, container open + in range), and reject → correct
with an authoritative echo. A negotiated extension is a capability, not a
permission.

### 20.4 Client identification via AppName — complementary, not a replacement gate

The client already sends a human‑readable **AppName** in the CPE `ExtInfo` handshake
(`Server.AppName`, built in `src/Server.c:560` from `GAME_APP_NAME` = "ClassiCube
1.3.8" in `src/Constants.h`, written by `CPE_SendExtInfo` `src/Protocol.c:929`). We
*can and should* make the fork advertise itself — e.g. append " Indev" so it reads
**"ClassiCube Indev 1.3.8"** — which is genuinely useful: it shows in MCGalaxy's
player list / logs (MCGalaxy already parses this via `ClientName()`), so admins can
see who's on the survival client at a glance.

**But use it for identity/UX and a coarse fallback signal only — the authoritative
capability gate stays the `SurvivalTest` CPE extension (§3, §20.0).** Why not gate
on the name:
- AppName is a free‑form, **spoofable, unversioned** string; string‑matching is
  brittle across version bumps and forks. The CPE ext is the standard,
  machine‑negotiated, **versioned** capability signal (branch on
  `survival_Ext.serverVersion` when the wire format changes).
- It conflates *identity* ("I am the Indev fork") with *capability* ("I support
  SurvivalTest v2"). Route on capability, label with identity.
- **Timing:** AppName is sent at handshake, *before* the client learns the map is
  Indev (in MP the mode is server‑decided and arrives in `SURV_HELLO` after the
  level). So it can't be a per‑map toggle — the **fork build always advertises
  Indev** because it's always survival‑capable. That's fine and cleaner: the name
  identifies the build, the ext confirms capability, and the per‑level `SurvivalMode`
  + §16 policy decide what each session is actually sent.

Recommendation: **do both** — set the fork AppName to "ClassiCube Indev 1.3.8"
(identity, logs, and a cheap secondary sanity check) **and** gate all routing on
`hasSurvival` (the real, versioned capability). If they ever disagree (name says
Indev, ext absent), trust the ext and treat the client as non‑survival.

### 20.5 Checklist

- [ ] Add `hasSurvival` per session in `AddExtension`; set it from the client's
      `SurvivalTest` `ExtEntry`.
- [ ] (Optional, recommended) Fork AppName → "ClassiCube Indev 1.3.8" for
      identification; keep routing gated on `hasSurvival`, not the name.
- [ ] Gate every `SURV_*` send + Indev `BlockDefinitions` + Indev texture URL on
      the matching flag(s); never send unnegotiated packets.
- [ ] Give every Indev block a Classic **fallback id** for no‑CustomBlocks clients.
- [ ] Ensure "open container" is triggered **only** by a `hasSurvival` intent,
      never inferred from `SET_BLOCK_CLIENT`.
- [ ] Combine per‑session caps with the per‑level `SurvivalMode` + §16 policy to
      decide each client's packet set on join.
- [ ] Validate + bounds‑check every inbound `SURV_*` regardless of `hasSurvival`.

## 21. Tick model & time‑of‑day (with the stock‑client fallback)

### 21.1 Tick rates & reconciliation

- **The server simulates at 20 TPS** — Indev's tick rate. Every faithful formula
  (mob invuln 20t, attack cooldowns, fuse 30t, air 300t, furnace 200t, day cycle
  24000t, random‑tick budget = volume/200) assumes 20 Hz, so the authoritative sim
  MUST run at 20 Hz regardless of MCGalaxy's default physics interval. Do not tie it
  to MCGalaxy's block‑physics tick if that differs — give the survival module its
  own 20 Hz scheduler.
- **Send rate ≤ tick rate.** The server need not transmit every tick. Positions/
  state go out at an adaptive/throttled cadence (e.g. mob moves a few times/sec,
  block/meta on change, health on change); the **client interpolates** between
  updates exactly as it already does for entities (`prev`→`next` snapshots).
- **The client never drives authoritative time or state** (§15.0). Its own tick is
  render/interpolation only; it displays what the server last told it. This is what
  keeps 30 fps and 144 fps clients identical — they interpolate the same
  server‑authored keyframes.
- Client‑side prediction stays limited to its own single block place/break (§15).

### 21.2 How each client tier sees day/night

Our Indev day/night (`Indev_TickDayNight`) is, on the client, **purely visual**:
it advances `worldTime`, eases the sky‑light level, and pushes five Env colours —
sky, cloud, fog, **sun (diffuse)** and **shadow (ambient)** — plus it renders the
moving sun/moon quads and stars. Crucially, **all the gameplay effects of time
(spawn darkness, grass/leaf light rules, mob sun‑burning) are server‑authoritative
(§15.2)**, so a client rendering the cycle imperfectly changes *nothing* about the
world. That's what makes the fallback safe.

Route per capability (§20):

- **Fork (`hasSurvival`):** send **`SURV_TIME`** (the `worldTime`/eased sky level).
  The client runs its own Indev colour curve locally and renders the moving
  sun/moon/stars — full fidelity, smooth between updates via local interpolation.
- **Stock / CPE‑but‑not‑survival (`hasEnvColors`):** the server drives the cycle
  with the **standard `EnvColors` CPE packet** (`OPCODE_ENV_SET_COLOR`), pushing
  updated sky(0)/cloud(1)/fog(2)/shadow=ambient(3)/sun=diffuse(4) colours over the
  day. ClassiCube applies `SunCol`/`ShadowCol` to lit/shadowed block faces, so the
  **whole world visibly darkens at night and brightens at day** for a stock client
  too — no custom code on their side. They just don't get the *moving* sun/moon or
  stars (that's our render), which is graceful, not broken.
- **No `EnvColors` support (ancient clients):** the map keeps its static day
  colours — no cycle, still no breakage.

> **This is exactly the fallback you asked for, and it can't break anything:**
> `EnvColors` is a standard, expected CPE packet (servers use it for custom skies
> every day); it's gated on `hasEnvColors` per §20; and because day/night is
> visual‑only on the client while the server owns every light‑driven mechanic,
> a stock client seeing a simpler sky has zero gameplay consequence.

### 21.3 Implementation notes

- **One source of truth:** the server increments `worldTime` at 20 TPS (port
  `Indev_TickDayNight`'s worldTime + skylight easing). From that single value it
  derives both `SURV_TIME` (for fork clients) and the `EnvColors` (for others) —
  so every tier is showing the same moment of the same day.
- **Cadence:** the Indev colour curve moves slowly (24000‑tick day). Send
  `EnvColors` only when a component actually changes (a handful of updates per
  minute), or a light fixed rate; ClassiCube applies them instantly, so small
  frequent steps look smooth without flooding bandwidth. `SURV_TIME` can be even
  sparser since the fork interpolates the curve locally.
- **Don't double‑drive the fork:** send a client **either** `SURV_TIME` **or** the
  `EnvColors` day stream, not both (routing on `hasSurvival`), so the fork's local
  curve isn't fighting server colour packets.
- The server may still use `EnvColors`/`EnvWeatherType` for non‑cycle effects
  (custom map ambience, rain) on any capable client as usual.

### 21.4 Checklist

- [ ] Server survival sim runs at a dedicated **20 TPS**, independent of MCGalaxy's
      physics interval.
- [ ] Server owns `worldTime`; derive `SURV_TIME` (fork) and `EnvColors` (others)
      from it. Route per `hasSurvival` (§20).
- [ ] Stock/CPE clients get the day/night via `EnvColors` (sky/cloud/fog/ambient/
      diffuse); verify a stock client's world darkens at night on an Indev map.
- [ ] Throttle `EnvColors`/`SURV_TIME` sends; clients interpolate. No client‑driven
      time.

## 22. Custom blocks on stock clients — growth updates & shapes/sizes

Two separate things, with different answers.

### 22.1 Block *updates* (seeds growing, farmland wetting, furnace lighting) — yes, everyone reads them

Growth/state changes are just **block‑id changes**, and those ride the **base
Classic `OPCODE_SET_BLOCK`** every client understands. Crop growth is a
server‑authoritative random tick (§15.2): the server advances the stage and sends
`SET_BLOCK` with the next crop id (our scheme uses **distinct ids per state** —
crops `85–92`, farmland dry/wet `83/84`, furnace lit/idle, chest facing `71–74`),
so **a stock client sees the block change to the grown stage** with no special
protocol. The metadata nibble never needs to cross the wire to stock clients —
the id itself carries the appearance. So: yes, stock ClassiCube reads seed growth,
farmland wetting, furnace lighting, etc.

### 22.2 Block *appearance/size* — mostly yes, via BlockDefinitions; a few degrade

Whether a stock client renders the *right shape* depends on `BlockDefinitions`
(and `BlockDefinitionsExt` for arbitrary boxes), which stock ClassiCube supports.
The server defines each Indev block's draw type, textures, collide, sound **and
shape** to the client:

- **Full cubes** (`IndevBlock_Define` sets MinBB 0,0,0 / MaxBB 1,1,1, `DRAW_OPAQUE`)
  — chest, furnace, diamond ore, bookshelf, etc. Render **perfectly** on any
  `BlockDefinitions` client (they're just textured cubes).
- **Custom boxes** — **farmland** is **15/16 tall** (genuine
  `BlockFarmland.setBlockBounds(0,0,0, 1, 15/16, 1)`; our block now sets
  `MaxBB.y = 15/16`), and the **torch** is a thin tall box (`MinBB` 7/16..9/16,
  `MaxBB` y 0..10/16); slabs are half‑height. These need **`DefineBlockExt`** (full
  min/max bounds) or the basic `DefineBlock` **Shape** byte (height → `MaxBB.y`,
  which covers farmland). A `BlockDefinitionsExt` client renders the exact box; a
  basic‑`BlockDefinitions` client gets the height (fine for farmland) but not odd
  x/z insets (torch). Send farmland's reduced height so stock clients see the
  1‑px‑shorter tilled soil. (Genuine keeps farmland's *collision* full‑height;
  CC ties render bounds to the collision box, so ours is 15/16 both — a harmless
  1/16 difference. The server should send the render height regardless.)
- **Sprite/cross blocks** — saplings, flowers, mushrooms: `DefineBlock` draw =
  sprite → stock clients render the X‑cross. Faithful (genuine Indev draws these as
  crosses too).
- **Custom‑render blocks** — the **crop "#" ground pattern** (`IndevTest_IsCropBlock`
  → our chunk builder draws the genuine BlockCrops row pattern, not a standard
  draw) and the **angled wall‑torch** placement are done by *our* renderer, which a
  stock client can't reproduce. They **degrade to the nearest CPE draw**: crops →
  an upright X‑cross sprite (still recognisable and still updating through the
  growth stages), wall torches → the standing torch box. Visible and correct in
  position/state, just not pixel‑identical.
- **No `BlockDefinitions` support** (ancient/pure‑Classic): the block falls back to
  a Classic **stand‑in id** (§20.1) — a plain cube — but block updates still apply.

So the short version: **stock clients render the different seed/farmland/etc.
blocks correctly** as long as they support `BlockDefinitions`/`BlockDefinitionsExt`
(nearly all modern ClassiCube builds do), because our special blocks are cubes,
reduced boxes (farmland 15/16, torch, slabs), or standard sprites — all
expressible; only the couple that need our bespoke renderer (crop "#" rows, angled
wall torches) degrade to the closest standard shape — never breaking, just looking
simpler.

### 22.3 Implication for the block‑definition parity

Extend the shared block table (§15.3 / §17.7 / §19.3) so the server's
`DefineBlock`/`DefineBlockExt` carry **draw type, per‑face textures, collide,
sound, light, and MinBB/MaxBB (shape)** — not just texture indices — matching the
client's `Blocks.*` table exactly. Send `DefineBlockExt` for the custom‑box blocks
(torch), basic `DefineBlock` for the cubes/sprites, and give crops/wall‑torch a
sprite/standing fallback shape for stock clients. Fork clients ignore these and use
their baked‑in bespoke renderer for full fidelity.

### 22.4 Paintings are entities, not blocks — bespoke, fork‑only

Paintings are **not blocks** — they're `EntityPainting` (genuine `setSize(0.5,0.5)`
with a per‑art bounding box derived from `art.sizeX/sizeY`, mounted flat on a wall
by facing). We render them with our own painting entity + the `kz.png` art atlas
(`SurvivalTest_RenderPaintings`, task #32). Stock ClassiCube has **no painting
entity type**, and there's no block to fall back to, so:

- **Fork clients:** stream paintings like mobs — a bespoke `SURV_PAINTING_SPAWN`
  (pos, facing, art id → picks size + atlas cell) / `SURV_PAINTING_REMOVE`, drawn
  by the existing entity renderer. The per‑art bounding box travels as the art id.
- **Stock / CPE clients:** they simply **don't see paintings** — harmless, since
  paintings are purely decorative and affect no gameplay or collision the server
  cares about. Don't try to fake them with a block; degrade to invisible.
- **Optional later:** a flat `CustomModels` model per art size could let stock CPE
  clients see a painting quad, but that's polish, not required.

Persistence: paintings already round‑trip in `.mclevel` (they're in the `Entities`
list, §18.1), so the server loads/saves them with the world and re‑streams to fork
clients on join.

### 22.5 Checklist

- [ ] Server defines every Indev block via `DefineBlock`/`DefineBlockExt` with the
      full shape/draw/texture/collide/sound set (not just textures).
- [ ] Send farmland's 15/16 render height; slabs' half height; torch's box.
- [ ] Custom‑box blocks (torch) use `DefineBlockExt`; crops/wall‑torch get a
      sprite/standing fallback shape for stock clients.
- [ ] Verify on a stock client: crops visibly advance through stages, farmland/
      furnace/chest render as the right blocks, and updates apply live.

## 23. Cross‑client fidelity pitfalls & miscellaneous tidbits

Things that bite when multiple clients (fork + stock, fast + laggy) share a
survival map. Most are "expected divergence is OK; authoritative world is not".

### 23.1 Entity ID spaces — keep survival ids private

Classic entity ids are **8‑bit**: `ENTITIES_MAX_COUNT` = 255 net players + local,
`ENTITIES_SELF_ID` = 255 (`src/Entity.h`). Other **players** use that system as
normal (skins/models/nametags). Our **mobs, item drops, and paintings must NOT go
into that list** — give them their **own 16‑bit id space** inside `SURV_*`.
Spawning mobs as Classic entities would both exhaust the 256 slots and collide
with players. (This is the concrete reason §15.1 chose bespoke mob transport.)

### 23.2 Don't let client‑side block physics fight the server

Our Indev random‑tick sim (grass/leaf/crop/fire/furnace/day‑night) is gated to SP
(§15.2). But ClassiCube *also* has a separate, **option‑driven** classic block
physics (`Physics.Enabled` = `OPT_BLOCK_PHYSICS`, default on — sand/gravel fall,
water/lava flow), toggled only in options, **not** auto‑disabled in MP. On a
networked survival map the server owns all of that, so ensure the client does not
locally predict it (force `Physics.Enabled` off, or otherwise ignore it, while a
`SurvivalTest` map is active) — otherwise a client predicts a sand fall the server
never confirms and desyncs until the next `SET_BLOCK`.

### 23.3 Interest management / bandwidth (Indev is mob‑heavy)

Indev spawn caps are large (`≈ vol·20/64³/2` monsters + animals). Broadcasting
every mob's position to every client each tick will saturate the link. **Stream
only the mobs within a relevance radius of each player**, at the throttled cadence
of §21.1, and drop/despawn them client‑side when they leave range. This is more
than Classic's all‑players broadcast — plan for it before mob sync (Phase 3).

### 23.4 Player movement is client‑authoritative; reconstruct, don't trust

Classic movement is **client‑driven** — the fork client runs its Indev player
physics locally and reports position; the server can't cheaply re‑run Indev
physics for every player. So: **validate** with sanity checks (max speed, teleport
distance, noclip) and **reconstruct fall distance from the position stream** to
compute fall damage server‑side (§13) — never trust a client "I took N damage" or
"I fell" message. A stock client uses Classic movement on an Indev map (slightly
different feel) — harmless.

### 23.5 Divergence between tiers is expected — and fine

Two clients at different capability tiers legitimately see **different things** at
the same spot: a `BlockDefinitions` client sees a chest, a pure‑Classic client
sees a stand‑in cube (§20.1); a fork client sees mobs/paintings a stock client
doesn't (§15.1/§22.4). That is by design — **degrade the view, never the world.**
Don't chase pixel‑parity across tiers; guarantee the *authoritative server world*
is identical for everyone and let each client render what it can.

### 23.6 Lighting‑mode variance is visual‑only

Clients differ in `Lighting_Mode` (classic vs fancy, `src/Lighting.h`) → different
brightness/shadows. This is **purely visual and harmless** because the server owns
every light‑driven mechanic (spawns, decay, mob burning — §15.2) and the client
must never derive state from local light. Optionally force a mode for consistency
with the `LightingMode` CPE ext; otherwise accept the variance.

### 23.7 Sounds are client‑derived from events, not streamed

The client plays mob/break/place sounds from received `SURV_*` state edges +
its **own** position for distance falloff (our `Audio` soundboard, §12). The
server sends events/state, not audio. Make sure the same event yields the same
sound on every fork client (drive sound off the authoritative state change, not
off local prediction).

### 23.8 Container concurrency

Two players, one chest: if A breaks or empties a chest B has open, the server must
**scatter/close/re‑sync** B's window. Serialise container mutations server‑side so
two clients' optimistic clicks can't both "win" (the server cursor per player, one
authoritative container state — §13). Test this explicitly with two clients.

### 23.9 Ruleset authority (the "enhanced" flag)

The server's per‑level `SurvivalMode` + enhanced ruleset is authoritative and
travels in `SURV_HELLO`; **ignore the client's local `survival-enhanced` option in
MP** so everyone on a map plays the same rules. (SP keeps reading the option.)

### 23.10 Version skew & malformed frames

Never assume the peer's `SurvivalTest` version matches. **Validate every message's
length before reading, branch on the negotiated ext version, and ignore unknown
message ids** (forward/backward compat). A malformed 64‑byte frame must never
crash or desync a client — bounds‑check, then drop.

### 23.11 Misc tidbits

- Client gate mirror: `Server.SupportsSurvival` = `IsSupported(survival_Ext)`;
  all client survival code keys off `!Server.IsSinglePlayer && Server.SupportsSurvival`.
- Other **players** render via the normal Classic entity/model path — held block
  via `HeldBlock` (`OPCODE_HOLD_THIS`), model via `ChangeModel`; showing their
  worn armor/held *item* is later polish (`CustomModels`).
- **Reach:** the `ClickDistance`/`SetReach` CPE changes a client's interaction
  distance — survival reach is validated **server‑side** regardless (§8.2/§20.3);
  a client can't extend reach by negotiating a bigger click distance.
- The **Human mob** uses `char.png` (default skin) — ensure the pack provides it
  and a mob never adopts a connecting player's skin/entity.
- Re‑run the **stock‑server regression baseline** (§9) after every networking
  change — it's the cheapest guard against silently breaking Classic.

### 23.12 Cross‑client fidelity checklist

- [ ] Survival mob/drop/painting ids live in a private 16‑bit space, not the
      Classic entity list.
- [ ] Client‑side classic block physics forced off on `SurvivalTest` maps.
- [ ] Mob streaming uses per‑player relevance + throttling.
- [ ] Fall damage reconstructed server‑side from position; movement sanity‑checked.
- [ ] Container mutations serialised; two‑client chest test passes.
- [ ] Every inbound `SURV_*` length‑validated + version‑branched; unknown ids dropped.
- [ ] Ruleset from `SURV_HELLO`, not the local option, in MP.

## 24. Player‑to‑player interactions & PvP

**Framing first (important for the faithfulness mandate):** neither Indev nor
c0.30 Survival Test ever had multiplayer, so *any* player‑to‑player interaction is
**non‑genuine** — a designed feature layered on the (already non‑genuine) MP
system, with Beta as the loose inspiration. Therefore PvP is a **per‑level,
opt‑in server toggle, OFF by default**, so faithful survival maps stay pure
single‑player‑style co‑existence and only maps that explicitly enable it get
combat. Never make PvP a client‑side assumption.

### 24.1 It's the mob‑combat pipeline with a player target

PvP reuses everything from mob combat (§13, §8.2) — the only new part is that the
*target* is another player, and players are **Classic 8‑bit entities**, not our
16‑bit `SURV` mobs (§23.1). So the attack intent must say *which id space*:

- **`SURV_ATTACK { targetKind, targetId }`** — `targetKind` = mob (16‑bit `SURV`
  id) or player (8‑bit Classic entity id). This unifies "hit a mob" and "hit a
  player" through one server‑validated path.
- Alternatively, the standard **`PlayerClick`** packet already carries the clicked
  **entity id** (`CPE_SendPlayerClick(button, pressed, targetId, raytracer)`,
  `src/Protocol.c:861`) and can serve as the player‑target signal; but `SURV_ATTACK`
  is cleaner because it also covers mobs. Pick one and be consistent.

### 24.2 Server‑authoritative damage (never trust the client)

On a valid attack intent, the **server** does all of it (client only asked):

1. **Gate:** PvP enabled on this level, both parties are survival combatants
   (not visitors — §16, §24.4), attacker off cooldown, target in reach + line of
   sight, target not in a safe/spawn zone.
2. **Damage:** the attacker's held‑item melee value (`IndevTest_MeleeDamage(heldId)`,
   `src/IndevTest.c:385`) → the target's damage routine with **armor absorption**
   and the **20‑tick invulnerability window** (reuse the `SurvivalTest_Damage`
   math, §13). Wear the attacker's weapon (`IndevTest_ToolUseWear(..., true)`).
3. **Apply + feedback:** push the target's new health (`SURV_HEALTH`), server‑apply
   **knockback** away from the attacker via `VelocityControl` (`velControl_Ext`,
   `src/Protocol.c:1506`), and broadcast a hurt state so nearby clients show the
   flash/sound.

The victim's own client shows the **hurt camera tilt** (`st_hurtTicks` /
`SurvivalTest_GetHurtTilt`, `src/SurvivalTest.c`) when it receives the health drop —
no special packet needed, just drive it off the `SURV_HEALTH` decrease.

### 24.3 Death & loot — a design decision, not a port

Genuine Indev/c0.30 have **no death‑drops** (death = Game Over / respawn‑by‑reload).
Since PvP is non‑genuine anyway, dropping the victim's inventory on death is a
*choice*:
- **Faithful default:** no drops — victim sees the existing `GameOverScreen`,
  server respawns them (reload/teleport to spawn). Simplest, matches SP.
- **PvP‑map option:** drop inventory/armor as `SURV_DROP_*` at the death spot
  (Beta‑style loot). Make it a per‑level flag alongside the PvP toggle.
Either way the **server** decides and drives it; the client just renders.

### 24.4 Cross‑client behaviour & pitfalls

- **Only survival combatants fight.** On a survival map, non‑`SurvivalTest`
  clients are **visitors** (§16) — the server treats them as **non‑combatants**:
  not targetable (ignore attacks against them) and unable to attack. They have no
  server‑side health to damage.
- **Tier‑dependent visuals.** The victim gets health/knockback/tilt; nearby **fork**
  clients can show the red hurt‑flash on the attacked player entity (our render
  hook, same idea as mobs — standard ClassiCube doesn't flash players) + the hit
  sound; **stock** clients just see the victim get knocked (a normal position
  update). Graceful, not broken.
- **Knockback vs client‑authoritative movement (§23.4).** Movement is
  client‑driven, so knockback is a server `VelocityControl` the victim's client
  applies; the server then reconciles the reported position. Don't compute PvP
  fall/knockback damage from a client claim — reconstruct from the position stream.
- **Combat log / anti‑abuse.** Standard MCGalaxy concerns apply — spawn protection,
  safe zones, per‑area PvP flags, and rate/cooldown limits are all **server‑side**;
  a client can't bypass them by spamming `SURV_ATTACK` (validate cooldown + reach
  every hit, §20.3).
- **Friendly context.** Non‑combat player interactions (seeing each other's held
  block via `HeldBlock`, models via `ChangeModel`, nametags via ExtPlayerList) are
  the normal Classic/CPE entity plumbing (§23.11) and work regardless of PvP.

### 24.5 Checklist

- [ ] PvP is a per‑level toggle, **default off**; faithful maps unaffected.
- [ ] `SURV_ATTACK` carries target kind (mob 16‑bit / player 8‑bit) → one validated
      server path for both.
- [ ] Server computes damage (`IndevTest_MeleeDamage` + armor + invuln), applies
      health + `VelocityControl` knockback; victim tilt from the `SURV_HEALTH` drop.
- [ ] Visitors are non‑combatants (untargetable, can't attack).
- [ ] Death drops are a per‑level option; faithful default = no drops + Game Over.
- [ ] Every hit re‑validated (PvP on, reach, LOS, cooldown, not safe‑zone); never
      trust client‑reported damage.

## 25. Wire format v1 — concrete byte layouts (starting spec)

All on channel `0xB0`, packet `[0x35][0xB0][64‑byte payload]`. Payload `[0]` = the
1‑byte message id below; `[1..63]` = fields. **Big‑endian.** Positions are int16
fixed‑point `= round(coord * 32)` per axis (6 bytes/xyz) — swap to int32
(`ExtEntityPositions`) for maps taller/wider than the int16/32 range. Yaw/pitch are
`uint8 = round(deg * 256/360)`. This is a *first draft to implement against and
refine*, not frozen; bump the `SurvivalTest` ext version when it changes (§20/§23.10).

**Server → client (state):**

```
SURV_HELLO      0x01  [1]mode(0 off/1 c030s/2 indev)
                      [2]flags(b0 enhanced, b1 creative, b2 pvp, b3 deathDrops)  [3]protoVer
SURV_WORLDINFO  0x02  ** v2 AS IMPLEMENTED (SurvivalTest ext version 2 - the
                      i16 promotion; floating maps genuinely use groundLevel
                      -128 / waterLevel -127, which v1's u8 clamped to 0): **
                      [1..2]groundLevel(i16) [3..4]waterLevel(i16) [5]fluidId
                      [6]theme  [7]flags(b0 floating)  [8]sidesBlk  [9]edgeBlk
                      (env colours ride the stock EnvColors CPE path meanwhile;
                      both sides branch on the NEGOTIATED ext version - a v1
                      peer still speaks/receives the old layout below)
                      -- v1 layout (legacy):
                      [1]groundLevel(u8) [2]waterLevel(u8) [3]fluidId [4]theme
                      [5]flags(b0 floating) [6]sidesBlk [7]edgeBlk
                      -- still-deferred fuller revision (next ext bump):
                      [+]sidesOffset(i16)  skyRGB  fogRGB  cloudRGB
                      [+]cloudHeight(i16)  worldTime(u16)  skyBrightness
SURV_HEALTH     0x03  [1]health(0..20)  [2..5]score(i32 BE - as shipped by the
                      MCGalaxy fork's SurvivalNet.cs; an early draft said i16)
SURV_TIME       0x04  [1..2]worldTime(u16)  [3]easedSkyLight(0..15)
SURV_MOB_SPAWN  0x10  [1..2]mobId(u16)  [3]type  [4..9]pos  [10]yaw [11]pitch
                      [12]health  [13]flags(b0 helmet,b1 armor,b2 fur)
SURV_MOB_MOVE   0x11  [1..2]mobId  [3..8]pos  [9]yaw  [10]pitch
SURV_MOB_STATE  0x12  [1..2]mobId  [3]health  [4]flags(b0 hurt,b1 fuse,b2 onFire,
                                                       b3 graze,b4 dead,b5 noFur)
                      (b5 added in the phase-3 implementation - a shear must be
                      visible mid-life; same bytes, no ext-version bump needed)
SURV_MOB_DESP   0x13  [1..2]mobId  [3]reason
SURV_INV_FULL   0x20  [1]baseSlot  [2]runLen  then runLen×{id(u16),count(u8),dmg(i16)}
                      (5 bytes each → ≤12/frame; chunk across frames)
SURV_INV_SLOT   0x21  [1]slot  [2..3]id(u16)  [4]count  [5..6]dmg(i16)
                      (slots: 0..35 main, 36..44 craft, 45..98 container, 100+ armor)
SURV_CONT_OPEN  0x22  [1]kind(1 chest/2 furnace/3 large)  [2]rows
SURV_CONT_SLOT  0x23  [1]slot  [2..3]id  [4]count  [5..6]dmg
SURV_FURN_PROG  0x24  [1]burn(0..12)  [2]cook(0..24)
SURV_CURSOR     0x25  [1..2]id(u16)  [3]count  [4..5]dmg  (server-owned held stack)
SURV_DROP_SPAWN 0x30  [1..2]dropId(u16)  [3..4]itemId(u16, ≥256 = item)  [5]count
                      [6..11]pos  [12..17]vel(i16 = coord/sec × 512)  [18]rot0(u8)
SURV_DROP_PICKUP 0x31 [1..2]dropId  [3]pickerEntityId(u8 Classic entity id)
SURV_DROP_REMOVE 0x32 [1..2]dropId  [3]reason(0 despawn/1 destroyed)
SURV_PLAYER_EQUIP 0x50 [1]entityId(u8)  [2..3]heldId(u16)  [4..11]armor[4](u16 each)
SURV_BLOCKMETA  0x40  [1..6]xyz(i16 block coords)  [7]meta
```

**Client → server (intents):** (server validates every one — §8.2/§20.3)

```
SURV_ATTACK       0x80  [1]targetKind(0 mob/1 player)  [2..3]targetId
                        (mob = 16‑bit SURV id; player = 8‑bit Classic entity id)
SURV_USE_ITEM     0x81  [1]heldSlot  [2..7]targetBlock xyz  [8]face
SURV_SLOT_CLICK   0x82  [1..2]slotIdx(u16, extended: 0..35 main, 36..44 craft,
                        45..98 container, 100..103 armor)  [3]button(0 L/1 R)
SURV_RESULT_CLICK 0x83  (take the craft result onto the cursor)
SURV_CONT_CLOSE   0x84  (window closed → server returns cursor + craft grid)
SURV_HELD_SLOT    0x85  [1]hotbarIndex
SURV_DROP_ITEM    0x86  [1]slot  [2]wholeStack(0/1)
SURV_RESPAWN      0x87  (menu action)
```

Reuse standard packets where they fit (don't duplicate): player movement =
Classic position packets (+`ExtEntityPositions`); held *block* = `HeldBlock`;
knockback = `VelocityControl`; env colours for stock clients = `EnvColors` (§21).

### 25.1 Other players' equipment (armor / held item)

Showing a *remote* player's worn armor and held item is bespoke — standard
ClassiCube renders neither on other entities. Stream **`SURV_PLAYER_EQUIP`**
(above): the player's Classic entity id + held item id + the 4 armor ids. Fork
clients render it with the same hooks as the local player (`IndevArmor_Render`
plates + the held‑item render) applied to that entity; **stock clients ignore it**
(no handler) — they just see the base player model. This parallels the mob
approach (§15.1): bespoke, fork‑only, graceful degradation. It's **polish**, not a
Phase‑0..4 requirement — the local player's own armor/health/HUD comes first.

## 26. Item drops (deep dive + code pointers)

Drops are their own entity system in `src/SurvivalTest.c` — read it before
networking them; the model below reuses it almost wholesale.

### 26.1 Client code map

- **State:** `struct DropItem` (`position`,`prevPos`,`velocity`, `block` = full id
  space [block id, or ≥256 item id], `count`, `age`/`prevAge` for the spin/bob/glow,
  `rot0`, `pickupDelay`, `onGround`, `pickingUp`/`pickupTime`/`pickupFrom` for the
  fly‑in). Pool `st_drops[DROP_MAX]` (`DROP_MAX = 256`, evicts oldest when full).
- **Spawn:** `SurvivalTest_SpawnDropAt` (`:538`) — the **only RNG** is here (spawn
  velocity `(rand·0.2−0.1)·20` per axis + `rot0`); `SpawnDropWorld`/`SpawnDrop` are
  the public/inset wrappers. `pickupDelay` = 10t Indev toss / 0 c0.30.
- **Physics:** `SurvivalTest_DropPhysics` (`:761`) — **deterministic**: gravity,
  `Collisions_MoveAndWallSlide`, `×0.98` drag, `×0.7` ground friction. **No per‑tick
  RNG** (verified). This is what lets clients simulate from spawn state (§26.3).
- **Pickup:** `SurvivalTest_DropTryPickup` (`:802`) — AABB overlap of the drop vs
  the player bb grown ±1 horizontally (genuine `findEntities(bb.grow(1,0,1))`), then
  `addResource()` (partial stacks allowed, stays if inventory full), then the
  `TakeEntityAnim` fly‑in (removes after ~3t).
- **Tick/despawn:** `SurvivalTest_TickDrops` (`:895`); despawn at `age ≥ 6000`
  ticks (Item.tick). Save/iterate: `SurvivalTest_DropNext` (already in `.mclevel`
  Entities — §18.1). Render: `IndevTest_DropIsSprite` (mini‑block cube vs items.png
  billboard) + the age‑driven spin/bob/glow.

### 26.2 Drop sources (all become server‑side)

Block break (`SurvivalTest_GetBlockDrop`/`SpawnIndevDrops`), mob death
(`indevDeathDrop`), chest scatter (`IndevTE_Scatter`), TNT/explosion
(`ExplodeDropsForBlock`), player Q‑toss, and (optional) PvP death (§24.3). In MP
the **server** runs each of these and emits `SURV_DROP_SPAWN`.

### 26.3 Networking model (server‑authoritative, client‑simulated render)

- **Spawn:** the server does the RNG (velocity/rot0) and sends
  `SURV_DROP_SPAWN { dropId, itemId, count, pos, vel, rot0 }`. The client seeds a
  local `DropItem` from that and runs the **same deterministic `DropPhysics`** — so
  the drop falls and settles **identically on every client** with *no per‑tick
  position stream*. (Optional `SURV_DROP_MOVE` only if a block changes under a
  resting drop and positions could diverge — rare.)
- **Pickup is server‑authoritative:** the server (which tracks player positions from
  the movement stream + its own drop sim) detects the overlap + `pickupDelay` +
  inventory space, updates the inventory (`SURV_INV_SLOT`), and sends
  `SURV_DROP_PICKUP { dropId, pickerEntityId }`. The **picker's** client plays the
  fly‑in animation; **all** clients then remove the drop. Never let the client
  self‑award a pickup (anti‑dupe / anti‑reach‑hack).
- **Despawn (6000t)** and merging (Indev/c0.30 don't merge drops — keep them
  separate) are server‑owned; a `SURV_DROP_REMOVE` retires the entity everywhere.
- **Interest management (§23.3):** many drops appear at once (a chopped tree, a
  chest scatter). Batch the `SURV_DROP_SPAWN` frames, and only stream drops within a
  relevance radius of each player; drop them client‑side when out of range. The
  256‑slot pool is per‑client render budget, not a server cap.

### 26.4 Checklist

- [ ] Server runs all drop sources + deterministic `DropPhysics` + 6000t despawn.
- [ ] `SURV_DROP_SPAWN` carries pos+vel+rot0; clients simulate locally (no per‑tick
      stream); verify a drop lands in the same cell on two clients.
- [ ] Pickup is server‑detected → `SURV_INV_SLOT` + `SURV_DROP_PICKUP`; the picker
      plays the fly‑in, everyone removes. No client self‑award.
- [ ] Batch multi‑drop scatters; relevance‑filter per player.

## 27. Inventory / crafting / container transactions & interaction clicks

This is the fiddliest survival subsystem (Beta's window transactions were
notoriously bug‑prone). The good news: our click logic is already one clean,
server‑portable function, and the Classic transport is **TCP** (ordered +
reliable), which removes most of Beta's difficulty.

### 27.1 The whole click model is one function

`SurvivalTest_SlotClick(idx, rightClick)` (`src/SurvivalTest.c:6326`) is the entire
inventory interaction, driven by a single held stack `st_cursor` (`:6317`). `idx`
is the unified extended‑slot index (main / craft / container / armor) resolved by
`SurvivalTest_SlotPtr` (`:6264`). The four cases:

1. **cursor empty** → pick up all (or `ceil(count/2)` on right‑click);
2. **cursor id == slot id** → merge (all, or 1 on right‑click), respecting `ST_MaxStack`;
3. **slot empty + right‑click** → drop exactly one;
4. **else** → swap slot ↔ cursor.

Plus `SurvivalTest_ResultClick` (`:6375`, take craft result onto cursor + consume
one of each grid ingredient), `SurvivalTest_CursorReturn` (`:6392`, put the cursor
stack back on window close — never lose it), armor‑slot validation
(`IndevTest_ArmorPiece`, only accepts the matching piece), and the crafting matcher
`IndevTest_MatchRecipe` / `SurvivalTest_CraftResult`. **The server runs these exact
functions on its authoritative state** — the port is nearly 1:1.

### 27.2 Authority & the transaction protocol

- **Server owns the inventory, the craft grid, the open container, AND the cursor**
  (per player). The client sends *intents*; it never mutates authoritative state.
- **Messages (from §25):** `SURV_SLOT_CLICK { slotIdx, button }`,
  `SURV_RESULT_CLICK`, `SURV_CONT_CLOSE`. The server applies the click via its
  `SurvivalTest_SlotClick`/`ResultClick` and **echoes** the changed slots
  (`SURV_INV_SLOT` / `SURV_CONT_SLOT`) **and the cursor** (`SURV_CURSOR`). The
  cursor is the shared mutable state that makes clicks stateful — because it's
  server‑owned and echoed, a client can't lie about it or dupe with it.
- **TCP saves us from Beta's confirm dance.** Clicks arrive **in order and never
  drop**, so the server is a simple deterministic sequencer — no transaction ids,
  no window‑confirm/resync packets (Beta needed those because of its own quirks).
  Apply click, echo state, done.
- **Optimistic prediction (polish, not required):** for instant UI, the client may
  run its local `SurvivalTest_SlotClick` immediately, then **reconcile** when the
  `SURV_INV_SLOT`/`SURV_CURSOR` echo arrives (server wins — §15.0). In the common
  case prediction == echo, so nothing visibly changes; on divergence (e.g. a shared
  chest slot changed under you) it snaps. A correct **first cut can skip prediction**
  (echo‑only: one round‑trip of latency per click) and add it later.

### 27.3 Crafting specifics

- The 2×2 / 3×3 grid cells are just slots (`st_craft`, `SURVIVAL_CRAFT_BASE`); clicks
  into them go through the same `SURV_SLOT_CLICK` path.
- The **result** is computed server‑side from the grid (`IndevTest_MatchRecipe`,
  incl. mirrored layouts) every time the grid changes. It's a *virtual* slot — the
  client can preview it locally (it has the grid) or the server can push a
  `SURV_CONT_SLOT`‑style preview. Taking it is `SURV_RESULT_CLICK` → the server runs
  `ResultClick` **atomically** (consume one of each ingredient + produce the result
  onto the cursor) so there's no dupe window.
- Shift‑click "craft‑all"/quick‑move isn't in the current SP code; if added, it's a
  new server‑side op — don't let the client compute the moves.

### 27.4 Containers & concurrency

- **Open** (right‑click a chest, `hasSurvival`‑gated — §20.2): server sets the open
  tile entity (`IndevTest_OpenContainer`, incl. the double‑chest `indev_openTE2`
  routing) and sends `SURV_CONT_OPEN` + the slots. **Close**: `SURV_CONT_CLOSE` →
  server `CursorReturn` + returns/clears the craft grid so nothing is lost.
- **Concurrency (two players, one chest):** the container state is single and
  server‑owned; every mutating click re‑echoes the changed `SURV_CONT_SLOT` to **all
  current viewers**. If the chest is broken while open, scatter + `SURV_CONT_CLOSE`
  the viewers (§23.8). Furnace burn/cook progress streams via `SURV_FURN_PROG`.

### 27.5 Interaction clicks (attack / use / place‑break)

"Entity/block clicking" splits by what's clicked — all server‑validated:

- **Attack** a mob or player → `SURV_ATTACK { targetKind, targetId }` (§24).
- **Use / right‑click** a block (open chest/furnace, use hoe/flint&steel/bucket,
  eat) → `SURV_USE_ITEM { heldSlot, targetBlock, face }`; the server runs the
  genuine use logic (`IndevTest_UseHeldItem`, container open, etc.) and echoes the
  results (block change, inventory, container open).
- **Place / break** a block → the **standard Classic `SET_BLOCK_CLIENT`**; the
  server validates (reach, tool, survival rules), consumes/wears server‑side, drops
  server‑side, and confirms with the authoritative `SET_BLOCK` (reverting on
  reject). Don't invent a survival packet for plain block edits.
- The CPE **`PlayerClick`** packet (`CPE_SendPlayerClick`, carries button + target
  entity id + raytrace) is an available transport if you'd rather derive
  attack/use from it than add explicit intents — but explicit `SURV_*` intents are
  clearer and cover our bespoke mobs (which aren't Classic entities). Pick one.

### 27.6 Anti‑dupe invariants (do not violate)

- Never apply the client's *claimed* cursor/slot contents — the server uses its own.
- Result‑take and place‑consume are **atomic** server operations.
- Every mutation echoes authoritative state; the client always accepts it.
- Window close always accounts for the cursor + craft grid (return, don't drop into
  the void or duplicate).

### 27.7 Checklist

- [ ] Port `SlotClick`/`ResultClick`/`CursorReturn`/`MatchRecipe` server‑side, run
      on authoritative state; cursor is server‑owned.
- [ ] `SURV_SLOT_CLICK`/`SURV_RESULT_CLICK`/`SURV_CONT_CLOSE` in; echo
      `SURV_INV_SLOT`/`SURV_CONT_SLOT`/`SURV_CURSOR`. Echo‑only first, prediction later.
- [ ] Craft result computed + taken atomically server‑side.
- [ ] Container concurrency: re‑echo to all viewers; break‑while‑open handled.
- [ ] `SURV_USE_ITEM` for right‑click use/open; `SET_BLOCK_CLIENT` for place/break;
      `SURV_ATTACK` for combat. All validated; nothing client‑authoritative.

## 28. Server persistence lifecycle — when / where / how to save it all

§18 covers the `.mclevel` *format*; this covers the server's *save lifecycle* —
so nothing is lost when a map unloads or the server shuts down. MCGalaxy's native
`.lvl` stores only blocks, so the survival state needs its own persistence.

### 28.1 Everything that must survive a restart

- **Shared world state:** the block array + `Data` metadata nibbles (chest facing,
  farmland moisture, crop stage, furnace lit, fire age), `worldTime` (time of day),
  surroundings/env (ground/water level, theme, floating, colours), tile‑entity
  contents (**chest + furnace** items + furnace burn/cook), and the **live mob**
  list (type/pos/health).
- **Per‑player state:** each player's inventory (36) + armor (4) + **health** +
  score + position/rotation. In MP many players share one map, so this is
  per‑(map, player), unlike the single `LocalPlayer` in an SP `.mclevel`.

### 28.2 Format: reuse `.mclevel` (+ per‑player files)

Don't invent a new format — the `.mclevel` NBT already carries all the *shared*
state (§18.1: `Map`+`Data`, `Environment`/`TimeOfDay`/surroundings, `TileEntities`,
mob `Entities`) and is client‑compatible. Split it:

- **`world.mclevel`** — the shared world snapshot (blocks, metadata, time,
  surroundings, tile entities, mobs). No `LocalPlayer`, or an optional last‑seen one
  purely so the file still opens in the SP client.
- **`players/<name>.nbt`** — one small NBT per player: their `Inventory`(+Slot 100+
  armor), `Health`, `Score`, `Pos`/`Rotation` — the same item/entity schema as
  §18.1, just scoped to a player. (Alternative: fold players into `world.mclevel`'s
  `Entities` if you prefer a single file; per‑player files are cleaner for MP and
  for saving on disconnect.)

Runtime stays MCGalaxy `Level` (blocks) + the SurvivalTest module's in‑memory state
(inventories, mobs, tile entities, time); save = serialise that to the files above;
load = read them back into the module.

### 28.3 The auto‑generated folder

Create it lazily on first save of a survival map — a per‑map folder so everything
for a world lives together:

```
survival/
  <mapname>/
    world.mclevel          # shared world + tile entities + mobs + time
    players/
      <player1>.nbt        # inventory/armor/health/score/pos
      <player2>.nbt
```

(`survival/` sits alongside MCGalaxy's existing `levels/` + `levels/level
properties/` sidecar convention — MCGalaxy already keeps per‑level sidecar data, so
this follows the grain. Pick the exact root to match the fork's layout.)

### 28.4 When to save (hook MCGalaxy's lifecycle)

- **Autosave interval** — piggyback MCGalaxy's periodic level autosave; write the
  survival files whenever the level saves.
- **`/save` / manual save** — same path.
- **Level unload** (last player leaves, `/unload`) — save then free the module state.
- **Player disconnect / leave** — save *that player's* `players/<name>.nbt`
  immediately, so a crash or DC never loses their progress even if others stay.
- **Server shutdown** — flush every loaded survival level + all online players.
- **On join** — load the player's file (or start a fresh kit if none); load the
  world state if the map isn't already resident.

Hook these on MCGalaxy's level save / `OnLevelUnload` / player‑disconnect / shutdown
events (the SurvivalTest server module subscribes — §5.2, §8.2).

### 28.5 Gotchas

- **Crash safety:** write to `*.tmp` then atomic‑rename over the real file, so a
  crash mid‑write can't corrupt a save. Save per‑player on disconnect (cheap).
- **Gen vs load (mobs):** a freshly *generated* Indev world runs the 1000‑pass
  initial spawn (§8.1); a *loaded* world must **restore its saved mobs** and NOT
  re‑run the initial population, or every reload doubles the mob count. Same rule
  the SP `.mclevel` load already follows.
- **Per‑player scope decision:** per‑(map, player) (each world its own save, most
  Indev‑faithful) vs per‑player‑global (inventory follows you between maps). Pick
  one up front; it changes where `players/` lives. Recommend per‑(map, player).
- **Consistency:** save the shared world and its tile entities/mobs together (one
  snapshot) so a chest's contents can't desync from the block that holds it.
- **Don't double‑persist:** the `.mclevel` is the survival save; don't also try to
  cram survival data into the `.lvl` — keep `.lvl` as the plain block cache MCGalaxy
  expects, and let `world.mclevel` be the source of truth for survival state (or
  regenerate the `.lvl` block cache from it on load).

### 28.6 Checklist

- [ ] SurvivalTest module serialises shared state to `survival/<map>/world.mclevel`
      and each player to `survival/<map>/players/<name>.nbt`.
- [ ] Save hooks: autosave, `/save`, unload, player disconnect, shutdown.
- [ ] Load hooks: on map load restore world+tiles+mobs; on join restore the player.
- [ ] Loaded worlds restore saved mobs; only *generated* worlds run the initial spawn.
- [ ] Temp‑file + atomic rename; per‑player save on disconnect.

## 29. Server‑side admin/debug commands (MCGalaxy)

Port the client `/client …` debug toolkit to real MCGalaxy commands — but now they
are **authoritative server actions** that mutate server state and stream the result
to clients via `SURV_*`, not local pokes.

### 29.1 The set to port (our client debug commands are the spec)

From `src/SurvivalTest.c` (the F9 menu became `/client` chat commands, task #34):
- **spawn** — `SurvivalTest_DebugSpawnMob(type, noAI, armor)` (+ `DebugSpawnTnt`,
  `DebugSpawnDrops`, arrow): `/client spawn <zombie|skeleton|pig|creeper|spider|
  sheep|human|tnt|drops|arrow> [count] [noai] [armor]`.
- **give** — `SurvivalTest_DebugGiveItem(id)` / `GiveCommand`: give any block/item
  (tools, food, materials) by name or id, optional count.
- **time** — set `worldTime`. **god** — invulnerability toggle. **heal** / **hurt** —
  set/adjust health. **arrows** — give arrows. **kill** — `DebugKillAllMobs`.

Server‑side each does the authoritative thing + emits the sync message:
`spawn` → server spawns the mob in its sim → `SURV_MOB_SPAWN` to nearby clients;
`give` → adds to the player's **server** inventory → `SURV_INV_SLOT`; `time` →
sets server `worldTime` → `SURV_TIME` (+ `EnvColors` for stock clients, §21);
`heal/hurt/god` → server health → `SURV_HEALTH`; `kill` → despawn all → `SURV_MOB_DESP`.
**Reuse the client functions as the exact behaviour spec** (types, counts, item
table, parse rules) — the C# is a port, not a redesign.

### 29.2 Making an MCGalaxy command (structure)

MCGalaxy commands are `Command` subclasses. One **parent** command with
subcommands keeps the namespace clean and mirrors our `/client` layout (and avoids
colliding with MCGalaxy's own `/spawn` = go‑to‑spawn and `/give` = economy):

```csharp
public sealed class CmdSurv : Command2 {
    public override string name { get { return "Surv"; } }
    public override string shortcut { get { return "sv"; } }
    public override string type { get { return CommandTypes.Other; } }
    // >>> permission: staff only (moderator+). See §29.3.
    public override LevelPermission defaultRank { get { return LevelPermission.Operator; } }

    public override void Use(Player p, string message, CommandData data) {
        // require a survival level (§29.4)
        if (!IsSurvivalLevel(p.level)) { p.Message("Not a survival map."); return; }
        string[] a = message.SplitSpaces();
        switch (a[0].ToLower()) {
            case "spawn": /* validate, spawn mob in server sim, SURV_MOB_SPAWN */ break;
            case "give":  /* add to server inventory, SURV_INV_SLOT */ break;
            case "time":  /* set worldTime, SURV_TIME + EnvColors */ break;
            case "god": case "heal": case "hurt": /* server health, SURV_HEALTH */ break;
            case "kill":  /* despawn mobs, SURV_MOB_DESP */ break;
            default: Help(p); break;
        }
    }
    public override void Help(Player p) {
        p.Message("&T/Surv spawn <mob> [count] [noai] [armor]");
        p.Message("&T/Surv give <item> [count] &H| time <t> | god | heal | hurt | kill");
    }
}
```

Use `Command2`/`CommandData` (the current MCGalaxy base) and MCGalaxy helpers for
arg parsing, player lookup (`PlayerInfo.FindMatches`), and messaging (`p.Message`).

### 29.3 ⚠️ Permissions — set to moderator+ (staff only)

**Set `defaultRank` to a staff level (`LevelPermission.Operator`, or the fork's
`Moderator` rank if it defines one).** These commands spawn mobs, hand out items,
toggle god mode and edit health/time — regular players must **not** have them, or
survival is trivially broken. Operators can still re‑tune it at runtime with
`/cmdset Surv <rank>`; the point is the **default is staff‑only**, never Guest.
(MCGalaxy's default ranks are Guest/Builder/AdvBuilder/Operator/Owner — if the fork
has a custom "Moderator" rank between AdvBuilder and Operator, use it; otherwise
Operator is the standard staff default.)

### 29.4 ⚠️ Auto command load — it must be picked up when compiled

MCGalaxy **auto‑registers** commands compiled into it: `Command.InitAll()` reflects
over the assembly and registers every **public, non‑abstract `Command` subclass**.
So to get the new command loaded when compiled:
- Make the class **`public sealed class Cmd…  : Command2`** (not internal/abstract)
  and put it in the **MCGalaxy assembly** that `InitAll` scans (or ship it as a
  plugin DLL in `plugins/` for the plugin loader to pick up).
- Confirm it appears in `/cmdlist` / `/help Surv` after build — that's the proof it
  auto‑loaded. If it doesn't show, it wasn't in the scanned assembly or wasn't
  public.
- Gate its *behaviour* on survival levels (§29.4 check `IsSurvivalLevel`), but its
  *registration* is unconditional (it just no‑ops on non‑survival maps).

### 29.5 Notes

- **Authoritative + streamed:** never mutate only a client — do it on the server and
  let the normal `SURV_*` sync carry it to everyone (so `/surv spawn` shows the mob
  to all nearby players, `/surv give` echoes the real inventory).
- **Target selection:** default to the invoking player / their current level;
  accept an optional player arg for staff acting on others (MCGalaxy convention).
- **Reuse the tables:** the item/block name→id table and mob type list are the same
  as the client's (`indev_items[]`, the mob enum) — share one definition (§15.3) so
  `/surv give iron pickaxe` means the same thing on both ends.
- **Logging:** these are staff actions — log them (MCGalaxy logs command use), useful
  for abuse review.

### 29.6 Checklist

- [ ] One parent `/Surv` command (subcommands spawn/give/time/god/heal/hurt/kill),
      ported from the client debug functions as the spec.
- [ ] `defaultRank` = Operator / Moderator+ (staff only); adjustable via `/cmdset`.
- [ ] Public `Command2` subclass in the scanned assembly → auto‑loaded; verify in
      `/cmdlist`.
- [ ] Every action is server‑authoritative and streams via `SURV_*`.
- [ ] Behaviour gated to survival levels; item/mob tables shared with the client.

## 30. Per‑level block sets — survival blocks scoped to survival maps

**Yes, this is not only possible, it's already how the client works** — you're
matching an existing mechanism, and MCGalaxy's per‑level `BlockDefinitions` line
up with it perfectly. Classic maps stay pristine; survival maps get the Indev set.

### 30.1 It's already per‑level on the client

The Indev blocks live at genuine ids **50–92** (torch 50, fire 51, chest 54,
workbench 58, crops 85–92, …), which **shadow ClassiCube's CPE decoration blocks
(50–65) — but only in Indev mode.** Map loading runs `Game_Reset`, which **wipes
all custom block definitions** (`src/IndevTest.c:2354`); `OnNewMapLoaded` then
re‑applies `IndevBlocks_Define()` **only if the map is Indev** (`:2360`, `:765`).
So a non‑Indev map load restores the default/CPE block table automatically. That
*is* per‑level block awareness — every map switch re‑establishes the right set.

### 30.2 The server mirrors it with per‑level BlockDefinitions

MCGalaxy supports **per‑level** custom blocks (not just global). So:
- **Survival levels:** attach the Indev block collection (ids 50–92, the shared
  table from §15.3/§17.7/§22.3 — draw/textures/collide/sound/light/MinBB‑MaxBB).
  On join, the server sends *this level's* `DefineBlock`/`DefineBlockExt`; the
  client's `Game_Reset` + `IndevBlocks_Define` mirrors it.
- **Classic levels:** no Indev defs — the default/CPE set. Untouched, exactly as
  today.
- **Switching maps** re‑sends the destination level's block defs; the client
  re‑defines on map load. Both ends stay in lock‑step because both key off the
  per‑level survival flag.

**ID‑collision note:** because Indev 50–92 overlaps the CPE decoration blocks
(50–65), the *per‑level* definition is what lets a survival level override those
ids while a Classic level keeps them. This only works cleanly because both the
client (Game_Reset per map) and MCGalaxy (per‑level defs) scope block definitions
to the level — don't define the Indev blocks globally on the server, or you'd
clobber CPE decoration on Classic maps.

Think of it as one named **"Indev block collection"** defined once (the shared
table) and *flagged onto* survival levels — not re‑authored per map.

### 30.3 Generation: fold it into `/os map add` and `/newlvl` (recommended)

Your instinct is right — **integrate it, don't keep a separate survival‑only
flow.** Registering the `indev` generator (§8.1) makes it a **theme** in the shared
generator registry, so it appears in **both** `/os map add` and `/newlvl` for free
(they read the same registry). Then have that generator function do the full
setup, not just terrain:

1. generate the Indev terrain (the `IndevGen` port, §8.1);
2. set the level's **`SurvivalMode = indev`** property (drives the sim, the
   `SURV_HELLO`, the compatibility policy §16, and the block‑set choice);
3. **attach the per‑level Indev block collection** (§30.2);
4. set the **texture‑pack URL** to the Indev pack (§19);
5. persist via the survival save path (§28).

Result: `/os map add indev` or `/newlvl <name> <dims> indev` produces a
ready‑to‑play survival world — one command, correct blocks, correct sim, correct
textures. A separate bespoke command would just duplicate MCGalaxy's whole
map‑management + generator plumbing for no benefit.

(If you'd rather stage it: land the `indev` generator as a theme first with blocks
+ textures but the sim still gated behind the `SurvivalTest` handshake — i.e. the
world generates and looks right even before the full survival server module exists.
That's a clean intermediate milestone, not a reason to keep it separate.)

### 30.4 Pitfalls

- **Don't global‑define Indev blocks on the server** — per‑level only, or Classic
  maps lose their CPE decoration blocks (§30.2).
- **Client map‑switch already clears** Indev defs on a non‑Indev map (`Game_Reset`),
  so a player going survival→classic is handled client‑side; just make sure the
  server sends the *classic* level's (default) defs on arrival.
- **Stock/Classic clients** on a survival map get the Indev defs via
  `BlockDefinitions` (or fallback ids if unsupported, §20.1) — the per‑level set is
  what they receive, same as fork clients.
- **One flag drives all of it:** the per‑level `SurvivalMode` decides block set,
  sim, texture URL, `SURV_HELLO`, and §16 policy — keep it the single source of
  truth so a level can't be "survival for blocks but not for sim".

### 30.5 Checklist

- [ ] Define the Indev block collection once (shared table); attach **per‑level** to
      survival maps only; never global.
- [ ] Register the `indev` generator as a theme → appears in `/os map add` +
      `/newlvl`; the generator sets `SurvivalMode`, block defs, texture URL, saves.
- [ ] On join/switch, server sends the destination level's block defs; client
      mirrors via its map‑load `Game_Reset` + re‑define.
- [ ] Classic maps verified unchanged (default/CPE blocks, no Indev defs).

## 31. Integrating the Indev generator + its world types

Indev world creation is a 4‑axis matrix (our client's `IndevGenScreen`,
`src/Menus.c:1263`): **type** × **shape** × **size** × **theme**. Map it onto one
parameterised MCGalaxy generator, not 144 registrations.

### 31.1 The axes (and what each does)

- **World type / shape** — `indevgen_type` (`src/IndevGen.c:33`): **Inland**(0,
  bordered by an infinite grass plane at ground level), **Island**(1, ringed by
  ocean at sea level), **Floating**(2, sky islands over a void with a negative
  `groundLevel` + invisible bedrock floor), **Flat**(3). Set via `IndevGen_Setup(type, theme)`.
- **Theme** — `indevgen_theme`: **Normal**(0, water/grass, skylight 15),
  **Hell**(1, lava instead of water, dark skylight 7, grass quirks — `:477,:648,:794`),
  **Paradise**(2, higher beaches, 10× flowers — `:451,:1368`), **Woods**(3, +50 tree
  passes, skylight 12 — `:794,:1364`).
- **Shape (aspect)** — Square / Long / Deep: the width×length×height *proportions*.
- **Size** — Small / Normal / Huge: base dimensions.

Shape + size are just **presets that pick the map dimensions**. Since MCGalaxy's
`/newlvl` and `/os map add` already take (or default) explicit dimensions, those two
axes are **subsumed by the dims the command supplies** — the generator only needs
**type + theme** as flavour args.

### 31.2 One parameterised `indev` generator

Register a single generator (§8.1); pass **type** and **theme** through `MapGenArgs`,
take dimensions from the command (or the `/os` default), and the **seed** from
`MapGenArgs`:

```
/newlvl <name> <x> <y> <z> indev [type] [theme]
/os map add indev [type] [theme]         # x/y/z = os default size
    type  = inland | island | floating | flat     (default inland)
    theme = normal | hell | paradise | woods       (default normal)
    e.g.  /newlvl sky 256 64 256 indev floating hell
```

The generator function then:
1. parse `type`/`theme` from `args.Args` (default inland/normal);
2. run the `IndevGen` C# port for the requested **dims + seed + type + theme**
   (`doc/indev-generation.md` + `src/IndevGen.c` are the byte‑exact spec — §8.1);
3. do the **survival‑level setup** (§30.3): `SurvivalMode = indev`, attach the
   per‑level Indev block collection, set the texture URL, and persist (§28).

Put the arg syntax in the generator's `desc` so `/help newlvl`/the gen list shows
it. Optionally add a few **convenience aliases** for popular combos
(`indev_floating`, `indev_hell`, `indev_woods`) that just call the same function
with preset args — nice for `/os map add` where remembering args is friction.

### 31.3 Seed parity (must match SP)

For a given `(seed, type, theme, dims)` the MCGalaxy world must be **byte‑identical**
to the SP client's, so shared/downloaded seeds reproduce. That means porting the
**three RNG streams exactly** (gen `rand`, `world.random` = seed+1 with the burned
`nextInt`, spawn = seed+2 — §8.1 / tasks 26–28) and the noise stack. Reuse the
Java‑oracle parity harness from `doc/indev-generation.md`: fixed seed → dump the
server world → diff against the SP dump; they must be equal.

### 31.4 Dimensions & edge cases

- **Floating/Island** need the env planes + surroundings the SP generator sets
  (`IndevGen_ApplyPostLoad` → `IndevTest_SetSurroundings`); persist them into
  `world.mclevel` (§18.1) and send `SURV_WORLDINFO` (§21/§25) so the client shows the
  right horizon/void.
- **Hell** uses lava as the fluid and a darker skylight — make sure the theme flows
  into the surroundings/`TimeOfDay`/skylight fields, not just the blocks.
- **Huge** maps are large; the generator + the initial 1000‑pass spawn (§8.1) run at
  gen time — keep it off the main tick (MCGalaxy generates async) and stream the
  finished level.
- **`/os map add`** default size: if it's smaller than an Indev world expects, the
  noise still generates (it scales), but document that Indev worlds look best near
  the genuine sizes; allow an explicit size where the command supports it.

### 31.5 Checklist

- [ ] One `indev` generator registered (theme in `/os map add` + `/newlvl`); type +
      theme via args, dims from the command, seed from `MapGenArgs`.
- [ ] Generator runs the `IndevGen` port then the §30.3 survival‑level setup.
- [ ] Seed parity vs SP verified with the fixed‑seed dump/diff harness.
- [ ] Surroundings/theme (floating void, hell lava, skylight) persisted + sent via
      `SURV_WORLDINFO`.
- [ ] Optional convenience aliases for popular type/theme combos.

## References

- CPE spec: https://c4k3.github.io/wiki.vg/Classic_Protocol_Extension.html
- Classic protocol (reference impls): https://github.com/PrismarineJS/minecraft-classic-protocol
- MCGalaxy (server base, this fork): https://github.com/UmbreoClaw/mcgalaxy
- Client transport: `src/Protocol.c` (`CPE_SendPluginMessage` `:891`, `CPE_PluginMessage` `:1551`), `src/Protocol.h:98`, `src/Server.c`, `src/Server.h`.
- Simulation reference (the C "spec" to mirror server‑side): `src/SurvivalTest.c`, `src/IndevTest.c`, `src/IndevGen.c`, `doc/indev-generation.md`, `SURVIVAL_TEST_NOTES.md`.
- Per‑subsystem client symbols: §12. Server‑side design Q&A: §13. Indev creative mode: §14.
- Indev creative/survival game modes (decompiled): `net/minecraft/client/controller/PlayerControllerCreative.java` (creative: `survivalWorld=false`, no HUD, instant break, palette hotbar, mobs still spawn) and `PlayerControllerSP.java` (survival). Serialization manifest: `src/Formats.c` `MCLevel_*`.
