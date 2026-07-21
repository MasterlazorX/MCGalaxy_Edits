#!/usr/bin/env python3
# Round-trip comparison: parses two MCGalaxy .lvl files (header + raw blocks +
# custom-block chunk section) and diffs the effective per-cell view id.
import gzip, struct, sys

CUSTOM = 163

def load(path):
    with gzip.open(path, "rb") as f:
        hdr = f.read(18)
        ver, W, L, H, sx, sz, sy = struct.unpack("<7H", hdr[:14])
        assert ver == 1874, ver
        vol = W * L * H
        blocks = bytearray(f.read(vol))
        marker = f.read(1)
        chunks = {}
        if marker == b"\xbd":
            cx, cy, cz = (W + 15) // 16, (H + 15) // 16, (L + 15) // 16
            idx = 0
            for y in range(cy):
                for z in range(cz):
                    for x in range(cx):
                        has = f.read(1)[0]
                        if has: chunks[(x, y, z)] = f.read(4096)
                        idx += 1
    return (W, L, H, sx, sy, sz, blocks, chunks)

def view(W, L, H, blocks, chunks, i):
    b = blocks[i]
    if b != CUSTOM: return b
    x = i % W; z = (i // W) % L; y = i // (W * L)
    ch = chunks.get((x >> 4, y >> 4, z >> 4))
    if ch is None: return 0
    return ch[((y & 15) << 8) | ((z & 15) << 4) | (x & 15)]

a = load(sys.argv[1]); b = load(sys.argv[2])
assert a[:3] == b[:3], "dims differ: %s vs %s" % (a[:3], b[:3])
print("dims: %sx%sx%s  spawnA=%s spawnB=%s" % (a[0], a[2], a[1], a[3:6], b[3:6]))

W, L, H = a[0], a[1], a[2]
diff = 0
examples = []
for i in range(W * L * H):
    va = view(W, L, H, a[6], a[7], i)
    vb = view(W, L, H, b[6], b[7], i)
    if va != vb:
        diff += 1
        if len(examples) < 12:
            x = i % W; z = (i // W) % L; y = i // (W * L)
            examples.append((x, y, z, va, vb))
print("differing cells:", diff)
for e in examples: print("   (%d,%d,%d): %d -> %d" % e)
print("IDENTICAL" if diff == 0 else "DIFFERS")
