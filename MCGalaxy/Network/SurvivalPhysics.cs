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
    /// <summary> Server-authoritative Indev block physics: BlockFire (spread,
    /// burn-out, flint) and the genuine FINITE fluids (BlockFlowing/Stationary -
    /// spread-down-then-one-horizontal, volume-conserving donor removal, infinite
    /// springs, stagnation/petrify/evaporate, water<->lava/fire interactions).
    /// Ports of the client's IndevFire.c and IndevTest.c fluid section, driven off
    /// the 20 TPS survival tick and streamed to viewers as ordinary SetBlock.
    /// Indev maps only; every change flows through SurvivalGrowth.SetView so it is
    /// broadcast and re-announced back here via Notify (the genuine notify hook). </summary>
    /// <remarks>
    /// Genuine Minecraft drives fire and fluids off World's scheduled-update list;
    /// the client mirrors that with inline recursion through its block-change hook.
    /// The server uses the scheduled model directly (Notify only ENQUEUES work,
    /// never sets blocks inline) so a lake waking or a fire field collapsing can
    /// never blow the stack - it just spreads the work across ticks, which is what
    /// genuine does anyway. TNT caught by fire is consumed but not detonated:
    /// block-destroying explosions aren't ported yet (same limit as CreeperExplode).
    /// </remarks>
    internal static class SurvivalPhysics
    {
        const ushort FIRE       = SurvivalBlocks.FIRE;         // 51
        const ushort WATER_SRC  = SurvivalBlocks.WATER_SOURCE; // 52
        const ushort LAVA_SRC   = SurvivalBlocks.LAVA_SOURCE;  // 53

        internal static bool IsFire(ushort v)        { return v == FIRE; }
        internal static bool IsMovingFluid(ushort v) { return v == Block.Water || v == Block.Lava; }
        internal static bool IsSource(ushort v)      { return v == WATER_SRC || v == LAVA_SRC; }

        static bool IsWaterMat(ushort b) { return b == Block.Water || b == Block.StillWater || b == WATER_SRC; }
        static bool IsLavaMat(ushort b)  { return b == Block.Lava  || b == Block.StillLava  || b == LAVA_SRC; }

        // ==================== per-level state ====================

        struct FireEntry { public int Index; public int Time; }
        struct FluidEntry { public int Index; public int Delay; public bool Water; public bool Activate; }

        sealed class LevelPhys
        {
            public byte[] FireAge;                       // one nibble (0-15) per cell
            public int    FireVol;
            public readonly Queue<FireEntry> FireQueue = new Queue<FireEntry>();

            public readonly List<FluidEntry> Fluid = new List<FluidEntry>();
            public readonly HashSet<int> FluidPending = new HashSet<int>(); // dedup by index

            public readonly Random Rng = new Random();
            public readonly int[] LiquidOrder = { 0, 1, 2, 3 };

            // flood-fill scratch (genuine x + (z<<10) layer packing), lazily sized
            public ushort[] Stamps; public int StampsLen; public ushort Counter;
            public int[] StackA, StackB; public int StackCap;

            public bool Loaded; // setTickOnLoad scan done
        }

        static readonly Dictionary<Level, LevelPhys> registry = new Dictionary<Level, LevelPhys>();
        static readonly object registryLock = new object();

        const int FIRE_QUEUE_MAX = 8192;
        const int FLUID_SCHED_MAX = 8192;

        static LevelPhys Get(Level lvl, bool create) {
            lock (registryLock) {
                LevelPhys lp;
                if (registry.TryGetValue(lvl, out lp)) return lp;
                if (!create) return null;
                lp = new LevelPhys();
                registry[lvl] = lp;
                return lp;
            }
        }

        public static void Prune(Level[] loaded) {
            lock (registryLock) {
                List<Level> dead = null;
                foreach (KeyValuePair<Level, LevelPhys> kvp in registry)
                {
                    if (Array.IndexOf(loaded, kvp.Key) < 0) {
                        if (dead == null) dead = new List<Level>();
                        dead.Add(kvp.Key);
                    }
                }
                if (dead != null) foreach (Level lvl in dead) registry.Remove(lvl);
            }
        }


        // ==================== block helpers ====================

        static ushort View(Level lvl, int x, int y, int z) { return SurvivalGrowth.ViewAt(lvl, x, y, z); }
        static void   Set(Level lvl, int x, int y, int z, ushort v) { SurvivalGrowth.SetView(lvl, x, y, z, v); }
        static bool   In(Level lvl, int x, int y, int z) {
            return x >= 0 && y >= 0 && z >= 0 && x < lvl.Width && y < lvl.Height && z < lvl.Length;
        }
        static int Pack(Level lvl, int x, int y, int z) { return (y * lvl.Length + z) * lvl.Width + x; }
        static void Unpack(Level lvl, int idx, out int x, out int y, out int z) {
            x = idx % lvl.Width; z = (idx / lvl.Width) % lvl.Length; y = idx / (lvl.Width * lvl.Length);
        }

        // isBlockNormalCube proxy: a solid full cube (fire support / smoke test).
        static bool NormalCube(Level lvl, int x, int y, int z) {
            if (!In(lvl, x, y, z)) return false;
            return CollideType.IsSolid(lvl.CollideType(lvl.GetBlock((ushort)x, (ushort)y, (ushort)z)));
        }


        // ==================== the notify hook (enqueue only) ====================

        /// <summary> Announced for every server-authored block change on the level
        /// (SurvivalGrowth.SetView). Purely schedules work - never sets a block
        /// inline - so cascades spread across ticks instead of recursing. </summary>
        internal static void Notify(Level lvl, int x, int y, int z, ushort oldV, ushort newV) {
            if (lvl.Config.SurvivalMode != SurvivalMode.Indev) return;
            LevelPhys lp = Get(lvl, true);

            // Notify is reachable from PLAYER receive threads (OnBlockChanged) as
            // well as the tick thread (Set -> SetView -> Notify); the schedules are
            // plain Queue/List/HashSet, so every mutation serializes on the LevelPhys
            // monitor (re-entrant, so tick-thread nesting is fine).
            lock (lp) {
                if (newV == FIRE) { SetAge(lp, lvl, Pack(lvl, x, y, z), 0); ScheduleFire(lp, Pack(lvl, x, y, z)); }
                else if (oldV == FIRE) SetAge(lp, lvl, Pack(lvl, x, y, z), 0);

                if (newV == Block.Water || newV == Block.Lava) ScheduleFluid(lp, Pack(lvl, x, y, z), newV == Block.Water, false);

                // neighbours react: re-check adjacent fires, wake adjacent still fluids
                NotifyNeighbour(lp, lvl, x - 1, y, z);
                NotifyNeighbour(lp, lvl, x + 1, y, z);
                NotifyNeighbour(lp, lvl, x, y - 1, z);
                NotifyNeighbour(lp, lvl, x, y + 1, z);
                NotifyNeighbour(lp, lvl, x, y, z - 1);
                NotifyNeighbour(lp, lvl, x, y, z + 1);
            }
        }

        /// <summary> OnBlockChangedEvent: a player edit finished. Route it to the
        /// physics notify so placed fire/fluid come alive and mining next to a
        /// fluid/fire wakes the neighbours. The event carries no old block, but
        /// only the new block + neighbour reactions matter for scheduling. </summary>
        public static void OnBlockChanged(Player p, ushort x, ushort y, ushort z, ChangeResult result) {
            Level lvl = p.level;
            if (lvl == null || lvl.Config.SurvivalMode != SurvivalMode.Indev) return;
            if (result == ChangeResult.Unchanged) return;
            ushort now = SurvivalGrowth.ViewAt(lvl, x, y, z);
            // old block isn't carried by the event; only newV + neighbour
            // reactions drive scheduling, so Air is a safe stand-in.
            Notify(lvl, x, y, z, Block.Air, now);
            // a player edit may change the light (torch placed/mined, roof opened)
            SurvivalGrowth.MarkLightDirty(lvl);
        }

        static void NotifyNeighbour(LevelPhys lp, Level lvl, int x, int y, int z) {
            if (!In(lvl, x, y, z)) return;
            ushort b = View(lvl, x, y, z);
            if (b == FIRE) ScheduleFire(lp, Pack(lvl, x, y, z));
            else if (b == Block.StillWater) ScheduleFluid(lp, Pack(lvl, x, y, z), true, true);
            else if (b == Block.StillLava)  ScheduleFluid(lp, Pack(lvl, x, y, z), false, true);
        }


        // ==================== per-tick driver ====================

        /// <summary> setTickOnLoad: schedule pre-existing fire + moving fluid the
        /// first time a level ticks so a loaded/generated map comes alive. </summary>
        static void EnsureLoaded(LevelPhys lp, Level lvl) {
            if (lp.Loaded) return;
            lp.Loaded = true;
            int w = lvl.Width, h = lvl.Height, d = lvl.Length;
            for (int y = 0; y < h; y++)
                for (int z = 0; z < d; z++)
                    for (int x = 0; x < w; x++)
                    {
                        ushort v = View(lvl, x, y, z);
                        if (v == FIRE) ScheduleFire(lp, Pack(lvl, x, y, z));
                        else if (v == Block.Water) ScheduleFluid(lp, Pack(lvl, x, y, z), true, false);
                        else if (v == Block.Lava)  ScheduleFluid(lp, Pack(lvl, x, y, z), false, false);
                    }
        }

        /// <summary> One 20 TPS physics tick: process the fire queue (<=200/tick,
        /// genuine World.tick cap) and the due fluid schedule entries. </summary>
        public static void Tick(Level lvl) {
            LevelPhys lp = Get(lvl, true);
            lock (lp) { // serialize against player-thread Notify (see Notify)
                EnsureLoaded(lp, lvl);
                TickFire(lp, lvl);
                TickFluids(lp, lvl);
            }
        }


        // ==================== fire ====================

        static void EnsureFireAges(LevelPhys lp, Level lvl) {
            int vol = lvl.Width * lvl.Height * lvl.Length;
            if (lp.FireAge == null || lp.FireVol != vol) { lp.FireAge = new byte[vol]; lp.FireVol = vol; }
        }
        static int GetAge(LevelPhys lp, Level lvl, int index) {
            EnsureFireAges(lp, lvl);
            return (index >= 0 && index < lp.FireVol) ? lp.FireAge[index] : 0;
        }
        static void SetAge(LevelPhys lp, Level lvl, int index, int age) {
            EnsureFireAges(lp, lvl);
            if (index >= 0 && index < lp.FireVol) lp.FireAge[index] = (byte)(age & 15);
        }

        static void ScheduleFire(LevelPhys lp, int index) {
            if (lp.FireQueue.Count >= FIRE_QUEUE_MAX) return; // overflow self-heals
            lp.FireQueue.Enqueue(new FireEntry { Index = index, Time = 20 }); // BlockFire.tickRate
        }

        static void TickFire(LevelPhys lp, Level lvl) {
            int n = Math.Min(lp.FireQueue.Count, 200);
            for (int i = 0; i < n; i++)
            {
                FireEntry e = lp.FireQueue.Dequeue();
                if (e.Time > 0) {
                    e.Time--;
                    if (lp.FireQueue.Count < FIRE_QUEUE_MAX) lp.FireQueue.Enqueue(e);
                } else if (e.Index >= 0 && e.Index < lvl.Width * lvl.Height * lvl.Length &&
                           View(lvl, e.Index % lvl.Width, e.Index / (lvl.Width * lvl.Length), (e.Index / lvl.Width) % lvl.Length) == FIRE) {
                    int x, y, z; Unpack(lvl, e.Index, out x, out y, out z);
                    FireUpdate(lp, lvl, x, y, z);
                }
            }
        }

        internal static void RandomTickFire(Level lvl, int x, int y, int z) {
            LevelPhys lp = Get(lvl, true);
            if (View(lvl, x, y, z) != FIRE) return;
            lock (lp) FireUpdate(lp, lvl, x, y, z); // vs player-thread Notify
        }

        static readonly byte[] fireChance  = BuildFireTable(true);
        static readonly byte[] fireAbility = BuildFireTable(false);
        static byte[] BuildFireTable(bool chance) {
            byte[] t = new byte[256];
            // BlockFire.setBurnRate(chance, ability)
            t[Block.Wood]      = chance ? (byte)5  : (byte)20;
            t[Block.Log]       = chance ? (byte)5  : (byte)5;
            t[Block.Leaves]    = chance ? (byte)30 : (byte)60;
            t[Block.Bookshelf] = chance ? (byte)30 : (byte)20;
            t[Block.TNT]       = chance ? (byte)15 : (byte)100;
            for (int i = Block.Red; i <= Block.White; i++) t[i] = chance ? (byte)30 : (byte)60; // cloth 21-36
            return t;
        }
        static int FireChance(ushort b)  { return b < 256 ? fireChance[b]  : 0; }
        static int FireAbility(ushort b) { return b < 256 ? fireAbility[b] : 0; }
        static bool CanCatch(ushort b)   { return FireChance(b) > 0; }

        static bool CanBlockCatch(Level lvl, int x, int y, int z) {
            return In(lvl, x, y, z) && CanCatch(View(lvl, x, y, z));
        }
        static bool CanNeighbourCatch(Level lvl, int x, int y, int z) {
            return CanBlockCatch(lvl, x + 1, y, z) || CanBlockCatch(lvl, x - 1, y, z)
                || CanBlockCatch(lvl, x, y - 1, z) || CanBlockCatch(lvl, x, y + 1, z)
                || CanBlockCatch(lvl, x, y, z - 1) || CanBlockCatch(lvl, x, y, z + 1);
        }
        static int EncourageChance(Level lvl, int x, int y, int z, int cur) {
            int c = In(lvl, x, y, z) ? FireChance(View(lvl, x, y, z)) : 0;
            return c > cur ? c : cur;
        }

        const float TNT_BLAST_RADIUS = 4.0f; // c0.30/Indev PrimedTnt.explode radius

        // BlockFire.tryToCatchBlockOnFire: rand(bound) < ability consumes the block
        // (half fire, half air); caught TNT is removed and primed as a full-fuse
        // entity (BlockTNT.onBlockDestroyedByPlayer) rather than blowing instantly.
        static void FireTryCatch(LevelPhys lp, Level lvl, int x, int y, int z, int bound) {
            if (!In(lvl, x, y, z)) return;
            ushort b = View(lvl, x, y, z);
            int ability = FireAbility(b);
            if (lp.Rng.Next(bound) >= ability) return;
            if (b == Block.TNT) {
                Set(lvl, x, y, z, Block.Air);
                SurvivalTnt.Ignite(lvl, x, y, z, SurvivalTnt.DefaultFuse(lvl));
                return;
            }
            Set(lvl, x, y, z, lp.Rng.Next(2) == 0 ? FIRE : (ushort)Block.Air);
        }

        // BlockFire.updateTick, verbatim.
        static void FireUpdate(LevelPhys lp, Level lvl, int x, int y, int z) {
            int index = Pack(lvl, x, y, z);
            int meta = GetAge(lp, lvl, index);

            if (meta < 15) { SetAge(lp, lvl, index, meta + 1); ScheduleFire(lp, index); }

            if (!CanNeighbourCatch(lvl, x, y, z)) {
                if (!NormalCube(lvl, x, y - 1, z) || meta > 3) Set(lvl, x, y, z, Block.Air);
                return;
            }
            if (!CanBlockCatch(lvl, x, y - 1, z) && meta == 15 && lp.Rng.Next(4) == 0) {
                Set(lvl, x, y, z, Block.Air);
                return;
            }
            if (meta % 5 != 0 || meta <= 5) return;

            FireTryCatch(lp, lvl, x + 1, y, z, 300);
            FireTryCatch(lp, lvl, x - 1, y, z, 300);
            FireTryCatch(lp, lvl, x, y - 1, z, 100);
            FireTryCatch(lp, lvl, x, y + 1, z, 200);
            FireTryCatch(lp, lvl, x, y, z - 1, 300);
            FireTryCatch(lp, lvl, x, y, z + 1, 300);

            for (int xx = x - 1; xx <= x + 1; xx++)
                for (int zz = z - 1; zz <= z + 1; zz++)
                    for (int yy = y - 1; yy <= y + 4; yy++)
                    {
                        if (xx == x && yy == y && zz == z) continue;
                        int bound = 100;
                        if (yy > y + 1) bound += (yy - (y + 1)) * 100;
                        if (!In(lvl, xx, yy, zz) || View(lvl, xx, yy, zz) != Block.Air) continue;

                        int chance = EncourageChance(lvl, xx + 1, yy, zz, 0);
                        chance = EncourageChance(lvl, xx - 1, yy, zz, chance);
                        chance = EncourageChance(lvl, xx, yy - 1, zz, chance);
                        chance = EncourageChance(lvl, xx, yy + 1, zz, chance);
                        chance = EncourageChance(lvl, xx, yy, zz - 1, chance);
                        chance = EncourageChance(lvl, xx, yy, zz + 1, chance);
                        if (chance > 0 && lp.Rng.Next(bound) <= chance) Set(lvl, xx, yy, zz, FIRE);
                    }
        }

        // BlockFire.fireSpread fireCheck: existing fire counts, an air cell becomes
        // fire, anything else fails.
        static bool FireSpreadCheck(Level lvl, int x, int y, int z) {
            if (!In(lvl, x, y, z)) return false;
            ushort b = View(lvl, x, y, z);
            if (b == FIRE) return true;
            if (b != Block.Air) return false;
            Set(lvl, x, y, z, FIRE);
            return true;
        }

        // Lava flowing next to a flammable block lights the first free spot around
        // it (up, -x, +x, -z, +z, below) or turns the block itself to fire.
        static bool LavaFlowInto(Level lvl, int x, int y, int z) {
            if (!In(lvl, x, y, z) || !CanCatch(View(lvl, x, y, z))) return false;
            bool lit = FireSpreadCheck(lvl, x, y + 1, z)
                    || FireSpreadCheck(lvl, x - 1, y, z) || FireSpreadCheck(lvl, x + 1, y, z)
                    || FireSpreadCheck(lvl, x, y, z - 1) || FireSpreadCheck(lvl, x, y, z + 1)
                    || FireSpreadCheck(lvl, x, y - 1, z);
            if (!lit) Set(lvl, x, y, z, FIRE);
            return true;
        }


        // ==================== finite fluids ====================

        static void ScheduleFluid(LevelPhys lp, int index, bool water, bool activate) {
            if (lp.FluidPending.Contains(index)) return;
            if (lp.Fluid.Count >= FLUID_SCHED_MAX) return; // overflow self-heals
            int delay = activate ? 0 : (water ? 5 : 25); // BlockFlowing.tickRate
            lp.Fluid.Add(new FluidEntry { Index = index, Delay = delay, Water = water, Activate = activate });
            lp.FluidPending.Add(index);
        }

        internal static void RandomTickFluid(Level lvl, int x, int y, int z, ushort v) {
            LevelPhys lp = Get(lvl, true);
            lock (lp) FluidUpdate(lp, lvl, Pack(lvl, x, y, z), v); // vs player-thread Notify
        }

        // BlockSource.updateTick: an infinite spring - fill each of the 4
        // horizontal air neighbours with the flowing fluid (which then flows via
        // the fluid physics). The source itself just sits and refills.
        internal static void RandomTickSource(Level lvl, int x, int y, int z, ushort v) {
            ushort fluid = v == LAVA_SRC ? (ushort)Block.Lava : (ushort)Block.Water;
            if (In(lvl, x - 1, y, z) && View(lvl, x - 1, y, z) == Block.Air) Set(lvl, x - 1, y, z, fluid);
            if (In(lvl, x + 1, y, z) && View(lvl, x + 1, y, z) == Block.Air) Set(lvl, x + 1, y, z, fluid);
            if (In(lvl, x, y, z - 1) && View(lvl, x, y, z - 1) == Block.Air) Set(lvl, x, y, z - 1, fluid);
            if (In(lvl, x, y, z + 1) && View(lvl, x, y, z + 1) == Block.Air) Set(lvl, x, y, z + 1, fluid);
        }

        static void TickFluids(LevelPhys lp, Level lvl) {
            for (int i = 0; i < lp.Fluid.Count; )
            {
                FluidEntry e = lp.Fluid[i];
                if (e.Delay > 0) { e.Delay--; lp.Fluid[i] = e; i++; continue; }
                // swap-remove BEFORE running (the update may reschedule this cell)
                lp.Fluid[i] = lp.Fluid[lp.Fluid.Count - 1];
                lp.Fluid.RemoveAt(lp.Fluid.Count - 1);
                lp.FluidPending.Remove(e.Index);

                int x, y, z; Unpack(lvl, e.Index, out x, out y, out z);
                ushort b = View(lvl, x, y, z);
                if (e.Activate) {
                    if (b == Block.StillWater || b == Block.StillLava) ActivateStill(lp, lvl, e.Index, b);
                } else if (b == Block.Water || b == Block.Lava) {
                    FluidUpdate(lp, lvl, e.Index, b);
                }
            }
        }

        // BlockFluid.canFlow: target must be air or a non-colliding non-liquid
        // (plants/torch/fire/gears - flowing over them destroys them); water also
        // refuses within 2 blocks of a sponge.
        static bool CanFlowInto(Level lvl, bool water, int x, int y, int z) {
            if (!In(lvl, x, y, z)) return false;
            ushort b = View(lvl, x, y, z);
            if (b != Block.Air && lvl.CollideType(lvl.GetBlock((ushort)x, (ushort)y, (ushort)z)) != CollideType.WalkThrough)
                return false;
            if (water) {
                for (int sx = x - 2; sx <= x + 2; sx++)
                    for (int sy = y - 2; sy <= y + 2; sy++)
                        for (int sz = z - 2; sz <= z + 2; sz++)
                            if (In(lvl, sx, sy, sz) && View(lvl, sx, sy, sz) == Block.Sponge) return false;
            }
            return true;
        }

        // --- flood-fill scratch (genuine x + (z<<10) layer packing) ---
        static bool EnsureScratch(LevelPhys lp, Level lvl) {
            int stamps = 1024 * lvl.Length;
            int cap = lvl.Width * lvl.Length + 64;
            if (lp.Stamps != null && lp.StampsLen == stamps && lp.StackCap == cap) return true;
            lp.StampsLen = stamps; lp.StackCap = cap; lp.Counter = 0;
            lp.Stamps = new ushort[stamps];
            lp.StackA = new int[cap];
            lp.StackB = new int[cap];
            return true;
        }
        static void BumpCounter(LevelPhys lp) {
            if (++lp.Counter == 30000) { Array.Clear(lp.Stamps, 0, lp.StampsLen); lp.Counter = 1; }
        }

        static bool IsBody(ushort b, ushort moving, ushort still) { return b == moving || b == still; }

        // World.floodFill on one layer: 0 = adjacent air found (can absorb), 1 =
        // fully closed, 2 = touches the border.
        static int FloodFill(LevelPhys lp, Level lvl, int x, int y, int z, ushort moving, ushort still) {
            if (!In(lvl, x, y, z)) return 0;
            EnsureScratch(lp, lvl);
            BumpCounter(lp);
            ushort[] stamps = lp.Stamps; int[] stackA = lp.StackA;
            int W = lvl.Width, L = lvl.Length, H = lvl.Height;
            int top = 0;
            stackA[top++] = x + (z << 10);

            while (top > 0)
            {
                int p2 = stackA[--top];
                if (stamps[p2] == lp.Counter) continue;
                x = p2 & 1023; z = p2 >> 10;
                if (x == 0 || x == W - 1 || y == 0 || y == H - 1 || z == 0 || z == L - 1) return 2;

                while (x > 0 && stamps[p2 - 1] != lp.Counter && IsBody(View(lvl, x - 1, y, z), moving, still)) { x--; p2--; }
                if (x > 0 && View(lvl, x - 1, y, z) == Block.Air) return 0;

                bool spanN = false, spanS = false;
                for (; x < W && stamps[p2] != lp.Counter && IsBody(View(lvl, x, y, z), moving, still); x++, p2++)
                {
                    if (x == 0 || x == W - 1) return 2;
                    if (z > 0) {
                        ushort b = View(lvl, x, y, z - 1);
                        if (b == Block.Air) return 0;
                        bool match = stamps[p2 - 1024] != lp.Counter && IsBody(b, moving, still);
                        if (match && !spanN && top < lp.StackCap) stackA[top++] = p2 - 1024;
                        spanN = match;
                    }
                    if (z < L - 1) {
                        ushort b = View(lvl, x, y, z + 1);
                        if (b == Block.Air) return 0;
                        bool match = stamps[p2 + 1024] != lp.Counter && IsBody(b, moving, still);
                        if (match && !spanS && top < lp.StackCap) stackA[top++] = p2 + 1024;
                        spanS = match;
                    }
                    stamps[p2] = lp.Counter;
                }
                if (x < W && View(lvl, x, y, z) == Block.Air) return 0;
            }
            return 1;
        }

        // World.fluidFlowCheck: walk the body upward for a donor cell to pull
        // volume from. Returns packed ((y<<10|z)<<10)|x of the farthest cell on the
        // highest layer, -9999 when the body touches a SOURCE (infinite), or -1 OOB.
        static int FlowCheck(LevelPhys lp, Level lvl, int x, int y, int z, ushort moving, ushort still) {
            if (!In(lvl, x, y, z)) return -1;
            EnsureScratch(lp, lvl);
            int ox = x, oz = z, W = lvl.Width, L = lvl.Length, H = lvl.Height;
            ushort source = moving == Block.Water ? WATER_SRC : LAVA_SRC;
            int donor = ((y << 10 | z) << 10) | x;
            bool sourced = false;
            int[] stackA = lp.StackA, stackB = lp.StackB;
            ushort[] stamps = lp.Stamps;
            int top = 0;
            stackA[top++] = x + (z << 10);

            for (; y < H; y++)
            {
                int best = -1, upTop = 0;
                BumpCounter(lp);

                while (top > 0)
                {
                    int p2 = stackA[--top];
                    if (stamps[p2] == lp.Counter) continue;
                    x = p2 & 1023; z = p2 >> 10;
                    int dz2 = (z - oz) * (z - oz);

                    while (x > 0 && stamps[p2 - 1] != lp.Counter && IsBody(View(lvl, x - 1, y, z), moving, still)) { x--; p2--; }
                    if (x > 0 && View(lvl, x - 1, y, z) == source) sourced = true;

                    bool spanN = false, spanS = false, spanUp = false;
                    for (; x < W && stamps[p2] != lp.Counter && IsBody(View(lvl, x, y, z), moving, still); x++, p2++)
                    {
                        if (z > 0) {
                            ushort b = View(lvl, x, y, z - 1);
                            if (b == source) sourced = true;
                            bool match = stamps[p2 - 1024] != lp.Counter && IsBody(b, moving, still);
                            if (match && !spanN && top < lp.StackCap) stackA[top++] = p2 - 1024;
                            spanN = match;
                        }
                        if (z < L - 1) {
                            ushort b = View(lvl, x, y, z + 1);
                            if (b == source) sourced = true;
                            bool match = stamps[p2 + 1024] != lp.Counter && IsBody(b, moving, still);
                            if (match && !spanS && top < lp.StackCap) stackA[top++] = p2 + 1024;
                            spanS = match;
                        }
                        if (y < H - 1) {
                            bool match = IsBody(View(lvl, x, y + 1, z), moving, still);
                            if (match && !spanUp && upTop < lp.StackCap) stackB[upTop++] = p2;
                            spanUp = match;
                        }
                        int d = (x - ox) * (x - ox) + dz2;
                        if (d > best) { best = d; donor = ((y << 10 | z) << 10) | x; }
                        stamps[p2] = lp.Counter;
                    }
                    if (x < W && View(lvl, x, y, z) == source) sourced = true;
                }

                if (upTop == 0) break;
                int[] tmp = stackA; stackA = stackB; stackB = tmp;
                lp.StackA = stackA; lp.StackB = stackB;
                top = upTop;
            }
            return sourced ? -9999 : donor;
        }

        // BlockFlowing.liquidSpread: plain (stagnation-retry) spread.
        static bool FluidSpread(LevelPhys lp, Level lvl, ushort moving, bool water, int tx, int ty, int tz) {
            if (!CanFlowInto(lvl, water, tx, ty, tz)) return false;
            Set(lvl, tx, ty, tz, moving);
            return true;
        }

        // BlockFlowing.liquidSpread2: spread + donor removal (volume conservation).
        static bool FluidSpread2(LevelPhys lp, Level lvl, int x, int y, int z, ushort moving, ushort still,
                                 bool water, int tx, int ty, int tz) {
            if (!CanFlowInto(lvl, water, tx, ty, tz)) return false;

            int r = FlowCheck(lp, lvl, x, y, z, moving, still);
            if (r != -9999) {
                if (r < 0) return false;
                int dx = r & 1023; r >>= 10;
                int dz = r & 1023; r >>= 10;
                int dy = r & 1023;
                if ((dy > ty || !CanFlowInto(lvl, water, tx, ty - 1, tz)) && dy <= ty &&
                    dx != 0 && dx != lvl.Width - 1 && dz != 0 && dz != lvl.Length - 1) {
                    return false;
                }
                Set(lvl, dx, dy, dz, Block.Air);
            }
            Set(lvl, tx, ty, tz, moving);
            return true;
        }

        // water side effects: extinguish adjacent fire, petrify adjacent lava.
        static bool WaterContact(Level lvl, int x, int y, int z) {
            if (!In(lvl, x, y, z)) return false;
            ushort b = View(lvl, x, y, z);
            if (b == FIRE) { Set(lvl, x, y, z, Block.Air); return true; }
            if (b == Block.Lava || b == Block.StillLava) { Set(lvl, x, y, z, Block.Stone); return true; }
            return false;
        }

        // BlockFlowing.update - the whole genuine driver.
        static void FluidUpdate(LevelPhys lp, Level lvl, int index, ushort block) {
            int x, y, z; Unpack(lvl, index, out x, out y, out z);
            bool water = block == Block.Water;
            ushort moving = water ? (ushort)Block.Water : (ushort)Block.Lava;
            ushort still  = water ? (ushort)Block.StillWater : (ushort)Block.StillLava;

            bool canSide = CanFlowInto(lvl, water, x - 1, y, z) || CanFlowInto(lvl, water, x + 1, y, z) ||
                           CanFlowInto(lvl, water, x, y, z - 1) || CanFlowInto(lvl, water, x, y, z + 1);

            ushort below = y > 0 ? View(lvl, x, y - 1, z) : (ushort)Block.Air;
            if (canSide && y > 0 && (water ? IsWaterMat(below) : IsLavaMat(below))) {
                if (FloodFill(lp, lvl, x, y - 1, z, moving, still) == 1) {
                    int r = FlowCheck(lp, lvl, x, y, z, moving, still);
                    if (r != -9999) {
                        if (r < 0) return;
                        int dx = r & 1023; r >>= 10;
                        int dz = r & 1023; r >>= 10;
                        int dy = r & 1023;
                        Set(lvl, dx, dy, dz, Block.Air);
                    }
                    return;
                }
            }

            bool spread = FluidSpread2(lp, lvl, x, y, z, moving, still, water, x, y - 1, z);

            // one random horizontal direction per update (partial Fisher-Yates)
            for (int i = 0; i < 4; i++)
            {
                int j = lp.Rng.Next(4 - i) + i;
                int t = lp.LiquidOrder[i]; lp.LiquidOrder[i] = lp.LiquidOrder[j]; lp.LiquidOrder[j] = t;
                int dir = lp.LiquidOrder[i];
                if (!spread) {
                    if (dir == 0) spread = FluidSpread2(lp, lvl, x, y, z, moving, still, water, x - 1, y, z);
                    else if (dir == 1) spread = FluidSpread2(lp, lvl, x, y, z, moving, still, water, x + 1, y, z);
                    else if (dir == 2) spread = FluidSpread2(lp, lvl, x, y, z, moving, still, water, x, y, z - 1);
                    else if (dir == 3) spread = FluidSpread2(lp, lvl, x, y, z, moving, still, water, x, y, z + 1);
                }
            }

            if (!spread && canSide) {
                if (lp.Rng.Next(3) == 0) {
                    if (lp.Rng.Next(3) == 0) {
                        for (int i = 0; i < 4; i++)
                        {
                            int j = lp.Rng.Next(4 - i) + i;
                            int t = lp.LiquidOrder[i]; lp.LiquidOrder[i] = lp.LiquidOrder[j]; lp.LiquidOrder[j] = t;
                            int dir = lp.LiquidOrder[i];
                            if (!spread) {
                                if (dir == 0) spread = FluidSpread(lp, lvl, moving, water, x - 1, y, z);
                                else if (dir == 1) spread = FluidSpread(lp, lvl, moving, water, x + 1, y, z);
                                else if (dir == 2) spread = FluidSpread(lp, lvl, moving, water, x, y, z - 1);
                                else if (dir == 3) spread = FluidSpread(lp, lvl, moving, water, x, y, z + 1);
                            }
                        }
                    } else if (!water) {
                        Set(lvl, x, y, z, Block.Stone);
                    } else {
                        Set(lvl, x, y, z, Block.Air);
                    }
                }
                return;
            }

            if (water) {
                spread |= WaterContact(lvl, x - 1, y, z);
                spread |= WaterContact(lvl, x + 1, y, z);
                spread |= WaterContact(lvl, x, y, z - 1);
                spread |= WaterContact(lvl, x, y, z + 1);
            } else {
                spread |= LavaFlowInto(lvl, x - 1, y, z);
                spread |= LavaFlowInto(lvl, x + 1, y, z);
                spread |= LavaFlowInto(lvl, x, y, z - 1);
                spread |= LavaFlowInto(lvl, x, y, z + 1);
            }

            if (!spread) {
                Set(lvl, x, y, z, still); // settled: become still
            } else {
                ScheduleFluid(lp, index, water, false);
            }
        }

        // BlockStationary.onNeighborBlockChange: wake to moving when flow is
        // possible (or fire encourages, for lava); petrify on contact with the
        // opposite fluid.
        static readonly int[] NX = { -1, 1, 0, 0, 0, 0 };
        static readonly int[] NY = { 0, 0, -1, 1, 0, 0 };
        static readonly int[] NZ = { 0, 0, 0, 0, -1, 1 };
        static void ActivateStill(LevelPhys lp, Level lvl, int index, ushort block) {
            int x, y, z; Unpack(lvl, index, out x, out y, out z);
            bool water = block == Block.StillWater;

            for (int n = 0; n < 6; n++)
            {
                int nx = x + NX[n], ny = y + NY[n], nz = z + NZ[n];
                if (!In(lvl, nx, ny, nz)) continue;
                ushort nb = View(lvl, nx, ny, nz);
                if (water ? IsLavaMat(nb) : IsWaterMat(nb)) { Set(lvl, x, y, z, Block.Stone); return; }
            }

            bool wake = CanFlowInto(lvl, water, x, y - 1, z) ||
                        CanFlowInto(lvl, water, x - 1, y, z) || CanFlowInto(lvl, water, x + 1, y, z) ||
                        CanFlowInto(lvl, water, x, y, z - 1) || CanFlowInto(lvl, water, x, y, z + 1);
            if (!wake && !water) {
                for (int n = 0; n < 6; n++)
                {
                    int nx = x + NX[n], ny = y + NY[n], nz = z + NZ[n];
                    if (In(lvl, nx, ny, nz) && CanCatch(View(lvl, nx, ny, nz))) { wake = true; break; }
                }
            }
            if (!wake) return;

            Set(lvl, x, y, z, water ? (ushort)Block.Water : (ushort)Block.Lava);
        }
    }
}
