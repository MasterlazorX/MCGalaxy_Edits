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
    /// <summary> Follow a player (hidden) and watch their survival inventory live,
    /// read-only. Movement reuses /Follow (which hides you, rank-checks, and TPs
    /// you to the target's map if they're elsewhere); the inventory half reuses the
    /// read-only /Inventory view. A survival tool - usable only on a survival map;
    /// operators may spectate on their own map, admins across maps. </summary>
    public sealed class CmdSpectate : Command2
    {
        public override string name { get { return "Spectate"; } }
        public override string shortcut { get { return "Spec"; } }
        public override string type { get { return CommandTypes.World; } }
        public override bool museumUsable { get { return false; } }
        public override bool SuperUseable { get { return false; } } // needs a player camera
        public override LevelPermission defaultRank { get { return LevelPermission.Operator; } }
        public override CommandPerm[] ExtraPerms {
            get { return new[] { new CommandPerm(LevelPermission.Admin, "can spectate players on other maps") }; }
        }

        const string SPEC_KEY = "survival.spectating";

        public override void Use(Player p, string message, CommandData data) {
            if (message.Length == 0 || message.CaselessEq("stop")) { Stop(p, data); return; }

            Player target = PlayerInfo.FindMatches(p, message);
            if (target == null) return;
            if (target == p) { p.Message("&WYou can't spectate yourself."); return; }

            // A survival tool: only usable while on a survival map. Classic clients
            // are allowed too - they just follow (there's no GUI to mirror).
            if (p.level == null || p.level.Config.SurvivalMode == SurvivalMode.Off) {
                p.Message("&WSpectate is a survival tool - use it while on a survival map."); return;
            }
            // Cross-map gate (mirrors /Inventory): operators spectate players on
            // their OWN map; spectating across maps is an admin capability (perm 1).
            if (target.level != p.level && !HasExtraPerm(p, data.Rank, 1)) {
                p.Message("&W{0} &Wis on {1}&W - operators can only spectate players on their own map.",
                          target.ColoredName, target.level == null ? "another map" : target.level.ColoredName);
                return;
            }

            // Movement: /Follow hides us, rank-checks, and (cross-map) TPs us to the
            // target's map. Skip the toggle if we're already following this target.
            object cur;
            bool already = p.Extras.TryGet(SPEC_KEY, out cur) && ((string)cur).CaselessEq(target.name);
            if (!already) {
                Command.Find("Follow").Use(p, target.name, data);
                if (!p.following.CaselessEq(target.name)) return; // /Follow declined (rank etc.)
            }

            // Live inventory mirror (read-only). Returns false for a non-survival
            // client, or if /Follow just TP'd us onto a non-survival map - then it's
            // follow-only.
            bool gui = SurvivalInventory.OpenPlayerInventory(p, target, false, solo: true);
            p.Extras[SPEC_KEY] = target.name;
            p.Message("Now spectating {0}&S{1}. &T/Spectate stop &Sto end.",
                      target.ColoredName, gui ? " &S(inventory mirrored)" : " &S(follow only)");
        }

        void Stop(Player p, CommandData data) {
            object o;
            if (!p.Extras.TryGet(SPEC_KEY, out o)) {
                p.Message("You aren't spectating anyone. &HUse &T/Spectate [player]"); return;
            }
            string tname = (string)o;
            p.Extras.Remove(SPEC_KEY);
            if (p.following.CaselessEq(tname)) Command.Find("Follow").Use(p, tname, data); // toggles off
            SurvivalInventory.ForceCloseView(p);
            p.Message("Stopped spectating {0}.", p.FormatNick(tname));
        }

        public override void Help(Player p) {
            p.Message("&T/Spectate [player] &H- follow a player (hidden) and watch their");
            p.Message("&Hsurvival inventory live, read-only.");
            p.Message("&T/Spectate stop &H- stop spectating.");
            p.Message("&HOperators spectate players on their own map; admins across maps.");
            p.Message("&HUsable while on a survival map.");
        }
    }
}
