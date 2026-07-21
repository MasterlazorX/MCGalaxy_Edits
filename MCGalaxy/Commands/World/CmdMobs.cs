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
using MCGalaxy.Network;

namespace MCGalaxy.Commands.World
{
    /// <summary> Lists the nearest live survival mobs on the current level.
    /// Split out of /Survival (was "/Survival mobs"). </summary>
    public sealed class CmdMobs : Command2
    {
        public override string name { get { return "Mobs"; } }
        public override string type { get { return CommandTypes.World; } }
        public override bool museumUsable { get { return false; } }
        public override LevelPermission defaultRank { get { return LevelPermission.Operator; } }

        public override void Use(Player p, string message, CommandData data) {
            Level lvl = p.level ?? Server.mainLevel;
            if (lvl == null) { p.Message("No level to inspect."); return; }
            SurvivalMobs.ReportMobs(p, lvl, 8);
        }

        public override void Help(Player p) {
            p.Message("&T/Mobs");
            p.Message("&HLists the nearest live survival mobs on this level.");
        }
    }
}
