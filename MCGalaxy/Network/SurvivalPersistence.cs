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
using System.IO;

namespace MCGalaxy.Network
{
    /// <summary> Persists a survival map's live simulation state across unload/reload
    /// in a per-level sidecar (extra/survival/&lt;level&gt;.sur): its mobs (type,
    /// position, health, state) and its chest/furnace tile-entity contents. Time of
    /// day rides Level.Config.SurvivalTime and grown terrain rides the .lvl block
    /// array, so both persist on their own - this file covers the two things that
    /// only live in memory. Written on level save + unload, read on level load. </summary>
    internal static class SurvivalPersistence
    {
        static string Path(Level lvl) {
            return "extra/survival/" + lvl.name + ".sur";
        }

        /// <summary> OnLevelSave / OnLevelUnload: write the map's mobs + containers. </summary>
        public static void Save(Level lvl) {
            if (lvl == null || lvl.Config.SurvivalMode == SurvivalMode.Off) return;
            try {
                Directory.CreateDirectory("extra/survival");
                string tmp = Path(lvl) + ".tmp";
                using (StreamWriter w = new StreamWriter(tmp)) {
                    w.WriteLine("# survival sidecar v1: " + lvl.name);
                    SurvivalMobs.SaveMobs(lvl, w);
                    SurvivalInventory.SaveContainers(lvl, w);
                }
                // atomic-ish replace so a crash mid-write can't corrupt the sidecar
                if (File.Exists(Path(lvl))) File.Delete(Path(lvl));
                File.Move(tmp, Path(lvl));
            } catch (Exception ex) {
                Logger.LogError("Error saving survival state for " + lvl.name, ex);
            }
        }

        /// <summary> OnLevelLoaded: restore the map's mobs + containers, then delete the
        /// sidecar (it is rewritten on the next save/unload). </summary>
        public static void Load(Level lvl) {
            if (lvl == null || lvl.Config.SurvivalMode == SurvivalMode.Off) return;
            string path = Path(lvl);
            if (!File.Exists(path)) return;
            try {
                foreach (string line in File.ReadAllLines(path))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] parts = line.Split(' ');
                    if (parts[0] == "mob")       SurvivalMobs.RestoreMob(lvl, parts);
                    else if (parts[0] == "cont") SurvivalInventory.RestoreContainer(lvl, parts);
                }
            } catch (Exception ex) {
                Logger.LogError("Error loading survival state for " + lvl.name, ex);
            }
        }

        // ==================== event hooks ====================

        public static void OnLevelSave(Level lvl, ref bool cancel)   { Save(lvl); }
        public static void OnLevelUnload(Level lvl, ref bool cancel) { Save(lvl); }
        public static void OnLevelLoaded(Level lvl)                  { Load(lvl); }
    }
}
