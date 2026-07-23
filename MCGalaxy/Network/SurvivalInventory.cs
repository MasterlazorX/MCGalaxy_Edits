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
using System.Collections.Generic;
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
    /// Current scope (see doc/survival-support/session-notes.md):
    ///  * Main 36 + craft 9 + armor 4 slots stream; the container range (45..98)
    ///    resolves through the player's OPEN container view (chest/large/furnace).
    ///  * Crafting is real: SURV_RESULT_CLICK matches the SurvivalItems recipe
    ///    table (identical to the client's, which renders the preview locally).
    ///  * Mining yields the genuine Indev drop table (SurvivalItems.MiningDrops,
    ///    harvest-gated) straight into the inventory; the drop-entity hop is phase 5.
    ///  * Furnaces smelt on the 20 TPS survival tick (TickFurnaces).
    ///  * Armor slots accept their matching piece (SlotArmor.isItemValid) and
    ///    broadcast the worn change; damage-absorption is future work.
    ///  * Death keeps the inventory (drops are phase 5; SurvivalDeathDrops honoured then).
    ///  * Max stacks: per-id in Indev (blocks 99 / items 64 / tools 1), flat 99 in c0.30.
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
        static readonly Random dropRng = new Random();

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
            // Indev: the per-id table (blocks 99, items 64, tools/food/armor 1),
            // identical to the client's. c0.30 has no items - flat 99.
            return indev ? SurvivalItems.MaxStack(id) : 99;
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
            // SendRange doesn't route through SendSlot, so mirror the main slots
            // into any open /Inventory view of this player - one scan, all cells.
            EchoAllPlayerViews(p);
            // any full resync may have changed the visible held/armor (pickup, give,
            // craft, death); the dedup makes this a no-op unless it actually did.
            BroadcastEquip(p);
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
            // mirror the change into any open /Inventory view of this player
            // (main + hotbar slots, and the 4 armor slots the panel also shows)
            if (idx < MAIN_SLOTS || (idx >= ARMOR_BASE && idx < ARMOR_BASE + ARMOR_SLOTS))
                EchoPlayerViews(p, idx);
        }

        static void SendCursor(Player p, PlayerInv inv) {
            byte[] msg = new byte[Packet.PluginMessageDataLength];
            msg[0] = SurvivalNet.CURSOR;
            msg[1] = (byte)(inv.Cursor.Id >> 8); msg[2] = (byte)inv.Cursor.Id;
            msg[3] = inv.Cursor.Count;
            msg[4] = (byte)(inv.Cursor.Damage >> 8); msg[5] = (byte)inv.Cursor.Damage;
            SurvivalNet.SendMessage(p, msg);
        }


        // ==================== containers (rest of the phase-4 GUI) ====================
        // Server-side tile entities: chests (27 slots) and furnaces (3 slots),
        // created lazily on first open, keyed by level + position, session-scoped
        // like the rest of the survival state (no persistence yet). A chest
        // touching another chest opens as the genuine InventoryLargeChest: the
        // -X/-Z neighbour is the UPPER 27 slots, the clicked chest the lower.
        // V1 deviations: destroying a container discards its contents (chest
        // scatter needs phase-5 drops); container GUIs are not opened on
        // creative maps (the client inventory is a local palette there).

        public const byte CONT_NONE = 0, CONT_CHEST = 1, CONT_FURNACE = 2,
                          CONT_LARGE = 3, CONT_WORKBENCH = 4, CONT_PLAYERINV = 5;

        // A CONT_PLAYERINV view (/Inventory) proxies another player's inventory as
        // a container: 40 cells laid out like the genuine pocket inventory - cells
        // 0..26 are the target's main storage (their slots 9..35), 27..35 the hotbar
        // (0..8), and 36..39 the 4 armor slots (ARMOR_BASE..+3, boots..helmet). The
        // v3 client renders this as a dedicated inventory panel; a v2 client (which
        // only knows chest) is sent a 36-cell chest fallback instead.
        const int PLAYERINV_SLOTS = 40;
        // container cell -> target inventory slot
        static int PlayerInvSlot(int ci) {
            if (ci < 27) return ci + 9;              // cells 0..26  -> main storage 9..35
            if (ci < MAIN_SLOTS) return ci - 27;     // cells 27..35 -> hotbar 0..8
            return ARMOR_BASE + (ci - MAIN_SLOTS);   // cells 36..39 -> armor 99..102
        }
        // and back (target inventory slot -> container cell), for echoing the
        // target's own edits into every open view. -1 for slots not shown (craft).
        static int PlayerInvCell(int pslot) {
            if (pslot >= 9 && pslot < MAIN_SLOTS) return pslot - 9;   // storage
            if (pslot >= 0 && pslot < 9)          return pslot + 27;  // hotbar
            if (pslot >= ARMOR_BASE && pslot < ARMOR_BASE + ARMOR_SLOTS)
                return MAIN_SLOTS + (pslot - ARMOR_BASE);            // armor
            return -1;
        }

        class Container
        {
            public byte  Kind; // CONT_CHEST or CONT_FURNACE (a large chest is two of these)
            public Slot[] Slots;
            public int X, Y, Z;
            // furnace state (TileEntityFurnace): slots 0 input, 1 fuel, 2 output
            public int BurnTime, CookTime, CurrentBurn;
        }
        // Kind CONT_CHEST/FURNACE/LARGE/WORKBENCH use Upper/Lower (tile entities);
        // CONT_PLAYERINV uses Target (the viewed player) + CanEdit (Admin can move
        // items, Operator is view-only). Lvl is the VIEWER's level at open time -
        // the per-click guard drops the ref if the viewer leaves it.
        class OpenRef { public byte Kind; public Container Upper, Lower; public Level Lvl;
                        public Player Target; public bool CanEdit; }

        const string OPEN_KEY = "survival.container";
        static readonly object contLock = new object();
        static readonly Dictionary<Level, Dictionary<long, Container>> contRegistry =
            new Dictionary<Level, Dictionary<long, Container>>();

        static long PackPos(int x, int y, int z) {
            return ((long)x << 40) | ((long)y << 20) | (uint)z;
        }

        static bool IsChestView(ushort raw) {
            return raw == SurvivalBlocks.CHEST ||
                   (raw >= SurvivalBlocks.CHEST_V0 && raw <= SurvivalBlocks.CHEST_V0 + 3);
        }
        static bool IsFurnaceView(ushort raw) {
            return raw == SurvivalBlocks.FURNACE || raw == SurvivalBlocks.FURNACE_LIT ||
                   (raw >= SurvivalBlocks.FURN_V0 && raw <= SurvivalBlocks.FURNL_V0 + 3);
        }

        static ushort RawAt(Level lvl, int x, int y, int z) {
            if (x < 0 || y < 0 || z < 0 || x >= lvl.Width || y >= lvl.Height || z >= lvl.Length) return 0;
            return Block.ToRaw(Block.Convert(lvl.GetBlock((ushort)x, (ushort)y, (ushort)z)));
        }

        static Container GetTE(Level lvl, int x, int y, int z, byte kind) {
            lock (contLock) {
                Dictionary<long, Container> map;
                if (!contRegistry.TryGetValue(lvl, out map)) {
                    map = new Dictionary<long, Container>();
                    contRegistry[lvl] = map;
                }
                long key = PackPos(x, y, z);
                Container te;
                if (!map.TryGetValue(key, out te)) {
                    te = new Container();
                    te.Kind  = kind;
                    te.Slots = new Slot[kind == CONT_FURNACE ? 3 : 27];
                    te.X = x; te.Y = y; te.Z = z;
                    map[key] = te;
                }
                return te;
            }
        }

        static OpenRef GetOpen(Player p) {
            object o;
            return p.Extras.TryGet(OPEN_KEY, out o) ? (OpenRef)o : null;
        }

        static int OpenSlotCount(OpenRef open) {
            if (open == null) return 0;
            if (open.Kind == CONT_PLAYERINV) return PLAYERINV_SLOTS;
            if (open.Upper == null) return 0;
            int n = open.Upper.Slots.Length;
            if (open.Lower != null) n += open.Lower.Slots.Length;
            return n;
        }
        // container-relative slot resolution (large chest: upper 0..26, lower 27..53;
        // a player-inventory view proxies the target player's own slots).
        static Slot GetContSlot(OpenRef open, int ci) {
            if (open.Kind == CONT_PLAYERINV)
                return Get(open.Target).Slots[PlayerInvSlot(ci)];
            if (open.Lower != null && ci >= open.Upper.Slots.Length)
                return open.Lower.Slots[ci - open.Upper.Slots.Length];
            return open.Upper.Slots[ci];
        }
        static void SetContSlot(OpenRef open, int ci, Slot s) {
            if (open.Kind == CONT_PLAYERINV) {
                Get(open.Target).Slots[PlayerInvSlot(ci)] = s;
                return;
            }
            if (open.Lower != null && ci >= open.Upper.Slots.Length)
                open.Lower.Slots[ci - open.Upper.Slots.Length] = s;
            else
                open.Upper.Slots[ci] = s;
        }

        // BlockChest.blockActivated: a NORMAL CUBE directly above a chest half
        // keeps the lid shut. The client uses isBlockNormalCube (Blocks.FullOpaque),
        // which excludes glass/leaves/slabs/sprites - so those do NOT block the
        // lid. Reuse the same NormalCube predicate the placement code uses, not a
        // bare IsSolid (which wrongly treated glass/slabs as blocking).
        static bool SolidAbove(Level lvl, int x, int y, int z) {
            if (y + 1 >= lvl.Height) return false;
            return NormalCube(lvl, x, y + 1, z);
        }

        /// <summary> SURV_USE_ITEM: right-click use. With a valid target block it
        /// opens container GUIs (workbench/chest/large chest/furnace) or applies an
        /// item-on-block use (hoe tilling, seed planting); a targetless intent
        /// (sentinel coords, sent for right-click-to-eat) eats a held food. Flint
        /// &amp; steel / fire lands with the phase-5 fire tick system. </summary>
        public static void HandleUseItem(Player p, int held, int x, int y, int z, int face) {
            Level lvl = p.level;
            if (lvl == null || lvl.Config.SurvivalMode != SurvivalMode.Indev) return;
            if (!SurvivalNet.Active(p, lvl) || SurvivalNet.IsDead(p)) return;
            if (lvl.Config.SurvivalCreative) return; // v1: no container/item-use sync in creative

            if (held < 0 || held > 8) held = 0;
            PlayerInv inv = Get(p);
            ushort heldId = inv.Slots[held].Count > 0 ? inv.Slots[held].Id : (ushort)0;

            // A targetless intent (x==-1 sentinel) is a right-click-to-eat; a
            // targeted one goes through reach + the block/item-use dispatch.
            bool hasTarget = x >= 0 && y >= 0 && z >= 0 &&
                             x < lvl.Width && y < lvl.Height && z < lvl.Length;
            if (hasTarget) {
                // reach: same envelope as melee (Player.getEntitiesWithinAABB reach)
                double dx = p.Pos.X / 32.0 - (x + 0.5), dy = p.Pos.Y / 32.0 - (y + 0.5),
                       dz = p.Pos.Z / 32.0 - (z + 0.5);
                if (dx * dx + dy * dy + dz * dz > 6.0 * 6.0) hasTarget = false;
            }

            if (hasTarget) {
                ushort raw = RawAt(lvl, x, y, z);

                // blockActivated (containers) takes priority over item onItemUse
                if (raw == SurvivalBlocks.WORKBENCH) {
                    // no container slots - the client opens its 3x3 grid over the
                    // streamed craft slots 36..44. The open ref records the 3x3 dim
                    // for RESULT_CLICK's recipe matching.
                    OpenRef wb = new OpenRef();
                    wb.Kind = CONT_WORKBENCH; wb.Lvl = lvl;
                    p.Extras[OPEN_KEY] = wb;
                    SurvivalNet.SendContOpen(p, CONT_WORKBENCH, 0);
                    return;
                }

                if (IsChestView(raw)) {
                    if (SolidAbove(lvl, x, y, z)) return; // lid blocked - click still consumed
                    // genuine neighbour scan order: -X, +X, -Z, +Z; at most one matches
                    int nx = x, nz = z; bool neighbourUpper = false, hasNeighbour = false;
                    if      (IsChestView(RawAt(lvl, x - 1, y, z))) { nx = x - 1; neighbourUpper = true;  hasNeighbour = true; }
                    else if (IsChestView(RawAt(lvl, x + 1, y, z))) { nx = x + 1; neighbourUpper = false; hasNeighbour = true; }
                    else if (IsChestView(RawAt(lvl, x, y, z - 1))) { nz = z - 1; neighbourUpper = true;  hasNeighbour = true; }
                    else if (IsChestView(RawAt(lvl, x, y, z + 1))) { nz = z + 1; neighbourUpper = false; hasNeighbour = true; }
                    if (hasNeighbour && SolidAbove(lvl, nx, y, nz)) return; // other half blocked

                    OpenRef open = new OpenRef();
                    open.Kind = CONT_CHEST; open.Lvl = lvl;
                    Container clicked = GetTE(lvl, x, y, z, CONT_CHEST);
                    if (!hasNeighbour) {
                        open.Upper = clicked;
                    } else {
                        Container other = GetTE(lvl, nx, y, nz, CONT_CHEST);
                        open.Upper = neighbourUpper ? other : clicked;
                        open.Lower = neighbourUpper ? clicked : other;
                    }
                    p.Extras[OPEN_KEY] = open;
                    SurvivalNet.SendContOpen(p, hasNeighbour ? CONT_LARGE : CONT_CHEST,
                                             (byte)OpenSlotCount(open));
                    StreamContainer(p, open);
                    return;
                }

                if (IsFurnaceView(raw)) {
                    OpenRef open = new OpenRef();
                    open.Kind  = CONT_FURNACE; open.Lvl = lvl;
                    open.Upper = GetTE(lvl, x, y, z, CONT_FURNACE);
                    p.Extras[OPEN_KEY] = open;
                    SurvivalNet.SendContOpen(p, CONT_FURNACE, 3);
                    StreamContainer(p, open);
                    SurvivalNet.SendFurnProg(p, FurnBurnScaled(open.Upper), FurnCookScaled(open.Upper));
                    return;
                }

                // Item.onItemUse: hoe tilling, seed planting (flint&steel deferred)
                if (UseHoe(p, lvl, inv, held, heldId, x, y, z)) return;
                if (UseSeeds(p, lvl, inv, held, heldId, x, y, z)) return;
            }

            // TryEat: a held food is eaten with or without a target block
            EatFood(p, inv, held, heldId);
        }

        // ItemHoe.onItemUse: grass (with no solid block above) or dirt becomes
        // farmland; the hoe wears 1 durability, and tilling grass has a 1/8 chance
        // to pop a seed (v1: straight to inventory - the drop entity is phase 5).
        static bool UseHoe(Player p, Level lvl, PlayerInv inv, int held, ushort heldId, int x, int y, int z) {
            if (!SurvivalItems.IsHoe(heldId)) return false;
            ushort target = RawAt(lvl, x, y, z);
            bool solidAbove = y + 1 < lvl.Height &&
                CollideType.IsSolid(lvl.CollideType(lvl.GetBlock((ushort)x, (ushort)(y + 1), (ushort)z)));
            if ((target != Block.Grass || solidAbove) && target != Block.Dirt) return false;

            lvl.UpdateBlock(Player.Console, (ushort)x, (ushort)y, (ushort)z, Block.FromRaw(SurvivalBlocks.FARMLAND));
            DamageHeldTool(p, inv, held, 1);
            if (target == Block.Grass) {
                int roll; lock (dropRng) roll = dropRng.Next(8);
                if (roll == 0 && AddOne(p, inv, SurvivalItems.SEEDS, 0)) SendAll(p);
            }
            return true;
        }

        // ItemSeeds.onItemUse: seeds planted on farmland (with air above) become a
        // stage-0 crop in the cell above; one seed is consumed.
        static bool UseSeeds(Player p, Level lvl, PlayerInv inv, int held, ushort heldId, int x, int y, int z) {
            if (heldId != SurvivalItems.SEEDS) return false;
            ushort target = RawAt(lvl, x, y, z);
            bool farmland = target == SurvivalBlocks.FARMLAND || target == SurvivalBlocks.FARMLAND_WET;
            ushort above  = y + 1 < lvl.Height ? RawAt(lvl, x, y + 1, z) : Block.Air;
            if (!farmland || above != Block.Air) return false;

            lvl.UpdateBlock(Player.Console, (ushort)x, (ushort)(y + 1), (ushort)z,
                            Block.FromRaw(SurvivalBlocks.CROPS_0));
            ConsumeHeld(inv, held, 1);
            SendSlot(p, inv, held);
            return true;
        }

        // ItemFood/ItemSoup.onItemRightClick: heal the food's value and consume one;
        // an eaten soup leaves its empty bowl behind (soups don't stack).
        static void EatFood(Player p, PlayerInv inv, int held, ushort heldId) {
            int heal = SurvivalItems.FoodHeal(heldId);
            if (heal <= 0) return;
            SurvivalNet.SetHealth(p, SurvivalNet.GetHealth(p) + heal);

            inv.Slots[held].Count--;
            if (inv.Slots[held].Count == 0) {
                if (heldId == SurvivalItems.SOUP) {
                    inv.Slots[held].Id = SurvivalItems.BOWL; inv.Slots[held].Count = 1; inv.Slots[held].Damage = 0;
                } else {
                    inv.Slots[held].Id = 0; inv.Slots[held].Damage = 0;
                }
            }
            SendSlot(p, inv, held);
        }

        // ItemStack.damageItem: wear a tool by `amount`; it shatters (empties the
        // slot) once damage exceeds its maxDamage. No-op for non-damageable ids.
        static void DamageHeldTool(Player p, PlayerInv inv, int held, int amount) {
            int max = SurvivalItems.MaxDurability(inv.Slots[held].Id);
            if (max == 0) return;
            inv.Slots[held].Damage += (short)amount;
            if (inv.Slots[held].Damage > max) { // damageItem: strictly greater = break
                inv.Slots[held].Id = 0; inv.Slots[held].Count = 0; inv.Slots[held].Damage = 0;
            }
            SendSlot(p, inv, held);
        }

        // consume `amount` from the held slot (no send - the caller echoes)
        static void ConsumeHeld(PlayerInv inv, int held, int amount) {
            if (inv.Slots[held].Count <= 0) return;
            inv.Slots[held].Count -= (byte)amount;
            if (inv.Slots[held].Count <= 0) {
                inv.Slots[held].Id = 0; inv.Slots[held].Count = 0; inv.Slots[held].Damage = 0;
            }
        }

        // the client zeroes its container view on CONT_OPEN, so only send occupied
        // slots. Under contLock so the snapshot is consistent w.r.t. clicks/ticks.
        static void StreamContainer(Player p, OpenRef open) {
            int n = OpenSlotCount(open);
            lock (contLock) {
                for (int i = 0; i < n; i++)
                {
                    Slot s = GetContSlot(open, i);
                    if (s.Count == 0) continue;
                    SurvivalNet.SendContSlot(p, i, s.Id, s.Count, s.Damage);
                }
            }
        }

        // echo a changed container slot to every viewer of the same tile entity
        static void EchoContSlot(OpenRef open, int ci) {
            // A player-inventory view is backed by the target's own slots: route
            // through SendSlot so the target sees the change (INV_SLOT) AND every
            // open view of them (this admin included) gets the CONT_SLOT echo.
            if (open.Kind == CONT_PLAYERINV) {
                SendSlot(open.Target, Get(open.Target), PlayerInvSlot(ci));
                return;
            }
            Slot s = GetContSlot(open, ci);
            Player[] players = PlayerInfo.Online.Items;
            foreach (Player pl in players)
            {
                OpenRef o = GetOpen(pl);
                if (o == null || (o.Upper != open.Upper && o.Upper != open.Lower)) continue;
                // same upper container = same view (large-chest halves share both)
                SurvivalNet.SendContSlot(pl, ci, s.Id, s.Count, s.Damage);
            }
        }

        // Fan a target's own inventory change out to every open /Inventory view of
        // them, so an operator watching (or another admin editing) sees it live.
        // O(online) but only for slots that appear in a player-inventory view, and
        // player-inv views are rare - fine for the expected scale.
        static void EchoPlayerViews(Player target, int pslot) {
            int ci = PlayerInvCell(pslot);
            if (ci < 0) return;
            PlayerInv tinv = Get(target);
            Slot s = tinv.Slots[pslot];
            Player[] players = PlayerInfo.Online.Items;
            foreach (Player pl in players)
            {
                if (pl == target) continue;
                OpenRef o = GetOpen(pl);
                if (o == null || o.Kind != CONT_PLAYERINV || o.Target != target) continue;
                SurvivalNet.SendContSlot(pl, ci, s.Id, s.Count, s.Damage);
            }
        }

        // The full-resync fan-out: one scan for the viewers, then all 36 cells to
        // each (a SendAll changed potentially every slot).
        static void EchoAllPlayerViews(Player target) {
            Player[] players = PlayerInfo.Online.Items;
            PlayerInv tinv = null;
            foreach (Player pl in players)
            {
                if (pl == target) continue;
                OpenRef o = GetOpen(pl);
                if (o == null || o.Kind != CONT_PLAYERINV || o.Target != target) continue;
                if (tinv == null) tinv = Get(target);
                for (int ci = 0; ci < PLAYERINV_SLOTS; ci++)
                {
                    Slot s = tinv.Slots[PlayerInvSlot(ci)];
                    SurvivalNet.SendContSlot(pl, ci, s.Id, s.Count, s.Damage);
                }
            }
        }

        // ==================== furnace smelting (TileEntityFurnace) ====================

        static byte FurnBurnScaled(Container te) {
            return (byte)(te.CurrentBurn > 0 ? te.BurnTime * 12 / te.CurrentBurn : 0);
        }
        static byte FurnCookScaled(Container te) {
            return (byte)(te.CookTime * 24 / 200);
        }

        // Furnace_CanSmelt hard-caps the OUTPUT at 64 for every result, not the
        // per-id max stack (SurvivalTest.c:1398 return slots[2].count < 64). This
        // matters for the block results (sand->glass, cobble->stone) whose
        // MaxStack would otherwise be 99.
        const int FURNACE_OUTPUT_MAX = 64;

        static bool CanSmelt(Container te) {
            if (te.Slots[0].Count == 0) return false;
            ushort result = SurvivalItems.SmeltResult(te.Slots[0].Id);
            if (result == 0) return false;
            if (te.Slots[2].Count == 0) return true;
            return te.Slots[2].Id == result &&
                   te.Slots[2].Count < FURNACE_OUTPUT_MAX;
        }

        /// <summary> One 20 TPS smelting pass over a level's furnaces - the genuine
        /// TileEntityFurnace.updateEntity: burn the fuel down, cook for 200 ticks
        /// per item, flip the block to its lit/unlit form (facing preserved), and
        /// stream slots + FURN_PROG to viewers. Called from the survival mob tick
        /// so furnaces run whenever anyone is on the map. </summary>
        public static void TickFurnaces(Level lvl) {
            if (lvl.Config.SurvivalMode != SurvivalMode.Indev) return;

            // The whole per-furnace read-modify-write of te.Slots/burn/cook runs
            // under contLock, serialized against HandleSlotClick and the other
            // container paths (which now lock the same object) - without this the
            // tick thread and a click thread race the same slot array and
            // duplicate/lose items. Block flips touch the level array + broadcast,
            // so they're collected and applied AFTER the lock is released.
            List<KeyValuePair<Container, bool>> flips = null;
            lock (contLock) {
                Dictionary<long, Container> map;
                if (!contRegistry.TryGetValue(lvl, out map)) return;
                foreach (Container te in map.Values)
                {
                    if (te.Kind != CONT_FURNACE) continue;

                    bool wasBurning = te.BurnTime > 0;
                    bool slotsChanged = false;
                    if (te.BurnTime > 0) te.BurnTime--;

                    bool canSmelt = CanSmelt(te);
                    if (te.BurnTime == 0 && canSmelt) {
                        int fuel = SurvivalItems.FuelTime(te.Slots[1].Id);
                        if (fuel > 0) {
                            te.CurrentBurn = te.BurnTime = fuel;
                            if (--te.Slots[1].Count == 0) { te.Slots[1].Id = 0; te.Slots[1].Damage = 0; }
                            slotsChanged = true;
                        }
                    }

                    if (te.BurnTime > 0 && canSmelt) {
                        if (++te.CookTime >= 200) {
                            te.CookTime = 0;
                            ushort result = SurvivalItems.SmeltResult(te.Slots[0].Id);
                            if (te.Slots[2].Count == 0) { te.Slots[2].Id = result; te.Slots[2].Damage = 0; }
                            te.Slots[2].Count++;
                            if (--te.Slots[0].Count == 0) { te.Slots[0].Id = 0; te.Slots[0].Damage = 0; }
                            slotsChanged = true;
                        }
                    } else {
                        te.CookTime = 0;
                    }

                    bool burning = te.BurnTime > 0;
                    if (burning != wasBurning) {
                        if (flips == null) flips = new List<KeyValuePair<Container, bool>>();
                        flips.Add(new KeyValuePair<Container, bool>(te, burning));
                    }

                    // stream to viewers: slots on change, progress at 4 Hz while lit
                    // (or once when it goes out so the flame/arrow zero out)
                    bool tickProg = burning && (te.CookTime % 5) == 0;
                    if (!slotsChanged && !tickProg && burning == wasBurning) continue;
                    Player[] players = PlayerInfo.Online.Items;
                    foreach (Player pl in players)
                    {
                        OpenRef o = GetOpen(pl);
                        if (o == null || o.Upper != te) continue;
                        if (slotsChanged) {
                            for (int i = 0; i < 3; i++)
                                SurvivalNet.SendContSlot(pl, i, te.Slots[i].Id, te.Slots[i].Count, te.Slots[i].Damage);
                        }
                        SurvivalNet.SendFurnProg(pl, FurnBurnScaled(te), FurnCookScaled(te));
                    }
                }
            }

            if (flips != null) {
                foreach (KeyValuePair<Container, bool> f in flips)
                    FlipFurnaceBlock(lvl, f.Key, f.Value);
            }
        }

        // BlockFurnace.updateFurnaceBlockState: idle 61 <-> lit 62, facing views
        // 75..78 <-> 79..82 (+4/-4), keeping the orientation
        static void FlipFurnaceBlock(Level lvl, Container te, bool burning) {
            ushort raw = RawAt(lvl, te.X, te.Y, te.Z);
            ushort now = raw;
            if (burning) {
                if (raw == SurvivalBlocks.FURNACE) now = SurvivalBlocks.FURNACE_LIT;
                else if (raw >= SurvivalBlocks.FURN_V0 && raw <= SurvivalBlocks.FURN_V0 + 3)
                    now = (ushort)(raw + 4);
            } else {
                if (raw == SurvivalBlocks.FURNACE_LIT) now = SurvivalBlocks.FURNACE;
                else if (raw >= SurvivalBlocks.FURNL_V0 && raw <= SurvivalBlocks.FURNL_V0 + 3)
                    now = (ushort)(raw - 4);
            }
            if (now == raw) return;
            lvl.UpdateBlock(Player.Console, (ushort)te.X, (ushort)te.Y, (ushort)te.Z,
                            Block.FromRaw(now));
        }


        /// <summary> A container block was mined/removed: scatter its stored contents
        /// as drop entities (genuine TileEntity.onBreak / IndevTE scatter), discard
        /// the tile entity, and force-close any screens viewing it. Called from
        /// OnBlockChanging. </summary>
        /// <summary> Scatter + tile-entity cleanup for a chest/furnace destroyed by
        /// something other than a normal mine (an explosion), by view id. </summary>
        internal static void ContainerRemovedIfAny(Level lvl, int x, int y, int z, ushort view) {
            if (IsChestView(view) || IsFurnaceView(view)) ContainerRemoved(lvl, x, y, z);
        }

        // ==================== persistence (SurvivalPersistence) ====================

        /// <summary> Writes the level's chest/furnace tile entities as "cont x y z kind
        /// burn cook curBurn nslots id:count:dmg..." lines for the map sidecar. </summary>
        internal static void SaveContainers(Level lvl, System.IO.TextWriter w) {
            lock (contLock) {
                Dictionary<long, Container> map;
                if (!contRegistry.TryGetValue(lvl, out map)) return;
                foreach (Container c in map.Values)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    sb.AppendFormat("cont {0} {1} {2} {3} {4} {5} {6} {7}",
                        c.X, c.Y, c.Z, c.Kind, c.BurnTime, c.CookTime, c.CurrentBurn, c.Slots.Length);
                    foreach (Slot s in c.Slots) sb.AppendFormat(" {0}:{1}:{2}", s.Id, s.Count, s.Damage);
                    w.WriteLine(sb.ToString());
                }
            }
        }

        /// <summary> Restores one saved tile entity into the level's container
        /// registry (the block itself is already in the .lvl). </summary>
        internal static void RestoreContainer(Level lvl, string[] p) {
            if (p.Length < 9) return;
            Container c = new Container();
            c.X = int.Parse(p[1]); c.Y = int.Parse(p[2]); c.Z = int.Parse(p[3]);
            c.Kind = byte.Parse(p[4]);
            c.BurnTime = int.Parse(p[5]); c.CookTime = int.Parse(p[6]); c.CurrentBurn = int.Parse(p[7]);
            int n = int.Parse(p[8]);
            c.Slots = new Slot[n];
            for (int i = 0; i < n && 9 + i < p.Length; i++) {
                string[] f = p[9 + i].Split(':');
                if (f.Length < 3) continue;
                c.Slots[i] = new Slot { Id = ushort.Parse(f[0]), Count = byte.Parse(f[1]), Damage = short.Parse(f[2]) };
            }
            lock (contLock) {
                Dictionary<long, Container> map;
                if (!contRegistry.TryGetValue(lvl, out map)) { map = new Dictionary<long, Container>(); contRegistry[lvl] = map; }
                map[PackPos(c.X, c.Y, c.Z)] = c;
            }
        }

        public static void ContainerRemoved(Level lvl, int x, int y, int z) {
            Container te = null;
            lock (contLock) {
                Dictionary<long, Container> map;
                if (contRegistry.TryGetValue(lvl, out map)) {
                    long key = PackPos(x, y, z);
                    if (map.TryGetValue(key, out te)) map.Remove(key);
                }
            }
            if (te == null) return;

            // scatter each non-empty slot as one drop carrying its full stack
            // (one EntityItem per stack, at the broken block's centre)
            int delay = SurvivalDrops.MinedDelay(lvl);
            foreach (Slot s in te.Slots)
            {
                if (s.Count == 0 || s.Id == 0) continue;
                SurvivalDrops.SpawnStack(lvl, x + 0.5, y + 0.5, z + 0.5, s.Id, s.Count, delay);
            }

            Player[] players = PlayerInfo.Online.Items;
            foreach (Player pl in players)
            {
                OpenRef o = GetOpen(pl);
                if (o == null || (o.Upper != te && o.Lower != te)) continue;
                pl.Extras.Remove(OPEN_KEY);
                SurvivalNet.SendContOpen(pl, CONT_NONE, 0); // force-close the screen
            }
        }

        /// <summary> Removes contRegistry entries whose Level is no longer loaded.
        /// The Level object is the dictionary key, so without this an unloaded map
        /// (its whole block array + container state) leaks forever. Called from the
        /// mob tick's prune sweep, mirroring SurvivalMobs' own registry prune. </summary>
        public static void PruneRegistry(Level[] loaded) {
            lock (contLock) {
                List<Level> dead = null;
                foreach (KeyValuePair<Level, Dictionary<long, Container>> kvp in contRegistry)
                {
                    if (Array.IndexOf(loaded, kvp.Key) < 0) {
                        if (dead == null) dead = new List<Level>();
                        dead.Add(kvp.Key);
                    }
                }
                if (dead != null) foreach (Level lvl in dead) contRegistry.Remove(lvl);
            }
        }

        /// <summary> The player changed level: drop any open-container ref, which
        /// points at the level they just left (holding that Level + its Containers
        /// alive, and - without the per-click level guard - lootable remotely).
        /// Called from SurvivalNet.OnJoinedLevel. </summary>
        public static void OnLeftLevel(Player p) {
            p.Extras.Remove(OPEN_KEY);
        }

        /// <summary> Opens a chest-style view of another player's inventory
        /// (/Inventory). Operators view (canEdit false); admins may move items
        /// between the target's slots and their own. The window renders on the
        /// viewer as a 36-cell chest - genuine inventory layout (main storage on
        /// top, hotbar on the bottom row). Returns false if the viewer isn't a
        /// survival-test client that can show the GUI. </summary>
        /// <remarks> An admin's edits run under contLock; the target's own click /
        /// block-bridge mutations run on their receive thread without it, so a
        /// simultaneous edit-and-self-click on the very same slot can lose one
        /// update (self-heals on the next resync). This is the same accepted race
        /// as /SurvivalGive, and vanishingly rare for a live admin tool. </remarks>
        public static bool OpenPlayerInventory(Player viewer, Player target, bool canEdit, bool solo = false) {
            if (viewer == null || target == null) return false;
            if (viewer.Session == null || !viewer.Session.hasSurvival) return false;
            if (!SurvivalNet.Active(viewer, viewer.level)) return false;

            OpenRef open = new OpenRef();
            open.Kind = CONT_PLAYERINV;
            open.Lvl = viewer.level;
            open.Target = target;
            open.CanEdit = canEdit;
            viewer.Extras[OPEN_KEY] = open;
            // v3 clients render the dedicated player-inventory panel (kind 5, all
            // 40 cells incl. armor); a v2 client only knows chest, so fall back to
            // a 36-cell chest view (armor cells hidden - graceful degradation).
            bool panel = SurvivalNet.SurvVer(viewer) >= 3;
            if (panel) {
                // the target's paperdoll: send the entity id the viewer sees the
                // target as (0xFF = not visible to them - a different level - so
                // the client shows no model). solo = single-panel spectate view.
                byte eid;
                if (viewer == target || !viewer.EntityList.TryGetVisibleID(target, out eid)) eid = 0xFF;
                SurvivalNet.SendPlayerInvOpen(viewer, PLAYERINV_SLOTS, eid, solo);
            } else {
                SurvivalNet.SendContOpen(viewer, CONT_CHEST, 36);
            }
            StreamContainer(viewer, open);
            return true;
        }

        /// <summary> Closes whatever container/inventory view the player currently
        /// has open (clears the ref and force-closes the client screen). Used by
        /// /Spectate stop to dismiss the mirrored inventory panel. </summary>
        public static void ForceCloseView(Player p) {
            if (GetOpen(p) == null) return;
            p.Extras.Remove(OPEN_KEY);
            SurvivalNet.SendContOpen(p, CONT_NONE, 0);
        }

        /// <summary> A player disconnected: force-close every open /Inventory view
        /// of them (the view holds a now-departed Player). Registered on
        /// OnPlayerDisconnectEvent. </summary>
        public static void OnPlayerDisconnect(Player target, string reason) {
            Player[] players = PlayerInfo.Online.Items;
            foreach (Player pl in players)
            {
                if (pl == target) continue;
                OpenRef o = GetOpen(pl);
                if (o == null || o.Kind != CONT_PLAYERINV || o.Target != target) continue;
                pl.Extras.Remove(OPEN_KEY);
                SurvivalNet.SendContOpen(pl, CONT_NONE, 0); // force-close the screen
            }
        }


        // ==================== .mclevel export bridge ====================

        /// <summary> A plain-data snapshot of one container tile entity, for the
        /// .mclevel exporter (contents + furnace progress; positions in blocks). </summary>
        public class ContSnapshot
        {
            public bool Furnace;
            public int X, Y, Z, BurnTime, CookTime;
            public ushort[] Ids; public byte[] Counts; public short[] Damages;
        }

        /// <summary> Snapshots every live container tile entity on a level. </summary>
        public static List<ContSnapshot> SnapshotContainers(Level lvl) {
            List<ContSnapshot> list = new List<ContSnapshot>();
            lock (contLock) {
                Dictionary<long, Container> map;
                if (!contRegistry.TryGetValue(lvl, out map)) return list;

                foreach (Container te in map.Values)
                {
                    ContSnapshot s = new ContSnapshot();
                    s.Furnace  = te.Kind == CONT_FURNACE;
                    s.X = te.X; s.Y = te.Y; s.Z = te.Z;
                    s.BurnTime = te.BurnTime; s.CookTime = te.CookTime;

                    int n = te.Slots.Length;
                    s.Ids = new ushort[n]; s.Counts = new byte[n]; s.Damages = new short[n];
                    for (int i = 0; i < n; i++)
                    {
                        s.Ids[i]     = te.Slots[i].Id;
                        s.Counts[i]  = te.Slots[i].Count;
                        s.Damages[i] = te.Slots[i].Damage;
                    }
                    list.Add(s);
                }
            }
            return list;
        }


        // ==================== other players' equipment (SURV_PLAYER_EQUIP) ====================

        // A player's currently-visible equipment as item ids: held (selected hotbar
        // slot) + the 4 armor pieces (boots..helmet, matching the client's order),
        // 0 = empty. Counts aren't streamed - the client treats non-zero as worn.
        static void ComputeEquip(PlayerInv inv, out ushort held, out ushort[] armor) {
            held = inv.HeldSlot >= 0 && inv.HeldSlot < 9 && inv.Slots[inv.HeldSlot].Count > 0
                 ? inv.Slots[inv.HeldSlot].Id : (ushort)0;
            armor = new ushort[ARMOR_SLOTS];
            for (int i = 0; i < ARMOR_SLOTS; i++)
                armor[i] = inv.Slots[ARMOR_BASE + i].Count > 0 ? inv.Slots[ARMOR_BASE + i].Id : (ushort)0;
        }

        // ==================== armor absorption (EntityPlayer.attackEntityFrom) ====================

        const string DMG_REMAINDER_KEY = "survival.dmgRemainder"; // EntityPlayer.damageRemainder

        // InventoryPlayer.getPlayerArmorValue: the summed damageReduceAmount of
        // worn pieces, weighted by their remaining durability.
        static int ArmorValue(PlayerInv inv) {
            int reduce = 0, remain = 0, max = 0;
            for (int i = 0; i < ARMOR_SLOTS; i++) {
                int s = ARMOR_BASE + i;
                if (inv.Slots[s].Count <= 0) continue;
                int m = SurvivalItems.ArmorMaxDamage(inv.Slots[s].Id);
                if (m <= 0) continue;
                remain += m - inv.Slots[s].Damage;
                max    += m;
                reduce += SurvivalItems.ArmorReduce(inv.Slots[s].Id);
            }
            return max == 0 ? 0 : (reduce - 1) * remain / max + 1;
        }

        /// <summary> EntityPlayer.attackEntityFrom armor scaling (Indev): reduces the
        /// raw damage in 25ths weighted by worn armor, carrying the sub-1HP remainder
        /// between hits, and wears every worn piece by the raw damage (shattering a
        /// piece past its max). Returns the HP actually lost (0 = fully absorbed; the
        /// armor still wore). Caller applies the returned HP loss. </summary>
        public static int AbsorbArmor(Player p, int damage) {
            PlayerInv inv = Get(p);
            int armorValue = ArmorValue(inv);
            int scaled = damage * (25 - armorValue) + p.Extras.GetInt(DMG_REMAINDER_KEY, 0);

            bool shattered = false;
            for (int i = 0; i < ARMOR_SLOTS; i++) {
                int s = ARMOR_BASE + i;
                if (inv.Slots[s].Count <= 0) continue;
                if (SurvivalItems.ArmorMaxDamage(inv.Slots[s].Id) <= 0) continue;
                inv.Slots[s].Damage += (short)damage;
                if (inv.Slots[s].Damage > SurvivalItems.ArmorMaxDamage(inv.Slots[s].Id)) {
                    inv.Slots[s] = new Slot(); // shatter
                    shattered = true;
                }
                SendSlot(p, inv, s);
            }
            p.Extras[DMG_REMAINDER_KEY] = scaled % 25;
            if (shattered) BroadcastEquip(p); // a worn piece vanished - update the body
            return scaled / 25;
        }


        const string EQUIP_KEY = "survival.equip"; // last-broadcast {held,a0,a1,a2,a3}

        // Resolves the equipped player's entity id as the viewer sees it and streams
        // their current worn armor + held item. No-op if the viewer can't see them.
        static void SendEquipTo(Player viewer, Player equipped) {
            Level lvl = equipped.level;
            if (viewer.level != lvl || !SurvivalNet.Active(viewer, lvl) || !SurvivalNet.Active(equipped, lvl)) return;
            byte eid;
            if (!viewer.EntityList.TryGetVisibleID(equipped, out eid)) return;
            ushort held; ushort[] armor;
            ComputeEquip(Get(equipped), out held, out armor);
            SurvivalNet.SendPlayerEquip(viewer, eid, held, armor);
        }

        /// <summary> Streams a player's worn armor + held item to every OTHER survival
        /// viewer on their level (each with the equipped player's per-viewer entity id).
        /// The equipped player renders their own equipment locally, so they're skipped.
        /// Deduped on the visible equipment so it can be called from the central echo
        /// path (SendAll) without spamming - only a real change fans out. NEW viewers
        /// are handled by OnEntitySpawned, not this. </summary>
        public static void BroadcastEquip(Player equipped) {
            Level lvl = equipped.level;
            if (lvl == null || !SurvivalNet.Active(equipped, lvl)) return;
            ushort held; ushort[] armor;
            ComputeEquip(Get(equipped), out held, out armor);
            ushort[] cur = { held, armor[0], armor[1], armor[2], armor[3] };

            object o;
            if (equipped.Extras.TryGet(EQUIP_KEY, out o) && SameEquip((ushort[])o, cur)) return;
            equipped.Extras[EQUIP_KEY] = cur;

            Player[] players = PlayerInfo.Online.Items;
            foreach (Player viewer in players)
                if (viewer != equipped) SendEquipTo(viewer, equipped);
        }

        static bool SameEquip(ushort[] a, ushort[] b) {
            for (int i = 0; i < 5; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // A player entity became visible to another. The entity id isn't registered
        // until just after this event fires (Spawn() calls it before SpawnRaw), so we
        // queue the (viewer, equipped) pair and flush it on the next survival tick,
        // by when TryGetVisibleID resolves. Handles both join directions (each side is
        // spawned to the other), which is why the handshake doesn't send equip itself.
        static readonly object equipQueueLock = new object();
        static readonly List<KeyValuePair<Player, Player>> equipQueue = new List<KeyValuePair<Player, Player>>();

        public static void OnEntitySpawned(Entity e, ref string name, ref string skin, ref string model, Player dst) {
            Player equipped = e as Player;
            if (equipped == null || dst == null || equipped == dst) return;
            if (!SurvivalNet.Active(equipped, equipped.level) || !SurvivalNet.Active(dst, dst.level)) return;
            lock (equipQueueLock) equipQueue.Add(new KeyValuePair<Player, Player>(dst, equipped));
        }

        /// <summary> Flushes queued "entity became visible" equip sends (called once per
        /// survival tick from SurvivalMobs). </summary>
        public static void FlushEquip() {
            KeyValuePair<Player, Player>[] pending;
            lock (equipQueueLock) {
                if (equipQueue.Count == 0) return;
                pending = equipQueue.ToArray();
                equipQueue.Clear();
            }
            foreach (KeyValuePair<Player, Player> kv in pending)
                SendEquipTo(kv.Key, kv.Value); // key = viewer, value = equipped
        }

        // Re-broadcast a player's equipment if a mutated slot could have changed what
        // shows on their body (the held hotbar slot or an armor slot).
        static void EquipIfVisibleChange(Player p, int slot) {
            if (slot < 0) return;
            PlayerInv inv = Get(p);
            if (slot == inv.HeldSlot || (slot >= ARMOR_BASE && slot < ARMOR_BASE + ARMOR_SLOTS))
                BroadcastEquip(p);
        }


        // ==================== intents ====================

        public static void HandleHeldSlot(Player p, int slot) {
            if (slot < 0 || slot > 8) return;
            PlayerInv inv = Get(p);
            bool changed = inv.HeldSlot != slot;
            inv.HeldSlot = slot;
            if (changed) BroadcastEquip(p); // the held item on the body follows the hotbar selection
        }

        /// <summary> SURV_SLOT_CLICK: the GuiContainer click model, run on the server's
        /// authoritative slots + cursor, echoing the changed slot and the cursor. </summary>
        public static void HandleSlotClick(Player p, int idx, int button) {
            if (!SurvivalNet.Active(p, p.level) || SurvivalNet.IsDead(p)) return;
            // creative maps: the inventory is client-local (the palette) - a stray
            // intent must not mutate the server's (unused) slots
            if (p.level != null && p.level.Config.SurvivalCreative) return;
            if (idx < 0 || idx >= TOTAL_SLOTS) return;

            // container range: resolve through the player's OPEN container view
            bool isCont = idx >= CONT_BASE && idx < CONT_BASE + CONT_MAX;
            OpenRef open = null;
            int ci = 0;
            if (isCont) {
                open = GetOpen(p);
                if (open == null) return;
                // Stale ref from a level the player left (no CONT_CLOSE arrived):
                // drop it so a container on another level can't be looted remotely.
                if (open.Lvl != p.level) { p.Extras.Remove(OPEN_KEY); return; }
                // an operator's /Inventory view is read-only - refuse every click
                // on the target's slots (only an admin's CanEdit view may mutate)
                if (open.Kind == CONT_PLAYERINV && !open.CanEdit) return;
                ci = idx - CONT_BASE;
                if (ci >= OpenSlotCount(open)) return;
            }

            PlayerInv inv = Get(p);
            bool right = button != 0;

            // SlotArmor.isItemValid: an armor slot (99 boots .. 102 helmet) accepts
            // ONLY its matching piece; wrong item or non-armor is refused, taking
            // out is always allowed. Applies to the player's own armor slots and,
            // for an admin's editable /Inventory view, the target's armor cells.
            if (inv.Cursor.Count > 0) {
                if (idx >= ARMOR_BASE && idx < ARMOR_BASE + ARMOR_SLOTS &&
                    ArmorSlotRejects(idx, inv.Cursor.Id)) return;
                if (isCont && open.Kind == CONT_PLAYERINV) {
                    int ts = PlayerInvSlot(ci);
                    if (ts >= ARMOR_BASE && ts < ARMOR_BASE + ARMOR_SLOTS &&
                        ArmorSlotRejects(ts, inv.Cursor.Id)) return;
                }
            }
            // SlotFurnace (the output, container slot 2): TAKE-ONLY - placing
            // into it (including merging onto an existing stack) is refused,
            // like the genuine furnace GUI (user-reported).
            if (isCont && open.Kind == CONT_FURNACE && ci == 2 && inv.Cursor.Count > 0) return;

            Slot cur = inv.Cursor;
            if (isCont) {
                // read-modify-write + echo atomically under contLock, serialized
                // against the furnace tick and other viewers clicking the same
                // shared tile entity (all of which lock the same object)
                lock (contLock) {
                    Slot slot = GetContSlot(open, ci);
                    if (!ApplyClick(p, ref slot, ref cur, right)) return;
                    SetContSlot(open, ci, slot);
                    inv.Cursor = cur;
                    EchoContSlot(open, ci); // every viewer of this tile entity
                }
                SendCursor(p, inv);
                // an admin editing a viewed player's inventory (/Inventory CanEdit)
                // may have changed that TARGET's worn armor or held hotbar item
                if (open.Kind == CONT_PLAYERINV && open.Target != null) BroadcastEquip(open.Target);
            } else {
                // player inventory is per-player - only this receive thread mutates it
                Slot slot = inv.Slots[idx];
                if (!ApplyClick(p, ref slot, ref cur, right)) return;
                inv.Slots[idx] = slot;
                inv.Cursor = cur;
                SendSlot(p, inv, idx);
                SendCursor(p, inv);
                EquipIfVisibleChange(p, idx); // took off armor / rearranged the held slot
            }
        }

        // SlotArmor.isItemValid: the armor slot (99 boots .. 102 helmet, matching
        // the client's cell order) accepts only its own piece. True = refuse.
        static bool ArmorSlotRejects(int armorSlot, ushort cursorId) {
            int want = (ARMOR_BASE + ARMOR_SLOTS - 1) - armorSlot; // 102->0 helmet .. 99->3 boots
            return SurvivalItems.ArmorPiece(cursorId) != want;
        }

        // The GuiContainer click model applied to one slot + the cursor: pick up
        // all (or ceil-half on right-click), merge onto a like stack to its max,
        // right-click place one into an empty slot, else swap. Returns false when
        // the click is a no-op (empty slot with empty cursor, or a full merge) so
        // the caller skips the echo. Container callers hold contLock.
        static bool ApplyClick(Player p, ref Slot slot, ref Slot cur, bool right) {
            if (cur.Count == 0) {
                if (slot.Count == 0) return false;
                int moved = right ? (slot.Count + 1) / 2 : slot.Count;
                cur = slot;
                cur.Count   = (byte)moved;
                slot.Count -= (byte)moved;
                if (slot.Count == 0) { slot.Id = 0; slot.Damage = 0; }
            } else if (slot.Count > 0 && slot.Id == cur.Id) {
                int space = MaxStack(p, slot.Id) - slot.Count;
                if (space <= 0) return false;
                int moved = right ? 1 : cur.Count;
                if (moved > space) moved = space;
                slot.Count += (byte)moved;
                cur.Count  -= (byte)moved;
                if (cur.Count == 0) { cur.Id = 0; cur.Damage = 0; }
            } else if (slot.Count == 0 && right) {
                slot.Id     = cur.Id;
                slot.Damage = cur.Damage;
                slot.Count  = 1;
                if (--cur.Count == 0) { cur.Id = 0; cur.Damage = 0; }
            } else {
                Slot tmp = slot; slot = cur; cur = tmp;
            }
            return true;
        }

        /// <summary> SURV_RESULT_CLICK: SlotCrafting pickup. Matches the craft grid
        /// (2x2 pocket, or 3x3 with a workbench view open) against the recipe
        /// table - which must stay identical to the client's, since the client
        /// renders the preview locally - then yields the result onto the CURSOR
        /// (stacking when it fits) and consumes one of each grid ingredient. </summary>
        public static void HandleResultClick(Player p) {
            if (!SurvivalNet.Active(p, p.level) || SurvivalNet.IsDead(p)) return;
            if (p.level == null || p.level.Config.SurvivalMode != SurvivalMode.Indev) return;
            if (p.level.Config.SurvivalCreative) return;
            PlayerInv inv = Get(p);

            OpenRef open = GetOpen(p);
            // ignore a workbench ref left over from a level the player left
            if (open != null && open.Lvl != p.level) { p.Extras.Remove(OPEN_KEY); open = null; }
            int dim = open != null && open.Kind == CONT_WORKBENCH ? 3 : 2;
            ushort[] grid = new ushort[dim * dim];
            for (int i = 0; i < grid.Length; i++)
            {
                Slot s = inv.Slots[CRAFT_BASE + i];
                grid[i] = s.Count > 0 ? s.Id : (ushort)0;
            }

            ushort id; int count;
            if (!SurvivalItems.MatchRecipe(grid, dim, dim, out id, out count)) return;
            // result goes onto the cursor; refuse when it holds something else
            // or the stack would overflow (the client's ResultClick rule)
            if (inv.Cursor.Count > 0 &&
                (inv.Cursor.Id != id || inv.Cursor.Count + count > MaxStack(p, id))) return;

            inv.Cursor.Id     = id;
            inv.Cursor.Count += (byte)count;
            inv.Cursor.Damage = 0;
            for (int i = CRAFT_BASE; i < CRAFT_BASE + CRAFT_SLOTS; i++)
            {
                if (inv.Slots[i].Count == 0) continue;
                if (--inv.Slots[i].Count == 0) { inv.Slots[i].Id = 0; inv.Slots[i].Damage = 0; }
                SendSlot(p, inv, i);
            }
            SendCursor(p, inv);
        }

        /// <summary> SURV_CONT_CLOSE: the window closed - return the cursor and the
        /// craft grid to the inventory (never lose either), then resync. </summary>
        public static void HandleContClose(Player p) {
            if (!SurvivalNet.Active(p, p.level)) return;
            p.Extras.Remove(OPEN_KEY); // the container view is closed either way
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

        /// <summary> Drop-pickup path: if the WHOLE stack fits, add it and echo the
        /// affected slots; returns true (collected) or false (no room - the drop
        /// stays on the ground for someone else / later). Whole-stack only, so a
        /// pickup never leaves a partial drop the client can't re-count. </summary>
        public static bool PickUp(Player p, ushort id, int count) {
            if (!SurvivalNet.Active(p, p.level)) return false;
            PlayerInv inv = Get(p);
            if (!HasRoomFor(inv, p, id, count)) return false;
            for (int n = 0; n < count; n++) AddOne(p, inv, id, 0);
            SendAll(p); // several slots may change - a full resync is simplest
            return true;
        }

        // Whether the main inventory can absorb `count` more of `id` (empty slots
        // hold a full stack each; matching stacks take up to their max).
        static bool HasRoomFor(PlayerInv inv, Player p, ushort id, int count) {
            int max = MaxStack(p, id), room = 0;
            for (int i = 0; i < MAIN_SLOTS; i++)
            {
                if (inv.Slots[i].Count == 0)              room += max;
                else if (inv.Slots[i].Id == id)           room += Math.Max(0, max - inv.Slots[i].Count);
                if (room >= count) return true;
            }
            return room >= count;
        }

        /// <summary> Player-death scatter (SurvivalDeathDrops): drops one entity per
        /// non-empty player slot (main + craft + armor + cursor - never the shared
        /// container range) at the player's chest, carrying the full stack, then
        /// clears them and resyncs. Genuine Player.die scatters the inventory so a
        /// respawned player can walk back and re-collect it. </summary>
        public static void DeathScatter(Player p) {
            Level lvl = p.level;
            if (lvl == null || !SurvivalNet.Active(p, lvl)) return;
            PlayerInv inv = Get(p);
            double x = p.Pos.X / 32.0;
            double y = (p.Pos.Y - Entities.CharacterHeight) / 32.0 + 1.0; // chest height
            double z = p.Pos.Z / 32.0;
            int delay = SurvivalDrops.MinedDelay(lvl);
            bool any = false;

            for (int i = 0; i < TOTAL_SLOTS; i++)
            {
                if (i >= CONT_BASE && i < ARMOR_BASE) continue; // container range isn't the player's
                if (inv.Slots[i].Count == 0 || inv.Slots[i].Id == 0) continue;
                SurvivalDrops.SpawnStack(lvl, x, y, z, inv.Slots[i].Id, inv.Slots[i].Count, delay);
                inv.Slots[i] = new Slot();
                any = true;
            }
            if (inv.Cursor.Count > 0 && inv.Cursor.Id != 0) {
                SurvivalDrops.SpawnStack(lvl, x, y, z, inv.Cursor.Id, inv.Cursor.Count, delay);
                inv.Cursor = new Slot();
                any = true;
            }
            if (any) { SendAll(p); BroadcastEquip(p); } // held + armor cleared - update the body
        }

        /// <summary> Indev bow fire: consumes one arrow item (id 256+6) from the first
        /// main slot holding it and echoes that slot. Returns false if the player has
        /// no arrows (a dry bow does nothing). </summary>
        public static bool ConsumeArrow(Player p) {
            const ushort ARROW = 256 + 6;
            PlayerInv inv = Get(p);
            for (int i = 0; i < MAIN_SLOTS; i++)
            {
                if (inv.Slots[i].Count == 0 || inv.Slots[i].Id != ARROW) continue;
                inv.Slots[i].Count--;
                if (inv.Slots[i].Count == 0) { inv.Slots[i].Id = 0; inv.Slots[i].Damage = 0; }
                SendSlot(p, inv, i);
                return true;
            }
            return false;
        }

        /// <summary> Q-toss path: takes item(s) off a hotbar slot for SurvivalDrops
        /// to fling out as a drop entity. `whole` empties the stack, otherwise one
        /// is taken. Echoes the slot; returns false (nothing taken) for an empty or
        /// out-of-range slot. </summary>
        public static bool TakeForToss(Player p, int slot, bool whole, out ushort id, out byte count) {
            id = 0; count = 0;
            if (slot < 0 || slot >= MAIN_SLOTS) return false;
            PlayerInv inv = Get(p);
            if (inv.Slots[slot].Count == 0) return false;

            id    = inv.Slots[slot].Id;
            count = whole ? inv.Slots[slot].Count : (byte)1;
            inv.Slots[slot].Count -= count;
            if (inv.Slots[slot].Count == 0) { inv.Slots[slot].Id = 0; inv.Slots[slot].Damage = 0; }
            SendSlot(p, inv, slot);
            return true;
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
            if (lvl.Config.SurvivalCreative) {
                // creative: free build for everyone, no pickup/consume - but the
                // Indev placement shaping (furnace/chest facing, torch mounting,
                // leftover-block refusal) still applies so the authoritative
                // world matches what the Indev client builds locally
                if (placing && p.Session != null && lvl.Config.SurvivalMode == SurvivalMode.Indev)
                    ShapeIndevPlacement(p, lvl, x, y, z, block, ref cancel);
                return;
            }

            // networking-plan §16's hard invariant: a client that never negotiated
            // SurvivalTest bypasses tools/consumption/drops, so letting it modify a
            // survival world would corrupt the authoritative state. Default policy
            // is look-but-don't-touch; the map owner may opt into Allow. Referees
            // keep their staff escape hatch (draw commands are unaffected anyway).
            bool indev = lvl.Config.SurvivalMode == SurvivalMode.Indev;

            if (p.Session == null || !p.Session.hasSurvival) {
                if (lvl.Config.SurvivalVisitors == SurvivalVisitorPolicy.Allow || p.Game.Referee) {
                    // permitted stock-client builds still get the Indev placement
                    // shaping so the world stays consistent (faced containers,
                    // mounted torches, no leftover blocks)
                    if (indev && placing && p.Session != null)
                        ShapeIndevPlacement(p, lvl, x, y, z, block, ref cancel);
                    return;
                }
                cancel = true;
                p.RevertBlock(x, y, z);
                WarnVisitor(p);
                return;
            }
            if (SurvivalNet.IsDead(p)) { cancel = true; p.RevertBlock(x, y, z); return; }

            PlayerInv inv = Get(p);

            if (placing) {
                ushort raw  = p.Session.ConvertBlock(block);
                ushort view = raw;

                if (indev && !ValidateIndevPlace(p, lvl, x, y, z, raw, out view)) {
                    cancel = true;
                    p.RevertBlock(x, y, z);
                    return; // refused before anything is consumed
                }

                ushort cost = raw <= Block.CLASSIC_MAX_BLOCK ? raw
                            : indev ? SurvivalBlocks.PlaceCost(raw) : (ushort)0;
                if (cost != 0) {
                    int idx = ConsumeSlot(p, inv, cost);
                    if (idx < 0) {
                        cancel = true;
                        p.RevertBlock(x, y, z);
                        // resync the hotbar so a stale client view corrects itself
                        SendAll(p);
                        return;
                    }
                    SendSlot(p, inv, idx);
                    EquipIfVisibleChange(p, idx); // placing may have emptied the held stack
                } else if (!indev) {
                    return; // not survival content - pass through unconsumed
                }

                if (view != raw) {
                    // placement rotation/mounting: cancel the canonical place and
                    // broadcast the directional view instead - one authoritative
                    // write that also confirms (or corrects) the fork client's
                    // own local facing guess
                    cancel = true;
                    lvl.UpdateBlock(Player.Console, x, y, z, Block.FromRaw(view));
                }
            } else {
                BlockID old = lvl.GetBlock(x, y, z);
                ushort raw  = p.Session.ConvertBlock(Block.Convert(old));
                if (raw == Block.Air) return;
                // a mined container discards its tile entity + force-closes viewers
                if (indev && (IsChestView(raw) || IsFurnaceView(raw)))
                    ContainerRemoved(lvl, x, y, z);
                // liquids never yield a pickup (breaking still-water via commands etc.)
                byte collide = lvl.CollideType(old);
                if (collide == CollideType.SwimThrough || collide == CollideType.LiquidWater ||
                    collide == CollideType.LiquidLava) return;

                // phase 5: mining no longer teleports the yield into the inventory -
                // it spawns physical drop entities (the genuine Indev drop table on
                // an Indev map, the block itself on c0.30) that the player then
                // walks over to collect. SurvivalDrops owns the harvest gating,
                // grass->dirt / ore->item mapping, seed rolls and the pop/settle.
                ushort held = indev && inv.HeldSlot >= 0 && inv.HeldSlot < 9
                            ? inv.Slots[inv.HeldSlot].Id : (ushort)0;
                SurvivalDrops.SpawnMined(p, lvl, x, y, z, raw, held);
            }
        }

        // ==================== Indev placement shaping ====================
        // Mirrors the client's SP placement handling (IndevTest_BlockChanged +
        // IndevTest_CanPlaceBlockAt): canonical chests/furnaces rotate so the
        // front faces the placer, torches wall-mount off their support, chest
        // triples/L-shapes and the non-Indev CPE leftovers are refused. The
        // server is authoritative - its rewrite is broadcast to everyone,
        // confirming (or correcting) the fork client's local guess.

        /// <summary> The creative-map variant: no inventory bookkeeping, just
        /// validation + the directional rewrite. </summary>
        static void ShapeIndevPlacement(Player p, Level lvl, ushort x, ushort y, ushort z,
                                        BlockID block, ref bool cancel) {
            ushort raw = p.Session.ConvertBlock(block);
            ushort view;
            if (!ValidateIndevPlace(p, lvl, x, y, z, raw, out view)) {
                cancel = true;
                p.RevertBlock(x, y, z);
            } else if (view != raw) {
                cancel = true;
                lvl.UpdateBlock(Player.Console, x, y, z, Block.FromRaw(view));
            }
        }

        /// <summary> Validates an Indev-map placement and picks the view id that
        /// actually enters the world (facing/mount variants). False = refuse. </summary>
        static bool ValidateIndevPlace(Player p, Level lvl, int x, int y, int z,
                                       ushort raw, out ushort view) {
            view = raw;

            // ids 50-65 that hold no Indev block (turquoise wool, ice, pillar,
            // crate, stone brick) don't exist in this world - refused, like the
            // client's CanPlace=false on its nonGenuine list
            if (raw > Block.CLASSIC_MAX_BLOCK && raw <= Block.CPE_MAX_BLOCK &&
                !SurvivalBlocks.IsIndevBlock(raw)) return false;

            // BlockTorch: canPlaceBlockAt needs a support; onBlockAdded's auto
            // wall-pick mounts it (the clicked-face override needs face info the
            // classic place packet doesn't carry - a documented deviation when
            // several supports exist)
            if (raw == SurvivalBlocks.TORCH) {
                int meta = TorchAutoMeta(lvl, x, y, z);
                if (meta == 0) return false;
                if (meta != 5) view = (ushort)(SurvivalBlocks.TORCH_W1 + meta - 1);
                return true;
            }
            // a wall-torch view placed directly still needs some support
            if (raw >= SurvivalBlocks.TORCH_W1 && raw <= SurvivalBlocks.TORCH_W4) {
                return TorchAutoMeta(lvl, x, y, z) != 0;
            }

            // BlockChest.canPlaceBlockAt: at most ONE neighbouring chest, and
            // never one that is already half of a double
            if (raw == SurvivalBlocks.CHEST) {
                bool paired = false;
                int n = ChestNeighbour(lvl, x - 1, y, z, ref paired)
                      + ChestNeighbour(lvl, x + 1, y, z, ref paired)
                      + ChestNeighbour(lvl, x, y, z - 1, ref paired)
                      + ChestNeighbour(lvl, x, y, z + 1, ref paired);
                if (n > 1 || paired) return false;
                view = SurvivalBlocks.FacingVariant(raw, YawFacingMeta(p));
                return true;
            }

            // BlockFurnace.setDefaultDirection: face the placer
            if (raw == SurvivalBlocks.FURNACE || raw == SurvivalBlocks.FURNACE_LIT) {
                view = SurvivalBlocks.FacingVariant(raw, YawFacingMeta(p));
                return true;
            }
            return true;
        }

        // The client's yaw-quadrant facing pick (IndevTest_BlockChanged):
        // floor(yaw * 4/360 + 0.5) & 3 -> Indev metadata 3/4/2/5. Yaw rides
        // the wire as a byte, so *4/360 degrees = *4/256 raw.
        static int YawFacingMeta(Player p) {
            int q = ((p.Rot.RotY * 4 + 128) >> 8) & 3;
            return q == 0 ? 3 : q == 1 ? 4 : q == 2 ? 2 : 5;
        }

        // World.isBlockNormalCube approximation: solid collide + light-blocking
        // (glass, leaves, plants, slabs and the non-cube customs all pass
        // light, so they can't hold a torch - matching genuine)
        static bool NormalCube(Level lvl, int x, int y, int z) {
            if (x < 0 || y < 0 || z < 0 || x >= lvl.Width || y >= lvl.Height || z >= lvl.Length) return false;
            BlockID b = lvl.GetBlock((ushort)x, (ushort)y, (ushort)z);
            if (Block.Convert(b) == Block.Slab) return false; // half height
            return CollideType.IsSolid(lvl.CollideType(b)) && !lvl.LightPasses(b);
        }

        // BlockTorch.onBlockAdded's wall-pick: first solid neighbour in the
        // genuine -X, +X, -Z, +Z, floor order -> metadata 1/2/3/4/5 (0 = none)
        static int TorchAutoMeta(Level lvl, int x, int y, int z) {
            if (NormalCube(lvl, x - 1, y, z)) return 1;
            if (NormalCube(lvl, x + 1, y, z)) return 2;
            if (NormalCube(lvl, x, y, z - 1)) return 3;
            if (NormalCube(lvl, x, y, z + 1)) return 4;
            if (NormalCube(lvl, x, y - 1, z)) return 5;
            return 0;
        }

        // one arm of BlockChest.isThereANeighborChest: the cell holds a chest,
        // and `paired` picks up whether that chest already touches another
        static int ChestNeighbour(Level lvl, int x, int y, int z, ref bool paired) {
            if (!IsChestView(RawAt(lvl, x, y, z))) return 0;
            if (IsChestView(RawAt(lvl, x - 1, y, z)) || IsChestView(RawAt(lvl, x + 1, y, z)) ||
                IsChestView(RawAt(lvl, x, y, z - 1)) || IsChestView(RawAt(lvl, x, y, z + 1)))
                paired = true;
            return 1;
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
