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

namespace MCGalaxy.Network
{
    /// <summary> The Indev ITEM definitions + crafting/smelting/drop tables - a
    /// port of the ClassiCube fork's tables (src/IndevTest.c indevItems[] /
    /// indevRecipes[] / the RecipesTools+Weapons+Armor generators /
    /// Furnace_SmeltResult / Furnace_FuelTime, and src/SurvivalTest.c's
    /// SpawnIndevDrops + IndevTest_CanHarvest), themselves ports of
    /// in-20100223's Item/CraftingManager/TileEntityFurnace/Block classes. </summary>
    /// <remarks>
    /// The full-space id model matches the client exactly: ids &lt; 256 are
    /// blocks (view ids), ids 256+ are items (256 + the genuine Item local id).
    /// The wire already carries u16 ids everywhere, and the client renders item
    /// icons from its own table - so items need NO client changes and no ext
    /// bump. Both recipe tables must stay identical: the client computes the
    /// craft-result PREVIEW locally from the streamed grid, while the server
    /// authoritatively crafts on SURV_RESULT_CLICK.
    /// </remarks>
    public static class SurvivalItems
    {
        // item kinds (client ITEM_KIND_*) - drives max stacks + harvest gating
        const byte K_MAT = 0, K_SWORD = 1, K_SHOVEL = 2, K_PICKAXE = 3, K_AXE = 4,
                   K_HOE = 5, K_FLINTSTEEL = 6, K_BOW = 7, K_FOOD = 8, K_SOUP = 9,
                   K_SEEDS = 10, K_ARMOR = 11;

        struct ItemDef
        {
            public byte Id, Kind, Param; // param = tool tier / food heal / armor piece
            public string Name;
            public ItemDef(byte id, byte kind, byte param, string name) {
                Id = id; Kind = kind; Param = param; Name = name;
            }
        }

