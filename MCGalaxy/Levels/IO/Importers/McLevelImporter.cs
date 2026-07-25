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
using fNbt;
using MCGalaxy.Maths;
using MCGalaxy.Network;

namespace MCGalaxy.Levels.IO {
    public sealed class McLevelImporter : IMapImporter {

        public override string Extension { get { return ".mclevel"; } }
        public override string Description { get { return "Minecraft Indev map"; } }

        public override Vec3U16 ReadDimensions(Stream src) {
            throw new NotSupportedException();
        }

        public override Level Read(Stream src, string name, bool metadata) {
            NbtFile file = new NbtFile();
            file.LoadFromStream(src);

            Level lvl;
            ReadData(file.RootTag, name, out lvl);
            if (!metadata) return lvl;

            ReadMetadata(file.RootTag, lvl);
            return lvl;
        }

        void ReadData(NbtCompound root, string name, out Level lvl) {
            NbtCompound map = (NbtCompound)root["Map"];
            ushort width  = (ushort)map["Width" ].ShortValue;
            ushort height = (ushort)map["Height"].ShortValue;
            ushort length = (ushort)map["Length"].ShortValue;
            byte[] blocks = map["Blocks"].ByteArrayValue;
            lvl = new Level(name, width, height, length, blocks);

            NbtList spawn = (NbtList)map["Spawn"];
            lvl.spawnx = (ushort)spawn.Tags[0].ShortValue;
            lvl.spawny = (ushort)spawn.Tags[1].ShortValue;
            lvl.spawnz = (ushort)spawn.Tags[2].ShortValue;

            ConvertBlocks(map, lvl, blocks);
        }

        // Genuine Indev block ids 50-62 (torch/chest/workbench/furnaces/...)
        // collide with CPE ids - remap them onto the SurvivalTest Indev-mode
        // view blocks, with the Data array's metadata high nibble picking the
        // directional chest/furnace variant, farmland moisture, crop stage and
        // wall-torch orientation. Mirrors the client's MCLevel_Load remap; the
        // resulting view ids above 65 become extended custom blocks.
        void ConvertBlocks(NbtCompound map, Level lvl, byte[] blocks) {
            byte[] data = map.Contains("Data") ? map["Data"].ByteArrayValue : null;

            for (int i = 0; i < blocks.Length; i++) {
                if (blocks[i] < 50) continue;
                byte b = SurvivalBlocks.FromIndev(blocks[i]);
                if (data != null && i < data.Length) {
                    b = SurvivalBlocks.ApplyDataMeta(b, (data[i] >> 4) & 15);
                }
                blocks[i] = b;
            }
            ConvertCustom(lvl);
        }

        void ReadMetadata(NbtCompound root, Level lvl) {
            NbtCompound env = (NbtCompound)root["Environment"];
            // TODO: Work out sun/shadow color from Skylight and TimeOfDay
            lvl.Config.SkyColor = env["SkyColor"].IntValue.ToString("X6");
            lvl.Config.FogColor = env["FogColor"].IntValue.ToString("X6");
            lvl.Config.CloudColor = env["CloudColor"].IntValue.ToString("X6");
            lvl.Config.CloudsHeight = env["CloudHeight"].ShortValue;

            // The OOB horizon planes, same approximation the Indev generator
            // uses: the fluid plane from the genuine surrounding fluid id, the
            // ground plane always grass in genuine files (dirt when it would
            // be underwater or the fluid is lava, to match the generator)
            byte fluid = env["SurroundingWaterType"].ByteValue;
            bool lava  = fluid == Block.Lava || fluid == Block.StillLava;
            lvl.Config.HorizonBlock = lava ? Block.StillLava : Block.StillWater;
            lvl.Config.EdgeLevel = env["SurroundingWaterHeight"].ShortValue;
            int borderHeight = env["SurroundingGroundHeight"].ShortValue;
            lvl.Config.SidesOffset = borderHeight - lvl.Config.EdgeLevel;
            lvl.Config.EdgeBlock = (lava || borderHeight <= lvl.Config.EdgeLevel)
                                        ? Block.Dirt : Block.Grass;

            // .mclevel worlds are Indev survival worlds - imported maps come
            // out survival-ready like generated ones (the block set itself is
            // runtime-only and re-applied on every level load)
            lvl.Config.SurvivalMode  = SurvivalMode.Indev;
            lvl.Config.SurvivalDeath = true;
            lvl.Config.SurvivalTheme = GuessTheme(env, lvl.Config.EdgeLevel);

            // the clock is per-map now: the world's own TimeOfDay carries over
            if (env.Contains("TimeOfDay")) {
                lvl.Config.SurvivalTime = ((env["TimeOfDay"].ShortValue % 24000) + 24000) % 24000;
            }

            // Entities (mobs) + TileEntities (chest/furnace contents) restore
            // through the survival sidecar: written here, consumed exactly-once
            // by SurvivalPersistence when the imported level first loads.
            WriteSidecar(root, lvl);
        }

        // genuine Indev entity ids -> server mob types (SurvivalMobs.Types order)
        static readonly string[] mobIds = { "Zombie", "Skeleton", "Pig", "Creeper", "Spider", "Sheep" };
        static readonly System.Globalization.CultureInfo INV =
            System.Globalization.CultureInfo.InvariantCulture;

