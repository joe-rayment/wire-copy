#!/usr/bin/env python3
"""
Drive the REAL Ctrl+L AI layout wizard on techmeme.com and capture artifacts so
a human/vision judge can compare the CURATED link tree against what the page
actually shows. Unlike the existing gates (which assert wiring/log/coverage
proxies), this script makes NO pass/fail claim — it produces:

  - som.png        : the exact (badged, headful) screenshot the model curated from
  - 00-load.txt    : the link tree as first rendered on page load
  - 01-preview.txt : the wizard's "Your new layout" preview (the curated tree)
  - 02-reprev.txt  : the re-preview after a plain-English steering instruction
  - 03-saved.txt   : the tree rebuilt from the saved durable config
  - config.json    : the saved SiteHierarchyConfig (sections / exclude rules)
  - logs.txt       : relevant app-log lines (aggregator promotion, wizard screenshot)
  - transcript.txt : every captured screen, in order

Headful only: a real Chromium under Xvfb (never headless). The SoM screenshot is
captured by the app's own headful browser via CDP.

Usage:
  python3 scripts/judge_techmeme_curation.py --label baseline
  python3 scripts/judge_techmeme_curation.py --label steer1 \
      --steer "Drop sponsor posts and section hubs; lead with the top story, then the main river of headlines."
  python3 scripts/judge_techmeme_curation.py --label revisit --no-reset --no-wizard
"""

import argparse
import glob
import os
import re
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402

DISPLAY = ":95"
SCREEN_W, SCREEN_H = 1600, 900
TERM_W, TERM_H = 150, 50
URL = "https://www.techmeme.com/"
BASE_OUT = "/workspace/output/techmeme-judge"
HIER = os.path.expanduser("~/.local/share/WireCopy/hierarchy/www.techmeme.com.json")
PAGE_CACHE = os.path.expanduser("~/.local/share/WireCopy/page-cache")


def app_log_lines(pattern):
    logs = sorted(glob.glob("/workspace/logs/wirecopy-*.log"))
    if not logs:
        return []
    out = subprocess.run(["grep", "-h", pattern, logs[-1]],
                         capture_output=True, text=True).stdout
    return [l for l in out.splitlines() if l]


