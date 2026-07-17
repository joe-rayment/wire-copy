#!/usr/bin/env python3
"""workspace-42q8.8 — REAL end-to-end for the g s schedule flow.

(1) g s on a live-served, UNCONFIGURED, auto-grouped fixture page (deterministic
    local content; real-length articles) creates the recipe from the page itself:
    the card's pre-filled cursor section -> top story -> New schedule. This walks
    the 42q8 path end-to-end: auto-derived section config, whole in-flow entry.
(2) Runs it via the non-interactive `run-recipe` verb: REAL article extraction,
    REAL OpenAI TTS, REAL GCS publish through the unchanged frpl pipeline.
(3) Asserts the published episode: feed.xml HEADs 200, first enclosure HEADs 200,
    ffprobe shows non-zero duration/size on the local M4B.

GATED on a configured GCS bucket + OpenAI key (real cost, one short article).
Evidence -> docs/qa/workspace-42q8.8/. The app is never-headless: the gate owns
an Xvfb display.
"""
import glob
import http.server
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
os.environ.setdefault("WIRECOPY_DLL", "src/WireCopy.API/bin/Debug/net10.0/WireCopy.API.dll")
from termtest import TermTest  # noqa: E402
from test_podcast_e2e_real import verify_feed, verify_first_audio_in_feed, verify_audio, get_bucket_name  # noqa: E402

DATA = os.path.expanduser("~/.local/share/WireCopy")
DB = os.path.join(DATA, "wirecopy.db")
HIERARCHY_DIR = os.path.join(DATA, "hierarchy")
DLL = os.path.join("/workspace", "src/WireCopy.API/bin/Debug/net10.0/WireCopy.API.dll")
DOTNET = os.path.join("/workspace", "dotnet")
QA = "/workspace/docs/qa/workspace-42q8.8"
os.makedirs(QA, exist_ok=True)
RES = {"bead": "workspace-42q8.8", "assertions": []}

PAGE_TITLE = "Evening Fixture Dispatch"

# ---- fixture content: index page with two DOM-detected sections, and articles
# long enough to clear extraction's quality floor (>500 words, varied openings,
# substantial paragraphs).

OPENERS = ["Officials", "Residents", "Analysts", "Engineers", "Historians", "Volunteers",
           "Planners", "Researchers", "Farmers", "Curators", "Teachers", "Sailors"]


def article_html(title):
    paras = []
    for i, opener in enumerate(OPENERS):
        paras.append(
            f"<p>{opener} described the development in careful detail on Thursday, noting that the "
            f"changes had been years in the making and would reshape how the region approaches the "
            f"question for a generation. The report runs to more than two hundred pages and draws on "
            f"interviews conducted across fourteen towns, with findings that surprised even the "
            f"committee that commissioned it in the first place, part {i + 1} of the series.</p>")
    body = "\n".join(paras)
    return (f"<!doctype html><html><head><title>{title}</title></head><body>"
            f"<article><h1>{title}</h1>{body}</article></body></html>").encode()


WORLD = ["Global summit reaches accord on trade terms",
         "Coastal cities brace for the spring flood season",
         "Election observers arrive ahead of the vote"]
BIZ = ["Chipmaker posts record quarterly earnings again",
       "Retail giant expands same-day delivery network",
       "Startups chase the compact fusion power prize"]


def index_html():
    def items(prefix, names):
        return "\n".join(f'<li><a href="/{prefix}/{i}.html">{n}</a></li>'
                         for i, n in enumerate(names, 1))
    return (f"<!doctype html><html><head><title>{PAGE_TITLE}</title></head><body>"
            f"<h1>{PAGE_TITLE}</h1>"
            f"<section><h2>World</h2><ul>{items('world', WORLD)}</ul></section>"
            f"<section><h2>Business</h2><ul>{items('biz', BIZ)}</ul></section>"
            f"</body></html>").encode()


def serve():
    idx = index_html()
    world = {f"/world/{i}.html": article_html(n) for i, n in enumerate(WORLD, 1)}
    biz = {f"/biz/{i}.html": article_html(n) for i, n in enumerate(BIZ, 1)}
    pages = {"/index.html": idx, "/": idx, **world, **biz}

    class H(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            body = pages.get(self.path)
            if body is None:
                self.send_response(404)
                self.end_headers()
                return
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *a):
            pass

    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv, srv.server_address[1]


