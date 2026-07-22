#!/usr/bin/env python3
"""Live test for server-authoritative Indev growth (SurvivalGrowth.cs).

Joins the default Indev survival map (SurvivalCreative temporarily on so a
synthetic client may place freely), decodes the streamed level, finds a patch
of grass-topped columns near the middle, and plants a dense field of saplings.
Then it watches the ordinary SetBlock (0x06) stream for the server's growth:
  * sapling -> tree      : a burst of Log(17)/Leaves(18) where a sapling grew
  * grass spread/decay   : Dirt(3) <-> Grass(2) transitions

All the signals are classic block ids so no BlockDefinitions negotiation is
needed. Prints a summary of what growth the server streamed back.
"""
import socket, sys, time, struct, threading, gzip, io

HOST, PORT = "127.0.0.1", 25565
NAME = sys.argv[1] if len(sys.argv) > 1 else "Grower"
WATCH = int(sys.argv[2]) if len(sys.argv) > 2 else 120

SIZES = {0x00:130, 0x01:0, 0x02:0, 0x03:1027, 0x04:6, 0x06:7, 0x07:73, 0x08:9,
         0x09:6, 0x0a:4, 0x0b:3, 0x0c:1, 0x0d:65, 0x0e:64, 0x0f:1,
         0x10:66, 0x11:68, 0x12:2, 0x13:1, 0x14:65, 0x15:130, 0x16:3,
         0x17:80, 0x18:2, 0x19:7, 0x1a:5, 0x1b:65, 0x1c:8, 0x1d:65,
         0x1e:72, 0x1f:3, 0x20:2, 0x21:137, 0x22:2, 0x23:5, 0x24:8, 0x25:86,
         0x26:2, 0x27:4, 0x28:6, 0x29:2, 0x2b:3, 0x2c:4, 0x2e:66, 0x2f:69,
         0x32:2, 0x33:9, 0x35:65}

AIR, GRASS, DIRT, SAPLING, LOG, LEAVES = 0, 2, 3, 6, 17, 18

def pad(s): return s.encode()[:64].ljust(64)
def i16(b, o): return struct.unpack_from(">h", b, o)[0]

class Client(threading.Thread):
    def __init__(self, name):
        super().__init__(daemon=True)
        self.name = name
        self.sock = socket.create_connection((HOST, PORT), timeout=10)
        self.buf = b""
        self.ext_done = False
        self.spawn = None
        self.lvl_raw = b""
        self.W = self.H = self.L = 0
        self.blocks = None
        self.finalized = False
        self.setblocks = []   # (t, x, y, z, blk)
        self.lock = threading.Lock()
        self.running = True
        self.sock.sendall(bytes([0x00, 7]) + pad(name) + pad("x"*32) + bytes([0x42]))

    def send_exts(self):
        exts = [("EnvColors",1), ("ChangeModel",1), ("SurvivalTest",3)]
        self.sock.sendall(bytes([0x10]) + pad("GrowthTest") + struct.pack(">h", len(exts)))
        for n,v in exts:
            self.sock.sendall(bytes([0x11]) + pad(n) + struct.pack(">i", v))

    def chat(self, msg):
        self.sock.sendall(bytes([0x0d, 0xff]) + pad(msg))

    def place(self, x, y, z, block):
        self.sock.sendall(bytes([0x05]) + struct.pack(">hhh", x, y, z) + bytes([1, block & 0xff]))

    def blk(self, x, y, z):
        if x<0 or y<0 or z<0 or x>=self.W or y>=self.H or z>=self.L: return -1
        return self.blocks[(y*self.L + z)*self.W + x]

    def decode_level(self):
        raw = gzip.GzipFile(fileobj=io.BytesIO(self.lvl_raw)).read()
        n = struct.unpack(">i", raw[:4])[0]
        self.blocks = bytearray(raw[4:4+n])

    def run(self):
        self.sock.settimeout(0.3)
        while self.running:
            try:
                data = self.sock.recv(65536)
                if not data: break
                self.buf += data
            except socket.timeout: pass
            except OSError: break
            while self.buf:
                op = self.buf[0]
                size = SIZES.get(op)
                if size is None:
                    print(f"[{self.name}] !! unknown opcode 0x{op:02x}"); self.running=False; return
                if len(self.buf) < 1+size: break
                body, self.buf = self.buf[1:1+size], self.buf[1+size:]
                if op == 0x10 and not self.ext_done:
                    self.ext_done = True; self.send_exts()
                elif op == 0x03:
                    ln = struct.unpack(">h", body[0:2])[0]
                    self.lvl_raw += body[2:2+ln]
                elif op == 0x04:
                    self.W, self.H, self.L = i16(body,0), i16(body,2), i16(body,4)
                    try: self.decode_level(); self.finalized = True
                    except Exception as e: print("decode err", e)
                elif op == 0x07 and body[0] == 0xff:
                    self.spawn = (i16(body,65), i16(body,67), i16(body,69))
                elif op == 0x08 and body[0] == 0xff and self.spawn is None:
                    self.spawn = (i16(body,1), i16(body,3), i16(body,5))
                elif op == 0x06:
                    x,y,z,blk = struct.unpack(">hhhB", body)
                    with self.lock: self.setblocks.append((time.time(), x,y,z,blk))

    def drain(self):
        with self.lock:
            f = self.setblocks[:]; self.setblocks = []
        return f

