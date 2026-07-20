/*
    Copyright 2015-2024 MCGalaxy

    Dual-licensed under the Educational Community License, Version 2.0 and
    the GNU General Public License, Version 3 (the "Licenses"); you may
    not use this file except in compliance with the Licenses. You may
    obtain a copy of the Licenses at

    https://opensource.org/license/ecl-2-0/
    https://www.gnu.org/licenses/gpl-3.0.html

    Unless required by applicable law or agreed to in writing,
    software distributed under the Licenses are distributed on an "AS IS"
    BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express
    or implied. See the Licenses for the specific language governing
    permissions and limitations under the Licenses.
 */
using System;
using MCGalaxy.Blocks;
using BlockID = System.UInt16;

namespace MCGalaxy.Network
{
    /// <summary> Phase 4 (first slice): the server-authoritative player inventory,
    /// streamed to survival-test clients as SURV_INV_FULL/INV_SLOT/CURSOR and mutated
    /// only by validated intents (SLOT_CLICK/CONT_CLOSE/HELD_SLOT) and the
    /// mine-&gt;pickup / place-&gt;consume block bridge. </summary>
    /// <remarks>
    /// The click model is a 1:1 port of the ClassiCube fork's SurvivalTest_SlotClick /
    /// CursorReturn (GuiContainer lineage) run on server state, per networking-plan §27:
    /// the cursor is server-owned, every mutation echoes authoritative slots, and TCP
    /// ordering removes Beta's transaction dance. No client claim is ever applied.
    ///
    /// V1 scope (deliberate, see doc/survival-support/session-notes.md):
    ///  * Main 36 + craft grid 9 + armor 4 slots stream; containers (45..98) are
    ///    NOT streamed yet - clicks into that range are rejected.
    ///  * No crafting recipes server-side yet: the craft grid holds items fine, but
    ///    SURV_RESULT_CLICK is a no-op (the client computes no local result in MP).
    ///  * Mining adds the broken block directly to the inventory and placing consumes
    ///    it - the drop-entity hop arrives with phase 5.
    ///  * Armor slots accept nothing (no armor items exist yet); taking out works.
    ///  * Death keeps the inventory (drops are phase 5; SurvivalDeathDrops honoured then).
    ///  * Max stacks: 99 in c0.30 mode, 64 in Indev (the per-id table - tools 1, etc. -
    ///    lands with the item definitions in later phase-4 work).
    /// </remarks>
    public static class SurvivalInventory
    {
        // Slot layout - must mirror the client's SurvivalTest.h exactly:
        // 0..35 main (0..8 hotbar), 36..44 craft, 45..98 container, 99..102 armor.
        public const int MAIN_SLOTS  = 36;
        public const int CRAFT_BASE  = 36, CRAFT_SLOTS = 9;
        public const int CONT_BASE   = 45, CONT_MAX    = 54;
        public const int ARMOR_BASE  = 99, ARMOR_SLOTS = 4;
        public const int TOTAL_SLOTS = ARMOR_BASE + ARMOR_SLOTS; // 103

        struct Slot
        {
            public ushort Id;
            public byte   Count;
            public short  Damage;
        }

        class PlayerInv
        {
            public Slot[] Slots = new Slot[TOTAL_SLOTS];
            public Slot   Cursor;
            public int    HeldSlot; // hotbar index from SURV_HELD_SLOT
        }

        const string INV_KEY = "survival.inventory";

        static PlayerInv Get(Player p) {
            object o;
            if (p.Extras.TryGet(INV_KEY, out o)) return (PlayerInv)o;
            PlayerInv inv = new PlayerInv();
            p.Extras[INV_KEY] = inv;
            return inv;
        }

        static int MaxStack(Player p, ushort id) {
            Level lvl = p.level;
            bool indev = lvl != null && lvl.Config.SurvivalMode == SurvivalMode.Indev;
            return indev ? 64 : 99;
        }


        // ==================== streaming ====================

        /// <summary> Streams the full inventory + cursor to a player (their per-map
        /// handshake). Called from SurvivalNet.SendHandshake. </summary>
        public static void SendAll(Player p) {
            PlayerInv inv = Get(p);
            // main + craft (0..44) then armor (99..102), 12 slots per frame
            SendRange(p, inv, 0, MAIN_SLOTS + CRAFT_SLOTS);
            SendRange(p, inv, ARMOR_BASE, ARMOR_SLOTS);
            SendCursor(p, inv);
        }

