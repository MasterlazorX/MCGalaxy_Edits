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

namespace MCGalaxy.Network
{
    /// <summary> Graceful degradation for clients that did NOT negotiate SurvivalTest,
    /// visiting a survival map (networking-plan §20-§21 fallback policy): they can't
    /// speak the sub-protocol, but stock CPE gives us enough to approximate. </summary>
    /// <remarks>
    /// * Day/night: the survival clock scales the level's environment colours
    ///   (sky/cloud/fog/shadow/sunlight) via the stock EnvColors extension - §21's
    ///   prescribed fallback. Sent only when the eased sky-light level changes, and
    ///   the level's own colours are restored when the map stops being survival
    ///   (a normal map join always resends the level env anyway).
    /// * Mobs: mirrored as plain Classic entities with CPE ChangeModel set to the
    ///   mob's model name (stock ClassiCube ships all six c0.30 mob models) -
    ///   visible and moving, though without the bespoke animations (hurt flash,
    ///   creeper swell, shear) the sub-protocol carries. Pure pre-CPE clients see
    ///   humanoids. Mirror entity ids are allocated 254 downward, far above
    ///   MCGalaxy's low player/bot id range; up to 48 mobs mirror per viewer.
    /// Both fallbacks are strictly per-session views: survival-capable clients
    /// never receive them (they get the genuine sub-protocol streams instead).
    /// </remarks>
    public static class SurvivalFallbacks
    {
        // ==================== day/night via EnvColors (§21) ====================

        const string ENV_KEY = "survival.envLight";

        // client-default environment colours, used when the level doesn't set its own
        static readonly string[] defaultColors = { "99CCFF", "FFFFFF", "FFFFFF", "9B9B9B", "FFFFFF" };

        /// <summary> Once a second (from SurvivalNet.TimeTick): darken/restore the env
        /// colours of non-survival clients on survival maps as the clock moves. </summary>
        public static void TickEnv(Player p, Level lvl) {
            if (!p.Supports(CpeExt.EnvColors)) return;
            int light = SurvivalNet.CurrentSkyLight();
            if (p.Extras.GetInt(ENV_KEY, -1) == light) return;
            p.Extras[ENV_KEY] = light;

            // quadratic ease into night, floored so the world stays readable
            double f = Math.Max(0.15, (light / 15.0) * (light / 15.0));
            for (int type = 0; type < 5; type++)
            {
                string hex = lvl.Config.GetColor(type);
                if (string.IsNullOrEmpty(hex)) hex = defaultColors[type];
                p.Session.SendSetEnvColor((byte)type, Scale(hex, f));
            }
        }

        /// <summary> Restores the level's own colours (e.g. after /Survival off). </summary>
        public static void RestoreEnv(Player p) {
            if (p.Extras.GetInt(ENV_KEY, -1) == -1) return;
            p.Extras[ENV_KEY] = -1;
            p.SendCurrentEnv();
        }

        static string Scale(string hex, double f) {
            try {
                int r = Convert.ToInt32(hex.Substring(0, 2), 16);
                int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                int b = Convert.ToInt32(hex.Substring(4, 2), 16);
                return ((int)(r * f)).ToString("X2") + ((int)(g * f)).ToString("X2") +
                       ((int)(b * f)).ToString("X2");
            } catch { return hex; }
        }


        // ==================== mob mirror entities (§15.1 fallback) ====================

        const string MIRROR_KEY = "survival.mobMirror";
        const byte MIRROR_ID_MAX = 254; // ids walk downward from here (255 = self)
        const int  MIRROR_LIMIT  = 48;

        class MirrorState
        {
            public Level Level; // reset when the viewer changes maps
            public Dictionary<ushort, byte> Ids = new Dictionary<ushort, byte>();
            public Stack<byte> Free = new Stack<byte>();
            // guards Ids/Free: SyncMirror runs on the mob-tick thread, ClearMirror
            // on the command thread (/Survival off) - both touch this dictionary
            public readonly object Sync = new object();

            public MirrorState() {
                for (int i = 0; i < MIRROR_LIMIT; i++) Free.Push((byte)(MIRROR_ID_MAX - i));
            }
        }

        static MirrorState GetMirror(Player p, Level lvl) {
            object o;
            MirrorState st = null;
            if (p.Extras.TryGet(MIRROR_KEY, out o)) st = (MirrorState)o;
            if (st == null || st.Level != lvl) {
                // a map change wiped the client's entity table for us
                st = new MirrorState { Level = lvl };
                p.Extras[MIRROR_KEY] = st;
            }
            return st;
        }

        /// <summary> Syncs one spectator's view of the level's mobs: spawns new ones
        /// (with ChangeModel where supported), teleports the live ones, removes the
        /// gone ones. Called from the mob tick at a reduced cadence. </summary>
        public static void SyncMirror(Player p, Level lvl,
                                      List<SurvivalMobs.MirrorMob> mobs) {
            MirrorState st = GetMirror(p, lvl);
            bool models = p.Supports(CpeExt.ChangeModel);

            lock (st.Sync) {
                // remove mirrors whose mob is gone
                List<ushort> dead = null;
                foreach (KeyValuePair<ushort, byte> kvp in st.Ids)
                {
                    bool alive = false;
                    foreach (SurvivalMobs.MirrorMob m in mobs)
                    {
                        if (m.Id == kvp.Key) { alive = true; break; }
                    }
                    if (alive) continue;
                    if (dead == null) dead = new List<ushort>();
                    dead.Add(kvp.Key);
                }
                if (dead != null) {
                    foreach (ushort id in dead)
                    {
                        p.Session.SendRemoveEntity(st.Ids[id]);
                        st.Free.Push(st.Ids[id]);
                        st.Ids.Remove(id);
                    }
                }

                foreach (SurvivalMobs.MirrorMob m in mobs)
                {
                    Position pos = new Position((int)(m.X * 32), (int)(m.Y * 32) + Entities.CharacterHeight,
                                                (int)(m.Z * 32));
                    Orientation rot = new Orientation(m.Yaw, 0);
                    byte id;
                    if (st.Ids.TryGetValue(m.Id, out id)) {
                        p.Session.SendTeleport(id, pos, rot);
                    } else if (st.Free.Count > 0) {
                        id = st.Free.Pop();
                        st.Ids[m.Id] = id;
                        p.Session.SendSpawnEntity(id, "", m.Model, pos, rot);
                        if (models) p.Session.SendChangeModel(id, m.Model);
                    }
                }
            }
        }

        /// <summary> Tears a spectator's mirrors down (e.g. the map stopped being
        /// survival while they stood on it). </summary>
        public static void ClearMirror(Player p) {
            object o;
            if (!p.Extras.TryGet(MIRROR_KEY, out o) || o == null) return;
            MirrorState st = (MirrorState)o;
            lock (st.Sync) {
                if (st.Level == p.level) {
                    foreach (KeyValuePair<ushort, byte> kvp in st.Ids)
                        p.Session.SendRemoveEntity(kvp.Value);
                }
                st.Ids.Clear();
            }
            p.Extras[MIRROR_KEY] = null;
        }
    }
}
