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
using MCGalaxy.Maths;
using MCGalaxy.Tasks;
using BlockID = System.UInt16;

namespace MCGalaxy.Network
{
    /// <summary> Phase 3: server-authoritative mob simulation, streamed to survival-test
    /// clients as SURV_MOB_SPAWN/MOVE/STATE/DESPAWN (the client renders a puppet pool -
    /// ClassiCube fork's networking-plan.md §15.1/§17.5, wire layouts §25). </summary>
    /// <remarks>
    /// The AI/physics below is a port of the ClassiCube fork's SurvivalTest.c mob
    /// simulation, itself a verified port of the original c0.30 Survival Test /
    /// Indev decompiles (Mob.java, BasicAI/BasicAttackAI, EntityMob/EntityCreeper...).
    /// Ticks at 20 TPS on a dedicated scheduler; only levels that currently have
    /// ANY player (survival or classic spectator) are simulated - mobs keep
    /// roaming for classic viewers via the mirror, and freeze only on maps with
    /// nobody at all on them (a server-cost deviation).
    ///
    /// V1 scope cuts, all deliberate and documented in doc/survival-support/session-notes.md:
    ///  * Indev's A* creature pathfinding is NOT ported yet - both modes chase with the
    ///    c0.30 BasicAttackAI direct-steer model (mobs bump into obstacles rather than
    ///    pathing around them).
    ///  * Skeletons melee like zombies - arrows need their own wire messages (phase 5's
    ///    projectile/drops work) before ranged AI can stream.
    ///  * Lighting rules (spawn darkness, spider light-flee, monster fast-despawn in
    ///    light, undead sunburn) approximate "brightness" as sky-exposure x day/night,
    ///    since the server has no block-light engine: a column open to the sky uses the
    ///    day/night level, anything under cover counts as dark.
    ///  * Creeper explosions damage players (genuine radius/falloff) but do NOT destroy
    ///    blocks - most MCGalaxy maps are protected builds; block damage needs its own
    ///    opt-in config + undo integration before it can land.
    ///  * Death drops / wool shear drops are phase 5 (no drop streaming yet).
    /// </remarks>
    public static class SurvivalMobs
    {
        // Mirrors the client's enum MobType / mobTypeInfo ordering exactly
        // (MobSpawner.spawn's random.nextInt(6) indexes this table).
        public const byte TYPE_ZOMBIE = 0, TYPE_SKELETON = 1, TYPE_PIG = 2,
                          TYPE_CREEPER = 3, TYPE_SPIDER = 4, TYPE_SHEEP = 5;
        const int SPAWN_TYPES = 6;

        class MobType
        {
            public string Name;
            public bool Passive, IsCreeper;
            public float RunSpeed, LookAngle;
            public int Damage;                  // c0.30 BasicAttackAI damage roll base
            public int IndevMelee;              // Indev EntityMob.attackStrength (0 = never melees)
            public float W030, H030, WIndev, HIndev;
            public float HeightOff;             // Entity.heightOffset - the eye-ish anchor
        }

        static readonly MobType[] Types = {
            new MobType { Name="zombie",   RunSpeed=1.00f, LookAngle=30, Damage=6, IndevMelee=5, HeightOff=1.62f, W030=0.6f, H030=1.8f,  WIndev=0.6f, HIndev=1.8f },
            new MobType { Name="skeleton", RunSpeed=0.30f, LookAngle=0,  Damage=8, IndevMelee=2, HeightOff=1.62f, W030=0.6f, H030=1.8f,  WIndev=0.6f, HIndev=1.8f },
            new MobType { Name="pig",      RunSpeed=0.70f, LookAngle=0,  Damage=0, IndevMelee=0, HeightOff=1.72f, W030=1.4f, H030=1.2f,  WIndev=0.9f, HIndev=0.9f, Passive=true },
            new MobType { Name="creeper",  RunSpeed=0.70f, LookAngle=45, Damage=6, IndevMelee=0, HeightOff=1.62f, W030=0.6f, H030=1.8f,  WIndev=0.6f, HIndev=1.8f, IsCreeper=true },
            new MobType { Name="spider",   RunSpeed=0.56f, LookAngle=0,  Damage=6, IndevMelee=2, HeightOff=0.72f, W030=1.4f, H030=0.9f,  WIndev=1.4f, HIndev=0.9f },
            new MobType { Name="sheep",    RunSpeed=0.70f, LookAngle=0,  Damage=0, IndevMelee=0, HeightOff=1.72f, W030=1.4f, H030=1.72f, WIndev=0.9f, HIndev=1.3f, Passive=true },
        };

        // Indev EntityLiving moveSpeed overrides (client's Mob_IndevMoveSpeed)
        static float IndevMoveSpeed(byte type) {
            if (type == TYPE_ZOMBIE) return 0.5f;
            if (type == TYPE_SPIDER) return 0.8f;
            return 0.7f;
        }

        class SurvMob
        {
            public ushort Id;
            public byte Type;
            public double X, Y, Z;      // feet position (client Entity.Position convention)
            public double VX, VY, VZ;   // per-tick displacement (Java velocity convention)
            public float Yaw, Pitch;    // degrees
            public bool OnGround;

            public int Health = 20, LastHealth, InvincTicks, AttackDelay, DeathTicks;
            public int NoActionTime, AirTicks = 300;
            public float MoveStrafe, MoveForward, TurnRate;
            public bool Jumping, Dead;
            public Player Target;

            public bool HasFur = true, Grazing;
            public bool HasHelmet, HasArmor; // c0.30 HumanoidMob 20% cosmetic rolls
            public int GrazeTime;
            public int Fire;            // Entity.fire burn ticks
            public sbyte FuseState = -1;
            public int FuseTicks;
            public bool Falling; public double FallPeakY;

            // last-streamed snapshot, so MOVE/STATE only go out on change
            public short SentX = short.MinValue, SentY, SentZ;
            public byte SentYaw, SentPitch;
            public int SentHealth = -1; public byte SentFlags;
            public bool HurtThisTick;
        }

        /// <summary> Read-only mob snapshot for the non-survival-client mirror
        /// (SurvivalFallbacks): id, feet position, yaw and model name. </summary>
        public struct MirrorMob
        {
            public ushort Id;
            public double X, Y, Z;
            public byte   Yaw;
            public string Model;
        }

        class SpawnStats
        {
            public long Ticks, Rolls, Attempts, Spawned;
            public long RejOutOfBounds, RejNoGround, RejLightMonster, RejLightAnimal, RejCap;
            public string LastSpawn = "(none yet)";
        }

        class LevelMobs
        {
            public Level Level;
            public List<SurvMob> Mobs = new List<SurvMob>();
            public bool InitialSpawned;
            public int  Cap = MAX_MOBS_PER_LEVEL; // effective standing-population cap (recomputed each tick)
            public Random Rng = new Random();
            public SpawnStats Stats = new SpawnStats();
        }

        static readonly Dictionary<Level, LevelMobs> registry = new Dictionary<Level, LevelMobs>();
        static readonly object registryLock = new object();
        static ushort nextMobId = 1;
        static Scheduler scheduler;
        static SchedulerTask tickTask;

        public const int MAX_MOBS_PER_LEVEL = 256; // matches the client's MOB_MAX pool

        public static void Start() {
            if (scheduler == null) scheduler = new Scheduler("MCG_SurvivalMobs");
            if (tickTask != null) return;
            tickTask = scheduler.QueueRepeat(Tick, null, TimeSpan.FromMilliseconds(50));
        }

        public static void Stop() {
            if (tickTask != null && scheduler != null) scheduler.Cancel(tickTask);
            tickTask = null;
            lock (registryLock) registry.Clear();
        }


        // ==================== per-level registry ====================

        static LevelMobs GetLevel(Level lvl, bool create) {
            lock (registryLock) {
                LevelMobs lm;
                if (registry.TryGetValue(lvl, out lm)) return lm;
                if (!create) return null;
                lm = new LevelMobs { Level = lvl };
                registry[lvl] = lm;
                return lm;
            }
        }

        /// <summary> Streams every live mob on the level to a player (their per-map
        /// handshake). Called from SurvivalNet.SendHandshake. </summary>
        public static void SendLevelMobs(Player p, Level lvl) {
            LevelMobs lm = GetLevel(lvl, false);
            if (lm == null) return;
            lock (lm.Mobs) {
                foreach (SurvMob m in lm.Mobs) SendSpawn(p, m);
            }
        }


