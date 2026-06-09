#!/usr/bin/env python3
"""
Live e2e for the sidecar (workspace-exbz): runs the REAL app in tmux against a
local link-list site with a real Xvfb display, and proves frame by frame that:

  1. Opening a page auto-engages the sidecar (status: 'Sidecar open'), with the
     headed Chromium window VISIBLY occupying the RIGHT half of the X display
     (verified by luma analysis of an x11grab screenshot — bare Xvfb is black,
     so the only bright pixels are the browser window).
  2. The app renders into the LEFT columns only (right columns blanked).
  3. 'O' switches to the immersive view (full-width app) and back.
  4. A revisit served from cache keeps the sidecar engaged.

Usage:  python3 scripts/test_sidecar_e2e.py
Needs: tmux, Xvfb, ffmpeg, a Release build of WireCopy.API.
"""

import http.server
import os
import re
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402

DISPLAY = ":99"
SCREEN_W, SCREEN_H = 1600, 900
PORT = 0  # ephemeral — resolved after bind
OUT_DIR = "/workspace/output/sidecar-e2e"
TERM_W = 150


class Site(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith("/story-"):
            n = self.path.split("-")[-1].rstrip("/")
            body = f"<h1>Story {n}</h1>" + "".join(
                f"<p>Paragraph {i} of story {n}. " + ("Lorem ipsum dolor sit amet. " * 8) + "</p>"
                for i in range(1, 15))
            html = f"<!DOCTYPE html><html><head><title>Story {n}</title></head><body><article>{body}</article></body></html>"
        else:
            links = "".join(
                f'<div style="height:120px"><a href="/story-{i}">Headline number {i} about topic {i}</a></div>'
                for i in range(1, 31))
            html = f"<!DOCTYPE html><html><head><title>Sidecar Gazette</title></head><body><h1>Sidecar Gazette</h1>{links}</body></html>"
        data = html.encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


def x_screenshot(name):
    """Grab the X display to a PNG; returns the path."""
    path = os.path.join(OUT_DIR, name)
    subprocess.run(
        ["ffmpeg", "-y", "-loglevel", "error", "-f", "x11grab",
         "-video_size", f"{SCREEN_W}x{SCREEN_H}", "-i", DISPLAY,
         "-frames:v", "1", path],
        check=True, env={**os.environ, "DISPLAY": DISPLAY})
    return path


def half_luma(png, half):
    """Average luma (YAVG) of the left or right half of a screenshot."""
    x = 0 if half == "left" else SCREEN_W // 2
    out = subprocess.run(
        ["ffmpeg", "-i", png, "-vf",
         f"crop={SCREEN_W // 2}:{SCREEN_H}:{x}:0,signalstats,metadata=print",
         "-f", "null", "-"],
        capture_output=True, text=True)
    m = re.search(r"YAVG[=:]([0-9.]+)", out.stderr)
    assert m, f"no YAVG in ffmpeg output:\n{out.stderr[-2000:]}"
    return float(m.group(1))


def content_right_edge(lines):
    """Rightmost non-blank column over the app's content lines."""
    return max((len(line.rstrip()) for line in lines), default=0)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    # CRITICAL: run our tmux session on a PRIVATE tmux server. The developer (or an
    # AI agent…) may be running inside tmux themselves — a kill-server / new-session
    # on the default socket would tear down THEIR session. TMUX_TMPDIR isolates every
    # tmux call termtest.py makes; popping TMUX lets new-session run from inside tmux.
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/sidecar-e2e-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)  # private server only

    # Clear a stale X lock from a crashed run (only when no server owns it).
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
    server = http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Site)
    port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()
    os.environ["DISPLAY"] = DISPLAY

    failures = []
    try:
        with TermTest(url=f"http://127.0.0.1:{port}/", width=TERM_W, height=40) as t:
            # --- 1. Page opens, sidecar auto-engages ---
            t.wait_for("Headline number", timeout=60)
            print("page loaded; waiting for sidecar engage (headed launch can take a while)…")
            # The persistent status-bar affordance ('⇉ docked') is the reliable signal;
            # the transient 'Sidecar open' hint can expire between polls.
            t.wait_for("docked", timeout=90)
            time.sleep(2)  # let the dock + first spotlight sync settle
            screen = t.screenshot("sidecar engaged (link list)")

            # --- 2. App confined to the LEFT columns ---
            content = [l for l in screen.split("\n") if "Headline number" in l]
            edge = content_right_edge(content)
            print(f"content right edge = col {edge} (terminal {TERM_W} cols)")
            if edge > TERM_W // 2 + 8:
                failures.append(f"docked render not confined to left half (edge col {edge})")

            # --- 3. Browser window visibly on the RIGHT half of the X screen ---
            shot = x_screenshot("01-docked.png")
            left, right = half_luma(shot, "left"), half_luma(shot, "right")
            print(f"X display luma: left={left:.1f} right={right:.1f}  ({shot})")
            if not (right > 60 and right > left + 30):
                failures.append(
                    f"browser window not visible on the right half (left={left:.1f}, right={right:.1f})")

            # --- 4. Selection moves still work; spotlight follows (visual artifact) ---
            t.send_keys("j", "j", "j")
            time.sleep(2)
            x_screenshot("02-selection-follow.png")

            # --- 5. 'O' -> immersive: full width back ---
            t.send_keys("O")
            t.wait_until_gone("docked", timeout=15)
            time.sleep(1)
            screen = t.screenshot("immersive")
            content = [l for l in screen.split("\n") if "Headline number" in l]
            edge = content_right_edge(content)
            print(f"immersive content right edge = col {edge}")
            if edge <= TERM_W // 2 + 8:
                failures.append(f"immersive view did not restore full width (edge col {edge})")

            # --- 6. 'O' -> sidecar back ---
            t.send_keys("O")
            t.wait_for("docked", timeout=30)
            time.sleep(1)
            x_screenshot("03-redocked.png")

            # --- 7. Open a story (live), back to the CACHED link list: sidecar persists ---
            t.send_keys("j", "Enter")
            t.wait_for("Story", timeout=30)
            time.sleep(2)
            screen = t.screenshot("reader view (docked)")
            # Status-bar text truncates at docked width — the layout is the real signal.
            reader = [l for l in screen.split("\n") if "Paragraph" in l or "Lorem" in l]
            edge = content_right_edge(reader)
            if edge > TERM_W // 2 + 8:
                failures.append(f"reader view not docked-width after opening a story (edge col {edge})")
            x_screenshot("04-reader.png")

            t.send_keys("BSpace")  # back -> link list, served from cache
            t.wait_for("Headline number", timeout=30)
            time.sleep(2)
            screen = t.screenshot("back to cached link list")
            content = [l for l in screen.split("\n") if "Headline number" in l]
            edge = content_right_edge(content)
            if edge > TERM_W // 2 + 8:
                failures.append(f"cache revisit lost the docked layout (edge col {edge})")
            shot = x_screenshot("05-cached-revisit.png")
            left, right = half_luma(shot, "left"), half_luma(shot, "right")
            print(f"after cached revisit: luma left={left:.1f} right={right:.1f}")
            if not (right > 60 and right > left + 30):
                failures.append(
                    f"browser window gone after cached revisit (left={left:.1f}, right={right:.1f})")
    finally:
        server.shutdown()
        xvfb.terminate()
        subprocess.run(["tmux", "kill-server"], capture_output=True)  # private server only

    print()
    if failures:
        print("SIDECAR E2E FAILED:")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)
    print(f"SIDECAR E2E PASSED — screenshots in {OUT_DIR}")


if __name__ == "__main__":
    main()
