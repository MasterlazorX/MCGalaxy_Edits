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
    /// <summary> Phase 5 projectiles: server-authoritative arrows. A player Tab-fire
    /// (c0.30) / bow use (Indev) and a skeleton's shot become server-owned Arrow
    /// entities that fly under the genuine c0.30 Arrow.tick model, stick into blocks,
    /// and hit players/mobs for authoritative damage. Streamed to survival clients as
    /// SURV_ARROW_SPAWN/STICK/REMOVE; the client simulates the SAME flight for a smooth
    /// visual (its st_arrows pool) but never resolves a hit itself. </summary>
    /// <remarks>
    /// Division of labour mirrors SurvivalDrops: the server owns each arrow's
    /// authoritative position, its block-stick, and (crucially) who it damages; the
    /// streamed pos+vel let the client render the arc with matching drag+gravity so it
    /// stays in lock-step until the server issues a STICK (snap + freeze) or REMOVE.
    /// v1 runs the c0.30 flight model for both modes (the Indev spread/water-drag
    /// nuances are cosmetic and deferred). Ammo is server-owned: c0.30 keeps a per-
    /// player arrow count (20..99), Indev consumes an arrow item from the inventory.
    /// </remarks>
    public static class SurvivalArrows
    {
        public class Arrow
        {
            public int    Id;
            public double X, Y, Z;      // box-centre position (Arrow.setPos centres on all axes)
            public double VX, VY, VZ;   // blocks per TICK (genuine Arrow velocity units)
            public double Gravity;      // c0.30: 1/force scales the fall term (Indev uses a flat 0.03)
            public byte   Type;         // 0 player-fired, 1 mob-fired (texture row + despawn rule)
            public int    Damage;
            public bool   Indev;        // flight model: Indev (move->drag->flat g) vs c0.30 (drag+g->move)
            public Player OwnerPlayer;  // non-null for a player shot (owner-grace + pickup credit)
            public int    OwnerMobId;   // >0 for a skeleton shot, else 0
            public int    Age;          // ticks since spawn (owner-grace gate)
            public bool   Stuck;
            public int    StuckTicks;
        }

        class LevelArrows
        {
            public List<Arrow> Arrows = new List<Arrow>();
            public int NextId = 1;
        }

        static readonly Dictionary<Level, LevelArrows> registry = new Dictionary<Level, LevelArrows>();
        static readonly object registryLock = new object();
        static readonly Random rng = new Random();

        // genuine c0.30 Arrow.tick constants (velocity in blocks/tick)
        const double DRAG = 0.998;
        const double SUBSTEP = 0.2;
        const int    STICK_MOB_TICKS = 20; // c0.30 mob-fired arrows despawn 20t after sticking
        const int    STICK_PLAYER_MIN = 300;
        const double STICK_PLAYER_DESPAWN_CHANCE = 0.01;
        const int    STICK_INDEV_TICKS = 1200; // Indev: any stuck arrow dies at ticksInGround 1200
        const int    MAX_ARROWS_PER_LEVEL = 256;

        // arrow bounding half-extents: Arrow.setSize(0.3, 0.5), centred on the position
        const double HALF_W = 0.15, HALF_H = 0.25;

        // player ammo (Player.java: arrows = 20, MAX_ARROWS = 99)
        public const int PLAYER_START_ARROWS = 20;
        public const int PLAYER_MAX_ARROWS   = 99;
        const string AMMO_KEY = "survival.arrows";
        const ushort ARROW_ITEM = 256 + 6; // Indev bow ammo / pickup item id

        internal const double POS_SCALE = 32.0;
        internal const double VEL_SCALE = 1024.0; // blocks/tick * 1024 fits i16 (max ~1.5 b/t)


        // ==================== registry ====================

        static LevelArrows GetLevel(Level lvl, bool create) {
            lock (registryLock) {
                LevelArrows la;
                if (registry.TryGetValue(lvl, out la)) return la;
                if (!create) return null;
                la = new LevelArrows();
                registry[lvl] = la;
                return la;
            }
        }

        public static void Prune(Level[] loaded) {
            lock (registryLock) {
                List<Level> dead = null;
                foreach (KeyValuePair<Level, LevelArrows> kvp in registry)
                {
                    if (Array.IndexOf(loaded, kvp.Key) < 0) {
                        if (dead == null) dead = new List<Level>();
                        dead.Add(kvp.Key);
                    }
                }
                if (dead != null) foreach (Level lvl in dead) registry.Remove(lvl);
            }
        }

        /// <summary> Streams the level's in-flight + stuck arrows to a joining player. </summary>
        public static void SendLevelArrows(Player p, Level lvl) {
            LevelArrows la = GetLevel(lvl, false);
            if (la == null) return;
            lock (la.Arrows) {
                foreach (Arrow a in la.Arrows)
                {
                    SurvivalNet.SendArrowSpawn(p, a.Id, a.Type, a.Gravity, a.X, a.Y, a.Z, a.VX, a.VY, a.VZ);
                    if (a.Stuck) SurvivalNet.SendArrowStick(p, a.Id, a.X, a.Y, a.Z);
                }
            }
        }


        // ==================== ammo ====================

        public static int Ammo(Player p) { return p.Extras.GetInt(AMMO_KEY, PLAYER_START_ARROWS); }

        static void SetAmmo(Player p, int n) {
            if (n < 0) n = 0;
            if (n > PLAYER_MAX_ARROWS) n = PLAYER_MAX_ARROWS;
            p.Extras[AMMO_KEY] = n;
            SurvivalNet.SendArrowAmmo(p, n);
        }

        /// <summary> Seeds + streams the player's starting ammo on handshake (c0.30 only;
        /// Indev fires arrow items from the inventory, no counter). </summary>
        public static void SendInitialAmmo(Player p, Level lvl) {
            if (lvl.Config.SurvivalMode == SurvivalMode.Indev) return;
            SurvivalNet.SendArrowAmmo(p, Ammo(p));
        }


        // ==================== firing ====================

        /// <summary> Handles a SURV_FIRE_ARROW intent: fires from the player's eye along
        /// the given aim. c0.30 spends a counted arrow; Indev spends an inventory arrow
        /// item. kind is 0 (Tab) / 1 (bow) - purely informational. </summary>
        public static void FireFromPlayer(Player p, double yawDeg, double pitchDeg, int kind) {
            Level lvl = p.level;
            if (lvl == null || !SurvivalNet.Active(p, lvl) || SurvivalNet.IsDead(p)) return;
            if (lvl.Config.SurvivalCreative) return;
            bool indev = lvl.Config.SurvivalMode == SurvivalMode.Indev;

            double dx, dy, dz;
            AimVector(yawDeg, pitchDeg, out dx, out dy, out dz);

            if (indev) {
                // Indev has NO Tab-fire - arrows come only from the BOW (kind 1),
                // consuming an inventory arrow item. ItemBow: setArrowHeading speed
                // 1.5, spread 1.0, flat 4 damage; spawn offset 0.16 sideways / 0.1
                // down along the yaw (the genuine EntityArrow ctor hand offset).
                if (kind != 1) return;                         // reject a stray Tab intent
                if (!SurvivalInventory.ConsumeArrow(p)) return; // dry bow: nothing happens
                double yawRad = yawDeg * Math.PI / 180.0;
                double ox = p.Pos.X / 32.0 + Math.Cos(yawRad) * 0.16;
                double oy = p.Pos.Y / 32.0 - 0.1;
                double oz = p.Pos.Z / 32.0 + Math.Sin(yawRad) * 0.16;
                SpawnIndev(lvl, ox, oy, oz, dx, dy, dz, 1.5, 1.0, 4, 0, p, 0);
            } else {
                // c0.30 Tab-fire: counted quiver, force 1.2, damage 7
                int ammo = Ammo(p);
                if (ammo <= 0) return;
                SetAmmo(p, ammo - 1);
                SpawnC030(lvl, p.Pos.X / 32.0, p.Pos.Y / 32.0, p.Pos.Z / 32.0,
                          dx, dy, dz, 1.2, 7, 0, p, 0);
            }
        }

        /// <summary> A c0.30 skeleton looses an arrow (Mob_ShootArrow: type 1, damage
        /// 3, force 1.0) along yaw/pitch. Called from the c0.30 mob AI. </summary>
        public static void FireFromMobC030(Level lvl, int mobId, double eyeX, double eyeY, double eyeZ,
                                           double yawDeg, double pitchDeg) {
            double dx, dy, dz;
            AimVector(yawDeg, pitchDeg, out dx, out dy, out dz);
            SpawnC030(lvl, eyeX, eyeY, eyeZ, dx, dy, dz, 1.0, 3, 1, null, mobId);
        }

        /// <summary> An Indev skeleton looses an arrow (Mob_IndevShootArrow: the RAW
        /// unnormalized aim vector into setArrowHeading speed 0.6 / spread 12, flat 4
        /// damage, type 0). aim is target-relative (with the lob already applied by the
        /// AI). Spawn point is the skeleton's offset eye. </summary>
        public static void FireFromMobIndev(Level lvl, int mobId, double eyeX, double eyeY, double eyeZ,
                                            double aimX, double aimY, double aimZ) {
            SpawnIndev(lvl, eyeX, eyeY, eyeZ, aimX, aimY, aimZ, 0.6, 12.0, 4, 0, null, mobId);
        }

        // ClassiCube Vec3_GetDirVector: x=cos(pitch)*sin(yaw), y=-sin(pitch), z=-cos(pitch)*cos(yaw)
        static void AimVector(double yawDeg, double pitchDeg, out double dx, out double dy, out double dz) {
            double yaw = yawDeg * Math.PI / 180.0, pitch = pitchDeg * Math.PI / 180.0;
            dx = Math.Cos(pitch) * Math.Sin(yaw);
            dy = -Math.Sin(pitch);
            dz = -Math.Cos(pitch) * Math.Cos(yaw);
        }

        // c0.30 Arrow ctor: spawn along a UNIT aim scaled by force, backed off 0.2 so
        // the tip clears the shooter; gravity = 1/force scales the fall term.
        static void SpawnC030(Level lvl, double px, double py, double pz,
                              double dx, double dy, double dz, double force, int damage,
                              byte type, Player owner, int ownerMobId) {
            Add(lvl, px - dx * 0.2, py - dy * 0.2, pz - dz * 0.2,
                dx * force, dy * force, dz * force, 1.0 / force, damage, type, false, owner, ownerMobId);
        }

        // Indev EntityArrow.setArrowHeading: normalize the raw aim, nudge each axis by
        // an independent gaussian * 0.0075 * spread, then scale by speed WITHOUT
        // re-normalizing. Flat Indev gravity (0.03) is applied per tick, so the stored
        // Gravity field is unused (kept 1.0). No 0.2 back-off (callers offset the eye).
        static void SpawnIndev(Level lvl, double px, double py, double pz,
                               double aimX, double aimY, double aimZ, double speed, double spread,
                               int damage, byte type, Player owner, int ownerMobId) {
            double len = Math.Sqrt(aimX * aimX + aimY * aimY + aimZ * aimZ);
            if (len < 0.0001) { aimX = 0; aimY = 1; aimZ = 0; len = 1; }
            aimX /= len; aimY /= len; aimZ /= len;
            lock (rng) {
                aimX += NextGaussian() * 0.0075 * spread;
                aimY += NextGaussian() * 0.0075 * spread;
                aimZ += NextGaussian() * 0.0075 * spread;
            }
            Add(lvl, px, py, pz, aimX * speed, aimY * speed, aimZ * speed,
                1.0, damage, type, true, owner, ownerMobId);
        }

        // Box-Muller standard-normal sample (distribution-shape parity with genuine
        // java.util.Random.nextGaussian; the time-seeded RNG can't be sequence-matched).
        static double NextGaussian() {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        static void Add(Level lvl, double px, double py, double pz,
                        double vx, double vy, double vz, double gravity, int damage,
                        byte type, bool indev, Player owner, int ownerMobId) {
            LevelArrows la = GetLevel(lvl, true);
            Arrow a = new Arrow {
                X = px, Y = py, Z = pz, VX = vx, VY = vy, VZ = vz,
                Gravity = gravity, Type = type, Damage = damage, Indev = indev,
                OwnerPlayer = owner, OwnerMobId = ownerMobId
            };
            lock (la.Arrows) {
                if (la.Arrows.Count >= MAX_ARROWS_PER_LEVEL) la.Arrows.RemoveAt(0); // evict oldest
                a.Id = la.NextId++;
                if (la.NextId > 65535) la.NextId = 1;
                la.Arrows.Add(a);
            }
            foreach (Player p in Watchers(lvl))
                SurvivalNet.SendArrowSpawn(p, a.Id, a.Type, a.Gravity, a.X, a.Y, a.Z, a.VX, a.VY, a.VZ);
        }


        // ==================== tick ====================

        public static void Tick(Level lvl) {
            LevelArrows la = GetLevel(lvl, false);
            if (la == null) return;
            Player[] watchers = Watchers(lvl);

            lock (la.Arrows) {
                for (int i = la.Arrows.Count - 1; i >= 0; i--)
                {
                    Arrow a = la.Arrows[i];
                    a.Age++;

                    if (a.Stuck) {
                        a.StuckTicks++;
                        // player-fired stuck arrows are pickable (refund ammo / arrow item)
                        if (a.Type == 0 && TryPickup(lvl, a, watchers)) {
                            Broadcast(watchers, a.Id, remove: true, reason: 2);
                            la.Arrows.RemoveAt(i);
                            continue;
                        }
                        // despawn: Indev EntityArrow dies at exactly 1200 ticks stuck
                        // (any type); c0.30 - player 300t+1%/tick, mob 20t.
                        bool despawn = a.Indev
                            ? (a.StuckTicks >= STICK_INDEV_TICKS)
                            : a.Type == 0
                                ? (a.StuckTicks >= STICK_PLAYER_MIN && rng.NextDouble() < STICK_PLAYER_DESPAWN_CHANCE)
                                : (a.StuckTicks >= STICK_MOB_TICKS);
                        if (despawn) {
                            Broadcast(watchers, a.Id, remove: true, reason: 0);
                            la.Arrows.RemoveAt(i);
                        }
                        continue;
                    }

                    // c0.30 Arrow.tick applies drag + speed-scaled gravity BEFORE the
                    // move; Indev EntityArrow.onEntityUpdate moves FIRST, then drag
                    // (0.99 air / 0.8 water) + a flat 0.03 gravity AFTER.
                    if (!a.Indev) {
                        a.VX *= DRAG; a.VY *= DRAG; a.VZ *= DRAG;
                        a.VY -= 0.02 * a.Gravity;
                    }

                    double len = Math.Sqrt(a.VX * a.VX + a.VY * a.VY + a.VZ * a.VZ);
                    int steps = (int)(len / SUBSTEP + 1.0);
                    double sx = a.VX / steps, sy = a.VY / steps, sz = a.VZ / steps;

                    bool gone = false, stuck = false;
                    for (int s = 0; s < steps; s++)
                    {
                        double nx = a.X + sx, ny = a.Y + sy, nz = a.Z + sz;
                        if (SolidOverlap(lvl, nx, ny, nz)) { stuck = true; break; } // stick at the pre-step pos
                        // entity hits are tested at the stepped position; the shooter
                        // is always excluded (HitEntity / TryArrowHitMob skip the owner),
                        // so no blanket grace window is needed - a close target still hits.
                        if (HitEntity(lvl, a, nx, ny, nz)) { gone = true; break; }
                        a.X = nx; a.Y = ny; a.Z = nz;
                    }

                    if (gone) {
                        Broadcast(watchers, a.Id, remove: true, reason: 1);
                        la.Arrows.RemoveAt(i);
                        continue;
                    }
                    if (stuck) {
                        a.Stuck = true; a.StuckTicks = 0;
                        a.VX = a.VY = a.VZ = 0;
                        foreach (Player p in watchers) SurvivalNet.SendArrowStick(p, a.Id, a.X, a.Y, a.Z);
                        continue;
                    }

                    // Indev: drag + flat gravity AFTER the move (water slows to 0.8)
                    if (a.Indev) {
                        double drag = InWater(lvl, a.X, a.Y, a.Z) ? 0.8 : 0.99;
                        a.VX *= drag; a.VY *= drag; a.VZ *= drag;
                        a.VY -= 0.03;
                    }
                    // in-flight arrows are client-simulated from the spawn state (identical
                    // physics), so no per-tick position stream is needed.
                }
            }
        }

        // Whether the arrow's centre cell is water (Indev water-drag test - the
        // genuine handleWaterMovement band degenerates to ~the centre block).
        static bool InWater(Level lvl, double x, double y, double z) {
            int bx = (int)Math.Floor(x), by = (int)Math.Floor(y), bz = (int)Math.Floor(z);
            if (bx < 0 || by < 0 || bz < 0 || bx >= lvl.Width || by >= lvl.Height || bz >= lvl.Length) return false;
            BlockID b = Block.Convert(lvl.GetBlock((ushort)bx, (ushort)by, (ushort)bz));
            return b == Block.Water || b == Block.StillWater;
        }

        // Whether the arrow's box (centred on the given point) overlaps any solid block.
        static bool SolidOverlap(Level lvl, double x, double y, double z) {
            int x0 = (int)Math.Floor(x - HALF_W), x1 = (int)Math.Floor(x + HALF_W);
            int y0 = (int)Math.Floor(y - HALF_H), y1 = (int)Math.Floor(y + HALF_H);
            int z0 = (int)Math.Floor(z - HALF_W), z1 = (int)Math.Floor(z + HALF_W);
            for (int bx = x0; bx <= x1; bx++)
            for (int by = y0; by <= y1; by++)
            for (int bz = z0; bz <= z1; bz++)
            {
                if (bx < 0 || by < 0 || bz < 0 || bx >= lvl.Width || by >= lvl.Height || bz >= lvl.Length) continue;
                BlockID b = lvl.GetBlock((ushort)bx, (ushort)by, (ushort)bz);
                if (MCGalaxy.Blocks.CollideType.IsSolid(lvl.CollideType(b))) return true;
            }
            return false;
        }

        // Tests the arrow against players + mobs on the level; applies authoritative
        // damage on the first hit. Returns whether the arrow was consumed.
        static bool HitEntity(Level lvl, Arrow a, double x, double y, double z) {
            // players (skip the owner). A PLAYER-fired arrow only hits other players
            // when the map's SurvivalPvP flag is on; skeleton arrows always hit.
            // Player box is ~0.6 wide, ~1.8 tall, feet at (Pos - characterHeight).
            bool playersHittable = a.OwnerPlayer == null || lvl.Config.SurvivalPvP;
            Player[] players = playersHittable ? PlayerInfo.Online.Items : new Player[0];
            foreach (Player p in players)
            {
                if (p.level != lvl || !SurvivalNet.Active(p, lvl) || SurvivalNet.IsDead(p)) continue;
                if (p == a.OwnerPlayer) continue;
                if (p.Game.Referee) continue; // observers aren't targets
                double fx = p.Pos.X / 32.0, fy = (p.Pos.Y - Entities.CharacterHeight) / 32.0, fz = p.Pos.Z / 32.0;
                if (BoxHit(x, y, z, fx, fy, fz, 0.3, 1.8)) {
                    string who = a.OwnerPlayer != null ? a.OwnerPlayer.name : "an arrow";
                    SurvivalNet.DamagePlayer(p, a.Damage, "@p was shot by " + who);
                    return true;
                }
            }
            // mobs (owned by SurvivalMobs - it applies HurtMob + aggro credit)
            return SurvivalMobs.TryArrowHitMob(lvl, x, y, z, HALF_W, HALF_H,
                                               a.OwnerMobId, a.Damage, a.OwnerPlayer);
        }

        // AABB overlap of the arrow box (centre px,py,pz) against a feet-anchored
        // entity box of the given full width/height.
        static bool BoxHit(double px, double py, double pz, double ex, double ey, double ez,
                           double ew, double eh) {
            double hw = ew / 2.0;
            return px + HALF_W >= ex - hw && px - HALF_W <= ex + hw &&
                   py + HALF_H >= ey       && py - HALF_H <= ey + eh &&
                   pz + HALF_W >= ez - hw && pz - HALF_W <= ez + hw;
        }

        // A stuck player-fired arrow within pickup reach of any survival player
        // refunds ammo (c0.30) or an arrow item (Indev) and is retired.
        static bool TryPickup(Level lvl, Arrow a, Player[] watchers) {
            bool indev = lvl.Config.SurvivalMode == SurvivalMode.Indev;
            foreach (Player p in watchers)
            {
                if (SurvivalNet.IsDead(p)) continue;
                double fx = p.Pos.X / 32.0, fy = (p.Pos.Y - Entities.CharacterHeight) / 32.0, fz = p.Pos.Z / 32.0;
                double dx = fx - a.X, dz = fz - a.Z, dy = a.Y - fy;
                if (dx * dx + dz * dz > 1.35 * 1.35) continue;
                if (dy < -0.5 || dy > 2.0) continue;
                if (indev) {
                    if (!SurvivalInventory.PickUp(p, ARROW_ITEM, 1)) continue; // no room - leave it
                } else {
                    if (Ammo(p) >= PLAYER_MAX_ARROWS) continue; // quiver full - leave it
                    SetAmmo(p, Ammo(p) + 1);
                }
                return true;
            }
            return false;
        }

        static void Broadcast(Player[] watchers, int id, bool remove, byte reason) {
            foreach (Player p in watchers) SurvivalNet.SendArrowRemove(p, id, reason);
        }

        static Player[] Watchers(Level lvl) {
            Player[] players = PlayerInfo.Online.Items;
            List<Player> result = new List<Player>();
            foreach (Player p in players)
            {
                if (p.level == lvl && SurvivalNet.Active(p, lvl)) result.Add(p);
            }
            return result.ToArray();
        }
    }
}
