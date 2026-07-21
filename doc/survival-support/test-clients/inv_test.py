#!/usr/bin/env python3
"""Synthetic survival clients to exercise /Inventory (CONT_PLAYERINV).
Connects a target and a viewer (both negotiate SurvivalTest v2), waits for a
signal file (so the console can rank the viewer + give the target items), then
the viewer runs /inventory <target> and we decode the 0xB0 survival frames:
CONT_OPEN / CONT_SLOT / INV_SLOT / CURSOR. Optionally drives SLOT_CLICK edits."""
import socket, sys, time, struct, threading, os

HOST, PORT = "127.0.0.1", 25565
SIGNAL = "/tmp/claude-0/-home-user/ebc9ea10-f533-5652-9e7f-ec7fb09f7000/scratchpad/go"
CH = 0xB0

# server->client payload sizes (after the opcode byte). Base classic + the CPE
# subset a survival client negotiates here (EnvColors, ChangeModel, SurvivalTest).
SIZES = {0x00:130, 0x01:0, 0x02:0, 0x03:1027, 0x04:6, 0x06:7, 0x07:73, 0x08:9,
         0x09:6, 0x0a:5, 0x0b:3, 0x0c:1, 0x0d:65, 0x0e:64, 0x0f:1,
         0x10:66, 0x11:68, 0x12:2, 0x13:1, 0x14:65, 0x15:130, 0x16:3,
         0x17:80, 0x18:2, 0x19:7, 0x1a:5, 0x1b:65, 0x1c:8, 0x1d:65,
         0x1e:72, 0x1f:3, 0x20:2, 0x21:137, 0x22:2, 0x23:5, 0x24:8, 0x25:86,
         0x26:2, 0x27:4, 0x28:6, 0x29:2, 0x2b:3, 0x2c:4, 0x2e:66, 0x2f:69,
         0x32:2, 0x33:9, 0x35:65}

def pad(s): return s.encode()[:64].ljust(64)

class Client(threading.Thread):
    def __init__(self, name):
        super().__init__(daemon=True)
        self.name = name
        self.sock = socket.create_connection((HOST, PORT), timeout=10)
        self.buf = b""
        self.ext_done = False
        self.joined = False
        self.frames = []          # decoded (label, tuple) survival frames
        self.lock = threading.Lock()
        self.running = True
        # login (CPE)
        self.sock.sendall(bytes([0x00, 7]) + pad(name) + pad("x"*32) + bytes([0x42]))

    def send_exts(self):
        exts = [("EnvColors",1), ("ChangeModel",1), ("SurvivalTest",2)]
        self.sock.sendall(bytes([0x10]) + pad("InvTest") + struct.pack(">h", len(exts)))
        for n,v in exts:
            self.sock.sendall(bytes([0x11]) + pad(n) + struct.pack(">i", v))

    def chat(self, msg):
        self.sock.sendall(bytes([0x0d, 0xff]) + pad(msg))

    def plugin(self, data):
        d = bytes(data)[:64].ljust(64, b"\0")
        self.sock.sendall(bytes([0x35, CH]) + d)

    def slot_click(self, idx, button):
        self.plugin(bytes([0x82, (idx>>8)&0xff, idx&0xff, button]))

    def cont_close(self):
        self.plugin(bytes([0x84]))

    def decode_surv(self, data):
        op = data[0]
        if op == 0x20:   # INV_FULL [base][count] then count*(id2,cnt,dmg2)
            base, cnt = data[1], data[2]
            items = []
            for i in range(cnt):
                at = 3 + i*5
                sid = (data[at]<<8)|data[at+1]; c = data[at+2]
                if c: items.append((base+i, sid, c))
            if items: self._log("INV_FULL", items)
        elif op == 0x21: # INV_SLOT [idx][id2][cnt][dmg2]
            idx=data[1]; sid=(data[2]<<8)|data[3]; c=data[4]
            self._log("INV_SLOT", (idx, sid, c))
        elif op == 0x22: # CONT_OPEN [kind][slots]
            self._log("CONT_OPEN", (data[1], data[2]))
        elif op == 0x23: # CONT_SLOT [cell][id2][cnt][dmg2]
            cell=data[1]; sid=(data[2]<<8)|data[3]; c=data[4]
            self._log("CONT_SLOT", (cell, sid, c))
        elif op == 0x25: # CURSOR [id2][cnt][dmg2]
            sid=(data[1]<<8)|data[2]; c=data[3]
            self._log("CURSOR", (sid, c))

    def _log(self, label, payload):
        with self.lock:
            self.frames.append((label, payload))

    def drain_frames(self):
        with self.lock:
            f = self.frames[:]; self.frames = []
        return f

    def run(self):
        self.sock.settimeout(0.3)
        while self.running:
            try:
                data = self.sock.recv(16384)
                if not data: break
                self.buf += data
            except socket.timeout:
                pass
            except OSError:
                break
            while self.buf:
                op = self.buf[0]
                size = SIZES.get(op)
                if size is None:
                    print(f"[{self.name}] !! unknown opcode 0x{op:02x}"); self.running=False; return
                if len(self.buf) < 1+size: break
                body, self.buf = self.buf[1:1+size], self.buf[1+size:]
                if op == 0x10 and not self.ext_done:
                    self.ext_done = True; self.send_exts()
                elif op == 0x04:
                    self.joined = True
                elif op == 0x35:
                    ch = body[0]
                    if ch == CH: self.decode_surv(body[1:])
                elif op == 0x0d:   # server chat message
                    try:
                        txt = body[1:].split(b'\0')[0].decode('cp437','replace').rstrip()
                    except Exception:
                        txt = repr(body[1:])
                    self._log("CHAT", txt)

