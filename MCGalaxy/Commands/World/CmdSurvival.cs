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
using MCGalaxy.Network;

namespace MCGalaxy.Commands.World
{
    /// <summary> Configures a level's SurvivalTest settings live and re-sends the handshake to
    /// survival-test clients on that level, so changes take effect without a rejoin. </summary>
    public sealed class CmdSurvival : Command2
    {
        public override string name { get { return "Survival"; } }
        public override string type { get { return CommandTypes.World; } }
        public override bool museumUsable { get { return false; } }
        public override LevelPermission defaultRank { get { return LevelPermission.Operator; } }

        public override void Use(Player p, string message, CommandData data) {
            Level lvl = p.level ?? Server.mainLevel; // console has no current level -> configure main
            if (lvl == null) { p.Message("No level to configure."); return; }
            if (message.Length == 0) { PrintInfo(p, lvl); return; }

            string[] args = message.SplitSpaces();
            string opt = args[0].ToLower();
            LevelConfig cfg = lvl.Config;

            switch (opt) {
                case "off":     cfg.SurvivalMode = SurvivalMode.Off;     break;
                case "classic": cfg.SurvivalMode = SurvivalMode.Classic; break;
                case "indev":   cfg.SurvivalMode = SurvivalMode.Indev;   break;
                case "theme":
                    if (args.Length < 2 || !SetTheme(cfg, args[1])) {
                        p.Message("Themes: Normal, Hell, Paradise, Woods, Floating"); return;
                    }
                    break;
                case "enhanced": case "creative": case "pvp": case "deathdrops":
                    if (args.Length < 2 || !SetFlag(cfg, opt, args[1])) {
                        p.Message("Use: &T/Survival {0} [on/off]", opt); return;
                    }
                    break;
                case "spawn":
                    // test aid: spawn one mob at/near the level spawn point
                    if (args.Length < 2 || !SpawnMob(p, lvl, args[1])) {
                        p.Message("Use: &T/Survival spawn [zombie/skeleton/pig/creeper/spider/sheep]");
                    }
                    return; // no config change - skip save/refresh
                case "mobs":
                    p.Message("Live mobs on {0}&S: &b{1}", lvl.ColoredName, SurvivalMobs.CountMobs(lvl));
                    return;
                default:
                    Help(p); return;
            }

            lvl.SaveSettings();
            SurvivalNet.RefreshLevel(lvl); // apply live to survival-test clients on this level
            p.Message("Updated survival settings for {0}&S:", lvl.ColoredName);
            PrintInfo(p, lvl);
        }

        static bool SetTheme(LevelConfig cfg, string val) {
            try {
                SurvivalTheme theme = (SurvivalTheme)Enum.Parse(typeof(SurvivalTheme), val, true);
                if (!Enum.IsDefined(typeof(SurvivalTheme), theme)) return false;
                cfg.SurvivalTheme = theme;
                return true;
            } catch { return false; }
        }

        static bool SetFlag(LevelConfig cfg, string flag, string val) {
            bool on;
            if (val.CaselessEq("on")  || val.CaselessEq("true"))  on = true;
            else if (val.CaselessEq("off") || val.CaselessEq("false")) on = false;
            else return false;

            switch (flag) {
                case "enhanced":   cfg.SurvivalEnhanced   = on; break;
                case "creative":   cfg.SurvivalCreative   = on; break;
                case "pvp":        cfg.SurvivalPvP        = on; break;
                case "deathdrops": cfg.SurvivalDeathDrops = on; break;
            }
            return true;
        }

        static bool SpawnMob(Player p, Level lvl, string typeName) {
            string[] names = { "zombie", "skeleton", "pig", "creeper", "spider", "sheep" };
            int type = Array.IndexOf(names, typeName.ToLower());
            if (type < 0) return false;
            if (lvl.Config.SurvivalMode == SurvivalMode.Off) {
                p.Message("This level is not a survival map."); return true;
            }

            // drop at the requester's feet when in-game, else at the level spawn
            int x = lvl.spawnx, y = lvl.spawny, z = lvl.spawnz;
            if (p != Player.Console && p.level == lvl) {
                Maths.Vec3S32 feet = p.Pos.FeetBlockCoords;
                x = feet.X; y = feet.Y; z = feet.Z;
            }
            if (SurvivalMobs.DebugSpawn(lvl, (byte)type, x, y, z)) {
                p.Message("Spawned a &b{0}&S at ({1}, {2}, {3}).", typeName, x, y, z);
            } else {
                p.Message("Could not spawn (mob cap reached?).");
            }
            return true;
        }

        static void PrintInfo(Player p, Level lvl) {
            LevelConfig cfg = lvl.Config;
            p.Message("Survival on {0}&S: mode &b{1}&S, theme &b{2}", lvl.ColoredName, cfg.SurvivalMode, cfg.SurvivalTheme);
            p.Message("  flags: enhanced &b{0}&S, creative &b{1}&S, pvp &b{2}&S, deathDrops &b{3}",
                      cfg.SurvivalEnhanced, cfg.SurvivalCreative, cfg.SurvivalPvP, cfg.SurvivalDeathDrops);
        }

        public override void Help(Player p) {
            p.Message("&T/Survival &H- shows this level's survival settings");
            p.Message("&T/Survival [off/classic/indev] &H- sets the survival mode (the per-map gate)");
            p.Message("&T/Survival theme [normal/hell/paradise/woods/floating]");
            p.Message("&T/Survival [enhanced/creative/pvp/deathdrops] [on/off] &H- sets a flag");
            p.Message("&T/Survival spawn [type] &H- spawns a test mob at your feet");
            p.Message("&T/Survival mobs &H- shows the level's live mob count");
            p.Message("&HChanges apply live to survival-test clients on this level.");
        }
    }
}
