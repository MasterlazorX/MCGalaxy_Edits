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
using MCGalaxy.Blocks;
using BlockID = System.UInt16;

namespace MCGalaxy.Network
{
    /// <summary> Phase 1 (step 1): the Indev block set, applied to Indev-mode survival
    /// maps as level-scoped BlockDefinitions. A 1:1 port of the ClassiCube fork's
    /// IndevBlocks_Define table (src/IndevTest.c) - names, tiles, shapes, collide,
    /// sounds, light and classic fallback ids all match the client. </summary>
    /// <remarks>
    /// The id/metadata model (networking-plan §18, the fork's core decision):
    /// genuine Indev stores (block id + a 4-bit Data nibble); the CLIENT runtime -
    /// and therefore the classic wire protocol and this server's level array - uses
    /// a flattened VIEW-ID space where each visible metadata state gets its own id
    /// (chest facings 71-74, crop stages 85-92, wall torches 94-97, ...). The
    /// bijection helpers below (ToIndev/FromIndev/DataMeta/ApplyDataMeta) translate
    /// view ids &lt;-&gt; genuine (id, meta) pairs at I/O boundaries; they are the
    /// authoritative encoding for the phase-1 map generator + .mclevel steps.
    ///
    /// Who sees what:
    ///  * The fork client redefines these blocks locally (genuine models, torch
    ///    stick geometry, fire mesh) when SURV_HELLO says Indev - the server defs
    ///    are briefly visible before HELLO lands, then overridden by better ones.
    ///  * Stock CPE clients (visitors) render the server defs as sent: sprite
    ///    torches/crops, textured chest/furnace/workbench cubes.
    ///  * Pre-BlockDefs clients get each def's classic FallBack id.
    /// Tile indices target the fork's genuine-derived texture pack (96+ are the
    /// patched workbench/furnace/chest/crop tiles); a client on the plain default
    /// pack sees mismatched art there - proper fix is §19 texture-pack serving.
    ///
    /// The defs are runtime-only: applied when a level with SurvivalMode=Indev
    /// loads (or is switched live), removed when the mode turns off, and never
    /// saved into blockdefs/lvl_*.json.
    /// </remarks>
    public static class SurvivalBlocks
    {
        // Client view ids - mirror ClassiCube src/IndevTest.c exactly.
        // 50-62 are the genuine Indev ids; 71+ are the fork's flattened
        // view encodings of (id, metadata) pairs.
        public const byte TORCH         = 50;
        public const byte FIRE          = 51;
        public const byte WATER_SOURCE  = 52;
        public const byte LAVA_SOURCE   = 53;
        public const byte CHEST         = 54;
        public const byte GEARS         = 55;
        public const byte DIAMOND_ORE   = 56;
        public const byte DIAMOND_BLOCK = 57;
        public const byte WORKBENCH     = 58;
        public const byte FURNACE       = 61;
        public const byte FURNACE_LIT   = 62;
        public const byte CHEST_V0      = 71; // 71-74: chest front on -Z/+Z/-X/+X
        public const byte FURN_V0       = 75; // 75-78: idle furnace facing views
        public const byte FURNL_V0      = 79; // 79-82: lit furnace facing views
        public const byte FARMLAND      = 83;
        public const byte FARMLAND_WET  = 84;
        public const byte CROPS_0       = 85; // 85-92: growth stages 0-7
        public const byte CROPS_7       = 92;
        public const byte TORCH_W1      = 94; // 94-97: wall torch metadata 1-4
        public const byte TORCH_W4      = 97;

        const string DEFS_KEY = "survival.blockdefs";

        // SoundType bytes (DefaultSet.SoundType)
        const byte SND_WOOD = 1, SND_GRAVEL = 2, SND_GRASS = 3, SND_STONE = 4, SND_METAL = 5;


        // ==================== the block table ====================

        static BlockDefinition[] template;
        static readonly object templateLock = new object();

        static BlockDefinition[] Template() {
            lock (templateLock) {
                if (template == null) template = BuildTemplate();
                return template;
            }
        }

        static BlockDefinition Cube(byte raw, string name, ushort top, ushort side,
                                    ushort bottom, byte sound, byte fallback) {
            BlockDefinition def = new BlockDefinition();
            def.RawID = raw; def.Name = name; def.Speed = 1;
            def.CollideType = CollideType.Solid;
            def.TopTex = top; def.BottomTex = bottom;
            def.SetSideTex(side);
            def.BlocksLight = true;
            def.WalkSound   = sound;
            def.BlockDraw   = DrawType.Opaque;
            def.Shape = 16;
            def.MaxX  = 16; def.MaxY = 16; def.MaxZ = 16; // def Z = vertical
            def.FallBack = fallback;
            def.Brightness = 0;
            return def;
        }