def wait_join(c, secs=8):
    end=time.time()+secs
    while time.time()<end and not c.joined: time.sleep(0.1)
    return c.joined

def main():
    tgt = Client("Tgt"); adm = Client("Adm")
    tgt.start(); adm.start()
    print("connecting...")
    print("Tgt joined:", wait_join(tgt), " Adm joined:", wait_join(adm))
    time.sleep(1.0)
    tgt.drain_frames(); adm.drain_frames()  # clear handshake noise

    print("BOTH_JOINED - waiting for signal file (console: rank Adm + give Tgt items)")
    end=time.time()+60
    while time.time()<end and not os.path.exists(SIGNAL): time.sleep(0.2)
    if not os.path.exists(SIGNAL): print("no signal, aborting"); return
    print("signal received")
    time.sleep(0.5)

    # show Tgt's own inventory frames from the /SurvivalGive (sanity)
    print("--- Tgt inventory frames (from give) ---")
    for f in tgt.drain_frames(): print("   Tgt", f)

    # sanity: does chat work at all?
    adm.drain_frames()
    print(">>> Adm runs /help (chat sanity)")
    adm.chat("/help")
    time.sleep(1.5)
    for f in adm.drain_frames():
        if f[0]=="CHAT": print("   Adm", f)

    # viewer opens the target's inventory
    adm.drain_frames()
    print(">>> Adm runs /inventory Tgt")
    adm.chat("/inventory Tgt")
    time.sleep(2.5)
    print("--- Adm received frames (open + stream) ---")
    open_frames = adm.drain_frames()
    for f in open_frames: print("   Adm", f)

    # EDIT test (only meaningful if Adm has edit perm): pick up cell 27 (hotbar
    # slot 0 -> should hold the first given stack), then place into own main slot 9.
    print(">>> Adm SLOT_CLICK pickup cell 45+27=72 (container base 45)")
    adm.slot_click(45+27, 0)   # left click container cell 27
    time.sleep(1.5)
    for f in adm.drain_frames(): print("   Adm", f)
    print(">>> Adm SLOT_CLICK place into own slot 9")
    adm.slot_click(9, 0)
    time.sleep(1.5)
    for f in adm.drain_frames(): print("   Adm", f)
    print("--- Tgt frames after admin edit (should mirror the pickup) ---")
    for f in tgt.drain_frames(): print("   Tgt", f)

    adm.cont_close()
    time.sleep(1.0)
    print("--- Adm frames after close ---")
    for f in adm.drain_frames(): print("   Adm", f)

    tgt.running = adm.running = False
    time.sleep(0.5)
    print("DONE")

main()
