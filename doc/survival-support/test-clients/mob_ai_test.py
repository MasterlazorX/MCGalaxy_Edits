#!/usr/bin/env python3
"""Live test for the Indev A* creature AI (SurvivalMobs.IndevCreatureAI):
spawn a hostile mob at the client's feet, step the client away, and confirm the
mob PATHS toward it (streamed SURV_MOB_MOVE distance shrinks)."""
import socket,struct,time,gzip,io,sys,math
HOST,PORT=("127.0.0.1",25565); CH=0xB0
MOB=sys.argv[1] if len(sys.argv)>1 else "creeper"
def pad(s): return s.encode()[:64].ljust(64)
def i16(b,o): return struct.unpack_from(">h",b,o)[0]
SIZES={0x00:130,0x01:0,0x02:0,0x03:1027,0x04:6,0x06:7,0x07:73,0x08:9,0x09:6,0x0a:4,0x0b:3,0x0c:1,0x0d:65,0x0e:64,0x0f:1,0x10:66,0x11:68,0x12:2,0x13:1,0x14:65,0x15:130,0x16:3,0x17:80,0x18:2,0x19:7,0x1a:5,0x1b:65,0x1c:8,0x1d:65,0x1e:72,0x1f:3,0x20:2,0x21:137,0x22:2,0x23:5,0x24:8,0x25:86,0x26:2,0x27:4,0x28:6,0x29:2,0x2b:3,0x2c:4,0x2e:66,0x2f:69,0x32:2,0x33:9,0x35:65}
s=socket.create_connection((HOST,PORT),timeout=10)
s.sendall(bytes([0x00,7])+pad("physop")+pad("x"*32)+bytes([0x42]))
buf=b"";lvl=b"";W=H=L=0;blocks=None;fin=False;spawn=None;ext=False
mobs={}  # id -> (x,y,z)
def blk(x,y,z):
    if x<0 or y<0 or z<0 or x>=W or y>=H or z>=L: return -1
    return blocks[(y*L+z)*W+x]
def pump(dur):
    global buf,lvl,W,H,L,blocks,fin,spawn,ext
    e=time.time()+dur
    while time.time()<e:
        try:
            d=s.recv(65536)
            if not d:break
            buf+=d
        except socket.timeout: pass
        while buf:
            op=buf[0];sz=SIZES.get(op)
            if sz is None or len(buf)<1+sz: break
            body,buf=buf[1:1+sz],buf[1+sz:]
            if op==0x10 and not ext:
                ext=True;exts=[("EnvColors",1),("ChangeModel",1),("SurvivalTest",3)]
                s.sendall(bytes([0x10])+pad("M")+struct.pack(">h",len(exts)))
                for n,v in exts:s.sendall(bytes([0x11])+pad(n)+struct.pack(">i",v))
            elif op==0x03:
                ln=struct.unpack(">h",body[0:2])[0];lvl+=body[2:2+ln]
            elif op==0x04:
                W,H,L=i16(body,0),i16(body,2),i16(body,4)
                raw=gzip.GzipFile(fileobj=io.BytesIO(lvl)).read();n=struct.unpack(">i",raw[:4])[0];blocks=bytearray(raw[4:4+n]);fin=True
            elif op==0x07 and body[0]==0xff: spawn=(i16(body,65),i16(body,67),i16(body,69))
            elif op==0x08 and body[0]==0xff and spawn is None: spawn=(i16(body,1),i16(body,3),i16(body,5))
            elif op==0x35 and body[0]==CH:
                d=body[1:]; sop=d[0]
                if sop==0x10:  # MOB_SPAWN
                    mid=(d[1]<<8)|d[2]; x=i16(d,4)/32.0; y=i16(d,6)/32.0; z=i16(d,8)/32.0
                    mobs[mid]=(x,y,z,d[3])
                elif sop==0x11:  # MOB_MOVE
                    mid=(d[1]<<8)|d[2]; x=i16(d,3)/32.0; y=i16(d,5)/32.0; z=i16(d,7)/32.0
                    if mid in mobs: mobs[mid]=(x,y,z,mobs[mid][3])
                elif sop==0x13:  # MOB_DESPAWN
                    mid=(d[1]<<8)|d[2]; mobs.pop(mid,None)
pump(6)
# find a broad flat open grass patch near centre (room to path)
cx,cz=W//2,L//2;gx=gy=gz=None
for r in range(0,45):
    dn=False
    for dx in range(-r,r+1):
        for dz in range(-r,r+1):
            x,z=cx+dx,cz+dz
            if x<12 or z<12 or x>=W-12 or z>=L-12: continue
            # flat 9x9 grass roof with clear air above
            ok=True
            for ax in range(x-1,x+8):
                for az in range(z-1,z+2):
                    if not(blk(ax, blk_top(ax,az) if False else 0,az)>=0): pass
            # simpler: this column grass with air above, and the +X lane 8 cells all grass-topped at same y
            yy=None
            for y in range(H-12,3,-1):
                if blk(x,y,z)==2 and blk(x,y+1,z)==0 and blk(x,y+2,z)==0: yy=y;break
            if yy is None: continue
            good=True
            for k in range(0,9):
                if not(blk(x+k,yy,z)==2 and blk(x+k,yy+1,z)==0 and blk(x+k,yy+2,z)==0): good=False;break
            if good: gx,gy,gz=x,yy,z; dn=True; break
        if dn:break
    if dn:break
if gx is None: print("FAIL: no flat lane"); sys.exit(1)
print(f"flat lane start grass {(gx,gy,gz)} extending +X")
# stand at the lane start (feet at gy+1)
def tp(x,y,z): s.sendall(bytes([0x08,0xff])+struct.pack(">hhh",int(x*32),int(y*32),int(z*32))+bytes([0,0]))
px,py,pz=gx+0.5, gy+3, gz+0.5
tp(px,py,pz); pump(0.5)
s.sendall(bytes([0x0d,0xff])+pad("/SurvSpawn "+MOB))
pump(1.2)
if not mobs: print("FAIL: no mob spawned (echo below)"); 
mid=max(mobs) if mobs else None
print("spawned mob id",mid,"at",mobs.get(mid))
# step the client to the far end of the lane (8 blocks +X)
px=gx+8+0.5
tp(px,py,pz); pump(0.3)
print(f"client moved to ({px:.1f},{py},{pz:.1f}); watching the mob chase for 24s")
def dist():
    if mid not in mobs: return None
    mx,my,mz,_=mobs[mid]; return math.sqrt((mx-px)**2+(mz-pz)**2)
d0=dist(); print(f"  t=0  mob-to-player horizontal dist = {d0:.1f}" if d0 else "  mob gone")
mind=d0 if d0 else 999
for t in range(2,26,2):
    tp(px,py,pz)  # keep our position fresh
    pump(2.0)
    d=dist()
    if d is None: print(f"  t={t:2d} mob DESPAWNED/blew up (reached player or died)"); break
    mind=min(mind,d)
    print(f"  t={t:2d} dist={d:.1f}")
print(f"\nstart dist={d0:.1f}  closest approach={mind:.1f}" if d0 else "no data")
print("CHASE/PATHFIND:", "OBSERVED (mob approached)" if (d0 and mind < d0-1.5) else ("REACHED (blew up)" if mid not in mobs else "not seen"))
