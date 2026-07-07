#!/usr/bin/env python3
"""
E2E gate: ParkMode=Corner — the parked browser is an INTENTIONAL bottom-right tile,
background navigation NEVER steals focus, and a USER-minimized window STAYS minimized
(workspace-v7g7).

The field bugs this locks down (user's Mac, 2026-07-07):
  - the "hidden" window was an OS-clamped sliver at the screen edge ("looks like a mistake");
  - opening a new site popped Chromium to the foreground;
  - a manual minimize was undone moments later (every prefetch park normalized the window).

Under Xvfb + openbox (a real WM — REQUIRED here: minimize and activation semantics need one),
with a stand-in terminal window T holding focus, driven end-to-end through the real TUI:

  1. CORNER   — the browser window sits fully on-screen, flush against the bottom-right
                corner (xdotool geometry + screenshot luma: bottom-right bright, top-right
                empty). No sliver: the window may not hang off any screen edge.
  2. NO STEAL — while stories are opened and the ~30-link prefetch churns (dozens of park
                cycles), the ACTIVE window never stops being T, and the tile never moves.
  3. MINIMIZE — after the user (simulation) minimizes the browser, opening another story
                still works (content renders in the TUI) while the window STAYS minimized
                and focus stays on T — the exact inverse of the field repro.

Needs: Xvfb, openbox, xdotool, ffmpeg, tmux, a Release build.
"""
import datetime
import glob
import http.server
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402

DISPLAY = ":97"
SCREEN_W, SCREEN_H = 1600, 900
TILE_W, TILE_H, MARGIN = 800, 600, 8
DECOR_TOLERANCE = 48  # WM frame/titlebar slack around the CDP-requested rect
OUT_DIR = "/workspace/output/corner-park-e2e"
CHROME = glob.glob(os.path.expanduser("~/.cache/ms-playwright/chromium-*/chrome-linux/chrome"))


