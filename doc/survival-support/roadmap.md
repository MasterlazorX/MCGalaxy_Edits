# Survival-support roadmap

How the MCGalaxy server side of the ClassiCube survival-test protocol is meant
to grow. Phases follow `doc/networking-plan.md` in the ClassiCube fork. This is
a living plan: the wire contract may still change, so treat message ids and
field lists as the current intent, not a frozen spec.

**Legend:** ✅ done · 🔜 next · ⬜ planned · 🔁 cross-cutting

Guiding rule (server-authoritative model): the server owns health, damage,
inventory, containers, mob AI, physics, and every RNG-/tick-driven event. The
client sends *intents* and renders; it never computes RNG-driven, multi-block,
or multi-entity changes. The server wins all reconciliation.

---

## ✅ Phase 0 — Handshake & capability (this branch)

Foundation, landed. See `session-notes.md`.

- `SurvivalTest` CPE extension advertised + negotiated → per-session `hasSurvival`.
- Per-map `SurvivalMode` gate in level config.
- `SURV_HELLO` (0x01) + `SURV_WORLDINFO` (0x02) sent on map join to capable
  clients on survival maps.
- Inbound `0xB0` traffic received, bounds-checked, capability-gated, logged.
- Stock Classic / CPE clients fully unaffected.

---

## 🔁 Cross-cutting work (do alongside the phases)

- ⬜ **Per-map `survivalActive` mode-flip (client) / authority split (server).**
  Before *changing* any simulated state, the client needs a per-map
  `survivalActive` flag (set on `SURV_HELLO`, cleared on new map) so stale
  survival state cannot leak across a `/goto` into a plain Classic level. On the
  server, gate the single-player simulation **off** in MP for every
  authoritative subsystem (mob AI/spawners, health/damage, furnace tick,
  day/night, random block ticks, explosions, inventory consumption) while
  keeping rendering/sound/GUI running. This is the prerequisite for Phases 2–5
  doing anything visible.
- ⬜ **Chunking for large messages.** A full inventory, a 54-slot container, or a
  burst of mob spawns will not fit in 63 payload bytes. Decide *once*: either a
  base-slot + run-of-N message sent repeatedly, or an explicit
  `[seq][total][chunk]` split. Document max element counts per message so
  neither side ever over-reads the fixed 64-byte buffer.
- 🔁 **Validate every inbound intent.** Independent of `hasSurvival`: bounds-check
  the payload, verify action legality (reach, cooldown, slot validity, container
  access), and reject invalid requests by sending an authoritative correction.
- 🔁 **Version negotiation.** Treat the negotiated `SurvivalTest` CPE ext version
  as the authoritative protocol version; branch wire-format changes on it and
  bump both sides together. `HELLO.protoVer` is reserved for finer-grained
  same-ext-version sub-revisions.
- 🔁 **Fallbacks for non-fork clients.** Keep driving day/night via stock CPE
  `EnvColors`, custom blocks via `BlockDefinitions`/fallback ids, etc., so stock
  Classic and CPE-only clients degrade gracefully (visitors on survival maps).
  ✅ *Landed so far: EnvColors day/night scaling and the ChangeModel mob mirror
  (`SurvivalFallbacks.cs`), custom-block fallback ids (`SurvivalBlocks.cs`).*
- 🔁 **Namespace hygiene.** `0xB0` is a shared PluginMessages namespace with no
  registry; keep the one-line "do not reuse" note in the server code.

---

## 🔶 Phase 1 — World generation & block metadata (block set + generator landed)

Server generates Indev/c0.30-s worlds and owns block metadata.

- ✅ **The Indev block set** (`SurvivalBlocks.cs`): level-scoped BlockDefinitions
  on Indev maps (1:1 port of the client's `IndevBlocks_Define` — ids 50–62 +
  view ids 71–97), applied/stripped live with the survival mode; classic
  fallback ids for pre-BlockDefs clients; the mine/place inventory bridge
  extended to the set (`PickupFor`/`PlaceCost`); `/Survival give` debug
  command; the `ToIndev`/`FromIndev`/`DataMeta` view-id ⇄ (id, meta) bijection
  ported as the authoritative encoding for the steps below. Live-tested.