        // 1:1 with the client's indevItems[] (icons are client-side only)
        static readonly ItemDef[] items = {
            new ItemDef( 0, K_SHOVEL, 2, "Iron Shovel"),   new ItemDef( 1, K_PICKAXE, 2, "Iron Pickaxe"),
            new ItemDef( 2, K_AXE, 2, "Iron Axe"),         new ItemDef( 3, K_FLINTSTEEL, 0, "Flint and Steel"),
            new ItemDef( 4, K_FOOD, 4, "Apple"),           new ItemDef( 5, K_BOW, 0, "Bow"),
            new ItemDef( 6, K_MAT, 0, "Arrow"),            new ItemDef( 7, K_MAT, 0, "Coal"),
            new ItemDef( 8, K_MAT, 0, "Diamond"),          new ItemDef( 9, K_MAT, 0, "Iron Ingot"),
            new ItemDef(10, K_MAT, 0, "Gold Ingot"),       new ItemDef(11, K_SWORD, 2, "Iron Sword"),
            new ItemDef(12, K_SWORD, 0, "Wooden Sword"),   new ItemDef(13, K_SHOVEL, 0, "Wooden Shovel"),
            new ItemDef(14, K_PICKAXE, 0, "Wooden Pickaxe"),new ItemDef(15, K_AXE, 0, "Wooden Axe"),
            new ItemDef(16, K_SWORD, 1, "Stone Sword"),    new ItemDef(17, K_SHOVEL, 1, "Stone Shovel"),
            new ItemDef(18, K_PICKAXE, 1, "Stone Pickaxe"),new ItemDef(19, K_AXE, 1, "Stone Axe"),
            new ItemDef(20, K_SWORD, 3, "Diamond Sword"),  new ItemDef(21, K_SHOVEL, 3, "Diamond Shovel"),
            new ItemDef(22, K_PICKAXE, 3, "Diamond Pickaxe"),new ItemDef(23, K_AXE, 3, "Diamond Axe"),
            new ItemDef(24, K_MAT, 0, "Stick"),            new ItemDef(25, K_MAT, 0, "Bowl"),
            new ItemDef(26, K_SOUP, 10, "Mushroom Soup"),  new ItemDef(27, K_SWORD, 0, "Golden Sword"),
            new ItemDef(28, K_SHOVEL, 0, "Golden Shovel"), new ItemDef(29, K_PICKAXE, 0, "Golden Pickaxe"),
            new ItemDef(30, K_AXE, 0, "Golden Axe"),       new ItemDef(31, K_MAT, 0, "String"),
            new ItemDef(32, K_MAT, 0, "Feather"),          new ItemDef(33, K_MAT, 0, "Gunpowder"),
            new ItemDef(34, K_HOE, 0, "Wooden Hoe"),       new ItemDef(35, K_HOE, 1, "Stone Hoe"),
            new ItemDef(36, K_HOE, 2, "Iron Hoe"),         new ItemDef(37, K_HOE, 3, "Diamond Hoe"),
            new ItemDef(38, K_HOE, 4, "Golden Hoe"),       new ItemDef(39, K_SEEDS, 0, "Seeds"),
            new ItemDef(40, K_MAT, 0, "Wheat"),            new ItemDef(41, K_FOOD, 5, "Bread"),
            new ItemDef(42, K_ARMOR, 0, "Leather Cap"),    new ItemDef(43, K_ARMOR, 1, "Leather Tunic"),
            new ItemDef(44, K_ARMOR, 2, "Leather Pants"),  new ItemDef(45, K_ARMOR, 3, "Leather Boots"),
            new ItemDef(46, K_ARMOR, 0, "Chain Helmet"),   new ItemDef(47, K_ARMOR, 1, "Chain Chestplate"),
            new ItemDef(48, K_ARMOR, 2, "Chain Leggings"), new ItemDef(49, K_ARMOR, 3, "Chain Boots"),
            new ItemDef(50, K_ARMOR, 0, "Iron Helmet"),    new ItemDef(51, K_ARMOR, 1, "Iron Chestplate"),
            new ItemDef(52, K_ARMOR, 2, "Iron Leggings"),  new ItemDef(53, K_ARMOR, 3, "Iron Boots"),
            new ItemDef(54, K_ARMOR, 0, "Diamond Helmet"), new ItemDef(55, K_ARMOR, 1, "Diamond Chestplate"),
            new ItemDef(56, K_ARMOR, 2, "Diamond Leggings"),new ItemDef(57, K_ARMOR, 3, "Diamond Boots"),
            new ItemDef(58, K_ARMOR, 0, "Golden Helmet"),  new ItemDef(59, K_ARMOR, 1, "Golden Chestplate"),
            new ItemDef(60, K_ARMOR, 2, "Golden Leggings"),new ItemDef(61, K_ARMOR, 3, "Golden Boots"),
            new ItemDef(62, K_MAT, 0, "Flint"),
            new ItemDef(63, K_FOOD, 3, "Raw Porkchop"),
            new ItemDef(64, K_FOOD, 8, "Cooked Porkchop"),
            new ItemDef(65, K_MAT, 0, "Painting"),
        };

        static ItemDef? Find(ushort id) {
            int local = id - 256;
            if (local < 0) return null;
            foreach (ItemDef d in items) { if (d.Id == local) return d; }
            return null;
        }

        /// <summary> Display name of an item id (256+), or null. </summary>
        public static string NameOf(ushort id) {
            ItemDef? d = Find(id);
            return d.HasValue ? d.Value.Name : null;
        }

        /// <summary> Finds an item id by display name; spaces/underscores are
        /// skipped on both sides ("iron_pickaxe" == "Iron Pickaxe"). 0 = none. </summary>
        public static ushort FindByName(string query) {
            foreach (ItemDef d in items)
            {
                if (NameMatches(d.Name, query)) return (ushort)(256 + d.Id);
            }
            return 0;
        }

        static bool NameMatches(string name, string query) {
            int i = 0, j = 0;
            for (;;) {
                while (i < name.Length && name[i] == ' ') i++;
                while (j < query.Length && (query[j] == ' ' || query[j] == '_')) j++;
                if (i == name.Length || j == query.Length) return i == name.Length && j == query.Length;
                char a = char.ToLowerInvariant(name[i]), b = char.ToLowerInvariant(query[j]);
                if (a != b) return false;
                i++; j++;
            }
        }

