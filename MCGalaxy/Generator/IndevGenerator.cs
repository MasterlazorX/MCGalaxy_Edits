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
using MCGalaxy.Network;
using BlockID = System.UInt16;

namespace MCGalaxy.Generator
{
    /// <summary> Phase 1 (step 2): the Indev world generator - a port of the
    /// ClassiCube fork's src/IndevGen.c, which is itself a statement-for-statement,
    /// oracle-verified port of in-20100223's LevelGenerator.java. </summary>
    /// <remarks>
    /// The port preserves the things the client's parity write-up calls out as
    /// load-bearing: java.util.Random's exact LCG (three separate streams - the
    /// generator stream, World.random recreated at the end of Assembling, and
    /// findSpawn's own fresh Random), MathHelper's 65536-entry float sine table
    /// built from double-precision Math.Sin, double-vs-float expression precision,
    /// and the WR_* World-semantics replica (clamped reads, interior-only setBlock,
    /// falling sand, liquid wake-ups, flower pops that burn World.random draws).
    /// For the same seed/type/theme/size the output should match the client's
    /// generator block-for-block; the level array stores the client view ids
    /// (torch 50, diamond ore 56, wall torches 94-97 via extended blocks).
    ///
    /// Registered as the "Indev" map theme: /NewLvl name w h l indev [theme]
    /// [type] [seed] with themes normal/hell/paradise/woods and types
    /// inland/island/floating/flat. Width/length must be powers of two and
    /// height at least 64 (the genuine size grid). Generated maps come out
    /// survival-ready: SurvivalMode=Indev, hazards on, the Indev block set
    /// applied, theme env colours, and the spawn inside the generated house.
    /// </remarks>
    public sealed class IndevGenerator
    {
        public static void RegisterGenerators() {
            MapGen.Register("Indev", GenType.Advanced, Generate,
                "&HArgs: [theme] [type] [seed] &H- themes: &fnormal/hell/paradise/woods&H, " +
                "types: &finland/island/floating/flat&H. Width/length must be powers of 2, height 64+.");
        }

        static readonly string[] THEMES = { "normal", "hell", "paradise", "woods" };
        static readonly string[] TYPES  = { "inland", "island", "floating", "flat" };

        static bool Generate(Player p, Level lvl, MapGenArgs args) {
            int theme = 0, type = 0;
            args.ArgFilter = (arg) =>
                Array.IndexOf(THEMES, arg.ToLower()) >= 0 || Array.IndexOf(TYPES, arg.ToLower()) >= 0;
            args.ArgParser = (arg) => {
                int i = Array.IndexOf(THEMES, arg.ToLower());
                if (i >= 0) { theme = i; return true; }
                i = Array.IndexOf(TYPES, arg.ToLower());
                if (i >= 0) { type = i; return true; }
                return false;
            };
            if (!args.ParseArgs(p)) return false;

            if ((lvl.Width & (lvl.Width - 1)) != 0 || (lvl.Length & (lvl.Length - 1)) != 0) {
                p.Message("&WIndev generation needs power-of-two width and length (64, 128, 256, ...)");
                return false;
            }
            if (lvl.Height < 64) {
                p.Message("&WIndev generation needs a map height of at least 64.");
                return false;
            }

            IndevGenerator gen = new IndevGenerator(lvl, type, theme, args.Seed);
            gen.player = p;
            gen.Run();
            gen.ApplyLevelSettings();
            p.Message("Indev world ready: theme &b{0}&S, type &b{1}&S, seed &b{2}&S - spawn house at ({3}, {4}, {5}).",
                      THEMES[theme], TYPES[type], args.Seed, gen.spawnX, gen.spawnY, gen.spawnZ);
            return true;
        }


        // ==================== java.util.Random replica ====================

        sealed class JRandom
        {
            const ulong VALUE = 0x5DEECE66DUL, MASK = (1UL << 48) - 1;
            ulong state;

            public JRandom(int seed) { state = ((ulong)(long)seed ^ VALUE) & MASK; }

            public int NextBits(int bits) {
                state = (state * VALUE + 0xBUL) & MASK;
                return (int)(state >> (48 - bits));
            }

            public int Next(int n) {
                if ((n & -n) == n) { // power of 2
                    long raw = NextBits(31);
                    return (int)((n * raw) >> 31);
                }
                int bits, val;
                do {
                    bits = NextBits(31);
                    val  = bits % n;
                } while (bits - val + (n - 1) < 0);
                return val;
            }

            public float NextFloat() {
                return NextBits(24) / ((float)(1 << 24));
            }
        }


        // ==================== noise stack ====================

        sealed class Perlin
        {
            readonly int[] perm = new int[512];

            public Perlin(JRandom rnd) {
                int i, j, t;
                for (i = 0; i < 256; i++) perm[i] = i;
                for (i = 0; i < 256; i++) {
                    j = rnd.Next(256 - i) + i;
                    t = perm[i];
                    perm[i]       = perm[j];
                    perm[j]       = t;
                    perm[i + 256] = perm[i];
                }
            }

            static double Fade(double t) { return t * t * t * (t * (t * 6.0 - 15.0) + 10.0); }
            static double Lerp(double t, double a, double b) { return a + t * (b - a); }

            static double Grad(int hash, double x, double y, double z) {
                hash &= 15;
                double u = hash < 8 ? x : y;
                double v = hash < 4 ? y : (hash != 12 && hash != 14 ? z : x);
                return ((hash & 1) == 0 ? u : -u) + ((hash & 2) == 0 ? v : -v);
            }

            static int Floor(double d) {
                int i = (int)d;
                return d < i ? i - 1 : i;
            }

            // NoiseGeneratorPerlin.generateNoise(x, y) - 3D improved perlin at z = 0
            public double Noise(double x, double y) {
                int X = Floor(x) & 255, Y = Floor(y) & 255;
                double fx = x - Floor(x), fy = y - Floor(y), fz = 0.0;
                double u = Fade(fx), v = Fade(fy), w = Fade(fz);

                int A = perm[X] + Y,     AA = perm[A], AB = perm[A + 1];
                int B = perm[X + 1] + Y, BA = perm[B], BB = perm[B + 1];

                return Lerp(w,
                    Lerp(v,
                        Lerp(u, Grad(perm[AA], fx, fy, fz),
                                Grad(perm[BA], fx - 1.0, fy, fz)),
                        Lerp(u, Grad(perm[AB], fx, fy - 1.0, fz),
                                Grad(perm[BB], fx - 1.0, fy - 1.0, fz))),
                    Lerp(v,
                        Lerp(u, Grad(perm[AA + 1], fx, fy, fz - 1.0),
                                Grad(perm[BA + 1], fx - 1.0, fy, fz - 1.0)),
                        Lerp(u, Grad(perm[AB + 1], fx, fy - 1.0, fz - 1.0),
                                Grad(perm[BB + 1], fx - 1.0, fy - 1.0, fz - 1.0))));
            }
        }

