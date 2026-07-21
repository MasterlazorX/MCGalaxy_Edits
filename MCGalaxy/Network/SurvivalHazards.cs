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
    /// <summary> The genuine graduated player hazard simulation (fall / drowning /
    /// lava / fire / void), run server-side at 20 TPS for survival-capable players
    /// on survival maps. Replaces the binary MCGalaxy SurvivalDeath bridge, whose
    /// lethal-or-nothing fall/drown detection could never produce partial HP -
    /// health only ever went 20 to 0, so the Indev experience never synced. </summary>
    /// <remarks>
    /// A port of the client's verified SP implementation (SurvivalTest.c - itself
    /// line-checked against the c0.30/Indev decompiles), driven by the same
    /// position stream MCGalaxy's PlayerPhysics already consumes:
    ///  * fall: peak-Y tracking, damage ceil(dist - 3) on landing
    ///    (Mob.causeFallDamage / SurvivalTest_UpdateFall, FALL_SAFE_BLOCKS 3)
    ///  * drowning: Indev's air counter with the -20 underflow timer (2 HP, first
    ///    hit 320 ticks under, then every 20); c0.30's empty-air 2 HP/tick shaped
    ///    by the invulnerability window (EntityLiving.onEntityUpdate / Mob.tick)
    ///  * lava: 10 HP/tick through the invuln window (Mob.tick's lava branch)
    ///  * fire (Indev): lava arms the 600-tick burn, 1 HP every 20 ticks while it
    ///    counts down, water fizzes it out (Entity.onEntityUpdate). Fire-BLOCK
    ///    contact waits on phase 1's custom blocks - lava is the only igniter yet,
    ///    and there is no on-fire overlay flag for players in the protocol
    ///    (handoff item: needs a reserved bit or SURV_PLAYER_STATE).
    ///  * void: 4 HP/tick below the world (Indev World.outOfWorld) - floating maps.
    /// All damage flows through SurvivalNet.DamagePlayer, so the dual-threshold
    /// invulnerability window and the death-screen dwell keep working unchanged.
    /// Damage ticks are logged at Debug level for live diagnosis.
    /// </remarks>
    public static class SurvivalHazards
    {
        const float PLAYER_WIDTH  = 0.6f;
        const float PLAYER_HEIGHT = 1.8f;
        const float EYE_HEIGHT    = 1.62f;
        const double FALL_SAFE    = 3.0;   // FALL_SAFE_BLOCKS
        const int MAX_AIR         = 300;   // Entity.maxAir
        const int LAVA_DAMAGE     = 10;
        const int DROWN_DAMAGE    = 2;
        const int VOID_DAMAGE     = 4;
        const double VOID_Y       = -32;   // ST_VOID_KILL_Y - grace below the world before the void bites
        const double TELEPORT_DIST_SQ = 8 * 8; // a jump this large in one tick = teleport

        class HazardState
        {
            public double LastX, LastY, LastZ;
            public bool   HasLast;
            public bool   Falling;
            public double FallPeakY;
            public int    AirTicks = MAX_AIR;
            public int    FireTicks;
        }

        const string KEY = "survival.hazards";

        static HazardState Get(Player p) {
            object o;
            if (p.Extras.TryGet(KEY, out o)) return (HazardState)o;
            HazardState st = new HazardState();
            p.Extras[KEY] = st;
            return st;
        }

        /// <summary> Runs one 20 TPS hazard tick for a survival player. Called from
        /// the SurvivalMobs scheduler alongside TickPlayerCombat. </summary>
        public static void TickPlayer(Player p, Level lvl, bool indev) {
            HazardState st = Get(p);
            double x = p.Pos.X / 32.0;
            double y = (p.Pos.Y - Entities.CharacterHeight) / 32.0; // feet
            double z = p.Pos.Z / 32.0;

            // Dead players dwell at the death spot; nothing keeps hurting them
            // (repeat deaths are cancelled anyway) and their fall state resets.
            if (SurvivalNet.IsDead(p)) { ResetMotion(st, x, y, z); return; }

            // A respawn / /tp / map change moves the player instantly - never
            // read that jump as falling distance.
            if (st.HasLast) {
                double dx = x - st.LastX, dy = y - st.LastY, dz = z - st.LastZ;
                if (dx * dx + dy * dy + dz * dz > TELEPORT_DIST_SQ) ResetMotion(st, x, y, z);
            }

            bool inWater    = BoxTouches(lvl, x, y, z, false);
            bool inLava     = BoxTouches(lvl, x, y, z, true);
            bool headUnder  = LiquidAt(lvl, x, y + EYE_HEIGHT, z, false);
            bool onGround   = OnGround(lvl, x, y, z);

            // ---- drowning ----
            if (headUnder) {
                if (indev) {
                    // EntityLiving.onEntityUpdate: --air; the -20 underflow IS the
                    // damage timer - first hit 320 ticks under, then every 20.
                    st.AirTicks--;
                    if (st.AirTicks <= -20) {
                        st.AirTicks = 0;
                        Damage(p, DROWN_DAMAGE, "@p &Sdrowned.", "drowning");
                    }
                } else {
                    // c0.30 Mob.tick: hurt(2) every tick once the air runs out;
                    // the invulnerability window shapes the real cadence.
                    if (st.AirTicks > 0) st.AirTicks--;
                    else Damage(p, DROWN_DAMAGE, "@p &Sdrowned.", "drowning");
                }
            } else {
                st.AirTicks = MAX_AIR;
            }

            // ---- lava + the Indev burn counter ----
            if (inLava) {
                Damage(p, LAVA_DAMAGE, "@p &Stried to swim in lava.", "lava");
                if (indev) st.FireTicks = 600; // Entity.onEntityUpdate: lava re-arms the burn
            }
            if (indev) {
                if (inWater && st.FireTicks > 0) st.FireTicks = 0; // water fizzes it out
                if (st.FireTicks > 0) {
                    if (st.FireTicks % 20 == 0)
                        Damage(p, 1, "@p &Sburned to death.", "burning");
                    st.FireTicks--;
                }
            }

            // ---- void (floating maps are bottomless) ----
            // Indev-only: the client applies void death only under IndevTest_Enabled
            // (SurvivalTest.c:7826); c0.30 maps are solid and have no void.
            if (indev && y < VOID_Y) {
                Damage(p, VOID_DAMAGE, "@p &Sfell out of the world.", "the void");
            }

            // ---- fall damage (peak-Y tracking, client's SurvivalTest_UpdateFall) ----
            // Only WATER cushions a fall (Mob.tick resets fallDistance for isInWater
            // only); landing in lava does NOT cushion - the fall accumulates through
            // the non-solid lava and lands ceil(dist-3) damage on the solid pool bed,
            // on top of the lava burn (SurvivalTest.c:7818).
            if (inWater) {
                st.Falling = false; // touching water cushions the landing
            } else if (onGround) {
                if (st.Falling) {
                    double dist = st.FallPeakY - y;
                    if (dist > FALL_SAFE) {
                        int dmg = (int)Math.Ceiling(dist - FALL_SAFE);
                        Damage(p, dmg, "@p &Shit the ground too hard.", "a " + (int)dist + "-block fall");
                    }
                    st.Falling = false;
                }
            } else {
                if (!st.Falling) {
                    st.Falling   = true;
                    st.FallPeakY = Math.Max(st.HasLast ? st.LastY : y, y);
                } else if (y > st.FallPeakY) {
                    st.FallPeakY = y; // jump arc: the apex is the real peak
                }
            }

            st.LastX = x; st.LastY = y; st.LastZ = z; st.HasLast = true;
        }

        static void ResetMotion(HazardState st, double x, double y, double z) {
            st.Falling = false;
            st.LastX = x; st.LastY = y; st.LastZ = z; st.HasLast = true;
        }

        static void Damage(Player p, int amount, string deathMsg, string cause) {
            int before = SurvivalNet.GetHealth(p);
            SurvivalNet.DamagePlayer(p, amount, deathMsg);
            int after = SurvivalNet.GetHealth(p);
            if (after != before) {
                Logger.Log(LogType.Debug, "survival: {0} took {1} damage from {2} ({3} -> {4} HP)",
                           p.name, amount, cause, before, after);
            }
        }


        // ==================== environment probes ====================

        static BlockID BlockAt(Level lvl, int x, int y, int z) {
            if (x < 0) x = 0; else if (x >= lvl.Width)  x = lvl.Width  - 1;
            if (y < 0) y = 0; else if (y >= lvl.Height) y = lvl.Height - 1;
            if (z < 0) z = 0; else if (z >= lvl.Length) z = lvl.Length - 1;
            return lvl.GetBlock((ushort)x, (ushort)y, (ushort)z);
        }

        static bool IsLiquid(byte collide, bool lava) {
            if (lava) return collide == CollideType.LiquidLava;
            return collide == CollideType.LiquidWater || collide == CollideType.SwimThrough;
        }

        static bool LiquidAt(Level lvl, double x, double y, double z, bool lava) {
            byte collide = lvl.CollideType(BlockAt(lvl,
                (int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z)));
            return IsLiquid(collide, lava);
        }

        // any block the player's bounding box overlaps with the liquid type.
        // The box is shrunk 0.4 vertically on top and bottom before the touch
        // test, exactly like the client's ST_InLiquid (SurvivalTest.c:249
        // bb.Min.y += 0.4; bb.Max.y -= 0.4), so a shallow head-graze or a foot
        // barely dipping in doesn't register as "in" the liquid.
        static bool BoxTouches(Level lvl, double x, double y, double z, bool lava) {
            double w = PLAYER_WIDTH / 2;
            int minX = (int)Math.Floor(x - w),       maxX = (int)Math.Floor(x + w - 0.001);
            int minY = (int)Math.Floor(y + 0.4),     maxY = (int)Math.Floor(y + PLAYER_HEIGHT - 0.4 - 0.001);
            int minZ = (int)Math.Floor(z - w),       maxZ = (int)Math.Floor(z + w - 0.001);

            for (int by = minY; by <= maxY; by++)
                for (int bz = minZ; bz <= maxZ; bz++)
                    for (int bx = minX; bx <= maxX; bx++)
            {
                if (IsLiquid(lvl.CollideType(BlockAt(lvl, bx, by, bz)), lava)) return true;
            }
            return false;
        }

        // solid ground directly under any corner of the footprint
        static bool OnGround(Level lvl, double x, double y, double z) {
            double w = PLAYER_WIDTH / 2;
            int by = (int)Math.Floor(y - 0.06);
            int minX = (int)Math.Floor(x - w), maxX = (int)Math.Floor(x + w - 0.001);
            int minZ = (int)Math.Floor(z - w), maxZ = (int)Math.Floor(z + w - 0.001);

            for (int bz = minZ; bz <= maxZ; bz++)
                for (int bx = minX; bx <= maxX; bx++)
            {
                if (CollideType.IsSolid(lvl.CollideType(BlockAt(lvl, bx, by, bz)))) return true;
            }
            return false;
        }
    }
}
