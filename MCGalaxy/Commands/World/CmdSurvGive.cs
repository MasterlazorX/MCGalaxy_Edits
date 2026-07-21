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
    /// <summary> Puts blocks or items straight into a survival player's server
    /// inventory (a test aid). Split out of /Survival (was "/Survival give").
    /// Accepts item names ("iron_pickaxe", "coal") or ids 256+, block names /
    /// ids for the Indev set; count defaults to one stack. </summary>
    public sealed class CmdSurvGive : Command2
    {
        public override string name { get { return "SurvGive"; } }
        public override string type { get { return CommandTypes.World; } }
        public override LevelPermission defaultRank { get { return LevelPermission.Operator; } }

        public override void Use(Player p, string message, CommandData data) {
            if (message.Length == 0) { Help(p); return; }
            string[] args = message.SplitSpaces();

            Player target = p;
            if (args.Length >= 3) {
                target = PlayerInfo.FindMatches(p, args[2]);
                if (target == null) return;
            } else if (p == Player.Console) {
                p.Message("From console, use: &T/SurvGive [block] [count] [player]"); return;
            }

            // items first (256+): by display name ("iron_pickaxe", "coal") or id
            ushort raw;
            string name;
            int numeric;
            ushort item = SurvivalItems.FindByName(args[0]);
            if (item == 0 && int.TryParse(args[0], out numeric) && numeric >= 256 && numeric <= 1023 &&
                SurvivalItems.NameOf((ushort)numeric) != null) {
                item = (ushort)numeric;
            }
            if (item != 0) {
                raw  = item;
                name = SurvivalItems.NameOf(item);
            } else {
                ushort block;
                if (!CommandParser.GetBlock(target, args[0], out block)) return;
                raw = Block.ToRaw(block);
                if (raw > 255) { p.Message("&WOnly blocks with ids 0-255 can be given."); return; }
                name = Block.GetName(target, block);
            }

            int count = 64;
            if (args.Length >= 2 && (!int.TryParse(args[1], out count) || count < 1 || count > 576)) {
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

        public override void Help(Player p) {
            p.Message("&T/SurvGive [block/item] <count> <player>");
            p.Message("&HPuts blocks/items in a survival player's inventory.");
            p.Message("&HAccepts item names (coal, iron_pickaxe), ids 256+, or Indev block names/ids.");
            p.Message("&HCount defaults to one stack; console must name the player.");
        }
    }
}