        sealed class Octaves
        {
            readonly Perlin[] gens;

            public Octaves(JRandom rnd, int count) {
                gens = new Perlin[count];
                for (int i = 0; i < count; i++) gens[i] = new Perlin(rnd);
            }

            public double Noise(double x, double y) {
                double sum = 0.0, freq = 1.0;
                for (int i = 0; i < gens.Length; i++) {
                    sum += gens[i].Noise(x / freq, y / freq) * freq;
                    freq *= 2.0;
                }
                return sum;
            }
        }

        // NoiseGeneratorDistort: source sampled at x offset by the distort noise
        sealed class Distort
        {
            readonly Octaves source, distort;

            public Distort(JRandom rnd, int octaves) {
                source  = new Octaves(rnd, octaves);
                distort = new Octaves(rnd, octaves);
            }

            public double Noise(double x, double y) {
                return source.Noise(x + distort.Noise(x, y), y);
            }
        }


        // ==================== MathHelper sine table ====================
        // sin(f) = table[(int)(f * 10430.378F) & 0xFFFF], cos adds 16384. Built
        // with double Math.Sin - libm float variants shift worm paths off genuine.

        const float PI_F = 3.1415927f; // (float)Math.PI
        static float[] sinTable;
        static readonly object sinLock = new object();

        static void InitSinTable() {
            lock (sinLock) {
                if (sinTable != null) return;
                float[] t = new float[65536];
                for (int i = 0; i < 65536; i++) {
                    t[i] = (float)Math.Sin(i * Math.PI * 2.0 / 65536.0);
                }
                sinTable = t;
            }
        }

        static float MHSin(float f) { return sinTable[(int)(f * 10430.378f) & 0xFFFF]; }
        static float MHCos(float f) { return sinTable[(int)(f * 10430.378f + 16384.0f) & 0xFFFF]; }


        // ==================== instance state ====================

        readonly Level lvl;
        readonly byte[] blocks;
        readonly int width, length, height, volume;
        readonly int type, theme, seed;
        readonly JRandom rnd;
        Player player; // who requested the gen - receives the phase banners

        // The genuine Indev ProgressBarDisplay phase banner (LevelGenerator's
        // setText). Streamed to the generating player so the client sees the
        // same "Raising.. / Soiling.. / Growing.." progression Indev shows.
        void Status(string phase) {
            if (player != null) player.Message("&8Indev: &7{0}", phase);
        }

        bool IsIsland   { get { return type == 1; } }
        bool IsFloating { get { return type == 2; } }
        bool IsFlat     { get { return type == 3; } }

        int waterLevel, groundLevel, cloudHeight;
        int[] heightmap;
        internal int spawnX, spawnY, spawnZ;

        // the WR_* World-replica snapshot (light + heightMap from Assembling)
        byte[] wrLight;
        int[]  wrHeightMap;
        int    wrSkylight;

        // wall-torch cells recorded during GenerateHouse: bytes 94-97 sit in the
        // byte array through the later passes (client-identical semantics), then
        // are converted to extended custom blocks before the level goes live
        readonly List<int[]> wallTorches = new List<int[]>();

        IndevGenerator(Level lvl, int type, int theme, int seed) {
            this.lvl   = lvl;
            this.type  = type;
            this.theme = theme;
            this.seed  = seed;
            blocks = lvl.blocks;
            width  = lvl.Width; length = lvl.Length; height = lvl.Height;
            volume = width * length * height;

            InitSinTable();
            rnd = new JRandom(seed);
        }

        int  Index(int x, int y, int z)          { return (y * length + z) * width + x; }
        byte Get(int x, int y, int z)            { return blocks[(y * length + z) * width + x]; }
        void Set(int x, int y, int z, byte b)    { blocks[(y * length + z) * width + x] = b; }


        // ==================== flood fill ====================
        // LevelGenerator.floodFill: scanline fill; to = 255 is the probe pass -
        // aborts with -1 the moment the fill touches a map border.

        int[] ffStack;
        int   ffCount;

        void FFPush(int v) {
            if (ffCount == ffStack.Length) {
                int[] grown = new int[ffStack.Length * 2];
                Array.Copy(ffStack, grown, ffCount);
                ffStack = grown;
            }
            ffStack[ffCount++] = v;
        }

        long FloodFill(int x, int y, int z, int from, int to) {
            byte target = (byte)to, source = (byte)from;
            int shiftX = 1, shiftZ = 1;
            while ((1 << shiftX) < width)  shiftX++;
            while ((1 << shiftZ) < length) shiftZ++;
            int maskZ = length - 1, maskX = width - 1;
            int oneY  = width * length;
            long filled = 0;

            ffCount = 0;
            FFPush((((y << shiftZ) + z) << shiftX) + x);

            while (ffCount > 0) {
                int i  = ffStack[--ffCount];
                int zz = (i >> shiftX) & maskZ;
                int yy =  i >> (shiftX + shiftZ);
                int x0 = i & maskX;

                // expand to the full matching run along x
                int x1 = x0;
                while (x0 > 0     && blocks[i - 1]         == source) { x0--; i--; }
                while (x1 < width && blocks[i + (x1 - x0)] == source) { x1++; }

                if (to == 255 && (x0 == 0 || x1 == width - 1 ||
                    yy == 0 || yy == height - 1 || zz == 0 || zz == length - 1)) {
                    return -1; // probe touched the border - not a closed pocket
                }

                bool spreadNegZ = false, spreadPosZ = false, spreadNegY = false;
                filled += x1 - x0;

                for (; x0 < x1; x0++, i++) {
                    blocks[i] = target;
                    if (zz > 0) {
                        bool match = blocks[i - width] == source;
                        if (match && !spreadNegZ) FFPush(i - width);
                        spreadNegZ = match;
                    }
                    if (zz < length - 1) {
                        bool match = blocks[i + width] == source;
                        if (match && !spreadPosZ) FFPush(i + width);
                        spreadPosZ = match;
                    }
                    if (yy > 0) {
                        byte below = blocks[i - oneY];
                        // lava flooding over water freezes the water to stone
                        if ((target == Block.Lava || target == Block.StillLava) &&
                            (below == Block.Water || below == Block.StillWater)) {
                            blocks[i - oneY] = Block.Stone;
                        }
                        bool match = below == source;
                        if (match && !spreadNegY) FFPush(i - oneY);
                        spreadNegY = match;
                    }
                }
            }
            return filled;
        }