c = Client(NAME)
c.start()

# wait for level + spawn
t0 = time.time()
while time.time()-t0 < 15 and not (c.finalized and c.spawn):
    time.sleep(0.2)
if not c.finalized:
    print("FAIL: level not received"); sys.exit(1)
print(f"level {c.W}x{c.H}x{c.L}  spawn={c.spawn}")

# find grass-topped columns near the map centre and plant saplings on them
cx, cz = c.W//2, c.L//2
planted = []
R = 9
for dx in range(-R, R+1):
    for dz in range(-R, R+1):
        x, z = cx+dx, cz+dz
        if x<1 or z<1 or x>=c.W-1 or z>=c.L-1: continue
        # topmost solid: scan down for first grass with air above
        for y in range(c.H-2, 1, -1):
            if c.blk(x,y,z) == GRASS and c.blk(x,y+1,z) == AIR:
                planted.append((x, y+1, z)); break
print(f"grass columns found near centre: {len(planted)}")
if not planted:
    print("FAIL: no grass surface near centre to plant on"); c.running=False; sys.exit(1)

# teleport near the patch so we are an active viewer, then plant
sx = (cx)<<5; sy = (min(p[1] for p in planted)+3)<<5; sz = (cz)<<5
c.sock.sendall(bytes([0x08, 0xff]) + struct.pack(">hhh", sx, sy, sz) + bytes([0,0]))
time.sleep(0.3)
c.drain()  # clear placement-unrelated traffic

for (x,y,z) in planted:
    c.place(x,y,z,SAPLING)
    time.sleep(0.01)
time.sleep(1.0)

# how many saplings actually stuck (server echoed a place of block 6)?
echoed = c.drain()
stuck = set()
for (t,x,y,z,b) in echoed:
    if b == SAPLING: stuck.add((x,y,z))
print(f"saplings the server accepted: {len(stuck)} / {len(planted)}")

print(f"watching growth for {WATCH}s ...")
trees = 0
grass_spread = 0
grass_decay = 0
stage = 0
end = time.time() + WATCH
sap_cells = set(planted)
last = time.time()
while time.time() < end and c.running:
    time.sleep(2.0)
    for (t,x,y,z,b) in c.drain():
        if b == LOG or b == LEAVES:
            trees += 1
        elif b == GRASS:
            grass_spread += 1
        elif b == DIRT:
            grass_decay += 1
    el = int(time.time()-t0)
    print(f"  [{el:3d}s] tree-blocks(log/leaf)={trees}  dirt->grass~={grass_spread}  grass->dirt~={grass_decay}")

print("\n=== RESULT ===")
print(f"tree growth blocks (Log/Leaves streamed): {trees}")
print(f"grass spread (Grass set):                 {grass_spread}")
print(f"grass decay  (Dirt set):                  {grass_decay}")
ok = trees > 0
print("SAPLING->TREE growth:", "OBSERVED" if trees>0 else "not seen")
print("GRASS activity:", "OBSERVED" if (grass_spread+grass_decay)>0 else "not seen")
c.running = False
sys.exit(0 if ok else 2)
