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
    /// <summary> Test aid: spawns one survival mob at/near the level spawn.
    /// Split out of /Survival (was "/Survival spawn"). </summary>
    public sealed class CmdSurvSpawn : Command2
    {
        public override string name { get { return "SurvSpawn"; } }
        public override string type { get { return CommandTypes.World; } }
        public override bool museumUsable { get { return false; } }
        public override LevelPermission defaultRank { get { return LevelPermission.Operator; } }

        public override void Use(Player p, string message, CommandData data) {
            Level lvl = p.level ?? Server.mainLevel;
            if (lvl == null) { p.Message("No level to spawn on."); return; }
            if (message.Length == 0) { Help(p); return; }

            if (!SpawnMob(p, lvl, message.SplitSpaces()[0])) Help(p);
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

        public override void Help(Player p) {
            p.Message("&T/SurvSpawn [zombie/skeleton/pig/creeper/spider/sheep]");
            p.Message("&HSpawns a survival test mob at your feet (or the level spawn).");
        }
    }
}
