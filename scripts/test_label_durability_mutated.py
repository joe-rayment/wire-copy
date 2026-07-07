#!/usr/bin/env python3
"""
Gate for workspace-t1ok.9 — "a pattern that survives if the links change
tomorrow", the epic's literal acceptance test.

Phase 1: on the pinned techmeme fixture, label rank-1/rank-2 articles and press
x on the SPONSOR-BLOCK pitch (found by scrolling the label card until the
cursor row reads "Fast, affordable law…") so the derivation takes the DURABLE
heading route (excludeSectionTitles gains "Sponsor Posts"); save.

Phase 2: serve a MUTATED copy of the fixture simulating tomorrow's page —
every dated story URL rotated to a new date (per-URL identity dies), the lead
headline reworded (SpaceX -> OrbitalX), the sponsor block's per-day random
container id regenerated AND a brand-new sponsor pitch rotated in — while the
class structure and the "Sponsor Posts" heading stay (that is what survives a
day, per the real site). Clear the page build cache, KEEP the hierarchy store,
relaunch, and assert the USER-VISIBLE outcomes:

  1. the labeled "Stories" river still renders with a healthy story count;
  2. the mutated lead headline (OrbitalX…) is IN the story flow — the pattern
     captures tomorrow's new links, not yesterday's URLs;
  3. the NEW sponsor pitch is ABSENT — the heading-level ad kill carried;
  4. no "Saved layout no longer matches" nag; the More group is present;
  5. ZERO analyzer invocations in the phase-2 log (the saved pattern serves
     alone).

Usage: python3 scripts/test_label_durability_mutated.py
Needs: tmux, Xvfb, a Release build. Phase 1 may spend one AI fallback call if
derivation misses; phase 2 is zero-model by assertion.
"""

import glob as globmod
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
from keys import choose_layout, LABEL_ARTICLE, LABEL_AD, summary_refine  # noqa: E402

DISPLAY = ":92"
SCREEN_W, SCREEN_H = 1600, 900
TERM_W = 150
FIXTURE = "/workspace/tests/WireCopy.Tests/Fixtures/techmeme-2026-06-12.html"
HIERARCHY_DIR = os.path.expanduser("~/.local/share/WireCopy/hierarchy")
PAGE_CACHE_DIR = os.path.expanduser("~/.local/share/WireCopy/page-cache")
LOG_GLOB = "/workspace/logs/wirecopy-*.log"
# The one CONTENT-typed sponsor-post row (the other sponsor anchors classify
# as Navigation and never reach the label card).
SPONSOR_PITCH = "Agentic AI: The need for a data"
NEW_SPONSOR_PITCH = "Brand-new sponsored pitch about widgets"


class FixtureServer:
    """Serves the fixture; `mutate()` flips it to the tomorrow-page variant."""

    def __init__(self):
        base = open(FIXTURE, "rb").read()
        base = base.replace(b"window.location.replace", b"console.log")
        base = base.replace(b"window.location.href=window.location.pathname", b"void 0")
        self.payload = base
        self.base = base

    def mutate(self):
        s = self.base
        # Tomorrow's page: every dated URL rotates (per-URL identity dies)...
        s = s.replace(b"2026-06-11", b"2026-07-07").replace(b"2026/06/11", b"2026/07/07")
        s = s.replace(b"2026-06-12", b"2026-07-08").replace(b"2026/06/12", b"2026/07/08")
        # ...the lead headline is reworded...
        s = s.replace(b"SpaceX", b"OrbitalX")
        # ...the sponsor block regenerates its per-day random id and rotates in a
        # brand-new pitch — but keeps its class structure and heading.
        s = s.replace(b'ID="bcfphmqrf"', b'ID="zzqxvwyyk"')
        s = s.replace(b"Agentic AI: The need for a data foundation",
                      NEW_SPONSOR_PITCH.encode())
        self.payload = s

    def start(self):
        server = self

        class H(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                body = server.payload
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *a):
                pass

        self.srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), H)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        return self.srv.server_address[1]


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
        "modelVersion": "t1ok9-gate-seed",
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


def cursor_row(screen: str) -> str:
    return next((l for l in screen.splitlines() if "▸" in l), "")


def log_size() -> int:
    paths = sorted(globmod.glob(LOG_GLOB))
    return os.path.getsize(paths[-1]) if paths else 0


def log_tail_since(offset: int) -> str:
    paths = sorted(globmod.glob(LOG_GLOB))
    if not paths:
        return ""
    with open(paths[-1], errors="replace") as f:
        f.seek(offset if offset <= os.path.getsize(paths[-1]) else 0)
        return f.read()


def clear_build_cache(url: str):
    """Drop only the cache entries for OUR fixture URL (scoped, no rm -rf)."""
    for path in globmod.glob(os.path.join(PAGE_CACHE_DIR, "**", "*"), recursive=True):
        if not os.path.isfile(path):
            continue
        try:
            with open(path, errors="replace") as f:
                if url in f.read():
                    os.remove(path)
        except OSError:
            pass


