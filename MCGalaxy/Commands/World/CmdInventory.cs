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
    /// <summary> Opens a live view of another player's survival inventory. An
    /// Operator gets a read-only window; an Admin (extra perm 1) may move items
    /// between the target's inventory and their own. A non-survival viewer (or
    /// the console) falls back to the text dump. Was "/Survival inv" / "/SurvInv"
    /// (kept as an alias). </summary>
    public sealed class CmdInventory : Command2
    {
        public override string name { get { return "Inventory"; } }
        public override string type { get { return CommandTypes.World; } }
        public override bool museumUsable { get { return false; } }
        public override LevelPermission defaultRank { get { return LevelPermission.Operator; } }
        public override CommandAlias[] Aliases {
            get { return new[] { new CommandAlias("SurvInv") }; }
        }
        public override CommandPerm[] ExtraPerms {
            get { return new[] {
                new CommandPerm(LevelPermission.Admin, "can move/edit the inventory"),
                new CommandPerm(LevelPermission.Admin, "can view players on other maps"),
                new CommandPerm(LevelPermission.Admin, "can view offline players' saved inventories"),
            }; }
        }

        public override void Use(Player p, string message, CommandData data) {
            if (message.Length == 0) {
                p.Message("Use: &T/Inventory [player]"); return;
            }

            // Offline viewing (admin, extra perm 3): inventories persist as
            // extra/survival/players/<name>.inv, so an exact offline name with a
            // saved file dumps read-only - checked BEFORE FindMatches so its
            // "not found" message doesn't fire for a legitimately offline player.
            if (PlayerInfo.FindExact(message) == null &&
                HasExtraPerm(p, data.Rank, 3) && SurvivalInventory.HasSavedInv(message)) {
                SurvivalInventory.DebugDumpOffline(p, message);
                return;
            }

            Player target = PlayerInfo.FindMatches(p, message);
            if (target == null) return;

            // Cross-map gate: an Operator may only view a player on their OWN map
            // (live + in-map). Viewing a player on another map is an admin
            // capability (extra perm 2). The console (no map) is exempt.
            if (p != Player.Console && target.level != p.level && !HasExtraPerm(p, data.Rank, 2)) {
                p.Message("&W{0} &Wis on {1}&W - operators can only view inventories on their own map.",
                          target.ColoredName,
                          target.level == null ? "another map" : target.level.ColoredName);
                return;
            }

            // Admins (extra perm 1) get an editable window; Operators view-only.
            bool canEdit = HasExtraPerm(p, data.Rank, 1);

            // A survival-test client on a survival map gets the GUI; anyone else
            // (a stock client, another mode, or the console) gets the text dump.
            // A cross-map target isn't spawned to the viewer, so the panel shows the
            // target's items with no live paperdoll (handled server-side).
            if (p != Player.Console && SurvivalInventory.OpenPlayerInventory(p, target, canEdit)) {
                p.Message("Opened {0}&S's inventory ({1}).", target.ColoredName,
                          canEdit ? "&aeditable&S" : "&7view-only&S");
                return;
            }
            SurvivalInventory.DebugDump(p, target);
        }

        public override void Help(Player p) {
            p.Message("&T/Inventory [player]");
            p.Message("&HOpens a live view of a player's survival inventory.");
            p.Message("&HOperators view; admins may move/edit items (drag to/from your own).");
            p.Message("&HOperators can only view players on their own map; admins can");
            p.Message("&Hview across maps and offline players' saved inventories (read-only).");
            p.Message("&HNon-survival clients see a text dump instead.");
        }
    }
}
