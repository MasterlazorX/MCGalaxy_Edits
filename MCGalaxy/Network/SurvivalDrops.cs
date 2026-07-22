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
    /// <summary> Phase 5: the server-authoritative dropped-item entities. A mined
    /// block (or a tossed stack, or - later - a death scatter) becomes a physical
    /// Drop that lives on the map until a player walks over it or it despawns.
    /// Streamed to survival-test clients as SURV_DROP_SPAWN/PICKUP/REMOVE; the
    /// client's existing st_drops pool renders the visual arc + spin, but the
    /// SERVER owns every drop's logical position, its pickup-delay countdown, and
    /// (authoritatively) who collects it. </summary>
    /// <remarks>
    /// Division of labour (networking-plan §5 drops): the client can run
    /// ClassiCube's real collision engine and the server cannot, so the server
    /// does NOT try to replicate the bouncing arc. It settles each drop straight
    /// down onto the first solid block (the resting spot the client's physics
    /// will also reach, within a fraction of a block) and uses THAT as the pickup
    /// centre; the streamed pos+vel are purely the client's visual pop. Pickup is
    /// proximity + tick-order authoritative, exactly like genuine Player.tick's
    /// findEntities sweep: the first player in the level's tick order who is in
    /// range and has room wins the item. The delayBeforeCanPickup counter (10
    /// ticks for a mined block, 40 for a tossed one - genuine EntityItem) is a
    /// property of the drop, so during that window NOBODY can pick it up: that is
    /// what lets a player toss a stack to a friend without instantly re-vacuuming
    /// it themselves.
    /// </remarks>
    public static class SurvivalDrops
    {
        public class Drop
        {
            public int    Id;            // per-level drop id (wire key), 1..65535
            public ushort Item;          // block id, or 256+ item id
            public byte   Count;         // how many blocks/items this drop carries
            public double X, Y, Z;       // logical (settled) feet-space position
            public int    Age;           // ticks alive - despawns at DESPAWN_TICKS
            public int    PickupDelay;   // ticks before ANY player may collect it
            public byte   Rot0;          // random spin phase (cosmetic, for re-streams)
        }

        class LevelDrops
        {
            public List<Drop> Drops = new List<Drop>();
            public int NextId = 1;
        }

        static readonly Dictionary<Level, LevelDrops> registry = new Dictionary<Level, LevelDrops>();
        static readonly object registryLock = new object();
        static readonly Random rng = new Random();

        // genuine EntityItem: age >= 6000 ticks (5 min at 20 TPS) => removed
        const int DESPAWN_TICKS = 6000;
        // Block.dropBlockAsItemWithChance spawns with delayBeforeCanPickup 10;
        // EntityPlayer.dropPlayerItem uses 40 so the tosser can't re-grab at once.
        const int MINED_DELAY = 10;
        const int TOSS_DELAY  = 40;
        const int MAX_DROPS_PER_LEVEL = 512; // safety cap (client pool is 256; excess just isn't streamed)

        // pickup reach: Player.tick uses bb.grow(1,0,1) - generous horizontally,
        // limited to roughly the player's own height vertically. Feet-space.
        const double PICKUP_H2   = 1.35 * 1.35; // horizontal radius^2 from the drop centre
        const double PICKUP_YLO  = -0.5;        // drop may rest just below the feet (on a step)
        const double PICKUP_YHI  = 2.0;         // ...up to head height

        // wire fixed-point: positions coord*32 (matches mob streaming), velocity
        // coord/sec * 512 (i16 range +/-64 blocks/sec, ample for a pop or a toss).
        internal const double POS_SCALE = 32.0;
        internal const double VEL_SCALE = 512.0;


        // ==================== per-level registry ====================

        static LevelDrops GetLevel(Level lvl, bool create) {
            lock (registryLock) {
                LevelDrops ld;
                if (registry.TryGetValue(lvl, out ld)) return ld;
                if (!create) return null;
                ld = new LevelDrops();
                registry[lvl] = ld;
                return ld;
            }
        }

        /// <summary> Drops a level's registry when it unloads (called from the
        /// mob tick's prune pass so unloaded Levels don't leak as keys). </summary>
        public static void Prune(Level[] loaded) {
            lock (registryLock) {
                List<Level> dead = null;
                foreach (KeyValuePair<Level, LevelDrops> kvp in registry)
                {
                    if (Array.IndexOf(loaded, kvp.Key) < 0) {
                        if (dead == null) dead = new List<Level>();
                        dead.Add(kvp.Key);
                    }
                }
                if (dead != null) foreach (Level lvl in dead) registry.Remove(lvl);
            }
        }

        /// <summary> Streams every live drop on the level to a joining player, at
        /// its settled resting position with zero velocity (it already landed).
        /// Called from SurvivalNet.SendHandshake. </summary>
        public static void SendLevelDrops(Player p, Level lvl) {
            LevelDrops ld = GetLevel(lvl, false);
            if (ld == null) return;
            lock (ld.Drops) {
                foreach (Drop d in ld.Drops)
                    SurvivalNet.SendDropSpawn(p, d.Id, d.Item, d.Count, d.X, d.Y, d.Z, 0, 0, 0, d.Rot0);
            }
        }


        // ==================== spawning ====================

        /// <summary> Settles a feet-space Y straight down onto the first solid
        /// block top at or below the spawn point (the resting spot the client's
        /// own physics also reaches). Never returns below the map floor. </summary>
        static double SettleY(Level lvl, int bx, double y, int bz) {
            int gy = (int)Math.Floor(y);
            if (gy >= lvl.Height) gy = lvl.Height - 1;
            for (; gy >= 0; gy--)
            {
                BlockID b = lvl.GetBlock((ushort)bx, (ushort)gy, (ushort)bz);
                if (MCGalaxy.Blocks.CollideType.IsSolid(lvl.CollideType(b)))
                    return gy + 1; // rest on top of this block
            }
            return 0; // fell to the floor
        }

        /// <summary> Spawns one drop at (x,y,z) feet-space with the given pop
        /// velocity (blocks/sec) and pickup delay, streams SURV_DROP_SPAWN to the
        /// level's survival watchers, and remembers the settled position. </summary>
        static void Spawn(Level lvl, double x, double y, double z,
                          double vx, double vy, double vz, ushort item, int count, int delay) {
            if (count <= 0) return;
            LevelDrops ld = GetLevel(lvl, true);
            byte rot0 = (byte)rng.Next(256);
            int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z);
            double settleY = SettleY(lvl, bx, y, bz);

            Drop d;
            lock (ld.Drops) {
                if (ld.Drops.Count >= MAX_DROPS_PER_LEVEL) return; // pool guard
                d = new Drop {
                    Id = ld.NextId, Item = item, Count = (byte)Math.Min(count, 255),
                    X = x, Y = settleY, Z = z, Age = 0, PickupDelay = delay, Rot0 = rot0
                };
                ld.NextId++;
                if (ld.NextId > 65535) ld.NextId = 1; // wrap (u16 wire key)
                ld.Drops.Add(d);
            }

            Player[] watchers = Watchers(lvl);
            foreach (Player p in watchers)
                SurvivalNet.SendDropSpawn(p, d.Id, item, d.Count, x, y, z, vx, vy, vz, rot0);
        }

        /// <summary> Mining bridge: a survival player broke a block. Replaces the
        /// v1 "straight into the inventory" hop with genuine drop entities. On an
        /// Indev map this rolls the full SurvivalItems.MiningDrops table (harvest
        /// gating, grass->dirt, ore->item, crop seed rolls...); on a c0.30 map the
        /// block simply drops itself. Returns true if anything was spawned. </summary>
        public static bool SpawnMined(Player p, Level lvl, ushort x, ushort y, ushort z,
                                      ushort view, ushort held) {
            bool indev = lvl.Config.SurvivalMode == SurvivalMode.Indev;
            double cx = x + 0.5, cy = y + 0.5, cz = z + 0.5;

            if (indev) {
                List<KeyValuePair<ushort, int>> drops = new List<KeyValuePair<ushort, int>>();
                lock (rng) SurvivalItems.MiningDrops(rng, view, held, drops);
                if (drops.Count == 0) return false;
                foreach (KeyValuePair<ushort, int> kv in drops)
                {
                    // one drop entity per item (genuine BlockUtils.dropItems), each
                    // with its own little pop so a multi-item break scatters
                    for (int n = 0; n < kv.Value; n++)
                        Spawn(lvl, cx, cy, cz, PopX(), PopY(), PopZ(), kv.Key, 1, MINED_DELAY);
                }
                return true;
            } else {
                if (view > Block.CLASSIC_MAX_BLOCK) return false;
                Spawn(lvl, cx, cy, cz, PopX(), PopY(), PopZ(), view, 1, 0);
                return true;
            }
        }

        /// <summary> Q-toss bridge: the player asked to drop from a hotbar slot.
        /// The server takes the item off the server-owned inventory, then flings a
        /// drop out in front along the player's look vector with a 40-tick
        /// self-pickup delay (genuine EntityPlayer.dropPlayerItem). </summary>
        public static void Toss(Player p, int slot, bool whole) {
            Level lvl = p.level;
            if (lvl == null || !SurvivalNet.Active(p, lvl)) return;
            if (lvl.Config.SurvivalCreative) return; // creative keeps the local palette

            ushort id; byte count;
            if (!SurvivalInventory.TakeForToss(p, slot, whole, out id, out count)) return;

            // dropPlayerItem spawns at eye height - 0.3. p.Pos.Y already carries the
            // character-height offset (feet + 51/32), i.e. ~eye level, so eye-0.3
            // is just p.Pos.Y/32 - 0.3.
            double fx = p.Pos.X / 32.0;
            double fy = p.Pos.Y / 32.0 - 0.3;
            double fz = p.Pos.Z / 32.0;

            double yaw   = p.Rot.RotY  * 2.0 * Math.PI / 256.0;
            double pitch = p.Rot.HeadX * 2.0 * Math.PI / 256.0;
            // ClassiCube Vec3_GetDirVector: x=cos(pitch)*sin(yaw), y=-sin(pitch), z=-cos(pitch)*cos(yaw)
            double dx = Math.Cos(pitch) * Math.Sin(yaw);
            double dy = -Math.Sin(pitch);
            double dz = -Math.Cos(pitch) * Math.Cos(yaw);

            // dropPlayerItem: velocity = dir*0.3/tick (+0.1 up) with a little jitter,
            // expressed here per-second (x20). One drop carries the whole taken stack.
            double vx = dx * 0.3 * 20.0 + (rng.NextDouble() - 0.5) * 0.04 * 20.0;
            double vy = dy * 0.3 * 20.0 + 0.1 * 20.0 + (rng.NextDouble() - rng.NextDouble()) * 0.1 * 20.0;
            double vz = dz * 0.3 * 20.0 + (rng.NextDouble() - 0.5) * 0.04 * 20.0;
            Spawn(lvl, fx, fy, fz, vx, vy, vz, id, count, TOSS_DELAY);
        }

        // Item ctor pop: xd/zd uniform +/-0.1 block/tick, yd fixed 0.2 block/tick.
        static double PopX() { lock (rng) return (rng.NextDouble() * 0.2 - 0.1) * 20.0; }
        static double PopY() { return 0.2 * 20.0; }
        static double PopZ() { lock (rng) return (rng.NextDouble() * 0.2 - 0.1) * 20.0; }

        /// <summary> The mined/scattered pickup delay for this map: 10 ticks on Indev
        /// (genuine dropBlockAsItemWithChance), 0 on c0.30. </summary>
        public static int MinedDelay(Level lvl) {
            return lvl.Config.SurvivalMode == SurvivalMode.Indev ? MINED_DELAY : 0;
        }

        /// <summary> Spawns `count` separate single-item drops at (x,y,z), each with
        /// its own random pop - a mob-death scatter, a wool shear, an explosion
        /// scatter (genuine BlockUtils.dropItems: one EntityItem per item). </summary>
        public static void SpawnScatter(Level lvl, double x, double y, double z,
                                        ushort item, int count, int delay) {
            for (int i = 0; i < count; i++)
                Spawn(lvl, x, y, z, PopX(), PopY(), PopZ(), item, 1, delay);
        }

        /// <summary> Spawns ONE drop carrying a whole stack at (x,y,z) with a random
        /// pop - a chest scatter or a player-death scatter (one drop per slot). </summary>
        public static void SpawnStack(Level lvl, double x, double y, double z,
                                      ushort item, int count, int delay) {
            Spawn(lvl, x, y, z, PopX(), PopY(), PopZ(), item, count, delay);
        }


        // ==================== tick (pickup + despawn) ====================

        /// <summary> 20 TPS drop tick for one level: ages every drop, counts down
        /// its pickup delay, hands it to the first in-range player with room, and
        /// despawns it after 5 minutes. Called from the survival mob tick. </summary>
        public static void Tick(Level lvl) {
            LevelDrops ld = GetLevel(lvl, false);
            if (ld == null) return;

            Player[] watchers = Watchers(lvl);
            lock (ld.Drops) {
                for (int i = ld.Drops.Count - 1; i >= 0; i--)
                {
                    Drop d = ld.Drops[i];
                    d.Age++;
                    if (d.PickupDelay > 0) d.PickupDelay--;

                    if (d.Age >= DESPAWN_TICKS) {
                        Broadcast(watchers, d.Id, isPickup: false, picker: null);
                        ld.Drops.RemoveAt(i);
                        continue;
                    }
                    if (d.PickupDelay > 0) continue; // still in the no-touch window

                    Player picker = FindPicker(watchers, d);
                    if (picker == null) continue;
                    // authoritative collect: only if the whole stack fits (room is
                    // checked before any mutation, so a full inventory leaves the
                    // drop on the ground for someone else / later)
                    if (!SurvivalInventory.PickUp(picker, d.Item, d.Count)) continue;

                    Broadcast(watchers, d.Id, isPickup: true, picker: picker);
                    ld.Drops.RemoveAt(i);
                }
            }
        }

        // First watcher (level tick order) within reach of the drop centre. Genuine
        // has no distance tiebreak - the earliest player in the sweep collects it.
        static Player FindPicker(Player[] watchers, Drop d) {
            foreach (Player p in watchers)
            {
                double px = p.Pos.X / 32.0;
                double py = (p.Pos.Y - Entities.CharacterHeight) / 32.0; // feet
                double pz = p.Pos.Z / 32.0;
                double dx = px - d.X, dz = pz - d.Z, dy = d.Y - py;
                if (dx * dx + dz * dz > PICKUP_H2) continue;
                if (dy < PICKUP_YLO || dy > PICKUP_YHI) continue;
                return p;
            }
            return null;
        }

        // Streams the drop's removal to every watcher. A pickup carries the
        // collecting player's entity id AS EACH VIEWER SEES IT (255 = the viewer
        // themselves) so the client can play the fly-into-you animation toward the
        // right body; a despawn is a plain SURV_DROP_REMOVE.
        static void Broadcast(Player[] watchers, int dropId, bool isPickup, Player picker) {
            foreach (Player p in watchers)
            {
                if (!isPickup) { SurvivalNet.SendDropRemove(p, dropId, 0); continue; }

                byte eid;
                if (p == picker)      eid = 255;               // ENTITIES_SELF_ID on the client
                else if (!p.EntityList.TryGetVisibleID(picker, out eid)) eid = 0xFF; // not visible -> no anim
                SurvivalNet.SendDropPickup(p, dropId, eid);
            }
        }


        // Survival clients on this level (mirrors SurvivalMobs.Watchers).
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