        static BlockDefinition Sprite(byte raw, string name, ushort tile,
                                      byte sound, byte fallback) {
            BlockDefinition def = Cube(raw, name, tile, tile, tile, sound, fallback);
            def.CollideType = CollideType.WalkThrough;
            def.BlocksLight = false;
            def.BlockDraw   = DrawType.Transparent;
            def.Shape       = 0; // sprite
            return def;
        }

        static void Hide(BlockDefinition def) { def.InventoryOrder = 0; }

        /// <summary> Port of IndevBlocks_Define (ClassiCube src/IndevTest.c): tiles
        /// 96 wb top, 97 wb side, 98 wb front, 99 furn front, 100 furn lit,
        /// 101 furn side, 102 furn top, 103 chest front, 104 chest side, 105 top,
        /// 106 torch, 107-114 crops, 115/116 wet/dry farmland, 118 fire,
        /// 119 diamond ore, 122 diamond block, 123 gears. </summary>
        static BlockDefinition[] BuildTemplate() {
            BlockDefinition[] t = new BlockDefinition[TORCH_W4 + 1];
            BlockDefinition d;

            // Torch: genuine BlockTorch stick model approximated as a sprite for
            // stock clients (the fork swaps in the real model on HELLO). Light 14
            // in the lamp channel; pick bounds = the 2/16 wide, 10/16 tall column.
            d = Sprite(TORCH, "Torch", 106, SND_WOOD, Block.Sapling);
            d.SetBrightness(14, true);
            d.MinX = 7; d.MaxX = 9; d.MinY = 7; d.MaxY = 9; d.MaxZ = 10;
            t[TORCH] = d;

            // Fire: the default pack's animated fire tile; full-bright 15.
            // CPE Fire (54) is the natural fallback (classic converts it onward).
            d = Sprite(FIRE, "Fire", 38, SND_WOOD, Block.Fire);
            d.SetBrightness(15, true);
            t[FIRE] = d;

            // Water/lava source (BlockSource): look exactly like the still fluid;
            // refill physics arrives with the phase-5 tick systems.
            d = Cube(WATER_SOURCE, "Water Source", 14, 14, 14, 0, Block.StillWater);
            d.CollideType = CollideType.LiquidWater;
            d.BlockDraw   = DrawType.Translucent;
            d.FogDensity  = 11; d.FogR = 5; d.FogG = 5; d.FogB = 51;
            t[WATER_SOURCE] = d;

            d = Cube(LAVA_SOURCE, "Lava Source", 30, 30, 30, 0, Block.StillLava);
            d.CollideType = CollideType.LiquidLava;
            d.FogDensity  = 229; d.FogR = 153; d.FogG = 25; d.FogB = 0;
            d.SetBrightness(15, true);
            t[LAVA_SOURCE] = d;

            // Chest / workbench / furnaces: front-textured cubes (canonical ids
            // face -Z; the directional views below carry the other facings).
            d = Cube(CHEST, "Chest", 105, 104, 105, SND_WOOD, Block.Crate);
            d.FrontTex = 103;
            t[CHEST] = d;

            d = Cube(WORKBENCH, "Workbench", 96, 97, 4, SND_WOOD, Block.Wood);
            d.FrontTex = 98;
            t[WORKBENCH] = d;

            d = Cube(FURNACE, "Furnace", 102, 101, 102, SND_STONE, Block.Cobblestone);
            d.FrontTex = 99;
            t[FURNACE] = d;

            d = Cube(FURNACE_LIT, "Furnace (lit)", 102, 101, 102, SND_STONE, Block.MagmaBlock);
            d.FrontTex = 100;
            d.SetBrightness(14, true);
            t[FURNACE_LIT] = d;

            // Gears: non-solid decorative (Material.circuits); tile 123 only exists
            // in the fork's patched pack. No sensible classic stand-in -> air.
            d = Sprite(GEARS, "Gears", 123, SND_METAL, Block.Air);
            t[GEARS] = d;

            // Diamond ore/block (tiles patched from the beta jar)
            t[DIAMOND_ORE]   = Cube(DIAMOND_ORE,   "Diamond Ore",   119, 119, 119, SND_STONE, Block.IronOre);
            t[DIAMOND_BLOCK] = Cube(DIAMOND_BLOCK, "Diamond Block", 122, 122, 122, SND_METAL, Block.Iron);

            // Directional container views: the front tile rotated onto the face
            // matching Indev metadata 2/3/4/5 (-Z, +Z, -X, +X). Hidden from the
            // block menu - placement always produces the canonical id in v1.
            for (int k = 0; k < 4; k++) {
                d = Cube((byte)(CHEST_V0 + k), "Chest", 105, 104, 105, SND_WOOD, Block.Crate);
                SetFacingTex(d, k, 103);
                Hide(d); t[CHEST_V0 + k] = d;

                d = Cube((byte)(FURN_V0 + k), "Furnace", 102, 101, 102, SND_STONE, Block.Cobblestone);
                SetFacingTex(d, k, 99);
                Hide(d); t[FURN_V0 + k] = d;

                d = Cube((byte)(FURNL_V0 + k), "Furnace (lit)", 102, 101, 102, SND_STONE, Block.MagmaBlock);
                SetFacingTex(d, k, 100);
                d.SetBrightness(14, true);
                Hide(d); t[FURNL_V0 + k] = d;
            }

            // Farmland: tilled top (116 dry / 115 wet) on dirt, 15/16 tall like
            // genuine BlockFarmland. Only obtainable by hoeing - hidden.
            d = Cube(FARMLAND, "Farmland", 116, 2, 2, SND_GRAVEL, Block.Dirt);
            d.Shape = 15; d.MaxZ = 15;
            Hide(d); t[FARMLAND] = d;

            d = Cube(FARMLAND_WET, "Farmland", 115, 2, 2, SND_GRAVEL, Block.Dirt);
            d.Shape = 15; d.MaxZ = 15;
            Hide(d); t[FARMLAND_WET] = d;

            // Crop stages 0-7: sprites, tiles 107-114; pick box 4/16 tall.
            for (int k = 0; k < 8; k++) {
                d = Sprite((byte)(CROPS_0 + k), "Crops", (ushort)(107 + k), SND_GRASS, Block.Sapling);
                d.MaxZ = 4;
                Hide(d); t[CROPS_0 + k] = d;
            }

            // Wall torch views (metadata 1-4): sprite approximation of the tilted
            // genuine model; pick bounds from BlockTorch.collisionRayTrace (x16).
            byte[,] wtMin = { {0,5,3}, {11,5,3}, {5,0,3}, {5,11,3} };  // x, z, y(vertical)
            byte[,] wtMax = { {5,11,13}, {16,11,13}, {11,5,13}, {11,16,13} };
            for (int k = 0; k < 4; k++) {
                d = Sprite((byte)(TORCH_W1 + k), "Torch", 106, SND_WOOD, Block.Sapling);
                d.SetBrightness(14, true);
                d.MinX = wtMin[k,0]; d.MinY = wtMin[k,1]; d.MinZ = wtMin[k,2];
                d.MaxX = wtMax[k,0]; d.MaxY = wtMax[k,1]; d.MaxZ = wtMax[k,2];
                Hide(d); t[TORCH_W1 + k] = d;
            }
            return t;
        }

