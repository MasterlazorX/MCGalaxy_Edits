#!/usr/bin/env python3
"""Two-player drop-contest test: both survival clients stand on the SAME spot,
one mines the block, and we confirm the SERVER awards the drop to exactly one
player (tick order) - the winner gets DROP_PICKUP picker=255 (self) + the
inventory echo, the loser sees the SAME drop id picked up by a NON-self entity
id and gets no inventory change. Also exercises the pickup-delay window by
reading how long after DROP_SPAWN the DROP_PICKUP arrives.
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
def i16(b,o): return struct.unpack_from(">h", b, o)[0]

class Client(threading.Thread):
    def __init__(self, name):
        super().__init__(daemon=True)
        self.name=name
        self.sock=socket.create_connection((HOST,PORT),timeout=10)
        self.buf=b""; self.ext_done=False; self.spawn=None
        self.frames=[]; self.lock=threading.Lock(); self.running=True
        self.sock.sendall(bytes([0x00,7])+pad(name)+pad("x"*32)+bytes([0x42]))
    def send_exts(self):
        exts=[("EnvColors",1),("ChangeModel",1),("SurvivalTest",3)]
        self.sock.sendall(bytes([0x10])+pad("DropPvp")+struct.pack(">h",len(exts)))
        for n,v in exts: self.sock.sendall(bytes([0x11])+pad(n)+struct.pack(">i",v))
    def set_block(self,x,y,z,mode,block):
        self.sock.sendall(bytes([0x05])+struct.pack(">hhh",x,y,z)+bytes([mode,block&0xff]))
    def move(self,x,y,z):
        self.sock.sendall(bytes([0x08,0xff])+struct.pack(">hhh",x,y,z)+bytes([0,0]))
    def decode(self,d):
        op=d[0]; t=time.time()
        if op==0x30:
            self._log("DROP_SPAWN",dict(id=(d[1]<<8)|d[2],item=(d[3]<<8)|d[4],
                pos=(round(i16(d,6)/32,1),round(i16(d,8)/32,1),round(i16(d,10)/32,1))),t)
        elif op==0x31: self._log("DROP_PICKUP",dict(id=(d[1]<<8)|d[2],picker=d[3]),t)
        elif op==0x32: self._log("DROP_REMOVE",dict(id=(d[1]<<8)|d[2],reason=d[3]),t)
        elif op==0x20:
            base,cnt=d[1],d[2]; items=[(base+i,(d[3+i*5]<<8)|d[4+i*5],d[5+i*5]) for i in range(cnt) if d[5+i*5]]
            if items: self._log("INV_FULL",items,t)
        elif op==0x21: self._log("INV_SLOT",(d[1],(d[2]<<8)|d[3],d[4]),t)
    def _log(self,l,p,t=None):
        with self.lock: self.frames.append((l,p,t or time.time()))
    def drain(self):
        with self.lock: f=self.frames[:]; self.frames=[]
        return f
    def run(self):
        self.sock.settimeout(0.3)
        while self.running:
            try:
                data=self.sock.recv(16384)
                if not data: break
                self.buf+=data
            except socket.timeout: pass
            except OSError: break
            while self.buf:
                op=self.buf[0]; size=SIZES.get(op)
                if size is None:
                    print(f"[{self.name}] unk 0x{op:02x} head={self.buf[:16].hex()}")
                    self.running=False; return
                if len(self.buf)<1+size: break
                body,self.buf=self.buf[1:1+size],self.buf[1+size:]
                if op==0x10 and not self.ext_done: self.ext_done=True; self.send_exts()
                elif op==0x07 and body[0]==0xff:
                    self.spawn=(i16(body,65),i16(body,67),i16(body,69))
                elif op==0x08 and body[0]==0xff and self.spawn is None:
                    self.spawn=(i16(body,1),i16(body,3),i16(body,5))
                elif op==0x35 and body[0]==CH: self.decode(body[1:])

def main():
    a=Client("Alice"); a.start()
    b=Client("Bob");   b.start()
    end=time.time()+10
    while time.time()<end and (a.spawn is None or b.spawn is None): time.sleep(0.1)
    if a.spawn is None or b.spawn is None: print("no spawn; abort"); return
    sx,sy,sz=a.spawn
    bx,bz=(sx>>5)+3, sz>>5; feet=(sy-51)>>5   # +3 to dodge any earlier-broken column
    px,pz=bx*32+16, bz*32+16          # both stand at the block centre
    print(f"both near block=({bx},{feet},{bz})")
    time.sleep(1.0)

    # park both players on the spot, then scan down for the first block that
    # actually yields a drop (surface may be a couple blocks below the feet)
    drop=None
    for by in range(feet-1, feet-6, -1):
        for _ in range(3):
            a.move(px,(by<<5)+51,pz); b.move(px,(by<<5)+51,pz); time.sleep(0.15)
        a.drain(); b.drain()
        print(f">>> Alice breaks block ({bx},{by},{bz}) - both standing on it")
        a.set_block(bx,by,bz,0,0)
        for _ in range(10):
            a.move(px,(by<<5)+51,pz); b.move(px,(by<<5)+51,pz); time.sleep(0.2)
        fa=a.frames[:];
        if any(l=="DROP_SPAWN" for l,_,_ in fa): break

    fa=a.drain(); fb=b.drain()
    print("--- Alice frames ---")
    for f in fa: print("   ",f[0],f[1])
    print("--- Bob frames ---")
    for f in fb: print("   ",f[0],f[1])

    # analyse
    spawn_t = next((t for l,p,t in fa if l=="DROP_SPAWN"), None)
    a_pick = [ (p,t) for l,p,t in fa if l=="DROP_PICKUP" ]
    b_pick = [ (p,t) for l,p,t in fb if l=="DROP_PICKUP" ]
    a_inv  = [ p for l,p,t in fa if l in ("INV_FULL","INV_SLOT") ]
    b_inv  = [ p for l,p,t in fb if l in ("INV_FULL","INV_SLOT") ]

    print("\n== ANALYSIS ==")
    if a_pick and spawn_t:
        print(f"pickup delay: {round((a_pick[0][1]-spawn_t)*1000)} ms after spawn (expect >=~500ms / 10 ticks)")
    winner = None
    if a_pick and a_pick[0][0].get("picker")==255: winner="Alice"
    if b_pick and b_pick[0][0].get("picker")==255: winner="Bob"
    print(f"winner (picker=255 self): {winner}")
    print(f"Alice sees picker id: {a_pick[0][0]['picker'] if a_pick else None}   got inventory: {bool(a_inv)} {a_inv}")
    print(f"Bob   sees picker id: {b_pick[0][0]['picker'] if b_pick else None}   got inventory: {bool(b_inv)} {b_inv}")
    only_one = (bool(a_inv) ^ bool(b_inv))
    print(f"exactly ONE player got the item: {only_one}")
    a.running=b.running=False; time.sleep(0.3); print("DONE")

main()