        // ==================== terrain passes ====================

        // "Raising.." + "Eroding.." - the distorted-noise heightmap
        void RaiseAndErode() {
            Distort d1 = new Distort(rnd, 8), d2 = new Distort(rnd, 8);
            Octaves o1 = new Octaves(rnd, 6), o2 = new Octaves(rnd, 2);

            for (int x = 0; x < width; x++) {
                double distFromCentreX = (x / (width - 1.0) - 0.5) * 2.0;
                if (distFromCentreX < 0) distFromCentreX = -distFromCentreX;

                for (int z = 0; z < length; z++) {
                    double distFromCentreZ = (z / (length - 1.0) - 0.5) * 2.0;
                    if (distFromCentreZ < 0) distFromCentreZ = -distFromCentreZ;
                    double hillA = d1.Noise(x * 1.3f, z * 1.3f) / 6.0 + -4.0;
                    double hillB = d2.Noise(x * 1.3f, z * 1.3f) / 5.0 + 10.0 + -4.0;
                    double mountains = o1.Noise(x, z) / 8.0;
                    if (mountains > 0.0) hillB = hillA;

                    double h = Math.Max(hillA, hillB) / 2.0;
                    if (IsIsland) {
                        // genuine: Math.sqrt (double) * (double)1.2F
                        double edge = Math.Sqrt(distFromCentreX * distFromCentreX +
                                                distFromCentreZ * distFromCentreZ) * (double)1.2f;
                        double coast = o2.Noise(x * 0.05f, z * 0.05f) / 4.0 + 1.0;
                        edge = Math.Min(edge, coast);
                        edge = Math.Max(edge, Math.Max(distFromCentreX, distFromCentreZ));
                        if (edge > 1.0) edge = 1.0;
                        if (edge < 0.0) edge = 0.0;

                        edge *= edge;
                        h = h * (1.0 - edge) - edge * 10.0 + 5.0;
                        if (h < 0.0) h -= h * h * 0.2f;
                    } else if (h < 0.0) {
                        h *= 0.8;
                    }

                    heightmap[x + z * width] = (int)h;
                }
            }

            d1 = new Distort(rnd, 8);
            d2 = new Distort(rnd, 8);
            for (int x = 0; x < width; x++) {
                for (int z = 0; z < length; z++) {
                    double erode = d1.Noise(x << 1, z << 1) / 8.0;
                    int eroded   = d2.Noise(x << 1, z << 1) > 0.0 ? 1 : 0;
                    if (erode > 2.0) {
                        int sh = heightmap[x + z * width];
                        sh = ((sh - eroded) / 2 << 1) + eroded;
                        heightmap[x + z * width] = sh;
                    }
                }
            }
        }

        // "Soiling.." - dirt over stone under the heightmap (with the floating-
        // layer carve-out), heights relative to this layer's water level
        void Soil() {
            Octaves o1 = new Octaves(rnd, 8), o2 = new Octaves(rnd, 8);

            for (int x = 0; x < width; x++) {
                double distX = (x / (width - 1.0) - 0.5) * 2.0;
                if (distX < 0) distX = -distX;

                for (int z = 0; z < length; z++) {
                    double distZ = (z / (length - 1.0) - 0.5) * 2.0;
                    if (distZ < 0) distZ = -distZ;
                    double corner = Math.Max(distX, distZ);
                    corner = corner * corner * corner;

                    int stoneY = (int)(o1.Noise(x, z) / 24.0) - 4;
                    int dirtY  = heightmap[x + z * width] + waterLevel;
                    stoneY = dirtY + stoneY;
                    heightmap[x + z * width] = Math.Max(dirtY, stoneY);
                    if (heightmap[x + z * width] > height - 2) heightmap[x + z * width] = height - 2;
                    if (heightmap[x + z * width] <= 0)         heightmap[x + z * width] = 1;

                    // genuine: (int)(Math.sqrt(Math.abs(n)) * Math.signum(n) * 20.0D) - all double
                    double cliff = o2.Noise(x * 2.3, z * 2.3) / 24.0;
                    int floatCut = (int)(Math.Sqrt(cliff < 0.0 ? -cliff : cliff) *
                                     (cliff < 0.0 ? -1.0 : (cliff > 0.0 ? 1.0 : 0.0)) * 20.0) + waterLevel;
                    floatCut = (int)(floatCut * (1.0 - corner) + corner * height);
                    if (floatCut > waterLevel) floatCut = height;

                    for (int y = 0; y < height; y++) {
                        int index = (y * length + z) * width + x;
                        int id = 0;
                        if (y <= dirtY)  id = Block.Dirt;
                        if (y <= stoneY) id = Block.Stone;
                        if (IsFloating && y < floatCut) id = 0;

                        if (blocks[index] == 0) blocks[index] = (byte)id;
                    }
                }
            }
        }

        // "Growing.." - surface pass: gravel under shallow water, sand (or hell
        // grass) on dry beach-height surfaces
        void Grow() {
            Octaves o1 = new Octaves(rnd, 8), o2 = new Octaves(rnd, 8);
            int sandY = waterLevel - 1;
            if (theme == 2) sandY += 2; // paradise: beaches reach higher

            for (int x = 0; x < width; x++) {
                for (int z = 0; z < length; z++) {
                    bool sandNoise = o1.Noise(x, z) > 8.0;
                    if (IsIsland)   sandNoise = o1.Noise(x, z) > -8.0;
                    if (theme == 2) sandNoise = o1.Noise(x, z) > -32.0;
                    if (theme == 1 || theme == 3) {
                        sandNoise = o1.Noise(x, z) > -8.0;
                    }
                    bool gravelNoise = o2.Noise(x, z) > 12.0;

                    int surfaceY = heightmap[x + z * width];
                    int index    = (surfaceY * length + z) * width + x;
                    byte above   = blocks[((surfaceY + 1) * length + z) * width + x];

                    if ((above == Block.Water || above == Block.StillWater || above == 0) &&
                        surfaceY <= waterLevel - 1 && gravelNoise) {
                        blocks[index] = Block.Gravel;
                    }

                    if (above == 0) {
                        int id = -1;
                        if (surfaceY <= sandY && sandNoise) {
                            id = Block.Sand;
                            if (theme == 1) id = Block.Grass; // hell quirk
                        }
                        if (blocks[index] != 0 && id > 0) blocks[index] = (byte)id;
                    }
                }
            }
        }

