import socket, time, struct, threading
HOST,PORT=("127.0.0.1",25565); CH=0xB0
SIZES={0x00:130,0x01:0,0x02:0,0x03:1027,0x04:6,0x06:7,0x07:73,0x08:9,0x09:6,0x0a:4,0x0b:3,0x0c:1,
 0x0d:65,0x0e:64,0x0f:1,0x10:66,0x11:68,0x12:2,0x13:1,0x14:65,0x15:130,0x16:3,0x17:80,0x18:2,0x19:7,
 0x1a:5,0x1b:65,0x1c:8,0x1d:65,0x1e:72,0x1f:3,0x20:2,0x21:137,0x22:2,0x23:5,0x24:8,0x25:86,0x26:2,
 0x27:4,0x28:6,0x29:2,0x2b:3,0x2c:4,0x2e:66,0x2f:69,0x32:2,0x33:9,0x35:65}
def pad(s): return s.encode()[:64].ljust(64)
def i16(b,o): return struct.unpack_from(">h",b,o)[0]
class C(threading.Thread):
    def __init__(s):
        super().__init__(daemon=True); s.sock=socket.create_connection((HOST,PORT),timeout=10)
        s.buf=b""; s.ext=False; s.spawn=None; s.mobs={}; s.arrows=[]; s.lock=threading.Lock(); s.run_=True
        s.sock.sendall(bytes([0x00,7])+pad("SkelBait")+pad("x"*32)+bytes([0x42]))
    def exts(s):
        s.sock.sendall(bytes([0x10])+pad("k")+struct.pack(">h",3))
        for n,v in [("EnvColors",1),("ChangeModel",1),("SurvivalTest",3)]: s.sock.sendall(bytes([0x11])+pad(n)+struct.pack(">i",v))
    def move(s,x,y,z): s.sock.sendall(bytes([0x08,0xff])+struct.pack(">hhh",int(x),int(y),int(z))+bytes([0,0]))
    def dec(s,d):
        op=d[0]
        if op==0x10:
            mid=(d[1]<<8)|d[2]; t=d[3]
            with s.lock: s.mobs[mid]=[t,i16(d,4)/32,i16(d,6)/32,i16(d,8)/32]
        elif op==0x11:
            mid=(d[1]<<8)|d[2]
            with s.lock:
                if mid in s.mobs: s.mobs[mid][1:]=[i16(d,3)/32,i16(d,5)/32,i16(d,7)/32]
        elif op==0x13:
            with s.lock: s.mobs.pop((d[1]<<8)|d[2],None)
        elif op==0x33:
            spd=round((i16(d,11)**2+i16(d,13)**2+i16(d,15)**2)**0.5/1024,2)
            with s.lock: s.arrows.append(dict(id=(d[1]<<8)|d[2],type=d[3],grav=d[4]/100,speed=spd,
                pos=(round(i16(d,5)/32,1),round(i16(d,7)/32,1),round(i16(d,9)/32,1))))
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
                if sz is None: print("unk",hex(op)); s.run_=False; return
                if len(s.buf)<1+sz: break
                body,s.buf=s.buf[1:1+sz],s.buf[1+sz:]
                if op==0x10 and not s.ext: s.ext=True; s.exts()
                elif op==0x07 and body[0]==0xff: s.spawn=(i16(body,65),i16(body,67),i16(body,69))
                elif op==0x08 and body[0]==0xff and s.spawn is None: s.spawn=(i16(body,1),i16(body,3),i16(body,5))
                elif op==0x35 and body[0]==CH: s.dec(body[1:])
c=C(); c.start()
t=time.time()+10
while time.time()<t and c.spawn is None: time.sleep(0.1)
if not c.spawn: print("no spawn"); raise SystemExit
print("joined; waiting for mobs..."); time.sleep(4)
# find skeletons, camp next to each in turn, waiting for a shot
for rounds in range(12):
    with c.lock: skels={k:v for k,v in c.mobs.items() if v[0]==1}
    print(f"round {rounds}: {len(skels)} skeletons, {len(c.mobs)} mobs total")
    if not skels: time.sleep(1.5); continue
    sid=list(skels)[0]; t,mx,my,mz=skels[sid]
    print(f"  camping skeleton {sid} at ({mx:.0f},{my:.0f},{mz:.0f})")
    # stand ~4 blocks from the skeleton at its height (within the 10-block shoot range)
    for _ in range(10):
        with c.lock:
            if sid in c.mobs: t,mx,my,mz=c.mobs[sid]
        c.move((mx+3)*32, my*32+51, mz*32)
        time.sleep(0.4)
    with c.lock: ar=[a for a in c.arrows]
    if ar: break
with c.lock: ar=list(c.arrows)
print(f"\narrows seen: {len(ar)}")
for a in ar[:6]: print("   ",a)
print(f"\n== INDEV SKELETON SHOT (type 0, speed~0.6): {any(a['type']==0 for a in ar)} ==")
c.run_=False; time.sleep(0.3); print("DONE")
