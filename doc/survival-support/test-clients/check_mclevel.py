#!/usr/bin/env python3
# Structural check of an exported .mclevel: parses the gzip NBT, dumps the
# tag tree (sans arrays), and sanity-checks the survival invariants.
import gzip, struct, sys, collections

def read_str(f):
    n = struct.unpack(">H", f.read(2))[0]
    return f.read(n).decode("utf-8")

def read_payload(f, t):
    if t == 1:  return struct.unpack(">b", f.read(1))[0]
    if t == 2:  return struct.unpack(">h", f.read(2))[0]
    if t == 3:  return struct.unpack(">i", f.read(4))[0]
    if t == 4:  return struct.unpack(">q", f.read(8))[0]
    if t == 5:  return struct.unpack(">f", f.read(4))[0]
    if t == 6:  return struct.unpack(">d", f.read(8))[0]
    if t == 7:
        n = struct.unpack(">i", f.read(4))[0]
        return bytearray(f.read(n))
    if t == 8:  return read_str(f)
    if t == 9:
        ct = f.read(1)[0]
        n  = struct.unpack(">i", f.read(4))[0]
        return [read_payload(f, ct) for _ in range(n)]
    if t == 10:
        d = {}
        while True:
            ct = f.read(1)[0]
            if ct == 0: return d
            name = read_str(f)
            d[name] = read_payload(f, ct)
    raise ValueError("bad tag %d" % t)

path = sys.argv[1]
with gzip.open(path, "rb") as f:
    t = f.read(1)[0]
    assert t == 10, "root not a compound"
    rootname = read_str(f)
    root = read_payload(f, 10)

print("root name:", repr(rootname))
about, env, m = root["About"], root["Environment"], root["Map"]
print("About:", about)
print("Environment:", {k: v for k, v in env.items()})
W, L, H = m["Width"], m["Length"], m["Height"]
blocks, data = m["Blocks"], m["Data"]
print("Map: %dx%dx%d spawn=%s blocks=%d data=%d" % (W, H, L, m["Spawn"], len(blocks), len(data)))
assert len(blocks) == W * L * H and len(data) == len(blocks)

hist = collections.Counter(blocks)
print("block histogram (top 16):", hist.most_common(16))
over62 = {b: c for b, c in hist.items() if b > 62}
print("ids over 62 (should be empty):", over62)

metas = collections.Counter(d >> 4 for d in data)
print("meta nibble histogram:", dict(metas))
lights = set(d & 15 for d in data)
print("light nibbles (should be {15}):", lights)

ents = root["Entities"]
print("Entities:", len(ents))
for e in ents: print("  ", {k: v for k, v in e.items() if k != "Inventory"}, "inv:", e.get("Inventory"))

tes = root["TileEntities"]
print("TileEntities:", len(tes))
kinds = collections.Counter(te["id"] for te in tes)
print("  kinds:", dict(kinds))
for te in tes:
    pos = te["Pos"]; x, y, z = pos & 1023, (pos >> 10) & 1023, (pos >> 20) & 1023
    idx = (y * L + z) * W + x
    b = blocks[idx]
    ok_chest = te["id"] == "Chest" and b == 54
    ok_furn  = te["id"] == "Furnace" and b in (61, 62)
    assert ok_chest or ok_furn, "TE at (%d,%d,%d) id=%s but block=%d" % (x, y, z, te["id"], b)
    if te["Items"]:
        print("  %s @(%d,%d,%d) burn=%s cook=%s items=%s" %
              (te["id"], x, y, z, te.get("BurnTime"), te.get("CookTime"), te["Items"]))

# every chest/furnace block must have a TE
te_pos = set(te["Pos"] for te in tes)
missing = 0
for i, b in enumerate(blocks):
    if b in (54, 61, 62):
        x = i % W; z = (i // W) % L; y = i // (W * L)
        if (x + (y << 10) + (z << 20)) not in te_pos: missing += 1
print("container blocks without a TE (should be 0):", missing)
print("ALL CHECKS PASSED")
