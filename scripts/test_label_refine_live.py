#!/usr/bin/env python3
"""
Gate for workspace-t1ok.8 — refinement builds on progress and NEVER regresses a
hand label, across sessions.

Phase 1 (same steps as test_label_mode_live): label rank-1/rank-2 articles, an
ad and a menu link on the techmeme fixture, apply, save.

Phase 2 (a NEW app session — the cross-session proof):
  1. g l opens the configured summary with "Refine the layout with AI (keeps
     your fixes)"; entering it seeds the preview from the SAVED config — the
     rank-1 headline is already the first story row (never a blank slate).
  2. Space -> "Tell the AI what to change…" -> type a real instruction -> the
     refine round-trips (real model call) -> back on the preview the rank-1
     headline is STILL the first row (the refine edited, not regenerated).
  3. Enter saves; the persisted JSON still carries all 4 labels, the ad's
     exclude rules, PLUS the applied instruction in userInstructions.
  4. Re-entering label mode shows the [ 1] badge pre-lit on the rank-1 row and
     an [ad] badge — the ledger is visible session-to-session.

Usage: python3 scripts/test_label_refine_live.py
Needs: tmux, Xvfb, a Release build, an OpenAI key in app settings (ONE refine
call). Never headless.
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
from keys import choose_layout, LABEL_ARTICLE, LABEL_AD, LABEL_MENU, summary_refine  # noqa: E402

DISPLAY = ":93"
SCREEN_W, SCREEN_H = 1600, 900
TERM_W = 150
FIXTURE = "/workspace/tests/WireCopy.Tests/Fixtures/techmeme-2026-06-12.html"
HIERARCHY_DIR = os.path.expanduser("~/.local/share/WireCopy/hierarchy")
INSTRUCTION = "hide the sponsor posts section"


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
        "modelVersion": "t1ok8-gate-seed",
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
    for line in screen.splitlines():
        if badge in line and "•" in line:
            return line.split("•", 1)[1].strip().rstrip("│").strip()
    return None


def first_bullet_row(screen: str) -> str | None:
    for line in screen.splitlines():
        if "•" in line:
            return line.split("•", 1)[1].strip().rstrip("│").strip()
    return None


def go_to_option(t, text: str, presses: int = 8) -> bool:
    """Walk the card cursor down until the ▸ row contains `text`."""
    for _ in range(presses):
        cursor_line = next((l for l in t.capture().splitlines() if "▸" in l), "")
        if text in cursor_line:
            return True
        t.send_keys("Down")
        time.sleep(0.3)
    return False


def rules_count(cfg) -> int:
    return len(cfg.get("excludeSelectors", []) + cfg.get("excludeUrlPatterns", [])
               + cfg.get("excludeSectionTitles", []))


def main():
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/t1ok-refine-tmux"
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
    n1 = None
    try:
        # ---- Phase 1: label + save (the t1ok.5 flow) ---------------------------
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
            t.send_keys(LABEL_ARTICLE)
            time.sleep(0.5)
            t.send_keys("j", LABEL_ARTICLE)
            time.sleep(0.5)
            t.send_keys("j", LABEL_AD)
            time.sleep(0.5)
            t.send_keys("j", LABEL_MENU)
            time.sleep(0.8)
            n1 = badged_text(t.capture(), "[ 1]")
            if not n1:
                failures.append("phase 1: rank-1 badge missing")
            t.send_keys("Enter")
            t.wait_for("Your new layout", timeout=120)
            time.sleep(1)
            t.send_keys("Enter")  # save
            t.wait_for("Site set up", timeout=20)
            time.sleep(1)

        with open(seed_path) as f:
            phase1 = json.load(f)[-1]
        phase1_rules = rules_count(phase1)
        print(f"phase1: n1={n1!r} labels={len(phase1.get('userLabels', []))} rules={phase1_rules}")

        # ---- Phase 2: NEW session — refine builds on the saved progress --------
        with TermTest(url=url, width=TERM_W, height=45) as t:
            t.wait_for("Techmeme", timeout=120)
            time.sleep(6)
            choose_layout(t)
            t.wait_for("Refine the layout with AI", timeout=25)
            summary_refine(t)

            # Never-blank-slate: the preview seeds from the SAVED layout.
            t.wait_for("Your new layout", timeout=45)
            time.sleep(1.5)
            screen = t.screenshot("1 phase-2 seeded preview")
            first = first_bullet_row(screen)
            if not (n1 and first and n1[:28] in first):
                failures.append(
                    f"phase 2: seeded preview does not lead with the labeled rank-1 (first={first!r})")

            # Plain-English refine.
            t.send_keys("Space")
            t.wait_for("Fix links by hand", timeout=20)
            if not go_to_option(t, "Tell the AI"):
                failures.append("phase 2: could not reach the free-text option")
            t.send_keys("Enter")
            t.wait_for("Tell the AI what to change", timeout=15)
            t.send_text(INSTRUCTION)
            t.send_keys("Enter")
            t.wait_for("Your new layout", timeout=240)
            time.sleep(1.5)
            screen = t.screenshot("2 after refine")
            first = first_bullet_row(screen)
            if not (n1 and first and n1[:28] in first):
                failures.append(
                    f"phase 2: refine changed the labeled lead (first={first!r}) — it must edit, not regenerate")

            t.send_keys("Enter")  # save the refined layout
            t.wait_for("Site set up", timeout=20)
            time.sleep(1)

            # Cross-session ledger badges: re-enter label mode.
            choose_layout(t)
            t.wait_for("Refine the layout with AI", timeout=25)
            summary_refine(t)
            t.wait_for("Your new layout", timeout=45)
            t.send_keys("Space")
            t.wait_for("Fix links by hand", timeout=20)
            t.send_keys("Enter")
            t.wait_for("Mark the links", timeout=15)
            time.sleep(1)
            screen = t.screenshot("3 label mode with seeded ledger")
            lit1 = badged_text(screen, "[ 1]")
            if not (lit1 and n1 and n1[:28] in lit1):
                failures.append(f"phase 2: [ 1] badge not pre-lit on the rank-1 row (got {lit1!r})")

            # The labeled ad is EXCLUDED from the current layout, so its badge
            # lives in the all-links rescue view (Tab).
            t.send_keys("Tab")
            time.sleep(1)
            all_view = t.screenshot("4 all-links view")
            if not badged_text(all_view, "[ad]"):
                failures.append("phase 2: [ad] badge not pre-lit in the all-links view")
            t.send_keys("Escape")
            time.sleep(0.5)
            t.send_keys("Escape")
            time.sleep(0.5)

        with open(seed_path) as f:
            phase2 = json.load(f)[-1]
        labels2 = phase2.get("userLabels", [])
        instructions = phase2.get("userInstructions", [])
        print(f"phase2: labels={len(labels2)} rules={rules_count(phase2)} instructions={instructions}")
        print(f"phase2 rules detail: sel={phase2.get('excludeSelectors')} "
              f"url={phase2.get('excludeUrlPatterns')} titles={phase2.get('excludeSectionTitles')} "
              f"ad={next((l for l in labels2 if l.get('kind') == 1), None)}")
        if len(labels2) != len(phase1.get("userLabels", [])):
            failures.append(f"ledger shrank across the refine: {len(labels2)} labels")
        if INSTRUCTION not in instructions:
            failures.append("the applied instruction is not in the standing userInstructions log")

        # The USER'S ad label must still be enforced by some rule (machine-derived
        # leak rules may legitimately be restructured by the refine; the ledger's
        # corrections may not). Mirror IsExcluded's Contains semantics.
        ad = next((l for l in labels2 if l.get("kind") == 1), None)
        if ad is None:
            failures.append("the ad label vanished from the ledger")
        else:
            url = (ad.get("url") or "").lower()
            parent = (ad.get("parentSelector") or "").lower()
            excluded = (
                any(p.lower() in url for p in phase2.get("excludeUrlPatterns", []) if p)
                or any(sel.lower() in parent for sel in phase2.get("excludeSelectors", []) if sel and parent)
                # Heading-routed ad kills persist as section titles; the ledger
                # doesn't store the heading, so presence is the best JSON-level
                # check (the unit suite asserts IsExcluded exactly).
                or bool(phase2.get("excludeSectionTitles"))
            )
            if not excluded:
                failures.append("no persisted exclude rule matches the labeled ad — it would resurface")
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
        print("T1OK.8 LABEL-REFINE GATE FAILED:")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)
    print("T1OK.8 LABEL-REFINE GATE PASSED")


if __name__ == "__main__":
    main()
