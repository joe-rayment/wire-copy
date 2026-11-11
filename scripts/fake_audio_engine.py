#!/usr/bin/env python3
"""Fake audio-engine for the Routines harness (workspace-wyvga.9). stdlib only.

  fake_audio_engine.py --port 8090 [--log FILE] [--no-default-feed]

Any bearer key is accepted. Jobs succeed instantly; audio is ~1 s of silent MP3.
--log: "METHOD path" per request, and per POST /v1/jobs one line per chapter:
"CHAPTER <title> | <source url or ->".
By default one fake feed is the Default feed: jobs sent with feed "@default" get a
"publication" that is pending, then published ~2 s after the job succeeds.
--no-default-feed: no feed exists and publication is null (episode not published). The client's ChapterInput carries only
Title+Text, so the source url is the first http(s) URL found in the chapter
text/title, else in request metadata, else "-".
"""
import argparse, hashlib, json, re, time, uuid, datetime, threading
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

# MPEG-1 Layer III, 128 kbps, 44.1 kHz, no padding: 417-byte frame, 26.1 ms. Zeroed
# side info + main data = a valid frame that decodes to silence.
FRAME = bytes([0xFF, 0xFB, 0x90, 0x64]) + bytes(417 - 4)
MP3 = FRAME * 40
def _box(t, d): return (8 + len(d)).to_bytes(4, "big") + t + d
# Pipeline only saves the bytes (format "m4b"): minimal MP4-family container, ftyp M4B + mdat.
M4B = _box(b"ftyp", b"M4B \0\0\0\0M4B mp42isom") + _box(b"mdat", bytes(64))
DURATION = 40 * 1152 / 44100
JOBS = {}
LOCK = threading.Lock()
LOGF = None
NO_DEFAULT_FEED = False
PUBLISH_DELAY = 2.0
FEED = dict(name="wire-copy", title="Wire Copy", description=None, author=None, language="en", category=None,
            explicit=False, subscribeUrl="https://example.invalid/feeds/fake-token/feed.xml", episodeCount=0,
            pendingCount=0, createdAt="2026-01-01T00:00:00Z", updatedAt="2026-01-01T00:00:00Z", isDefault=True)
URL_RE = re.compile(r"https?://[^\s\"'<>)\]]+")

def log(line):
    if LOGF:
        with LOCK, open(LOGF, "a", encoding="utf-8") as f: f.write(line + "\n")

def now(): return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

def publication(j):
    if not j.get("feed"): return None
    done = time.time() - j["created"] >= PUBLISH_DELAY
    return dict(feed=j["feed"], status="published" if done else "pending",
                audioUrl=f"{FEED['subscribeUrl'].rsplit('/', 1)[0]}/{j['id']}.m4b" if done else None,
                error=None, publishedAt=now() if done else None, removedAt=None)

def job_json(j):
    return dict(id=j["id"], status="succeeded", phase="done", progress=1, chunksDone=j["chunks"],
                chunksTotal=j["chunks"], title=j["title"], engine="fake", voice="fake", format="m4b",
                priority=0, attempts=1, estCostUsd=0, durationSeconds=DURATION, bytes=len(M4B),
                createdAt=j["createdAt"], startedAt=j["createdAt"], finishedAt=j["createdAt"],
                updatedAt=j["createdAt"], metadata=j["metadata"], audioUrl=f"/v1/jobs/{j['id']}/audio",
                publication=publication(j))