class Site(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if "/story-" in self.path:
            n = self.path.split("-")[-1].rstrip("/")
            words = ["harbor", "granite", "meadow", "lantern", "compass", "thicket",
                     "ember", "current", "signal", "marble", "drift", "anchor"]
            body = f"<h1>Story {n}</h1>" + "".join(
                f"<p>{words[i % len(words)].capitalize()} reporting in paragraph {i} of story {n} "
                f"explores the {words[(i + 3) % len(words)]} question while residents recall the "
                f"{words[(i + 7) % len(words)]} years with measured detail across interviews this "
                f"{words[(i + 5) % len(words)]} season and the {words[(i + 2) % len(words)]} committee "
                f"weighed the {words[(i + 9) % len(words)]} proposal against the {words[(i + 4) % len(words)]} "
                f"budget while observers documented every development for the public record.</p>"
                for i in range(1, 16))
            html = f"<!DOCTYPE html><html><head><title>Story {n}</title></head><body><article>{body}</article></body></html>"
        else:
            links = "".join(
                f'<div style="height:120px"><a href="/story-{i}">Headline number {i} about topic {i}</a></div>'
                for i in range(1, 31))
            html = ("<!DOCTYPE html><html><head><title>Corner Gazette</title></head>"
                    f"<body><h1>Corner Gazette</h1>{links}</body></html>")
        data = html.encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


def x_screenshot(name, env):
    os.makedirs(OUT_DIR, exist_ok=True)
    path = os.path.join(OUT_DIR, name)
    subprocess.run(
        ["ffmpeg", "-y", "-loglevel", "error", "-f", "x11grab",
         "-video_size", f"{SCREEN_W}x{SCREEN_H}", "-i", DISPLAY,
         "-frames:v", "1", path],
        check=True, env=env)
    return path


def region_luma(png, x, y, w, h):
    out = subprocess.run(
        ["ffmpeg", "-i", png, "-vf",
         f"crop={w}:{h}:{x}:{y},signalstats,metadata=print", "-f", "null", "-"],
        capture_output=True, text=True)
    m = re.search(r"YAVG[=:]([0-9.]+)", out.stderr)
    assert m, out.stderr[-1000:]
    return float(m.group(1))


def main():
    if not CHROME:
        print("SKIP: no playwright chromium found for the stand-in window")
        return 0
    if shutil.which("openbox") is None:
        print("SKIP-WARNING: openbox not installed — this gate needs a real WM for minimize/"
              "activation semantics. Install openbox to gate the corner park.")
        return 0
    chrome = CHROME[-1]

    for lk in glob.glob(f"/tmp/.X{DISPLAY.lstrip(':')}-lock"):
        try:
            os.remove(lk)
        except OSError:
            pass

    xvfb = subprocess.Popen(["Xvfb", DISPLAY, "-screen", "0", f"{SCREEN_W}x{SCREEN_H}x24"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.5)
    env = {**os.environ, "DISPLAY": DISPLAY}
    ob = subprocess.Popen(["openbox"], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.0)

    def search(name):
        r = subprocess.run(["xdotool", "search", "--name", name], env=env, capture_output=True, text=True)
        ids = [x for x in r.stdout.split() if x.strip()]
        return ids[-1] if ids else None

    def activate(wid):
        subprocess.run(["xdotool", "windowactivate", "--sync", wid], env=env, capture_output=True)
        time.sleep(0.8)

    def active():
        return subprocess.run(["xdotool", "getactivewindow"], env=env,
                              capture_output=True, text=True).stdout.strip()

    def geometry(wid):
        r = subprocess.run(["xdotool", "getwindowgeometry", "--shell", wid], env=env,
                           capture_output=True, text=True)
        vals = dict(line.split("=") for line in r.stdout.split() if "=" in line)
        return (int(vals.get("X", -99999)), int(vals.get("Y", -99999)),
                int(vals.get("WIDTH", 0)), int(vals.get("HEIGHT", 0)))

    def visible_chromium_ids():
        r = subprocess.run(["xdotool", "search", "--onlyvisible", "--class", "chrom"],
                           env=env, capture_output=True, text=True)
        return {x for x in r.stdout.split() if x.strip()}

    def is_iconified(wid):
        # openbox unmaps an iconified window, so it drops out of the --onlyvisible set.
        return wid not in visible_chromium_ids()

    # Stand-in terminal: small window pinned top-left so the bottom-right corner is free.
    term_proc = subprocess.Popen(
        [chrome, f"--user-data-dir={tempfile.mkdtemp()}", "--no-sandbox", "--disable-gpu",
         "--no-first-run", "--window-position=0,0", "--window-size=400,300",
         "--app=data:text/html,<title>WC_TERMINAL_STANDIN</title><body style='background:%23204020'>"],
        env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(6)
    T = search("WC_TERMINAL_STANDIN")
    print(f"stand-in terminal T={T}")
    if not T:
        print("FAIL: could not create the stand-in terminal window")
        return 1

    data_home = tempfile.mkdtemp(prefix="wirecopy-corner-e2e-")
    os.environ["DISPLAY"] = DISPLAY
    os.environ["XDG_DATA_HOME"] = data_home
    os.environ["Browser__ParkMode"] = "Corner"
    os.environ.pop("Browser__Sidecar", None)  # parked (non-dock) mode — the field configuration
    os.environ["WINDOWID"] = T
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/corner-e2e-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Site)
    port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()

    # File-log window for THIS run (the app logs to /workspace/logs/wirecopy-<day>.log).
    log_path = f"/workspace/logs/wirecopy-{datetime.date.today():%Y%m%d}.log"
    log_offset = os.path.getsize(log_path) if os.path.exists(log_path) else 0

    failures = []
    samples = []
    stop_sampling = threading.Event()

    def sampler():
        while not stop_sampling.is_set():
            samples.append(active())
            time.sleep(0.2)

    try:
        with TermTest(url=f"http://127.0.0.1:{port}/", width=150, height=40) as t:
            t.wait_for("Headline number", timeout=90)
            time.sleep(4)  # let the early/end-of-launch corner park settle

            B = search("Corner Gazette")
            print(f"WireCopy browser window B={B}")
            if not B:
                print(t.capture())
                print("FAIL: could not locate WireCopy's browser window")
                return 1

            # ---- 1. CORNER: fully on-screen, flush bottom-right, visibly rendering there ----
            x, y, w, h = geometry(B)
            right, bottom = x + w, y + h
            print(f"[corner] geometry: {w}x{h} at ({x},{y}) -> right={right} bottom={bottom} "
                  f"(screen {SCREEN_W}x{SCREEN_H}, margin {MARGIN})")
            if x < 0 or y < 0 or right > SCREEN_W or bottom > SCREEN_H:
                failures.append(f"corner: window hangs off-screen (({x},{y}) {w}x{h}) — the sliver bug")
            if right < SCREEN_W - MARGIN - DECOR_TOLERANCE or bottom < SCREEN_H - MARGIN - DECOR_TOLERANCE:
                failures.append(
                    f"corner: window is not flush in the bottom-right (right={right} bottom={bottom}, "
                    f"want ≥ {SCREEN_W - MARGIN - DECOR_TOLERANCE}/{SCREEN_H - MARGIN - DECOR_TOLERANCE})")

            shot = x_screenshot("corner-01-placed.png", env)
            tile_luma = region_luma(shot, SCREEN_W - 400, SCREEN_H - 300, 400, 300)
            empty_luma = region_luma(shot, SCREEN_W - 400, 0, 400, 300)
            print(f"[corner] luma bottom-right={tile_luma:.1f} (want bright), top-right={empty_luma:.1f} (want empty)")
            if tile_luma < 50:
                failures.append(f"corner: bottom-right corner is not visibly rendering (luma {tile_luma:.1f})")
            if empty_luma > 45:
                failures.append(f"corner: top-right should be empty desktop but is bright (luma {empty_luma:.1f}) "
                                "— the window is not where it should be")

            # ---- 2. NO STEAL: open stories while prefetch churns; focus must stay on T ----
            activate(T)
            if active() != T:
                failures.append("setup: could not give the stand-in terminal focus")
            sampler_thread = threading.Thread(target=sampler, daemon=True)
            sampler_thread.start()

            t.send_keys("j", "j", "Enter")
            try:
                t.wait_for("Story", timeout=30)
            except TimeoutError:
                failures.append("no-steal: first story did not render")
            time.sleep(2)
            t.send_keys("BSpace")
            t.wait_for("Headline number", timeout=30)
            t.send_keys("j", "Enter")
            try:
                t.wait_for("Story", timeout=30)
            except TimeoutError:
                failures.append("no-steal: second story did not render")
            time.sleep(15)  # let the ~30-link prefetch cycle parks in the background

            stop_sampling.set()
            sampler_thread.join(timeout=3)
            stolen = sorted({s for s in samples if s and s != T})
            print(f"[no-steal] {len(samples)} focus samples during navigation+prefetch; "
                  f"foreign actives: {stolen or 'none'}")
            if stolen:
                failures.append(
                    f"no-steal: focus left the terminal during background navigation "
                    f"(active became {stolen}, want only T={T}) — the field focus-steal")

            x2, y2, w2, h2 = geometry(B)
            print(f"[no-steal] geometry after churn: {w2}x{h2} at ({x2},{y2})")
            if abs(x2 - x) > 2 or abs(y2 - y) > 2 or w2 != w or h2 != h:
                failures.append(
                    f"no-steal: the parked tile wandered during background parks "
                    f"(({x},{y}) {w}x{h} -> ({x2},{y2}) {w2}x{h2})")
            x_screenshot("corner-02-after-churn.png", env)

            # ---- 3. MINIMIZE: a user minimize survives new-site opens; the app keeps working ----
            subprocess.run(["xdotool", "windowminimize", "--sync", B], env=env, capture_output=True)
            time.sleep(1.5)
            if not is_iconified(B):
                failures.append("minimize-setup: xdotool windowminimize did not iconify the window")
            activate(T)

            t.send_keys("BSpace")
            t.wait_for("Headline number", timeout=30)
            t.send_keys("j", "j", "j", "Enter")
            story_rendered = True
            try:
                t.wait_for("Story", timeout=45)
            except TimeoutError:
                story_rendered = False
                failures.append("minimize: story did not render while the browser was minimized "
                                "(the app must keep working)")
            time.sleep(10)  # several prefetch parks — each used to deminiaturize the window

            still_iconified = is_iconified(B)
            print(f"[minimize] story rendered={story_rendered}; still iconified={still_iconified}; "
                  f"active={active()} (want T={T})")
            if not still_iconified:
                failures.append("minimize: the window came back out of the user's minimize — "
                                "background parks are still fighting the user")
            if active() != T:
                failures.append(f"minimize: focus was stolen while minimized (active={active()}, want T={T})")
            x_screenshot("corner-03-minimized.png", env)
    finally:
        server.shutdown()
        stop_sampling.set()
        for p in (term_proc, ob, xvfb):
            try:
                p.terminate()
            except Exception:
                pass
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        shutil.rmtree(data_home, ignore_errors=True)

    # ---- Log asserts: the corner park ran and prefetch used the QUIET tab creation ----
    log_tail = ""
    if os.path.exists(log_path):
        with open(log_path, errors="replace") as fh:
            fh.seek(log_offset)
            log_tail = fh.read()
    quiet = "Created background tab quietly via CDP" in log_tail
    fallback = "fallback NewPageAsync (activating)" in log_tail
    placed_log = "Corner park placed:" in log_tail
    print(f"[log] corner placement logged={placed_log}; quiet bg tab={quiet}; activating fallback={fallback}")
    if not placed_log:
        failures.append("log: no 'Corner park placed' entry — the corner park never ran")
    if not quiet:
        failures.append("log: prefetch never created its tab via the quiet CDP path "
                        "(did prefetch run at all?)")
    if fallback:
        failures.append("log: the ACTIVATING NewPageAsync fallback ran — quiet creation failed")

    print()
    if failures:
        print("CORNER-PARK E2E FAILED:")
        for f in failures:
            print(f"  - {f}")
        return 1
    print(f"CORNER-PARK E2E PASSED — tile flush bottom-right, zero focus transitions during "
          f"navigation+prefetch, user minimize honored. Screenshots in {OUT_DIR}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
