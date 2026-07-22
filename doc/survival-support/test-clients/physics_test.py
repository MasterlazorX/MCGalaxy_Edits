#!/usr/bin/env python3
"""Live test for server-side Indev block physics (SurvivalPhysics.cs):
finite fluids, leaf decay, and fire spread.

Joins gt1 (SurvivalCreative temporarily on), decodes the level, finds a grass
column, and near it:
  * FLUID : drops a water block high in the air -> it should fall + pool + settle
            (classic ids 8 flowing / 9 still stream back as it flows)
  * LEAF  : places floating leaves (no log below/near) -> they should decay to
            air (block 18 cell -> 0), sometimes dropping a sapling
  * FIRE  : builds a small wood platform + fire on top -> the wood should burn
            away (block 5 -> 0/fire)
Watches the SetBlock (0x06) stream and reports the transitions.
"""
import socket, sys, time, struct, threading, gzip, io

HOST, PORT = "127.0.0.1", 25565
NAME = sys.argv[1] if len(sys.argv) > 1 else "Physicist"
WATCH = int(sys.argv[2]) if len(sys.argv) > 2 else 50

SIZES = {0x00:130, 0x01:0, 0x02:0, 0x03:1027, 0x04:6, 0x06:7, 0x07:73, 0x08:9,
         0x09:6, 0x0a:4, 0x0b:3, 0x0c:1, 0x0d:65, 0x0e:64, 0x0f:1,
         0x10:66, 0x11:68, 0x12:2, 0x13:1, 0x14:65, 0x15:130, 0x16:3,
         0x17:80, 0x18:2, 0x19:7, 0x1a:5, 0x1b:65, 0x1c:8, 0x1d:65,
         0x1e:72, 0x1f:3, 0x20:2, 0x21:137, 0x22:2, 0x23:5, 0x24:8, 0x25:86,
         0x26:2, 0x27:4, 0x28:6, 0x29:2, 0x2b:3, 0x2c:4, 0x2e:66, 0x2f:69,
         0x32:2, 0x33:9, 0x35:65}

AIR, STONE, WOOD, GRASS, DIRT, SAPLING, WATER, STILLW, LOG, LEAVES = 0,1,5,2,3,6,8,9,17,18
FIRE_VIEW, WATER_SRC = 51, 52

def pad(s): return s.encode()[:64].ljust(64)
def i16(b, o): return struct.unpack_from(">h", b, o)[0]

class Client(threading.Thread):
    def __init__(self, name):
        super().__init__(daemon=True)
        self.sock = socket.create_connection((HOST, PORT), timeout=10)
        self.buf=b""; self.ext_done=False; self.spawn=None
        self.lvl_raw=b""; self.W=self.H=self.L=0; self.blocks=None; self.finalized=False
        self.setblocks=[]; self.lock=threading.Lock(); self.running=True
        self.sock.sendall(bytes([0x00,7])+pad(name)+pad("x"*32)+bytes([0x42]))
    def send_exts(self):
        exts=[("EnvColors",1),("ChangeModel",1),("SurvivalTest",3)]
        self.sock.sendall(bytes([0x10])+pad("PhysTest")+struct.pack(">h",len(exts)))
        for n,v in exts: self.sock.sendall(bytes([0x11])+pad(n)+struct.pack(">i",v))
    def place(self,x,y,z,block): self.sock.sendall(bytes([0x05])+struct.pack(">hhh",x,y,z)+bytes([1,block&0xff]))
    def blk(self,x,y,z):
        if x<0 or y<0 or z<0 or x>=self.W or y>=self.H or z>=self.L: return -1
        return self.blocks[(y*self.L+z)*self.W+x]
    def run(self):
        self.sock.settimeout(0.3)
        while self.running:
            try:
                d=self.sock.recv(65536)
                if not d: break
                self.buf+=d
            except socket.timeout: pass
            except OSError: break
            while self.buf:
                op=self.buf[0]; sz=SIZES.get(op)
                if sz is None: print("unk",hex(op)); self.running=False; return
                if len(self.buf)<1+sz: break
                body,self.buf=self.buf[1:1+sz],self.buf[1+sz:]
                if op==0x10 and not self.ext_done: self.ext_done=True; self.send_exts()
                elif op==0x03:
                    ln=struct.unpack(">h",body[0:2])[0]; self.lvl_raw+=body[2:2+ln]
                elif op==0x04:
                    self.W,self.H,self.L=i16(body,0),i16(body,2),i16(body,4)
                    raw=gzip.GzipFile(fileobj=io.BytesIO(self.lvl_raw)).read()
                    n=struct.unpack(">i",raw[:4])[0]; self.blocks=bytearray(raw[4:4+n]); self.finalized=True
                elif op==0x07 and body[0]==0xff: self.spawn=(i16(body,65),i16(body,67),i16(body,69))
                elif op==0x08 and body[0]==0xff and self.spawn is None: self.spawn=(i16(body,1),i16(body,3),i16(body,5))
                elif op==0x06:
                    x,y,z,b=struct.unpack(">hhhB",body)
                    with self.lock: self.setblocks.append((time.time(),x,y,z,b))
    def drain(self):
        with self.lock: f=self.setblocks[:]; self.setblocks=[]
        return f