class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"  # one request per connection: no keep-alive bookkeeping
    def log_message(self, *a): pass
    def send(self, code, body, ctype="application/json"):
        if not isinstance(body, bytes): body = json.dumps(body).encode()
        self.send_response(code); self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body))); self.send_header("Connection", "close"); self.end_headers()
        if self.command != "HEAD": self.wfile.write(body)
    def body(self):
        try:
            if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
                raw = b""
                while True:
                    size = int(self.rfile.readline().split(b";")[0].strip() or b"0", 16)
                    if size == 0:
                        while self.rfile.readline().strip(): pass  # trailers
                        break
                    raw += self.rfile.read(size); self.rfile.readline()
            else:
                raw = self.rfile.read(int(self.headers.get("Content-Length") or 0))
            return json.loads(raw or b"{}")
        except Exception: return {}
    def do_GET(self):
        p = self.path.split("?")[0]; log(f"GET {p}")
        if p in ("/healthz", "/readyz"): return self.send(200, {"status": "ok"})
        if p == "/v1/engines":
            # WireCopy's readiness check (routine runs do it before loading any page) looks
            # its selected engine up BY NAME — "openai" or "chatterbox" — and needs it
            # configured, so the fake offers both.
            return self.send(200, [dict(name=n, isConfigured=True, voices=["fake"], defaultVoice="fake",
                                        supportsInstructions=False, maxConcurrency=1, maxChunkSize=4096,
                                        audioExtension=".mp3", isDefault=(n == "chatterbox"),
                                        settingsFingerprint=hashlib.sha256(n.encode()).hexdigest())
                                   for n in ("openai", "chatterbox", "fake")])
        if p == "/v1/feeds":
            return self.send(200, dict(items=[] if NO_DEFAULT_FEED else [FEED]))
        if p == "/v1/jobs":
            return self.send(200, dict(items=[job_json(j) for j in JOBS.values()], nextCursor=None))
        m = re.fullmatch(r"/v1/jobs/([^/]+)(?:/(events|audio))?", p)
        if m:
            j = JOBS.get(m.group(1))
            if m.group(2) == "events":   # never 404: the client rethrows it
                ev = dict(jobId=m.group(1), type="job.completed", status="succeeded", progress=1)
                return self.send(200, f"data: {json.dumps(ev)}\n\n".encode(), "text/event-stream")
            if not j: return self.send(404, {"error": "not found"})
            if m.group(2) == "audio": return self.send(200, M4B, "audio/mp4")
            return self.send(200, job_json(j))
        self.send(404, {"error": "not found"})
    def do_DELETE(self):
        log(f"DELETE {self.path}"); self.send(204, b"", "text/plain")
    def do_POST(self):
        p = self.path.split("?")[0]; log(f"POST {p}"); b = self.body()
        if p == "/v1/jobs":
            jid = uuid.uuid4().hex[:12]; chs = b.get("chapters") or []
            md = b.get("metadata") or {}
            mdurl = next((v for v in md.values() if isinstance(v, str) and URL_RE.match(v)), None)
            for c in chs:
                src = c.get("sourceUrl") or c.get("source") or c.get("url")
                if not src:
                    m = URL_RE.search((c.get("text") or "") + " " + (c.get("title") or ""))
                    src = m.group(0) if m else mdurl
                log(f"CHAPTER {c.get('title', '')} | {src or '-'}")
            feed = b.get("feed")
            if feed == "@default": feed = None if NO_DEFAULT_FEED else FEED["name"]
            JOBS[jid] = dict(id=jid, feed=feed, created=time.time(), title=b.get("title"), chunks=max(1, len(chs)), createdAt=now(), metadata=md or None)
            return self.send(202, dict(id=jid, status="succeeded", createdAt=JOBS[jid]["createdAt"],
                                       publication=publication(JOBS[jid])))
        if p == "/v1/jobs/estimate":
            chs = b.get("chapters") or []
            chars = sum(len(c.get("text") or "") for c in chs) + len(b.get("text") or "")
            return self.send(200, dict(engine="fake", format=b.get("format") or "mp3", engineConfigured=True,
                                       estimateUsd=0, fullEstimateUsd=0, chunksTotal=max(1, len(chs)), chunksCached=0,
                                       characters=chars, estimatedDurationSeconds=DURATION, chapters=[],
                                       budget=dict(allowed=True)))
        if p == "/v1/audio/speech": return self.send(200, MP3, "audio/mpeg")
        m = re.fullmatch(r"/v1/jobs/([^/]+)/(cancel|retry)", p)
        if m and m.group(1) in JOBS: return self.send(200, job_json(JOBS[m.group(1)]))
        self.send(404, {"error": "not found"})

if __name__ == "__main__":
    ap = argparse.ArgumentParser(); ap.add_argument("--port", type=int, default=8090)
    ap.add_argument("--log"); ap.add_argument("--no-default-feed", action="store_true")
    a = ap.parse_args(); LOGF = a.log; NO_DEFAULT_FEED = a.no_default_feed
    ThreadingHTTPServer(("127.0.0.1", a.port), H).serve_forever()
