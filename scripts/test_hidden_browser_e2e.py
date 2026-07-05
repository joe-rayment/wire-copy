#!/usr/bin/env python3
"""
E2E gate: the parked/dismissed browser is TRULY HIDDEN, and re-dock shows the
CURRENT live page (workspace-ynn9).

The bug this locks down: "hidden" was a coordinate-only park (move to -32000).
Under a real window manager that "keeps windows on screen", that coordinate is
clamped back on-screen as a stray tile ("loads on the left, takes part of the
screen, looks cheap"). The fix ALSO iconifies the window on non-macOS.

Two scenarios, both under Xvfb + openbox (a real WM):

  A. CLAMP REGRESSION (the actual bug) — Browser__ParkCoordinate=0 makes the
     off-screen move land at (0,0), exactly what a clamping WM does. Only the
     minimize can hide it here, so this is the scenario that fails on the old
     code and needs a real WM (openbox) to be meaningful.
  B. HONORED OFF-SCREEN — the default park coordinate; the move hides it and the
     minimize is belt-and-suspenders. Runs with or without a WM.

Each scenario asserts, driven end-to-end through the real TUI:
  DOCKED   -> dock strip BRIGHT (window mapped)
  LIVE     -> opening a story changes the docked pixels (live render, not frozen)
  PARKED   -> whole screen DARK  (THE FIX: truly hidden, no stray tile)
  RE-DOCK  -> dock strip BRIGHT again and NOT the black parked frame (fresh)

Needs: Xvfb, openbox (for scenario A), ffmpeg, tmux, a Release build.
"""
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

DISPLAY = ":99"
SCREEN_W, SCREEN_H = 1600, 900
OUT_DIR = "/workspace/output/hidden-e2e"
DOCK_W = 430
HAVE_OPENBOX = shutil.which("openbox") is not None


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
            html = f"<!DOCTYPE html><html><head><title>Hidden Gazette</title></head><body><h1>Hidden Gazette</h1>{links}</body></html>"
        data = html.encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


def x_screenshot(name):
    os.makedirs(OUT_DIR, exist_ok=True)
    path = os.path.join(OUT_DIR, name)
    subprocess.run(
        ["ffmpeg", "-y", "-loglevel", "error", "-f", "x11grab",
         "-video_size", f"{SCREEN_W}x{SCREEN_H}", "-i", DISPLAY,
         "-frames:v", "1", path],
        check=True, env={**os.environ, "DISPLAY": DISPLAY})
    return path


def strip_luma(png, x, w):
    out = subprocess.run(
        ["ffmpeg", "-i", png, "-vf",
         f"crop={w}:{SCREEN_H}:{x}:0,signalstats,metadata=print", "-f", "null", "-"],
        capture_output=True, text=True)
    m = re.search(r"YAVG[=:]([0-9.]+)", out.stderr)
    assert m, out.stderr[-1000:]
    return float(m.group(1))


def brightest_strip(png, w=DOCK_W, step=80):
    return max((strip_luma(png, x, w) for x in range(0, SCREEN_W - w, step)), default=0.0)


def psnr(png_a, png_b):
    """Average PSNR between two frames; low => very different content, inf => identical."""
    out = subprocess.run(
        ["ffmpeg", "-i", png_a, "-i", png_b, "-lavfi", "psnr", "-f", "null", "-"],
        capture_output=True, text=True)
    m = re.search(r"average:([0-9.]+|inf)", out.stderr)
    if not m:
        return 0.0
    return float("inf") if m.group(1) == "inf" else float(m.group(1))


