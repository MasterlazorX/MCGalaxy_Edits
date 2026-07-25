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
using System.IO.Compression;
using System.Text;
using MCGalaxy.Network;
using BlockID = System.UInt16;

namespace MCGalaxy.Levels.IO {

    /// <summary> Exports a level in Minecraft Indev's own .mclevel format
    /// (gzipped NBT, schema per the in-20100223 LevelIO / the client's
    /// MCLevel_Save). Blocks are remapped from the SurvivalTest view ids to
    /// genuine Indev ids, with facing/moisture/stage/orientation carried in
    /// the Data array's metadata nibble, so files load in genuine Indev and
    /// round-trip through /Import. Chest/furnace tile entities are written
    /// with their live contents (every container block gets an entry - even
    /// never-opened ones, which genuine Indev NPE-crashes without). The only
    /// entity written is a LocalPlayer at the level spawn: server-side mobs
    /// and player inventories are session state, not part of the map. </summary>
    public sealed class McLevelExporter : IMapExporter {

        public override string Extension { get { return ".mclevel"; } }

        public override void Write(Stream dst, Level lvl) {
            using (GZipStream gz = new GZipStream(dst, CompressionMode.Compress, true)) {
                BufferedStream s = new BufferedStream(gz, 16 * 1024);
                WriteLevel(s, lvl);
                s.Flush();
            }
        }

        // ==================== NBT primitives (big-endian) ====================

        const byte NBT_END = 0, NBT_I8 = 1, NBT_I16 = 2, NBT_I32 = 3, NBT_I64 = 4,
                   NBT_F32 = 5, NBT_BYTES = 7, NBT_STR = 8, NBT_LIST = 9, NBT_DICT = 10;

        static void TagHeader(Stream s, byte type, string name) {
            s.WriteByte(type);
            byte[] raw = Encoding.UTF8.GetBytes(name);
            U16(s, raw.Length);
            s.Write(raw, 0, raw.Length);
        }