        // ==================== streaming ====================

        static Player[] Watchers(Level lvl) {
            Player[] players = PlayerInfo.Online.Items;
            List<Player> result = new List<Player>();
            foreach (Player p in players)
            {
                if (p.level == lvl && SurvivalNet.Active(p, lvl)) result.Add(p);
            }
            return result.ToArray();
        }

        // EVERY player on the level, survival or not. Classic spectators keep the
        // sim alive (mobs roam for them via the mirror); only survival clients
        // are streamed to, hazard-ticked, or targeted by hostile AI.
        static Player[] AnyPlayers(Level lvl) {
            Player[] players = PlayerInfo.Online.Items;
            List<Player> result = new List<Player>();
            foreach (Player p in players)
            {
                if (p.level == lvl) result.Add(p);
            }
            return result.ToArray();
        }

        static short Fixed(double v)  { return (short)Math.Round(v * 32.0); }
        static byte  Angle(float deg) {
            int a = (int)Math.Round(deg * 256.0 / 360.0);
            return (byte)(((a % 256) + 256) % 256);
        }

        static byte SpawnFlags(SurvMob m) {
            byte flags = 0;
            if (m.HasHelmet) flags |= 0x01;
            if (m.HasArmor)  flags |= 0x02;
            if (m.HasFur)    flags |= 0x04;
            return flags;
        }

        static byte StateFlags(SurvMob m) {
            byte flags = 0;
            if (m.HurtThisTick)       flags |= 0x01; // hurt
            if (m.FuseState > 0)      flags |= 0x02; // fuse
            if (m.Fire > 0)           flags |= 0x04; // onFire
            if (m.Grazing)            flags |= 0x08; // graze
            if (m.Dead)               flags |= 0x10; // dead
            if (!m.HasFur)            flags |= 0x20; // noFur (visible shear)
            return flags;
        }

        static void SendSpawn(Player p, SurvMob m) {
            byte[] msg = new byte[Packet.PluginMessageDataLength];
            short x = Fixed(m.X), y = Fixed(m.Y), z = Fixed(m.Z);
            msg[0]  = SurvivalNet.MOB_SPAWN;
            msg[1]  = (byte)(m.Id >> 8); msg[2] = (byte)m.Id;
            msg[3]  = m.Type;
            msg[4]  = (byte)(x >> 8); msg[5]  = (byte)x;
            msg[6]  = (byte)(y >> 8); msg[7]  = (byte)y;
            msg[8]  = (byte)(z >> 8); msg[9]  = (byte)z;
            msg[10] = Angle(m.Yaw);
            msg[11] = Angle(m.Pitch);
            msg[12] = (byte)m.Health;
            msg[13] = SpawnFlags(m);
            SurvivalNet.SendMessage(p, msg);
        }

        static void BroadcastSpawn(Level lvl, SurvMob m) {
            foreach (Player p in Watchers(lvl)) SendSpawn(p, m);
            m.SentX = Fixed(m.X); m.SentY = Fixed(m.Y); m.SentZ = Fixed(m.Z);
            m.SentYaw = Angle(m.Yaw); m.SentPitch = Angle(m.Pitch);
            m.SentHealth = m.Health; m.SentFlags = StateFlags(m);
        }

        static void StreamMob(Level lvl, Player[] watchers, SurvMob m) {
            short x = Fixed(m.X), y = Fixed(m.Y), z = Fixed(m.Z);
            byte yaw = Angle(m.Yaw), pitch = Angle(m.Pitch);
            byte flags = StateFlags(m);

            if (x != m.SentX || y != m.SentY || z != m.SentZ || yaw != m.SentYaw || pitch != m.SentPitch) {
                byte[] msg = new byte[Packet.PluginMessageDataLength];
                msg[0] = SurvivalNet.MOB_MOVE;
                msg[1] = (byte)(m.Id >> 8); msg[2] = (byte)m.Id;
                msg[3] = (byte)(x >> 8); msg[4] = (byte)x;
                msg[5] = (byte)(y >> 8); msg[6] = (byte)y;
                msg[7] = (byte)(z >> 8); msg[8] = (byte)z;
                msg[9] = yaw; msg[10] = pitch;
                foreach (Player p in watchers) SurvivalNet.SendMessage(p, msg);
                m.SentX = x; m.SentY = y; m.SentZ = z;
                m.SentYaw = yaw; m.SentPitch = pitch;
            }

            if (m.Health != m.SentHealth || flags != m.SentFlags) {
                byte[] msg = new byte[Packet.PluginMessageDataLength];
                msg[0] = SurvivalNet.MOB_STATE;
                msg[1] = (byte)(m.Id >> 8); msg[2] = (byte)m.Id;
                msg[3] = (byte)Math.Max(0, m.Health);
                msg[4] = flags;
                foreach (Player p in watchers) SurvivalNet.SendMessage(p, msg);
                m.SentHealth = m.Health; m.SentFlags = flags;
            }
            m.HurtThisTick = false;
        }

        static void BroadcastDespawn(Level lvl, SurvMob m, byte reason) {
            byte[] msg = new byte[Packet.PluginMessageDataLength];
            msg[0] = SurvivalNet.MOB_DESPAWN;
            msg[1] = (byte)(m.Id >> 8); msg[2] = (byte)m.Id;
            msg[3] = reason;
            foreach (Player p in Watchers(lvl)) SurvivalNet.SendMessage(p, msg);
        }


        // ==================== world helpers ====================

        // Genuine World.getBlockId CLAMPS out-of-bounds coords to the edge block
        // (client's Mob_BlockIsSolid note: this is what stops floating-map voids
        // from reading as solid ground).
        static BlockID BlockAt(Level lvl, int x, int y, int z) {
            if (x < 0) x = 0; else if (x >= lvl.Width)  x = lvl.Width  - 1;
            if (y < 0) y = 0; else if (y >= lvl.Height) y = lvl.Height - 1;
            if (z < 0) z = 0; else if (z >= lvl.Length) z = lvl.Length - 1;
            return lvl.GetBlock((ushort)x, (ushort)y, (ushort)z);
        }

        static bool IsSolidAt(Level lvl, int x, int y, int z) {
            return CollideType.IsSolid(lvl.CollideType(BlockAt(lvl, x, y, z)));
        }

        static bool BoxFree(Level lvl, SurvMob m, double x, double y, double z) {
            float w = Width(lvl, m) / 2, h = Height(lvl, m);
            int minX = (int)Math.Floor(x - w), maxX = (int)Math.Floor(x + w - 0.001);
            int minY = (int)Math.Floor(y),     maxY = (int)Math.Floor(y + h - 0.001);
            int minZ = (int)Math.Floor(z - w), maxZ = (int)Math.Floor(z + w - 0.001);

            // The map edge is a wall for mobs: BlockAt clamps out-of-bounds reads
            // to the edge column, which reads as open air above ground - knockback
            // was punting mobs clean off the map (live-testing report).
            if (minX < 0 || maxX >= lvl.Width || minZ < 0 || maxZ >= lvl.Length) return false;

            for (int by = minY; by <= maxY; by++)
                for (int bz = minZ; bz <= maxZ; bz++)
                    for (int bx = minX; bx <= maxX; bx++)
            {
                if (IsSolidAt(lvl, bx, by, bz)) return false;
            }
            return true;
        }

        static float Width(Level lvl, SurvMob m) {
            return lvl.Config.SurvivalMode == SurvivalMode.Indev ? Types[m.Type].WIndev : Types[m.Type].W030;
        }
        static float Height(Level lvl, SurvMob m) {
            return lvl.Config.SurvivalMode == SurvivalMode.Indev ? Types[m.Type].HIndev : Types[m.Type].H030;
        }

