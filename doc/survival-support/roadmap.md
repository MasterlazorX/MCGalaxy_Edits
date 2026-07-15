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
- 🔁 **Namespace hygiene.** `0xB0` is a shared PluginMessages namespace with no
  registry; keep the one-line "do not reuse" note in the server code.

---

## ⬜ Phase 1 — World generation & block metadata

Server generates Indev/c0.30-s worlds and owns block metadata.

- Server-side Indev world generation (themes, floating maps, ground/water) that
  fills the full `SURV_WORLDINFO` field set (promote heights to int16, add the
  remaining `.mclevel` env fields).
- `SURV_BLOCKMETA (0x40)` — push block metadata (facing, crop stage, farmland
  moisture, furnace/chest orientation) as authoritative updates.
- Block-ID space mapping at I/O boundaries: genuine on-disk Indev ids ⇄ client
  runtime ids (metadata nibbles expanded) ⇄ MCGalaxy server ids. Careful
  remapping prevents world corruption. See networking-plan §18.
- `.mclevel` (NBT) round-trip byte-compatibility for saves.

## ⬜ Phase 2 — Health & damage

Server owns health; drives day/night.

- `SURV_HEALTH (0x03)` — authoritative health/armour updates.
- `SURV_TIME (0x04)` — day/night driven by the server (client stops predicting).
- `SURV_RESPAWN (0x87)` — client respawn intent → server validates and repositions.
- Damage sources: fall, drown, fire, lava, mob attacks, PvP (gated on
  `SurvivalPvP`). Death drops gated on `SurvivalDeathDrops`.

## ⬜ Phase 3 — Mob streaming

Mobs stream into a dedicated puppet system (not standard CPE entities), to keep
Indev-specific animations (creeper swell, sheep grazing).

- `SURV_MOB_SPAWN 0x10`, `SURV_MOB_MOVE 0x11`, `SURV_MOB_STATE 0x12`,
  `SURV_MOB_DESPAWN 0x13`.
- Server runs mob AI, spawners, and pathing; client renders + interpolates.
- `SURV_ATTACK (0x80)` — client attack intent → server resolves damage/aggro.

## ⬜ Phase 4 — Inventory & containers

Server owns inventory, containers, crafting, smelting.

- Inventory: `SURV_INV_FULL 0x20`, `SURV_INV_SLOT 0x21`,
  `SURV_PLAYER_EQUIP 0x50`, `SURV_HELD_SLOT (0x85)`.
- Containers/crafting: `SURV_CONT_OPEN 0x22`, `SURV_CONT_SLOT 0x23`,
  `SURV_FURN_PROG 0x24`, `SURV_CURSOR 0x25`, plus intents
  `SURV_SLOT_CLICK 0x82`, `SURV_RESULT_CLICK 0x83`, `SURV_CONT_CLOSE 0x84`,
  `SURV_USE_ITEM 0x81`.
- Requires the chunking decision above (full inventory > 63 bytes).

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
| 0x03 | HEALTH | S→C | 2 |
| 0x04 | TIME | S→C | 2 |
| 0x10–0x13 | MOB_* | S→C | 3 |
| 0x20–0x25 | INV_/CONT_/FURN_/CURSOR | S→C | 4 |
| 0x30–0x32 | DROP_* | S→C | 5 |
| 0x40 | BLOCKMETA | S→C | 1 |
| 0x50 | PLAYER_EQUIP | S→C | 4 |
| 0x80 | ATTACK | C→S | 3 |
| 0x81 | USE_ITEM | C→S | 4 |
| 0x82–0x85 | SLOT/RESULT/CONT/HELD | C→S | 4 |
| 0x86 | DROP_ITEM | C→S | 5 |
| 0x87 | RESPAWN | C→S | 2 |

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
