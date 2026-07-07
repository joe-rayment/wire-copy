#!/usr/bin/env python3
"""
Gate for workspace-t1ok.2 ("More" parity on pattern paths). A pattern-configured
site must render its Navigation + Footer chrome as ONE collapsed "More" sub-menu
(like the document-order path has since cn2g.3), not separate Navigation/Footer
groups. Asserts the user-visible outcome on the real rendered list, exercises
the collapsed -> expanded state change, and re-asserts across a relaunch (the
cache-rehydrate revisit path). Zero model calls: the config is pre-seeded.

Usage: python3 scripts/test_more_menu_live.py
Needs: tmux, Xvfb, a Release build. Never headless.
"""

import http.server
import json
import os
import re
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402

DISPLAY = ":95"
SCREEN_W, SCREEN_H = 1600, 900
TERM_W = 150
FIXTURE = "/workspace/tests/WireCopy.Tests/Fixtures/techmeme-2026-06-12.html"
HIERARCHY_DIR = os.path.expanduser("~/.local/share/WireCopy/hierarchy")
KNOWN_CHROME = ("About", "Archives", "Events", "Sponsor …", "Sponsor")


def serve_fixture():
    html = open(FIXTURE, "rb").read()
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
    domain = f"127.0.0.1:{port}"
    config = [{
        "domain": domain,
        "urlPattern": f"^http://127\\.0\\.0\\.1:{port}/",
        "sections": [{
            "name": "Headlines",
            "sortOrder": 0,
            "parentSelectors": [],
            "urlPatterns": ["http"],
            "startCollapsed": False,
            "maxLinks": None,
        }],
        "createdAt": "2026-07-07T00:00:00Z",
        "modelVersion": "t1ok2-gate-seed",
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


def assert_chrome_shape(screen: str, phase: str, failures: list):
    # The renderer upper-cases group headers ("▶ MORE (23)").
    if not re.search(r"(?i)\bMORE \(\d+\)", screen):
        failures.append(f"[{phase}] no consolidated 'More (N)' group in the rendered list")
    if re.search(r"(?i)\bNAVIGATION \(\d+\)", screen):
        failures.append(f"[{phase}] separate 'Navigation' group still rendered")
    if re.search(r"(?i)\bFOOTER \(\d+\)", screen):
        failures.append(f"[{phase}] separate 'Footer' group still rendered")


def main():
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/t1ok-more-tmux"
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
        # --- Phase 1: fresh load renders the pattern tree with one More menu ---
        with TermTest(url=url, width=TERM_W, height=45) as t:
            t.wait_for("Techmeme", timeout=120)
            time.sleep(6)
            t.send_keys("G")  # bottom — the collapsed More header is the last node
            time.sleep(1)
            screen = t.screenshot("1 fresh load, bottom of list")
            assert_chrome_shape(screen, "fresh", failures)

            if not re.search(r"▶ MORE", screen):
                failures.append("[expand] More group is not collapsed on load")
            t.send_keys("l")  # expand the selected (More) node
            time.sleep(1)
            expanded = t.capture()
            if not re.search(r"▼ MORE", expanded):
                failures.append("[expand] pressing l did not expand the More group (no ▼ MORE)")
            t.send_keys("G")  # scroll into the now-visible More children
            time.sleep(1)
            after = t.screenshot("2 More expanded, children in view")
            if not any(k.lower() in after.lower() for k in KNOWN_CHROME):
                failures.append(
                    f"[expand] expanded More shows none of the known chrome anchors {KNOWN_CHROME}")

        # --- Phase 2: relaunch (build-cache rehydrate path) keeps the shape ---
        with TermTest(url=url, width=TERM_W, height=45) as t:
            t.wait_for("Techmeme", timeout=120)
            time.sleep(6)
            t.send_keys("G")
            time.sleep(1)
            screen = t.screenshot("3 relaunch, bottom of list")
            assert_chrome_shape(screen, "relaunch", failures)
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
        print("T1OK.2 MORE-MENU GATE FAILED:")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)
    print("T1OK.2 MORE-MENU GATE PASSED")


if __name__ == "__main__":
    main()