        // "Carving.." - the worm-tunnel caves
        void Carve() {
            int caves = width * length * height / 256 / 64 << 1;

            for (int i = 0; i < caves; i++) {
                float x = rnd.NextFloat() * width;
                float y = rnd.NextFloat() * height;
                float z = rnd.NextFloat() * length;
                int steps = (int)((rnd.NextFloat() + rnd.NextFloat()) * 200.0f);

                float dirXZ       = rnd.NextFloat() * PI_F * 2.0f;
                float dirXZChange = 0.0f;
                float dirY        = rnd.NextFloat() * PI_F * 2.0f;
                float dirYChange  = 0.0f;
                float sizeMul     = rnd.NextFloat() * rnd.NextFloat();

                for (int step = 0; step < steps; step++) {
                    x += MHSin(dirXZ) * MHCos(dirY);
                    z += MHCos(dirXZ) * MHCos(dirY);
                    y += MHSin(dirY);

                    dirXZ += dirXZChange * 0.2f;
                    dirXZChange *= 0.9f;
                    dirXZChange += rnd.NextFloat() - rnd.NextFloat();
                    dirY += dirYChange * 0.5f;
                    dirY *= 0.5f;
                    dirYChange *= 12.0f / 16.0f;
                    dirYChange += rnd.NextFloat() - rnd.NextFloat();

                    if (rnd.NextFloat() < 0.25f) continue;
                    float cx = x + (rnd.NextFloat() * 4.0f - 2.0f) * 0.2f;
                    float cy = y + (rnd.NextFloat() * 4.0f - 2.0f) * 0.2f;
                    float cz = z + (rnd.NextFloat() * 4.0f - 2.0f) * 0.2f;

                    float heightF  = (height - cy) / height;
                    float baseSize = 1.2f + (heightF * 3.5f + 1.0f) * sizeMul;
                    float size     = MHSin(step * PI_F / steps) * baseSize;

                    for (int xx = (int)(cx - size); xx <= (int)(cx + size); xx++) {
                        for (int yy = (int)(cy - size); yy <= (int)(cy + size); yy++) {
                            for (int zz = (int)(cz - size); zz <= (int)(cz + size); zz++) {
                                float dx = xx - cx, dy = yy - cy, dz = zz - cz;
                                float distSq = dx * dx + dy * dy * 2.0f + dz * dz;
                                if (distSq < size * size &&
                                    xx > 0 && yy > 0 && zz > 0 &&
                                    xx < width - 1 && yy < height - 1 && zz < length - 1) {
                                    int index = (yy * length + zz) * width + xx;
                                    if (blocks[index] == Block.Stone) blocks[index] = 0;
                                }
                            }
                        }
                    }
                }
            }
        }

        // populateOre - the same worm shape, thinner, replacing stone
        void PopulateOre(byte ore, int abundance, int veinSize, int maxHeight) {
            int count = width * length * height / 256 / 64 * abundance / 100;

            for (int i = 0; i < count; i++) {
                float x = rnd.NextFloat() * width;
                float y = rnd.NextFloat() * height;
                float z = rnd.NextFloat() * length;
                if (y > maxHeight) continue;

                int steps = (int)((rnd.NextFloat() + rnd.NextFloat()) * 75.0f * veinSize / 100.0f);
                float dirXZ       = rnd.NextFloat() * PI_F * 2.0f;
                float dirXZChange = 0.0f;
                float dirY        = rnd.NextFloat() * PI_F * 2.0f;
                float dirYChange  = 0.0f;

                for (int step = 0; step < steps; step++) {
                    x += MHSin(dirXZ) * MHCos(dirY);
                    z += MHCos(dirXZ) * MHCos(dirY);
                    y += MHSin(dirY);

                    dirXZ += dirXZChange * 0.2f;
                    dirXZChange *= 0.9f;
                    dirXZChange += rnd.NextFloat() - rnd.NextFloat();
                    dirY += dirYChange * 0.5f;
                    dirY *= 0.5f;
                    dirYChange *= 0.9f;
                    dirYChange += rnd.NextFloat() - rnd.NextFloat();

                    float size = MHSin(step * PI_F / steps) * veinSize / 100.0f + 1.0f;

                    for (int xx = (int)(x - size); xx <= (int)(x + size); xx++) {
                        for (int yy = (int)(y - size); yy <= (int)(y + size); yy++) {
                            for (int zz = (int)(z - size); zz <= (int)(z + size); zz++) {
                                float dx = xx - x, dy = yy - y, dz = zz - z;
                                float distSq = dx * dx + dy * dy * 2.0f + dz * dz;
                                if (distSq < size * size &&
                                    xx > 0 && yy > 0 && zz > 0 &&
                                    xx < width - 1 && yy < height - 1 && zz < length - 1) {
                                    int index = (yy * length + zz) * width + xx;
                                    if (blocks[index] == Block.Stone) blocks[index] = ore;
                                }
                            }
                        }
                    }
                }
            }
        }

        // "Melting.." - underground lava pockets, biased deep by a quadruple-min
        void LavaGen() {
            int attempts = width * length * height / 2000;
            int ground   = groundLevel;

            for (int i = 0; i < attempts; i++) {
                int x  = rnd.Next(width);
                // genuine draws exactly four, left to right
                int d1 = rnd.Next(ground);
                int d2 = rnd.Next(ground);
                int d3 = rnd.Next(ground);
                int d4 = rnd.Next(ground);
                int y  = Math.Min(Math.Min(d1, d2), Math.Min(d3, d4));
                int z  = rnd.Next(length);

                if (blocks[(y * length + z) * width + x] != 0) continue;
                long size = FloodFill(x, y, z, 0, 255);
                if (size > 0 && size < 640) {
                    FloodFill(x, y, z, 255, Block.StillLava);
                } else {
                    FloodFill(x, y, z, 255, 0);
                }
            }
        }

        // "Watering.." part 1 - theme springs: small closed air pockets anywhere
        // become still water (lava on hell)
        void LiquidThemeSpawner() {
            byte fluid = theme == 1 ? Block.StillLava : Block.StillWater;
            int attempts = width * length * height / 1000;

            for (int i = 0; i < attempts; i++) {
                int x = rnd.Next(width);
                int y = rnd.Next(height);
                int z = rnd.Next(length);

                if (blocks[(y * length + z) * width + x] != 0) continue;
                long size = FloodFill(x, y, z, 0, 255);
                if (size > 0 && size < 640) {
                    FloodFill(x, y, z, 255, fluid);
                } else {
                    FloodFill(x, y, z, 255, 0);
                }
            }
        }


        // ==================== genuine World replica (Building/Planting) ====================
        // World.generate() initialises heightMap + the light nibbles once at the
        // end of Assembling; after that setBlock only QUEUES light updates - so
        // findSpawn/growGrass/growTrees/flowers all see this static snapshot.