        static void SendRange(Player p, PlayerInv inv, int base_, int count) {
            for (int off = 0; off < count; off += 12)
            {
                int run = Math.Min(12, count - off);
                byte[] msg = new byte[Packet.PluginMessageDataLength];
                msg[0] = SurvivalNet.INV_FULL;
                msg[1] = (byte)(base_ + off);
                msg[2] = (byte)run;
                for (int i = 0; i < run; i++)
                {
                    Slot s = inv.Slots[base_ + off + i];
                    int at = 3 + i * 5;
                    msg[at]     = (byte)(s.Id >> 8); msg[at + 1] = (byte)s.Id;
                    msg[at + 2] = s.Count;
                    msg[at + 3] = (byte)(s.Damage >> 8); msg[at + 4] = (byte)s.Damage;
                }
                SurvivalNet.SendMessage(p, msg);
            }
        }

        static void SendSlot(Player p, PlayerInv inv, int idx) {
            Slot s = inv.Slots[idx];
            byte[] msg = new byte[Packet.PluginMessageDataLength];
            msg[0] = SurvivalNet.INV_SLOT;
            msg[1] = (byte)idx;
            msg[2] = (byte)(s.Id >> 8); msg[3] = (byte)s.Id;
            msg[4] = s.Count;
            msg[5] = (byte)(s.Damage >> 8); msg[6] = (byte)s.Damage;
            SurvivalNet.SendMessage(p, msg);
        }

        static void SendCursor(Player p, PlayerInv inv) {
            byte[] msg = new byte[Packet.PluginMessageDataLength];
            msg[0] = SurvivalNet.CURSOR;
            msg[1] = (byte)(inv.Cursor.Id >> 8); msg[2] = (byte)inv.Cursor.Id;
            msg[3] = inv.Cursor.Count;
            msg[4] = (byte)(inv.Cursor.Damage >> 8); msg[5] = (byte)inv.Cursor.Damage;
            SurvivalNet.SendMessage(p, msg);
        }


        // ==================== intents ====================

        public static void HandleHeldSlot(Player p, int slot) {
            if (slot < 0 || slot > 8) return;
            Get(p).HeldSlot = slot;
        }

        /// <summary> SURV_SLOT_CLICK: the GuiContainer click model, run on the server's
        /// authoritative slots + cursor, echoing the changed slot and the cursor. </summary>
        public static void HandleSlotClick(Player p, int idx, int button) {
            if (!SurvivalNet.Active(p, p.level) || SurvivalNet.IsDead(p)) return;
            // creative maps: the inventory is client-local (the palette) - a stray
            // intent must not mutate the server's (unused) slots
            if (p.level != null && p.level.Config.SurvivalCreative) return;
            // containers aren't streamed yet; reject that range (and anything oob)
            if (idx < 0 || idx >= TOTAL_SLOTS) return;
            if (idx >= CONT_BASE && idx < CONT_BASE + CONT_MAX) {
                Logger.Log(LogType.Debug, "survival: rejected container click from {0} (not streamed yet)", p.name);
                return;
            }
            PlayerInv inv = Get(p);
            bool right = button != 0;

            // SlotArmor.isItemValid: nothing is placeable into armor slots yet
            // (no armor items exist in MP v1); taking out is always allowed.
            if (idx >= ARMOR_BASE && inv.Cursor.Count > 0) return;

            Slot slot = inv.Slots[idx];
            Slot cur  = inv.Cursor;

            if (cur.Count == 0) {
                // pick up: all, or ceil(half) on right-click
                if (slot.Count == 0) return;
                int moved = right ? (slot.Count + 1) / 2 : slot.Count;
                cur = slot;
                cur.Count   = (byte)moved;
                slot.Count -= (byte)moved;
                if (slot.Count == 0) { slot.Id = 0; slot.Damage = 0; }
            } else if (slot.Count > 0 && slot.Id == cur.Id) {
                // merge into the slot, respecting the id's max stack
                int space = MaxStack(p, slot.Id) - slot.Count;
                if (space <= 0) return;
                int moved = right ? 1 : cur.Count;
                if (moved > space) moved = space;
                slot.Count += (byte)moved;
                cur.Count  -= (byte)moved;
                if (cur.Count == 0) { cur.Id = 0; cur.Damage = 0; }
            } else if (slot.Count == 0 && right) {
                // right-click into an empty slot: place exactly one
                slot.Id     = cur.Id;
                slot.Damage = cur.Damage;
                slot.Count  = 1;
                if (--cur.Count == 0) { cur.Id = 0; cur.Damage = 0; }
            } else {
                // different contents (or left-click into empty): swap
                Slot tmp = slot; slot = cur; cur = tmp;
            }

            inv.Slots[idx] = slot;
            inv.Cursor     = cur;
            SendSlot(p, inv, idx);
            SendCursor(p, inv);
        }

        /// <summary> SURV_RESULT_CLICK: no server-side recipes yet - the craft result
        /// is always empty in MP v1, so taking it is a validated no-op. </summary>
        public static void HandleResultClick(Player p) {
            Logger.Log(LogType.Debug, "survival: result click from {0} (recipes not implemented yet)", p.name);
        }