        // Front-tile placement for directional views, k = Indev meta - 2:
        // 0 -> -Z (FrontTex), 1 -> +Z (BackTex), 2 -> -X (LeftTex), 3 -> +X (RightTex)
        static void SetFacingTex(BlockDefinition def, int k, ushort tile) {
            switch (k) {
                case 0: def.FrontTex = tile; break;
                case 1: def.BackTex  = tile; break;
                case 2: def.LeftTex  = tile; break;
                case 3: def.RightTex = tile; break;
            }
        }


        // ==================== apply / remove ====================

        /// <summary> Brings a level's custom block defs in line with its survival
        /// mode: Indev maps get the block set, everything else has it removed.
        /// Idempotent - safe to call from every config refresh. </summary>
        public static void Sync(Level lvl) {
            if (lvl == null) return;
            bool want = lvl.Config.SurvivalMode == SurvivalMode.Indev;
            bool have = lvl.Extras.Contains(DEFS_KEY);
            if (want && !have)      Apply(lvl);
            else if (!want && have) Remove(lvl);
        }

        /// <summary> OnLevelLoadedEvent: re-apply on every load (the defs are
        /// runtime-only, deliberately never saved to blockdefs/lvl_*.json). </summary>
        public static void OnLevelLoaded(Level lvl) { Sync(lvl); }

