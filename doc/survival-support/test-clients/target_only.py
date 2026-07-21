#!/usr/bin/env python3
"""Minimal survival client that joins gt1 as 'Tgt' and idles, so the graphical
viewer sees it as an entity (for the /Inventory target paperdoll test)."""
import socket, sys, time, struct
HOST, PORT = "127.0.0.1", 25565
NAME = sys.argv[1] if len(sys.argv) > 1 else "Tgt"
SECS = int(sys.argv[2]) if len(sys.argv) > 2 else 60
SIZES = {0x00:130,0x01:0,0x02:0,0x03:1027,0x04:6,0x06:7,0x07:73,0x08:9,0x09:6,
         0x0a:5,0x0b:3,0x0c:1,0x0d:65,0x0e:64,0x0f:1,0x10:66,0x11:68,0x12:2,
         0x13:1,0x14:65,0x15:130,0x16:3,0x17:80,0x18:2,0x19:7,0x1a:5,0x1b:65,
         0x1c:8,0x1d:65,0x1e:72,0x1f:3,0x20:2,0x21:137,0x22:2,0x23:5,0x24:8,
         0x25:86,0x26:2,0x27:4,0x28:6,0x29:2,0x2b:3,0x2c:4,0x2e:66,0x2f:69,
         0x32:2,0x33:9,0x35:65}
def pad(s): return s.encode()[:64].ljust(64)
sock = socket.create_connection((HOST, PORT), timeout=10)
sock.sendall(bytes([0x00,7]) + pad(NAME) + pad("x"*32) + bytes([0x42]))
buf=b""; ext_done=False
def send_exts():
    exts=[("EnvColors",1),("ChangeModel",1),("SurvivalTest",3)]
    sock.sendall(bytes([0x10])+pad("TgtTest")+struct.pack(">h",len(exts)))
    for n,v in exts: sock.sendall(bytes([0x11])+pad(n)+struct.pack(">i",v))
end=time.time()+SECS
sock.settimeout(0.3)
while time.time()<end:
    try:
        d=sock.recv(16384)
        if not d: break
        buf+=d
    except socket.timeout: pass
    except OSError: break
    while buf:
        op=buf[0]; size=SIZES.get(op)
        if size is None: print("unknown op 0x%02x"%op); sys.exit(1)
        if len(buf)<1+size: break
        body,buf=buf[1:1+size],buf[1+size:]
        if op==0x10 and not ext_done:
            ext_done=True; send_exts()
print("Tgt done")