def chk(label, ok, detail=""):
    RES["assertions"].append({"label": label, "pass": bool(ok), "detail": str(detail)[:300]})
    print(("PASS" if ok else "FAIL"), "—", label, ("" if ok else f"  [{detail}]"), flush=True)
    return bool(ok)


def selected_line(screen):
    for line in screen.splitlines():
        if "▸" in line:
            return line
    return ""


def select_option(t, substr, max_moves=40):
    for _ in range(max_moves):
        if substr.lower() in selected_line(t.capture()).lower():
            return True
        t.send_keys("j")
        time.sleep(0.25)
    return substr.lower() in selected_line(t.capture()).lower()


def type_field(t, text, clear_first=False):
    if clear_first:
        for _ in range(50):
            t.send_keys("BSpace", delay=0.02)
    if text:
        t.send_text(text)
    time.sleep(0.3)
    t.send_keys("Enter")
    time.sleep(0.9)


def clear_browser_locks():
    os.system("pkill -9 -f WireCopy.API >/dev/null 2>&1; pkill -9 -f chrome >/dev/null 2>&1")
    time.sleep(1.5)
    prof = os.path.join(DATA, "browser-profile")
    for f in ("SingletonCookie", "SingletonLock", "SingletonSocket"):
        p = os.path.join(prof, f)
        try:
            if os.path.exists(p) or os.path.islink(p):
                os.remove(p)
        except OSError:
            pass


def create_recipe(t):
    time.sleep(6)  # settle first render
    for attempt in range(5):
        t.send_keys("g")
        time.sleep(0.6)
        t.send_keys("s")
        time.sleep(2.5)
        s = t.capture()
        if "Add to schedule" in s or "How many stories?" in s:
            break
        time.sleep(3)
    s = t.capture()
    if "Add to schedule" not in s and "How many stories?" not in s:
        print("---- SCREEN at g s failure ----")
        print(s)
        raise AssertionError("g s card did not open")
    if "Add to schedule" in s:
        t.send_keys("Enter")  # WHAT: the pre-filled cursor section (World)
        time.sleep(0.8)
    select_option(t, "top story")
    t.send_keys("Enter")
    time.sleep(0.8)
    select_option(t, "New schedule")
    t.send_keys("Enter")
    time.sleep(0.9)
    type_field(t, "GS E2E Brief", clear_first=True)
    select_option(t, "Every day")
    t.send_keys("Enter")
    time.sleep(0.6)

    # workspace-ua0c FIXED the attribution bug: the run-recipe verb now reports the
    # exact run-now row it created (IScheduleRunNow.RunAsync returns it), so a past
    # slot's 'Skipped (missed past grace)' row written by this host's scheduler tick
    # can no longer be reported instead of the real result. The future-today slot is
    # therefore NO LONGER REQUIRED for correct attribution; it is kept only so the
    # startup tick stays "not due" and never contends for the generation gate mid-run.
    # Run-now itself is user-initiated and ignores the cadence.
    now = time.localtime()
    slot = "23:59" if now.tm_hour >= 23 else f"{now.tm_hour + 1:02d}:{now.tm_min:02d}"
    type_field(t, slot, clear_first=True)
    time.sleep(1.5)