def x_screenshot(path):
    try:
        subprocess.run(["import", "-display", DISPLAY, "-window", "root", path],
                       check=True, env={**os.environ, "DISPLAY": DISPLAY},
                       capture_output=True)
        return os.path.exists(path)
    except Exception as e:
        print(f"  (x_screenshot failed: {e})")
        return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--label", default="baseline")
    ap.add_argument("--steer", default="", help="plain-English steering instruction")
    ap.add_argument("--no-reset", action="store_true", help="keep existing config/cache")
    ap.add_argument("--no-wizard", action="store_true", help="just load + screenshot, no Ctrl+L")
    ap.add_argument("--no-save", action="store_true", help="do not Enter-save the layout")
    ap.add_argument("--dock", action="store_true", help="dock the real page (|) and grab it")
    ap.add_argument("--effort", default="", help="override OpenAiHierarchy:SetupReasoningEffort (minimal/low/medium/high)")
    ap.add_argument("--undo", action="store_true", help="after the steer, press 'z' to undo and capture the revert")
    args = ap.parse_args()

    if args.effort:
        os.environ["OpenAiHierarchy__SetupReasoningEffort"] = args.effort

    out = os.path.join(BASE_OUT, args.label)
    os.makedirs(out, exist_ok=True)
    log = []

    def cap(t, step, fname=None):
        screen = t.capture()
        log.append(f"\n===== {step} =====\n{screen}")
        if fname:
            with open(os.path.join(out, fname), "w") as fh:
                fh.write(screen)
        print(f"--- {step}")
        return screen

    if not args.no_reset:
        if os.path.exists(HIER):
            os.remove(HIER)
        for f in glob.glob(os.path.join(PAGE_CACHE, "*")):
            os.remove(f)
        print("reset: removed techmeme hierarchy config + cleared page cache")

    som = os.path.join(out, "som.png")
    os.environ["WIRECOPY_SOM_DEBUG"] = som

    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/techmeme-judge-tmux"
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
    time.sleep(1)

    cov_preview = cov_reprev = None
    try:
        with TermTest(url=URL, width=TERM_W, height=TERM_H) as t:
            t.wait_for("Techmeme", timeout=120)
            time.sleep(8)
            cap(t, "00. techmeme loaded — link tree on load", "00-load.txt")

            if args.dock:
                t.send_keys("|")
                try:
                    t.wait_for("docked", timeout=30)
                except Exception:
                    pass
                time.sleep(3)
                x_screenshot(os.path.join(out, "real-page-docked.png"))

            if not args.no_wizard:
                t.send_keys("g")  # 'g l' chord opens the AI layout wizard (was Ctrl+L)
                t.send_keys("l")
                t.wait_for("How should WireCopy read this site?", timeout=25)
                cap(t, "01. Ctrl+L entry card", "01a-entry.txt")
                t.send_keys("Enter")  # Let AI find the stories

                # round 1 → question cards → round 2 → preview
                deadline = time.time() + 360
                seen_q = 0
                while time.time() < deadline:
                    screen = t.capture()
                    if "Your new layout" in screen or "No reliable pattern found" in screen:
                        break
                    if "Set up this site with AI ·" in screen:
                        seen_q += 1
                        cap(t, f"question card {seen_q} (Enter accepts focused)",
                            f"01b-question{seen_q}.txt")
                        t.send_keys("Enter")
                        time.sleep(1)
                    else:
                        time.sleep(2)
                else:
                    cap(t, "TIMEOUT before preview", "timeout.txt")

                screen = cap(t, "02. PREVIEW — curated tree", "01-preview.txt")
                m = re.search(r"(\d+) of (\d+) story links covered", screen)
                cov_preview = (int(m.group(1)), int(m.group(2))) if m else None

                if args.steer:
                    if adjust_round(t, args.steer, out, log):
                        screen = cap(t, "03. RE-PREVIEW after steer", "02-reprev.txt")
                        m = re.search(r"(\d+) of (\d+) story links covered", screen)
                        cov_reprev = (int(m.group(1)), int(m.group(2))) if m else None

                        if args.undo:
                            # workspace-q77e: press 'z' to undo the refine; the
                            # preview must revert to the pre-steer layout.
                            t.send_keys("z")
                            time.sleep(2)
                            cap(t, "03b. AFTER UNDO (should match pre-steer 01-preview)", "02b-after-undo.txt")

                if not args.no_save and "Your new layout" in t.capture():
                    t.send_keys("Enter")
                    try:
                        t.wait_for("Site set up", timeout=30)
                    except Exception:
                        pass
                    time.sleep(2)
                    cap(t, "04. saved — tree from durable config", "03-saved.txt")
    finally:
        # copy the SoM dump (already at `som`); save config + logs + transcript
        if os.path.exists(HIER):
            shutil.copy(HIER, os.path.join(out, "config.json"))
        with open(os.path.join(out, "logs.txt"), "w") as fh:
            for pat in ["Aggregator page detected", "Wizard screenshot",
                        "Wizard lens:", "Wizard proceeding", "promoted"]:
                for l in app_log_lines(pat):
                    fh.write(l + "\n")
        with open(os.path.join(out, "transcript.txt"), "w") as fh:
            fh.write("\n".join(log))
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        xvfb.terminate()

    print(f"\nartifacts in {out}/")
    print(f"  SoM screenshot exists: {os.path.exists(som)} "
          f"({os.path.getsize(som)//1024 if os.path.exists(som) else 0} KB)")
    print(f"  coverage preview: {cov_preview}")
    if args.steer:
        print(f"  coverage after steer: {cov_reprev}")


def adjust_round(t, text, out, log):
    """Space → 'Adjust the layout' → free-text row → type → Enter → re-preview."""
    t.send_keys("Space")
    time.sleep(1)
    screen = t.capture()
    if "Adjust the layout" not in screen:
        print("  adjust card did not open")
        return False
    log.append(f"\n===== adjust card =====\n{screen}")
    if "Point at the main story" in screen:
        t.send_keys("Down")
    t.send_keys("Enter")
    try:
        t.wait_for("Tell the AI what to change", timeout=15)
    except Exception:
        print("  free-text field did not open")
        return False
    t.send_text(text, delay=0.02)
    with open(os.path.join(out, "02a-typed-instruction.txt"), "w") as fh:
        fh.write(t.capture())
    t.send_keys("Enter")
    deadline = time.time() + 240
    while time.time() < deadline:
        screen = t.capture()
        if "Your new layout" in screen or "No reliable pattern found" in screen:
            return True
        time.sleep(2)
    print("  never returned to preview after steer")
    return False


if __name__ == "__main__":
    main()
