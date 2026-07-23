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

namespace MCGalaxy.Network
{
    /// <summary> Server-authoritative primed-TNT entities (c0.30/Indev PrimedTnt /
    /// EntityTNTPrimed). Igniting a TNT block - mining it, fire consuming it, or a
    /// blast catching it - removes the block and spawns a primed entity here that
    /// pops up, falls under gravity, counts down its fuse, and then detonates
    /// (SurvivalMobs.ExplodeAt) at its own position. The client renders the hop +
    /// smoke + flash by simulating the SAME physics from the streamed spawn state
    /// (like arrows); the server owns the fuse and the detonation. A blast that
    /// clears another TNT block chain-ignites it with a randomized partial fuse -
    /// the classic TNT chain reaction. </summary>
    internal static class SurvivalTnt
    {
        internal class Primed
        {
            public int    Id;                 // per-level wire key (1..65535)
            public double X, Y, Z;            // entity centre (PrimedTnt x/y/z)
            public double VX, VY, VZ;         // per-tick velocity (xd/yd/zd)
            public int    Fuse;               // PrimedTnt.life; detonates when it passes 0
            public bool   OnGround;
        }

        class LevelTnt
        {
            public List<Primed> List = new List<Primed>();
            public int NextId = 1;
        }

        static readonly Dictionary<Level, LevelTnt> registry = new Dictionary<Level, LevelTnt>();
        static readonly object registryLock = new object();
        static readonly Random rng = new Random();

        // PrimedTnt.setSize(0.98,0.98); pos is the box centre (heightOffset = h/2).
        const double SIZE = 0.98, HALF = SIZE / 2.0;
        const float  BLAST_RADIUS = 4.0f;     // EXPLOSION_RADIUS - shared with the creeper blast
        const int    MAX_PER_LEVEL = 128;     // chain-reaction safety cap (client pool is 64)

        // EntityTNTPrimed default fuse: Indev 80 ticks (4s), c0.30 40 ticks (2s).
        internal static int DefaultFuse(Level lvl) {
            return lvl.Config.SurvivalMode == SurvivalMode.Indev ? 80 : 40;
        }

        // A blast that clears a TNT block re-primes it with a short randomized fuse
        // (World.createExplosion / Level.explode): Indev 10 + rand(20) = 10..29,
        // c0.30 life/8 + rand(life/4) = 5 + rand(10) = 5..14.
        internal static int ChainFuse(Level lvl, Random r) {
            return lvl.Config.SurvivalMode == SurvivalMode.Indev
                 ? 10 + r.Next(20)
                 : 5 + r.Next(10);
        }


        // ==================== per-level registry ====================

        static LevelTnt GetLevel(Level lvl, bool create) {
            lock (registryLock) {
                LevelTnt lt;
                if (registry.TryGetValue(lvl, out lt)) return lt;
                if (!create) return null;
                lt = new LevelTnt();
                registry[lvl] = lt;
                return lt;
            }
        }

        /// <summary> Drops an unloaded level's primed-TNT registry (called from the
        /// mob tick's prune pass, same as SurvivalDrops.Prune). </summary>
        public static void Prune(Level[] loaded) {
            lock (registryLock) {
                List<Level> dead = null;
                foreach (KeyValuePair<Level, LevelTnt> kvp in registry)
                {
                    if (Array.IndexOf(loaded, kvp.Key) < 0) {
                        if (dead == null) dead = new List<Level>();
                        dead.Add(kvp.Key);
                    }
                }
                if (dead != null) foreach (Level lvl in dead) registry.Remove(lvl);
            }
        }

        /// <summary> Streams every live primed TNT to a joining player at its current
        /// state (the client simulates it forward from there). Called from the
        /// survival handshake. </summary>
        public static void SendLevel(Player p, Level lvl) {
            LevelTnt lt = GetLevel(lvl, false);
            if (lt == null) return;
            lock (lt.List) {
                foreach (Primed t in lt.List)
                    SurvivalNet.SendTntSpawn(p, t.Id, t.X, t.Y, t.Z, t.VX, t.VY, t.VZ, t.Fuse);
            }
        }


        // ==================== spawning (ignition) ====================

