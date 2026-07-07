#!/usr/bin/env python3
"""
Drive the AI layout wizard on LIVE techmeme.com and capture BOTH the screenshot
the analyzer judged against (WIRECOPY_SOM_DEBUG, badge-marked full page) and the
resulting layout (preview headlines + saved tree), so a human/vision judge can
score fidelity of the extracted layout vs the visual page.

Outputs (in OUT_DIR):
  shot.png           — full-page techmeme screenshot with numbered link badges
  preview.txt        — the 'Your new layout' preview (sections + extracted headlines)
  tree.txt           — the saved link tree
  links.txt          — first-screen link rows before setup

Usage: python3 scripts/judge_techmeme_layout.py
Needs: tmux, Xvfb, a Release build, an OpenAI key in app settings, network.
"""

import glob
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402
from keys import choose_layout  # noqa: E402

DISPLAY = ":95"
SCREEN_W, SCREEN_H = 1600, 1200
TERM_W = 160
URL = "https://www.techmeme.com/"
OUT_DIR = "/workspace/output/techmeme-judge"


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    for d in ("hierarchy", "page-cache", "layouts"):
        for f in glob.glob(os.path.expanduser(f"~/.local/share/WireCopy/{d}/*")):
            try:
                os.remove(f)
            except OSError:
                pass

    os.environ["WIRECOPY_SOM_DEBUG"] = os.path.join(OUT_DIR, "shot.png")
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/judge-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)
    lock = f"/tmp/.X{DISPLAY.lstrip(':')}-lock"
    if os.path.exists(lock):
        os.remove(lock)
    xvfb = subprocess.Popen(
        ["Xvfb", DISPLAY, "-screen", "0", f"{SCREEN_W}x{SCREEN_H}x24"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    os.environ["DISPLAY"] = DISPLAY

    def dump(name, text):
        with open(os.path.join(OUT_DIR, name), "w") as fh:
            fh.write(text)

    try:
        with TermTest(url=URL, width=TERM_W, height=50) as t:
            t.wait_for("Techmeme", timeout=120)
            time.sleep(8)
            dump("links.txt", t.capture())
            print("techmeme loaded")

            choose_layout(t)
            t.wait_for("How should WireCopy read this site?", timeout=25)
            t.send_keys("Enter")  # ✨ Let AI figure out this site's layout

            deadline = time.time() + 400
            while time.time() < deadline:
                screen = t.capture()
                if "Your new layout" in screen or "No reliable pattern" in screen:
                    break
                if "Set up this site with AI ·" in screen:
                    dump("question.txt", screen)
                    t.send_keys("Enter")  # accept focused option
                time.sleep(2)

            screen = t.capture()
            dump("preview.txt", screen)
            if "Your new layout" in screen:
                print("reached preview; saving")
                t.send_keys("Enter")
                try:
                    t.wait_for("Site set up", timeout=30)
                except TimeoutError:
                    pass
                time.sleep(2)
                dump("tree.txt", t.capture())
            else:
                print("did NOT reach preview (failure card?)")
    finally:
        xvfb.terminate()
        subprocess.run(["tmux", "kill-server"], capture_output=True)

    shot = os.path.join(OUT_DIR, "shot.png")
    print(f"\nDONE. screenshot={'yes' if os.path.exists(shot) else 'MISSING'} · outputs in {OUT_DIR}")


if __name__ == "__main__":
    main()