        // ItemFood/tools set maxStackSize = 1; every other item defaults to 64;
        // blocks keep the engine's 99 (the client never overrides block ids).
        static bool StacksToOne(byte kind) {
            return kind == K_SWORD || kind == K_SHOVEL || kind == K_PICKAXE ||
                   kind == K_AXE   || kind == K_HOE    || kind == K_FLINTSTEEL ||
                   kind == K_BOW   || kind == K_SOUP   || kind == K_ARMOR || kind == K_FOOD;
        }

        /// <summary> Per-id max stack, matching the client: blocks 99, items 64,
        /// tools/food/armor 1. </summary>
        public static int MaxStack(ushort id) {
            if (id < 256) return 99;
            ItemDef? d = Find(id);
            if (d.HasValue && StacksToOne(d.Value.Kind)) return 1;
            return 64;
        }


        // ==================== right-click item use (IndevTest_UseHeldItem / TryEat) ====================

        public const ushort SEEDS = 256 + 39;
        public const ushort SOUP  = 256 + 26;
        public const ushort BOWL  = 256 + 25;

        /// <summary> Whether the id is a hoe (any tier). </summary>
        public static bool IsHoe(ushort id) {
            ItemDef? d = Find(id);
            return d.HasValue && d.Value.Kind == K_HOE;
        }

        /// <summary> Whether the id is flint &amp; steel. </summary>
        public static bool IsFlintSteel(ushort id) {
            ItemDef? d = Find(id);
            return d.HasValue && d.Value.Kind == K_FLINTSTEEL;
        }

        /// <summary> The armor piece an item equips (SlotArmor.getArmorType /
        /// ItemArmor.armorType: 0 helmet, 1 chestplate, 2 leggings, 3 boots), or
        /// -1 when the item is not armor. </summary>
        public static int ArmorPiece(ushort id) {
            ItemDef? d = Find(id);
            if (!d.HasValue || d.Value.Kind != K_ARMOR) return -1;
            return d.Value.Param;
        }

        // ItemArmor durability: maxDamage {11,16,15,13}[piece] * 3 << tier, with
        // tiers cloth 0 / chain 1 / iron 2 / diamond 3 / GOLD 1 (gold really has
        // chain durability). Armor ids are contiguous 256+42..61, 4 per set.
        static readonly int[] armorBase  = { 11, 16, 15, 13 }; // helmet, chestplate, leggings, boots
        static readonly int[] armorTier  = { 0, 1, 2, 3, 1 };  // cloth, chain, iron, diamond, gold
        static readonly int[] armorReduce = { 3, 8, 6, 3 };    // damageReduceAmount per piece (tier-independent)

        /// <summary> ItemArmor.getMaxDamage for an armor id (durability), or 0. </summary>
        public static int ArmorMaxDamage(ushort id) {
            int piece = ArmorPiece(id);
            if (piece < 0) return 0;
            return armorBase[piece] * 3 << armorTier[(id - 256 - 42) / 4];
        }

        /// <summary> ItemArmor.damageReduceAmount for an armor id, or 0. </summary>
        public static int ArmorReduce(ushort id) {
            int piece = ArmorPiece(id);
            return piece < 0 ? 0 : armorReduce[piece];
        }

        /// <summary> HP an edible item restores (ItemFood/ItemSoup param), else 0. </summary>
        public static int FoodHeal(ushort id) {
            ItemDef? d = Find(id);
            if (!d.HasValue) return 0;
            return (d.Value.Kind == K_FOOD || d.Value.Kind == K_SOUP) ? d.Value.Param : 0;
        }