        /// <summary> Server-start sweep for levels that loaded before the core
        /// plugin registered its hooks. Called from SurvivalNet.Start. </summary>
        public static void SyncLoadedLevels() {
            Level[] loaded = LevelInfo.Loaded.Items;
            foreach (Level lvl in loaded) Sync(lvl);
        }

        static void Apply(Level lvl) {
            BlockDefinition[] tmpl = Template();
            BlockDefinition[] mine = new BlockDefinition[tmpl.Length];

            for (int raw = 0; raw < tmpl.Length; raw++) {
                if (tmpl[raw] == null) continue;
                // per-level instances so /lb edits on one map never leak into another
                BlockDefinition def = tmpl[raw].Copy();
                BlockDefinition.Add(def, lvl.CustomBlockDefs, lvl);
                mine[raw] = def;
            }
            lvl.Extras[DEFS_KEY] = mine;
            ReloadFallbackViewers(lvl);
            Logger.Log(LogType.Debug, "survival: applied the Indev block set to {0}", lvl.name);
        }

        static void Remove(Level lvl) {
            object o;
            if (!lvl.Extras.TryGet(DEFS_KEY, out o)) return;
            BlockDefinition[] mine = (BlockDefinition[])o;

            for (int raw = 0; raw < mine.Length; raw++) {
                if (mine[raw] == null) continue;
                // only strip OUR instances - a map owner's own /lb override at the
                // same id (reference-different) is left alone
                BlockID b = Block.FromRaw((BlockID)raw);
                if (lvl.CustomBlockDefs[b] != mine[raw]) continue;
                BlockDefinition.Remove(mine[raw], lvl.CustomBlockDefs, lvl);
            }
            lvl.Extras.Remove(DEFS_KEY);
            ReloadFallbackViewers(lvl);
            Logger.Log(LogType.Debug, "survival: removed the Indev block set from {0}", lvl.name);
        }

        // BlockDefinition.Add/Remove push live defs to BlockDefs-capable clients,
        // but pre-BlockDefs clients bake fallbacks into the map stream - they need
        // a reload to see the change.
        static void ReloadFallbackViewers(Level lvl) {
            Player[] players = PlayerInfo.Online.Items;
            foreach (Player pl in players)
            {
                if (pl.level != lvl || pl.Session == null) continue;
                if (pl.Session.hasBlockDefs) continue;
                PlayerActions.ReloadMap(pl);
            }
        }


        // ==================== inventory bridge helpers ====================

        /// <summary> Whether a raw/view id is part of the Indev block set. </summary>
        public static bool IsIndevBlock(ushort raw) {
            return raw >= TORCH && raw <= TORCH_W4 && Template()[raw] != null;
        }

        /// <summary> What mining a view id yields (0 = nothing). V1 deviations:
        /// diamond ore drops itself (no diamond item yet) and crops drop nothing
        /// (seeds are an item) - both fixed by the phase-4/5 item work. </summary>
        public static ushort PickupFor(ushort raw) {
            if (raw >= CHEST_V0 && raw <= CHEST_V0 + 3) return CHEST;
            if (raw >= FURN_V0  && raw <= FURNL_V0 + 3) return FURNACE; // lit drops idle
            if (raw >= TORCH_W1 && raw <= TORCH_W4)     return TORCH;
            if (raw == FARMLAND || raw == FARMLAND_WET) return Block.Dirt;
            if (raw >= CROPS_0 && raw <= CROPS_7)       return 0;
            switch (raw) {
                case TORCH: case CHEST: case GEARS: case DIAMOND_ORE:
                case DIAMOND_BLOCK: case WORKBENCH: case FURNACE:
                    return raw;
                case FURNACE_LIT: return FURNACE;
            }
            return 0; // fire, sources (liquid), leftover CPE ids
        }

        /// <summary> What placing a view id consumes from the inventory
        /// (0 = not survival content, place passes through unconsumed). </summary>
        public static ushort PlaceCost(ushort raw) {
            if (raw >= CHEST_V0 && raw <= CHEST_V0 + 3) return CHEST;
            if (raw >= FURN_V0  && raw <= FURN_V0  + 3) return FURNACE;
            if (raw >= FURNL_V0 && raw <= FURNL_V0 + 3) return FURNACE_LIT;
            if (raw >= TORCH_W1 && raw <= TORCH_W4)     return TORCH;
            return IsIndevBlock(raw) ? raw : (ushort)0;
        }


