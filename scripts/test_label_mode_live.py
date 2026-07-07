#!/usr/bin/env python3
"""
Gate for workspace-t1ok.4/.5 — LABEL MODE end to end, the epic's core flow.

Drives the real app on the pinned techmeme fixture with real keys (tmux,
headful under Xvfb — never headless), through: seeded config -> g l ->
Reconfigure -> seeded preview (0 model calls) -> Space -> "Fix links by hand"
-> label two articles (a, a), an ad (x) and a menu link (m) -> Enter (apply,
deterministic derivation) -> asserts the USER-VISIBLE outcomes:

  1. the label card lists real fixture headlines and shows [ 1]/[ 2]/[ad]/[menu]
     badges on the exact rows pressed;
  2. back on "Your new layout", the rank-1 headline is the FIRST story row and
     rank-2 follows it (the labeled order IS the story order);
  3. Enter saves; the persisted per-domain JSON carries the ledger (4 labels,
     ranks 1/2) plus derived More/exclude rules;
  4. a relaunch renders the saved layout: rank-1 headline before rank-2, a
     More group present, and ZERO analyzer invocations in the phase-2 log.

Usage: python3 scripts/test_label_mode_live.py
Needs: tmux, Xvfb, a Release build.
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
from keys import choose_layout, LABEL_ARTICLE, LABEL_AD, LABEL_MENU  # noqa: E402

DISPLAY = ":94"
SCREEN_W, SCREEN_H = 1600, 900
TERM_W = 150
FIXTURE = "/workspace/tests/WireCopy.Tests/Fixtures/techmeme-2026-06-12.html"
HIERARCHY_DIR = os.path.expanduser("~/.local/share/WireCopy/hierarchy")
LOG_GLOB = "/workspace/logs/wirecopy-*.log"


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
        "modelVersion": "t1ok5-gate-seed",
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


def badged_text(screen: str, badge: str) -> str | None:
    """Text after '• ' on the first line carrying the given badge."""
    for line in screen.splitlines():
        if badge in line and "•" in line:
            return line.split("•", 1)[1].strip().rstrip("│").strip()
    return None


def bullet_rows(screen: str) -> list[str]:
    return [l.split("•", 1)[1].strip().rstrip("│").strip()
            for l in screen.splitlines() if "•" in l]


def log_size() -> int:
    import glob as g
    paths = sorted(g.glob(LOG_GLOB))
    return os.path.getsize(paths[-1]) if paths else 0


def log_tail_since(offset: int) -> str:
    import glob as g
    paths = sorted(g.glob(LOG_GLOB))
    if not paths:
        return ""
    with open(paths[-1], errors="replace") as f:
        f.seek(offset if offset <= os.path.getsize(paths[-1]) else 0)
        return f.read()


def main():
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/t1ok-label-tmux"
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
    n1 = n2 = ad_text = menu_text = None
    try:
        # ---- Phase 1: label + apply + save -------------------------------------
        with TermTest(url=url, width=TERM_W, height=45) as t:
            t.wait_for("Techmeme", timeout=120)
            time.sleep(6)
            choose_layout(t)
            t.wait_for("Reconfigure with AI", timeout=25)
            t.send_keys("Up", "Up", "Up", "Enter")
            t.wait_for("Your new layout", timeout=45)

            t.send_keys("Space")
            t.wait_for("Fix links by hand", timeout=20)
            t.send_keys("Enter")
            t.wait_for("Mark the links", timeout=15)

            # Label: a (rank 1), down, a (rank 2), down, x (ad), down, m (menu).
            t.send_keys(LABEL_ARTICLE)
            time.sleep(0.5)
            t.send_keys("j", LABEL_ARTICLE)
            time.sleep(0.5)
            t.send_keys("j", LABEL_AD)
            time.sleep(0.5)
            t.send_keys("j", LABEL_MENU)
            time.sleep(0.8)
            screen = t.screenshot("1 labeled rows")

            n1 = badged_text(screen, "[ 1]")
            n2 = badged_text(screen, "[ 2]")
            ad_text = badged_text(screen, "[ad]")
            menu_text = badged_text(screen, "[menu]")
            print(f"labels: n1={n1!r} n2={n2!r} ad={ad_text!r} menu={menu_text!r}")
            for name, val in [("[ 1]", n1), ("[ 2]", n2), ("[ad]", ad_text), ("[menu]", menu_text)]:
                if not val:
                    failures.append(f"badge {name} did not appear on a labeled row")
            if "article(s)" not in screen:
                failures.append("running tally footnote missing from the label card")

            # Apply.
            t.send_keys("Enter")
            t.wait_for("Your new layout", timeout=120)  # generous: fallback may run once
            time.sleep(1.5)
            screen = t.screenshot("2 preview after apply")
            rows = bullet_rows(screen)
            if not rows:
                failures.append("no story rows on the post-apply preview")
            else:
                def row_index(want):
                    if not want:
                        return None
                    key = want[:28]
                    for i, r in enumerate(rows):
                        if key in r:
                            return i
                    return None

                i1 = row_index(n1)
                if i1 != 0:
                    failures.append(f"rank-1 headline is not the FIRST story row (got {rows[0]!r})")

            # Rank-2 lives in its own (second) section, likely below the visible
            # window — scroll the preview cursor down until its row appears.
            found_n2 = False
            for _ in range(30):
                if n2 and n2[:24] in t.capture():
                    found_n2 = True
                    break
                t.send_keys("j", "j", "j", "j", "j", delay=0.05)
                time.sleep(0.2)
            if not found_n2:
                failures.append("rank-2 headline never appeared while scrolling the preview")

            # Save.
            t.send_keys("Enter")
            t.wait_for("Site set up", timeout=20)
            time.sleep(1)

        # ---- Persisted artifact -------------------------------------------------
        with open(seed_path) as f:
            configs = json.load(f)
        cfg = configs[-1]
        labels = cfg.get("userLabels", [])
        kinds = sorted(l["kind"] for l in labels)
        ranks = sorted([l["rank"] for l in labels if l.get("rank") is not None])
        print(f"persisted: {len(labels)} labels kinds={kinds} ranks={ranks} "
              f"sections={[s['name'] for s in cfg['sections']]} "
              f"more={cfg.get('moreSelectors', []) + cfg.get('moreUrlPatterns', [])} "
              f"excl={len(cfg.get('excludeSelectors', []) + cfg.get('excludeUrlPatterns', []) + cfg.get('excludeSectionTitles', []))}")
        if len(labels) != 4:
            failures.append(f"persisted ledger has {len(labels)} labels, expected 4")
        if ranks != [1, 2]:
            failures.append(f"persisted article ranks are {ranks}, expected [1, 2]")
        if not (cfg.get("moreSelectors") or cfg.get("moreUrlPatterns")):
            failures.append("no More-menu rules persisted for the menu label")
        if not any(LabelKindIsAd(l) for l in labels):
            failures.append("no ad label persisted")
        if not cfg["sections"]:
            failures.append("no sections persisted")

        # ---- Phase 2: relaunch renders the saved layout, zero analyzer calls ----
        offset = log_size()
        with TermTest(url=url, width=TERM_W, height=45) as t:
            t.wait_for("Techmeme", timeout=120)
            time.sleep(6)
            screen = t.screenshot("3 relaunch")
            flat = " ".join(screen.split())
            if n1 and n1[:28] not in flat:
                failures.append(f"relaunch: rank-1 headline missing from the rendered list")
            if n1 and n2 and n1[:28] in flat and n2[:20] in flat:
                if flat.index(n1[:28]) > flat.index(n2[:20]):
                    failures.append("relaunch: rank-1 headline renders AFTER rank-2")
            if not re.search(r"(?i)\bMORE \(\d+\)", screen):
                t.send_keys("G")
                time.sleep(1)
                screen = t.capture()
                if not re.search(r"(?i)\bMORE \(\d+\)", screen):
                    failures.append("relaunch: no More group in the rendered list")
        tail = log_tail_since(offset)
        analyzer_calls = len(re.findall(r"invoking analyzer|Generalizing \d+ user label", tail, re.IGNORECASE))
        if analyzer_calls:
            failures.append(f"relaunch made {analyzer_calls} analyzer call(s) — the saved pattern should serve alone")
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
        print("T1OK.5 LABEL-MODE GATE FAILED:")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)
    print("T1OK.5 LABEL-MODE GATE PASSED")


def LabelKindIsAd(label) -> bool:
    # LinkLabelKind serializes numerically: Article=0, Ad=1, Menu=2, Ignore=3.
    return label.get("kind") == 1


if __name__ == "__main__":
    main()