        void WriteSidecar(NbtCompound root, Level lvl) {
            List<string> lines = new List<string>();
            try {
                ReadEntityLines(root, lines);
                ReadTileEntityLines(root, lines);
            } catch (Exception ex) {
                Logger.LogError("Error reading .mclevel entities for " + lvl.name, ex);
            }
            if (lines.Count == 0) return;
            try {
                Directory.CreateDirectory("extra/survival");
                File.WriteAllLines("extra/survival/" + lvl.name + ".sur", lines.ToArray());
            } catch (Exception ex) {
                Logger.LogError("Error writing survival sidecar for " + lvl.name, ex);
            }
        }

        void ReadEntityLines(NbtCompound root, List<string> lines) {
            NbtList ents = root["Entities"] as NbtList;
            if (ents == null) return;
            foreach (NbtTag t in ents.Tags)
            {
                NbtCompound e = t as NbtCompound;
                if (e == null || !e.Contains("id")) continue;
                int type = Array.IndexOf(mobIds, e["id"].StringValue);
                if (type < 0) continue; // LocalPlayer / unknown

                NbtList pos = e["Pos"] as NbtList, rot = e["Rotation"] as NbtList;
                if (pos == null || pos.Tags.Count < 3) continue;
                // genuine entity Pos.y = feet + heightOffset; the sidecar is feet-space
                double x = pos.Tags[0].FloatValue;
                double y = pos.Tags[1].FloatValue - SurvivalMobs.HeightOffOf(type);
                double z = pos.Tags[2].FloatValue;
                float yaw   = rot != null && rot.Tags.Count > 0 ? rot.Tags[0].FloatValue : 0;
                float pitch = rot != null && rot.Tags.Count > 1 ? rot.Tags[1].FloatValue : 0;
                int health  = e.Contains("Health") ? e["Health"].ShortValue : 10;
                if (health <= 0) continue;
                int fire    = e.Contains("Fire") ? Math.Max(0, (int)e["Fire"].ShortValue) : 0;
                bool hasFur = type == 5 && (!e.Contains("Sheared") || e["Sheared"].ByteValue == 0);

                lines.Add(string.Format(INV, "mob {0} {1} {2} {3} {4} {5} {6} {7} {8} {9}",
                    type, x, y, z, yaw, pitch, health, hasFur ? 1 : 0, -1, fire));
            }
        }

        void ReadTileEntityLines(NbtCompound root, List<string> lines) {
            NbtList tes = root["TileEntities"] as NbtList;
            if (tes == null) return;
            foreach (NbtTag t in tes.Tags)
            {
                NbtCompound te = t as NbtCompound;
                if (te == null || !te.Contains("id") || !te.Contains("Pos")) continue;
                string id = te["id"].StringValue;
                bool furnace = id == "Furnace";
                if (!furnace && id != "Chest") continue;

                int pos = te["Pos"].IntValue; // x + (y << 10) + (z << 20)
                int x = pos & 0x3FF, y = (pos >> 10) & 0x3FF, z = (pos >> 20) & 0x3FF;
                int burn = furnace && te.Contains("BurnTime") ? te["BurnTime"].ShortValue : 0;
                int cook = furnace && te.Contains("CookTime") ? te["CookTime"].ShortValue : 0;
                int nslots = furnace ? 3 : 27;
                int[] ids = new int[nslots]; int[] counts = new int[nslots]; int[] dmgs = new int[nslots];

                NbtList items = te["Items"] as NbtList;
                if (items != null) {
                    foreach (NbtTag it in items.Tags)
                    {
                        NbtCompound item = it as NbtCompound;
                        if (item == null || !item.Contains("Slot")) continue;
                        int slot = item["Slot"].ByteValue;
                        if (slot < 0 || slot >= nslots) continue;
                        int iid = item.Contains("id") ? item["id"].ShortValue : 0;
                        // genuine block ids inside stacks map back into view-id space
                        if (iid > 0 && iid < 256) iid = SurvivalBlocks.FromIndev((byte)iid);
                        ids[slot]    = iid;
                        counts[slot] = item.Contains("Count") ? item["Count"].ByteValue : 0;
                        dmgs[slot]   = item.Contains("Damage") ? item["Damage"].ShortValue : 0;
                    }
                }

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendFormat(INV, "cont {0} {1} {2} {3} {4} {5} {6} {7}",
                    x, y, z, furnace ? 2 : 1, burn, cook, 0, nslots); // kind: CONT_CHEST=1 / CONT_FURNACE=2
                for (int i = 0; i < nslots; i++)
                    sb.AppendFormat(INV, " {0}:{1}:{2}", ids[i], counts[i], dmgs[i]);
                lines.Add(sb.ToString());
            }
        }

        // The theme is not stored in .mclevel files - recognise the four
        // genuine theme skies (and the floating below-map water level) so
        // generator exports round-trip; anything else plays as Normal.
        static SurvivalTheme GuessTheme(NbtCompound env, int edgeLevel) {
            if (edgeLevel < 0) return SurvivalTheme.Floating;
            switch (env["SkyColor"].IntValue) {
                case 1049600:   return SurvivalTheme.Hell;
                case 13033215:  return SurvivalTheme.Paradise;
                case 7699847:   return SurvivalTheme.Woods;
                default:        return SurvivalTheme.Normal;
            }
        }
    }
}