        // ==================== the (id, metadata) bijection ====================
        // Port of IndevTest_BlockToIndev / _BlockFromIndev / _BlockDataMeta /
        // _ApplyDataMeta - the authoritative view-id <-> genuine (id, Data nibble)
        // encoding used by the map generator (step 2) and .mclevel I/O (step 3).

        /// <summary> View id -> genuine on-disk Indev block id. </summary>
        public static byte ToIndev(byte b) {
            if (b <= 49) return b;                          // classic identity
            if (b >= CHEST_V0 && b <= CHEST_V0 + 3) return CHEST;
            if (b >= FURN_V0  && b <= FURN_V0  + 3) return FURNACE;
            if (b >= FURNL_V0 && b <= FURNL_V0 + 3) return FURNACE_LIT;
            if (b >= TORCH_W1 && b <= TORCH_W4)     return TORCH;
            if (b == FARMLAND || b == FARMLAND_WET) return 60; // genuine tilledField
            if (b >= CROPS_0 && b <= CROPS_7)       return 59; // genuine crops
            switch (b) {
                case TORCH: case FIRE: case WATER_SOURCE: case LAVA_SOURCE:
                case CHEST: case GEARS: case DIAMOND_ORE: case DIAMOND_BLOCK:
                case WORKBENCH: case FURNACE: case FURNACE_LIT:
                    return b;                               // genuine ids 1:1
                case 59: return 28;  // leftover turquoise wool -> clothCapri
                case 60: return 20;  // leftover ice            -> glass
                case 63: return 1;   // pillar      -> stone
                case 64: return 54;  // crate       -> chest
                case 65: return 1;   // stone brick -> stone
                default: return 1;   // anything else -> stone
            }
        }

        /// <summary> Genuine on-disk Indev id -> canonical view id (before the
        /// Data nibble is applied via ApplyDataMeta). </summary>
        public static byte FromIndev(byte b) {
            switch (b) {
                case TORCH: case FIRE: case WATER_SOURCE: case LAVA_SOURCE:
                case CHEST: case GEARS: case DIAMOND_ORE: case DIAMOND_BLOCK:
                case WORKBENCH: case FURNACE: case FURNACE_LIT:
                    return b;
                case 59: return CROPS_0;   // + stage from the Data nibble
                case 60: return FARMLAND;  // wet variant from the Data nibble
                default: return b <= 49 ? b : (byte)0;
            }
        }

        /// <summary> The genuine Data nibble a view id carries: container facing
        /// (2-5), farmland moisture, crop stage, torch orientation. </summary>
        public static int DataMeta(byte b) {
            if (b >= CHEST_V0 && b <= CHEST_V0 + 3) return 2 + (b - CHEST_V0);
            if (b >= FURN_V0  && b <= FURN_V0  + 3) return 2 + (b - FURN_V0);
            if (b >= FURNL_V0 && b <= FURNL_V0 + 3) return 2 + (b - FURNL_V0);
            if (b == FARMLAND_WET)                  return 7;
            if (b >= CROPS_0 && b <= CROPS_7)       return b - CROPS_0;
            if (b == TORCH)                         return 5; // standing
            if (b >= TORCH_W1 && b <= TORCH_W4)     return 1 + (b - TORCH_W1);
            return 0;
        }

        /// <summary> Applies a loaded Data nibble to a canonical view id,
        /// producing the facing/stage/moisture view variant. </summary>
        public static byte ApplyDataMeta(byte b, int meta) {
            int k = meta - 2;
            if (b == CHEST       && k >= 0 && k <= 3) return (byte)(CHEST_V0 + k);
            if (b == FURNACE     && k >= 0 && k <= 3) return (byte)(FURN_V0  + k);
            if (b == FURNACE_LIT && k >= 0 && k <= 3) return (byte)(FURNL_V0 + k);
            if (b == FARMLAND && meta > 0)            return FARMLAND_WET;
            if (b == CROPS_0 && meta > 0)             return (byte)(CROPS_0 + Math.Min(meta, 7));
            if (b == TORCH && meta >= 1 && meta <= 4) return (byte)(TORCH_W1 + meta - 1);
            return b;
        }
    }
}
