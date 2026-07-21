#!/usr/bin/env python3
"""Non-survival CPE client that negotiates BlockPermissions, joins, optionally
runs a chat command, and reports the SetBlockPermission (0x1c) packets - to
verify a survival 'visitor' map is pushed as read-only (place=0/delete=0)."""
import socket, sys, time, struct

HOST, PORT = "127.0.0.1", 25565
NAME = sys.argv[1] if len(sys.argv) > 1 else "PermGuy"
CMD  = sys.argv[2] if len(sys.argv) > 2 else ""
WATCH = float(sys.argv[3]) if len(sys.argv) > 3 else 8

def pad(s): return s.encode()[:64].ljust(64)
sock = socket.create_connection((HOST, PORT), timeout=10)
sock.sendall(bytes([0x00, 7]) + pad(NAME) + pad("x" * 32) + bytes([0x42]))

SIZES = {0x00:130, 0x01:0, 0x02:0, 0x03:1027, 0x04:6, 0x06:7, 0x07:73, 0x08:9,
         0x09:6, 0x0a:5, 0x0b:3, 0x0c:1, 0x0d:65, 0x0e:64, 0x0f:1,
         0x10:66, 0x11:68, 0x12:2, 0x13:1, 0x14:65, 0x15:130, 0x16:3,
         0x17:80, 0x18:2, 0x19:7, 0x1a:5, 0x1b:65, 0x1c:3, 0x1d:65,
         0x1e:72, 0x1f:3, 0x20:2, 0x21:137, 0x22:0, 0x23:79, 0x24:1, 0x25:87,
         0x26:2, 0x27:4, 0x28:6, 0x29:2, 0x2b:3, 0x2c:4, 0x2e:66, 0x2f:69, 0x35:66}
buf = b""
perms = {}     # block id -> (place, delete)
ext_done = False
sent_cmd = False

def send_exts():
    sock.sendall(bytes([0x10]) + pad("PermTest") + struct.pack(">h", 1))
    sock.sendall(bytes([0x11]) + pad("BlockPermissions") + struct.pack(">i", 1))

def cstr(b): return b.decode("cp437", "replace").strip("\x00 ")

def run(duration):
    global buf, ext_done, sent_cmd
    end = time.time() + duration
    sock.settimeout(0.3)
    while time.time() < end:
        try:
            d = sock.recv(8192)
            if not d: break
            buf += d
        except socket.timeout:
            pass
        while buf:
            op = buf[0]
            size = SIZES.get(op)
            if size is None:
                print("!! unknown opcode 0x%02x" % op); return
            if len(buf) < 1 + size: break
            body, buf = buf[1:1+size], buf[1+size:]
            if op == 0x10 and not ext_done:
                ext_done = True; send_exts()
            elif op == 0x0f and CMD and not sent_cmd:
                sent_cmd = True
                sock.sendall(bytes([0x0d, 0xff]) + pad(CMD))
            elif op == 0x1c:
                blk, place, delete = body[0], body[1], body[2]
                perms[blk] = (place, delete)

run(WATCH)
print("SetBlockPermission packets:", len(perms))
if perms:
    can_place  = [b for b,(p,d) in perms.items() if p]
    can_delete = [b for b,(p,d) in perms.items() if d]
    print("blocks placeable:", len(can_place), "  deletable:", len(can_delete))
    print("sample (block: place,delete):", {b: perms[b] for b in list(sorted(perms))[:6]})
    print("VERDICT:", "READ-ONLY (no place, no delete)" if not can_place and not can_delete
          else "buildable (%d placeable)" % len(can_place))
sock.close()