        /// <summary> ItemTool.maxDamage: 32 &lt;&lt; tier for tools/hoes, 64 for
        /// flint &amp; steel, 0 for anything that doesn't take durability. </summary>
        public static int MaxDurability(ushort id) {
            ItemDef? d = Find(id);
            if (!d.HasValue) return 0;
            switch (d.Value.Kind) {
                case K_SWORD: case K_SHOVEL: case K_PICKAXE: case K_AXE: case K_HOE:
                    return 32 << d.Value.Param;
                case K_FLINTSTEEL: return 64;
                default: return 0;
            }
        }


        // ==================== harvest gating (IndevTest_CanHarvest) ====================

        // blocks whose genuine material is rock/iron (client: dig sound stone/metal);
        // gears (55) is Material.circuits despite its sounds - exempt
        static readonly HashSet<ushort> rockClass = new HashSet<ushort> {
            Block.Stone, Block.Cobblestone, Block.GoldOre, Block.IronOre, Block.CoalOre,
            Block.Gold, Block.Iron, Block.DoubleSlab, Block.Slab, Block.Brick,
            Block.MossyRocks, Block.Obsidian,
            SurvivalBlocks.DIAMOND_ORE, SurvivalBlocks.DIAMOND_BLOCK,
            SurvivalBlocks.FURNACE, SurvivalBlocks.FURNACE_LIT,
            75, 76, 77, 78, 79, 80, 81, 82, // furnace facing views
        };

        /// <summary> Block.canHarvestBlock: rock/iron-material blocks yield drops
        /// only with a pickaxe of sufficient tier. </summary>
        public static bool CanHarvest(ushort heldId, ushort viewBlock) {
            if (!rockClass.Contains(viewBlock)) return true;
            ItemDef? d = Find(heldId);
            if (!d.HasValue || d.Value.Kind != K_PICKAXE) return false;
            int level = d.Value.Param;
            switch (viewBlock) {
                case Block.Obsidian: return level == 3;
                case SurvivalBlocks.DIAMOND_ORE:
                case SurvivalBlocks.DIAMOND_BLOCK: return level >= 2;
                case Block.GoldOre: case Block.Gold: return level >= 2;
                case Block.IronOre: case Block.Iron: return level > 0;
                default: return true;
            }
        }


        // ==================== mining drops (SpawnIndevDrops) ====================

        /// <summary> What mining a view block yields on an Indev map (v1: straight
        /// into the inventory - drop entities are phase 5). May yield nothing
        /// (leaves/glass/liquids/wrong tool) or several stacks (crops). </summary>
        public static void MiningDrops(Random rng, ushort view, ushort heldId, List<KeyValuePair<ushort, int>> drops) {
            // crops: wheat only at full stage + up to 3 stage-weighted seed rolls
            if (view >= SurvivalBlocks.CROPS_0 && view <= SurvivalBlocks.CROPS_7) {
                int stage = view - SurvivalBlocks.CROPS_0;
                if (stage == 7) drops.Add(new KeyValuePair<ushort, int>(256 + 40, 1)); // Wheat
                for (int i = 0; i < 3; i++) {
                    if (rng.Next(15) <= stage) drops.Add(new KeyValuePair<ushort, int>(256 + 39, 1)); // Seeds
                }
                return;
            }
            if (view == SurvivalBlocks.FARMLAND || view == SurvivalBlocks.FARMLAND_WET) {
                drops.Add(new KeyValuePair<ushort, int>(Block.Dirt, 1));
                return;
            }

            // directional views drop the canonical form; a LIT furnace drops the
            // lit block 62 (no idDropped override in Indev); wall torch -> torch
            ushort b = view;
            if (b >= SurvivalBlocks.CHEST_V0 && b <= SurvivalBlocks.CHEST_V0 + 3) b = SurvivalBlocks.CHEST;
            else if (b >= SurvivalBlocks.FURN_V0  && b <= SurvivalBlocks.FURN_V0  + 3) b = SurvivalBlocks.FURNACE;
            else if (b >= SurvivalBlocks.FURNL_V0 && b <= SurvivalBlocks.FURNL_V0 + 3) b = SurvivalBlocks.FURNACE_LIT;
            else if (b >= SurvivalBlocks.TORCH_W1 && b <= SurvivalBlocks.TORCH_W4)     b = SurvivalBlocks.TORCH;

            if (!CanHarvest(heldId, b)) return;

            ushort dropId = b; int count = 1;
            switch (b) {
                case Block.Grass:     dropId = Block.Dirt; break;
                case Block.Stone:     dropId = Block.Cobblestone; break; // BlockStone -> cobble
                case Block.Obsidian:  dropId = Block.Cobblestone; break; // obsidian IS a BlockStone
                case Block.CoalOre:   dropId = 256 + 7; break;           // -> coal ITEM
                case SurvivalBlocks.DIAMOND_ORE: dropId = 256 + 8; break; // -> diamond ITEM
                case Block.Leaves:    dropId = Block.Sapling;
                                      count = rng.Next(10) == 0 ? 1 : 0; break;
                case Block.Gravel:    if (rng.Next(10) == 0) dropId = 256 + 62; break; // flint
                case Block.DoubleSlab: dropId = Block.Slab; break;
                case Block.Glass: case Block.Bookshelf:
                case Block.Water: case Block.StillWater:
                case Block.Lava:  case Block.StillLava:
                case SurvivalBlocks.FIRE:
                    return; // quantityDropped 0 / liquids / fire
                case Block.TNT:
                    // Genuine SpawnIndevDrops arms a primed TNT and drops NOTHING
                    // (SurvivalTest.c:709). The primed-TNT explosion itself needs
                    // the phase-5 entity sim; the important thing here is to NOT
                    // hand back a free TNT block (that was an infinite-TNT dupe,
                    // since TNT is craftable).
                    return;
            }
            if (count > 0) drops.Add(new KeyValuePair<ushort, int>(dropId, count));
        }