        const byte TORCH = SurvivalBlocks.TORCH; // view id 50, fits the byte array

        // genuine opaqueCubeLookup over every id the generator can produce.
        // NB: the wall-torch view bytes 94-97 fall through to true - identical
        // to the client port (they only exist inside the spawn house).
        static bool WROpaque(byte b) {
            switch (b) {
                case 0:
                case Block.Water: case Block.StillWater:
                case Block.Lava:  case Block.StillLava:
                case Block.Leaves: case Block.Sapling:
                case Block.Dandelion: case Block.Rose:
                case Block.Mushroom: case Block.RedMushroom:
                case TORCH:
                    return false;
            }
            return true;
        }
        // Material.isSolid - false for air, liquids and plants/circuits
        static bool WRSolid(byte b) {
            switch (b) {
                case 0:
                case Block.Water: case Block.StillWater:
                case Block.Lava:  case Block.StillLava:
                case Block.Sapling: case Block.Dandelion: case Block.Rose:
                case Block.Mushroom: case Block.RedMushroom:
                case TORCH:
                    return false;
            }
            return true;
        }
        static bool WRLiquid(byte b) {
            return b == Block.Water || b == Block.StillWater ||
                   b == Block.Lava  || b == Block.StillLava;
        }
        // Material.liquidSolidCheck - the cells liquid may flow into
        static bool WRFlowable(byte b) { return !WRLiquid(b) && !WRSolid(b); }
        // Material.getCanBlockGrass - everything except air and plants
        static bool WRBlocksGrass(byte b) {
            switch (b) {
                case 0:
                case Block.Sapling: case Block.Dandelion: case Block.Rose:
                case Block.Mushroom: case Block.RedMushroom:
                case TORCH:
                    return false;
            }
            return true;
        }
        static int WRLightOpacity(byte b) {
            if (b == Block.Water || b == Block.StillWater) return 3;
            if (b == Block.Lava  || b == Block.StillLava)  return 255;
            if (b == Block.Leaves) return 1;
            return WROpaque(b) ? 255 : 0;
        }
        static int WRLightValue(byte b) {
            if (b == Block.Lava || b == Block.StillLava) return 15;
            if (b == TORCH)          return 14;
            if (b == Block.Mushroom) return 1; // brown mushroom's faint glow
            return 0;
        }

        // World.getBlockId - out-of-range coordinates CLAMP to the map edge
        byte WRGetId(int x, int y, int z) {
            if (x < 0) x = 0; else if (x >= width)  x = width  - 1;
            if (y < 0) y = 0; else if (y >= height) y = height - 1;
            if (z < 0) z = 0; else if (z >= length) z = length - 1;
            return blocks[(y * length + z) * width + x];
        }
        // World.getBlockLightValue (clamped)
        int WRLight(int x, int y, int z) {
            if (x < 0) x = 0; else if (x >= width)  x = width  - 1;
            if (y < 0) y = 0; else if (y >= height) y = height - 1;
            if (z < 0) z = 0; else if (z >= length) z = length - 1;
            return wrLight[(y * length + z) * width + x];
        }
        bool WRCanSeeSky(int x, int y, int z) {
            if (wrHeightMap[x + z * width] <= y) return true;
            for (; y < height; y++) {
                if (WROpaque(Get(x, y, z))) return false;
            }
            return true;
        }

        // the light/heightMap init from World.generate()
        void WRInit() {
            wrSkylight  = theme == 1 ? 7 : (theme == 3 ? 12 : 15);
            wrLight     = new byte[volume];
            wrHeightMap = new int[width * length];

            for (int x = 0; x < width; x++) {
                for (int z = 0; z < length; z++) {
                    int y;
                    for (y = height - 1; y > 0 && WRLightOpacity(Get(x, y, z)) == 0; y--) { }
                    int hm = y + 1;
                    wrHeightMap[x + z * width] = hm;

                    for (y = 0; y < height; y++) {
                        int index = (y * length + z) * width + x;
                        int light = y >= hm ? wrSkylight : 0;
                        if (light < WRLightValue(blocks[index])) light = WRLightValue(blocks[index]);
                        wrLight[index] = (byte)light;
                    }
                }
            }
        }

        // World.setTileNoUpdate - full-map bounds, no reactions
        bool WRSetTileNoUpdate(int x, int y, int z, byte id) {
            if (x < 0 || y < 0 || z < 0 ||
                x >= width || y >= height || z >= length) return false;
            int index = (y * length + z) * width + x;
            if (blocks[index] == id) return false;
            blocks[index] = id;
            return true;
        }

        void WROnAdded(int x, int y, int z, byte id) {
            // the only onBlockAdded reactions that matter during generation
            if (id == Block.Sand || id == Block.Gravel) WRTryToFall(x, y, z);
        }

        // World.setBlock - INTERIOR only: the outermost shell is never written
        bool WRSetBlock(int x, int y, int z, byte id) {
            if (!(x > 0 && y > 0 && z > 0 &&
                x < width - 1 && y < height - 1 && z < length - 1)) return false;
            int index = (y * length + z) * width + x;
            byte old  = blocks[index];
            if (id == old) return false;

            blocks[index] = id;
            if (id != 0) WROnAdded(x, y, z, id);
            // light updates are queued, not applied, until the Lighting phase
            return true;
        }
        bool WRSetBlockNotify(int x, int y, int z, byte id) {
            if (!WRSetBlock(x, y, z, id)) return false;
            WRNotifyAll(x, y, z, id);
            return true;
        }
        // World.swap (used by falling sand)
        void WRSwap(int x1, int y1, int z1, int x2, int y2, int z2) {
            byte a = WRGetId(x1, y1, z1);
            byte b = WRGetId(x2, y2, z2);
            WRSetBlock(x1, y1, z1, b);
            WRSetBlock(x2, y2, z2, a);
            WRNotifyAll(x1, y1, z1, b);
            WRNotifyAll(x2, y2, z2, a);
        }

        // BlockSand.tryToFall - every read clamps like getBlockId, and the
        // landing scan can run off the bottom of the world (cur = -1)
        void WRTryToFall(int x, int y, int z) {
            int cur = y;

            for (;;) {
                byte below = WRGetId(x, cur - 1, z);
                if (!(below == 0 || WRLiquid(below)) || cur < 0) break;
                cur--;
            }

            if (cur < 0) WRSetTileNoUpdate(x, y, z, 0);
            if (cur != y) {
                byte target = WRGetId(x, cur, z);
                if (target > 0) WRSetTileNoUpdate(x, cur, z, 0);
                WRSwap(x, y, z, x, cur, z);
            }
        }