        static void U16(Stream s, int v) {
            s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }
        static void U32(Stream s, int v) {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));  s.WriteByte((byte)v);
        }
        static void F32(Stream s, float v) {
            byte[] raw = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(raw);
            s.Write(raw, 0, 4);
        }

        static void WriteU8(Stream s, string name, int v)  { TagHeader(s, NBT_I8,  name); s.WriteByte((byte)v); }
        static void WriteI16(Stream s, string name, int v) { TagHeader(s, NBT_I16, name); U16(s, v); }
        static void WriteI32(Stream s, string name, int v) { TagHeader(s, NBT_I32, name); U32(s, v); }
        static void WriteF32(Stream s, string name, float v) { TagHeader(s, NBT_F32, name); F32(s, v); }

        static void WriteI64Zero(Stream s, string name) {
            TagHeader(s, NBT_I64, name);
            for (int i = 0; i < 8; i++) s.WriteByte(0);
        }

        static void WriteString(Stream s, string name, string value) {
            TagHeader(s, NBT_STR, name);
            byte[] raw = Encoding.UTF8.GetBytes(value);
            U16(s, raw.Length);
            s.Write(raw, 0, raw.Length);
        }

        static void WriteDict(Stream s, string name) { TagHeader(s, NBT_DICT, name); }

        static void WriteList(Stream s, string name, byte childType, int count) {
            TagHeader(s, NBT_LIST, name);
            s.WriteByte(childType);
            U32(s, count);
        }

        static void WriteFloatList(Stream s, string name, params float[] v) {
            WriteList(s, name, NBT_F32, v.Length);
            for (int i = 0; i < v.Length; i++) F32(s, v[i]);
        }

        // ==================== level schema ====================

        void WriteLevel(Stream s, Level lvl) {
            LevelConfig cfg = lvl.Config;
            WriteDict(s, "MinecraftLevel");

            WriteDict(s, "About");
            {
                WriteString(s, "Author", "MCGalaxy");
                WriteString(s, "Name",   lvl.MapName);
                WriteI64Zero(s, "CreatedOn");
            } s.WriteByte(NBT_END);

            int water  = EnvValue(cfg, EnvProp.EdgeLevel,   lvl.Height);
            int ground = water + EnvValue(cfg, EnvProp.SidesOffset, lvl.Height);
            bool lava  = cfg.HorizonBlock == Block.Lava || cfg.HorizonBlock == Block.StillLava;

            WriteDict(s, "Environment");
            {
                WriteI32(s, "CloudColor", ParseColor(cfg.CloudColor, 0xFFFFFF));
                WriteI32(s, "SkyColor",   ParseColor(cfg.SkyColor,   0x99CCFF));
                WriteI32(s, "FogColor",   ParseColor(cfg.FogColor,   0xFFFFFF));
                // no server-side light engine - full brightness, the client
                // recomputes the sky level from TimeOfDay on load anyway
                WriteU8 (s, "SkyBrightness", 15);
                WriteI16(s, "CloudHeight", EnvValue(cfg, EnvProp.CloudsLevel, lvl.Height));
                WriteI16(s, "SurroundingGroundHeight", ground);
                WriteI16(s, "SurroundingWaterHeight",  water);
                WriteU8 (s, "SurroundingGroundType", 2); // grass - genuine writes grass always
                WriteU8 (s, "SurroundingWaterType",  lava ? Block.Lava : Block.Water);
                WriteI16(s, "TimeOfDay", SurvivalNet.WorldTimeOf(lvl) % 24000);
            } s.WriteByte(NBT_END);

            WriteDict(s, "Map");
            {
                WriteI16(s, "Width",  lvl.Width);
                WriteI16(s, "Length", lvl.Length);
                WriteI16(s, "Height", lvl.Height);
                WriteList(s, "Spawn", NBT_I16, 3);
                U16(s, lvl.spawnx); U16(s, lvl.spawny); U16(s, lvl.spawnz);

                int volume = lvl.blocks.Length;
                TagHeader(s, NBT_BYTES, "Blocks"); U32(s, volume);
                byte[] chunk = new byte[8192];
                for (int i = 0; i < volume; ) {
                    int n = Math.Min(volume - i, chunk.Length);
                    for (int j = 0; j < n; j++) {
                        chunk[j] = SurvivalBlocks.ToIndev(ViewAt(lvl, i + j));
                    }
                    s.Write(chunk, 0, n); i += n;
                }

                // metadata high nibble | light low nibble (full light)
                TagHeader(s, NBT_BYTES, "Data"); U32(s, volume);
                for (int i = 0; i < volume; ) {
                    int n = Math.Min(volume - i, chunk.Length);
                    for (int j = 0; j < n; j++) {
                        chunk[j] = (byte)((SurvivalBlocks.DataMeta(ViewAt(lvl, i + j)) << 4) | 0x0F);
                    }
                    s.Write(chunk, 0, n); i += n;
                }
            } s.WriteByte(NBT_END);

            WritePlayer(s, lvl);
            WriteTileEntities(s, lvl);

            s.WriteByte(NBT_END); // close MinecraftLevel
        }

        /// <summary> The raw view id at a block index: custom blocks resolve
        /// through the extended tiles, physics ids through their visible form. </summary>
        static byte ViewAt(Level lvl, int i) {
            byte b = lvl.blocks[i];
            if (b == Block.custom_block) return lvl.GetExtTile(i);
            if (b > Block.CPE_MAX_BLOCK) {
                BlockID conv = Block.Convert(b);
                return conv > Block.CPE_MAX_BLOCK ? Block.Bedrock : (byte)conv;
            }
            return b;
        }

        static int EnvValue(LevelConfig cfg, EnvProp prop, int height) {
            int v = cfg.GetEnvProp(prop);
            return v == EnvConfig.ENV_USE_DEFAULT ? EnvConfig.DefaultEnvProp(prop, height) : v;
        }

        static int ParseColor(string hex, int fallback) {
            if (string.IsNullOrEmpty(hex)) return fallback;
            int v;
            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out v)) return v;
            return fallback;
        }

        // genuine Indev entity ids, indexed by the server mob type
        static readonly string[] mobIds = { "Zombie", "Skeleton", "Pig", "Creeper", "Spider", "Sheep" };

        // genuine Indev always has a LocalPlayer entity - a file without one
        // makes it spawn a fresh player, so write a minimal one at the spawn.
        // The level's LIVE mobs follow it, each as its genuine mob compound
        // (Pos anchored at feet + heightOffset, like real Indev saves).
        static void WritePlayer(Stream s, Level lvl) {
            List<SurvivalMobs.MobSnapshot> mobs = SurvivalMobs.SnapshotMobs(lvl);
            mobs.RemoveAll(m => m.Type < 0 || m.Type >= mobIds.Length);

            WriteList(s, "Entities", NBT_DICT, 1 + mobs.Count);
            WriteString(s, "id", "LocalPlayer");
            WriteFloatList(s, "Pos", lvl.spawnx + 0.5f, lvl.spawny + 1.62f, lvl.spawnz + 0.5f);
            WriteFloatList(s, "Motion", 0.0f, 0.0f, 0.0f);
            WriteFloatList(s, "Rotation", lvl.rotx * 360.0f / 256.0f, lvl.roty * 360.0f / 256.0f);
            WriteF32(s, "FallDistance", 0.0f);
            WriteI16(s, "Fire", 0);
            WriteI16(s, "Air",  300);
            WriteI16(s, "Health", 20);
            WriteI16(s, "HurtTime", 0);
            WriteI16(s, "DeathTime", 0);
            WriteI16(s, "AttackTime", 0);
            WriteI32(s, "Score", 0);
            WriteList(s, "Inventory", NBT_DICT, 0);
            s.WriteByte(NBT_END); // close player compound

            foreach (SurvivalMobs.MobSnapshot m in mobs)
            {
                WriteString(s, "id", mobIds[m.Type]);
                WriteFloatList(s, "Pos", (float)m.X, (float)(m.Y + SurvivalMobs.HeightOffOf(m.Type)), (float)m.Z);
                WriteFloatList(s, "Motion", 0.0f, 0.0f, 0.0f);
                WriteFloatList(s, "Rotation", m.Yaw, m.Pitch);
                WriteF32(s, "FallDistance", 0.0f);
                WriteI16(s, "Fire", (short)Math.Min(m.Fire, short.MaxValue));
                WriteI16(s, "Air",  300);
                WriteI16(s, "Health", (short)m.Health);
                WriteI16(s, "HurtTime", 0);
                WriteI16(s, "DeathTime", 0);
                WriteI16(s, "AttackTime", 0);
                if (m.Type == 5) WriteU8(s, "Sheared", m.HasFur ? 0 : 1); // genuine EntitySheep
                s.WriteByte(NBT_END); // close mob compound
            }
        }

        static bool IsChestView(byte v) {
            return v == SurvivalBlocks.CHEST ||
                   (v >= SurvivalBlocks.CHEST_V0 && v <= SurvivalBlocks.CHEST_V0 + 3);
        }
        static bool IsFurnaceView(byte v) {
            return v == SurvivalBlocks.FURNACE || v == SurvivalBlocks.FURNACE_LIT ||
                   (v >= SurvivalBlocks.FURN_V0 && v <= SurvivalBlocks.FURNL_V0 + 3);
        }

        static void WriteTileEntities(Stream s, Level lvl) {
            // one entry per container BLOCK (the live tile entity if the
            // container was ever opened, an empty one otherwise) - genuine
            // Indev NPE-crashes opening a chest with no tile entity behind it
            List<int> sites = new List<int>();
            int volume = lvl.blocks.Length;
            for (int i = 0; i < volume; i++) {
                byte v = ViewAt(lvl, i);
                if (IsChestView(v) || IsFurnaceView(v)) sites.Add(i);
            }

            Dictionary<long, SurvivalInventory.ContSnapshot> live =
                new Dictionary<long, SurvivalInventory.ContSnapshot>();
            foreach (SurvivalInventory.ContSnapshot te in SurvivalInventory.SnapshotContainers(lvl)) {
                live[((long)te.X << 40) | ((long)te.Y << 20) | (uint)te.Z] = te;
            }

            WriteList(s, "TileEntities", NBT_DICT, sites.Count);
            foreach (int i in sites)
            {
                ushort x, y, z;
                lvl.IntToPos(i, out x, out y, out z);
                bool furnace = IsFurnaceView(ViewAt(lvl, i));

                SurvivalInventory.ContSnapshot te;
                live.TryGetValue(((long)x << 40) | ((long)y << 20) | (uint)z, out te);

                WriteI32(s, "Pos", x + (y << 10) + (z << 20));
                if (furnace) {
                    WriteString(s, "id", "Furnace");
                    WriteI16(s, "BurnTime", te != null ? te.BurnTime : 0);
                    WriteI16(s, "CookTime", te != null ? te.CookTime : 0);
                } else {
                    WriteString(s, "id", "Chest");
                }

                int used = 0;
                if (te != null) {
                    for (int n = 0; n < te.Ids.Length; n++) {
                        if (te.Counts[n] > 0) used++;
                    }
                }
                WriteList(s, "Items", NBT_DICT, used);
                if (te != null) {
                    for (int n = 0; n < te.Ids.Length; n++) {
                        if (te.Counts[n] == 0) continue;
                        WriteItem(s, n, te.Ids[n], te.Counts[n], te.Damages[n]);
                    }
                }
                s.WriteByte(NBT_END); // close tile entity compound
            }
        }

        // Block ids inside item stacks are converted to the genuine Indev id
        // space too - our custom view ids would be null entries in real
        // Indev's item table and crash its GUI rendering
        static void WriteItem(Stream s, int slot, int id, int count, int damage) {
            if (id > 0 && id < 256) id = SurvivalBlocks.ToIndev((byte)id);
            WriteU8 (s, "Slot",  slot);
            WriteI16(s, "id",    id);
            WriteU8 (s, "Count", count);
            WriteI16(s, "Damage", damage);
            s.WriteByte(NBT_END); // close item compound
        }
    }
}