        // ==================== smelting (TileEntityFurnace) ====================

        /// <summary> FurnaceRecipes.smelting: what an input smelts into (0 = nothing). </summary>
        public static ushort SmeltResult(ushort id) {
            if (id == Block.IronOre) return 256 + 9;  // Iron Ingot
            if (id == Block.GoldOre) return 256 + 10; // Gold Ingot
            if (id == SurvivalBlocks.DIAMOND_ORE) return 256 + 8; // Diamond
            if (id == Block.Sand)        return Block.Glass;
            if (id == Block.Cobblestone) return Block.Stone;
            if (id == 256 + 63)          return (ushort)(256 + 64); // Raw -> Cooked Porkchop
            return 0;
        }

        // wood-material blocks by the client's dig-sound rule (torch/fire excluded)
        static readonly HashSet<ushort> woodFuel = new HashSet<ushort> {
            Block.Wood, Block.Log, Block.Bookshelf,
            SurvivalBlocks.WORKBENCH, SurvivalBlocks.CHEST, 71, 72, 73, 74,
        };

        /// <summary> TileEntityFurnace.getItemBurnTime, in ticks (0 = not fuel). </summary>
        public static int FuelTime(ushort id) {
            if (id == 256 + 7)  return 1600; // Coal
            if (id == 256 + 24) return 100;  // Stick
            if (woodFuel.Contains(id)) return 300; // Material.wood
            return 0;
        }


        // ==================== crafting (CraftingManager) ====================

        class Recipe
        {
            public ushort Result; public byte Count, W, H;
            public ushort[] Cells;
            public Recipe(ushort result, byte count, byte w, byte h, params ushort[] cells) {
                Result = result; Count = count; W = w; H = h; Cells = cells;
            }
        }

