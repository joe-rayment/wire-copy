#!/usr/bin/env python3
"""Stdlib-only fake Chatterbox worker for tests — same protocol v1 as
tools/chatterbox-worker/chatterbox_worker.py, no torch, no pip deps.

speak writes a real 0.2 s, 24 kHz, mono, 16-bit PCM sine WAV to out_path.

Failure injection via env vars, checked per request:
  FAKE_CB_FAIL=1     -> every speak replies ok:false "synthetic failure"
  FAKE_CB_DIE=1      -> process exits 1 immediately after the first speak request
  FAKE_CB_SLOW_MS=n  -> sleep n ms before each speak reply
"""
import json
import math
import os
import struct
import sys
import time
import wave

PROTO_OUT = sys.stdout
sys.stdout = sys.stderr

LOADED = False
DEVICE = None

SAMPLE_RATE = 24000
DURATION_SECONDS = 0.2


def emit(obj):
    print(json.dumps(obj), file=PROTO_OUT, flush=True)


def write_sine_wav(path):
    frame_count = int(SAMPLE_RATE * DURATION_SECONDS)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SAMPLE_RATE)
        frames = bytearray()
        for i in range(frame_count):
            value = int(12000 * math.sin(2 * math.pi * 440 * i / SAMPLE_RATE))
            frames += struct.pack("<h", value)
        w.writeframes(bytes(frames))


def ensure_loaded():
    global LOADED, DEVICE
    if LOADED:
        return
    emit({"event": "progress", "stage": "load", "message": "Loading fake model..."})
    LOADED = True
    DEVICE = "cpu"


def main():
    emit({"event": "ready", "protocol": 1})
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        rid = None
        try:
            req = json.loads(line)
            rid = req.get("id")
            cmd = req.get("cmd")
            if cmd == "health":
                emit({"ok": True, "event": "health", "model_loaded": LOADED, "device": DEVICE})
            elif cmd == "load":
                ensure_loaded()
                emit({"ok": True, "event": "loaded", "device": DEVICE})
            elif cmd == "speak":
                if os.environ.get("FAKE_CB_DIE") == "1":
                    sys.exit(1)
                slow_ms = int(os.environ.get("FAKE_CB_SLOW_MS", "0") or "0")
                if slow_ms > 0:
                    time.sleep(slow_ms / 1000.0)
                if os.environ.get("FAKE_CB_FAIL") == "1":
                    emit({"ok": False, "event": "error", "id": rid, "error": "synthetic failure"})
                    continue
                ensure_loaded()
                t0 = time.time()
                write_sine_wav(req["out_path"])
                emit({"ok": True, "event": "spoken", "id": rid, "out_path": req["out_path"],
                      "audio_seconds": DURATION_SECONDS, "gen_seconds": time.time() - t0})
            elif cmd == "shutdown":
                emit({"ok": True, "event": "bye"})
                return
            else:
                emit({"ok": False, "event": "error", "id": rid, "error": f"unknown cmd: {cmd}"})
        except SystemExit:
            raise
        except Exception as e:  # noqa: BLE001 - the loop must survive any request failure
            print(repr(e), file=sys.stderr, flush=True)
            emit({"ok": False, "event": "error", "id": rid, "error": str(e)})


if __name__ == "__main__":
    main()