        // BlockFlower/BlockMushroom.canBlockStay
        bool WRCanBlockStay(byte plant, int x, int y, int z) {
            byte below = WRGetId(x, y - 1, z);

            if (plant == Block.Mushroom || plant == Block.RedMushroom) {
                return WRLight(x, y, z) <= 13 && WROpaque(below);
            }
            int light = WRLight(x, y, z);
            if (!(light >= 8 || (light >= 4 && WRCanSeeSky(x, y, z)))) return false;
            return below == Block.Grass || below == Block.Dirt;
        }
        // checkFlowerChange: a popped plant burns 4 World.random floats for its
        // item drop (pickup chance + 3 spawn offsets) before clearing itself
        void WRFlowerCheck(byte plant, int x, int y, int z) {
            if (WRCanBlockStay(plant, x, y, z)) return;
            worldRnd2.NextFloat();
            worldRnd2.NextFloat();
            worldRnd2.NextFloat();
            worldRnd2.NextFloat();
            WRSetBlockNotify(x, y, z, 0);
        }

        // BlockFluid.canFlow (with the water sponge scan)
        bool WRCanFlow(bool isWater, int x, int y, int z) {
            if (!WRFlowable(WRGetId(x, y, z))) return false;
            if (isWater) {
                for (int xx = x - 2; xx <= x + 2; xx++) {
                    for (int yy = y - 2; yy <= y + 2; yy++) {
                        for (int zz = z - 2; zz <= z + 2; zz++) {
                            if (WRGetId(xx, yy, zz) == Block.Sponge) return false;
                        }
                    }
                }
            }
            return true;
        }
        // BlockStationary.onNeighborBlockChange: opposite liquid -> stone, else a
        // flowable neighbour wakes the block to its MOVING id
        void WRStillLiquidCheck(byte self, int x, int y, int z, byte changed) {
            bool isWater = self == Block.StillWater;

            bool can = WRCanFlow(isWater, x, y - 1, z) ||
                       WRCanFlow(isWater, x - 1, y, z) ||
                       WRCanFlow(isWater, x + 1, y, z) ||
                       WRCanFlow(isWater, x, y, z - 1) ||
                       WRCanFlow(isWater, x, y, z + 1);

            if (changed != 0) {
                bool chgWater = changed == Block.Water || changed == Block.StillWater;
                bool chgLava  = changed == Block.Lava  || changed == Block.StillLava;
                if ((isWater && chgLava) || (chgWater && !isWater)) {
                    WRSetBlockNotify(x, y, z, Block.Stone);
                    return;
                }
            }
            if (can) WRSetTileNoUpdate(x, y, z, isWater ? Block.Water : Block.Lava);
        }

        void WRNotifyOne(int x, int y, int z, byte changed) {
            if (x < 0 || y < 0 || z < 0 ||
                x >= width || y >= height || z >= length) return;
            byte b = Get(x, y, z);

            switch (b) {
                case Block.Sand: case Block.Gravel:
                    WRTryToFall(x, y, z); return;
                case Block.Sapling: case Block.Dandelion: case Block.Rose:
                case Block.Mushroom: case Block.RedMushroom:
                    WRFlowerCheck(b, x, y, z); return;
                case Block.StillWater: case Block.StillLava:
                    WRStillLiquidCheck(b, x, y, z, changed); return;
            }
        }
        void WRNotifyAll(int x, int y, int z, byte changed) {
            WRNotifyOne(x - 1, y, z, changed);
            WRNotifyOne(x + 1, y, z, changed);
            WRNotifyOne(x, y - 1, z, changed);
            WRNotifyOne(x, y + 1, z, changed);
            WRNotifyOne(x, y, z - 1, changed);
            WRNotifyOne(x, y, z + 1, changed);
        }


        // ==================== assembling / building / planting ====================

        // "Assembling.." - World.generate()'s floor/border fill (with the genuine
        // y-skip quirk for interior columns at World.java:118)
        JRandom Assemble() {
            byte fluid = theme == 1 ? Block.Lava : Block.Water; // defaultFluid = MOVING id
            int cap = (groundLevel > waterLevel && theme != 1) ? Block.Grass : Block.Dirt;

            for (int x = 0; x < width; x++) {
                for (int z = 0; z < length; z++) {
                    bool border = x == 0 || z == 0 || x == width - 1 || z == length - 1;

                    for (int y = 0; y < height; y++) {
                        int index = (y * length + z) * width + x;
                        int id    = 0;
                        if (y <= 1 && y < groundLevel - 1 && blocks[index + width * length] == 0) {
                            id = Block.StillLava;
                        } else if (y < groundLevel - 1) {
                            id = Block.Bedrock;
                        } else if (y < groundLevel) {
                            id = cap;
                        } else if (y < waterLevel) {
                            id = fluid;
                        }

                        blocks[index] = (byte)id;
                        if (y == 1 && !border) y = height - 2;
                    }
                }
            }

            // World.load() runs at the end of Assembling: it recreates World.random
            // (the tree-shape/drop stream) and burns one nextInt() for randId
            JRandom wr = new JRandom(seed + 1);
            wr.NextBits(32);
            return wr;
        }

        // World.getFirstUncoveredBlock: first y above the topmost non-air block
        int FirstUncovered(int x, int z) {
            int y;
            for (y = height; y > 0; y--) {
                if (Get(x, y - 1, z) != 0) break;
            }
            return y;
        }

        // World.findSpawn: a random mid-map surface spot above water with room
        // for (and a solid foundation under) the spawn house
        void FindSpawn() {
            // genuine findSpawn creates its OWN fresh Random
            JRandom sr = new JRandom(seed + 2);
            int attempts = 0;

            for (;;) {
                attempts++;
                int x = sr.Next(width  / 2) + width  / 4;
                int z = sr.Next(length / 2) + length / 4;
                int y = FirstUncovered(x, z) + 1;

                if (attempts == 1000000) {
                    // graceful deviation (matches the client port): drop to the
                    // surface column we already sampled instead of the sky
                    spawnX = x;
                    spawnY = FirstUncovered(x, z) + 2;
                    spawnZ = z;
                    return;
                }
                if (y < 4 || y <= waterLevel) continue;

                // the house volume must be clear of solids (clamped reads)
                bool ok = true;
                for (int xx = x - 3; xx <= x + 3 && ok; xx++) {
                    for (int yy = y - 1; yy <= y + 2 && ok; yy++) {
                        for (int zz = z - 3 - 2; zz <= z + 3 && ok; zz++) {
                            if (WRSolid(WRGetId(xx, yy, zz))) ok = false;
                        }
                    }
                }
                if (!ok) continue;

                // and the ground under the footprint must be fully opaque
                int fy = y - 2;
                for (int xx = x - 3; xx <= x + 3 && ok; xx++) {
                    for (int zz = z - 3 - 2; zz <= z + 3 && ok; zz++) {
                        if (!WROpaque(WRGetId(xx, fy, zz))) ok = false;
                    }
                }
                if (!ok) continue;

                spawnX = x;
                spawnY = y;
                spawnZ = z;
                return;
            }
        }

