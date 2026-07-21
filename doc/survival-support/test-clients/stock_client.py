#!/usr/bin/env python3
"""Synthetic STOCK classic-protocol client (no CPE): joins, tries to place and
delete a block near spawn, reports every SetBlock echo / chat message seen."""
import socket, sys, time, gzip, struct

HOST, PORT = "127.0.0.1", 25565
NAME = sys.argv[1] if len(sys.argv) > 1 else "StockGuy"

def pad(s): return s.encode()[:64].ljust(64)

sock = socket.create_connection((HOST, PORT), timeout=10)
# Identify: [0x00][protocol 7][name][mppass][unused 0x00 = NOT CPE]
sock.sendall(bytes([0x00, 7]) + pad(NAME) + pad("x" * 32) + bytes([0x00]))

SIZES = {0x00:130, 0x01:0, 0x02:0, 0x03:1027, 0x04:6, 0x06:7, 0x07:73, 0x08:9,
         0x09:6, 0x0a:5, 0x0b:3, 0x0c:1, 0x0d:65, 0x0e:64, 0x0f:1}
buf = b""
spawn = None
level_done = False
messages = []
set_blocks = []

def read_packets(duration):
    global buf, spawn, level_done
    end = time.time() + duration
    sock.settimeout(0.3)
    while time.time() < end:
        try:
            data = sock.recv(4096)
            if not data: break
            buf += data
        except socket.timeout:
            pass
        while buf:
            op = buf[0]
            size = SIZES.get(op)
            if size is None:
                print(f"!! unknown opcode 0x{op:02x}, aborting parse"); return
            if len(buf) < 1 + size: break
            body, buf = buf[1:1+size], buf[1+size:]
            if op == 0x04:
                level_done = True
            elif op == 0x07:  # spawn entity
                pid = body[0]
                if pid == 255:  # self
                    x, y, z = struct.unpack(">hhh", body[65:71])
                    spawn = (x / 32.0, y / 32.0, z / 32.0)
            elif op == 0x08 and body[0] == 255:
                x, y, z = struct.unpack(">hhh", body[1:7])
                spawn = (x / 32.0, y / 32.0, z / 32.0)
            elif op == 0x06:
                x, y, z, blk = struct.unpack(">hhhB", body)
                set_blocks.append((x, y, z, blk))
            elif op == 0x0d:
                messages.append(body[1:].decode("cp437", "replace").strip())

read_packets(6)
print(f"joined: level_done={level_done} spawn={spawn}")
for m in messages: print("  chat:", m)
messages.clear()

if not spawn: sys.exit("no spawn received")
bx, by, bz = int(spawn[0]) + 1, int(spawn[1]) - 1, int(spawn[2])

# try PLACE stone one block over
sock.sendall(struct.pack(">BhhhBB", 0x05, bx, by, bz, 1, 1))
read_packets(2)
print(f"after PLACE stone at ({bx},{by},{bz}): setblocks={set_blocks} msgs={messages}")
set_blocks.clear(); messages.clear()

# try DELETE the ground block below spawn-ish
gx, gy, gz = int(spawn[0]), int(spawn[1]) - 2, int(spawn[2])
sock.sendall(struct.pack(">BhhhBB", 0x05, gx, gy, gz, 0, 1))
read_packets(2)
print(f"after DELETE at ({gx},{gy},{gz}): setblocks={set_blocks} msgs={messages}")
sock.close()

# ---- fall test: rejoin, jump up 20 blocks, drop to ground, watch for death ----
sock = socket.create_connection((HOST, PORT), timeout=10)
sock.sendall(bytes([0x00, 7]) + pad(NAME + "2") + pad("x" * 32) + bytes([0x00]))
buf = b""; spawn = None; level_done = False
read_packets(6)
sx, sy, sz = spawn
def send_pos(x, y, z):
    sock.sendall(struct.pack(">BBhhhBB", 0x08, 255, int(x*32), int(y*32), int(z*32), 0, 0))
send_pos(sx, sy + 20, sz)          # way up
time.sleep(0.1)
y = sy + 20
while y > sy:                       # fall in steps
    y -= 1.5
    send_pos(sx, max(y, sy), sz)
    time.sleep(0.05)
send_pos(sx, sy, sz)                # land
messages.clear(); set_blocks.clear()
read_packets(3)
print(f"after 20-block fall: msgs={messages}")
print("DEATH" if any("floor" in m or "died" in m or "fell" in m for m in messages) else "NO DEATH (stock client survived the fall)")
sock.close()
