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

namespace MCGalaxy.Commands.World
{
    /// <summary> Exports a level as Indev's own .mclevel format into
    /// extra/import/ (so it is immediately /Import-able, and openable in genuine
    /// Indev / the survival client's singleplayer loader). Split out of
    /// /Survival (was "/Survival export"). </summary>
    public sealed class CmdExport : Command2
    {
        public override string name { get { return "Export"; } }
        public override string type { get { return CommandTypes.World; } }
        public override bool museumUsable { get { return false; } }
        public override LevelPermission defaultRank { get { return LevelPermission.Operator; } }

        public override void Use(Player p, string message, CommandData data) {
            Level lvl = p.level ?? Server.mainLevel;
            string[] args = message.SplitSpaces();

            if (args.Length >= 2) { // export a level other than the current one
                lvl = Matcher.FindLevels(p, args[1]);
                if (lvl == null) return;
            }
            if (lvl == null) { p.Message("No level to export."); return; }

            string name = args.Length >= 1 && args[0].Length > 0 ? args[0] : lvl.MapName;
            if (!Formatter.ValidMapName(p, name)) return;

            if (!System.IO.Directory.Exists(Paths.ImportsDir))
                System.IO.Directory.CreateDirectory(Paths.ImportsDir);
            string path = Paths.ImportsDir + name + ".mclevel";

            try {
                new Levels.IO.McLevelExporter().Write(path, lvl);
            } catch (Exception ex) {
                Logger.LogError("Error exporting map " + lvl.name, ex);
                p.Message("&WExporting {0} &Wfailed. See error logs.", lvl.ColoredName);
                return;
            }
            p.Message("Exported {0}&S to &b{1}&S (&T/Import {2}&S loads it back).",
                      lvl.ColoredName, path, name);
        }

        public override void Help(Player p) {
            p.Message("&T/Export <name> <level>");
            p.Message("&HSaves a map as Indev's .mclevel format into extra/import/.");
            p.Message("&HDefaults: the map's own name, and the current level.");
        }
    }
}