        /// <summary> SURV_CONT_CLOSE: the window closed - return the cursor and the
        /// craft grid to the inventory (never lose either), then resync. </summary>
        public static void HandleContClose(Player p) {
            if (!SurvivalNet.Active(p, p.level)) return;
            if (p.level != null && p.level.Config.SurvivalCreative) return; // client-local palette
            PlayerInv inv = Get(p);

            while (inv.Cursor.Count > 0 && AddOne(p, inv, inv.Cursor.Id, inv.Cursor.Damage)) inv.Cursor.Count--;
            if (inv.Cursor.Count == 0) { inv.Cursor.Id = 0; inv.Cursor.Damage = 0; }

            for (int i = CRAFT_BASE; i < CRAFT_BASE + CRAFT_SLOTS; i++)
            {
                while (inv.Slots[i].Count > 0 && AddOne(p, inv, inv.Slots[i].Id, inv.Slots[i].Damage))
                    inv.Slots[i].Count--;
                if (inv.Slots[i].Count == 0) { inv.Slots[i].Id = 0; inv.Slots[i].Damage = 0; }
            }
            // several slots may have changed - a full resync is simplest and small
            SendAll(p);
        }


        /// <summary> Debug: puts `count` of a raw/view block id into a player's
        /// server-side inventory (/Survival give). Returns how many actually fit
        /// (0 = full), or -1 if the player isn't survival-active. </summary>
        public static int Give(Player p, ushort raw, int count) {
            if (!SurvivalNet.Active(p, p.level)) return -1;
            PlayerInv inv = Get(p);
            int given = 0;
            while (given < count && AddOne(p, inv, raw, 0)) given++;
            if (given > 0) SendAll(p); // several slots may change - full resync
            return given;
        }

        /// <summary> Debug: prints a player's non-empty server-side slots + cursor
        /// to the viewer (/Survival inv). </summary>
        public static void DebugDump(Player viewer, Player target) {
            PlayerInv inv = Get(target);
            int shown = 0;
            viewer.Message("Server inventory of {0}&S (held slot &b{1}&S):", target.ColoredName, inv.HeldSlot);
            for (int i = 0; i < TOTAL_SLOTS; i++)
            {
                if (inv.Slots[i].Count == 0) continue;
                string kind = i < MAIN_SLOTS ? (i < 9 ? "hotbar" : "main")
                            : i < CONT_BASE  ? "craft"
                            : i < ARMOR_BASE ? "container"
                            : "armor";
                viewer.Message("  slot &b{0}&S ({1}): id &b{2}&S x&b{3}&S dmg &b{4}",
                               i, kind, inv.Slots[i].Id, inv.Slots[i].Count, inv.Slots[i].Damage);
                shown++;
            }
            if (shown == 0) viewer.Message("  (all slots empty)");
            if (inv.Cursor.Count > 0) {
                viewer.Message("  cursor: id &b{0}&S x&b{1}", inv.Cursor.Id, inv.Cursor.Count);
            }
        }


        // ==================== add / consume ====================

        // InventoryPlayer.storePartialItemStack order: merge into an existing stack
        // first (slots scan 0..35, so the hotbar wins), then the first empty slot.
        static bool AddOne(Player p, PlayerInv inv, ushort id, short damage) {
            int max = MaxStack(p, id);
            for (int i = 0; i < MAIN_SLOTS; i++)
            {
                if (inv.Slots[i].Count > 0 && inv.Slots[i].Id == id && inv.Slots[i].Count < max) {
                    inv.Slots[i].Count++;
                    return true;
                }
            }
            for (int i = 0; i < MAIN_SLOTS; i++)
            {
                if (inv.Slots[i].Count == 0) {
                    inv.Slots[i].Id     = id;
                    inv.Slots[i].Count  = 1;
                    inv.Slots[i].Damage = damage;
                    return true;
                }
            }
            return false; // inventory full
        }

        // finds the slot AddOne would have changed, for a minimal echo
        static int FindStack(PlayerInv inv, ushort id) {
            for (int i = 0; i < MAIN_SLOTS; i++)
            {
                if (inv.Slots[i].Count > 0 && inv.Slots[i].Id == id) return i;
            }
            return -1;
        }


        // ==================== the block bridge (mine -> pickup, place -> consume) ====================

