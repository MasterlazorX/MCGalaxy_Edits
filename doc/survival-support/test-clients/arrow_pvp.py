#!/usr/bin/env python3
"""Arrow entity-hit test: Archer fires a level shot at Victim standing a few
blocks in front (-z). Confirm the server's HitEntity -> DamagePlayer path fires:
Victim's SURV_HEALTH drops and the arrow is REMOVEd with reason 1 (hit)."""
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
        self.ev=[]; self.hp=None; self.lock=threading.Lock(); self.running=True
        self.sock.sendall(bytes([0x00,7])+pad(name)+pad("x"*32)+bytes([0x42]))
    def send_exts(self):
        exts=[("EnvColors",1),("ChangeModel",1),("SurvivalTest",3)]
        self.sock.sendall(bytes([0x10])+pad("ArrowP")+struct.pack(">h",len(exts)))
        for n,v in exts: self.sock.sendall(bytes([0x11])+pad(n)+struct.pack(">i",v))
    def move(self,x,y,z): self.sock.sendall(bytes([0x08,0xff])+struct.pack(">hhh",int(x),int(y),int(z))+bytes([0,0]))
    def fire(self, yaw, pitch, kind=0):
        y=int((yaw%360)*100); p=int(pitch*100)&0xffff
        self.sock.sendall(bytes([0x35,CH])+bytes([0x88,(y>>8)&0xff,y&0xff,(p>>8)&0xff,p&0xff,kind]).ljust(64,b"\0"))
    def decode(self,d):
        op=d[0]
        if op==0x33: self._log("ARROW_SPAWN",dict(id=(d[1]<<8)|d[2],pos=(round(i16(d,5)/32,1),round(i16(d,7)/32,1),round(i16(d,9)/32,1))))
        elif op==0x34: self._log("ARROW_STICK",dict(id=(d[1]<<8)|d[2]))
        elif op==0x35: self._log("ARROW_REMOVE",dict(id=(d[1]<<8)|d[2],reason=d[3]))
        elif op==0x36: self._log("ARROW_AMMO",dict(count=(d[1]<<8)|d[2]))
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
                elif op==0x03:  # SURV_HEALTH inside... no, 0x03 is a classic op here
                    pass
                elif op==0x35 and body[0]==CH: pass
    def _log(self,l,p):
        with self.lock: self.ev.append((l,p))
    def drain(self):
        with self.lock: e=self.ev[:]; self.ev=[]
        return e

# SURV_HEALTH arrives as a 0xB0 plugin frame (op 0x03), decode it in decode()
def patch_health(cli):
    orig=cli.decode
    def dec(d):
        if d[0]==0x03:
            with cli.lock: cli.hp=d[1]
        else: orig(d)
    cli.decode=dec

def main():
    a=Client("Archer"); patch_health(a); a.start()
    v=Client("Victim"); patch_health(v); v.start()
    end=time.time()+10
    while time.time()<end and (a.spawn is None or v.spawn is None): time.sleep(0.1)
    if a.spawn is None or v.spawn is None: print("no spawn"); return
    sx,sy,sz=a.spawn
    # lift both well above the spawn structure into open air (synthetic clients
    # hold whatever position we send), so a level shot isn't blocked by walls
    HY = 55*32 + 51
    ax,ay,az=sx,HY,sz
    vx,vy,vz=sx,HY,sz-3*32
    for _ in range(6):
        a.move(ax,ay,az); v.move(vx,vy,vz); time.sleep(0.2)
    a.drain(); v.drain()
    with v.lock: hp0=v.hp
    print(f"Archer at z={az/32:.1f}, Victim at z={vz/32:.1f}, Victim HP before={hp0}")

    print(">>> Archer fires level (yaw 0 = -z) at Victim")
    for shot in range(3):
        a.move(ax,ay,az); v.move(vx,vy,vz)
        time.sleep(0.2)
        a.fire(0.0, 0.0, 0)   # level shot toward -z
        time.sleep(1.5)
    time.sleep(1.0)
    with v.lock: hp1=v.hp
    print(f"Victim HP after={hp1}")
    print("Archer arrow events:")
    for e in a.drain(): print("   ",e)
    hit = (hp0 is not None and hp1 is not None and hp1 < hp0)
    print(f"\n== VICTIM DAMAGED BY ARROW: {hit} (HP {hp0} -> {hp1}) ==")
    a.running=v.running=False; time.sleep(0.4); print("DONE")
main()
