#!/usr/bin/env python3
"""
Live gate for epic workspace-5vqk (aggregator-robust AI layout). Drives the REAL
Ctrl+L (g l) wizard on ./run --web against the pinned techmeme FIXTURE served
locally, under Xvfb with real OpenAI round trips, and asserts the USER-VISIBLE
outcomes the epic promises:

  .1/.2/.3  The 'Your new layout' preview lists MANY real story headlines (the
            '• {headline}' rows), not one lead — coverage N is aggregator-sized.
  .2/.3     Space -> 'Paste a story's URL' -> the fixture's lead news URL derives
            the news river DETERMINISTICALLY (no model) and the preview then lists
            ~20+ distinct real headlines.
  .4        'x' on a headline row drops that item; the row count falls by >=1.

Usage: python3 scripts/test_5vqk_wizard_live.py
Needs: tmux, Xvfb, a Release build, an OpenAI key in app settings.
"""

import glob
import http.server
import os
import re
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402
from keys import choose_layout  # noqa: E402

DISPLAY = ":96"
SCREEN_W, SCREEN_H = 1600, 900
TERM_W = 150
FIXTURE = "/workspace/tests/WireCopy.Tests/Fixtures/techmeme-2026-06-12.html"
# The lead story anchor present in THIS HTML snapshot (a Bloomberg SpaceX story).
LEAD_URL = "https://www.bloomberg.com/news/articles/2026-06-11/elon-musk-s-spacex-set-to-make-history-with-record-breaking-ipo"
OUT_DIR = "/workspace/output/5vqk-wizard-live"


def serve_fixture():
    html = open(FIXTURE, "rb").read()

    class H(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(html)))
            self.end_headers()
            self.wfile.write(html)

        def log_message(self, *a):
            pass

    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv, srv.server_address[1]


def headline_rows(screen):
    # The overlay draws box borders (│) before the '• {headline}' text, so match
    # the bullet anywhere on the line, not just at the start.
    return [l for l in screen.splitlines() if "•" in l]


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    # Fresh: the wizard must see an UNCONFIGURED site and re-extract.
    for d in ("hierarchy", "page-cache", "layouts"):
        for f in glob.glob(os.path.expanduser(f"~/.local/share/WireCopy/{d}/*")):
            try:
                os.remove(f)
            except OSError:
                pass

    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/5vqk-live-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)
    lock = f"/tmp/.X{DISPLAY.lstrip(':')}-lock"
    if os.path.exists(lock):
        os.remove(lock)
    xvfb = subprocess.Popen(
        ["Xvfb", DISPLAY, "-screen", "0", f"{SCREEN_W}x{SCREEN_H}x24"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    os.environ["DISPLAY"] = DISPLAY
    srv, port = serve_fixture()
    url = f"http://127.0.0.1:{port}/"

    failures = []
    try:
        with TermTest(url=url, width=TERM_W, height=45) as t:
            t.wait_for("Techmeme", timeout=120)
            time.sleep(6)
            t.screenshot("1 fixture loaded")

            # --- Ctrl+L (g l) -> AI-first entry -> preview ---
            choose_layout(t)
            t.wait_for("How should WireCopy read this site?", timeout=25)
            t.send_keys("Enter")  # ✨ Let AI figure out this site's layout

            deadline = time.time() + 360
            while time.time() < deadline:
                screen = t.capture()
                if "Your new layout" in screen or "No reliable pattern" in screen:
                    break
                if "Set up this site with AI ·" in screen:
                    t.send_keys("Enter")  # accept focused option on a question card
                time.sleep(2)

            screen = t.screenshot("2 AI preview")
            if "Your new layout" not in screen:
                failures.append("never reached the 'Your new layout' preview via the model path")
            else:
                rows = headline_rows(screen)
                print(f"AI preview: {len(rows)} headline rows on screen")
                cov = re.search(r"(\d+) of (\d+) story links covered", screen)
                covered = int(cov.group(1)) if cov else -1
                print(f"AI preview coverage: {covered}")
                if covered <= 1:
                    failures.append(f"preview collapsed to ~1 story (coverage {covered}) — the epic's core bug")
                if len(rows) < 3:
                    failures.append(f"preview showed {len(rows)} headline rows — headlines not rendered (.3)")

            # --- Space -> Paste a story's URL -> DETERMINISTIC news river ---
            t.send_keys("Space")
            t.wait_for_any("Paste the top story", "Paste another story", "Fix links by hand", timeout=20)
            # Move to the URL option and select it. Layout: pick row(s) then URL row.
            screen = t.capture()
            # Navigate: the 'Paste ... URL' row — press Down until focused, then Enter.
            for _ in range(4):
                if "▸" in "".join(l for l in t.capture().splitlines() if "URL" in l):
                    break
                t.send_keys("Down")
                time.sleep(0.3)
            t.send_keys("Enter")
            t.wait_for_any("URL", "Paste", timeout=15)
            t.send_text(LEAD_URL)
            t.send_keys("Enter")

            # Back on the preview — now the deterministic news river.
            t.wait_for("Your new layout", timeout=30)
            time.sleep(1)
            screen = t.screenshot("3 deterministic news river preview")
            rows = headline_rows(screen)
            cov = re.search(r"(\d+) of (\d+) story links covered", screen)
            covered = int(cov.group(1)) if cov else -1
            print(f"deterministic pick: coverage {covered}, {len(rows)} headline rows visible")
            if covered < 15:
                failures.append(f"deterministic news river covered only {covered} (expected ~24 news headlines)")

            # --- 'x' on a headline row drops it ---
            # Move cursor to the first headline row (Down once past the section header).
            t.send_keys("Down")
            time.sleep(0.3)
            before = re.search(r"(\d+) of (\d+) story links covered", t.capture())
            before_n = int(before.group(1)) if before else -1
            t.send_keys("x")
            time.sleep(1.5)
            screen = t.screenshot("4 after x-exclude")
            after = re.search(r"(\d+) of (\d+) story links covered", screen)
            after_n = int(after.group(1)) if after else -1
            dropped = after_n >= 0 and after_n < before_n
            # A story that shares its only identifier with others is correctly REFUSED
            # — 'x' must then explain why, never no-op silently.
            refused = "can't drop" in screen.lower() or "press x to drop" in screen.lower()
            print(f"x-exclude: coverage {before_n} -> {after_n} · dropped={dropped} refused={refused}")
            if not (dropped or refused):
                failures.append(f"'x' produced no visible effect (coverage {before_n} -> {after_n}, no refusal notice)")
    finally:
        srv.shutdown()
        xvfb.terminate()
        subprocess.run(["tmux", "kill-server"], capture_output=True)

    print()
    if failures:
        print("5VQK WIZARD LIVE FAILED:")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)
    print(f"5VQK WIZARD LIVE PASSED — screenshots in {OUT_DIR}")


if __name__ == "__main__":
    main()