        // LevelGenerator.generateHouse: the 7x5x7 stone/plank shelter around the
        // spawn - obsidian floor slab, door gap on the -Z face, two wall torches
        void GenerateHouse() {
            int x1 = spawnX, y1 = spawnY, z1 = spawnZ;
            if (y1 >= height) return; // the sky-fallback spawn

            for (int x = x1 - 3; x <= x1 + 3; x++) {
                for (int y = y1 - 2; y <= y1 + 2; y++) {
                    for (int z = z1 - 3; z <= z1 + 3; z++) {
                        int id = y < y1 - 1 ? Block.Obsidian : 0;
                        if (x == x1 - 3 || z == z1 - 3 || x == x1 + 3 || z == z1 + 3 ||
                            y == y1 - 2 || y == y1 + 2) {
                            id = Block.Stone;
                            if (y >= y1 - 1) id = Block.Wood;
                        }
                        if (z == z1 - 3 && x == x1 && y >= y1 - 1 && y <= y1) id = 0; // doorway

                        WRSetBlockNotify(x, y, z, (byte)id);
                    }
                }
            }
            WRSetBlockNotify(x1 - 2, y1, z1, TORCH);
            WRSetBlockNotify(x1 + 2, y1, z1, TORCH);

            // genuine BlockTorch.onBlockAdded mounts the torch on its first solid
            // neighbour (-X, +X, -Z, +Z order) - the house torches hang on the
            // side walls (metadata 1/2 = view ids 94/95), they do NOT stand.
            // The view byte sits in the array through the later passes (client-
            // identical) and is converted to an extended block at the very end.
            int[] tdx = { -2, 2 };
            for (int t = 0; t < 2; t++) {
                int tx = x1 + tdx[t];
                int meta = 0;
                if      (WROpaque(WRGetId(tx - 1, y1, z1))) meta = 1;
                else if (WROpaque(WRGetId(tx + 1, y1, z1))) meta = 2;
                else if (WROpaque(WRGetId(tx, y1, z1 - 1))) meta = 3;
                else if (WROpaque(WRGetId(tx, y1, z1 + 1))) meta = 4;
                if (meta != 0) {
                    blocks[(y1 * length + z1) * width + tx] = (byte)(93 + meta); // TORCH_W1 (94) + meta-1
                    wallTorches.Add(new int[] { tx, y1, z1 });
                }
            }
        }

        // growGrassOnDirt: EVERY lit dirt block in the volume becomes grass -
        // light comes from the static Assembling-time snapshot
        void GrowGrass() {
            for (int x = 0; x < width; x++) {
                for (int y = 0; y < height; y++) {
                    for (int z = 0; z < length; z++) {
                        if (Get(x, y, z) != Block.Dirt)          continue;
                        if (WRLight(x, y + 1, z) < 4)            continue;
                        if (WRBlocksGrass(WRGetId(x, y + 1, z))) continue;
                        WRSetBlock(x, y, z, Block.Grass); // setBlock - no notify
                    }
                }
            }
        }

        // World.growTrees(x, y, z): trunk rand(3)+4, clearance envelope, diamond
        // leaf canopy with random corner trimming
        bool GrowTree(int x, int y, int z) {
            // shape draws come from World.random, not the generator stream
            int trunkH = worldRnd2.Next(3) + 4;

            if (y <= 0 || y + trunkH + 1 > height) return false;

            for (int yy = y; yy <= y + 1 + trunkH; yy++) {
                int clearance = 1;
                if (yy == y) clearance = 0;
                if (yy >= y + 1 + trunkH - 2) clearance = 2;

                for (int xx = x - clearance; xx <= x + clearance; xx++) {
                    for (int zz = z - clearance; zz <= z + clearance; zz++) {
                        if (xx < 0 || yy < 0 || zz < 0 ||
                            xx >= width || yy >= height || zz >= length) return false;
                        if (Get(xx, yy, zz) != 0) return false;
                    }
                }
            }

            byte below = Get(x, y - 1, z);
            if (below != Block.Grass && below != Block.Dirt) return false;
            if (y >= height - trunkH - 1) return false;
            WRSetBlockNotify(x, y - 1, z, Block.Dirt);

            for (int yy = y - 3 + trunkH; yy <= y + trunkH; yy++) {
                int dy     = yy - (y + trunkH);
                int radius = 1 - dy / 2;

                for (int xx = x - radius; xx <= x + radius; xx++) {
                    int dxa = xx - x; if (dxa < 0) dxa = -dxa;
                    for (int zz = z - radius; zz <= z + radius; zz++) {
                        int dza = zz - z; if (dza < 0) dza = -dza;
                        if (dxa == radius && dza == radius &&
                            (worldRnd2.Next(2) == 0 || dy == 0)) continue;
                        if (!WROpaque(WRGetId(xx, yy, zz))) {
                            WRSetBlockNotify(xx, yy, zz, Block.Leaves);
                        }
                    }
                }
            }

            for (int yy = 0; yy < trunkH; yy++) {
                if (!WROpaque(WRGetId(x, y + yy, z))) {
                    WRSetBlockNotify(x, y + yy, z, Block.Log);
                }
            }
            return true;
        }

        void GrowTrees() {
            int clusters = width * length * height / 80000;

            for (int i = 0; i < clusters; i++) {
                int x = rnd.Next(width);
                int y = rnd.Next(height);
                int z = rnd.Next(length);

                for (int j = 0; j < 25; j++) {
                    int xx = x, yy = y, zz = z;
                    for (int k = 0; k < 20; k++) {
                        xx += rnd.Next(12) - rnd.Next(12);
                        yy += rnd.Next(3)  - rnd.Next(6);
                        zz += rnd.Next(12) - rnd.Next(12);
                        if (xx >= 0 && yy >= 0 && zz >= 0 &&
                            xx < width && yy < height && zz < length) {
                            GrowTree(xx, yy, zz);
                        }
                    }
                }
            }
        }

