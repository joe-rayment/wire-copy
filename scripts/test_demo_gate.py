#!/usr/bin/env python3
"""
workspace-kt19.4 — video-readiness gate for the demo experience.

Runs the REAL app on a FRESH data dir (XDG_DATA_HOME -> temp), no network
beyond loopback required:

  1. Launcher seeds + renders the demo bookmarks ("The Daily Gazette" et al)
     and the list scrolls on a short terminal.
  2. Opening The Daily Gazette yields a SECTIONED link list served by the
     built-in loopback demo server (The Disaster at Sea / The War in Europe /
     Science & Industry / The Lighter Side + Advertisements).
  3. Opening a story shows the genuine 1912 text in reader view.
  4. The sidecar docks beside it (mobile lens) under Xvfb.

Usage: python3 scripts/test_demo_gate.py
Needs: tmux, Xvfb, a Release build (csproj copies demo-site beside the DLL).
"""

import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402

DISPLAY = ":97"


def main():
    data_home = tempfile.mkdtemp(prefix="wirecopy-demo-gate-")
    os.environ["XDG_DATA_HOME"] = data_home
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/demo-gate-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)

    lock = f"/tmp/.X{DISPLAY.lstrip(':')}-lock"
    if os.path.exists(lock):
        try:
            with open(lock) as fh:
                os.kill(int(fh.read().strip()), 0)
        except (OSError, ValueError):
            os.remove(lock)
    xvfb = subprocess.Popen(["Xvfb", DISPLAY, "-screen", "0", "1600x900x24"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    os.environ["DISPLAY"] = DISPLAY

    failures = []
    try:
        # Short terminal so 13 bookmarks force scrolling.
        with TermTest(url=None, width=100, height=35) as t:
            t.wait_for("DAILY GAZETTE", timeout=90)
            screen = t.capture()
            print("--- launcher up")
            up = screen.upper()
            if "FOREIGN DESK" not in up and "SCIENCE & INDUSTRY" not in up:
                failures.append("launcher missing demo bookmarks beyond the first")

            # Scroll: walk down repeatedly; a later entry must come into view.
            t.send_keys(*["j"] * 12)
            time.sleep(1)
            scrolled = t.capture().upper()
            if "FIGHTING" not in scrolled and "WATCH DOGS" not in scrolled and "CORLISS" not in scrolled:
                failures.append("launcher did not scroll to reveal later demo bookmarks")
            else:
                print("--- launcher scrolls")

            # Back to the top-left card (The Daily Gazette) and open it:
            # k to the top row, h to the left column, Enter.
            t.send_keys(*["k"] * 12)
            t.send_keys("h")
            time.sleep(0.5)
            t.send_keys("Enter")
            t.wait_for("Disaster at Sea", timeout=60)
            screen = t.capture()
            print("--- gazette front page open (sectioned)")
            # All sections won't fit a 35-row fold; the header counts them.
            m = re.search(r"(\d+) sections", screen)
            if not m or int(m.group(1)) < 5:
                failures.append("front page header does not report 5+ sections")

            # Open the initially-selected story (the Titanic lead).
            t.send_keys("Enter")
            t.wait_for("Titanic", timeout=60)
            time.sleep(2)
            print("--- article open")

            # Sidecar affordance (docked browser beside the app).
            screen = t.capture()
            if "docked" not in screen and "⇉" not in screen:
                # not fatal for the content demo, but the video wants it
                failures.append("sidecar not engaged on the demo article")
            else:
                print("--- sidecar docked")
    finally:
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        xvfb.terminate()
        shutil.rmtree(data_home, ignore_errors=True)

    if failures:
        print("\nFAILURES:")
        for f in failures:
            print(f"  ✗ {f}")
        sys.exit(1)
    print("✓ demo gate PASSED — fresh install shows the full Daily Gazette experience")


if __name__ == "__main__":
    main()