        const ushort STICK = 256 + 24;
        static readonly Recipe[] recipes = {
            // planks x4 <- log; sticks x4 <- 2 planks; slabs x3; bread; gray cloth
            new Recipe(Block.Wood, 4, 1, 1, Block.Log),
            new Recipe(STICK,      4, 1, 2, Block.Wood, Block.Wood),
            new Recipe(Block.Slab, 3, 3, 1, Block.Cobblestone, Block.Cobblestone, Block.Cobblestone),
            new Recipe(256 + 41,   1, 3, 1, 256 + 40, 256 + 40, 256 + 40),
            new Recipe(Block.Gray, 1, 3, 3, 256+31,256+31,256+31, 256+31,256+31,256+31, 256+31,256+31,256+31),
            // TNT: gunpowder/sand checkerboard
            new Recipe(Block.TNT,  1, 3, 3, 256+33,Block.Sand,256+33, Block.Sand,256+33,Block.Sand, 256+33,Block.Sand,256+33),
            // the Indev blocks: workbench (2x2!), torches; chest/furnace need 3x3
            new Recipe(SurvivalBlocks.WORKBENCH, 1, 2, 2, Block.Wood, Block.Wood, Block.Wood, Block.Wood),
            new Recipe(SurvivalBlocks.TORCH,     4, 1, 2, 256 + 7, STICK),
            new Recipe(SurvivalBlocks.CHEST,     1, 3, 3, Block.Wood,Block.Wood,Block.Wood, Block.Wood,0,Block.Wood, Block.Wood,Block.Wood,Block.Wood),
            new Recipe(SurvivalBlocks.FURNACE,   1, 3, 3, Block.Cobblestone,Block.Cobblestone,Block.Cobblestone, Block.Cobblestone,0,Block.Cobblestone, Block.Cobblestone,Block.Cobblestone,Block.Cobblestone),
            // bowls x4; mushroom soup (both orders); flint&steel
            new Recipe(256 + 25, 4, 3, 2, Block.Wood,0,Block.Wood, 0,Block.Wood,0),
            new Recipe(256 + 26, 1, 1, 3, Block.RedMushroom, Block.Mushroom, 256 + 25),
            new Recipe(256 + 26, 1, 1, 3, Block.Mushroom, Block.RedMushroom, 256 + 25),
            new Recipe(256 + 3,  1, 2, 2, 256 + 9, 0, 0, 256 + 62),
            // bow; arrows x4
            new Recipe(256 + 5,  1, 3, 3, 0,STICK,256+31, STICK,0,256+31, 0,STICK,256+31),
            new Recipe(256 + 6,  4, 1, 3, 256 + 9, STICK, 256 + 32),
            // RecipesIngots: 9 ingots/gems <-> storage block, both directions
            new Recipe(Block.Gold, 1, 3, 3, 256+10,256+10,256+10, 256+10,256+10,256+10, 256+10,256+10,256+10),
            new Recipe(Block.Iron, 1, 3, 3, 256+9,256+9,256+9, 256+9,256+9,256+9, 256+9,256+9,256+9),
            new Recipe(SurvivalBlocks.DIAMOND_BLOCK, 1, 3, 3, 256+8,256+8,256+8, 256+8,256+8,256+8, 256+8,256+8,256+8),
            new Recipe(256 + 10, 9, 1, 1, Block.Gold),
            new Recipe(256 + 9,  9, 1, 1, Block.Iron),
            new Recipe(256 + 8,  9, 1, 1, SurvivalBlocks.DIAMOND_BLOCK),
            // painting: ring of planks around gray cloth
            new Recipe(256 + 65, 1, 3, 3, Block.Wood,Block.Wood,Block.Wood, Block.Wood,Block.Gray,Block.Wood, Block.Wood,Block.Wood,Block.Wood),
        };

        // 5 materials x 5 tool shapes (RecipesTools/RecipesWeapons)
        static readonly ushort[] toolMaterial = { Block.Wood, Block.Cobblestone, 256 + 9, 256 + 8, 256 + 10 };
        // item local ids per material: pickaxe, shovel, axe, hoe, sword
        static readonly byte[][] toolResult = {
            new byte[] { 14, 13, 15, 34, 12 }, // wood
            new byte[] { 18, 17, 19, 35, 16 }, // stone
            new byte[] {  1,  0,  2, 36, 11 }, // iron
            new byte[] { 22, 21, 23, 37, 20 }, // diamond
            new byte[] { 29, 28, 30, 38, 27 }, // gold
        };