        /// <summary> Ignites a primed TNT at block cell (x,y,z) with the given fuse -
        /// mining a TNT block, fire consuming it, or a blast chain-igniting it. The
        /// caller has already cleared the block to air. </summary>
        public static void Ignite(Level lvl, int x, int y, int z, int fuse) {
            LevelTnt lt = GetLevel(lvl, true);
            Primed t;
            // PrimedTnt ctor: a small upward pop (yd=0.2) plus a tiny random
            // horizontal drift (the original's double angle->radians conversion
            // makes it minuscule; the magnitude, not the exact value, is what
            // matters for a server sim that streams the seed to the client).
            double ang = rng.NextDouble() * 2.0 * Math.PI;
            lock (lt.List) {
                if (lt.List.Count >= MAX_PER_LEVEL) return; // pool guard (avoids a runaway chain)
                t = new Primed {
                    Id = lt.NextId,
                    X = x + 0.5, Y = y + 0.5, Z = z + 0.5,
                    VX = -Math.Sin(ang) * 0.02, VY = 0.2, VZ = -Math.Cos(ang) * 0.02,
                    Fuse = fuse, OnGround = false
                };
                lt.NextId++;
                if (lt.NextId > 65535) lt.NextId = 1;
                lt.List.Add(t);
            }
            foreach (Player p in Watchers(lvl))
                SurvivalNet.SendTntSpawn(p, t.Id, t.X, t.Y, t.Z, t.VX, t.VY, t.VZ, t.Fuse);
        }


        // ==================== tick (physics + fuse) ====================

        /// <summary> 20 TPS primed-TNT tick for one level: physics + fuse countdown,
        /// detonating each entity as it expires. Called from the survival mob tick,
        /// after the mob loop, under lock(lm.Mobs). </summary>
        public static void Tick(Level lvl) {
            LevelTnt lt = GetLevel(lvl, false);
            if (lt == null) return;

            Player[] watchers = Watchers(lvl);
            lock (lt.List) {
                // Descending so a detonation that Ignite()s new entries (appended at
                // the end, indices we have already passed) leaves them for next tick -
                // the genuine one-tick-later chain arm.
                for (int i = lt.List.Count - 1; i >= 0; i--)
                {
                    Primed t = lt.List[i];
                    Physics(lvl, t);

                    // PrimedTnt.tick: if (life-- > 0) smoke else explode - post-decrement,
                    // so a life=1 TNT survives this tick and detonates on the next.
                    if (t.Fuse-- > 0) continue;

                    lt.List.RemoveAt(i);
                    foreach (Player p in watchers) SurvivalNet.SendTntRemove(p, t.Id, 0);
                    SurvivalMobs.ExplodeAt(lvl, t.X, t.Y, t.Z, BLAST_RADIUS);
                }
            }
        }

        // PrimedTnt.tick physics: gravity, a swept move that clips against solid
        // blocks (Entity.move zeroes each clipped axis), then air drag and ground
        // friction. Horizontal drift is tiny, so the visible motion is the pop +
        // fall onto the block below.
        static void Physics(Level lvl, Primed t) {
            t.VY -= 0.04;

            double dy = ClipY(lvl, t, t.VY);
            t.Y += dy;
            if (dy != t.VY) { t.OnGround = t.VY < 0.0; t.VY = 0.0; } else t.OnGround = false;

            double dx = ClipX(lvl, t, t.VX);
            t.X += dx; if (dx != t.VX) t.VX = 0.0;
            double dz = ClipZ(lvl, t, t.VZ);
            t.Z += dz; if (dz != t.VZ) t.VZ = 0.0;

            t.VX *= 0.98; t.VY *= 0.98; t.VZ *= 0.98;
            if (t.OnGround) { t.VX *= 0.7; t.VZ *= 0.7; }
        }

