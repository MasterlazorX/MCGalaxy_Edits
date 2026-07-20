# Generating & running Indev worlds

The quick operator's guide to creating Indev survival maps and the commands
that go with them. For the internals see `session-notes.md` (the
`IndevGenerator.cs` and `SurvivalBlocks.cs` sections) and `roadmap.md`.

---

## 1. Generate a world

```
/NewLvl <name> <width> <height> <length> indev [theme] [type] [seed]
```

| Part | Values | Default | Notes |
|---|---|---|---|
| `width`/`length` | 64, 128, 256, 512, ... | — | **must be powers of two** (the genuine size grid; the generator's flood fill packs coordinates into bit shifts) |
| `height` | 64+ | — | 64 is the genuine depth; taller floating maps grow extra island layers (one per 48 blocks above 64) |
| `theme` | `normal` / `hell` / `paradise` / `woods` | `normal` | hell = lava oceans, dark red sky, no water; paradise = always-bright colours, high sandy beaches, 10× flowers; woods = dense forest, dim sky |
| `type` | `inland` / `island` / `floating` / `flat` | `inland` | inland = grass horizon plane, buried water table; island = ocean map; floating = layered sky islands over void; flat = flatgrass with the full decoration pipeline |
| `seed` | any integer | random | same seed + size + theme + type ⇒ the same world, on the server and in the client's own generator |

Theme and type are independent words in any order — `indev hell floating 42`
works. Anything that parses as an integer is the seed.

### Examples

```
/NewLvl survival1 128 64 128 indev                      # random normal inland
/NewLvl skylands 128 96 128 indev floating 777          # two-layer floating islands
/NewLvl inferno 256 64 256 indev hell island            # lava ocean island, random seed
/NewLvl meadow 128 64 128 indev paradise island 333
/NewLvl forest 128 64 128 indev woods 222
```

Generation takes well under a second at 128×64×128. The console prints the
theme/type/seed and where the spawn house landed.

## 2. What you get

Generated maps come out **survival-ready** — no follow-up commands needed:

- `SurvivalMode = Indev` with hazards (`SurvivalDeath`) already on.
- The full **Indev block set** applied as level-scoped BlockDefinitions
  (torches, chest, workbench, furnaces, diamond, farmland, crops...).
- The genuine **spawn house**: 7×5×7 stone/plank shelter, obsidian floor
  slab, doorway facing the spawn direction, two wall-mounted torches.
  Players spawn inside it, facing the door.
- Theme **environment**: sky/fog/cloud colours, the water-or-lava horizon
  plane and ground-plane sides, cloud height (below the islands on floating
  maps), all stored in the level's env config — survival clients get the
  genuine values via `SURV_WORLDINFO`, stock clients get the CPE env
  approximation.
- The mob spawner starts populating the map as soon as players are on it.

To make it the server's default map: `/Main <name>`.

## 3. The /Survival command

`/Survival` with no arguments shows the current level's settings (including
the "server build" tripwire line - if a feature seems missing, check that
line matches the phases you expect).

| Command | What it does |
|---|---|
| `/Survival [off/classic/indev]` | sets the per-map survival mode; `indev` also applies the block set + hazards, `off` strips them |
| `/Survival theme [normal/hell/paradise/woods/floating]` | sets the WORLDINFO theme byte (generated maps set this themselves) |
| `/Survival visitors [visitor/allow/deny]` | what non-survival clients may do: look-only (default) / build freely / not even join |
| `/Survival [enhanced/creative/pvp/deathdrops] [on/off]` | gameplay flags; `creative` = free build, no consume/pickup (the fork client switches to the Indev creative palette inventory; the classic picker deposits stacks into it) |
| `/Survival give [block] <count> <player>` | puts blocks in a survival player's server inventory (e.g. `torch`, `chest`, `workbench`, `diamondore`, or a raw id). Count defaults to a stack; console must name the player |
| `/Survival spawn [zombie/skeleton/pig/creeper/spider/sheep]` | spawns a test mob at your feet |
| `/Survival mobs` | lists the nearest live mobs |
| `/Survival spawner` | natural-spawn statistics + clock state |
| `/Survival time [day/noon/sunset/night/midnight/<ticks>]` | shows or sets the shared world clock |
| `/Survival inv [player]` | dumps a player's server-side inventory |

Changes apply live — survival clients on the level get a fresh handshake
without rejoining.

## 4. Turning an EXISTING map into a survival map

```
/Goto myMap
/Survival indev
```

That flips the mode, applies the block set, enables hazard detection, and
grounds a floating spawn point. Caveats for hand-built maps:

- The spawn must not be buried inside terrain — a buried spawn is a death
  loop (auto-grounding only fixes spawns floating in the air). Use
  `/SetSpawn` on a safe surface spot first if in doubt.
- The env (horizon/sides/colours) is whatever the map already had; only
  generated maps get the genuine Indev theme environment automatically.

## 5. Client-side notes

- The **fork client** negotiates the `SurvivalTest` CPE extension (v2) and
  re-defines the genuine block models locally on the survival handshake;
  it needs its normal first-run asset download for the Indev terrain tiles.
- **Stock CPE clients** see the server's BlockDefinitions (sprite torches,
  textured chests/furnaces) and the day/night cycle as env-colour scaling;
  they cannot modify survival maps unless `/Survival visitors allow` is set.
- Old survival clients (ext v1) still work — they just receive the legacy
  WORLDINFO layout, which clamps floating maps' negative ground/water
  levels (cosmetic: a horizon plane may show under floating islands).
