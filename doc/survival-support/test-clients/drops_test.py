#!/usr/bin/env python3
"""Server-side integration test for phase-5 dropped items (SURV_DROP_*).

A single synthetic survival client (negotiates SurvivalTest v3) joins the
default Indev survival map, captures its own spawn position, then:
  1. breaks the ground block under its feet  -> server SpawnMined -> DROP_SPAWN
  2. holds position on that spot past the 10-tick pickup delay -> DROP_PICKUP
     + the INV_SLOT/INV_FULL echo as the yield lands in the inventory.

Decodes the 0xB0 survival frames and prints DROP_SPAWN/PICKUP/REMOVE +
inventory echoes so we can confirm the whole mine->drop->pickup round-trip.
"""
import socket, sys, time, struct, threading

HOST, PORT = "127.0.0.1", 25565
CH = 0xB0

SIZES = {0x00:130, 0x01:0, 0x02:0, 0x03:1027, 0x04:6, 0x06:7, 0x07:73, 0x08:9,
         0x09:6, 0x0a:4, 0x0b:3, 0x0c:1, 0x0d:65, 0x0e:64, 0x0f:1,
         0x10:66, 0x11:68, 0x12:2, 0x13:1, 0x14:65, 0x15:130, 0x16:3,
         0x17:80, 0x18:2, 0x19:7, 0x1a:5, 0x1b:65, 0x1c:8, 0x1d:65,
         0x1e:72, 0x1f:3, 0x20:2, 0x21:137, 0x22:2, 0x23:5, 0x24:8, 0x25:86,
         0x26:2, 0x27:4, 0x28:6, 0x29:2, 0x2b:3, 0x2c:4, 0x2e:66, 0x2f:69,
         0x32:2, 0x33:9, 0x35:65}

def pad(s): return s.encode()[:64].ljust(64)

def i16(b, o): return struct.unpack_from(">h", b, o)[0]

class Client(threading.Thread):
    def __init__(self, name):
        super().__init__(daemon=True)
        self.name = name
        self.sock = socket.create_connection((HOST, PORT), timeout=10)
        self.buf = b""
        self.ext_done = False
        self.joined = False
        self.spawn = None      # (x,y,z) fixed-point from self spawn packet
        self.frames = []
        self.lock = threading.Lock()
        self.running = True
        self.sock.sendall(bytes([0x00, 7]) + pad(name) + pad("x"*32) + bytes([0x42]))

    def send_exts(self):
        exts = [("EnvColors",1), ("ChangeModel",1), ("SurvivalTest",3)]
        self.sock.sendall(bytes([0x10]) + pad("DropTest") + struct.pack(">h", len(exts)))
        for n,v in exts:
            self.sock.sendall(bytes([0x11]) + pad(n) + struct.pack(">i", v))

    def chat(self, msg):
        self.sock.sendall(bytes([0x0d, 0xff]) + pad(msg))

    def set_block(self, x, y, z, mode, block):
        self.sock.sendall(bytes([0x05]) + struct.pack(">hhh", x, y, z) + bytes([mode, block & 0xff]))

    def move(self, x, y, z, yaw=0, pitch=0):
        # client->server position, pid 0xff (self)
        self.sock.sendall(bytes([0x08, 0xff]) + struct.pack(">hhh", x, y, z) + bytes([yaw, pitch]))

    def decode_surv(self, d):
        op = d[0]
        if op == 0x30:   # DROP_SPAWN [dropId2][item2][cnt][pos3xi16][vel3xi16][rot0]
            drop=(d[1]<<8)|d[2]; item=(d[3]<<8)|d[4]; cnt=d[5]
            px,py,pz = i16(d,6)/32.0, i16(d,8)/32.0, i16(d,10)/32.0
            vx,vy,vz = i16(d,12)/512.0, i16(d,14)/512.0, i16(d,16)/512.0
            self._log("DROP_SPAWN", dict(id=drop, item=item, cnt=cnt,
                      pos=(round(px,2),round(py,2),round(pz,2)),
                      vel=(round(vx,2),round(vy,2),round(vz,2)), rot0=d[18]))
        elif op == 0x31: # DROP_PICKUP [dropId2][pickerEntId]
            self._log("DROP_PICKUP", dict(id=(d[1]<<8)|d[2], picker=d[3]))
        elif op == 0x32: # DROP_REMOVE [dropId2][reason]
            self._log("DROP_REMOVE", dict(id=(d[1]<<8)|d[2], reason=d[3]))
        elif op == 0x20: # INV_FULL
            base,cnt=d[1],d[2]; items=[]
            for i in range(cnt):
                at=3+i*5; sid=(d[at]<<8)|d[at+1]; c=d[at+2]
                if c: items.append((base+i, sid, c))
            if items: self._log("INV_FULL", items)
        elif op == 0x21: # INV_SLOT
            self._log("INV_SLOT", (d[1], (d[2]<<8)|d[3], d[4]))

    def _log(self, label, payload):
        with self.lock: self.frames.append((label, payload))

    def drain(self):
        with self.lock:
            f = self.frames[:]; self.frames = []
        return f

    def run(self):
        self.sock.settimeout(0.3)
        while self.running:
            try:
                data = self.sock.recv(16384)
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
                elif op == 0x07:                 # SpawnPlayer
                    pid = body[0]
                    if pid == 0xff:              # self
                        x,y,z = i16(body,65), i16(body,67), i16(body,69)
                        self.spawn = (x,y,z)
                        self.joined = True
                elif op == 0x08:                 # server position (also self at 0xff)
                    if body[0] == 0xff and self.spawn is None:
                        self.spawn = (i16(body,1), i16(body,3), i16(body,5))
                elif op == 0x04:
                    self.joined = True
                elif op == 0x35 and body[0] == CH:
                    self.decode_surv(body[1:])
                elif op == 0x0d:
                    try: txt = body[1:].split(b'\0')[0].decode('cp437','replace').rstrip()
                    except Exception: txt = repr(body[1:])
                    self._log("CHAT", txt)

