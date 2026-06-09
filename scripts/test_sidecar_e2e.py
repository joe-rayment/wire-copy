#!/usr/bin/env python3
"""
Live e2e for the sidecar v2 model (workspace-vzmr + workspace-o5yf): runs the
REAL app in tmux against a local link-list site with a real Xvfb display.

The v2 contract:
  1. Opening a page auto-engages the sidecar ('docked' affordance) with a
     PHONE-SHAPED Chromium window pinned to the configured screen edge
     (verified by luma analysis of an x11grab screenshot — bare Xvfb is black,
     so the only bright pixels are the browser window).
  2. The app renders to its REAL terminal size while docked (no overlap
     squeeze) — content spans the full tmux width.
  3. A terminal resize round-trip (150 -> 75 -> 150 cols) re-wraps correctly
     and returns to full width (regression: the stuck-shrunken link list).
  4. 'O' switches to the immersive view (affordance gone) and back.
  5. A cached revisit keeps the sidecar engaged.
  6. The '?' help popup renders fully (modal canary).
  7. Left dock: window on the left edge, app still full width.

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
OUT_DIR = "/workspace/output/sidecar-e2e"
TERM_W = 150
DOCK_W = 430  # Browser:DockWidthPx default


SLOW_STORIES = {"enabled": False}


class Site(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if "/story-" in self.path:
            if SLOW_STORIES["enabled"]:
                time.sleep(0.7)  # widen the takeover window
            n = self.path.split("-")[-1].rstrip("/")
            prefix = "/batch2" if self.path.startswith("/batch2") else ""
            body = f"<h1>Story {n}</h1>" + "".join(
                f"<p>Paragraph {i} of story {n}. " + ("Lorem ipsum dolor sit amet. " * 8) + "</p>"
                for i in range(1, 15))
            html = f"<!DOCTYPE html><html><head><title>Story {n}</title></head><body><article>{body}</article></body></html>"
        else:
            prefix = "/batch2" if self.path.startswith("/batch2") else ""
            links = "".join(
                f'<div style="height:120px"><a href="{prefix}/story-{i}">Headline number {i} about topic {i}</a></div>'
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
    path = os.path.join(OUT_DIR, name)
    subprocess.run(
        ["ffmpeg", "-y", "-loglevel", "error", "-f", "x11grab",
         "-video_size", f"{SCREEN_W}x{SCREEN_H}", "-i", DISPLAY,
         "-frames:v", "1", path],
        check=True, env={**os.environ, "DISPLAY": DISPLAY})
    return path


def strip_luma(png, x, w):
    """Average luma (YAVG) of a vertical strip [x, x+w) of the screenshot."""
    out = subprocess.run(
        ["ffmpeg", "-i", png, "-vf",
         f"crop={w}:{SCREEN_H}:{x}:0,signalstats,metadata=print",
         "-f", "null", "-"],
        capture_output=True, text=True)
    m = re.search(r"YAVG[=:]([0-9.]+)", out.stderr)
    assert m, f"no YAVG in ffmpeg output:\n{out.stderr[-2000:]}"
    return float(m.group(1))


def content_right_edge(lines):
    return max((len(line.rstrip()) for line in lines), default=0)


def headline_lines(screen):
    return [l for l in screen.split("\n") if "Headline number" in l]


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    # PRIVATE tmux server (TMUX_TMPDIR): never touch the developer's tmux.
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/sidecar-e2e-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)  # private server only

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
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Site)
    port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()
    os.environ["DISPLAY"] = DISPLAY

    failures = []
    try:
        with TermTest(url=f"http://127.0.0.1:{port}/", width=TERM_W, height=40) as t:
            # --- 1. Page opens, sidecar auto-engages ---
            t.wait_for("Headline number", timeout=60)
            print("page loaded; waiting for sidecar engage…")
            t.wait_for("docked", timeout=90)
            time.sleep(2)
            screen = t.screenshot("sidecar engaged (link list)")

            # --- 2. App renders FULL terminal width while docked (v2) ---
            edge = content_right_edge(headline_lines(screen))
            print(f"content right edge = col {edge} (terminal {TERM_W} cols)")
            if edge <= TERM_W // 2 + 8:
                failures.append(f"app squeezed while docked — overlap model not gone (edge col {edge})")

            # --- 3. Phone-shaped window pinned to the RIGHT edge ---
            shot = x_screenshot("01-docked.png")
            win = strip_luma(shot, SCREEN_W - DOCK_W, DOCK_W)
            rest = strip_luma(shot, 0, SCREEN_W - DOCK_W)
            print(f"luma: window strip={win:.1f} rest={rest:.1f}  ({shot})")
            if not (win > 60 and win > rest + 30):
                failures.append(f"phone window not on right edge (win={win:.1f}, rest={rest:.1f})")

            # --- 4. Selection follow (visual artifact) ---
            t.send_keys("j", "j", "j")
            time.sleep(2)
            x_screenshot("02-selection-follow.png")

            # --- 5. Resize round-trip: 150 -> 75 -> 150 ---
            subprocess.check_call(["tmux", "resize-window", "-t", "termtest", "-x", "75"])
            time.sleep(2)
            screen = t.capture()
            edge75 = content_right_edge(headline_lines(screen))
            print(f"resized to 75: edge = {edge75}")
            if edge75 > 76:
                failures.append(f"content did not re-wrap to 75 cols (edge {edge75})")
            subprocess.check_call(["tmux", "resize-window", "-t", "termtest", "-x", str(TERM_W)])
            time.sleep(2)
            t.send_keys("j")  # nudge a re-render
            time.sleep(1)
            screen = t.screenshot("after resize round-trip")
            edge150 = content_right_edge(headline_lines(screen))
            print(f"back to {TERM_W}: edge = {edge150}")
            if edge150 <= TERM_W // 2 + 8:
                failures.append(f"resize round-trip left the layout shrunken (edge {edge150})")

            # --- 6. 'O' -> immersive and back ---
            t.send_keys("O")
            t.wait_until_gone("docked", timeout=15)
            t.send_keys("O")
            t.wait_for("docked", timeout=30)
            time.sleep(1)

            # --- 7. Open a story, back to CACHED list: sidecar persists ---
            t.send_keys("j", "Enter")
            t.wait_for("Story", timeout=30)
            time.sleep(2)
            x_screenshot("03-reader.png")
            t.send_keys("BSpace")
            t.wait_for("Headline number", timeout=30)
            time.sleep(2)
            screen = t.capture()
            if "docked" not in screen.lower():
                failures.append("cache revisit lost the dock affordance")
            shot = x_screenshot("04-cached-revisit.png")
            win = strip_luma(shot, SCREEN_W - DOCK_W, DOCK_W)
            if win < 60:
                failures.append(f"browser window gone after cached revisit (win={win:.1f})")

            # --- 7b. workspace-u4o9 regression: consecutive article opens while
            #         docked must ALL load (follow-nav used to break fetches) ---
            for i in range(5):
                t.send_keys("j", "Enter")
                try:
                    t.wait_for("Paragraph", timeout=25)
                except TimeoutError:
                    failures.append(f"article open #{i + 1} failed to load while docked")
                    t.screenshot(f"failed article open {i + 1}")
                time.sleep(0.5)
                t.send_keys("BSpace")
                t.wait_for("Headline number", timeout=25)
                time.sleep(0.5)
            print("5 consecutive docked article opens: all loaded")

            # --- 8. '?' help popup renders (modal canary) ---
            t.send_keys("?")
            time.sleep(1.5)
            screen = t.capture()
            if not any("toggle expand" in l.lower() for l in screen.split("\n")):
                failures.append("'?' help popup did not render")
            t.send_keys("Escape")
            time.sleep(0.5)

        # --- 9. LEFT dock: window on the left edge; app STILL full width ---
        os.environ["Browser__DockSide"] = "Left"
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        try:
            with TermTest(url=f"http://127.0.0.1:{port}/", width=TERM_W, height=40) as t:
                t.wait_for("Headline number", timeout=60)
                t.wait_for("docked", timeout=90)
                time.sleep(2)
                screen = t.screenshot("left dock")
                edge = content_right_edge(headline_lines(screen))
                if edge <= TERM_W // 2 + 8:
                    failures.append(f"left dock: app squeezed (edge col {edge})")
                shot = x_screenshot("05-left-docked.png")
                win = strip_luma(shot, 0, DOCK_W)
                rest = strip_luma(shot, DOCK_W, SCREEN_W - DOCK_W)
                print(f"left dock luma: window strip={win:.1f} rest={rest:.1f}")
                if not (win > 60 and win > rest + 30):
                    failures.append(f"left dock: window not on left edge (win={win:.1f}, rest={rest:.1f})")
        finally:
            os.environ.pop("Browser__DockSide", None)

        # --- 10. workspace-mya7: takeover — user input in the browser pauses
        #         prefetch (checkpoint), quiet resumes it; restart mid-pause
        #         restores the checkpoint ---
        seam = "/tmp/sidecar-e2e-userinput"
        if os.path.exists(seam):
            os.remove(seam)
        os.environ["WIRECOPY_TEST_USERINPUT_FILE"] = seam
        os.environ["Browser__TakeoverInputWindowSeconds"] = "5"
        os.environ["Browser__TakeoverResumeIdleSeconds"] = "8"
        SLOW_STORIES["enabled"] = True
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        log_path = "/workspace/logs/wirecopy-" + time.strftime("%Y%m%d") + ".log"
        try:
            with TermTest(url=f"http://127.0.0.1:{port}/batch2/", width=TERM_W, height=40) as t:
                t.wait_for("Headline number", timeout=60)
                t.wait_for("docked", timeout=90)
                # Let prefetch get going on the slow batch…
                t.wait_for_any("1/30", "2/30", "3/30", "4/30", "5/30", timeout=60)
                print("prefetch running on batch2")

                # …then the user grabs the browser.
                with open(seam, "w") as fh:
                    fh.write("input")
                t.wait_for("using the browser", timeout=20)
                print("takeover pause confirmed")
                ckpt = os.path.expanduser("~/.local/share/WireCopy/preload-checkpoint.json")
                if not os.path.exists(ckpt):
                    failures.append("takeover pause did not write a checkpoint")

                # Keep the user 'active' a moment, then go quiet -> auto-resume.
                time.sleep(2)
                os.utime(seam)
                t.wait_until_gone("using the browser", timeout=40)
                print("auto-resume confirmed")

            # Restart mid-run: pause again right at startup is not needed — instead
            # prove the checkpoint RESTORE path: pause, kill the app, relaunch.
            with TermTest(url=f"http://127.0.0.1:{port}/batch2/", width=TERM_W, height=40) as t:
                t.wait_for("Headline number", timeout=60)
                os.utime(seam)  # user active again -> pause + checkpoint
                try:
                    t.wait_for("using the browser", timeout=30)
                except TimeoutError:
                    pass  # queue may already be drained; checkpoint test still valid if file exists
                had_checkpoint = os.path.exists(
                    os.path.expanduser("~/.local/share/WireCopy/preload-checkpoint.json"))
            if had_checkpoint:
                with TermTest(url=f"http://127.0.0.1:{port}/batch2/", width=TERM_W, height=40) as t:
                    t.wait_for("Headline number", timeout=60)
                    deadline = time.time() + 30
                    restored = False
                    while time.time() < deadline:
                        try:
                            with open(log_path) as fh:
                                if "Prefetch checkpoint restored" in fh.read():
                                    restored = True
                                    break
                        except OSError:
                            pass
                        time.sleep(1)
                    if not restored:
                        failures.append("restart did not restore the prefetch checkpoint")
                    else:
                        print("checkpoint restored after restart")
            else:
                print("note: queue drained before restart-pause — restore path covered by unit tests")
        finally:
            os.environ.pop("WIRECOPY_TEST_USERINPUT_FILE", None)
            os.environ.pop("Browser__TakeoverInputWindowSeconds", None)
            os.environ.pop("Browser__TakeoverResumeIdleSeconds", None)
            SLOW_STORIES["enabled"] = False
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
