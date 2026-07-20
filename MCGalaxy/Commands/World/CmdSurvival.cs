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
                case "classic": cfg.SurvivalMode = SurvivalMode.Classic; EnableHazards(p, lvl); break;
                case "indev":   cfg.SurvivalMode = SurvivalMode.Indev;   EnableHazards(p, lvl); break;
                case "theme":
                    if (args.Length < 2 || !SetTheme(cfg, args[1])) {
                        p.Message("Themes: Normal, Hell, Paradise, Woods, Floating"); return;
                    }
                    break;
                case "visitors":
                    if (args.Length < 2 || !SetVisitors(cfg, args[1])) {
                        p.Message("Use: &T/Survival visitors [visitor/allow/deny]");
                        p.Message("&Hvisitor = join but not build (default), allow = build, deny = no entry");
                        return;
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
                    SurvivalMobs.ReportMobs(p, lvl, 8);
                    return;
                case "spawner":
                    SurvivalMobs.ReportSpawner(p, lvl);
                    return;
                case "time":
                    HandleTime(p, args);
                    return;
                case "inv":
                    HandleInv(p, args);
                    return;
                case "give":
                    HandleGive(p, args);
                    return;
                default:
                    Help(p); return;
            }

            lvl.SaveSettings();
            SurvivalNet.RefreshLevel(lvl); // apply live to survival-test clients on this level
            p.Message("Updated survival settings for {0}&S:", lvl.ColoredName);
            PrintInfo(p, lvl);
        }

        // Turning a map survival should make its hazards real without a second,
        // easy-to-miss command: fall/drown/lava death detection is MCGalaxy's
        // per-level SurvivalDeath option ("/map death"), off by default. And a
        // default generated spawn floats well above the terrain, which with
        // death detection on makes every (re)spawn a lethal fall - so the
        // spawn is grounded too (unless the column is bottomless, e.g. a
        // Floating-theme void, where moving it would be worse than warning).
        static void EnableHazards(Player p, Level lvl) {
            if (!lvl.Config.SurvivalDeath) {
                lvl.Config.SurvivalDeath = true;
                p.Message("&SEnabled fall/drown death detection (&T/Map {0} death off &Sto revert).", lvl.name);
            }

            int x = lvl.spawnx, y = lvl.spawny, z = lvl.spawnz, ground = y;
            while (ground > 0 && !CollideSolid(lvl, x, ground - 1, z)) ground--;

            if (y - ground <= lvl.Config.FallHeight) return; // close enough to survive
            if (ground == 0 && !CollideSolid(lvl, x, 0, z)) {
                p.Message("&WThe map spawn hangs over a bottomless column - set a safe one with &T/SetSpawn&W.");
                return;
            }
            lvl.spawny  = (ushort)ground;
            lvl.Changed = true;
            p.Message("&SGrounded the floating map spawn (y {0} &S-> &b{1}&S) so respawning is survivable.", y, ground);
            p.Message("&SMove it with &T/SetSpawn &Sif you want it somewhere else.");
        }

        static bool CollideSolid(Level lvl, int x, int y, int z) {
            if (x < 0 || y < 0 || z < 0 || x >= lvl.Width || y >= lvl.Height || z >= lvl.Length) return false;
            return Blocks.CollideType.IsSolid(lvl.CollideType(lvl.GetBlock((ushort)x, (ushort)y, (ushort)z)));
        }

        static bool SetVisitors(LevelConfig cfg, string val) {
            try {
                SurvivalVisitorPolicy pol = (SurvivalVisitorPolicy)Enum.Parse(typeof(SurvivalVisitorPolicy), val, true);
                if (!Enum.IsDefined(typeof(SurvivalVisitorPolicy), pol)) return false;
                cfg.SurvivalVisitors = pol;
                return true;
            } catch { return false; }
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

        static void HandleTime(Player p, string[] args) {
            if (args.Length < 2) {
                int t = SurvivalNet.WorldTime;
                p.Message("World time: &b{0}&S ({1}&S), sky light &b{2}&S/15",
                          t, SurvivalMobs.DescribeTime(t), SurvivalNet.CurrentSkyLightPublic());
                p.Message("Cycle: 0 sunrise, 6000 noon, 12000 sunset, 18000 midnight (20 min/day).");
                p.Message("Set with &T/Survival time [day/noon/sunset/night/midnight/sunrise/<ticks>]");
                return;
            }
            int time;
            switch (args[1].ToLower()) {
                case "day": case "sunrise": time = 500;   break;
                case "noon":                time = 6000;  break;
                case "sunset": case "dusk": time = 11500; break;
                case "night":               time = 14000; break;
                case "midnight":            time = 18000; break;
                default:
                    if (!int.TryParse(args[1], out time)) {
                        p.Message("&WNot a time: {0}", args[1]); return;
                    }
                    break;
            }
            SurvivalNet.SetWorldTime(time);
            p.Message("World time set to &b{0}&S ({1}&S) - pushed to all survival players.",
                      SurvivalNet.WorldTime, SurvivalMobs.DescribeTime(SurvivalNet.WorldTime));
        }

        static void HandleInv(Player p, string[] args) {
            Player target = p;
            if (args.Length >= 2) {
                target = PlayerInfo.FindMatches(p, args[1]);
                if (target == null) return;
            } else if (p == Player.Console) {
                p.Message("From console, use: &T/Survival inv [player]"); return;
            }
            SurvivalInventory.DebugDump(p, target);
        }

        // test aid: put blocks straight into a survival player's server inventory,
        // so Indev-set blocks (torch/chest/workbench/...) are testable before
        // crafting exists. Accepts block names (incl. the level's custom defs)
        // or raw ids; count defaults to one stack.
        static void HandleGive(Player p, string[] args) {
            if (args.Length < 2) {
                p.Message("Use: &T/Survival give [block] <count> <player>"); return;
            }
            Player target = p;
            if (args.Length >= 4) {
                target = PlayerInfo.FindMatches(p, args[3]);
                if (target == null) return;
            } else if (p == Player.Console) {
                p.Message("From console, use: &T/Survival give [block] [count] [player]"); return;
            }

            // items first (256+): by display name ("iron_pickaxe", "coal") or id
            ushort raw;
            string name;
            int numeric;
            ushort item = Network.SurvivalItems.FindByName(args[1]);
            if (item == 0 && int.TryParse(args[1], out numeric) && numeric >= 256 && numeric <= 1023 &&
                Network.SurvivalItems.NameOf((ushort)numeric) != null) {
                item = (ushort)numeric;
            }
            if (item != 0) {
                raw  = item;
                name = Network.SurvivalItems.NameOf(item);
            } else {
                ushort block;
                if (!CommandParser.GetBlock(target, args[1], out block)) return;
                raw = Block.ToRaw(block);
                if (raw > 255) { p.Message("&WOnly blocks with ids 0-255 can be given."); return; }
                name = Block.GetName(target, block);
            }

            int count = 64;
            if (args.Length >= 3 && (!int.TryParse(args[2], out count) || count < 1 || count > 576)) {
                p.Message("&WCount must be 1-576."); return;
            }

            int given = SurvivalInventory.Give(target, raw, count);
            if (given < 0) {
                p.Message("&W{0} &Wis not on an active survival map (or not on a survival client).", target.name);
            } else if (given == 0) {
                p.Message("&W{0}'s &Winventory is full.", target.name);
            } else {
                p.Message("Gave {0} &b{1}&Sx &b{2}&S (id {3}).", target.ColoredName, given, name, raw);
            }
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
            p.Message("  hazards: death detection &b{0}&S, fall height &b{1}&S, live mobs &b{2}",
                      cfg.SurvivalDeath, cfg.FallHeight, SurvivalMobs.CountMobs(lvl));
            p.Message("  non-survival clients: &b{0}&S (change with &T/Survival visitors&S)", cfg.SurvivalVisitors);
            // stale-build tripwire: if this line is missing in-game, the server
            // binary predates the phase the missing feature shipped in
            p.Message("  server build: &bphases 0-4 + indev blocks &S(dwell, hacks-override, mobs, inventory)");
        }

        public override void Help(Player p) {
            p.Message("&T/Survival &H- shows this level's survival settings");
            p.Message("&T/Survival [off/classic/indev] &H- sets the survival mode (the per-map gate)");
            p.Message("&T/Survival theme [normal/hell/paradise/woods/floating]");
            p.Message("&T/Survival [enhanced/creative/pvp/deathdrops] [on/off] &H- sets a flag");
            p.Message("&T/Survival visitors [visitor/allow/deny] &H- what stock clients may do here");
            p.Message("&T/Survival spawn [type] &H- spawns a test mob at your feet");
            p.Message("&T/Survival mobs &H- lists the nearest live mobs");
            p.Message("&T/Survival spawner &H- natural-spawn statistics + clock state");
            p.Message("&T/Survival time [value] &H- shows or sets the world clock");
            p.Message("&T/Survival inv [player] &H- dumps the server-side inventory");
            p.Message("&T/Survival give [block] <count> &H- puts blocks in your inventory");
            p.Message("&HChanges apply live to survival-test clients on this level.");
        }
    }
}