c=Client(NAME); c.start()
t0=time.time()
while time.time()-t0<15 and not(c.finalized and c.spawn): time.sleep(0.2)
if not c.finalized: print("FAIL: no level"); sys.exit(1)
print(f"level {c.W}x{c.H}x{c.L} spawn={c.spawn}")

# grass column near centre with a TALL clear air column above (avoid trees from
# earlier growth runs) and clear air in a small neighbourhood for the test rigs
cx,cz=c.W//2,c.L//2; gx=gy=gz=None
def clear_air(x, y0, y1, z):
    return all(c.blk(x,yy,z)==AIR for yy in range(y0,y1+1))
for r in range(0,40):
    done=False
    for dx in range(-r,r+1):
        for dz in range(-r,r+1):
            x,z=cx+dx,cz+dz
            if x<8 or z<8 or x>=c.W-8 or z>=c.L-8: continue
            for y in range(c.H-16,2,-1):
                if c.blk(x,y,z)!=GRASS: continue
                # 14 cells of clear air straight up, plus the +/-6 x lanes clear
                if clear_air(x,y+1,y+14,z) and clear_air(x-6,y+1,y+8,z) and clear_air(x+6,y+1,y+8,z):
                    gx,gy,gz=x,y,z; done=True; break
            if done: break
        if done: break
    if done: break
if gx is None: print("FAIL: no clear grass column"); sys.exit(1)
print(f"test anchor grass at {(gx,gy,gz)}  (clear air column verified)")

# become an active viewer above the anchor
c.sock.sendall(bytes([0x08,0xff])+struct.pack(">hhh",gx<<5,(gy+4)<<5,gz<<5)+bytes([0,0]))
time.sleep(0.3); c.drain()

# --- FLUID: a water block high above the anchor, should fall+pool ---
wx,wy,wz = gx, gy+6, gz
c.place(wx,wy,wz,WATER)
# --- FLUID(source): a water source two cells up as well ---
c.place(gx+1, gy+6, gz+1, WATER_SRC)

# --- LEAF: floating leaves (no log) a few cells over ---
leaf_cells=[(gx+4,gy+5,gz),(gx+4,gy+5,gz+1),(gx+5,gy+5,gz)]
for (x,y,z) in leaf_cells: c.place(x,y,z,LEAVES)

# --- FIRE: a wood platform with fire on top, a few cells the other way ---
wood_cells=[(gx-4,gy+1,gz),(gx-5,gy+1,gz),(gx-6,gy+1,gz),(gx-4,gy+1,gz+1),(gx-5,gy+1,gz+1)]
for (x,y,z) in wood_cells: c.place(x,y,z,WOOD)
time.sleep(0.2)
c.place(gx-5,gy+2,gz,FIRE_VIEW)
c.place(gx-4,gy+2,gz,FIRE_VIEW)
time.sleep(1.0)
c.drain()  # discard the placement echoes

print(f"watching physics for {WATCH}s ...")
water_flow=set(); water_still=set(); leaf_gone=0; wood_burned=0; fire_seen=0
end=time.time()+WATCH
leafset=set(leaf_cells); woodset=set(wood_cells)
while time.time()<end and c.running:
    time.sleep(2.0)
    for (t,x,y,z,b) in c.drain():
        cell=(x,y,z)
        if b==WATER:  water_flow.add(cell)
        elif b==STILLW: water_still.add(cell)
        elif b==AIR and cell in leafset: leaf_gone+=1
        elif b==AIR and cell in woodset: wood_burned+=1
        elif b==FIRE_VIEW or b==c.blk if False else False: pass
    el=int(time.time()-t0)
    print(f"  [{el:3d}s] water flow cells={len(water_flow)} still={len(water_still)} "
          f"leaves_decayed={leaf_gone}/3 wood_burned={wood_burned}/5")

print("\n=== RESULT ===")
print(f"FLUID  water flowed to {len(water_flow)} cells, settled(still) at {len(water_still)}")
print(f"LEAF   floating leaves decayed to air: {leaf_gone}/3")
print(f"FIRE   wood blocks burned to air: {wood_burned}/5")
fluid_ok = len(water_flow)>1 or len(water_still)>1
print("FLUID physics:", "OBSERVED" if fluid_ok else "not seen")
print("LEAF decay:",   "OBSERVED" if leaf_gone>0 else "not seen")
print("FIRE burn:",    "OBSERVED" if wood_burned>0 else "not seen")
c.running=False
sys.exit(0)
