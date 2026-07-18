#ifndef CC_SURVIVALNET_H
#define CC_SURVIVALNET_H
#include "Core.h"
/* Networked survival (Indev / c0.30-s multiplayer) sub-protocol.
   Rides the CPE PluginMessages extension on one channel; every message is a
   1-byte id + fields inside the fixed 64-byte PluginMessage payload.

   This is the CLIENT reference implementation of the handshake/wire contract -
   the MCGalaxy server side is built to match it. Everything here is inert until
   the server negotiates the "SurvivalTest" CPE extension (Server.SupportsSurvival)
   AND we're in multiplayer, so a stock/Classic server is completely unaffected.

   See doc/networking-plan.md (esp. §3 gate, §20 gating, §25 wire format).
   Copyright 2014-2025 ClassiCube | Licensed under BSD-3
*/
struct IGameComponent;
extern struct IGameComponent SurvivalNet_Component;

/* PluginMessages channel reserved for the survival sub-protocol (§25). */
#define SURVNET_CHANNEL 0xB0

/* Message ids (payload byte 0). Server->client = state, client->server = intents.
   Multi-byte fields are big-endian; positions are int16 fixed-point (coord*32). */
enum SurvNetMsg {
	/* --- server -> client (authoritative state) --- */
	SURV_HELLO        = 0x01, /* mode, flags(enhanced/creative/pvp/deathDrops), protoVer */
	SURV_WORLDINFO    = 0x02, /* ground/water level, fluid, theme, floating, env, time */
	SURV_HEALTH       = 0x03, /* health, score */
	SURV_TIME         = 0x04, /* worldTime, eased sky light */
	SURV_MOB_SPAWN    = 0x10, /* mobId, type, pos, yaw/pitch, health, flags */
	SURV_MOB_MOVE     = 0x11, /* mobId, pos, yaw/pitch */
	SURV_MOB_STATE    = 0x12, /* mobId, health, flags(hurt/fuse/onFire/graze/dead) */
	SURV_MOB_DESPAWN  = 0x13, /* mobId, reason */
	SURV_INV_FULL     = 0x20, /* baseSlot, run of {id,count,dmg} */
	SURV_INV_SLOT     = 0x21, /* slot, id, count, dmg */
	SURV_CONT_OPEN    = 0x22, /* kind, rows */
	SURV_CONT_SLOT    = 0x23, /* slot, id, count, dmg */
	SURV_FURN_PROG    = 0x24, /* burn, cook */
	SURV_CURSOR       = 0x25, /* server-owned held stack: id, count, dmg */
	SURV_DROP_SPAWN   = 0x30, /* dropId, itemId, count, pos, vel, rot0 */
	SURV_DROP_PICKUP  = 0x31, /* dropId, pickerEntityId */
	SURV_DROP_REMOVE  = 0x32, /* dropId, reason */
	SURV_BLOCKMETA    = 0x40, /* xyz, meta nibble */
	SURV_PLAYER_EQUIP = 0x50, /* entityId, heldId, armor[4] */

	/* --- client -> server (intents; server validates every one) --- */
	SURV_ATTACK       = 0x80, /* targetKind(0 mob/1 player), targetId */
	SURV_USE_ITEM     = 0x81, /* heldSlot, targetBlock xyz, face */
	SURV_SLOT_CLICK   = 0x82, /* slotIdx(u16 extended), button(0 L/1 R) */
	SURV_RESULT_CLICK = 0x83, /* take craft result */
	SURV_CONT_CLOSE   = 0x84, /* window closed */
	SURV_HELD_SLOT    = 0x85, /* hotbar index */
	SURV_DROP_ITEM    = 0x86, /* slot, wholeStack */
	SURV_RESPAWN      = 0x87  /* menu action */
};

/* Sends a survival intent to the server (a thin wrapper over CPE_SendPluginMessage
   on SURVNET_CHANNEL). No-op unless connected to a survival server. `payload` is
   up to 63 bytes (byte 0 is the message id, set by the caller). */
void SurvivalNet_Send(cc_uint8* payload);

/* --- per-map activation state (the second layer of the two-layer design) --- */
/* Gamemode the server dictated for THIS map via SURV_HELLO: SURVIVAL_GAMEMODE_OFF /
   _C030 / _INDEV. Always OFF in singleplayer, before SURV_HELLO arrives, and on
   servers that never negotiated the SurvivalTest extension. */
int SurvivalNet_ActiveMode(void);
/* SURV_HELLO flags byte for this map (bit0 enhanced, bit1 creative, bit2 pvp,
   bit3 deathDrops). 0 unless a survival map is active. */
int SurvivalNet_ActiveFlags(void);
/* Whether the SERVER owns the survival simulation right now (multiplayer + an
   activated survival map). When true the client renders state and sends intents;
   every local mutation source (damage, mobs, drops, ticks, inventory) is off. */
cc_bool SurvivalNet_ServerDriven(void);

/* --- client -> server intent senders (each is a no-op unless ServerDriven) --- */
void SurvivalNet_SendRespawn(void);                      /* SURV_RESPAWN   [id] */
void SurvivalNet_SendHeldSlot(int slot);                 /* SURV_HELD_SLOT [id][slot] */
void SurvivalNet_SendDropItem(int slot, cc_bool whole);  /* SURV_DROP_ITEM [id][slot][whole] */
void SurvivalNet_SendAttack(int targetKind, int targetId);          /* SURV_ATTACK [id][kind][id:u16] */
void SurvivalNet_SendUseItem(int heldSlot, int x, int y, int z, int face); /* SURV_USE_ITEM */
void SurvivalNet_SendSlotClick(int slot, int button);    /* SURV_SLOT_CLICK [id][slot:u16][button] */
void SurvivalNet_SendResultClick(void);                  /* SURV_RESULT_CLICK [id] */
void SurvivalNet_SendContClose(void);                    /* SURV_CONT_CLOSE [id] */
#endif