        // Liquid test: any block the bounding box overlaps with a liquid collide type
        static bool InLiquid(Level lvl, SurvMob m, bool lava) {
            float w = Width(lvl, m) / 2, h = Height(lvl, m);
            int minX = (int)Math.Floor(m.X - w), maxX = (int)Math.Floor(m.X + w - 0.001);
            int minY = (int)Math.Floor(m.Y),     maxY = (int)Math.Floor(m.Y + h - 0.001);
            int minZ = (int)Math.Floor(m.Z - w), maxZ = (int)Math.Floor(m.Z + w - 0.001);

            for (int by = minY; by <= maxY; by++)
                for (int bz = minZ; bz <= maxZ; bz++)
                    for (int bx = minX; bx <= maxX; bx++)
            {
                byte collide = lvl.CollideType(BlockAt(lvl, bx, by, bz));
                if (lava  && collide == CollideType.LiquidLava)  return true;
                if (!lava && (collide == CollideType.LiquidWater || collide == CollideType.SwimThrough)) return true;
            }
            return false;
        }

        // "Brightness" approximation (no server-side light engine): a column open
        // to the sky uses the day/night sky light, anything under cover is dark.
        static bool SkyExposed(Level lvl, SurvMob m) {
            int x = (int)Math.Floor(m.X), z = (int)Math.Floor(m.Z);
            int top = lvl.Height - 1;
            for (int y = (int)Math.Floor(m.Y + Height(lvl, m)); y <= top; y++)
            {
                if (x < 0 || z < 0 || x >= lvl.Width || z >= lvl.Length) return true;
                if (IsSolidAt(lvl, x, y, z)) return false;
            }
            return true;
        }

        static bool IsBright(Level lvl, SurvMob m) {
            return SurvivalNet.CurrentSkyLight() > 7 && SkyExposed(lvl, m);
        }


        // ==================== physics (Mob.travel port) ====================

        // Mob_MoveRelative: convert strafe/forward intent into a velocity kick
        // along the mob's yaw. Uses the same basis the client derived for CC's
        // yaw convention (dir.x = sin(yaw), dir.z = -cos(yaw)).
        static void MoveRelative(SurvMob m, float strafe, float forward, float friction) {
            float dist = strafe * strafe + forward * forward;
            if (dist < 0.0001f) return;
            dist = (float)Math.Sqrt(dist);
            if (dist < 1) dist = 1;
            dist = friction / dist;
            strafe *= dist; forward *= dist;

            double sinYaw = Math.Sin(m.Yaw * Math.PI / 180.0);
            double cosYaw = Math.Cos(m.Yaw * Math.PI / 180.0);
            m.VX += forward * sinYaw + strafe * cosYaw;
            m.VZ += strafe  * sinYaw - forward * cosYaw;
        }

        // Axis-clipped move in sub-steps (Entity.move lineage: clip Y, then X,
        // then Z, zeroing a clipped axis). c0.30 mobs have no step-up assist
        // (Entity.footSize is only set on Player) - they jump instead.
        static void MoveClipped(Level lvl, SurvMob m) {
            double dx = m.VX, dy = m.VY, dz = m.VZ;
            double biggest = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
            int steps = (int)Math.Ceiling(biggest / 0.25);
            if (steps < 1) steps = 1;
            double sx = dx / steps, sy = dy / steps, sz = dz / steps;
            bool hitX = false, hitY = false, hitZ = false;
            m.OnGround = false;

            for (int i = 0; i < steps; i++)
            {
                if (!hitY && sy != 0) {
                    if (BoxFree(lvl, m, m.X, m.Y + sy, m.Z)) m.Y += sy;
                    else { hitY = true; if (sy < 0) m.OnGround = true; m.VY = 0; }
                }
                if (!hitX && sx != 0) {
                    if (BoxFree(lvl, m, m.X + sx, m.Y, m.Z)) m.X += sx;
                    else { hitX = true; m.VX = 0; }
                }
                if (!hitZ && sz != 0) {
                    if (BoxFree(lvl, m, m.X, m.Y, m.Z + sz)) m.Z += sz;
                    else { hitZ = true; m.VZ = 0; }
                }
            }
        }

        static void Travel(Level lvl, SurvMob m, bool inWater, bool inLava) {
            if (inWater || inLava) {
                // Mob.travel's water/lava branches: identical bar the drag factor
                double drag = inWater ? 0.8 : 0.5;
                MoveRelative(m, m.MoveStrafe, m.MoveForward, 0.02f);
                bool blockedBefore = !BoxFree(lvl, m, m.X + m.VX, m.Y, m.Z) ||
                                     !BoxFree(lvl, m, m.X, m.Y, m.Z + m.VZ);
                MoveClipped(lvl, m);
                m.VX *= drag; m.VY *= drag; m.VZ *= drag;
                m.VY -= 0.02;
                // paddle-up assist when pushing against terrain (client's approximation)
                if (blockedBefore) m.VY = 0.3;
            } else {
                float friction = m.OnGround ? 0.1f : 0.02f;
                MoveRelative(m, m.MoveStrafe, m.MoveForward, friction);
                MoveClipped(lvl, m);
                m.VX *= 0.91; m.VY *= 0.98; m.VZ *= 0.91;
                m.VY -= 0.08;
                if (m.OnGround) { m.VX *= 0.6; m.VZ *= 0.6; }
            }
        }

        // Entity.push(Entity): overlapping entities shove each other apart along the
        // horizontal centre-to-centre vector (genuine c0.30/Indev applyEntityCollision).
        // Only the MOB is pushed here (players own their own movement in Classic), which
        // reads as the mob being nudged aside when a player walks into it.
        static void PushApart(Level lvl, LevelMobs lm, SurvMob m, Player[] viewers) {
            double mw = Width(lvl, m), mh = Height(lvl, m);

            // ...away from players (skip hidden staff / spectators - a mob shoved by
            // an invisible body looks like a ghost pushing it)
            foreach (Player p in viewers)
            {
                if (p.hidden) continue;
                double px = p.Pos.X / 32.0, pz = p.Pos.Z / 32.0;
                double py = (p.Pos.Y - Entities.CharacterHeight) / 32.0;
                if (py >= m.Y + mh || py + 1.8 <= m.Y) continue;         // no vertical overlap
                if (!HorizOverlap(m.X, m.Z, px, pz, mw, 0.6)) continue;
                PushVec(m, m.X - px, m.Z - pz);
            }

            // ...apart from other mobs (so a cluster doesn't pile into one column)
            foreach (SurvMob o in lm.Mobs)
            {
                if (o == m || o.Dead) continue;
                if (o.Y >= m.Y + mh || o.Y + Height(lvl, o) <= m.Y) continue;
                if (!HorizOverlap(m.X, m.Z, o.X, o.Z, mw, Width(lvl, o))) continue;
                PushVec(m, m.X - o.X, m.Z - o.Z);
            }
        }

        // AABB overlap of two entity boxes (feet-centred widths) grown 0.2 horizontally,
        // matching findEntities(this, bb.grow(0.2, 0, 0.2)).
        static bool HorizOverlap(double ax, double az, double bx, double bz, double aw, double bw) {
            double r = aw / 2 + bw / 2 + 0.2;
            return Math.Abs(ax - bx) < r && Math.Abs(az - bz) < r;
        }

        // Entity.push(x, z): normalise the (already centre-relative) offset, clamp the
        // 1/dist boost to 1, scale by 0.05, and add to the mob's velocity.
        static void PushVec(SurvMob m, double xd, double zd) {
            double dist = Math.Max(Math.Abs(xd), Math.Abs(zd));
            if (dist < 0.01) return;
            dist = Math.Sqrt(dist);
            xd /= dist; zd /= dist;
            double f = 1.0 / dist; if (f > 1.0) f = 1.0;
            xd *= f * 0.05; zd *= f * 0.05;
            m.VX += xd; m.VZ += zd;
        }

        static void DoJump(SurvMob m, bool inWater, bool inLava, bool spiderLunge) {
            if (!m.Jumping) return;
            if (inWater || inLava) {
                m.VY += 0.04;
            } else if (m.OnGround) {
                if (spiderLunge) {
                    // JumpAttackAI.jumpFromGround's attackTarget branch: forward lunge
                    m.VX = 0; m.VZ = 0;
                    MoveRelative(m, 0, 1, 0.6f);
                    m.VY = 0.5;
                } else {
                    m.VY = 0.42;
                }
            }
        }


        // ==================== combat ====================