        // populateFlowersAndMushrooms: flowers need grass/dirt + sky exposure,
        // mushrooms just a solid floor (they live in caves)
        void Populate(byte plant, int rawCount) {
            int count = (int)((long)width * length * height * rawCount / 1600000);

            for (int i = 0; i < count; i++) {
                int x = rnd.Next(width);
                int y = rnd.Next(height);
                int z = rnd.Next(length);

                for (int j = 0; j < 10; j++) {
                    int xx = x, yy = y, zz = z;
                    for (int k = 0; k < 10; k++) {
                        xx += rnd.Next(4) - rnd.Next(4);
                        yy += rnd.Next(2) - rnd.Next(2);
                        zz += rnd.Next(4) - rnd.Next(4);
                        if (xx < 0 || zz < 0 || yy <= 0 ||
                            xx >= width || zz >= length || yy >= height) continue;
                        if (Get(xx, yy, zz) != 0) continue;

                        if (!WRCanBlockStay(plant, xx, yy, zz)) continue;
                        WRSetBlockNotify(xx, yy, zz, plant);
                    }
                }
            }
        }


        // ==================== main pipeline ====================

        JRandom worldRnd2; // World.random, live from the end of Assembling

        void Run() {
            heightmap = new int[width * length];
            ffStack   = new int[1048576];
            Array.Clear(blocks, 0, volume);

            int layers = 1;
            if (IsFloating) layers = (height - 64) / 48 + 1;

            for (int layer = 0; layer < layers; layer++) {
                waterLevel  = height - 32 - layer * 48;
                groundLevel = waterLevel - 2;

                if (IsFlat) {
                    for (int i = 0; i < width * length; i++) heightmap[i] = 0;
                } else {
                    if (layer == 0) { Status("Raising.."); Status("Eroding.."); }
                    RaiseAndErode();
                }
                if (layer == 0) Status("Soiling..");
                Soil();
                if (layer == 0) Status("Growing..");
                Grow();
            }

            Status("Carving..");
            Carve();
            // genuine runs the ore veins under the "Carving.." banner
            PopulateOre(Block.CoalOre, 1000, 10, (height << 2) / 5);
            PopulateOre(Block.IronOre,  800,  8, height * 3 / 5);
            PopulateOre(Block.GoldOre,  500,  6, (height << 1) / 5);
            PopulateOre(SurvivalBlocks.DIAMOND_ORE, 800, 2, height / 5);
            Status("Melting..");
            LavaGen();

            cloudHeight = height + 2;
            if (IsFloating) {
                groundLevel = -128;
                waterLevel  = groundLevel + 1;
                cloudHeight = -16;
            } else if (!IsIsland) {
                groundLevel = waterLevel + 1;
                waterLevel  = groundLevel - 16;
            } else {
                groundLevel = waterLevel - 9;
            }

            Status("Watering..");
            LiquidThemeSpawner();
            if (!IsFloating) {
                byte edgeFluid = theme == 1 ? Block.StillLava : Block.StillWater;
                for (int x = 0; x < width; x++) {
                    FloodFill(x, waterLevel - 1, 0,          0, edgeFluid);
                    FloodFill(x, waterLevel - 1, length - 1, 0, edgeFluid);
                }
                for (int z = 0; z < length; z++) {
                    FloodFill(width - 1, waterLevel - 1, z, 0, edgeFluid);
                    FloodFill(0,         waterLevel - 1, z, 0, edgeFluid);
                }
            }
            if (theme == 1 && IsFloating) {
                cloudHeight = height + 2;
                waterLevel = -16;
            }

            worldRnd2 = Assemble();
            // World.generate() also computed heightMap + the light snapshot that
            // every Building/Planting pass reads from
            Status("Lighting..");
            WRInit();

            FindSpawn();
            Status("Building..");
            GenerateHouse();

            Status("Planting..");
            if (theme != 1) GrowGrass();

            GrowTrees();
            if (theme == 3) { // woods: 50 extra tree passes
                for (int i = 0; i < 50; i++) GrowTrees();
            }

            int flowers = theme == 2 ? 1000 : 100; // paradise: 10x the flowers
            Populate(Block.Dandelion,   flowers);
            Populate(Block.Rose,        flowers);
            Populate(Block.Mushroom,     50);
            Populate(Block.RedMushroom,  50);

            // the wall-torch view bytes (94-97) are physics ids to MCGalaxy's
            // byte array - convert them to real extended custom blocks now
            foreach (int[] pos in wallTorches) {
                int index = (pos[1] * length + pos[2]) * width + pos[0];
                byte view = blocks[index];
                blocks[index] = 0;
                lvl.SetBlock((ushort)pos[0], (ushort)pos[1], (ushort)pos[2],
                             Block.FromRaw((BlockID)view));
            }

            wrLight = null; wrHeightMap = null;
            heightmap = null; ffStack = null;
        }

        // theme environments, straight from the client's ApplyPostLoad tail
        void ApplyLevelSettings() {
            LevelConfig cfg = lvl.Config;

            int sky, fog, clouds;
            if (theme == 0)      { sky = 10079487; fog = 16777215; clouds = 16777215; }
            else if (theme == 1) { sky = 1049600;  fog = 1049600;  clouds = 2164736;  }
            else if (theme == 2) { sky = 13033215; fog = 13033215; clouds = 15658751; }
            else                 { sky = 7699847;  fog = 5069403;  clouds = 5069403;  }
            cfg.SkyColor   = sky.ToString("X6");
            cfg.FogColor   = fog.ToString("X6");
            cfg.CloudColor = clouds.ToString("X6");

            cfg.CloudsHeight = cloudHeight;
            cfg.EdgeLevel    = waterLevel;
            cfg.SidesOffset  = groundLevel - waterLevel;
            // the OOB horizon planes approximated with stock env blocks: the
            // fluid plane (horizon) + the ground plane (sides)
            cfg.HorizonBlock = theme == 1 ? Block.StillLava : Block.StillWater;
            cfg.EdgeBlock    = (theme == 1 || groundLevel <= waterLevel) ? Block.Dirt : Block.Grass;

            // generated worlds come out survival-ready
            cfg.SurvivalMode  = SurvivalMode.Indev;
            cfg.SurvivalTheme = IsFloating ? SurvivalTheme.Floating : (SurvivalTheme)theme;
            cfg.SurvivalDeath = true; // hazards on (the spawn is grounded by findSpawn)
            SurvivalBlocks.Sync(lvl);

            // spawn inside the house, facing the genuine rotSpawn = 180
            lvl.spawnx = (ushort)spawnX;
            lvl.spawny = (ushort)spawnY;
            lvl.spawnz = (ushort)spawnZ;
            lvl.rotx   = 128; // yaw 180 degrees
            lvl.roty   = 0;
        }
    }
}
