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
    /// <summary> Shows or sets the shared survival world clock (day/night cycle,
    /// pushed to all survival players). Split out of /Survival ("/Survival time"). </summary>
    public sealed class CmdSurvTime : Command2
    {
        public override string name { get { return "SurvTime"; } }
        public override string type { get { return CommandTypes.World; } }
        public override LevelPermission defaultRank { get { return LevelPermission.Operator; } }

        public override void Use(Player p, string message, CommandData data) {
            if (message.Length == 0) {
                int t = SurvivalNet.WorldTime;
                p.Message("World time: &b{0}&S ({1}&S), sky light &b{2}&S/15",
                          t, SurvivalMobs.DescribeTime(t), SurvivalNet.CurrentSkyLightPublic());
                p.Message("Cycle: 0 sunrise, 6000 noon, 12000 sunset, 18000 midnight (20 min/day).");
                p.Message("Set with &T/SurvTime [day/noon/sunset/night/midnight/sunrise/<ticks>]");
                return;
            }
            int time;
            switch (message.ToLower()) {
                case "day": case "sunrise": time = 500;   break;
                case "noon":                time = 6000;  break;
                case "sunset": case "dusk": time = 11500; break;
                case "night":               time = 14000; break;
                case "midnight":            time = 18000; break;
                default:
                    if (!int.TryParse(message, out time)) {
                        p.Message("&WNot a time: {0}", message); return;
                    }
                    break;
            }
            SurvivalNet.SetWorldTime(time);
            p.Message("World time set to &b{0}&S ({1}&S) - pushed to all survival players.",
                      SurvivalNet.WorldTime, SurvivalMobs.DescribeTime(SurvivalNet.WorldTime));
        }

        public override void Help(Player p) {
            p.Message("&T/SurvTime &H- shows the survival world clock");
            p.Message("&T/SurvTime [day/noon/sunset/night/midnight/sunrise/<ticks>]");
            p.Message("&HSets the shared day/night clock (pushed to all survival players).");
        }
    }
}