def run_scenario(name, port, park_coordinate, failures):
    """One dock -> live -> park -> re-dock cycle; appends any failures."""
    tag = name
    right_x = SCREEN_W - DOCK_W
    with TermTest(url=f"http://127.0.0.1:{port}/", width=150, height=40) as t:
        t.wait_for("Headline number", timeout=90)
        t.wait_for("docked", timeout=90)
        time.sleep(3)

        docked = x_screenshot(f"{tag}-01-docked.png")
        docked_luma = strip_luma(docked, right_x, DOCK_W)
        print(f"[{tag}] DOCKED right-strip luma = {docked_luma:.1f}")
        if docked_luma < 50:
            failures.append(f"{tag}: docked window not visible (luma {docked_luma:.1f})")

        # Open a story: the docked lens must re-render to the story (live, not frozen).
        t.send_keys("j", "j", "Enter")
        try:
            t.wait_for("Story", timeout=30)
        except TimeoutError:
            pass
        time.sleep(3)
        story = x_screenshot(f"{tag}-02-story.png")
        live_delta = psnr(docked, story)
        print(f"[{tag}] LIVE docked->story PSNR = {live_delta:.2f} (low = content changed)")
        if live_delta > 25:
            failures.append(f"{tag}: docked view did not change when opening a story (PSNR {live_delta:.1f}) — frozen frame")
        t.send_keys("BSpace")
        t.wait_for("Headline number", timeout=30)
        time.sleep(2)

        # Dismiss: THE HIDE. Nothing may remain on-screen anywhere.
        t.send_keys("|")
        t.wait_until_gone("docked", timeout=20)
        time.sleep(3)
        parked = x_screenshot(f"{tag}-03-parked.png")
        parked_bright = brightest_strip(parked)
        print(f"[{tag}] PARKED brightest strip anywhere = {parked_bright:.1f}")
        if parked_bright > 45:
            failures.append(
                f"{tag}: LEAK — parked browser visible on-screen (brightest strip {parked_bright:.1f})")

        # Re-dock: bright again and a REAL frame (not the black parked one).
        t.send_keys("|")
        t.wait_for("docked", timeout=30)
        time.sleep(3)
        redock = x_screenshot(f"{tag}-04-redock.png")
        redock_luma = strip_luma(redock, right_x, DOCK_W)
        redock_vs_parked = psnr(redock, parked)
        print(f"[{tag}] RE-DOCK right-strip luma = {redock_luma:.1f}; vs-parked PSNR = {redock_vs_parked:.2f}")
        if redock_luma < 50:
            failures.append(f"{tag}: re-dock did not restore a visible window (luma {redock_luma:.1f})")
        if redock_vs_parked > 25:
            failures.append(f"{tag}: re-dock frame identical to the black parked frame (PSNR {redock_vs_parked:.1f}) — stale/black")


def main():
    lock = f"/tmp/.X{DISPLAY.lstrip(':')}-lock"
    if os.path.exists(lock):
        try:
            with open(lock) as fh:
                os.kill(int(fh.read().strip()), 0)
        except (OSError, ValueError):
            os.remove(lock)

    xvfb = subprocess.Popen(
        ["Xvfb", DISPLAY, "-screen", "0", f"{SCREEN_W}x{SCREEN_H}x24"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.5)
    os.environ["DISPLAY"] = DISPLAY
    ob = None
    if HAVE_OPENBOX:
        ob = subprocess.Popen(["openbox"], env={**os.environ, "DISPLAY": DISPLAY},
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(1.5)
        print("openbox running (real WM)")
    else:
        print("WARNING: openbox not installed — the CLAMP-REGRESSION scenario (A) is SKIPPED; "
              "only the honored-off-screen scenario (B) runs. Install openbox to gate the actual bug.")

    data_home = tempfile.mkdtemp(prefix="wirecopy-hidden-e2e-")
    os.environ["XDG_DATA_HOME"] = data_home
    os.environ["Browser__Sidecar"] = "true"
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/hidden-e2e-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Site)
    port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()

    failures = []
    try:
        # Scenario A — clamp regression (needs a WM to honor the iconify).
        if HAVE_OPENBOX:
            os.environ["Browser__ParkCoordinate"] = "0"   # simulate a WM that clamps to (0,0)
            run_scenario("clamp", port, 0, failures)
            os.environ.pop("Browser__ParkCoordinate", None)
            subprocess.run(["tmux", "kill-server"], capture_output=True)

        # Scenario B — honored off-screen park (default coordinate).
        run_scenario("offscreen", port, -32000, failures)
    finally:
        server.shutdown()
        if ob:
            ob.terminate()
        xvfb.terminate()
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        shutil.rmtree(data_home, ignore_errors=True)

    print()
    if failures:
        print("HIDDEN-BROWSER E2E FAILED:")
        for f in failures:
            print(f"  - {f}")
        return 1
    print(f"HIDDEN-BROWSER E2E PASSED — screenshots in {OUT_DIR}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
