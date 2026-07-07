#!/usr/bin/env python3
"""
Live quality gate for epic workspace-romy: the visually-grounded AI layout
wizard on REAL aggregator sites (real network, real OpenAI round trips).

Per site this asserts, in one run:
  1. DEFAULT TREE (fresh cache): >= 5 story headline rows visible — the
     workspace-romy.9 regression ("techmeme shows no articles").
  2. VISION ENGAGED: app log shows 'Wizard screenshot attached' (romy.1) —
     the old 750ms cap meant the model almost never saw the page.
  3. SET-OF-MARKS: WIRECOPY_SOM_DEBUG dump exists and is a real PNG (romy.3).
  4. PREVIEW QUALITY: coverage N of M with N > 0, M >= 15; the FIRST section
     row must not be a sponsor/promo/event slot (romy.4/.5).
  5. ITERATION: two consecutive free-text adjust rounds, each typed PAST the
     old ~60-char input cap (romy.6) and each returning to a re-preview
     (romy.7), then quick-accept saves (romy.8 keeps Enter-saves).
  6. WARM CACHE: a fresh process on the same URL still shows headlines and
     routes the saved config (romy.9 extraction-version gate).

Usage:
  python3 scripts/test_romy_wizard_live.py                 # techmeme only
  python3 scripts/test_romy_wizard_live.py --sites all     # + HN, memeorandum
Needs: tmux, Xvfb, a Release build, an OpenAI key in app settings, network.
"""

import argparse
import glob
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402
from keys import choose_layout  # noqa: E402  # shared chord constants (scripts/keys.py)

DISPLAY = ":97"
OUT_DIR = "/workspace/output/romy-wizard-live"
TERM_W = 150

SITES = {
    "techmeme": ("https://www.techmeme.com/", "Techmeme"),
    "hn": ("https://news.ycombinator.com/", "Hacker News"),
    "memeorandum": ("https://www.memeorandum.com/", "memeorandum"),
}

# Instructions deliberately longer than the old ~60-char visible-width cap.
ADJUST_1 = "Please exclude all sponsor posts, event listings and job listings entirely from the layout"
ADJUST_2 = "Group the remaining stories into a single Top Story section followed by one River section"

SPONSOR_WORDS = ("sponsor", "promo", "advert", "event", "job", "hiring", "upcoming")


def app_log_lines(pattern):
    logs = sorted(glob.glob("/workspace/logs/wirecopy-*.log"))
    if not logs:
        return []
    out = subprocess.run(
        ["grep", "-h", pattern, logs[-1]], capture_output=True, text=True).stdout
    return [line for line in out.splitlines() if line]


def headline_rows(screen):
    return [
        l for l in screen.splitlines()
        if len(l.strip()) > 60 and not l.strip().startswith(("│", "─", "╭", "╰"))
    ]


class Gate:
    def __init__(self, site_key):
        self.site = site_key
        self.failures = []
        self.log = []

    def shot(self, t, step):
        screen = t.capture()
        self.log.append(f"\n===== [{self.site}] {step} =====\n{screen}")
        print(f"--- [{self.site}] {step}")
        return screen

    def fail(self, msg):
        self.failures.append(f"[{self.site}] {msg}")
        print(f"  ✗ {msg}")


def drive_to_preview(t, gate):
    """Ctrl+L → AI → answer question cards → return the preview screen (or None)."""
    choose_layout(t)  # g l = AI layout wizard
    # Slow, link-heavy pages (memeorandum: 600+ links) take longer to open the
    # chooser; the pre-flight phase also captures a screenshot first.
    t.wait_for("How should WireCopy read this site?", timeout=60)
    gate.shot(t, "entry card")
    t.send_keys("Enter")  # ✨ Let AI figure out this site's layout

    deadline = time.time() + 360
    seen_q = 0
    while time.time() < deadline:
        screen = t.capture()
        if "Your new layout" in screen or "No reliable pattern found" in screen:
            return screen
        if "Set up this site with AI ·" in screen:
            seen_q += 1
            gate.shot(t, f"question card {seen_q} (Enter accepts default)")
            t.send_keys("Enter")
            time.sleep(1)
        else:
            time.sleep(2)
    gate.fail("never reached the preview (or failure) card")
    return None


