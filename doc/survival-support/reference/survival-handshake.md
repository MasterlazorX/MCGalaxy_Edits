# Survival multiplayer handshake — client foundation

*Companion to `doc/networking-plan.md` (the full plan). This doc is the focused
record of the **client-side handshake foundation** that actually landed in the
repo, why it's shaped the way it is, and exactly what the MCGalaxy server session
must build to match it.*

Status: **foundation only.** Capability negotiation + receive/log + send path are
in. The simulation mode-flip and all the state appliers (mobs, inventory, drops,
…) are deferred to the integrated server session. See "Deferred" at the end.

---

## 1. The one idea: capability vs. activation are two separate layers

This is the single most important design decision, and it's what makes the whole
thing safe and correct on a multi-level server like MCGalaxy.

- **Capability (per connection, negotiated once at login):** "This client can
  speak the survival sub-protocol." Carried by a **custom CPE extension**,
  `SurvivalTest v1`, exchanged in the normal CPE `ExtInfo`/`ExtEntry` handshake
  *before any map is sent*. Result lives in `Server.SupportsSurvival`.

- **Activation (per map, sent every time you enter a survival level):** "*This
  specific map* is a survival map — here's its mode and world parameters."
  Carried by a **`SURV_HELLO` + `SURV_WORLDINFO` plugin message** sent right
  around the level load.

Why two layers instead of one? MCGalaxy is a **multi-level** server: a player
hops between a plain Classic build map and an Indev survival map **without
reconnecting**. The CPE handshake happens once and can't re-fire per map, so it
can only answer "*can* this client do survival?" — never "is *this map*
survival?". That second question changes every time you `/goto` a different
level, so it must ride a per-map message. Folding both into the CPE version byte
would be wrong: you'd have no way to turn survival on for one level and off for
the next on the same connection.

Consequence for the client state model:

| State | Scope | Set by | Cleared by |
|---|---|---|---|
| `Server.SupportsSurvival` | whole connection | `ExtEntry("SurvivalTest")` | reconnect |
| *(future)* `survivalActive` | current map | `SURV_HELLO` | `OnNewMap` |

The foundation only implements the first row. The mode-flip work will add the
second — a per-map `survivalActive` flag set on `SURV_HELLO` and reset in the
component's `OnNewMap` hook (currently `NULL`, reserved for exactly this).

---

## 2. Transport: why CPE PluginMessages

The survival messages ride the standard CPE **PluginMessages** extension on one
channel, `SURVNET_CHANNEL = 0xB0`. Each message is `[id:1][fields…]` inside the
fixed 64-byte PluginMessage payload; on the wire that's a 66-byte packet
(`[0x35][channel][payload:64]`).

**Why this transport and not the alternatives:**

- **A brand-new opcode? No.** The Classic protocol is a raw, length-prefixed-by-
  opcode byte stream with no framing. A stock client that receives an opcode it
  doesn't know can't skip it — it mis-reads the following bytes and desyncs the
  entire stream. CPE explicitly forbids sending a client any packet it hasn't
  negotiated. **PluginMessages is the sanctioned escape hatch** for exactly this:
  arbitrary custom payloads over a known, fixed-size opcode (`0x35`).
- **PluginMessages, gated behind our own CPE ext.** We only ever send on `0xB0`
  after the server has confirmed the client negotiated `SurvivalTest`. A stock
  client never negotiates it, so it never receives survival traffic. This is the
  "never send an unnegotiated packet" rule (`doc/networking-plan.md` §20).
- **One channel + a sub-id byte** (vs. one PluginMessages channel per category).
  256 message types on a single channel is plenty and keeps routing to one
  `switch`. `0xB0` sits in the high range to stay clear of anything a plugin
  might casually pick low.

So: **CPE ext for capability, PluginMessages for the data.** This is the
idiomatic ClassiCube approach and it is the right one.

---

## 3. Message flow

```
  TCP connect
     │
     ├─►  Player identification (opcode 0x00)
     │
     ├─►  CPE handshake  (only if both sides set the 0x42 "I speak CPE" byte)
     │      client → ExtInfo + ExtEntry × N   (advertises SurvivalTest v1)
     │      server → ExtInfo + ExtEntry × M   (echoes SurvivalTest v1  ⇒
     │                                          Server.SupportsSurvival = true)
     │
     ├─►  LevelInitialize / Level data / LevelFinalize  (standard Classic packets)
     │
     ├─►  SURV_HELLO      (0xB0 / 0x01)  ── "this map is Indev, mode+flags"
     ├─►  SURV_WORLDINFO  (0xB0 / 0x02)  ── ground/water/theme/floating/…
     │
     └─►  … ongoing SURV_* state (mobs, health, inventory, drops, time) …
            client → SURV_* intents (attack, use, slot-click, …)
```

