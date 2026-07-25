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
    /// <summary> Server-authoritative Indev world growth: the genuine
    /// World.tick random-block-update loop (volume/200 picks per tick) driving
    /// crop growth, farmland hydration, sapling->tree growth and grass spread,
    /// gated on a faithful light model (sky-light heightmap + a block-light
    /// flood from torches/lava). A ported-to-C# mirror of the client's
    /// src/IndevTest.c growth handlers; every block change is emitted through
    /// lvl.UpdateBlock so it streams to every viewer as an ordinary SetBlock
    /// (no new wire needed). Runs on Indev maps only - c0.30 keeps the classic
    /// engine physics, and the client's own local growth loop is gated off
    /// under server drive so growth is authoritative and never doubled. </summary>
    /// <remarks>
    /// Lighting parity: the client renders with a cached heightmap; the server
    /// scans each queried column top-down instead (growth queries are sparse -
    /// only the volume/200 picks that land on a growth block ever ask), so no
    /// heightmap needs storing or invalidating. Block light IS stored per level
    /// (one byte per cell) and re-flooded lazily at most once a second, which
    /// keeps torch/lava light responsive without hooking every block change.
    /// The eased sky light (day 15 / night 4) comes from SurvivalNet, the same
    /// day/night input the mob sim's darkness rule already uses.
    /// </remarks>
    internal static class SurvivalGrowth
    {
        // ==================== per-level state ====================

        sealed class LevelGrowth
        {
            // genuine World.tick random-update accumulator + LCG pick stream
            public long UpdateLCG;
            public uint  RandId = 1;
            public readonly Random Rng = new Random();

            // block-light flood cache (one byte per cell, 0..15), re-flooded lazily
            public byte[] BlockLight;
            public int    LightVolume;
            public int    FloodCountdown; // ticks until the next lazy re-flood (0 => flood now)
            public bool   LightDirty = true; // a block changed since the last flood (start dirty)
        }

        static readonly Dictionary<Level, LevelGrowth> registry = new Dictionary<Level, LevelGrowth>();
        static readonly object registryLock = new object();

        // re-flood the block-light cache at most this often (20 TPS => ~1s);
        // plant growth spans many seconds so <=1s light latency is invisible.
        const int FLOOD_INTERVAL = 20;

        static LevelGrowth GetLevel(Level lvl, bool create) {
            lock (registryLock) {
                LevelGrowth g;
                if (registry.TryGetValue(lvl, out g)) return g;
                if (!create) return null;
                g = new LevelGrowth();
                registry[lvl] = g;
                return g;
            }
        }

        public static void Prune(Level[] loaded) {
            lock (registryLock) {
                List<Level> dead = null;
                foreach (KeyValuePair<Level, LevelGrowth> kvp in registry)
                {
                    if (Array.IndexOf(loaded, kvp.Key) < 0) {
                        if (dead == null) dead = new List<Level>();
                        dead.Add(kvp.Key);
                    }
                }
                if (dead != null) foreach (Level lvl in dead) registry.Remove(lvl);
            }
        }


        // ==================== view-id helpers ====================

        // Reads the flattened Indev view id stored at a cell (0 = air / OOB).
        internal static ushort ViewAt(Level lvl, int x, int y, int z) {
            if (x < 0 || y < 0 || z < 0 || x >= lvl.Width || y >= lvl.Height || z >= lvl.Length) return 0;
            return Block.ToRaw(Block.Convert(lvl.GetBlock((ushort)x, (ushort)y, (ushort)z)));
        }

        // Writes a view id back to the world, streaming it to every viewer as a
        // plain SetBlock (Player.Console == a server-authored change, no BlockDB
        // manual-place flag). Matches the existing survival server block writes.
        // Every server-authored change is announced to SurvivalPhysics so fire
        // and finite fluids schedule/validate off it (the genuine notify hook).
        internal static void SetView(Level lvl, int x, int y, int z, ushort view) {
            if (x < 0 || y < 0 || z < 0 || x >= lvl.Width || y >= lvl.Height || z >= lvl.Length) return;
            ushort old = ViewAt(lvl, x, y, z);
            if (old == view) return;
            lvl.UpdateBlock(Player.Console, (ushort)x, (ushort)y, (ushort)z, Block.FromRaw((BlockID)view));
            SurvivalPhysics.Notify(lvl, x, y, z, old, view);
            MarkLightDirty(lvl);
        }

        /// <summary> A block on the level changed, so the block-light flood cache may
        /// be stale. The tick refloods dirty levels at most once a second; a quiet
        /// level refloods never. Cheap + unlocked by design (a torn write only costs
        /// one missed/extra reflood). Player edits route here via
        /// SurvivalPhysics.OnBlockChanged. </summary>
        internal static void MarkLightDirty(Level lvl) {
            LevelGrowth g = GetLevel(lvl, false);
            if (g != null) g.LightDirty = true;
        }

        static bool IsCrops(ushort v)    { return v >= SurvivalBlocks.CROPS_0 && v <= SurvivalBlocks.CROPS_7; }
        static bool IsFarmland(ushort v) { return v == SurvivalBlocks.FARMLAND || v == SurvivalBlocks.FARMLAND_WET; }

        // BlockFlower.canThisPlantGrowOnThisBlockID: grass, dirt or farmland.
        static bool PlantSoilOk(ushort below) {
            return below == Block.Grass || below == Block.Dirt || IsFarmland(below);
        }


        // ==================== lighting ====================
        //
        // Sky light: a top-down column scan finds the highest sky blocker, then
        // IsLit(x,y,z) == y > heightmap. Normal opaque cubes "shade from below"
        // so the heightmap parks one cell under them (the top block itself reads
        // as lit - grass on the surface must count as lit to spread); leaves and
        // water clear that offset (they cast a shadow onto their own cell), as
        // the client does in IndevTest.c. Block light: a stored flood cache.

        // Whether a view id ends the sky top-scan. Sprites (crops/torch/fire/
        // flowers/mushrooms/sapling), glass and air pass the sky; everything
        // else - solid cubes, leaves (Indev override), water, lava, farmland,
        // the container/machine defs - blocks it.
        static bool BlocksSky(ushort v) {
            if (v == Block.Air) return false;
            switch (v) {
                case Block.Sapling: case Block.Rose: case Block.Dandelion:
                case Block.Mushroom: case Block.RedMushroom: case Block.Rope:
                case Block.Glass:
                    return false;
            }
            if (v == SurvivalBlocks.TORCH || v == SurvivalBlocks.FIRE || v == SurvivalBlocks.GEARS) return false;
            if (v >= SurvivalBlocks.CROPS_0  && v <= SurvivalBlocks.CROPS_7)  return false;
            if (v >= SurvivalBlocks.TORCH_W1 && v <= SurvivalBlocks.TORCH_W4) return false;
            return true;
        }

        // leaves and water sit AT the heightmap (shadow their own cell); every
        // other blocker shades from below, parking the heightmap one cell lower.
        static bool ShadesFromBelow(ushort v) {
            return !(v == Block.Leaves || v == Block.Water || v == Block.StillWater);
        }

        static int Heightmap(Level lvl, int x, int z) {
            for (int y = lvl.Height - 1; y >= 0; y--)
            {
                ushort v = ViewAt(lvl, x, y, z);
                if (BlocksSky(v)) return ShadesFromBelow(v) ? y - 1 : y;
            }
            return -1;
        }

        static bool IsLit(Level lvl, int x, int y, int z) {
            return y > Heightmap(lvl, x, z);
        }

        // Genuine getBlockLightValue: the greater of the (eased) sky light where
        // the cell sees the sky, and the flooded block light from lamps.
        static int LightLevel(Level lvl, LevelGrowth g, int x, int y, int z) {
            int sky = IsLit(lvl, x, y, z) ? SurvivalNet.CurrentSkyLight(lvl) : 0;
            int lamp = BlockLightAt(g, lvl, x, y, z);
            return sky > lamp ? sky : lamp;
        }

        static bool GrowLightOk(Level lvl, LevelGrowth g, int x, int y, int z) {
            return y < lvl.Height && LightLevel(lvl, g, x, y, z) >= 9;
        }

        /// <summary> The combined light level (0-15) at a cell - sky-if-lit vs the
        /// block-light flood - for callers outside growth (the mob AI's Indev
        /// wander weighting and spider light-flee). Uses the same cached flood. </summary>
        internal static int LightAt(Level lvl, int x, int y, int z) {
            return LightLevel(lvl, GetLevel(lvl, true), x, y, z);
        }


        // ==================== block-light flood ====================
        //
        // A BFS from every emitter, attenuating 1 per cell and only spreading
        // into cells that pass light (open air, sprites, crops). Torch 14,
        // fire/lava 15, lit furnace 13 - the genuine lamp values. Re-flooded
        // lazily (>=1s stale) so a placed torch lights crops within a second.

        static int Emission(ushort v) {
            if (v == SurvivalBlocks.TORCH) return 14;
            if (v >= SurvivalBlocks.TORCH_W1 && v <= SurvivalBlocks.TORCH_W4) return 14;
            if (v == SurvivalBlocks.FIRE) return 15;
            if (v == SurvivalBlocks.LAVA_SOURCE) return 15;
            if (v == Block.Lava || v == Block.StillLava) return 15;
            if (v == SurvivalBlocks.FURNACE_LIT) return 13;
            if (v >= SurvivalBlocks.FURNL_V0 && v <= SurvivalBlocks.FURNL_V0 + 3) return 13;
            return 0;
        }

        // whether block light spreads INTO a cell (open air / sprite / crop).
        static bool LightPasses(ushort v) {
            return !BlocksSky(v);
        }

        static int BlockLightAt(LevelGrowth g, Level lvl, int x, int y, int z) {
            byte[] bl = g.BlockLight;
            if (bl == null) return 0;
            if (x < 0 || y < 0 || z < 0 || x >= lvl.Width || y >= lvl.Height || z >= lvl.Length) return 0;
            int idx = (y * lvl.Length + z) * lvl.Width + x;
            if (idx < 0 || idx >= bl.Length) return 0;
            return bl[idx];
        }

        // Full re-flood of the level's block-light cache. Cheap when emitters are
        // few (BFS stays local); a lava lake floods farther but each cell is set
        // once via the array, so the whole pass is bounded by the lit volume.
        static void RefloodBlockLight(Level lvl, LevelGrowth g) {
            int w = lvl.Width, h = lvl.Height, d = lvl.Length;
            int vol = w * h * d;
            if (g.BlockLight == null || g.LightVolume != vol) {
                g.BlockLight = new byte[vol];
                g.LightVolume = vol;
            }
            byte[] bl = g.BlockLight;
            Array.Clear(bl, 0, vol);

            // seed emitters
            Queue<int> queue = null;
            for (int y = 0; y < h; y++)
                for (int z = 0; z < d; z++)
                    for (int x = 0; x < w; x++)
                    {
                        ushort v = ViewAt(lvl, x, y, z);
                        int e = Emission(v);
                        if (e <= 0) continue;
                        int idx = (y * d + z) * w + x;
                        if (e <= bl[idx]) continue;
                        bl[idx] = (byte)e;
                        if (queue == null) queue = new Queue<int>();
                        queue.Enqueue(idx);
                    }
            if (queue == null) return; // no lamps: cache stays all-zero

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int level = bl[idx];
                if (level <= 1) continue;
                int x = idx % w;
                int z = (idx / w) % d;
                int y = idx / (w * d);
                int nl = level - 1;

                Spread(lvl, bl, queue, x - 1, y, z, nl, w, h, d);
                Spread(lvl, bl, queue, x + 1, y, z, nl, w, h, d);
                Spread(lvl, bl, queue, x, y - 1, z, nl, w, h, d);
                Spread(lvl, bl, queue, x, y + 1, z, nl, w, h, d);
                Spread(lvl, bl, queue, x, y, z - 1, nl, w, h, d);
                Spread(lvl, bl, queue, x, y, z + 1, nl, w, h, d);
            }
        }

        static void Spread(Level lvl, byte[] bl, Queue<int> queue,
                           int x, int y, int z, int nl, int w, int h, int d) {
            if (x < 0 || y < 0 || z < 0 || x >= w || y >= h || z >= d) return;
            int idx = (y * d + z) * w + x;
            if (nl <= bl[idx]) return;
            if (!LightPasses(ViewAt(lvl, x, y, z))) return; // opaque absorbs
            bl[idx] = (byte)nl;
            queue.Enqueue(idx);
        }


        // ==================== growth tick ====================

        /// <summary> One 20 TPS growth tick for an Indev level: pays out the
        /// genuine volume/200 random block updates and runs the matching growth
        /// handler on each pick. Called from the survival mob tick (Indev only,
        /// under lock(lm.Mobs)); no-op if the map has no viewers. </summary>
        public static void Tick(Level lvl) {
            int w = lvl.Width, h = lvl.Height, d = lvl.Length;
            if (w <= 0 || h <= 0 || d <= 0) return;

            LevelGrowth g = GetLevel(lvl, true);

            // refresh the block-light cache lazily (first tick, then once a second)
            // reflood only when a block actually changed since the last flood
            // (the countdown still rate-limits a busy level to ~1/s; a quiet
            // level skips the full-volume rescan entirely)
            if (g.FloodCountdown > 0) g.FloodCountdown--;
            if (g.FloodCountdown <= 0 && (g.LightDirty || g.BlockLight == null)) {
                g.LightDirty = false;
                RefloodBlockLight(lvl, g);
                g.FloodCountdown = FLOOD_INTERVAL;
            }

            // genuine power-of-two coordinate masks. The masks must span the NEXT
            // power of two (out-of-range picks are then skipped by the bounds
            // check): masking with dim-1 on a non-power-of-two dimension knocks
            // holes in the bit patterns, so most cells would never be picked.
            int shiftX = 1, shiftZ = 1, shiftY = 1;
            while ((1 << shiftX) < w) shiftX++;
            while ((1 << shiftZ) < d) shiftZ++;
            while ((1 << shiftY) < h) shiftY++;
            int maskX = (1 << shiftX) - 1, maskZ = (1 << shiftZ) - 1, maskY = (1 << shiftY) - 1;

            long volume = (long)w * h * d;
            g.UpdateLCG += volume;
            int count = (int)(g.UpdateLCG / 200);
            g.UpdateLCG -= (long)count * 200;
            if (count > volume) count = (int)volume; // safety clamp

            for (int i = 0; i < count; i++)
            {
                g.RandId = g.RandId * 3u + 1013904223u;
                uint bits = g.RandId >> 2;
                int x = (int)(bits & (uint)maskX);
                int z = (int)((bits >> shiftX) & (uint)maskZ);
                int y = (int)((bits >> (shiftX + shiftZ)) & (uint)maskY);
                if (x >= w || y >= h || z >= d) continue;

                ushort v = ViewAt(lvl, x, y, z);

                // genuine in-20100223: sand/gravel/dirt/still fluids have no
                // updateTick, so the classic random handlers must not run here.
                if (v == Block.Sand || v == Block.Gravel || v == Block.Dirt ||
                    v == Block.StillWater || v == Block.StillLava) continue;

                if (v == Block.Grass)        { TickGrass(lvl, g, x, y, z); continue; }
                if (v == Block.Leaves)       { TickLeaves(lvl, g, x, y, z); continue; }
                if (IsCrops(v))              { TickCrops(lvl, g, x, y, z, v); continue; }
                if (IsFarmland(v))           { TickFarmland(lvl, g, x, y, z, v); continue; }
                if (v == Block.Sapling)      { TickSapling(lvl, g, x, y, z); continue; }
                if (SurvivalPhysics.IsFire(v))        { SurvivalPhysics.RandomTickFire(lvl, x, y, z); continue; }
                if (SurvivalPhysics.IsMovingFluid(v)) { SurvivalPhysics.RandomTickFluid(lvl, x, y, z, v); continue; }
                if (SurvivalPhysics.IsSource(v))      { SurvivalPhysics.RandomTickSource(lvl, x, y, z, v); continue; }
            }
        }


        // ==================== handlers (ported from IndevTest.c) ====================

        // BlockGrass.updateTick: covered grass decays to dirt (1-in-4); lit grass
        // seeds a random nearby lit dirt block.
        static void TickGrass(Level lvl, LevelGrowth g, int x, int y, int z) {
            ushort above = (y + 1 < lvl.Height) ? ViewAt(lvl, x, y + 1, z) : (ushort)Block.Air;
            if (BlocksSky(above)) {
                if (g.Rng.Next(4) == 0) SetView(lvl, x, y, z, Block.Dirt);
                return;
            }
            if (!IsLit(lvl, x, y, z)) return;

            int tx = x + g.Rng.Next(3) - 1;
            int ty = y + g.Rng.Next(5) - 3;
            int tz = z + g.Rng.Next(3) - 1;
            if (tx < 0 || ty < 0 || tz < 0 || tx >= lvl.Width || ty >= lvl.Height || tz >= lvl.Length) return;
            if (ViewAt(lvl, tx, ty, tz) != Block.Dirt) return;
            if (!IsLit(lvl, tx, ty, tz)) return;
            SetView(lvl, tx, ty, tz, Block.Grass);
        }

        // BlockLeaves.updateTick: a leaf whose block below is non-solid and that
        // has no log within x+-2, y-1..y, z+-2 decays - dropping a sapling on a
        // 1-in-10 roll, then vanishing. Because it only fires when the block
        // below is not solid, a chopped canopy peels away from the bottom up.
        static void TickLeaves(Level lvl, LevelGrowth g, int x, int y, int z) {
            ushort below = y > 0 ? ViewAt(lvl, x, y - 1, z) : (ushort)Block.Air;
            if (CollideType.IsSolid(lvl.CollideType(Block.FromRaw((BlockID)below)))) return; // !isSolid gate

            for (int dx = x - 2; dx <= x + 2; dx++)
                for (int dy = y - 1; dy <= y; dy++)
                    for (int dz = z - 2; dz <= z + 2; dz++)
                        if (ViewAt(lvl, dx, dy, dz) == Block.Log) return; // log nearby - keep

            if (g.Rng.Next(10) == 0)
                SurvivalDrops.SpawnScatter(lvl, x + 0.5, y + 0.5, z + 0.5,
                                           (ushort)Block.Sapling, 1, SurvivalDrops.MinedDelay(lvl));
            SetView(lvl, x, y, z, Block.Air);
        }

        // BlockCrops.updateTick: the stay check first, then a farmland-weighted,
        // crowding-halved growth roll advances the stage.
        static void TickCrops(Level lvl, LevelGrowth g, int x, int y, int z, ushort block) {
            int light = LightLevel(lvl, g, x, y, z);
            ushort below = y > 0 ? ViewAt(lvl, x, y - 1, z) : (ushort)Block.Air;
            bool stay = (light >= 8 || (light >= 4 && IsLit(lvl, x, y, z))) && IsFarmland(below);
            if (!stay) { PopCrop(lvl, g, x, y, z, block); return; }

            if (block >= SurvivalBlocks.CROPS_7) return;
            if (y + 1 >= lvl.Height || !GrowLightOk(lvl, g, x, y + 1, z)) return;

            float rate = 1.0f;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    int bx = x + dx, bz = z + dz, by = y - 1;
                    if (bx < 0 || by < 0 || bz < 0 || bx >= lvl.Width || by >= lvl.Height || bz >= lvl.Length) continue;
                    ushort b = ViewAt(lvl, bx, by, bz);
                    float f = 0.0f;
                    if (b == SurvivalBlocks.FARMLAND)     f = 1.0f;
                    if (b == SurvivalBlocks.FARMLAND_WET) f = 3.0f;
                    if (dx != 0 || dz != 0) f /= 4.0f;
                    rate += f;
                }

            bool rowX = CropAt(lvl, x - 1, y, z)     || CropAt(lvl, x + 1, y, z);
            bool rowZ = CropAt(lvl, x, y, z - 1)     || CropAt(lvl, x, y, z + 1);
            bool diag = CropAt(lvl, x - 1, y, z - 1) || CropAt(lvl, x + 1, y, z - 1) ||
                        CropAt(lvl, x + 1, y, z + 1) || CropAt(lvl, x - 1, y, z + 1);
            if (diag || (rowX && rowZ)) rate /= 2.0f;

            int bound = (int)(100.0f / rate);
            if (bound < 1) bound = 1;
            if (g.Rng.Next(bound) == 0) SetView(lvl, x, y, z, (ushort)(block + 1));
        }

        static bool CropAt(Level lvl, int x, int y, int z) {
            if (x < 0 || y < 0 || z < 0 || x >= lvl.Width || y >= lvl.Height || z >= lvl.Length) return false;
            return IsCrops(ViewAt(lvl, x, y, z));
        }

        // BlockCrops.idDropped: mature crops drop 1 wheat when they pop; younger
        // stages drop nothing (seeds only come from mining).
        static void PopCrop(Level lvl, LevelGrowth g, int x, int y, int z, ushort crop) {
            if (crop == SurvivalBlocks.CROPS_7)
                SurvivalDrops.SpawnScatter(lvl, x + 0.5, y + 0.5, z + 0.5,
                                           (ushort)(256 + 40), 1, SurvivalDrops.MinedDelay(lvl)); // Item.wheat
            SetView(lvl, x, y, z, Block.Air);
        }

        // BlockFarmland.updateTick (1-in-5 per random tick): hydrate from nearby
        // water; dry out; revert to dirt once dry with nothing planted above.
        static void TickFarmland(Level lvl, LevelGrowth g, int x, int y, int z, ushort block) {
            if (g.Rng.Next(5) != 0) return;

            ushort above = y + 1 < lvl.Height ? ViewAt(lvl, x, y + 1, z) : (ushort)Block.Air;
            // above the top layer is open sky, never "solid" (an unguarded GetBlock
            // there reads Block.Invalid, which counts as solid and reverted it)
            bool solidAbove = y + 1 < lvl.Height &&
                CollideType.IsSolid(lvl.CollideType(lvl.GetBlock((ushort)x, (ushort)(y + 1), (ushort)z)));
            if (solidAbove) {
                SetView(lvl, x, y, z, Block.Dirt);
                return;
            }
            if (WaterNear(lvl, x, y, z)) {
                if (block != SurvivalBlocks.FARMLAND_WET) SetView(lvl, x, y, z, SurvivalBlocks.FARMLAND_WET);
                return;
            }
            if (block == SurvivalBlocks.FARMLAND_WET) {
                SetView(lvl, x, y, z, SurvivalBlocks.FARMLAND); // moisture decays
                return;
            }
            if (!IsCrops(above)) SetView(lvl, x, y, z, Block.Dirt); // dry + nothing planted
        }

        // BlockFarmland moisture test: any water within x/z +-4, at y or y+1.
        static bool WaterNear(Level lvl, int x, int y, int z) {
            for (int wx = x - 4; wx <= x + 4; wx++)
                for (int wy = y; wy <= y + 1; wy++)
                    for (int wz = z - 4; wz <= z + 4; wz++)
                    {
                        if (wx < 0 || wy < 0 || wz < 0 || wx >= lvl.Width || wy >= lvl.Height || wz >= lvl.Length) continue;
                        ushort b = ViewAt(lvl, wx, wy, wz);
                        if (b == Block.Water || b == Block.StillWater) return true;
                    }
            return false;
        }

        // BlockSapling.updateTick: the flower stay check first, then with
        // light(x,y+1,z) >= 9 and the genuine 1-in-5 cadence the stage climbs to
        // 16 before a tree is attempted. The server has no per-cell metadata, so
        // the 16-step counter is collapsed into a single 1-in-16 roll after the
        // 1-in-5 gate - the same mean time-to-grow, no stored stage needed.
        static void TickSapling(Level lvl, LevelGrowth g, int x, int y, int z) {
            if (FlowerStayCheck(lvl, g, x, y, z, Block.Sapling)) return;
            if (y + 1 >= lvl.Height || LightLevel(lvl, g, x, y + 1, z) < 9) return;
            if (g.Rng.Next(5)  != 0) return;
            if (g.Rng.Next(16) != 0) return;

            SetView(lvl, x, y, z, Block.Air);
            if (!GrowTree(lvl, g, x, y, z)) SetView(lvl, x, y, z, Block.Sapling);
        }

        // BlockFlower.canBlockStay: light >= 8, or light >= 4 with open sky, on
        // grass/dirt/farmland. Pops the plant (drops itself) otherwise; returns
        // whether it popped.
        static bool FlowerStayCheck(Level lvl, LevelGrowth g, int x, int y, int z, ushort block) {
            int light = LightLevel(lvl, g, x, y, z);
            ushort below = y > 0 ? ViewAt(lvl, x, y - 1, z) : (ushort)Block.Air;
            if ((light >= 8 || (light >= 4 && IsLit(lvl, x, y, z))) && PlantSoilOk(below)) return false;

            SurvivalDrops.SpawnScatter(lvl, x + 0.5, y + 0.5, z + 0.5, block, 1, SurvivalDrops.MinedDelay(lvl));
            SetView(lvl, x, y, z, Block.Air);
            return true;
        }

        // World.growTrees runtime port: trunk rand(3)+4, a clearance envelope,
        // grass/dirt below (converted to dirt), the diamond canopy with the
        // corner trim, then the trunk logs.
        static bool GrowTree(Level lvl, LevelGrowth g, int x, int y, int z) {
            int trunkH = g.Rng.Next(3) + 4;
            if (y <= 0 || y + trunkH + 1 > lvl.Height) return false;

            for (int yy = y; yy <= y + 1 + trunkH; yy++)
            {
                int clearance = 1;
                if (yy == y) clearance = 0;
                if (yy >= y + 1 + trunkH - 2) clearance = 2;

                for (int xx = x - clearance; xx <= x + clearance; xx++)
                    for (int zz = z - clearance; zz <= z + clearance; zz++)
                    {
                        if (xx < 0 || yy < 0 || zz < 0 || xx >= lvl.Width || yy >= lvl.Height || zz >= lvl.Length) return false;
                        if (ViewAt(lvl, xx, yy, zz) != Block.Air) return false;
                    }
            }

            ushort below = ViewAt(lvl, x, y - 1, z);
            if (below != Block.Grass && below != Block.Dirt) return false;
            if (y >= lvl.Height - trunkH - 1)                return false;
            SetView(lvl, x, y - 1, z, Block.Dirt);

            for (int yy = y - 3 + trunkH; yy <= y + trunkH; yy++)
            {
                int dy = yy - (y + trunkH);
                int radius = 1 - dy / 2;
                for (int xx = x - radius; xx <= x + radius; xx++)
                {
                    int dxa = xx - x; if (dxa < 0) dxa = -dxa;
                    for (int zz = z - radius; zz <= z + radius; zz++)
                    {
                        int dza = zz - z; if (dza < 0) dza = -dza;
                        if (dxa == radius && dza == radius && (g.Rng.Next(2) == 0 || dy == 0)) continue;
                        if (xx < 0 || yy < 0 || zz < 0 || xx >= lvl.Width || yy >= lvl.Height || zz >= lvl.Length) continue;
                        if (!IsFullOpaque(ViewAt(lvl, xx, yy, zz))) SetView(lvl, xx, yy, zz, Block.Leaves);
                    }
                }
            }

            for (int yy = 0; yy < trunkH; yy++)
            {
                if (!IsFullOpaque(ViewAt(lvl, x, y + yy, z))) SetView(lvl, x, y + yy, z, Block.Log);
            }
            return true;
        }

        // The tree canopy only overwrites non-full-opaque cells (air/leaves/
        // sprites), never carving through solid stone/wood already there.
        static bool IsFullOpaque(ushort v) {
            if (v == Block.Air) return false;
            if (v == Block.Leaves) return false; // leaves block sky but are not full-opaque
            return BlocksSky(v);
        }
    }
}