        /// <summary> Mob.hurt()'s dual-threshold invulnerability + knockback + aggro.
        /// attacker may be null (environment). Returns whether the hit landed. </summary>
        static bool HurtMob(Level lvl, LevelMobs lm, SurvMob m, Player attacker, int damage) {
            if (m.Dead || m.Health <= 0 || damage <= 0) return false;

            // BasicAttackAI.hurt: aggro onto the attacker on every hit
            if (attacker != null && !Types[m.Type].Passive) m.Target = attacker;
            m.NoActionTime = 0;

            if (m.InvincTicks > 10) {
                if (m.LastHealth - damage >= m.Health) return false; // absorbed
                m.Health = m.LastHealth - damage;
            } else {
                m.LastHealth  = m.Health;
                m.InvincTicks = 20;
                m.Health     -= damage;
            }
            m.HurtThisTick = true;

            if (attacker != null) {
                double ax = attacker.Pos.X / 32.0, az = attacker.Pos.Z / 32.0;
                double dx = ax - m.X, dz = az - m.Z;
                double dist = Math.Sqrt(dx * dx + dz * dz);
                if (dist >= 0.0001) {
                    m.VX = m.VX / 2 - dx / dist * 0.4;
                    m.VZ = m.VZ / 2 - dz / dist * 0.4;
                }
                m.VY = Math.Min(m.VY / 2 + 0.4, 0.4);
            }

            if (m.Health <= 0) KillMob(lvl, lm, m, attacker);
            return true;
        }

        // Indev EntityLiving.onDeath reinterprets scoreValue() as the death-drop
        // item id: 0-2 of it per death (client indevDeathDrop). Sheep drop nothing
        // on death (their wool comes from the shear).
        static readonly ushort[] indevDeathDrop = {
            256 + 32, // ZOMBIE   -> feather
            256 + 6,  // SKELETON -> arrow (item)
            256 + 63, // PIG      -> raw porkchop
            256 + 33, // CREEPER  -> gunpowder
            256 + 31, // SPIDER   -> string
            0         // SHEEP    -> nothing
        };

        /// <summary> Arrow-vs-mob hit test (SurvivalArrows): the arrow box centred at
        /// (ax,ay,az) is tested against every live mob on the level (skipping the
        /// shooter mob); the first overlap takes HurtMob damage + aggro credit.
        /// Returns whether a mob was hit. </summary>
        public static bool TryArrowHitMob(Level lvl, double ax, double ay, double az,
                                          double halfW, double halfH, int ownerMobId,
                                          int damage, Player ownerPlayer) {
            LevelMobs lm = GetLevel(lvl, false);
            if (lm == null) return false;
            lock (lm.Mobs) {
                foreach (SurvMob m in lm.Mobs)
                {
                    if (m.Dead || m.Health <= 0) continue;
                    if (m.Id == ownerMobId) continue; // never self-hit the shooter
                    double hw = Width(lvl, m) / 2.0, h = Height(lvl, m);
                    if (ax + halfW < m.X - hw || ax - halfW > m.X + hw) continue;
                    if (ay + halfH < m.Y      || ay - halfH > m.Y + h)  continue;
                    if (az + halfW < m.Z - hw || az - halfW > m.Z + hw) continue;
                    HurtMob(lvl, lm, m, ownerPlayer, damage);
                    return true;
                }
            }
            return false;
        }

        static void KillMob(Level lvl, LevelMobs lm, SurvMob m, Player killer) {
            m.Health = 0;
            m.Dead   = true;
            m.DeathTicks = 0;
            // Mob.deathScore, credited only on a player kill and only in c0.30 mode
            // (Indev has no score) - client mobTypeInfo's deathScore column.
            if (killer != null && lvl.Config.SurvivalMode == SurvivalMode.Classic) {
                int[] scores = { 80, 120, 10, 200, 105, 10 };
                SurvivalNet.AddScore(killer, scores[m.Type]);
            }

            // phase 5 death drops (spawned at the mob's feet position)
            if (lvl.Config.SurvivalMode == SurvivalMode.Indev) {
                ushort item = indevDeathDrop[m.Type];
                if (item != 0) {
                    int n = lm.Rng.Next(3); // 0-2, genuine rand(3)
                    SurvivalDrops.SpawnScatter(lvl, m.X, m.Y, m.Z, item, n, SurvivalDrops.MinedDelay(lvl));
                }
            } else if (m.Type == TYPE_PIG || m.Type == TYPE_SHEEP) {
                // c0.30 Pig.die/Sheep.die both drop 1-2 brown mushrooms
                int n = (int)(lm.Rng.NextDouble() + lm.Rng.NextDouble() + 1.0);
                SurvivalDrops.SpawnScatter(lvl, m.X, m.Y, m.Z, Block.Mushroom, n, 0);
            }
        }

        // Genuine c0.30 death-explosion (client Mob_CreeperExplode): radius ~4,
        // entity damage only in v1 (block destruction deliberately not ported yet).
        static void CreeperExplode(Level lvl, SurvMob m, float radius) {
            Player[] players = PlayerInfo.Online.Items;
            foreach (Player p in players)
            {
                if (p.level != lvl || !SurvivalNet.Active(p, lvl)) continue;
                double px = p.Pos.X / 32.0, py = (p.Pos.Y - Entities.CharacterHeight) / 32.0, pz = p.Pos.Z / 32.0;
                double dx = px - m.X, dy = py - m.Y, dz = pz - m.Z;
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dist >= radius) continue;
                // Approximate falloff: full-strength up close, linear to 0 at the edge
                // (the genuine density-raycast falloff needs the block-destruction port)
                int damage = (int)((1.0 - dist / radius) * 15.0 + 1.0);
                SurvivalNet.DamagePlayer(p, damage, "@p was blown up by a creeper");
            }
        }

        /// <summary> Handles a SURV_ATTACK intent: validates reach + state, then applies
        /// the player's melee to the target mob. Called from SurvivalNet. </summary>
        public static void HandleAttack(Player p, int targetKind, int targetId) {
            if (targetKind != 0) return; // player targets = PvP, phase-later
            Level lvl = p.level;
            if (lvl == null || !SurvivalNet.Active(p, lvl) || SurvivalNet.IsDead(p)) return;
            LevelMobs lm = GetLevel(lvl, false);
            if (lm == null) return;

            lock (lm.Mobs) {
                SurvMob m = null;
                foreach (SurvMob mob in lm.Mobs) { if (mob.Id == targetId) { m = mob; break; } }
                if (m == null || m.Dead) return;

                // Reach validation: eye-to-mob within the survival reach (4 blocks,
                // padded to 6 for latency - the mob has moved since the client swung)
                double px = p.Pos.X / 32.0, py = p.Pos.Y / 32.0, pz = p.Pos.Z / 32.0;
                double dx = px - m.X, dy = py - (m.Y + Height(lvl, m) / 2), dz = pz - m.Z;
                if (dx * dx + dy * dy + dz * dz > 6 * 6) {
                    Logger.Log(LogType.Debug, "survival: rejected attack from {0} (out of reach)", p.name);
                    return;
                }

                // Sheep shear before damage: c0.30 replaces the hit entirely; Indev
                // shears AND falls through to damage. A punched furred sheep scatters
                // wool (Indev 1+rand(3) GRAY cloth at head height; c0.30 1-3 WHITE).
                bool indev = lvl.Config.SurvivalMode == SurvivalMode.Indev;
                if (m.Type == TYPE_SHEEP && m.HasFur) {
                    m.HasFur = false;
                    if (indev) {
                        int wool = 1 + lm.Rng.Next(3);
                        SurvivalDrops.SpawnScatter(lvl, m.X, m.Y + 1.0, m.Z, Block.Gray, wool, SurvivalDrops.MinedDelay(lvl));
                    } else {
                        int wool = (int)(lm.Rng.NextDouble() * 3.0 + 1.0);
                        SurvivalDrops.SpawnScatter(lvl, m.X, m.Y, m.Z, Block.White, wool, 0);
                        return; // c0.30: shear replaces the hit entirely
                    }
                }

                // Bare-fist damage: c0.30 flat 4, Indev fist 1 (held-item damage
                // tables arrive with phase 4's server-side inventory).
                HurtMob(lvl, lm, m, p, indev ? 1 : 4);
            }
        }


        // ==================== AI (BasicAI / BasicAttackAI port) ====================

