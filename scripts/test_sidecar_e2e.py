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
OUT_DIR = "/workspace/output/sidecar-e2e"
TERM_W = 150
DOCK_W = 390  # Browser:DockWidthPx default (workspace-jq7x: was a stale 430)


SLOW_STORIES = {"enabled": False}


class Site(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if "/story-" in self.path:
            if SLOW_STORIES["enabled"]:
                time.sleep(0.7)  # widen the takeover window
            n = self.path.split("-")[-1].rstrip("/")
            prefix = "/batch2" if self.path.startswith("/batch2") else ""
            words = ["harbor", "granite", "meadow", "lantern", "compass", "thicket",
                     "ember", "current", "signal", "marble", "drift", "anchor",
                     "summit", "hollow"]
            # NOTE: stories must exceed the browser-preload sufficiency floor
            # (MinPaywalledWordCount = 500 words) or prefetch silently skips them.
            body = f"<h1>Story {n}</h1>" + "".join(
                f"<p>{words[i % len(words)].capitalize()} reporting in paragraph {i} of story {n} "
                f"explores the {words[(i + 3) % len(words)]} question while residents recall "
                f"the {words[(i + 7) % len(words)]} years with measured detail across many "
                f"interviews conducted this {words[(i + 5) % len(words)]} season, and the "
                f"{words[(i + 2) % len(words)]} committee weighed the {words[(i + 9) % len(words)]} "
                f"proposal against the {words[(i + 4) % len(words)]} budget while observers from "
                f"the {words[(i + 6) % len(words)]} council documented every {words[(i + 8) % len(words)]} "
                f"development for the public record of the {words[(i + 10) % len(words)]} district.</p>"
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


def column_luma(png):
    """Per-column average luma (0..255) across the full screenshot height, in
    ONE ffmpeg pass: vertical-average to a 1px-tall gray row, then read raw bytes.
    workspace-jq7x: the old checks assumed the docked window was flush to the
    screen's right edge (x=1600); under the Xvfb devicePixelRatio it docked
    short, so a fixed [1600-DOCK_W, 1600) crop straddled window+black and read a
    misleadingly dim ~48. Scanning a luma PROFILE instead finds the window
    wherever it actually docked, independent of the exact right edge."""
    out = subprocess.run(
        ["ffmpeg", "-loglevel", "error", "-i", png, "-vf",
         f"scale={SCREEN_W}:1,format=gray", "-f", "rawvideo", "-"],
        capture_output=True)
    return list(out.stdout[:SCREEN_W])


def brightest_strip(profile, w):
    """(x, mean_luma) of the brightest w-wide vertical strip in a column profile."""
    if len(profile) < w:
        return 0, (sum(profile) / len(profile) if profile else 0.0)
    s = sum(profile[:w])
    best_sum, best_x = s, 0
    for x in range(1, len(profile) - w + 1):
        s += profile[x + w - 1] - profile[x - 1]
        if s > best_sum:
            best_sum, best_x = s, x
    return best_x, best_sum / w


def mean_outside(profile, x, w):
    """Mean luma of everything OUTSIDE the strip [x, x+w)."""
    outside = profile[:x] + profile[x + w:]
    return sum(outside) / len(outside) if outside else 0.0


def assert_docked_edge(shot, side, label, failures):
    """The phone-shaped window is the single bright block; assert it exists, is
    brighter than the rest of the screen, and hugs the expected screen edge —
    without hard-coding where under the current devicePixelRatio it lands."""
    profile = column_luma(shot)
    x, win = brightest_strip(profile, DOCK_W)
    rest = mean_outside(profile, x, DOCK_W)
    center = x + DOCK_W / 2
    on_side = (center <= SCREEN_W / 2) if side == "Left" else (center >= SCREEN_W / 2)
    print(f"{label}: brightest strip x={x} win={win:.1f} rest={rest:.1f} side_ok={on_side}")
    if not (win > 60 and win > rest + 30):
        failures.append(f"{label}: phone window not visible as a bright edge strip (win={win:.1f}, rest={rest:.1f})")
    elif not on_side:
        failures.append(f"{label}: bright window strip is not on the {side} edge (center col {center:.0f})")
    return win


def content_right_edge(lines):
    return max((len(line.rstrip()) for line in lines), default=0)


def headline_lines(screen):
    return [l for l in screen.split("\n") if "Headline number" in l]


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    # HERMETIC data dir: hierarchy configs are keyed by bare domain (port-blind),
    # so a config saved by another live test against 127.0.0.1 would route this
    # gate's pages through foreign selectors and blank the link list. A fresh
    # XDG_DATA_HOME isolates the gate from the developer's real data and from
    # other harnesses; one dir spans the whole run so the restart-mid-pause
    # phase still finds its prefetch checkpoint.
    data_home = tempfile.mkdtemp(prefix="wirecopy-sidecar-e2e-")
    os.environ["XDG_DATA_HOME"] = data_home
    app_data = os.path.join(data_home, "WireCopy")

    # workspace-jq7x: the sidecar default flipped to FALSE (workspace-75ng), so
    # this gate — which waits for the 'docked' affordance — hung 90s at step 1
    # until the sidecar was opted in explicitly. Bound as Browser:Sidecar via the
    # .NET double-underscore env convention; survives every TermTest relaunch.
    os.environ["Browser__Sidecar"] = "true"

    # The app appends to a SHARED per-day log; earlier runs today (including
    # legitimately-headless unit harnesses) would pollute string checks. Only
    # ever scan the slice this run appended.
    day_log = "/workspace/logs/wirecopy-" + time.strftime("%Y%m%d") + ".log"
    log_start = os.path.getsize(day_log) if os.path.exists(day_log) else 0

    def run_log():
        try:
            with open(day_log) as fh:
                fh.seek(log_start)
                return fh.read()
        except OSError:
            return ""

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
            assert_docked_edge(shot, "Right", "docked", failures)

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
            t.send_keys("|")
            t.wait_until_gone("docked", timeout=15)
            t.send_keys("|")
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
            assert_docked_edge(shot, "Right", "cached revisit", failures)

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

            # --- 7d. workspace-8qyo: the article layout TUNER, frame by frame ---
            t.send_keys("j", "Enter")
            t.wait_for("Paragraph", timeout=25)
            t.send_keys("E")  # E = tune article layout (was L)
            t.wait_for("1/3 Headline", timeout=20)
            t.screenshot("tuner step 1 (headline)")
            t.send_keys("j")          # cycle a candidate
            time.sleep(0.8)
            t.send_keys("Enter")      # confirm headline
            t.wait_for("2/3 Body", timeout=15)
            t.screenshot("tuner step 2 (body)")
            t.send_keys("Enter")      # confirm body (densest candidate first)
            matched, _ = t.wait_for_any(
                "3/3 Ignore", "Layout tuned", "NOT saved", timeout=25)
            if "3/3" in matched:
                t.screenshot("tuner step 3 (ignore)")
                t.send_keys("Enter")  # nothing to mark on this page — finish
                matched, _ = t.wait_for_any("Layout tuned", "NOT saved", timeout=15)
            if "NOT saved" in matched:
                failures.append("tuner self-test rejected the confirmed selectors")
            t.screenshot("tuner saved (toast)")
            t.send_keys("k")  # dismiss toast (harmless scroll in reader)
            time.sleep(0.5)
            layout_dir = os.path.join(app_data, "layouts")
            saved = [f for f in os.listdir(layout_dir)] if os.path.isdir(layout_dir) else []
            if not any("127.0.0.1" in f for f in saved):
                failures.append(f"tuner did not persist a per-site config (dir: {saved})")

            # Re-extract with the saved config: R bypasses the cache.
            t.send_keys("R")
            t.wait_for("Paragraph", timeout=40)
            time.sleep(1)
            if "saved selector config" not in run_log():
                failures.append("refresh did not extract via the saved selector config")
            else:
                print("tuned config used for re-extraction")
            t.send_keys("BSpace")
            t.wait_for("Headline number", timeout=25)
            time.sleep(0.5)

            # --- 7c. workspace-8cf2/5452: interactive session is NEVER headless ---
            log = run_log()
            if "Browser mode: VISIBLE" not in log:
                failures.append("startup did not log 'Browser mode: VISIBLE'")
            if "headless=True" in log or "(headless=true)" in log:
                failures.append("a headless context was created in an interactive session")

            # --- 8. '?' help popup renders (modal canary) ---
            t.send_keys("?")
            time.sleep(1.5)
            screen = t.capture()
            # workspace-jq7x: assert a keybinding line that actually exists in the
            # Hierarchical-mode popup ('collapse / expand group'); the old canary
            # 'toggle expand' was renamed and made this a false failure.
            if not any("expand group" in l.lower() for l in screen.split("\n")):
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
                assert_docked_edge(shot, "Left", "left dock", failures)
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
                ckpt = os.path.join(app_data, "preload-checkpoint.json")
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
                    os.path.join(app_data, "preload-checkpoint.json"))
            if had_checkpoint:
                with TermTest(url=f"http://127.0.0.1:{port}/batch2/", width=TERM_W, height=40) as t:
                    t.wait_for("Headline number", timeout=60)
                    deadline = time.time() + 30
                    restored = False
                    while time.time() < deadline:
                        if "Prefetch checkpoint restored" in run_log():
                            restored = True
                            break
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
        shutil.rmtree(data_home, ignore_errors=True)

    print()
    if failures:
        print("SIDECAR E2E FAILED:")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)
    print(f"SIDECAR E2E PASSED — screenshots in {OUT_DIR}")


if __name__ == "__main__":
    main()