def main():
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/t1ok-durability-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)
    lock = f"/tmp/.X{DISPLAY.lstrip(':')}-lock"
    if os.path.exists(lock):
        os.remove(lock)
    xvfb = subprocess.Popen(
        ["Xvfb", DISPLAY, "-screen", "0", f"{SCREEN_W}x{SCREEN_H}x24"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    os.environ["DISPLAY"] = DISPLAY

    server = FixtureServer()
    port = server.start()
    url = f"http://127.0.0.1:{port}/"
    seed_path = seed_config(port)

    failures = []
    try:
        # ---- Phase 1: label articles + the sponsor pitch, save ----------------
        with TermTest(url=url, width=TERM_W, height=45) as t:
            t.wait_for("Techmeme", timeout=120)
            time.sleep(6)
            choose_layout(t)
            t.wait_for("Refine the layout with AI", timeout=25)
            summary_refine(t)
            t.wait_for("Your new layout", timeout=45)
            t.send_keys("Space")
            t.wait_for("Fix links by hand", timeout=20)
            t.send_keys("Enter")
            t.wait_for("Mark the links", timeout=15)

            t.send_keys(LABEL_ARTICLE)          # rank 1 on the first story
            time.sleep(0.4)
            t.send_keys("j", LABEL_ARTICLE)     # rank 2
            time.sleep(0.4)

            # Walk to the sponsor pitch and mark it as an ad — its "Sponsor
            # Posts" heading is the durable exclude. The sponsor block sits in
            # the page's tail, so search upward from the bottom.
            t.send_keys("G")
            time.sleep(0.6)
            found = False
            for _ in range(220):
                if SPONSOR_PITCH.lower() in cursor_row(t.capture()).lower():
                    found = True
                    break
                t.send_keys("k", delay=0.05)
            if not found:
                failures.append("phase 1: never found the sponsor pitch row in the label card")
            else:
                t.send_keys(LABEL_AD)
                time.sleep(0.5)
                if "[ad]" not in cursor_row(t.capture()):
                    failures.append("phase 1: [ad] badge missing on the sponsor row")

            t.send_keys("Enter")
            t.wait_for("Your new layout", timeout=120)
            time.sleep(1)
            t.send_keys("Enter")  # save
            t.wait_for("Site set up", timeout=20)
            time.sleep(1)

        with open(seed_path) as f:
            saved = json.load(f)[-1]
        titles = saved.get("excludeSectionTitles", [])
        print(f"phase1 saved: sections={[s['name'] for s in saved['sections']]} "
              f"labels={len(saved.get('userLabels', []))} excludeTitles={titles}")
        if "Sponsor Posts" not in titles:
            failures.append("phase 1: the ad label did not take the heading route "
                            f"(excludeSectionTitles={titles})")

        # ---- Phase 2: TOMORROW'S PAGE — mutate, clear build cache, relaunch ----
        server.mutate()
        clear_build_cache(url)
        offset = log_size()
        with TermTest(url=url, width=TERM_W, height=45) as t:
            t.wait_for("Techmeme", timeout=120)
            time.sleep(6)
            screen = t.screenshot("phase 2: mutated page under the saved pattern")

            m = re.search(r"(?i)STORIES \((\d+)\)", screen)
            count = int(m.group(1)) if m else 0
            if count < 10:
                failures.append(f"phase 2: labeled river holds only {count} stories on the mutated page")

            flat = " ".join(screen.split())
            if "OrbitalX" not in flat:
                failures.append("phase 2: tomorrow's reworded lead (OrbitalX…) is not in the story flow")
            if "Saved layout no longer matches" in flat:
                failures.append("phase 2: false-stale nag shown — the pattern should still match")

            # The NEW sponsor pitch must be hidden everywhere reachable.
            if NEW_SPONSOR_PITCH.split()[0] in flat:
                failures.append("phase 2: the NEW sponsor pitch is visible at the top")
            t.send_keys("G")
            time.sleep(1)
            bottom = t.screenshot("phase 2: bottom of list")
            if NEW_SPONSOR_PITCH.split()[0] in " ".join(bottom.split()):
                failures.append("phase 2: the NEW sponsor pitch is visible at the bottom")
            if not re.search(r"(?i)\bMORE \(\d+\)", bottom):
                failures.append("phase 2: no More group on the mutated page")

        tail = log_tail_since(offset)
        analyzer_calls = len(re.findall(
            r"invoking analyzer|Generalizing \d+ user label|Refining layout", tail, re.IGNORECASE))
        if analyzer_calls:
            failures.append(f"phase 2 made {analyzer_calls} analyzer call(s) — "
                            "the saved pattern must serve alone")
        print(f"phase2: river={count if 'count' in dir() else '?'} analyzer_calls={analyzer_calls}")
    finally:
        server.srv.shutdown()
        xvfb.terminate()
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        try:
            os.remove(seed_path)
        except OSError:
            pass

    print()
    if failures:
        print("T1OK.9 DURABILITY GATE FAILED:")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)
    print("T1OK.9 DURABILITY GATE PASSED")


if __name__ == "__main__":
    main()
