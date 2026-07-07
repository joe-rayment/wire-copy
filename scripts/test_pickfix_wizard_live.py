#!/usr/bin/env python3
"""
Gate for workspace-t1ok.1 (pick-flow overlay/status bug). Before the fix, choosing
"Pick a story to teach the layout" and picking a story left the wizard INVISIBLE:
PickLeadFromTreeAsync cleared the overlay painter and pinned a 2-minute status
hint, and nothing restored either — so the preview card rendered into a null
painter while arrows moved an invisible cursor ("arrows move something in the
status bar" report).

This gate drives the REAL flow with real keys and asserts the USER-VISIBLE
outcome, with ZERO model calls:

  1. Pre-seed a Version-3 hierarchy config for the fixture's host:port, so
     `g l` opens the configured summary and "Reconfigure with AI" seeds the
     preview straight from the saved config (no OpenAI round trip).
  2. Space -> pick option -> j,j,Enter picks a story (deterministic river
     derivation — still no model call).
  3. ASSERT: the "Your new layout" preview card is VISIBLE again with its
     "Enter save" hint; the "Point at the top story" pick hint is GONE from
     the status bar; pressing Down twice visibly MOVES the card cursor (the
     focused `▸` row changes between captures).

Usage: python3 scripts/test_pickfix_wizard_live.py
Needs: tmux, Xvfb, a Release build. Never headless (app browser runs headful
under this script's Xvfb).
"""

import http.server
import json
import os
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402
from keys import choose_layout, summary_refine  # noqa: E402

DISPLAY = ":97"
SCREEN_W, SCREEN_H = 1600, 900
TERM_W = 150
FIXTURE = "/workspace/tests/WireCopy.Tests/Fixtures/techmeme-2026-06-12.html"
OUT_DIR = "/workspace/output/t1ok-pickfix"
HIERARCHY_DIR = os.path.expanduser("~/.local/share/WireCopy/hierarchy")


def serve_fixture():
    html = open(FIXTURE, "rb").read()
    # The techmeme snapshot's own JS navigates mid-load (window.location.replace),
    # which intermittently kills the app's fetch. Neuter navigation calls — this
    # gate tests wizard UI behavior, not the fixture's redirect logic.
    html = html.replace(b"window.location.replace", b"console.log")
    html = html.replace(b"window.location.href=window.location.pathname", b"void 0")

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


def seed_config(port: int) -> str:
    """Write a valid pattern config for the fixture host so g-l opens the
    configured summary and the wizard seeds its preview with no model call."""
    domain = f"127.0.0.1:{port}"
    config = [{
        "domain": domain,
        "urlPattern": f"^http://127\\.0\\.0\\.1:{port}/",
        "sections": [{
            "name": "Headlines",
            "sortOrder": 0,
            "parentSelectors": [],
            # Substring match — every content link contains "http", so the
            # seeded section covers 100% of stories (never degenerate).
            "urlPatterns": ["http"],
            "startCollapsed": False,
            "maxLinks": None,
        }],
        "createdAt": "2026-07-07T00:00:00Z",
        "modelVersion": "t1ok-gate-seed",
        "kind": 3,
        "version": 3,
        "strategy": "AiCurated",
        "excludeSelectors": [],
        "excludeUrlPatterns": [],
        "excludeSectionTitles": [],
        "needsReanalyze": False,
    }]
    os.makedirs(HIERARCHY_DIR, exist_ok=True)
    path = os.path.join(HIERARCHY_DIR, f"{domain.replace(':', '_')}.json")
    with open(path, "w") as f:
        json.dump(config, f, indent=2)
    return path


def focused_row(screen: str) -> str:
    for line in screen.splitlines():
        if "▸" in line:
            return line.strip()
    return ""


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/t1ok-pickfix-tmux"
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
    seed_path = seed_config(port)

    failures = []
    try:
        with TermTest(url=url, width=TERM_W, height=45) as t:
            t.wait_for("Techmeme", timeout=120)
            time.sleep(6)

            # --- g l on a CONFIGURED site -> summary -> Reconfigure with AI ---
            choose_layout(t)
            t.wait_for("Refine the layout with AI", timeout=25)
            summary_refine(t)  # navigate by screen state, not blind Up-counts

            # Seeded preview — no model call.
            t.wait_for("Your new layout", timeout=45)
            t.screenshot("1 seeded preview")

            # --- Space -> adjust card -> pick option (index 0) ---
            t.send_keys("Space")
            t.wait_for_any("Point at the top story", "Point at another story", timeout=20)
            t.send_keys("Down")  # past "Fix links by hand" (option 0) to the pick option
            t.send_keys("Enter")

            # Pick mode: card cleared, pick hint pinned. (Tolerate the pre-fix
            # wording so this gate fails on the BEHAVIOR, not the copy change.)
            t.wait_for_any("Point at the top story", "Pick a story to teach", timeout=15)
            screen = t.screenshot("2 pick mode")
            if "Adjust the layout" in screen:
                failures.append("adjust card still visible during the pick — expected the bare list")

            # --- Pick a story with real keys ---
            t.send_keys("j", "j")
            t.send_keys("Enter")

            # THE FIX: the preview card must come back, visibly.
            try:
                t.wait_for("Your new layout", timeout=30)
            except TimeoutError:
                t.screenshot("3 FAIL after pick")
                failures.append(
                    "preview card never reappeared after the pick — the invisible-wizard bug")
            else:
                time.sleep(1.5)
                screen = t.screenshot("3 preview restored after pick")
                if "Enter save" not in screen:
                    failures.append("preview hint (Enter save) missing after the pick")
                if "Point at the top story" in screen or "Pick a story to teach" in screen:
                    failures.append("stale pick hint still in the status bar after the pick")

                # Arrows must act on the VISIBLE card: focused row changes.
                t.send_keys("Down")
                time.sleep(0.7)
                row1 = focused_row(t.capture())
                t.send_keys("Down")
                time.sleep(0.7)
                row2 = focused_row(t.capture())
                t.screenshot("4 after two Downs")
                print(f"focused row 1: {row1!r}")
                print(f"focused row 2: {row2!r}")
                if not row1 or not row2:
                    failures.append("no focused ▸ row visible while pressing arrows on the preview")
                elif row1 == row2:
                    failures.append("arrow keys did not move the visible card cursor")

            t.send_keys("Escape")
            time.sleep(1)
    finally:
        srv.shutdown()
        xvfb.terminate()
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        try:
            os.remove(seed_path)
        except OSError:
            pass

    print()
    if failures:
        print("T1OK.1 PICK-FIX GATE FAILED:")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)
    print("T1OK.1 PICK-FIX GATE PASSED")


if __name__ == "__main__":
    main()