        // RecipesArmor: cloth / chain(FIRE!) / iron / diamond / gold
        static readonly ushort[] armorMaterial = { Block.Gray, SurvivalBlocks.FIRE, 256 + 9, 256 + 8, 256 + 10 };
        static readonly byte[] armorSet = { 42, 46, 50, 54, 58 };

        static bool MatchesAt(ushort[] cells, int rw, int rh, ushort[] grid, int gw, int gh,
                              int ox, int oy, bool mirror) {
            for (int y = 0; y < gh; y++) {
                for (int x = 0; x < gw; x++) {
                    int rx = x - ox, ry = y - oy;
                    ushort want = 0;
                    if (rx >= 0 && rx < rw && ry >= 0 && ry < rh) {
                        if (mirror) rx = rw - 1 - rx;
                        want = cells[ry * rw + rx];
                    }
                    if (grid[y * gw + x] != want) return false;
                }
            }
            return true;
        }

        static bool Matches(ushort[] cells, int rw, int rh, ushort[] grid, int gw, int gh) {
            // CraftingRecipe.matchRecipe: every offset, unmirrored AND mirrored
            if (rw > gw || rh > gh) return false;
            for (int oy = 0; oy + rh <= gh; oy++) {
                for (int ox = 0; ox + rw <= gw; ox++) {
                    if (MatchesAt(cells, rw, rh, grid, gw, gh, ox, oy, false)) return true;
                    if (MatchesAt(cells, rw, rh, grid, gw, gh, ox, oy, true))  return true;
                }
            }
            return false;
        }

        /// <summary> Matches a gw*gh grid of full-space ids (0 = empty) against
        /// every recipe. Must stay identical to the client's IndevTest_MatchRecipe
        /// (the client shows the preview, the server crafts). </summary>
        public static bool MatchRecipe(ushort[] grid, int gw, int gh, out ushort id, out int count) {
            id = 0; count = 0;
            foreach (Recipe r in recipes)
            {
                if (!Matches(r.Cells, r.W, r.H, grid, gw, gh)) continue;
                id = r.Result; count = r.Count;
                return true;
            }

            // generated tool shapes (X = material, S = stick), tightly packed
            byte[] patW = { 3, 1, 2, 2, 1 };
            byte[] patH = { 3, 3, 3, 3, 3 };
            for (int m = 0; m < 5; m++) {
                ushort X = toolMaterial[m], S = STICK;
                ushort[][] pats = {
                    new ushort[] { X,X,X, 0,S,0, 0,S,0 }, // pickaxe 3x3
                    new ushort[] { X,S,S },               // shovel  1x3
                    new ushort[] { X,X, X,S, 0,S },       // axe     2x3
                    new ushort[] { X,X, 0,S, 0,S },       // hoe     2x3
                    new ushort[] { X,X,S },               // sword   1x3
                };
                for (int t = 0; t < 5; t++) {
                    if (!Matches(pats[t], patW[t], patH[t], grid, gw, gh)) continue;
                    id = (ushort)(256 + toolResult[m][t]); count = 1;
                    return true;
                }
            }

            // armor shapes (chain armor is genuinely crafted from FIRE blocks)
            byte[] aW = { 3, 3, 3, 3 };
            byte[] aH = { 2, 3, 3, 2 };
            for (int m = 0; m < 5; m++) {
                ushort X = armorMaterial[m];
                ushort[][] pats = {
                    new ushort[] { X,X,X, X,0,X },        // helmet 3x2
                    new ushort[] { X,0,X, X,X,X, X,X,X }, // chestplate 3x3
                    new ushort[] { X,X,X, X,0,X, X,0,X }, // leggings 3x3
                    new ushort[] { X,0,X, X,0,X },        // boots 3x2
                };
                for (int t = 0; t < 4; t++) {
                    if (!Matches(pats[t], aW[t], aH[t], grid, gw, gh)) continue;
                    id = (ushort)(256 + armorSet[m] + t); count = 1;
                    return true;
                }
            }
            return false;
        }
    }
}