- ✅ **Server-side Indev world generation** (`IndevGenerator.cs`): port of the
  client's oracle-verified `IndevGen.c` (LevelGenerator.java), registered as
  the `/NewLvl ... indev [theme] [type] [seed]` map theme - themes
  normal/hell/paradise/woods, types inland/island/floating/flat. Generated
  maps come out survival-ready (mode Indev, hazards, block set, theme env,
  spawn house with wall torches). `SURV_WORLDINFO` now carries genuine
  per-map ground/water levels + edge fluid via the level env config.
  Live-tested across all themes/types.
- ⬜ `SURV_BLOCKMETA (0x40)` — push block metadata (facing, crop stage, farmland
  moisture, furnace/chest orientation) as authoritative updates; placement
  rotation (chest/furnace face the placer, wall torches) rides on it.
- ⬜ Block-ID space mapping at I/O boundaries: genuine on-disk Indev ids ⇄ client
  runtime ids (metadata nibbles expanded) ⇄ MCGalaxy server ids. Careful
  remapping prevents world corruption. See networking-plan §18.
- ⬜ `.mclevel` (NBT) round-trip byte-compatibility for saves.

## ✅ Phase 2 — Health & damage

Server owns health and the day/night clock. Core loop (health → damage → death →
respawn) is complete; graduated Indev damage is the one refinement left.

- ✅ `SURV_TIME (0x04)` — day/night driven by the server (scheduler clock, pushed
  to survival players + seeded at handshake). v1 clock is shared across maps.
- ✅ `SURV_HEALTH (0x03)` — authoritative health + score (stored in `Player.Extras`,
  sent at handshake and on change via `SetHealth`).
- ✅ `SURV_RESPAWN (0x87)` — client respawn intent → server resets health, repositions
  to spawn, echoes `SURV_HEALTH`.
- ✅ Damage/death bridge — `OnPlayerDied` maps every MCGalaxy hazard (fall/drown/
  lava/killer/weapons/`/kill`) into `SURV_HEALTH(0)` + respawn.
- ✅ Death-screen dwell — health held at 0 (auto-respawn suppressed, repeat
  hazard deaths cancelled) until the client's `SURV_RESPAWN` or a 30 s safety
  timeout; verified live against the real survival-test client (death camera +
  Game Over screen held, revive round-trip, stray-intent rejection).
- ✅ Hack permissions resolved from the survival config (`SurvivalCreative` ↔
  HELLO creative bit) instead of the MOTD on active survival maps.
- ✅ Genuine graduated player damage (`SurvivalHazards`): 20 TPS server tick
  for fall/drown/lava/fire/void, replacing the binary MCGalaxy bridge (which
  now only serves killer blocks + `/kill`). Live-tested.
- ⬜ *Refinements:* fire-block ignition (needs phase-1 custom blocks), a
  player on-fire overlay flag (reserved bit or `SURV_PLAYER_STATE`), armor
  absorption once armor exists.
- Damage sources: fall, drown, fire, lava, mob attacks, PvP (gated on
  `SurvivalPvP`). Death drops gated on `SurvivalDeathDrops`.

## ✅ Phase 3 — Mob streaming

Mobs stream into a dedicated puppet system (not standard CPE entities), to keep
Indev-specific animations (creeper swell, sheep grazing). Landed as
`SurvivalMobs.cs` + the client's `SurvivalTest_NetMob*` appliers, live-tested.

- ✅ `SURV_MOB_SPAWN 0x10`, `SURV_MOB_MOVE 0x11`, `SURV_MOB_STATE 0x12`,
  `SURV_MOB_DESPAWN 0x13` — see session-notes for the layouts + the sim scope.
- ✅ Server runs mob AI, spawner, physics, damage; client renders + interpolates.
- ✅ `SURV_ATTACK (0x80)` — reach-validated melee → damage/knockback/aggro/shear.
- ⬜ *Refinements:* Indev A* pathfinding, skeleton arrows (needs phase-5 wire),
  block-destroying explosions (opt-in), a real light model, mob persistence.