        static void WanderAI(LevelMobs lm, SurvMob m, bool indev, bool inWater, bool inLava) {
            MobType info = Types[m.Type];
            float speed = indev ? IndevMoveSpeed(m.Type) : info.RunSpeed;
            Random rng = lm.Rng;

            if (rng.Next(100) < 7) {
                m.MoveStrafe  = (float)(rng.NextDouble() - 0.5) * speed;
                m.MoveForward = (float)rng.NextDouble() * speed;
            }
            m.Jumping = rng.Next(100) < 1;

            if (rng.Next(100) < 4) {
                m.TurnRate = (float)(rng.NextDouble() - 0.5) * 60.0f;
            }
            m.Yaw  += m.TurnRate;
            m.Pitch = indev ? 0 : info.LookAngle;

            if (m.Target != null && !indev) {
                m.MoveForward = speed;
                m.Jumping = rng.Next(100) < 4;
            }
            // BasicAI.update: the water/lava bob roll applies to EVERY mob
            if (inWater || inLava) m.Jumping = rng.Next(100) < 80;
        }

        static void AttackAI(Level lvl, LevelMobs lm, SurvMob m, bool indev, Player[] watchers) {
            MobType info = Types[m.Type];
            Random rng = lm.Rng;
            Player target = m.Target;

            // drop a target that left / died / went to another level
            if (target != null && (target.level != lvl || target.Session == null ||
                                   !target.Session.hasSurvival || SurvivalNet.IsDead(target))) {
                m.Target = null; target = null;
            }

            // Only players are acquired by proximity (aggroRange = 16); mob-vs-mob
            // aggro comes from being hurt, which v1 doesn't have a source for yet.
            if (target == null) {
                double bestSq = 256.0;
                foreach (Player p in watchers)
                {
                    if (SurvivalNet.IsDead(p)) continue;
                    double dx = p.Pos.X / 32.0 - m.X, dy = (p.Pos.Y - Entities.CharacterHeight) / 32.0 - m.Y,
                           dz = p.Pos.Z / 32.0 - m.Z;
                    double distSq = dx * dx + dy * dy + dz * dz;
                    if (distSq <= bestSq) { bestSq = distSq; m.Target = p; }
                }
                target = m.Target;
                if (target == null) {
                    // A creeper that lost its target (player died / disconnected /
                    // left the level / the >32-block give-up fired) must keep
                    // winding its fuse back down every tick, exactly like the
                    // client's unconditional Mob_IndevCreatureUpdate
                    // (SurvivalTest.c:3942-3949). Without this the fuse latches
                    // (FuseState=1, FuseTicks frozen high) and the creeper
                    // detonates almost instantly when a player re-enters range.
                    if (indev && info.IsCreeper && (m.FuseState > 0 || m.FuseTicks > 0)) {
                        m.FuseState = -1;
                        if (m.FuseTicks > 0) m.FuseTicks--;
                    }
                    return;
                }
            }

            double tx = target.Pos.X / 32.0, ty = (target.Pos.Y - Entities.CharacterHeight) / 32.0,
                   tz = target.Pos.Z / 32.0;
            double ddx = tx - m.X, ddy = ty - m.Y, ddz = tz - m.Z;
            double dSq = ddx * ddx + ddy * ddy + ddz * ddz;
            double dist = Math.Sqrt(dSq);

            if (dSq > 1024.0 && rng.Next(100) == 0) { m.Target = null; return; } // 2x range give-up

            // face the victim (BasicAttackAI.doAttack); pitch's adjacent is the
            // full 3D distance - the genuine mild under-pitch quirk
            m.Yaw   = (float)(Math.Atan2(ddx, -ddz) * 180.0 / Math.PI);
            m.Pitch = (float)(Math.Atan2(-ddy, dist) * 180.0 / Math.PI);

            // chase: stride toward the victim (the c0.30 target branch in WanderAI
            // pushes forward; Indev v1 reuses it pending the A* port)
            float speed = indev ? IndevMoveSpeed(m.Type) : info.RunSpeed;
            m.MoveForward = speed;
            if (rng.Next(100) < 4) m.Jumping = true;

            if (indev) IndevAttack(lvl, lm, m, target, dist, rng);
            else       ClassicAttack(lvl, lm, m, target, dSq, rng);
        }

        static void ClassicAttack(Level lvl, LevelMobs lm, SurvMob m, Player target, double dSq, Random rng) {
            // c0.30 SkeletonAI.tick: a targeted skeleton has a 1/30 per-tick chance to
            // loose an arrow (Mob_ShootArrow), on top of - not instead of - the melee
            // below, at any range. Fired from the eye with the genuine asymmetric
            // spread (yaw +/-22.5, pitch biased upward).
            if (m.Type == TYPE_SKELETON && rng.Next(30) == 0) {
                double sy  = m.Yaw   + (rng.NextDouble() * 45.0 - 22.5);
                double sp  = m.Pitch - (rng.NextDouble() * 45.0 - 10.0);
                double eye = m.Y + Height(lvl, m) * 0.85;
                SurvivalArrows.FireFromMobC030(lvl, m.Id, m.X, eye, m.Z, sy, sp);
            }

            MobType info = Types[m.Type];
            if (dSq >= 4.0 || m.AttackDelay > 0) return;

            m.AttackDelay  = 10 + rng.Next(20);
            m.NoActionTime = 0;
            int damage = (int)((rng.NextDouble() + rng.NextDouble()) / 2.0 * info.Damage + 1.0);
            SurvivalNet.DamagePlayer(target, damage, "@p was slain by a " + info.Name);

            // CreeperAI.attack: headbutting hurts the creeper WITH ITS VICTIM AS
            // CAUSE; the self-damage death triggers the c0.30 death-explosion.
            if (info.IsCreeper) {
                if (m.InvincTicks > 10) {
                    if (m.LastHealth - 6 < m.Health) { m.Health = m.LastHealth - 6; m.HurtThisTick = true; }
                } else {
                    m.LastHealth = m.Health; m.InvincTicks = 20;
                    m.Health -= 6; m.HurtThisTick = true;
                }
                if (m.Health <= 0) KillMob(lvl, lm, m, target);
            }
        }

        static void IndevAttack(Level lvl, LevelMobs lm, SurvMob m, Player target, double dist, Random rng) {
            if (Types[m.Type].IsCreeper) {
                // EntityCreeper.attackEntity: fuse starts within 3 blocks, keeps
                // burning within 7 once lit, blows at 30 ticks.
                if ((m.FuseState <= 0 && dist < 3.0) || (m.FuseState > 0 && dist < 7.0)) {
                    m.FuseState = 1;
                    m.FuseTicks++;
                    m.MoveForward = 0; // stands its ground while swelling
                    if (m.FuseTicks >= 30) {
                        // Indev fuse blast (client Mob_IndevCreeperBlast): radius 3
                        CreeperExplode(lvl, m, 3.0f);
                        KillMob(lvl, lm, m, null);
                        m.DeathTicks = 20; // blast leaves no corpse window
                    }
                } else {
                    m.FuseState = -1;
                    if (m.FuseTicks > 0) m.FuseTicks--;
                }
                return;
            }

            if (m.Type == TYPE_SPIDER) {
                // EntitySpider.attackEntity: light makes it lose interest; a 2-6
                // block pounce roll; otherwise the shared melee below.
                if (IsBright(lvl, m) && rng.Next(100) == 0) { m.Target = null; return; }
                if (dist > 2.0 && dist < 6.0 && rng.Next(10) == 0) {
                    if (m.OnGround) {
                        double dx = target.Pos.X / 32.0 - m.X, dz = target.Pos.Z / 32.0 - m.Z;
                        double hor = Math.Sqrt(dx * dx + dz * dz);
                        m.VX = dx / hor * 0.5 * 0.8 + m.VX * 0.2;
                        m.VZ = dz / hor * 0.5 * 0.8 + m.VZ * 0.2;
                        m.VY = 0.4;
                    }
                    return;
                }
            }

            if (m.Type == TYPE_SKELETON) {
                // Indev EntitySkeleton.attackEntity: bow fire within 10 blocks on a
                // 30-tick cooldown, standing still - NO melee, and (unlike c0.30) NO
                // death fire-burst (Indev skeletons drop 0-2 arrow ITEMS on death,
                // handled in KillMob). Mob_IndevShootArrow: the raw unnormalized aim
                // into setArrowHeading(0.6, 12.0), spawned from the offset eye.
                if (dist < 10.0 && m.AttackDelay == 0) {
                    double yawRad = m.Yaw * Math.PI / 180.0;
                    double fromX  = m.X + Math.Cos(yawRad) * 0.16;
                    double fromY  = m.Y + Height(lvl, m) * 0.85 - 0.1 + 1.0; // eye - 0.1 + shootArrow ++posY
                    double fromZ  = m.Z + Math.Sin(yawRad) * 0.16;
                    double aimX   = target.Pos.X / 32.0 - m.X;
                    double aimZ   = target.Pos.Z / 32.0 - m.Z;
                    double aimY   = (target.Pos.Y / 32.0 - 0.2) - fromY; // aim at the target's eye - 0.2
                    double hor    = Math.Sqrt(aimX * aimX + aimZ * aimZ);
                    aimY += hor * 0.2; // the lob that clears mid-range dips
                    SurvivalArrows.FireFromMobIndev(lvl, m.Id, fromX, fromY, fromZ, aimX, aimY, aimZ);
                    m.AttackDelay = 30;
                }
                return; // skeleton never melees in Indev
            }

            // EntityMob.attackEntity: melee within 2.5 blocks (zombie 5, default 2).
            int strength = Types[m.Type].IndevMelee;
            if (strength == 0 || dist >= 2.5 || m.AttackDelay > 0) return;
            m.AttackDelay  = 10;
            m.NoActionTime = 0;
            SurvivalNet.DamagePlayer(target, strength, "@p was slain by a " + Types[m.Type].Name);
        }


