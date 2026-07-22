#!/usr/bin/env python3
"""SURV_PLAYER_EQUIP wire test. Two survival clients A + B on gt1. On join each
should receive the other's equip (held 0 = empty hand). Then A mines a block and
picks it up (held becomes dirt=3) -> B should receive an updated PLAYER_EQUIP for
A with heldId=3. Decodes the 0x50 frames."""
import socket, time, struct, threading
HOST,PORT=("127.0.0.1",25565); CH=0xB0
SIZES={0x00:130,0x01:0,0x02:0,0x03:1027,0x04:6,0x06:7,0x07:73,0x08:9,0x09:6,0x0a:4,0x0b:3,0x0c:1,
 0x0d:65,0x0e:64,0x0f:1,0x10:66,0x11:68,0x12:2,0x13:1,0x14:65,0x15:130,0x16:3,0x17:80,0x18:2,0x19:7,
 0x1a:5,0x1b:65,0x1c:8,0x1d:65,0x1e:72,0x1f:3,0x20:2,0x21:137,0x22:2,0x23:5,0x24:8,0x25:86,0x26:2,
 0x27:4,0x28:6,0x29:2,0x2b:3,0x2c:4,0x2e:66,0x2f:69,0x32:2,0x33:9,0x35:65}
def pad(s): return s.encode()[:64].ljust(64)
def i16(b,o): return struct.unpack_from(">h",b,o)[0]
class C(threading.Thread):
    def __init__(s,name):
        super().__init__(daemon=True); s.name=name
        s.sock=socket.create_connection((HOST,PORT),timeout=10)
        s.buf=b""; s.ext=False; s.spawn=None; s.ev=[]; s.lock=threading.Lock(); s.run_=True
        s.sock.sendall(bytes([0x00,7])+pad(name)+pad("x"*32)+bytes([0x42]))
    def exts(s):
        s.sock.sendall(bytes([0x10])+pad("k")+struct.pack(">h",3))
        for n,v in [("EnvColors",1),("ChangeModel",1),("SurvivalTest",3)]: s.sock.sendall(bytes([0x11])+pad(n)+struct.pack(">i",v))
    def move(s,x,y,z): s.sock.sendall(bytes([0x08,0xff])+struct.pack(">hhh",int(x),int(y),int(z))+bytes([0,0]))
    def set_block(s,x,y,z,mode,blk): s.sock.sendall(bytes([0x05])+struct.pack(">hhh",x,y,z)+bytes([mode,blk&0xff]))
    def dec(s,d):
        op=d[0]
        if op==0x50:  # PLAYER_EQUIP [eid][held2][armor4x2]
            eid=d[1]; held=(d[2]<<8)|d[3]
            armor=[(d[4+i*2]<<8)|d[5+i*2] for i in range(4)]
            s._log("EQUIP",dict(eid=eid,held=held,armor=armor))
        elif op==0x30:  # DROP_SPAWN
            s._log("DROP_SPAWN",dict(id=(d[1]<<8)|d[2],item=(d[3]<<8)|d[4],
                pos=(round(i16(d,6)/32,1),round(i16(d,8)/32,1),round(i16(d,10)/32,1))))
    def _log(s,l,p):
        with s.lock: s.ev.append((l,p))
    def drain(s):
        with s.lock: e=s.ev[:]; s.ev=[]
        return e
    def run(s):
        s.sock.settimeout(0.3)
        while s.run_:
            try:
                data=s.sock.recv(16384)
                if not data: break
                s.buf+=data
            except socket.timeout: pass
            except OSError: break
            while s.buf:
                op=s.buf[0]; sz=SIZES.get(op)
                if sz is None: print(f"[{s.name}] unk {hex(op)}"); s.run_=False; return
                if len(s.buf)<1+sz: break
                body,s.buf=s.buf[1:1+sz],s.buf[1+sz:]
                if op==0x10 and not s.ext: s.ext=True; s.exts()
                elif op==0x07 and body[0]==0xff: s.spawn=(i16(body,65),i16(body,67),i16(body,69))
                elif op==0x08 and body[0]==0xff and s.spawn is None: s.spawn=(i16(body,1),i16(body,3),i16(body,5))
                elif op==0x35 and body[0]==CH: s.dec(body[1:])

a=C("Alice"); a.start()
time.sleep(0.5)
b=C("Bob"); b.start()
t=time.time()+10
while time.time()<t and (a.spawn is None or b.spawn is None): time.sleep(0.1)
if a.spawn is None or b.spawn is None: print("no spawn"); raise SystemExit
time.sleep(2.0)
print("=== equip frames right after join (held 0 = empty hand expected) ===")
for e in a.drain(): print("  Alice", e)
for e in b.drain(): print("  Bob  ", e)

# Alice mines the block under her feet + picks up the drop -> held becomes dirt
sx,sy,sz=a.spawn; bx,bz=sx>>5,sz>>5; feet=(sy-51)>>5
print("\n>>> Alice mines + picks up a block (held should become non-zero)")
got=None
for by in range(feet-1, feet-6, -1):
    a.set_block(bx,by,bz,0,0)
    time.sleep(0.6)
    for l,p in a.drain():
        if l=="DROP_SPAWN": got=(by,p)
    if got: break
if got:
    by,drop=got; dpx,dpy,dpz=drop["pos"]
    for _ in range(10):
        a.move(int(dpx*32),int(dpy*32)+51,int(dpz*32)); time.sleep(0.2)
time.sleep(1.5)
print("=== Bob's equip frames after Alice picked up (expect held=3 dirt for Alice) ===")
bob_equip=[e for e in b.drain() if e[0]=="EQUIP"]
for e in bob_equip: print("  Bob", e)
held_seen = [e[1]["held"] for e in bob_equip]
print(f"\n== Bob saw Alice's held ids: {held_seen}  (3 = dirt held item) ==")
a.run_=b.run_=False; time.sleep(0.3); print("DONE")
