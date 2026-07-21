#!/usr/bin/env python3
"""Synthetic CPE client negotiating BlockDefinitions v1 + BlockDefinitionsExt v2
(NOT SurvivalTest): joins, optionally runs a chat command, and reports every
DefineBlock / DefineBlockExt / UndefineBlock received - verifying the phase-1
Indev block set a stock-but-CPE visitor gets on survival maps."""
import socket, sys, time, struct

HOST, PORT = "127.0.0.1", 25565
NAME  = sys.argv[1] if len(sys.argv) > 1 else "BdefGuy"
CMD   = sys.argv[2] if len(sys.argv) > 2 else ""
WATCH = float(sys.argv[3]) if len(sys.argv) > 3 else 10

def pad(s): return s.encode()[:64].ljust(64)

sock = socket.create_connection((HOST, PORT), timeout=10)
sock.sendall(bytes([0x00, 7]) + pad(NAME) + pad("x" * 32) + bytes([0x42]))  # CPE magic

SIZES = {0x00:130, 0x01:0, 0x02:0, 0x03:1027, 0x04:6, 0x06:7, 0x07:73, 0x08:9,
         0x09:6, 0x0a:5, 0x0b:3, 0x0c:1, 0x0d:65, 0x0e:64, 0x0f:1,
         0x10:66, 0x11:68, 0x12:2, 0x13:1, 0x14:65, 0x15:130, 0x16:3,
         0x17:80, 0x18:2, 0x19:7, 0x1a:5, 0x1b:65, 0x1c:8, 0x1d:65,
         0x1e:72, 0x1f:3, 0x20:2, 0x21:137, 0x22:0,
         0x23:79,   # DefineBlock (sprites)
         0x24:1,    # UndefineBlock
         0x25:87,   # DefineBlockExt v2 uniqueSideTexs (cubes)
         0x26:2, 0x27:4, 0x28:6, 0x29:2, 0x2b:3, 0x2c:4, 0x2e:66, 0x2f:69,
         0x35:66}
buf = b""
defs = {}       # raw id -> (name, kind, fields)
undefs = []
chats = []
ext_done = False
sent_cmd = False

def send_exts():
    sock.sendall(bytes([0x10]) + pad("BdefTest") + struct.pack(">h", 2))
    sock.sendall(bytes([0x11]) + pad("BlockDefinitions")    + struct.pack(">i", 1))
    sock.sendall(bytes([0x11]) + pad("BlockDefinitionsExt") + struct.pack(">i", 2))

def cstr(b): return b.decode("cp437", "replace").strip("\x00 ")

def read_packets(duration):
    global buf, ext_done, sent_cmd
    end = time.time() + duration
    sock.settimeout(0.3)
    while time.time() < end:
        try:
            data = sock.recv(8192)
            if not data: break
            buf += data
        except socket.timeout:
            pass
        while buf:
            op = buf[0]
            size = SIZES.get(op)
            if size is None:
                print(f"!! unknown opcode 0x{op:02x}"); return
            if len(buf) < 1 + size: break
            body, buf = buf[1:1+size], buf[1+size:]
            if op == 0x10 and not ext_done:
                ext_done = True
                send_exts()
            elif op == 0x0f and CMD and not sent_cmd:
                # level finalize -> logged in; run the requested command once
                sent_cmd = True
                sock.sendall(bytes([0x0d, 0xff]) + pad(CMD))
            elif op == 0x0d:
                chats.append(cstr(body[1:]))
            elif op == 0x23:
                raw = body[0]; name = cstr(body[1:65])
                collide, speed = body[65], body[66]
                top, side, bottom = body[67], body[68], body[69]
                light, sound, bright = body[70], body[71], body[72]
                shape, draw = body[73], body[74]
                defs[raw] = (name, "def", f"shape={shape} draw={draw} collide={collide} "
                             f"tex(t/s/b)={top}/{side}/{bottom} sound={sound} bright={bright:#x}")
            elif op == 0x25:
                raw = body[0]; name = cstr(body[1:65])
                collide = body[65]
                top, left, right, front, back, bottom = body[67:73]
                light, sound, bright = body[73], body[74], body[75]
                mins, maxs = tuple(body[76:79]), tuple(body[79:82])
                draw = body[82]
                defs[raw] = (name, "ext", f"draw={draw} collide={collide} "
                             f"tex(t/l/r/f/b/d)={top}/{left}/{right}/{front}/{back}/{bottom} "
                             f"sound={sound} bright={bright:#x} bb={mins}-{maxs}")
            elif op == 0x24:
                undefs.append(body[0])

read_packets(WATCH)
print(f"defs received: {len(defs)}")
for raw in sorted(defs):
    name, kind, info = defs[raw]
    print(f"  {raw:3d} [{kind}] {name:<14} {info}")
print(f"undefines: {sorted(undefs)}")
print("chat tail:")
for c in chats[-6:]: print("  |", c)
sock.close()