def main():
    if not get_bucket_name():
        print("SKIP: no GCS bucket configured (gated lane)")
        return 1

    # Own a display (the app dies at browser launch without one — never headless).
    d = os.environ.get("DISPLAY", "")
    num = d.lstrip(":").split(".")[0] if d.startswith(":") else ""
    if not (num and os.path.exists(f"/tmp/.X11-unix/X{num}")):
        if os.path.exists("/tmp/.X92-lock"):
            os.remove("/tmp/.X92-lock")
        subprocess.Popen(["Xvfb", ":92", "-screen", "0", "1600x900x24"],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        os.environ["DISPLAY"] = ":92"
        time.sleep(1.5)
    os.environ.pop("TMUX", None)
    os.environ.setdefault("TMUX_TMPDIR", "/tmp/42q8-sched-tmux")
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)

    srv, port = serve()
    url = f"http://127.0.0.1:{port}/index.html"
    cfg_path = os.path.join(HIERARCHY_DIR, f"127.0.0.1_{port}.json")

    # Clean slate for this recipe; keep the operator's files safe.
    backups = {}
    for name in ("schedules.json",):
        p = os.path.join(DATA, name)
        if os.path.exists(p):
            backups[p] = p + ".gs8bak"
            shutil.copy(p, backups[p])
            os.remove(p)
    con = sqlite3.connect(DB)
    con.execute("DELETE FROM ScheduledRuns")
    con.commit()
    con.close()

    all_ok = True
    try:
        # (1) Create via g s on the auto-grouped fixture.
        clear_browser_locks()
        with TermTest(url=url, width=140, height=42) as t:
            loaded = False
            for _ in range(90):
                s = t.capture()
                if "Global summit" in s and "Loading" not in s:
                    loaded = True
                    break
                time.sleep(1)
            if not chk("fixture page renders auto-grouped", loaded and "Business" in t.capture(), t.capture()[-400:]):
                return 1
            create_recipe(t)
            s = t.capture()
            with open(os.path.join(QA, "01_recipe_created.txt"), "w") as f:
                f.write(s)
            all_ok &= chk("recipe created via g s (confirmation announced)",
                          "GS E2E Brief" in s and "Added" in s, s.splitlines()[-3:])

        persisted = json.load(open(os.path.join(DATA, "schedules.json")))
        step0 = persisted["recipes"][0]["steps"][0] if persisted.get("recipes") else {}
        all_ok &= chk("persisted step pins the cursor's section (World, SingleTopStory, durable key)",
                      step0.get("sectionName") == "World" and step0.get("takeMode") == "SingleTopStory"
                      and bool(step0.get("configUrlPattern")), step0)
        all_ok &= chk("auto-derived config saved for the fixture host (World+Business)",
                      os.path.exists(cfg_path)
                      and [x["name"] for x in json.load(open(cfg_path))[0]["sections"]] == ["World", "Business"],
                      cfg_path)

        # (2) Run it for real: extraction + TTS + GCS publish via run-recipe.
        clear_browser_locks()
        env = dict(os.environ)
        env["Gcs__BucketName"] = get_bucket_name()
        sa = glob.glob("/workspace/creds/*.json")
        if sa:
            env["Gcs__ServiceAccountKeyPath"] = sa[0]
        print("[e2e] running the recipe (real generation, may take minutes)…", flush=True)
        out = subprocess.run([DOTNET, DLL, "run-recipe", "GS E2E Brief"], cwd="/workspace",
                             capture_output=True, text=True, timeout=540, env=env)
        run = None
        for line in out.stdout.splitlines():
            if line.startswith("RUN_RESULT:"):
                run = json.loads(line[len("RUN_RESULT:"):])
        print("[e2e] run result:", run, flush=True)
        json.dump(run or {"stdout_tail": out.stdout[-500:], "stderr_tail": out.stderr[-500:]},
                  open(os.path.join(QA, "02_run_result.json"), "w"), indent=2)
        all_ok &= chk("recipe ran to a successful/partial terminal status",
                      run and run.get("status") in ("Completed", "PartialSuccess"), run)

        feed_url = (run or {}).get("feedUrl")
        local_path = (run or {}).get("localPath")

        # (3) Real published artifacts.
        feed_ok, feed_info = verify_feed(feed_url) if feed_url else (False, {"error": "no feed url on the run"})
        all_ok &= chk("feed.xml HEAD returns 200", feed_ok and feed_info.get("status") == 200, feed_info)
        enc_ok, enc_info = verify_first_audio_in_feed(feed_url) if feed_url else (False, {"error": "no feed url"})
        all_ok &= chk("first enclosure HEADs 200", enc_ok and enc_info.get("status") == 200, enc_info)
        aud_ok, aud_info = verify_audio(local_path) if local_path else (False, {"error": "no local path"})
        all_ok &= chk("local M4B has non-zero duration/size (ffprobe)", aud_ok, aud_info)

        RES.update({"feed_url": feed_url, "local_path": local_path,
                    "result": "PASS" if all_ok else "FAIL"})
    finally:
        clear_browser_locks()
        for orig, bak in backups.items():
            if os.path.exists(bak):
                shutil.copy(bak, orig)
                os.remove(bak)
            elif os.path.exists(orig):
                os.remove(orig)
        if os.path.exists(cfg_path):
            os.remove(cfg_path)
        srv.shutdown()

    json.dump(RES, open(os.path.join(QA, "result.json"), "w"), indent=2)
    print("===", RES.get("result", "FAIL"), "===", flush=True)
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