        // AABB.clipYCollide-style axis sweeps: reduce the per-axis delta so the cube
        // does not pass through a solid block. Out-of-bounds cells (and y<0) count as
        // solid walls/floor so the entity can't leave the world.
        static double ClipY(Level lvl, Primed t, double dy) {
            if (dy == 0) return 0;
            int x0 = Floor(t.X - HALF), x1 = Floor(t.X + HALF);
            int z0 = Floor(t.Z - HALF), z1 = Floor(t.Z + HALF);
            double feet = t.Y - HALF, head = t.Y + HALF;
            if (dy < 0) {
                for (int by = Floor(feet + dy); by < Floor(feet + 1e-7); by++)
                    for (int bx = x0; bx <= x1; bx++)
                        for (int bz = z0; bz <= z1; bz++)
                            if (Solid(lvl, bx, by, bz)) { double gap = (by + 1) - feet; if (gap > dy) dy = Math.Min(0, gap); }
            } else {
                for (int by = Floor(head); by <= Floor(head + dy); by++)
                    for (int bx = x0; bx <= x1; bx++)
                        for (int bz = z0; bz <= z1; bz++)
                            if (Solid(lvl, bx, by, bz)) { double gap = by - head; if (gap < dy) dy = Math.Max(0, gap); }
            }
            return dy;
        }

        static double ClipX(Level lvl, Primed t, double dx) {
            if (dx == 0) return 0;
            int y0 = Floor(t.Y - HALF), y1 = Floor(t.Y + HALF);
            int z0 = Floor(t.Z - HALF), z1 = Floor(t.Z + HALF);
            double lo = t.X - HALF, hi = t.X + HALF;
            if (dx < 0) {
                for (int bx = Floor(lo + dx); bx < Floor(lo + 1e-7); bx++)
                    for (int by = y0; by <= y1; by++) for (int bz = z0; bz <= z1; bz++)
                        if (Solid(lvl, bx, by, bz)) { double gap = (bx + 1) - lo; if (gap > dx) dx = Math.Min(0, gap); }
            } else {
                for (int bx = Floor(hi); bx <= Floor(hi + dx); bx++)
                    for (int by = y0; by <= y1; by++) for (int bz = z0; bz <= z1; bz++)
                        if (Solid(lvl, bx, by, bz)) { double gap = bx - hi; if (gap < dx) dx = Math.Max(0, gap); }
            }
            return dx;
        }

        static double ClipZ(Level lvl, Primed t, double dz) {
            if (dz == 0) return 0;
            int x0 = Floor(t.X - HALF), x1 = Floor(t.X + HALF);
            int y0 = Floor(t.Y - HALF), y1 = Floor(t.Y + HALF);
            double lo = t.Z - HALF, hi = t.Z + HALF;
            if (dz < 0) {
                for (int bz = Floor(lo + dz); bz < Floor(lo + 1e-7); bz++)
                    for (int bx = x0; bx <= x1; bx++) for (int by = y0; by <= y1; by++)
                        if (Solid(lvl, bx, by, bz)) { double gap = (bz + 1) - lo; if (gap > dz) dz = Math.Min(0, gap); }
            } else {
                for (int bz = Floor(hi); bz <= Floor(hi + dz); bz++)
                    for (int bx = x0; bx <= x1; bx++) for (int by = y0; by <= y1; by++)
                        if (Solid(lvl, bx, by, bz)) { double gap = bz - hi; if (gap < dz) dz = Math.Max(0, gap); }
            }
            return dz;
        }

        static int Floor(double v) { return (int)Math.Floor(v); }

        // A block the cube physically rests on / bumps into. Below the world is
        // treated as solid floor; outside the horizontal bounds as solid walls;
        // above the world as air. Liquids and sprites (torches/flowers/crops) are
        // pass-through so the TNT falls through them, matching Entity.move.
        static bool Solid(Level lvl, int x, int y, int z) {
            if (y < 0) return true;
            if (y >= lvl.Height) return false;
            if (x < 0 || z < 0 || x >= lvl.Width || z >= lvl.Length) return true;
            ushort view = SurvivalGrowth.ViewAt(lvl, x, y, z);
            if (view == Block.Air) return false;
            return CollideType.IsSolid(lvl.CollideType(Block.FromRaw(view)));
        }

        static Player[] Watchers(Level lvl) {
            Player[] players = PlayerInfo.Online.Items;
            List<Player> result = new List<Player>();
            foreach (Player p in players)
                if (p.level == lvl && SurvivalNet.Active(p, lvl)) result.Add(p);
            return result.ToArray();
        }
    }
}
