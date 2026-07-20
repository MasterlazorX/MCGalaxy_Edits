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
            // Environment.TimeOfDay is not applied: the server clock is global
            // (per-map clocks are a known deviation). Entities/TileEntities
            // are not imported either - chest/furnace contents only persist
            // through the client's own singleplayer loader for now.
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