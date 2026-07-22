#!/usr/bin/env python3
"""Phase-5 mob-death drop test. Connect a survival client, find a nearby mob from
the MOB_SPAWN/MOVE stream, walk up to it, and melee it to death (spacing hits past
the 20-tick invuln window). Watch for the mob's DROP_SPAWN death item + the corpse
despawn. Pig -> raw porkchop (256+63), zombie -> feather (256+32), etc."""
import socket, time, struct, threading

HOST, PORT = "127.0.0.1", 25565
CH = 0xB0
SIZES = {0x00:130, 0x01:0, 0x02:0, 0x03:1027, 0x04:6, 0x06:7, 0x07:73, 0x08:9,
         0x09:6, 0x0a:4, 0x0b:3, 0x0c:1, 0x0d:65, 0x0e:64, 0x0f:1,
         0x10:66, 0x11:68, 0x12:2, 0x13:1, 0x14:65, 0x15:130, 0x16:3,
         0x17:80, 0x18:2, 0x19:7, 0x1a:5, 0x1b:65, 0x1c:8, 0x1d:65,
         0x1e:72, 0x1f:3, 0x20:2, 0x21:137, 0x22:2, 0x23:5, 0x24:8, 0x25:86,
         0x26:2, 0x27:4, 0x28:6, 0x29:2, 0x2b:3, 0x2c:4, 0x2e:66, 0x2f:69,
         0x32:2, 0x33:9, 0x35:65}
DEATH = {0:"feather(256+32)",1:"arrow(256+6)",2:"porkchop(256+63)",3:"gunpowder(256+33)",4:"string(256+31)",5:"none"}
def pad(s): return s.encode()[:64].ljust(64)
def i16(b,o): return struct.unpack_from(">h",b,o)[0]

class Client(threading.Thread):
    def __init__(self,name):
        super().__init__(daemon=True); self.name=name
        self.sock=socket.create_connection((HOST,PORT),timeout=10)
        self.buf=b""; self.ext_done=False; self.spawn=None
        self.mobs={}          # mobId -> [type, x,y,z]
        self.drops=[]; self.deaths=set()
        self.lock=threading.Lock(); self.running=True
        self.sock.sendall(bytes([0x00,7])+pad(name)+pad("x"*32)+bytes([0x42]))
    def send_exts(self):
        exts=[("EnvColors",1),("ChangeModel",1),("SurvivalTest",3)]
        self.sock.sendall(bytes([0x10])+pad("MobDrop")+struct.pack(">h",len(exts)))
        for n,v in exts: self.sock.sendall(bytes([0x11])+pad(n)+struct.pack(">i",v))
    def move(self,x,y,z):
        self.sock.sendall(bytes([0x08,0xff])+struct.pack(">hhh",int(x),int(y),int(z))+bytes([0,0]))
    def attack(self,mobId):
        self.sock.sendall(bytes([0x35,CH])+bytes([0x80,0,(mobId>>8)&0xff,mobId&0xff]).ljust(64,b"\0"))
    def decode(self,d):
        op=d[0]
        if op==0x10:   # MOB_SPAWN
            mid=(d[1]<<8)|d[2]; t=d[3]; x,y,z=i16(d,4)/32,i16(d,6)/32,i16(d,8)/32
            with self.lock: self.mobs[mid]=[t,x,y,z]
        elif op==0x11: # MOB_MOVE
            mid=(d[1]<<8)|d[2]; x,y,z=i16(d,3)/32,i16(d,5)/32,i16(d,7)/32
            with self.lock:
                if mid in self.mobs: self.mobs[mid][1:]=[x,y,z]
        elif op==0x12: # MOB_STATE [mid][hp][flags]
            mid=(d[1]<<8)|d[2]
            if d[4]&0x10:
                with self.lock: self.deaths.add(mid)
        elif op==0x13: # MOB_DESPAWN
            mid=(d[1]<<8)|d[2]
            with self.lock: self.mobs.pop(mid,None)
        elif op==0x30: # DROP_SPAWN
            with self.lock: self.drops.append(dict(id=(d[1]<<8)|d[2],item=(d[3]<<8)|d[4],cnt=d[5],
                pos=(round(i16(d,6)/32,1),round(i16(d,8)/32,1),round(i16(d,10)/32,1))))
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
                if size is None: print(f"[{self.name}] unk 0x{op:02x}"); self.running=False; return
                if len(self.buf)<1+size: break
                body,self.buf=self.buf[1:1+size],self.buf[1+size:]
                if op==0x10 and not self.ext_done: self.ext_done=True; self.send_exts()
                elif op==0x07 and body[0]==0xff: self.spawn=(i16(body,65),i16(body,67),i16(body,69))
                elif op==0x08 and body[0]==0xff and self.spawn is None: self.spawn=(i16(body,1),i16(body,3),i16(body,5))
                elif op==0x35 and body[0]==CH: self.decode(body[1:])

def main():
    c=Client("Slayer"); c.start()
    end=time.time()+10
    while time.time()<end and c.spawn is None: time.sleep(0.1)
    if not c.spawn: print("no spawn"); return
    print("joined; waiting for mobs to populate...")
    time.sleep(4)

    # pick the closest mob to our spawn
    sx,sy,sz=c.spawn; fx,fy,fz=sx/32,(sy-51)/32,sz/32
    with c.lock: snap=dict(c.mobs)
    if not snap: print("no mobs streamed; abort"); c.running=False; return
    cand={k:v for k,v in snap.items() if v[0]==2} or snap
    target=min(cand, key=lambda m:(cand[m][1]-fx)**2+(cand[m][2]-fy)**2+(cand[m][3]-fz)**2)
    t,mx,my,mz=snap[target]
    print(f"target mob id={target} type={t} ({DEATH.get(t)}) at ({mx:.1f},{my:.1f},{mz:.1f})")
    c.drops.clear()

    # walk up and beat it (space hits ~1.1s to clear the 20-tick invuln window)
    for swing in range(16):
        with c.lock:
            if target in c.mobs: _,mx,my,mz=c.mobs[target]
            dead = target in c.deaths
        c.move(mx*32, my*32+51, mz*32)   # stand on the mob (well within reach)
        time.sleep(0.15)
        c.attack(target)
        if dead: print(f"  swing {swing}: mob dead"); break
        time.sleep(1.0)
    time.sleep(2.0)

    with c.lock: drops=list(c.drops); dead=target in c.deaths
    print(f"\nmob dead={dead}")
    print(f"drops seen after the kill: {len(drops)}")
    for d in drops: print("   ", d)
    # the death item for this mob type (0-2 rolled, so 0 drops is a valid roll)
    print(f"\n== expected death item for type {t}: {DEATH.get(t)} ==")
    c.running=False; time.sleep(0.4); print("DONE")

main()
