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
    /// inventory (a test aid). Usage: /SurvivalGive [player] [block/item] &lt;amount&gt;.
    /// Accepts item names ("iron_pickaxe", "coal") or ids 256+, block names / ids
    /// for the Indev set; amount defaults to 1. (Aliased /SurvGive.) </summary>
    public sealed class CmdSurvivalGive : Command2
    {
        public override string name { get { return "SurvivalGive"; } }
        public override string type { get { return CommandTypes.World; } }
        public override LevelPermission defaultRank { get { return LevelPermission.Operator; } }
        public override CommandAlias[] Aliases {
            get { return new[] { new CommandAlias("SurvGive") }; }
        }

        public override void Use(Player p, string message, CommandData data) {
            string[] args = message.SplitSpaces();
            if (args.Length < 2) { Help(p); return; } // need <player> <item>

            Player target = PlayerInfo.FindMatches(p, args[0]);
            if (target == null) return;

            // items first (256+): by display name ("iron_pickaxe", "coal") or id
            ushort raw;
            string name;
            int numeric;
            ushort item = SurvivalItems.FindByName(args[1]);
            if (item == 0 && int.TryParse(args[1], out numeric) && numeric >= 256 && numeric <= 1023 &&
                SurvivalItems.NameOf((ushort)numeric) != null) {
                item = (ushort)numeric;
            }
            if (item != 0) {
                raw  = item;
                name = SurvivalItems.NameOf(item);
            } else {
                ushort block;
                // parse + name in the COMMAND RUNNER's context, not the target's -
                // an "unknown block" error (and the confirmation name) belongs to p,
                // who typed the command, not to the player receiving the item
                if (!CommandParser.GetBlock(p, args[1], out block)) return;
                raw = Block.ToRaw(block);
                if (raw > 255) { p.Message("&WOnly blocks with ids 0-255 can be given."); return; }
                name = Block.GetName(p, block);
            }

            int count = 1; // default one
            if (args.Length >= 3 && (!int.TryParse(args[2], out count) || count < 1 || count > 576)) {
                p.Message("&WAmount must be 1-576."); return;
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
            p.Message("&T/SurvivalGive [player] [block/item] <amount>");
            p.Message("&HPuts blocks/items into a survival player's inventory.");
            p.Message("&HAccepts item names (coal, iron_pickaxe), ids 256+, or Indev block names/ids.");
            p.Message("&HAmount defaults to 1.");
        }
    }
}