        /// <summary> Registered on OnBlockChangingEvent: survival players' manual block
        /// edits feed the inventory. Mining adds the broken block (v1: directly, the
        /// drop-entity hop is phase 5); placing consumes one - or is reverted if the
        /// player doesn't have the block. Creative survival maps build freely. </summary>
        public static void OnBlockChanging(Player p, ushort x, ushort y, ushort z, BlockID block, bool placing, ref bool cancel) {
            Level lvl = p.level;
            if (lvl == null || lvl.Config.SurvivalMode == SurvivalMode.Off) return;
            if (lvl.Config.SurvivalCreative) return; // creative: free build for everyone, no pickup/consume

            // networking-plan §16's hard invariant: a client that never negotiated
            // SurvivalTest bypasses tools/consumption/drops, so letting it modify a
            // survival world would corrupt the authoritative state. Default policy
            // is look-but-don't-touch; the map owner may opt into Allow. Referees
            // keep their staff escape hatch (draw commands are unaffected anyway).
            if (p.Session == null || !p.Session.hasSurvival) {
                if (lvl.Config.SurvivalVisitors == SurvivalVisitorPolicy.Allow) return;
                if (p.Game.Referee) return;
                cancel = true;
                p.RevertBlock(x, y, z);
                WarnVisitor(p);
                return;
            }
            if (SurvivalNet.IsDead(p)) { cancel = true; p.RevertBlock(x, y, z); return; }

            PlayerInv inv = Get(p);

            bool indev = lvl.Config.SurvivalMode == SurvivalMode.Indev;

            if (placing) {
                ushort raw  = p.Session.ConvertBlock(block);
                ushort cost = raw <= Block.CLASSIC_MAX_BLOCK ? raw
                            : indev ? SurvivalBlocks.PlaceCost(raw) : (ushort)0;
                if (cost == 0) return; // not survival content - pass through unconsumed
                int idx = ConsumeSlot(p, inv, cost);
                if (idx < 0) {
                    cancel = true;
                    p.RevertBlock(x, y, z);
                    // resync the hotbar so a stale client view corrects itself
                    SendAll(p);
                    return;
                }
                SendSlot(p, inv, idx);
            } else {
                BlockID old = lvl.GetBlock(x, y, z);
                ushort raw  = p.Session.ConvertBlock(Block.Convert(old));
                if (raw == Block.Air) return;
                ushort pick = raw <= Block.CLASSIC_MAX_BLOCK ? raw
                            : indev ? SurvivalBlocks.PickupFor(raw) : (ushort)0;
                if (pick == 0) return; // yields nothing (crops/fire/leftover CPE ids)
                // liquids never yield a pickup (breaking still-water via commands etc.)
                byte collide = lvl.CollideType(old);
                if (collide == CollideType.SwimThrough || collide == CollideType.LiquidWater ||
                    collide == CollideType.LiquidLava) return;

                if (AddOne(p, inv, pick, 0)) {
                    int idx = FindStack(inv, pick);
                    if (idx >= 0) SendSlot(p, inv, idx);
                } // full inventory: the block is simply not picked up (phase 5 drops fix this)
            }
        }

        // Rate-limited so click-spam doesn't flood the visitor's chat
        static void WarnVisitor(Player p) {
            const string WARN_KEY = "survival.visitorWarned";
            DateTime now = DateTime.UtcNow;
            object o;
            if (p.Extras.TryGet(WARN_KEY, out o) && now < (DateTime)o) return;
            p.Extras[WARN_KEY] = now.AddSeconds(10);
            p.Message("&WThis map runs the survival simulation - only survival-test clients can modify it.");
        }

        /// <summary> The Deny visitor policy: non-survival clients may not even join.
        /// Registered on OnJoiningLevelEvent. </summary>
        public static void OnJoiningLevel(Player p, Level lvl, ref bool canJoin) {
            if (lvl == null || lvl.Config.SurvivalMode == SurvivalMode.Off) return;
            if (lvl.Config.SurvivalVisitors != SurvivalVisitorPolicy.Deny) return;
            if (p.Session != null && p.Session.hasSurvival) return;
            if (p.Game.Referee) return;
            canJoin = false;
            p.Message("&W{0} &Wruns the survival simulation - a survival-test client is required to join.", lvl.ColoredName);
        }

        // consume one of `raw`, preferring the held hotbar slot (SURV_HELD_SLOT),
        // then any slot holding it. Returns the changed slot index, or -1.
        static int ConsumeSlot(Player p, PlayerInv inv, ushort raw) {
            int held = inv.HeldSlot;
            if (held >= 0 && held < 9 && inv.Slots[held].Count > 0 && inv.Slots[held].Id == raw) {
                if (--inv.Slots[held].Count == 0) { inv.Slots[held].Id = 0; inv.Slots[held].Damage = 0; }
                return held;
            }
            for (int i = 0; i < MAIN_SLOTS; i++)
            {
                if (inv.Slots[i].Count > 0 && inv.Slots[i].Id == raw) {
                    if (--inv.Slots[i].Count == 0) { inv.Slots[i].Id = 0; inv.Slots[i].Damage = 0; }
                    return i;
                }
            }
            return -1;
        }
    }
}
