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
using System.Collections.Generic;
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
            // If NEITHER registry exists, the level was pruned (or never ticked) -
            // an "empty" snapshot here is a lie, not a state. Never clobber a real
            // sidecar with it (LevelActions.Replace removes the level from Loaded
            // long before unloading it, so the prune can beat the unload-save).
            if (!SurvivalMobs.HasRegistry(lvl) && !SurvivalInventory.HasContainers(lvl)) return;
            try {
                Directory.CreateDirectory("extra/survival");
                string path = Path(lvl), tmp = path + ".tmp";
                using (StreamWriter w = new StreamWriter(tmp)) {
                    w.WriteLine("# survival sidecar v1: " + lvl.name);
                    SurvivalMobs.SaveMobs(lvl, w);
                    SurvivalInventory.SaveContainers(lvl, w);
                }
                // atomic replace where possible so a crash can't lose both copies
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else                   File.Move(tmp, path);
            } catch (Exception ex) {
                Logger.LogError("Error saving survival state for " + lvl.name, ex);
            }
        }

        /// <summary> Restores the map's mobs + containers, consuming the sidecar
        /// (deleted after a successful restore; rewritten on the next save/unload),
        /// so a double restore can never duplicate mobs. Runs on the survival tick
        /// thread via ProcessPending. </summary>
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
                File.Delete(path); // consumed - each sidecar restores exactly once
            } catch (Exception ex) {
                Logger.LogError("Error loading survival state for " + lvl.name, ex);
            }
        }

        // ==================== restore scheduling ====================
        // Restores run on the SURVIVAL TICK thread, not the loader thread: the tick
        // thread also owns the prune sweeps, so a freshly restored registry can never
        // be dropped by a concurrent prune (OnLevelLoadedEvent fires before
        // LevelInfo.Add, when the level is not yet in Loaded).

        static readonly List<Level> pending = new List<Level>();

        /// <summary> Queues every currently-loaded survival level for restore - the
        /// startup catch-up for levels (the MAIN level) that loaded before CorePlugin
        /// registered the OnLevelLoaded hook. Called from SurvivalNet.Start. </summary>
        public static void QueueLoadedLevels() {
            Level[] loaded = LevelInfo.Loaded.Items;
            lock (pending) {
                foreach (Level lvl in loaded)
                {
                    if (lvl.Config.SurvivalMode == SurvivalMode.Off) continue;
                    if (!pending.Contains(lvl)) pending.Add(lvl);
                }
            }
        }

        /// <summary> Drains the pending-restore queue. Called from the survival tick
        /// (SurvivalMobs.TickCore) BEFORE the prune sweeps. </summary>
        public static void ProcessPending() {
            Level[] todo;
            lock (pending) {
                if (pending.Count == 0) return;
                todo = pending.ToArray();
                pending.Clear();
            }
            foreach (Level lvl in todo) Load(lvl);
        }

        /// <summary> Writes every loaded survival level's sidecar + settings NOW.
        /// Called from SurvivalNet.Stop: server shutdown unloads plugins BEFORE
        /// saving levels (Server.cs), so by the time SaveAllLevels runs our
        /// OnLevelSave hook is unregistered - this is the last chance to persist
        /// mobs/containers and an advanced SurvivalTime. </summary>
        public static void SaveAllLoaded() {
            Level[] loaded = LevelInfo.Loaded.Items;
            foreach (Level lvl in loaded)
            {
                if (lvl.Config.SurvivalMode == SurvivalMode.Off) continue;
                Save(lvl);
                try { lvl.SaveSettings(); } catch (Exception ex) {
                    Logger.LogError("Error saving level settings for " + lvl.name, ex);
                }
            }
        }

        // ==================== event hooks ====================

        public static void OnLevelSave(Level lvl, ref bool cancel)   { Save(lvl); }
        public static void OnLevelUnload(Level lvl, ref bool cancel) {
            Save(lvl);
            // an idle map's clock still advanced: Level.Unload only calls Save()
            // (-> SaveSettings) when blocks Changed, so persist settings here too
            if (lvl != null && lvl.Config.SurvivalMode != SurvivalMode.Off) {
                try { lvl.SaveSettings(); } catch (Exception ex) {
                    Logger.LogError("Error saving level settings for " + lvl.name, ex);
                }
            }
        }
        public static void OnLevelLoaded(Level lvl) {
            if (lvl == null || lvl.Config.SurvivalMode == SurvivalMode.Off) return;
            lock (pending) { if (!pending.Contains(lvl)) pending.Add(lvl); }
        }

        // Sidecars are keyed by level NAME, so they must follow the level through
        // renames/copies/deletes - else the state is orphaned (lost for the renamed
        // map, resurrected into a future map that reuses the old name).

        static string PathOf(string map) { return "extra/survival/" + map + ".sur"; }

        public static void OnLevelRenamed(string srcMap, string dstMap) {
            try {
                string s = PathOf(srcMap), d = PathOf(dstMap);
                if (!File.Exists(s)) return;
                if (File.Exists(d)) File.Delete(d);
                File.Move(s, d);
            } catch (Exception ex) {
                Logger.LogError("Error moving survival sidecar for " + srcMap, ex);
            }
        }

        public static void OnLevelCopied(string srcMap, string dstMap) {
            try {
                string s = PathOf(srcMap);
                if (File.Exists(s)) File.Copy(s, PathOf(dstMap), true);
            } catch (Exception ex) {
                Logger.LogError("Error copying survival sidecar for " + srcMap, ex);
            }
        }

        public static void OnLevelDeleted(string map) {
            try {
                string s = PathOf(map);
                if (File.Exists(s)) File.Delete(s);
            } catch (Exception ex) {
                Logger.LogError("Error deleting survival sidecar for " + map, ex);
            }
        }
    }
}
