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
    /// <summary> Dumps a player's server-side survival inventory (a test aid).
    /// Split out of /Survival (was "/Survival inv"). </summary>
    public sealed class CmdSurvInv : Command2
    {
        public override string name { get { return "SurvInv"; } }
        public override string type { get { return CommandTypes.World; } }
        public override LevelPermission defaultRank { get { return LevelPermission.Operator; } }

        public override void Use(Player p, string message, CommandData data) {
            Player target = p;
            if (message.Length > 0) {
                target = PlayerInfo.FindMatches(p, message);
                if (target == null) return;
            } else if (p == Player.Console) {
                p.Message("From console, use: &T/SurvInv [player]"); return;
            }
            SurvivalInventory.DebugDump(p, target);
        }

        public override void Help(Player p) {
            p.Message("&T/SurvInv [player]");
            p.Message("&HDumps a player's server-side survival inventory.");
        }
    }
}