def adjust_round(t, gate, text, label):
    """Space → 'Tell the AI what to change…' → type text → wait for re-preview."""
    t.send_keys("Space")
    time.sleep(1)
    screen = t.capture()
    if "Adjust the layout" not in screen:
        gate.fail(f"{label}: adjust card did not open")
        return None

    # workspace-t1ok.7: options shifted (label mode is option 0) — walk the
    # cursor to the free-text row instead of assuming positions.
    for _ in range(6):
        cursor_line = next((l for l in t.capture().splitlines() if "▸" in l), "")
        if "Tell the AI" in cursor_line:
            break
        t.send_keys("Down")
        time.sleep(0.3)
    t.send_keys("Enter")
    try:
        t.wait_for("Tell the AI what to change", timeout=15)
    except Exception:
        gate.fail(f"{label}: free-text field did not open")
        return None

    t.send_text(text, delay=0.02)
    typed = t.capture()
    gate.shot(t, f"{label}: typed {len(text)} chars (input field)")

    # romy.6: the tail of a >60-char instruction must be visible (the scroll
    # window shows the end of the buffer; the old code froze at the box edge).
    tail = text[-20:]
    if tail not in typed.replace("…", ""):
        # The tail must appear on screen (window slid to keep cursor visible)
        if not any(tail[-12:] in line for line in typed.splitlines()):
            gate.fail(f"{label}: input did not scroll — tail of long instruction not visible")

    t.send_keys("Enter")
    deadline = time.time() + 240
    while time.time() < deadline:
        screen = t.capture()
        if "Your new layout" in screen or "No reliable pattern found" in screen:
            return gate.shot(t, f"{label}: re-preview")
        time.sleep(2)
    gate.fail(f"{label}: never returned to a preview after the adjustment")
    return None


