# Survival test-rig setup (fresh container)

A fresh session gets a clean container — the whole build/test toolchain must be
reinstalled. This is the exact toolchain this project uses. All of it was present
and working in the sessions that built the survival stack; on a truly bare image
run the install steps first.

## 1. Toolchain to install

**System packages** (Debian/Ubuntu base):

```bash
sudo apt-get update && sudo apt-get install -y \
    build-essential \       # gcc + make (ClassiCube C client)
    libx11-dev libxi-dev libgl1-mesa-dev \   # ClassiCube `make linux` libs: -lX11 -lXi -lGL
    xvfb \                  # headless X server for the graphical client (DISPLAY=:99)
    gdb \                   # driving/inspecting the client (attach, call functions)
    imagemagick \           # `import`/`convert` for screenshots
    python3                 # synthetic protocol clients + NBT parsers (usually preinstalled)
```

**.NET 8 SDK** (MCGalaxy server) — installs to `~/.dotnet`:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$PATH:$HOME/.dotnet          # so `dotnet` is on PATH; else use ~/.dotnet/dotnet
```

Verify: `~/.dotnet/dotnet --version` → 8.0.x, and `gcc make gdb Xvfb import python3` all resolve.

## 2. Build

```bash
# MCGalaxy server (from /home/user/mcgalaxy) -> CLI/bin/Release/net8.0/MCGalaxyCLI.dll
~/.dotnet/dotnet build CLI/MCGalaxyCLI_dotnet8.csproj -c Release -v q
# (the legacy MCGalaxy_.csproj lists command files explicitly; the CLI/dotnet8 csproj globs.
#  When adding a NEW Commands/*.cs file, also add it to MCGalaxy_.csproj.)

# ClassiCube fork client (from /home/user/ClassiCube) -> ./ClassiCube
make linux -j4
```

## 3. Launch the rig

```bash
# headless X
Xvfb :99 -screen 0 1280x800x24 &

# MCGalaxy server with a FIFO console (send commands by writing to console.in)
cd /home/user/mcgalaxy/CLI/bin/Release/net8.0
mkfifo console.in
DOTNET_ROOT=$HOME/.dotnet nohup sh -c 'tail -f console.in | $HOME/.dotnet/dotnet MCGalaxyCLI.dll > console.out 2>&1' &
# console:  echo "/survival" > console.in ;  read: tail console.out
# main level is gt1 (a generated Indev world, seed 12345)

# fork client (positional args: name mppass ip port)
cd /home/user/ClassiCube
DISPLAY=:99 ./ClassiCube CCUser x 127.0.0.1 25565 &

# screenshot the client
DISPLAY=:99 import -window root shot.png

# drive/inspect the client via gdb (one attach per script, keep them short)
timeout 40 gdb -p $(pgrep -x ClassiCube) -batch -x script.gdb
```

## 4. Synthetic protocol clients (`test-clients/`)

Headless Python clients that speak the classic/CPE + survival wire directly - test
the SERVER without the graphical client (invaluable when the graphical rig is
flaky). Run against a live server on 127.0.0.1:25565:

- `blockdef_client.py <name> [cmd] [secs]` — CPE client negotiating BlockDefinitions;
  reports every DefineBlock/Ext the server streams (verifies the Indev block set).
- `perm_client.py <name> [cmd] [secs]` — negotiates BlockPermissions; reports the
  SetBlockPermission packets (verifies the survival read-only visitor behaviour).
- `spectator_client.py` / `stock_client.py` — a survival-handshake client and a
  plain classic client for join/handshake/stream checks.
- `check_mclevel.py <file.mclevel>` — parses + validates an exported .mclevel
  (blocks, Data nibble, TileEntities). `cmp_lvl.py a.lvl b.lvl` — per-cell view-id
  diff of two MCGalaxy .lvl files (round-trip parity).

## 5. Client texture asset (Indev tiles)

The Indev block/item tiles (torch, chest, furnace, crops, diamond, gears...) live
in `terrain.png` rows 6-7, patched from a genuine b1.7.3 `terrain.png`. That jar is
NOT committed (copyrighted). To rebuild the rig texpack: extract `terrain.png` from
a b1.7.3 client jar and blit its rows into the fork's default texture pack; the
generator patcher rows are documented in ClassiCube `SURVIVAL_TEST_NOTES.md`.

## 6. Rig gotchas (learned the hard way)

- **gdb rapid attaches wedge the client** (frozen frame = identical HUD numbers).
  Space attaches ~3-4s; on a freeze `kill -9` + relaunch. Never `pkill -x` narrowly.
- **Command timeouts reap the process group** — a `timeout`'d compound command can
  kill Xvfb/server too; restart them after.
- **A heredoc inside a timed-out command never gets written** (the gdb script ends
  up "No such file" — always confirm the file exists before running gdb).
- **gdb-injected block placements desync the client's local view** — verify blocks
  SERVER-side (`/Survival export` + `check_mclevel.py`, or `/SurvInv`), not by
  reading the client's `World.Blocks`.
- If the server boots ("Finished setting up") but never binds :25565, or `console.out`
  is stale, the container's process/file state is corrupted — a fresh session resets it.