## 🔶 Phase 4 — Inventory & containers (first slice landed)

Server owns inventory, containers, crafting, smelting.

- ✅ Inventory streaming: `SURV_INV_FULL 0x20` (chunked, ≤12 slots/frame — the
  chunking decision), `SURV_INV_SLOT 0x21`, `SURV_CURSOR 0x25`,
  `SURV_HELD_SLOT (0x85)`; clicks `SURV_SLOT_CLICK 0x82` /
  `SURV_RESULT_CLICK 0x83` / `SURV_CONT_CLOSE 0x84` handled server-side
  (GuiContainer model, echo-only). Mining→pickup / placing→consume block
  bridge. Live-tested. See session-notes for layouts + v1 deviations.
- ✅ Container GUIs over the wire: `SURV_USE_ITEM 0x81` (v1: opens) →
  `SURV_CONT_OPEN 0x22` / `SURV_CONT_SLOT 0x23` / `SURV_FURN_PROG 0x24`;
  server tile entities (chest/large chest/furnace), container clicks via the
  45..98 slot range, force-close on destruction. Live-tested.
- ⬜ Remaining: recipes server-side (RESULT_CLICK is still a no-op), smelting,
  eating/tool USE_ITEM handling, `SURV_PLAYER_EQUIP 0x50`, per-id
  item/max-stack tables — all land with the ITEM definitions.

## ⬜ Phase 5 — Drops & advanced simulation

- Item drops: `SURV_DROP_SPAWN 0x30`, `SURV_DROP_PICKUP 0x31`,
  `SURV_DROP_REMOVE 0x32`, intent `SURV_DROP_ITEM (0x86)`. Despawn timing is
  server-owned.
- Remaining RNG-/tick-driven systems, all server-run and pushed as block
  changes / metadata / time: grass spread + decay, leaf decay, farmland
  moisture, crop + sapling growth, fire spread/burnout, furnace smelting.

---

## Reserved message id → phase map

| id | name | dir | phase |
|---|---|---|---|
| 0x01 | HELLO | S→C | ✅ 0 |
| 0x02 | WORLDINFO | S→C | ✅ 0 (extend in 1) |
| 0x03 | HEALTH | S→C | ✅ 2 |
| 0x04 | TIME | S→C | ✅ 2 |
| 0x10–0x13 | MOB_* | S→C | ✅ 3 |
| 0x20–0x25 | INV_/CONT_/FURN_/CURSOR | S→C | ✅ 4 |
| 0x30–0x32 | DROP_* | S→C | 5 |
| 0x40 | BLOCKMETA | S→C | 1 |
| 0x50 | PLAYER_EQUIP | S→C | 4 |
| 0x80 | ATTACK | C→S | ✅ 3 |
| 0x81 | USE_ITEM | C→S | 🔶 4 (opens ✅, eat/tools with items) |
| 0x82–0x85 | SLOT/RESULT/CONT/HELD | C→S | ✅ 4 |
| 0x86 | DROP_ITEM | C→S | 5 |
| 0x87 | RESPAWN | C→S | ✅ 2 |

---

## How to add a message (the pattern)

1. It already has a reserved id in `enum`-style constants in `SurvivalNet.cs`.
2. **Server → client:** add a `SendXxx(...)` builder next to `SendHello` /
   `SendWorldInfo`; write `[id][fields...]` into a 64-byte payload (respect the
   chunking rule for anything that can exceed 63 bytes) and call
   `SendMessage(p, payload)`. Send it from the appropriate authoritative
   subsystem, gated on `SurvivalNet.Active(p, lvl)`.
3. **Client → server:** add a `case` in `HandlePluginMessage`; bounds-check,
   validate the action, apply it authoritatively, and echo the corrected state.
4. Keep the byte layout in lockstep with the client's `src/SurvivalNet.h`; if it
   changes, bump the `SurvivalTest` CPE ext version on both sides.
5. Update `session-notes.md` (wire format table) and this roadmap.
