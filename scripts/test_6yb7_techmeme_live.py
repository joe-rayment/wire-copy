#!/usr/bin/env python3
"""
Live gate for epic workspace-6yb7: the AI layout setup on REAL techmeme.com —
the aggregator whose total failure motivated the epic (every story link is
off-domain; the old pipeline classified them all External and the wizard saw
an empty page).

Runs the REAL app (real network, real OpenAI round trips) in tmux under Xvfb:

  1. Opens techmeme.com; asserts the aggregator promotion fired (app log:
     'Aggregator page detected: promoted N external story links to content')
     and the link tree shows story headlines — NOT one giant External bucket.
  2. Ctrl+L -> 'Let AI find the stories' -> answers any clarifying-question
     cards -> asserts the 'Your new layout' PREVIEW appears with coverage
     'N of M story links covered' where N > 0 and M is aggregator-sized
     (>= 15) — proving classification + analyzer + gate all worked.
  3. Enter saves; asserts the 'Site set up · AI Curated' status.
  4. RELAUNCHES on the same URL: the saved durable config must route the
     revisit (no AI call) and Ctrl+L must open the configured summary.

Usage: python3 scripts/test_6yb7_techmeme_live.py
Needs: tmux, Xvfb, a Release build, an OpenAI key in app settings, network.
"""

import glob
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402

DISPLAY = ":97"
SCREEN_W, SCREEN_H = 1600, 900
OUT_DIR = "/workspace/output/6yb7-techmeme-live"
TERM_W = 150
URL = "https://www.techmeme.com/"


def app_log_lines(pattern):
    logs = sorted(glob.glob("/workspace/logs/wirecopy-*.log"))
    if not logs:
        return []
    out = subprocess.run(
        ["grep", "-h", pattern, logs[-1]], capture_output=True, text=True).stdout
    return [l for l in out.splitlines() if l]


def transcript(t, log, step):
    screen = t.capture()
    log.append(f"\n===== {step} =====\n{screen}")
    print(f"--- {step}")
    return screen


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    # Repeatability: the wizard must see an UNCONFIGURED site, and the
    # extraction must re-run (the aggregator-promotion log assertion needs a
    # fresh extraction, not a page-cache hit).
    for cfg in glob.glob(os.path.expanduser("~/.local/share/WireCopy/hierarchy/*techmeme*")):
        os.remove(cfg)
    for cached in glob.glob(os.path.expanduser("~/.local/share/WireCopy/page-cache/*")):
        os.remove(cached)  # hashed filenames — clear all so extraction re-runs

    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/6yb7-live-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)

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
    os.environ["DISPLAY"] = DISPLAY

    failures, log = [], []
    try:
        with TermTest(url=URL, width=TERM_W, height=45) as t:
            # Techmeme is JS-light but Cloudflare-fronted; generous timeout.
            t.wait_for("Techmeme", timeout=120)
            time.sleep(8)  # let extraction + tree build settle
            screen = transcript(t, log, "1. techmeme.com loaded — link tree")

            # The promotion log proves the classifier saw an aggregator. The
            # extraction may come from the page cache (the cached links were
            # produced by the same classifier), so scan today's log with a
            # short retry for flush latency.
            promos = []
            for _ in range(10):
                promos = app_log_lines("Aggregator page detected")
                if promos:
                    break
                time.sleep(2)
            if promos:
                print(f"  log: {promos[-1].split(']')[-1].strip()}")
            else:
                failures.append("aggregator promotion never fired on techmeme.com")

            # The old failure mode: everything under one 'External' group.
            # Now real story headlines (long link rows) must be visible in the
            # main list. Count plausible headline rows on the first screen.
            headline_rows = [
                l for l in screen.splitlines()
                if len(l.strip()) > 60 and not l.strip().startswith(("│", "─", "╭", "╰"))
            ]
            if len(headline_rows) < 5:
                failures.append(
                    f"expected >=5 long headline rows on the first screen, saw {len(headline_rows)}")

            # --- Ctrl+L -> AI-first entry card ---
            t.send_keys("C-l")
            t.wait_for("How should WireCopy read this site?", timeout=20)
            transcript(t, log, "2. Ctrl+L entry card (AI-first)")
            t.send_keys("Enter")  # ✨ Let AI find the stories

            # --- Round 1 → questions (if any) → round 2 → PREVIEW ---
            deadline = time.time() + 360
            seen_q = 0
            while time.time() < deadline:
                screen = t.capture()
                if "Looks good" in screen or "plain document order" in screen:
                    failures.append("a removed confirmation-theater option resurfaced")
                    break
                if "Your new layout" in screen or "No reliable pattern found" in screen:
                    break
                if "Set up this site with AI ·" in screen:
                    seen_q += 1
                    transcript(t, log, f"3.{seen_q} question card (Enter accepts the focused option)")
                    t.send_keys("Enter")
                    time.sleep(1)
                else:
                    time.sleep(2)
            else:
                failures.append("never reached the preview (or failure) card")

            screen = t.capture()
            if "No reliable pattern found" in screen:
                # The honest failure card is BETTER than a fake preview, but on
                # Techmeme the aggregator-aware analyzer is expected to succeed.
                transcript(t, log, "4. FAILURE CARD (unexpected on techmeme)")
                failures.append("analyzer could not find a pattern on techmeme.com")
            elif "Your new layout" in screen:
                screen = transcript(t, log, "4. PREVIEW — sections + coverage on the real tree")
                cov = re.search(r"(\d+) of (\d+) story links covered", screen)
                if not cov:
                    failures.append("preview caption shows no coverage line")
                else:
                    covered, total = int(cov.group(1)), int(cov.group(2))
                    print(f"  coverage: {covered} of {total} story links")
                    if covered == 0:
                        failures.append("preview presented a 0-coverage layout")
                    if total < 15:
                        failures.append(
                            f"only {total} content links reached the wizard — "
                            "aggregator stories are still being misclassified")

                t.send_keys("Enter")  # save exactly what is previewed
                t.wait_for("Site set up", timeout=30)
                transcript(t, log, "5. saved — AI Curated with durable sections")

                time.sleep(2)
                screen = transcript(t, log, "6. tree rebuilt from the saved config")
                if "section" not in screen.lower():
                    failures.append("post-save screen shows no section structure")

        # --- Revisit in a FRESH process: durable config must route ---
        if not failures:
            print("relaunching for the revisit check…")
            with TermTest(url=URL, width=TERM_W, height=45) as t2:
                t2.wait_for("Techmeme", timeout=120)
                time.sleep(8)
                screen = transcript(t2, log, "7. REVISIT — fresh process, same URL")
                if "section" not in screen.lower() and "▼" not in screen:
                    failures.append("revisit did not route through the saved durable config")
                t2.send_keys("C-l")
                t2.wait_for("Layout", timeout=20)
                screen = transcript(t2, log, "8. Ctrl+L on revisit — configured summary")
                if "Reconfigure with AI" not in screen:
                    failures.append("revisit Ctrl+L did not open the configured summary")
                t2.send_keys("Escape")
    finally:
        with open(os.path.join(OUT_DIR, "transcript.txt"), "w") as fh:
            fh.write("\n".join(log))
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        xvfb.terminate()

    print(f"\ntranscript in {OUT_DIR}/transcript.txt")
    if failures:
        print("\nFAILURES:")
        for f in failures:
            print(f"  ✗ {f}")
        sys.exit(1)
    print("✓ 6yb7 techmeme live gate PASSED")


if __name__ == "__main__":
    main()
