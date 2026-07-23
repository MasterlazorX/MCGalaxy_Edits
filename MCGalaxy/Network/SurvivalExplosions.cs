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
    /// <summary> Server-authoritative Indev explosion block destruction
    /// (World.createExplosion) - the terrain half of a creeper/TNT blast. A port
    /// of the client's Indev_CreateExplosion: 16^3 boundary rays seed a destroy
    /// set, paying each occupied cell its block resistance, then the marked
    /// blocks are cleared (30% Indev drop roll) in genuine descending order. The
    /// entity-damage half (density raycast) lives in SurvivalMobs.ExplodeAt,
    /// which owns the mob list; both together reproduce World.createExplosion.
    /// Block destruction is gated on the per-map SurvivalBlockDamage flag so
    /// protected builds can keep the entity damage but spare the terrain. </summary>
    internal static class SurvivalExplosions
    {
        // Block.getExplosionResistance() = resistance/5 (view ids; classic 1-49
        // map 1:1). Effective per-id values from Block.java's setResistance /
        // setHardness chains.
        static float Resistance(ushort b) {
            switch (b) {
                case Block.Stone: case Block.Cobblestone: case Block.Gold: case Block.Iron:
                case Block.DoubleSlab: case Block.Slab: case Block.Brick:
                case Block.MossyRocks: case Block.Obsidian:
                case SurvivalBlocks.DIAMOND_BLOCK: // 57
                    return 6.0f;
                case SurvivalBlocks.GEARS: return 0.5f; // 55
                case Block.Wood: return 3.0f;
                case Block.GoldOre: case Block.IronOre: case Block.CoalOre:
                case SurvivalBlocks.DIAMOND_ORE: // 56
                    return 3.0f;
                case Block.Log: return 2.0f;
                case Block.Bookshelf: return 1.5f;
                // BlockFluid resistance 6 raised to 500 by setHardness(100): water
                // and STILL lava are blast-proof; flowing lava keeps 6 -> 1.2.
                case Block.Water: case Block.StillWater: case Block.StillLava: return 100.0f;
                case Block.Lava: return 1.2f;
                case Block.Bedrock: return 3600000.0f;
                case SurvivalBlocks.FARMLAND: case SurvivalBlocks.FARMLAND_WET: return 0.6f; // 83/84
                case Block.Grass: case Block.Gravel: case Block.Sponge: return 0.6f;
                case Block.Dirt: case Block.Sand: return 0.5f;
                case Block.Glass: return 0.3f;
                case Block.Leaves: return 0.2f;
                case SurvivalBlocks.WORKBENCH: return 2.5f; // 58
                default:
                    if (b >= Block.Red && b <= Block.White) return 0.8f;          // cloth 21-36
                    if (b == SurvivalBlocks.CHEST || (b >= SurvivalBlocks.CHEST_V0 && b <= SurvivalBlocks.CHEST_V0 + 3))
                        return 2.5f;                                              // chest + facings
                    if (b == SurvivalBlocks.FURNACE || b == SurvivalBlocks.FURNACE_LIT ||
                        (b >= SurvivalBlocks.FURN_V0 && b <= SurvivalBlocks.FURNL_V0 + 3))
                        return 3.5f;                                              // furnaces
                    return 0.0f; // sapling/flowers/mushrooms/TNT/torch/fire/crops/air
            }
        }

        // +-16 window destroy-set bitset (ray reach maxes ~7 blocks). Reused each
        // call - the survival tick is single-threaded (under lock(lm.Mobs)).
        const int DIM = 33;
        static readonly byte[] bits = new byte[(DIM * DIM * DIM + 7) / 8];

        static ushort View(Level lvl, int x, int y, int z) { return SurvivalGrowth.ViewAt(lvl, x, y, z); }
        static bool In(Level lvl, int x, int y, int z) {
            return x >= 0 && y >= 0 && z >= 0 && x < lvl.Width && y < lvl.Height && z < lvl.Length;
        }

        /// <summary> The terrain half of World.createExplosion: collect the destroy
        /// set with 16^3 boundary rays, then clear the marked blocks (30% Indev
        /// drop roll) in descending order. No-op on maps with SurvivalBlockDamage
        /// off. Called under the survival tick lock. </summary>
        public static void DestroyBlocks(Level lvl, double cx, double cy, double cz, float r, Random rng) {
            if (!lvl.Config.SurvivalBlockDamage) return;
            Array.Clear(bits, 0, bits.Length);
            int icx = (int)cx, icy = (int)cy, icz = (int)cz;

            for (int i = 0; i < 16; i++)
                for (int j = 0; j < 16; j++)
                    for (int k = 0; k < 16; k++)
                    {
                        if (!(i == 0 || i == 15 || j == 0 || j == 15 || k == 0 || k == 15)) continue;
                        double dirx = i / 15.0 * 2.0 - 1.0, diry = j / 15.0 * 2.0 - 1.0, dirz = k / 15.0 * 2.0 - 1.0;
                        double len = Math.Sqrt(dirx * dirx + diry * diry + dirz * dirz);
                        dirx /= len; diry /= len; dirz /= len;

                        double power = r * (0.7 + rng.NextDouble() * 0.6);
                        double px = cx, py = cy, pz = cz;
                        while (power > 0.0) {
                            int bx = (int)px, by = (int)py, bz = (int)pz;
                            if (In(lvl, bx, by, bz)) {
                                ushort id = View(lvl, bx, by, bz);
                                if (id != Block.Air) power -= (Resistance(id) + 0.3) * 0.3;
                                if (power > 0.0) {
                                    int rel = (bx - icx + 16) + (by - icy + 16) * DIM + (bz - icz + 16) * DIM * DIM;
                                    if (rel >= 0 && rel < DIM * DIM * DIM) bits[rel >> 3] |= (byte)(1 << (rel & 7));
                                }
                            }
                            px += dirx * 0.3; py += diry * 0.3; pz += dirz * 0.3;
                            power -= 0.22500001;
                        }
                    }

            // destruction in genuine reverse-order (descending z, y, x)
            for (int k = DIM - 1; k >= 0; k--)
                for (int j = DIM - 1; j >= 0; j--)
                    for (int i = DIM - 1; i >= 0; i--)
                    {
                        int rel = i + j * DIM + k * DIM * DIM;
                        if ((bits[rel >> 3] & (1 << (rel & 7))) == 0) continue;
                        int bx = icx + i - 16, by = icy + j - 16, bz = icz + k - 16;
                        if (!In(lvl, bx, by, bz)) continue;
                        ushort id = View(lvl, bx, by, bz);
                        if (id == Block.Air) continue;

                        // TNT caught in a blast: cleared and re-primed as a fresh
                        // entity with a short randomized fuse (the classic chain
                        // reaction) instead of dropping an item.
                        if (id == Block.TNT) {
                            SurvivalGrowth.SetView(lvl, bx, by, bz, Block.Air);
                            SurvivalTnt.Ignite(lvl, bx, by, bz, SurvivalTnt.ChainFuse(lvl, rng));
                            continue;
                        }
                        ExplodeDrops(lvl, bx, by, bz, id, rng);
                        SurvivalInventory.ContainerRemovedIfAny(lvl, bx, by, bz, id); // chest/furnace scatter + TE cleanup
                        SurvivalGrowth.SetView(lvl, bx, by, bz, Block.Air);
                    }
        }

        // dropBlockAsItemWithChance(..., 0.3F) through the Indev idDropped table.
        static void ExplodeDrops(Level lvl, int x, int y, int z, ushort old, Random rng) {
            if (rng.NextDouble() > 0.3) return;
            ushort drop = old;
            switch (old) {
                case Block.Grass: drop = Block.Dirt; break;
                case Block.Stone: case Block.Obsidian: drop = Block.Cobblestone; break;
                case Block.CoalOre: drop = 256 + 7; break;
                case SurvivalBlocks.DIAMOND_ORE: drop = 256 + 8; break;
                case Block.Leaves:
                    if (rng.Next(10) != 0) return;
                    drop = Block.Sapling; break;
                case Block.Gravel:
                    drop = rng.Next(10) == 0 ? (ushort)(256 + 62) : Block.Gravel; break;
                case Block.DoubleSlab: drop = Block.Slab; break;
                case Block.Glass: case Block.Bookshelf:
                case Block.Water: case Block.StillWater:
                case Block.Lava: case Block.StillLava:
                    return;
                case SurvivalBlocks.FARMLAND: case SurvivalBlocks.FARMLAND_WET: drop = Block.Dirt; break;
                case SurvivalBlocks.CROPS_7: drop = 256 + 40; break; // ripe crops -> wheat
                case SurvivalBlocks.FIRE: case SurvivalBlocks.WATER_SOURCE: case SurvivalBlocks.LAVA_SOURCE:
                    return; // no drop
                default:
                    if (old >= SurvivalBlocks.CROPS_0 && old < SurvivalBlocks.CROPS_7) return; // growing crops
                    if (old >= SurvivalBlocks.TORCH_W1 && old <= SurvivalBlocks.TORCH_W4) { drop = SurvivalBlocks.TORCH; break; }
                    if (old >= SurvivalBlocks.CHEST_V0 && old <= SurvivalBlocks.CHEST_V0 + 3) { drop = SurvivalBlocks.CHEST; break; }
                    if (old >= SurvivalBlocks.FURN_V0 && old <= SurvivalBlocks.FURN_V0 + 3) { drop = SurvivalBlocks.FURNACE; break; }
                    if (old == SurvivalBlocks.FURNACE_LIT || (old >= SurvivalBlocks.FURNL_V0 && old <= SurvivalBlocks.FURNL_V0 + 3)) { drop = SurvivalBlocks.FURNACE_LIT; break; }
                    break; // classic solids (dirt/sand/wood/cloth/log/ore/...) drop themselves
            }
            SurvivalDrops.SpawnScatter(lvl, x + 0.5, y + 0.5, z + 0.5, drop, 1, SurvivalDrops.MinedDelay(lvl));
        }


        // ==================== density raycast (entity shielding) ====================

        // World.getBlockDensity: fraction of AABB grid samples with clear sight to
        // the blast centre (0 fully shielded, 1 fully exposed). Used by the
        // entity-damage callers in SurvivalMobs.
        public static double Density(Level lvl, double cX, double cY, double cZ,
                                     double minX, double minY, double minZ, double maxX, double maxY, double maxZ) {
            double sx = 1.0 / ((maxX - minX) * 2.0 + 1.0);
            double sy = 1.0 / ((maxY - minY) * 2.0 + 1.0);
            double sz = 1.0 / ((maxZ - minZ) * 2.0 + 1.0);
            int seen = 0, total = 0;
            for (double fx = 0.0; fx <= 1.0; fx += sx)
                for (double fy = 0.0; fy <= 1.0; fy += sy)
                    for (double fz = 0.0; fz <= 1.0; fz += sz)
                    {
                        double px = minX + (maxX - minX) * fx;
                        double py = minY + (maxY - minY) * fy;
                        double pz = minZ + (maxZ - minZ) * fz;
                        if (!RayBlocked(lvl, px, py, pz, cX, cY, cZ)) seen++;
                        total++;
                    }
            return total == 0 ? 0.0 : (double)seen / total;
        }

        // rayTraceBlocks stand-in: any collidable (non-liquid, non-sprite, non-fire)
        // block between the two points, sample-marched at quarter-block resolution.
        static bool RayBlocked(Level lvl, double fx, double fy, double fz, double tx, double ty, double tz) {
            double dx = tx - fx, dy = ty - fy, dz = tz - fz;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 0.0001) return false;
            int steps = (int)(len / 0.25) + 1;
            if (steps > 80) steps = 80;
            for (int i = 1; i < steps; i++)
            {
                double t = (double)i / steps;
                int bx = (int)(fx + dx * t), by = (int)(fy + dy * t), bz = (int)(fz + dz * t);
                if (!In(lvl, bx, by, bz)) continue;
                ushort b = View(lvl, bx, by, bz);
                if (b == Block.Air) continue;
                if (b == SurvivalBlocks.FIRE) continue;
                byte c = lvl.CollideType(Block.FromRaw((BlockID)b));
                if (c == CollideType.WalkThrough) continue;                 // sprites / decoration
                if (c == CollideType.SwimThrough || c == CollideType.LiquidWater || c == CollideType.LiquidLava) continue;
                return true;
            }
            return false;
        }
    }
}
