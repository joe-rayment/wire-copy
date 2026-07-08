#!/usr/bin/env python3
"""WireCopy Chatterbox worker.

stdout carries ONLY protocol JSON lines (one object per line); ALL logging,
tracebacks, and any stray library output go to stderr. Protocol v1:

  {"cmd":"health"}   -> {"ok":true,"event":"health","model_loaded":bool,"device":str|null}
  {"cmd":"load","device":"auto"}
                     -> progress lines, then {"ok":true,"event":"loaded","device":"cuda|mps|cpu"}
  {"cmd":"speak","id":"c3","text":"...","sample_path":null|abs,"exaggeration":0.5,
   "cfg_weight":0.5,"device":"auto","out_path":"/abs/tmp/c3.wav"}
                     -> {"ok":true,"event":"spoken","id":"c3","out_path":"...",
                         "audio_seconds":8.2,"gen_seconds":4.1}
  {"cmd":"shutdown"} -> {"ok":true,"event":"bye"} then exit 0
  anything else      -> {"ok":false,"event":"error","id":<if present>,"error":"..."}

Launch: uv run --python 3.11 --with chatterbox-tts==0.1.7 chatterbox_worker.py
health/shutdown never import torch, so they answer fast on a cold env.
"""
import json
import sys
import time
import traceback

# Protocol goes to the REAL stdout; everything else (including any library
# that print()s) is rerouted to stderr so it can never corrupt the protocol.
PROTO_OUT = sys.stdout
sys.stdout = sys.stderr


def emit(obj):
    print(json.dumps(obj), file=PROTO_OUT, flush=True)


def log(msg):
    print(msg, file=sys.stderr, flush=True)


MODEL, DEVICE = None, None


def pick_device(pref):
    import torch
    if pref and pref != "auto":
        return pref
    if torch.cuda.is_available():
        return "cuda"
    mps = getattr(torch.backends, "mps", None)
    if mps is not None and torch.backends.mps.is_available():
        return "mps"
    return "cpu"


def ensure_loaded(pref):
    global MODEL, DEVICE
    if MODEL is not None:
        return
    emit({"event": "progress", "stage": "load",
          "message": "Loading Chatterbox model (first run downloads weights, may take a while)..."})
    from chatterbox.tts import ChatterboxTTS
    DEVICE = pick_device(pref)
    t0 = time.time()
    MODEL = ChatterboxTTS.from_pretrained(device=DEVICE)
    emit({"event": "progress", "stage": "load",
          "message": f"Model ready on {DEVICE} in {time.time() - t0:.0f}s"})


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
                emit({"ok": True, "event": "health", "model_loaded": MODEL is not None, "device": DEVICE})
            elif cmd == "load":
                ensure_loaded(req.get("device", "auto"))
                emit({"ok": True, "event": "loaded", "device": DEVICE})
            elif cmd == "speak":
                ensure_loaded(req.get("device", "auto"))
                import torchaudio
                t0 = time.time()
                kwargs = {"exaggeration": float(req.get("exaggeration", 0.5)),
                          "cfg_weight": float(req.get("cfg_weight", 0.5))}
                if req.get("sample_path"):
                    kwargs["audio_prompt_path"] = req["sample_path"]
                wav = MODEL.generate(req["text"], **kwargs)
                torchaudio.save(req["out_path"], wav, MODEL.sr)
                emit({"ok": True, "event": "spoken", "id": rid, "out_path": req["out_path"],
                      "audio_seconds": wav.shape[-1] / MODEL.sr, "gen_seconds": time.time() - t0})
            elif cmd == "shutdown":
                emit({"ok": True, "event": "bye"})
                return
            else:
                emit({"ok": False, "event": "error", "id": rid, "error": f"unknown cmd: {cmd}"})
        except Exception as e:  # noqa: BLE001 - the loop must survive any request failure
            log(traceback.format_exc())
            emit({"ok": False, "event": "error", "id": rid, "error": str(e)})


if __name__ == "__main__":
    main()