def run_site(site_key, som_dir):
    url, marker = SITES[site_key]
    gate = Gate(site_key)
    som_path = os.path.join(som_dir, f"som-{site_key}.png")
    os.environ["WIRECOPY_SOM_DEBUG"] = som_path

    # Fresh path: wipe this site's configs and the whole page cache.
    for cfg in glob.glob(os.path.expanduser("~/.local/share/WireCopy/hierarchy/*")):
        os.remove(cfg)
    for cached in glob.glob(os.path.expanduser("~/.local/share/WireCopy/page-cache/*")):
        os.remove(cached)
    if os.path.exists(som_path):
        os.remove(som_path)

    with TermTest(url=url, width=TERM_W, height=45) as t:
        t.wait_for(marker, timeout=120)

        # Poll until the tree settles instead of a fixed sleep — heavy pages
        # (memeorandum) can still be extracting 8s after the title shows.
        rows, deadline = [], time.time() + 60
        while time.time() < deadline:
            rows = headline_rows(t.capture())
            if len(rows) >= 5:
                break
            time.sleep(3)
        screen = gate.shot(t, "1. default tree (fresh cache)")

        if len(rows) < 5:
            gate.fail(f"default tree shows only {len(rows)} headline rows (romy.9)")

        preview = drive_to_preview(t, gate)
        if preview is None:
            return gate

        # romy.1: vision telemetry.
        if app_log_lines("Wizard screenshot attached"):
            print("  ✓ wizard screenshot attached")
        else:
            textonly = app_log_lines("Wizard proceeding text-only")
            gate.fail(f"screenshot never attached (text-only={bool(textonly)}) (romy.1)")

        if "No reliable pattern found" in preview:
            gate.shot(t, "FAILURE CARD (unexpected)")
            gate.fail("analyzer could not find a pattern")
            return gate

        screen = gate.shot(t, "2. first preview")
        cov = re.search(r"(\d+) of (\d+) story links covered", screen)
        if not cov:
            gate.fail("preview shows no coverage line")
        else:
            covered, total = int(cov.group(1)), int(cov.group(2))
            print(f"  coverage: {covered} of {total}")
            if covered == 0:
                gate.fail("0-coverage preview")
            if total < 15:
                gate.fail(f"only {total} content links reached the wizard")

        # romy.4/.5: the FIRST section row must not be a promo slot. Section
        # rows render as '<name> — N link(s)'.
        section_rows = [l for l in screen.splitlines() if re.search(r"—\s+\d+ link", l)]
        if section_rows:
            first = section_rows[0].lower()
            if any(w in first for w in SPONSOR_WORDS):
                gate.fail(f"first section looks like a promo slot: {section_rows[0].strip()}")

        # romy.6/.7: two genuine adjust rounds, each re-previewed.
        if adjust_round(t, gate, ADJUST_1, "3. adjust round 1") is None:
            return gate
        if adjust_round(t, gate, ADJUST_2, "4. adjust round 2") is None:
            return gate

        # Save exactly what is previewed.
        t.send_keys("Enter")
        try:
            t.wait_for("Site set up", timeout=30)
            gate.shot(t, "5. saved")
        except Exception:
            gate.fail("save after two adjust rounds did not complete")
            return gate

    # romy.3: marked screenshot dump.
    if os.path.exists(som_path) and os.path.getsize(som_path) > 1000:
        with open(som_path, "rb") as fh:
            if fh.read(4) == b"\x89PNG":
                print(f"  ✓ SoM debug screenshot: {som_path} ({os.path.getsize(som_path)//1024} KB)")
            else:
                gate.fail("SoM debug dump is not a PNG")
    else:
        gate.fail("SoM debug screenshot was not written (romy.3)")

    # romy.9 warm path: fresh process, same URL, cache intact.
    with TermTest(url=url, width=TERM_W, height=45) as t2:
        t2.wait_for(marker, timeout=120)
        rows, deadline = [], time.time() + 60
        while time.time() < deadline:
            rows = headline_rows(t2.capture())
            if len(rows) >= 5:
                break
            time.sleep(3)
        gate.shot(t2, "6. warm-cache revisit (fresh process)")
        if len(rows) < 5:
            gate.fail("warm-cache revisit lost the headlines (romy.9 warm path)")

    return gate


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sites", default="techmeme",
                        help="comma list or 'all' (techmeme,hn,memeorandum)")
    args = parser.parse_args()
    keys = list(SITES) if args.sites == "all" else [s.strip() for s in args.sites.split(",")]

    os.makedirs(OUT_DIR, exist_ok=True)
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/romy-live-tmux"
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
        ["Xvfb", DISPLAY, "-screen", "0", "1600x900x24"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    os.environ["DISPLAY"] = DISPLAY

    all_failures, full_log = [], []
    try:
        for key in keys:
            try:
                gate = run_site(key, OUT_DIR)
            except Exception as ex:  # noqa: BLE001 — one site crashing must not kill the rest
                gate = Gate(key)
                first_line = str(ex).splitlines()[0] if str(ex) else type(ex).__name__
                gate.fail(f"crashed: {first_line}")
                gate.log.append(f"\n===== [{key}] CRASH =====\n{ex}")
            all_failures.extend(gate.failures)
            full_log.extend(gate.log)
            subprocess.run(["tmux", "kill-server"], capture_output=True)
    finally:
        with open(os.path.join(OUT_DIR, "transcript.txt"), "w") as fh:
            fh.write("\n".join(full_log))
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        xvfb.terminate()

    # Persist the verdict alongside the transcript so audits don't depend on
    # captured stdout.
    with open(os.path.join(OUT_DIR, "verdict.txt"), "w") as fh:
        fh.write(f"sites: {', '.join(keys)}\n")
        fh.write("PASSED\n" if not all_failures else "FAILED\n")
        for f in all_failures:
            fh.write(f"  ✗ {f}\n")

    print(f"\ntranscript in {OUT_DIR}/transcript.txt")
    if all_failures:
        print("\nFAILURES:")
        for f in all_failures:
            print(f"  ✗ {f}")
        sys.exit(1)
    print(f"✓ romy wizard live gate PASSED ({', '.join(keys)})")


if __name__ == "__main__":
    main()