**Ordering is free.** Classic runs over TCP (ordered + reliable), so if the
server sends `ExtEntry` → level → `SURV_HELLO` → `SURV_WORLDINFO` in that order,
that's the order the client sees them. No sequence numbers, no ack dance. The
server should send `SURV_HELLO` before (or immediately after) the level so the
client can configure the map's mode as it comes up, then `SURV_WORLDINFO` for the
non-Classic world params.

---

## 4. What landed — file by file

| File | Change |
|---|---|
| `src/SurvivalNet.h` | **New.** Wire contract: `SURVNET_CHANNEL 0xB0`, `enum SurvNetMsg` (server→client `0x01–0x50`, client→server `0x80–0x87`), `SurvivalNet_Component`, `SurvivalNet_Send`. |
| `src/SurvivalNet.c` | **New.** `SurvivalNet_Active()` gate, receive dispatch, `SURV_HELLO`/`SURV_WORLDINFO` parse+chat-log stubs, `SurvivalNet_Send`, the `IGameComponent`. |
| `src/Protocol.c` | `survival_Ext = { "SurvivalTest", 1 }`; appended `&survival_Ext` to `cpe_clientExtensions[]`; ExtEntry handler sets `Server.SupportsSurvival = true`. |
| `src/Server.h` | `cc_bool SupportsSurvival;` on the `Server` struct. |
| `src/Game.c` | `#include "SurvivalNet.h"` + `Game_AddComponent(&SurvivalNet_Component);` |

### The gate (why Classic is untouched)

```c
static cc_bool SurvivalNet_Active(void) {
    return !Server.IsSinglePlayer && Server.SupportsSurvival;
}
```

Both the receive handler and `SurvivalNet_Send` early-out unless this holds:

- **Singleplayer** → `IsSinglePlayer` is true → gate fails. The internal server
  never negotiates CPE exts, and we never touch the net path in SP anyway.
- **Stock / plain-CPE server** → never echoes `ExtEntry("SurvivalTest")` →
  `SupportsSurvival` stays false → gate fails. Zero behaviour change; the survival
  code is dead weight that never runs.
- **Our fork server on a survival map** → negotiates the ext, gate opens, we
  receive `SURV_*` and can `SurvivalNet_Send`.

Verified: full `make PLAT=linux` builds + links clean; `nm ClassiCube` shows
`SurvivalNet_Component`, `SurvivalNet_Send`, `SurvivalNet_OnPluginMessage`, and
`survival_Ext` present.

---

## 5. The handshake messages (byte layouts)

Multi-byte fields are **big-endian**; positions (later messages) are int16
fixed-point (`coord × 32`). Byte 0 is always the message id. Payload is the fixed
64-byte PluginMessage frame — unused tail bytes are zero and ignored.

### `SURV_HELLO` (0x01) — server → client
| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 1 | id = 0x01 | |
| 1 | 1 | mode | 0 = off, 1 = c0.30-s, 2 = Indev |
| 2 | 1 | flags | bit0 enhanced, bit1 creative, bit2 pvp, bit3 deathDrops, … |
| 3 | 1 | protoVer | sub-protocol revision (see caveat §6) |

### `SURV_WORLDINFO` (0x02) — server → client
| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 1 | id = 0x02 | |
| 1 | 1 | groundLevel | `.mclevel` metadata |
| 2 | 1 | waterLevel | |
| 3 | 1 | fluid id | still water/lava the surface uses |
| 4 | 1 | theme | Indev theme (normal/hell/paradise/woods/floating) |
| 5 | 1 | flags | bit0 floating, … |
| 6.. | | edge/sides block ids, env colours | the rest of the `.mclevel` env set |

The full canonical field list is `doc/networking-plan.md` §25 / §18 (the
`.mclevel` metadata is the source of truth — keep `SURV_WORLDINFO` a subset of
it so a saved level and a freshly-sent one describe the same world).

The client stubs currently read only the first couple of fields and log them, so
the server session can watch the wire and confirm framing. No state is applied.

---