def main():
    c = Client("Miner"); c.start()
    end = time.time()+10
    while time.time()<end and c.spawn is None: time.sleep(0.1)
    if c.spawn is None: print("did not get spawn position; aborting"); return
    sx,sy,sz = c.spawn
    bx, bz = sx>>5, sz>>5           # feet block x/z (fixed-point /32)
    feet = (sy - 51) >> 5           # classic Y carries +51 eye offset
    print(f"spawned at fixed=({sx},{sy},{sz}) -> block=({bx},{feet},{bz})")
    time.sleep(1.0)
    c.drain()

    # break the column under the feet: the first solid block that yields a drop
    got_spawn = None
    for by in range(feet-1, feet-6, -1):
        print(f">>> break block ({bx},{by},{bz})")
        c.set_block(bx, by, bz, 0, 0)   # mode 0 = delete
        time.sleep(0.8)
        frames = c.drain()
        for f in frames: print("   ", f)
        for label,pl in frames:
            if label == "DROP_SPAWN": got_spawn = (by, pl)
        if got_spawn: break

    if not got_spawn:
        print("NO DROP_SPAWN produced - mining path did not emit a drop"); c.running=False; return
    by, drop = got_spawn
    print(f"\n== DROP_SPAWN ok: {drop} (from block y={by}) ==")

    # stand on the drop's resting spot and hold past the 10-tick pickup delay
    dpx, dpy, dpz = drop["pos"]
    px = int(round(dpx*32)); pz = int(round(dpz*32))
    py = int(round(dpy*32)) + 51   # feet at the drop's y
    print(f">>> move onto drop at feet≈{drop['pos']}, hold for pickup")
    for _ in range(12):
        c.move(px, py, pz)
        time.sleep(0.25)
    frames = c.drain()
    for f in frames: print("   ", f)

    picked = any(l=="DROP_PICKUP" for l,_ in frames)
    invecho = any(l in ("INV_SLOT","INV_FULL") for l,_ in frames)
    print(f"\n== RESULT: DROP_PICKUP={picked}  inventory-echo={invecho} ==")
    c.running=False; time.sleep(0.4); print("DONE")

main()