        // ==================== spawning ====================

        // c0.30 prepareLevel's one-time population: map-wide random points, kept
        // clear of the level spawn point (MobSpawner.spawn's else branch).
        static void InitialSpawnerRun(Level lvl, LevelMobs lm, int attempts) {
            Random rng = lm.Rng;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (lm.Mobs.Count >= lm.Cap) return;
                int x = rng.Next(lvl.Width);
                int z = rng.Next(lvl.Length);
                double sx = lvl.spawnx - (x + 0.5), sz = lvl.spawnz - (z + 0.5);
                if (sx * sx + sz * sz < 256.0) continue; // 16 blocks of the level spawn
                TrySpawnCluster(lvl, lm, x, z);
            }
        }

        // Ongoing top-up: candidates in a ring 16..48 blocks around a random online
        // survival player, so the 256-mob budget concentrates where players ARE.
        // (Deviation from c0.30's map-wide roll, which on big maps saturated the
        // cap with mobs nobody ever met - "they spawned once when I entered, then
        // never again". The Alpha+ spawners made the same change for the same
        // reason. Y keeps the genuine min-of-two-uniforms low-altitude bias.)
        static void TopUpSpawnerRun(Level lvl, LevelMobs lm, Player[] viewers, int attempts) {
            Random rng = lm.Rng;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (lm.Mobs.Count >= lm.Cap) { lm.Stats.RejCap++; return; }
                lm.Stats.Attempts++;
                Player near = viewers[rng.Next(viewers.Length)];
                double ang  = rng.NextDouble() * 2 * Math.PI;
                double dist = 16 + rng.NextDouble() * 32;
                int x = (int)Math.Floor(near.Pos.X / 32.0 + Math.Cos(ang) * dist);
                int z = (int)Math.Floor(near.Pos.Z / 32.0 + Math.Sin(ang) * dist);
                TrySpawnCluster(lvl, lm, x, z);
            }
        }

        static readonly byte[] monsterTypes = { TYPE_ZOMBIE, TYPE_SKELETON, TYPE_CREEPER, TYPE_SPIDER };
        static readonly byte[] animalTypes  = { TYPE_PIG, TYPE_SHEEP };

        // The old fully-random Y wasted ~97% of attempts underground or in the air
        // ("awfully slow for mobs to spawn" - live-testing report), and pre-rolling
        // the type wasted most of the rest on the light rule. Now the COLUMN is
        // scanned for every standable spot (surface and caves alike), one is
        // picked, and its darkness picks the type POOL: dark spots roll monsters,
        // lit spots roll animals (the same Indev outcome, none of the waste).
        static void TrySpawnCluster(Level lvl, LevelMobs lm, int x, int z) {
            Random rng  = lm.Rng;
            bool indev  = lvl.Config.SurvivalMode == SurvivalMode.Indev;

            if (x < 0 || z < 0 || x >= lvl.Width || z >= lvl.Length) { lm.Stats.RejOutOfBounds++; return; }

            int found = 0, y = -1;
            for (int cy = 1; cy < lvl.Height - 1; cy++)
            {
                if (!SpawnValid(lvl, x, cy, z)) continue;
                found++;
                if (rng.Next(found) == 0) y = cy; // uniform pick over valid spots
            }
            if (y < 0) { lm.Stats.RejNoGround++; return; }

            byte type;
            if (indev) {
                bool dark = !ColumnLit(lvl, x, y, z);
                byte[] pool = dark ? monsterTypes : animalTypes;
                type = pool[rng.Next(pool.Length)];
            } else {
                type = (byte)rng.Next(SPAWN_TYPES); // c0.30 has no light rule
            }

            // scatter a small same-type cluster around the point (up to 3 in
            // v1 - the genuine 9-roll cluster with jitter walks is trimmed to
            // keep server populations tame)
            int cluster = 1 + rng.Next(3);
            for (int i = 0; i < cluster && lm.Mobs.Count < lm.Cap; i++)
            {
                int cx = x + rng.Next(7) - 3, cy = y, cz = z + rng.Next(7) - 3;
                if (!SpawnValid(lvl, cx, cy, cz)) continue;
                SpawnMob(lvl, lm, type, cx + 0.5, cy, cz + 0.5, (float)(rng.NextDouble() * 360.0));
                lm.Stats.Spawned++;
                lm.Stats.LastSpawn = Types[type].Name + " at (" + cx + ", " + cy + ", " + cz + ")";
                Logger.Log(LogType.Debug, "survival: spawner placed a {0} at ({1}, {2}, {3}) on {4}",
                           Types[type].Name, cx, cy, cz, lvl.name);
            }
        }

        static bool SpawnValid(Level lvl, int x, int y, int z) {
            if (x < 0 || y <= 0 || z < 0 || x >= lvl.Width || y >= lvl.Height - 1 || z >= lvl.Length) return false;
            if (!IsSolidAt(lvl, x, y - 1, z)) return false; // solid ground below
            // 2-block air column, no liquid
            for (int i = 0; i < 2; i++)
            {
                if (y + i >= lvl.Height) return false;
                byte collide = lvl.CollideType(BlockAt(lvl, x, y + i, z));
                if (collide != CollideType.WalkThrough) return false;
            }
            return true;
        }

        static bool ColumnLit(Level lvl, int x, int y, int z) {
            if (SurvivalNet.CurrentSkyLight() <= 7) return false; // night: everywhere is dark
            for (int by = y; by < lvl.Height; by++)
            {
                if (IsSolidAt(lvl, x, by, z)) return false;
            }
            return true;
        }

        static void SpawnMob(Level lvl, LevelMobs lm, byte type, double x, double y, double z, float yaw) {
            SurvMob m = new SurvMob();
            m.Id   = nextMobId++;
            if (nextMobId == 0) nextMobId = 1;
            m.Type = type;
            m.X = x; m.Y = y; m.Z = z; m.Yaw = yaw;
            // EntityLiving defaults to 10 HP; only EntityMob raises it to 20 - so
            // Indev pigs/sheep have 10. c0.30 mobs are a flat 20.
            bool indev = lvl.Config.SurvivalMode == SurvivalMode.Indev;
            m.Health = indev && Types[type].Passive ? 10 : 20;
            // HumanoidMob's 20% helmet/armor field initialisers - c0.30 only
            // (Indev's EntityZombie/EntitySkeleton have no such fields)
            if (!indev && (type == TYPE_ZOMBIE || type == TYPE_SKELETON)) {
                m.HasHelmet = lm.Rng.NextDouble() < 0.2;
                m.HasArmor  = lm.Rng.NextDouble() < 0.2;
            }
            lm.Mobs.Add(m);
            BroadcastSpawn(lvl, m);
        }


        // ==================== the tick ====================

        static void Tick(SchedulerTask task) {
            try { TickCore(); } catch (Exception ex) {
                Logger.LogError("Error in the survival mob tick", ex);
            }
        }

        static void TickCore() {
            Level[] loaded = LevelInfo.Loaded.Items;
            List<Level> dead = null;

            foreach (Level lvl in loaded)
            {
                if (lvl.Config.SurvivalMode == SurvivalMode.Off) continue;
                Player[] watchers = Watchers(lvl);   // survival clients
                Player[] viewers  = AnyPlayers(lvl); // anyone at all (incl. classic)
                if (viewers.Length == 0) continue; // mobs freeze on truly EMPTY maps only

                LevelMobs lm = GetLevel(lvl, true);
                lock (lm.Mobs) TickLevel(lvl, lm, watchers, viewers);
            }

            // prune registries for levels no longer loaded
            lock (registryLock) {
                foreach (KeyValuePair<Level, LevelMobs> kvp in registry)
                {
                    if (Array.IndexOf(loaded, kvp.Key) < 0) {
                        if (dead == null) dead = new List<Level>();
                        dead.Add(kvp.Key);
                    }
                }
                if (dead != null) foreach (Level lvl in dead) registry.Remove(lvl);
            }
            // the container registry is Level-keyed the same way and must be
            // pruned on unload too, or unloaded Levels (and their block arrays)
            // leak forever as dictionary keys
            SurvivalInventory.PruneRegistry(loaded);
            SurvivalDrops.Prune(loaded); // drop registries are Level-keyed the same way
            SurvivalArrows.Prune(loaded);
        }

        static void TickLevel(Level lvl, LevelMobs lm, Player[] watchers, Player[] viewers) {
            bool indev = lvl.Config.SurvivalMode == SurvivalMode.Indev;
            Random rng = lm.Rng;

            // player combat bookkeeping (invulnerability window countdown) and
            // the genuine graduated hazard simulation (fall/drown/lava/fire/void)
            foreach (Player p in watchers)
            {
                SurvivalNet.TickPlayerCombat(p);
                SurvivalHazards.TickPlayer(p, lvl, indev);
            }

            // population: c0.30 primes the level once then tops up on a roll;
            // Indev fills gradually under the darkness rule (no initial burst).
            // area floors at 1 so small (< 64^3) maps still spawn at all.
            lm.Stats.Ticks++;
            long volume = (long)lvl.Width * lvl.Height * lvl.Length;
            int area = Math.Max(1, (int)(volume / 64 / 64 / 64));
            // Per-map standing-population cap. SurvivalMobCap overrides; 0 = auto
            // (scaled from the map volume, deliberately much lower than c0.30's
            // area*20 which swarmed small maps). Always <= the client's 256 pool.
            int cap = lvl.Config.SurvivalMobCap;
            if (cap <= 0) cap = Math.Max(8, Math.Min(area * 4, 40));
            lm.Cap = Math.Min(cap, MAX_MOBS_PER_LEVEL);
            if (!lm.InitialSpawned) {
                lm.InitialSpawned = true;
                if (!indev) InitialSpawnerRun(lvl, lm, (int)(volume / 6400));
            }
            if (rng.Next(100) < Math.Min(area, 25) && lm.Mobs.Count < lm.Cap) {
                lm.Stats.Rolls++;
                // ring centres come from ANY player, so a classic-only map still
                // feels alive; hostile targeting stays survival-clients-only
                TopUpSpawnerRun(lvl, lm, viewers, 2); // column-scan attempts nearly always land
            }

            for (int i = lm.Mobs.Count - 1; i >= 0; i--)
            {
                SurvMob m = lm.Mobs[i];
                if (TickMob(lvl, lm, m, indev, watchers, viewers)) {
                    StreamMob(lvl, watchers, m);
                } else {
                    BroadcastDespawn(lvl, m, m.Dead ? (byte)1 : (byte)0);
                    lm.Mobs.RemoveAt(i);
                }
            }

            // furnaces smelt on the same 20 TPS cadence (TileEntityFurnace)
            SurvivalInventory.TickFurnaces(lvl);

            // dropped items age, get collected, and despawn on the same cadence
            SurvivalDrops.Tick(lvl);

            // arrows fly, stick, hit and despawn on the same cadence
            SurvivalArrows.Tick(lvl);

            // non-survival clients on this map see the mobs as plain Classic
            // entities with ChangeModel (SurvivalFallbacks) - synced at 10 Hz,
            // the same cadence MCGalaxy relays player positions at, so stock
            // clients' own interpolation smooths mobs just like other players
            if (lm.Stats.Ticks % 2 == 0) SyncSpectators(lvl, lm);
        }

        static void SyncSpectators(Level lvl, LevelMobs lm) {
            List<MirrorMob> snap = null;
            Player[] players = PlayerInfo.Online.Items;
            foreach (Player p in players)
            {
                if (p.level != lvl || p.Session == null || p.Session.hasSurvival) continue;
                if (snap == null) {
                    snap = new List<MirrorMob>();
                    foreach (SurvMob m in lm.Mobs)
                    {
                        MirrorMob mm;
                        mm.Id  = m.Id;
                        mm.X   = m.X; mm.Y = m.Y; mm.Z = m.Z;
                        mm.Yaw = Angle(m.Yaw);
                        mm.Model = m.Type == TYPE_SHEEP && !m.HasFur ? "sheep_nofur" : Types[m.Type].Name;
                        snap.Add(mm);
                    }
                }
                SurvivalFallbacks.SyncMirror(p, lvl, snap);
            }
        }

        // Returns false when the mob should be removed (despawn/corpse finished).
        // watchers = survival clients (AI targets); viewers = anyone on the map
        // (keeps mobs from despawning while classic spectators watch them).
        static bool TickMob(Level lvl, LevelMobs lm, SurvMob m, bool indev, Player[] watchers, Player[] viewers) {
            Random rng = lm.Rng;

            // fell out of a floating map - genuine mobs just vanish
            if (m.Y < -32) { m.Dead = false; return false; }

            if (m.InvincTicks > 0) m.InvincTicks--;
            if (m.AttackDelay > 0) m.AttackDelay--;

            if (m.Dead) {
                m.DeathTicks++;
                if (m.DeathTicks > 20) {
                    // c0.30 creepers blow up when their corpse window closes
                    if (Types[m.Type].IsCreeper && !indev) CreeperExplode(lvl, m, 4.0f);
                    return false;
                }
                // corpse: no AI, but gravity still settles the body
                m.Jumping = false; m.MoveStrafe = 0; m.MoveForward = 0; m.TurnRate = 0;
                bool dWater = InLiquid(lvl, m, false), dLava = InLiquid(lvl, m, true);
                Travel(lvl, m, dWater, dLava);
                return true;
            }

            bool inWater = InLiquid(lvl, m, false), inLava = InLiquid(lvl, m, true);

            // ---- environment: drowning / lava / fire / sunburn ----
            bool headUnder = false;
            {
                int hx = (int)Math.Floor(m.X), hz = (int)Math.Floor(m.Z);
                int hy = (int)Math.Floor(m.Y + Height(lvl, m) * 0.85);
                byte collide = lvl.CollideType(BlockAt(lvl, hx, hy, hz));
                headUnder = collide == CollideType.LiquidWater || collide == CollideType.SwimThrough;
            }
            if (headUnder) {
                m.AirTicks--;
                if (m.AirTicks <= -20) { m.AirTicks = 0; HurtMob(lvl, lm, m, null, 2); }
            } else {
                m.AirTicks = 300;
            }
            if (inLava) HurtMob(lvl, lm, m, null, 10);

            if (indev) {
                if (inWater && m.Fire > 0) m.Fire = 0;
                if (m.Fire > 0) {
                    if (m.Fire % 20 == 0) HurtMob(lvl, lm, m, null, 1);
                    m.Fire--;
                }
                if (inLava) m.Fire = 600;
                // undead burn in daylight (EntityZombie/EntitySkeleton.onLivingUpdate),
                // with the brightness approximated as sky exposure x day/night
                if ((m.Type == TYPE_ZOMBIE || m.Type == TYPE_SKELETON) &&
                    IsBright(lvl, m) && rng.Next(30) == 0) {
                    m.Fire = 300;
                }
            }
            if (m.Dead) return true; // environment just killed it - stream the corpse

            // ---- despawn roll (BasicAI.tick) ----
            m.NoActionTime++;
            if (indev && !Types[m.Type].Passive && IsBright(lvl, m)) m.NoActionTime += 2;
            if (m.NoActionTime > 600 && rng.Next(800) == 0) {
                bool near = false;
                foreach (Player p in viewers)
                {
                    double dx = p.Pos.X / 32.0 - m.X, dy = (p.Pos.Y - Entities.CharacterHeight) / 32.0 - m.Y,
                           dz = p.Pos.Z / 32.0 - m.Z;
                    if (dx * dx + dy * dy + dz * dz < 1024.0) { near = true; break; }
                }
                if (near) m.NoActionTime = 0;
                else return false;
            }

            // ---- AI ----
            if (m.Type == TYPE_SHEEP) SheepAI(lvl, lm, m, indev, inWater, inLava);
            else                      WanderAI(lm, m, indev, inWater, inLava);
            if (!Types[m.Type].Passive) AttackAI(lvl, lm, m, indev, watchers);

            // ---- physics ----
            bool spiderLunge = m.Type == TYPE_SPIDER && m.Target != null;
            DoJump(m, inWater, inLava, spiderLunge);
            m.MoveStrafe *= 0.98f; m.MoveForward *= 0.98f; m.TurnRate *= 0.9f;
            double oldY = m.Y;
            Travel(lvl, m, inWater, inLava);

            // entity collision (Entity.push / applyEntityCollision): shove the mob
            // away from any overlapping player (the "pushback from players" - walk
            // into a mob and it gets nudged aside) and from other mobs so they don't
            // stack. Adds to velocity, so it takes effect next tick, exactly like the
            // client's Mob_PushApart.
            PushApart(lvl, lm, m, viewers);

            // ---- fall damage (Mob.causeFallDamage) ----
            if (inWater || inLava) m.Falling = false;
            if (m.OnGround) {
                if (m.Falling) {
                    double distFallen = m.FallPeakY - m.Y;
                    if (distFallen > 3.0) HurtMob(lvl, lm, m, null, (int)Math.Ceiling(distFallen - 3.0));
                    m.Falling = false;
                }
            } else if (m.VY < 0 || m.Y < oldY) {
                if (!m.Falling) { m.Falling = true; m.FallPeakY = m.Y; }
                else if (m.Y > m.FallPeakY) m.FallPeakY = m.Y;
            }
            if (m.Y > m.FallPeakY && m.Falling) m.FallPeakY = m.Y;

            return true;
        }

        static void SheepAI(Level lvl, LevelMobs lm, SurvMob m, bool indev, bool inWater, bool inLava) {
            // Sheep.SheepAI: over grass it stops to graze; after 60 ticks the grass
            // becomes dirt and there's a 1/5 chance the fur regrows.
            double sinYaw = Math.Sin(m.Yaw * Math.PI / 180.0);
            double cosYaw = Math.Cos(m.Yaw * Math.PI / 180.0);
            int x = (int)Math.Floor(m.X + 0.7 * sinYaw);
            int y = (int)Math.Floor(m.Y) - 1;
            int z = (int)Math.Floor(m.Z - 0.7 * cosYaw);
            bool overGrass = x >= 0 && y >= 0 && z >= 0 && x < lvl.Width && y < lvl.Height && z < lvl.Length &&
                             lvl.GetBlock((ushort)x, (ushort)y, (ushort)z) == Block.Grass;

            if (m.Grazing) {
                if (!overGrass) {
                    m.Grazing = false;
                } else {
                    if (m.GrazeTime++ == 60) {
                        lvl.UpdateBlock(Player.Console, (ushort)x, (ushort)y, (ushort)z, Block.Dirt);
                        if (lm.Rng.Next(5) == 0) m.HasFur = true;
                    }
                    m.MoveStrafe = 0; m.MoveForward = 0;
                }
            } else {
                if (overGrass) { m.Grazing = true; m.GrazeTime = 0; }
                WanderAI(lm, m, indev, inWater, inLava);
            }
        }


        // ==================== debug ====================

        /// <summary> Console/test aid: spawns one mob of the given type near a position.
        /// Used by the /Survival spawn subcommand. </summary>
        public static bool DebugSpawn(Level lvl, byte type, int x, int y, int z) {
            if (type >= SPAWN_TYPES) return false;
            // snap to the ground below so a test mob doesn't take a spawn fall
            // (the level spawn point routinely floats well above the terrain)
            while (y > 1 && !IsSolidAt(lvl, x, y - 1, z)) y--;
            LevelMobs lm = GetLevel(lvl, true);
            lock (lm.Mobs) {
                if (lm.Mobs.Count >= MAX_MOBS_PER_LEVEL) return false;
                SpawnMob(lvl, lm, type, x + 0.5, y, z + 0.5, 0);
            }
            return true;
        }

        /// <summary> Debug: spawner statistics + clock state for /Survival spawner. </summary>
        public static void ReportSpawner(Player p, Level lvl) {
            LevelMobs lm = GetLevel(lvl, false);
            if (lm == null) { p.Message("No mob registry for this level yet (no survival player has ticked it)."); return; }
            SpawnStats st = lm.Stats;
            int time = SurvivalNet.WorldTime;
            p.Message("Spawner on {0}&S: &b{1}&S ticks, &b{2}&S rolls, &b{3}&S attempts, &b{4}&S spawned",
                      lvl.ColoredName, st.Ticks, st.Rolls, st.Attempts, st.Spawned);
            p.Message("  rejected: &b{0}&S empty-column, &b{1}&S out-of-bounds, &b{2}&S at-cap",
                      st.RejNoGround, st.RejOutOfBounds, st.RejCap);
            p.Message("  last spawn: &b{0}&S; live mobs &b{1}&S/&b{2}",
                      st.LastSpawn, CountMobs(lvl), MAX_MOBS_PER_LEVEL);
            p.Message("  clock: worldTime &b{0}&S ({1}&S), sky light &b{2}&S - monsters need dark, animals light",
                      time, DescribeTime(time), SurvivalNet.CurrentSkyLight());
        }

        /// <summary> Debug: the nearest live mobs to a player, for /Survival mobs. </summary>
        public static void ReportMobs(Player p, Level lvl, int max) {
            LevelMobs lm = GetLevel(lvl, false);
            p.Message("Live mobs on {0}&S: &b{1}", lvl.ColoredName, CountMobs(lvl));
            if (lm == null) return;
            double px = p.Pos.X / 32.0, py = (p.Pos.Y - Entities.CharacterHeight) / 32.0, pz = p.Pos.Z / 32.0;

            List<SurvMob> mobs;
            lock (lm.Mobs) mobs = new List<SurvMob>(lm.Mobs);
            mobs.Sort((a, b) => DistSq(a, px, py, pz).CompareTo(DistSq(b, px, py, pz)));
            for (int i = 0; i < mobs.Count && i < max; i++)
            {
                SurvMob m = mobs[i];
                p.Message("  &b{0}&S #{1} at ({2}, {3}, {4}) - {5} blocks, {6} HP{7}{8}",
                          Types[m.Type].Name, m.Id,
                          (int)m.X, (int)m.Y, (int)m.Z,
                          (int)Math.Sqrt(DistSq(m, px, py, pz)), m.Health,
                          m.Dead ? ", dying" : "",
                          m.Target != null ? ", hunting " + m.Target.name : "");
            }
        }

        static double DistSq(SurvMob m, double x, double y, double z) {
            double dx = m.X - x, dy = m.Y - y, dz = m.Z - z;
            return dx * dx + dy * dy + dz * dz;
        }

        internal static string DescribeTime(int time) {
            if (time < 11000) return "&eday";
            if (time < 12000) return "&6dusk";
            if (time < 23000) return "&9night";
            return "&edawn";
        }

        public static int CountMobs(Level lvl) {
            LevelMobs lm = GetLevel(lvl, false);
            if (lm == null) return 0;
            lock (lm.Mobs) return lm.Mobs.Count;
        }
    }
}