## 6. Is this the best way? — honest design review

**Verdict: yes, the shape is right.** CPE-ext-for-capability +
PluginMessages-for-data + per-map `SURV_HELLO` activation is the correct,
idiomatic, Classic-safe design, and the two-layer capability/activation split is
a genuine strength for a multi-level server. Nothing here needs re-architecting.

That said, there are four refinements worth folding in as the protocol fills out.
None of them block the foundation.

1. **`protoVer` in `SURV_HELLO` is partly redundant.** CPE already negotiates a
   version for the `SurvivalTest` ext (both sides pick `min(client, server)`), and
   that's available as `survival_Ext.serverVersion`. That is the natural,
   machine-negotiated place to branch wire-format changes. Recommendation: treat
   the **CPE ext version as the authoritative protocol version**, and keep
   `HELLO.protoVer` only for finer-grained, same-ext-version sub-revisions (or
   drop it and reserve the byte). Don't maintain two competing version numbers.

2. **64-byte frame → large messages must chunk.** A full inventory
   (`SURV_INV_FULL`), a 54-slot container, or a burst of mob spawns will not fit
   in 63 payload bytes. The protocol must define these as either (a) a base-slot +
   run-of-N-slots message you send repeatedly, or (b) an explicit
   `[seq][total][chunk]` split. Decide this once, up front, and document max
   element counts per message so neither side ever over-reads the fixed buffer.
   (Bounds-checking every inbound payload is already mandated server-side —
   §20.3 — and the client must do the same: never read past byte 63.)

3. **Channel `0xB0` is a shared namespace.** PluginMessages channels have no
   central registry; another plugin could also pick `0xB0`. In practice this only
   bites if a server negotiates `SurvivalTest` *and* uses `0xB0` for something
   else — which our own fork server won't. Low risk, but worth a one-line note in
   the server code so nobody reuses `0xB0`. (Making the channel negotiable would
   be over-engineering for a first version.)

4. **Activation state isn't wired yet — by design.** The gate today is
   capability-only (`SupportsSurvival`). On a non-survival map the server simply
   never sends `SURV_*`, so logging-only behaves correctly. But the mode-flip
   step **must** add a per-map `survivalActive` flag (set on `SURV_HELLO`, cleared
   in `SurvivalNet_Component.OnNewMap`) before it starts *changing* local sim
   state — otherwise stale survival state could leak across a `/goto` into a plain
   Classic level. The component's `OnNewMap` slot is left `NULL` reserved for this.

---

## 7. What the server (MCGalaxy) must implement to match

1. **Advertise + negotiate `SurvivalTest v1`** in its CPE `ExtInfo`/`ExtEntry`
   exchange (`Network/ClassicProtocol.cs`), and record a per-session
   `hasSurvival` from the client's `ExtEntry` (`doc/networking-plan.md` §20.5).
2. **Send `SURV_HELLO` / `SURV_WORLDINFO`** on `0xB0` after the level load, only
   to sessions where `hasSurvival && level.SurvivalMode`. Match the byte layouts
   in §5 (and the canonical §25).
3. **Never send `SURV_*` to a non-`hasSurvival` client**, and drive day/night,
   custom blocks, etc. for those clients via the stock CPE fallbacks
   (`EnvColors`, block fallback ids) per §20–§22.
4. **Validate every inbound intent** (`0x80–0x87`) regardless of capability —
   a negotiated ext is a capability, not a permission (§20.3).

Build your handshake **against `src/SurvivalNet.h`** — it is the contract. If the
wire format changes, bump the `SurvivalTest` CPE ext version on both sides.

---

## 8. Deferred (next / integrated-server session)

- `SURV_HELLO` **mode-flip**: flip the client into a server-authoritative Indev
  sim in MP (turn off local mob AI/spawner, health, furnace tick, day/night,
  random block ticks, `IndevGen`; force physics off on survival maps) — plus the
  per-map `survivalActive` flag (§6.4). `doc/networking-plan.md` §15.2, §17.4.
- **Server→client appliers** for the reserved ids: mobs `0x10–0x13`, inventory
  `0x20–0x25`, drops `0x30–0x32`, blockmeta `0x40`, equip `0x50`.
- **Client→server intent senders** for `0x80–0x87`, called from the existing
  input handlers instead of mutating local state.

All ids are already reserved in `enum SurvNetMsg`, so this is fill-in work against
a fixed contract, not new protocol design.
