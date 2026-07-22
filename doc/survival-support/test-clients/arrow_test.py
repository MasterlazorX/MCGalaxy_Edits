#!/usr/bin/env python3
"""Phase-5 arrow test. Connect a survival (c0.30... actually gt1 is Indev) client,
capture the initial ammo, then fire SURV_FIRE_ARROW and decode the server's
ARROW_SPAWN / ARROW_STICK / ARROW_REMOVE / ARROW_AMMO round-trip.

NOTE gt1 is an Indev map -> firing consumes an inventory arrow item, not the
counted quiver. We give the client arrows via the console first (or fire anyway
to confirm the intent path). Fire straight DOWN so the arrow instantly sticks in
the ground under the player, then the player picks it back up."""
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
def pad(s): return s.encode()[:64].ljust(64)
def i16(b,o): return struct.unpack_from(">h",b,o)[0]

class Client(threading.Thread):
    def __init__(self,name):
        super().__init__(daemon=True); self.name=name
        self.sock=socket.create_connection((HOST,PORT),timeout=10)
        self.buf=b""; self.ext_done=False; self.spawn=None
        self.ev=[]; self.lock=threading.Lock(); self.running=True
        self.sock.sendall(bytes([0x00,7])+pad(name)+pad("x"*32)+bytes([0x42]))
    def send_exts(self):
        exts=[("EnvColors",1),("ChangeModel",1),("SurvivalTest",3)]
        self.sock.sendall(bytes([0x10])+pad("ArrowT")+struct.pack(">h",len(exts)))
        for n,v in exts: self.sock.sendall(bytes([0x11])+pad(n)+struct.pack(">i",v))
    def chat(self,msg): self.sock.sendall(bytes([0x0d,0xff])+pad(msg))
    def move(self,x,y,z): self.sock.sendall(bytes([0x08,0xff])+struct.pack(">hhh",int(x),int(y),int(z))+bytes([0,0]))
    def fire(self, yaw, pitch, kind=0):
        y=int((yaw%360)*100); p=int(pitch*100)&0xffff
        self.sock.sendall(bytes([0x35,CH])+bytes([0x88,(y>>8)&0xff,y&0xff,(p>>8)&0xff,p&0xff,kind]).ljust(64,b"\0"))
    def decode(self,d):
        op=d[0]
        if op==0x33:   # ARROW_SPAWN [id][type][grav][pos3][vel3]
            self._log("ARROW_SPAWN",dict(id=(d[1]<<8)|d[2],type=d[3],grav=d[4]/100,
                pos=(round(i16(d,5)/32,1),round(i16(d,7)/32,1),round(i16(d,9)/32,1)),
                vel=(round(i16(d,11)/1024,2),round(i16(d,13)/1024,2),round(i16(d,15)/1024,2))))
        elif op==0x34: # ARROW_STICK [id][pos3]
            self._log("ARROW_STICK",dict(id=(d[1]<<8)|d[2],
                pos=(round(i16(d,3)/32,1),round(i16(d,5)/32,1),round(i16(d,7)/32,1))))
        elif op==0x35: # ARROW_REMOVE [id][reason]
            self._log("ARROW_REMOVE",dict(id=(d[1]<<8)|d[2],reason=d[3]))
        elif op==0x36: # ARROW_AMMO [count]
            self._log("ARROW_AMMO",dict(count=(d[1]<<8)|d[2]))
        elif op==0x20: # INV_FULL [base][cnt] then cnt*(id2,cnt,dmg2)
            base,cnt=d[1],d[2]; items=[]
            for i in range(cnt):
                at=3+i*5; sid=(d[at]<<8)|d[at+1]; c=d[at+2]
                if c: items.append((base+i,sid,c))
            if items: self._log("INV_FULL",items)
        elif op==0x21: # INV_SLOT [slot][id2][cnt][dmg2]
            self._log("INV_SLOT",(d[1],(d[2]<<8)|d[3],d[4]))
    def _log(self,l,p):
        with self.lock: self.ev.append((l,p))
    def drain(self):
        with self.lock: e=self.ev[:]; self.ev=[]
        return e
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
    c=Client("Archer"); c.start()
    end=time.time()+10
    while time.time()<end and c.spawn is None: time.sleep(0.1)
    if not c.spawn: print("no spawn"); return
    sx,sy,sz=c.spawn; bx,bz=sx>>5, sz>>5; feet=(sy-51)>>5
    print(f"spawned block=({bx},{feet},{bz})")
    time.sleep(1.0)

    # gt1 is Indev -> firing consumes an inventory arrow item. The bash wrapper
    # gives us arrows via the server console during this wait window.
    print("waiting for console to grant arrows...")
    time.sleep(2.0)
    c.drain()

    print(">>> fire straight DOWN (pitch 89) - should stick in the ground below")
    c.move(sx, sy, sz)
    time.sleep(0.3)
    c.fire(0.0, 89.0, 0)   # kind 1 = bow (Indev); near-straight-down
    time.sleep(3.0)
    ev=c.drain()
    for e in ev: print("   ", e)

    got_spawn=any(l=="ARROW_SPAWN" for l,_ in ev)
    got_stick=any(l=="ARROW_STICK" for l,_ in ev)
    got_remove=any(l=="ARROW_REMOVE" for l,_ in ev)
    print(f"\n== ARROW_SPAWN={got_spawn}  ARROW_STICK={got_stick}  ARROW_REMOVE(pickup)={got_remove} ==")
    c.running=False; time.sleep(0.4); print("DONE")

main()
