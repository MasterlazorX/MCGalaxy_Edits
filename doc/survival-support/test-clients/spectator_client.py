#!/usr/bin/env python3
"""Synthetic CPE spectator: negotiates EnvColors + ChangeModel (NOT SurvivalTest),
joins, and reports EnvSetColor / ExtAddEntity2 / ChangeModel / teleport traffic -
the two §21/§15.1 fallbacks a stock-but-CPE client should receive."""
import socket, sys, time, struct

HOST, PORT = "127.0.0.1", 25565
NAME = sys.argv[1] if len(sys.argv) > 1 else "SpecGuy"
WATCH = float(sys.argv[2]) if len(sys.argv) > 2 else 12

def pad(s): return s.encode()[:64].ljust(64)

sock = socket.create_connection((HOST, PORT), timeout=10)
sock.sendall(bytes([0x00, 7]) + pad(NAME) + pad("x" * 32) + bytes([0x42]))  # 0x42 = CPE

SIZES = {0x00:130, 0x01:0, 0x02:0, 0x03:1027, 0x04:6, 0x06:7, 0x07:73, 0x08:9,
         0x09:6, 0x0a:5, 0x0b:3, 0x0c:1, 0x0d:65, 0x0e:64, 0x0f:1,
         0x10:66, 0x11:68, 0x12:2, 0x13:1, 0x14:65, 0x15:130, 0x16:3,
         0x17:80, 0x18:2, 0x19:7, 0x1a:5, 0x1b:65, 0x1c:8, 0x1d:65,
         0x1e:72, 0x1f:3, 0x20:2, 0x21:137, 0x23:5, 0x24:8, 0x25:86,
         0x26:2, 0x27:4, 0x28:6, 0x29:2, 0x2b:3, 0x2c:4, 0x2e:66, 0x2f:69,
         0x35:66}
buf = b""
env_colors = []
spawns = []
models = []
teleports = 0
ext_done = False

def send_exts():
    # ExtInfo + our 2 entries
    sock.sendall(bytes([0x10]) + pad("SpectatorTest") + struct.pack(">h", 2))
    sock.sendall(bytes([0x11]) + pad("EnvColors")   + struct.pack(">i", 1))
    sock.sendall(bytes([0x11]) + pad("ChangeModel") + struct.pack(">i", 1))

def read_packets(duration):
    global buf, ext_done, teleports
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
            elif op == 0x19:
                t, r, g, b = struct.unpack(">Bhhh", body)
                env_colors.append((t, r, g, b))
            elif op == 0x21:
                eid = body[0]
                name = body[1:65].decode("cp437", "replace").strip("\x00 ")
                skin = body[65:129].decode("cp437", "replace").strip("\x00 ")
                spawns.append((eid, name, skin))
            elif op == 0x1d:
                eid = body[0]
                model = body[1:65].decode("cp437", "replace").strip("\x00 ")
                models.append((eid, model))
            elif op == 0x08 and body[0] != 255 and body[0] >= 200:
                teleports += 1

read_packets(WATCH)
print(f"env color packets: {len(env_colors)}"); [print("  env", e) for e in env_colors]
print(f"mirror entity spawns: {spawns}")
print(f"model changes: {models}")
print(f"mirror teleports seen: {teleports}")
sock.close()
