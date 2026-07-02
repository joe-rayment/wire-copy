#!/usr/bin/env python3
"""workspace-xx61 (qcnd): opening a link then going back must restore the user's
place (not dump them at the top). Drive techmeme: move selection down far enough
to force a scroll, open the story, press 'b' to go back, and assert BOTH:
  - the same story is selected (matched on its TEXT, not the bare marker bar)
  - the list is scrolled back to the same viewport (top-of-screen content equal)
The Enter step is asserted too (screen must actually change) so a broken open
can't make the back-check pass vacuously (Verification Doctrine)."""
import os, re, subprocess, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402
DISPLAY=":95"
JUMPS=20  # far enough down that the list must scroll on a 45-row terminal

def selected_story_text(t):
    # A selected card renders several "▌"-prefixed rows; the first is a
    # content-free padding bar. Return the first marker row that carries
    # real story text so two different selections can never compare equal.
    for ln in t.capture_lines():
        if ("▌" in ln or "►" in ln) and re.search(r"[A-Za-z]{3,}", ln.replace("▌","").replace("►","")):
            return ln.strip()
    return ""

def top_content(t, n=5):
    return "\n".join(ln.rstrip() for ln in t.capture_lines()[:n])

def main():
    os.environ.pop("TMUX",None); os.environ["TMUX_TMPDIR"]="/tmp/backrestore-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"],exist_ok=True)
    subprocess.run(["tmux","kill-server"],capture_output=True)
    lock=f"/tmp/.X{DISPLAY.lstrip(':')}-lock"
    if os.path.exists(lock):
        try:
            os.kill(int(open(lock).read().strip()),0)
        except (OSError,ValueError): os.remove(lock)
    xvfb=subprocess.Popen(["Xvfb",DISPLAY,"-screen","0","1600x900x24"],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    os.environ["DISPLAY"]=DISPLAY; time.sleep(1)
    fail=[]
    try:
        with TermTest(url="https://www.techmeme.com/", width=150, height=45) as t:
            t.wait_for("Techmeme", timeout=120); time.sleep(6)
            top_initial = top_content(t)
            # move selection down far enough that the list must scroll
            for _ in range(JUMPS): t.send_keys("j"); time.sleep(0.1)
            time.sleep(0.5)
            before = selected_story_text(t)
            top_before = top_content(t)
            print("selected before open:", before[:80])
            if not before:
                fail.append("no selected story with text found before open")
            if top_before == top_initial:
                fail.append(f"{JUMPS} j-presses did not scroll the list — gate can't exercise scroll restore")
            list_screen = "\n".join(t.capture_lines())
            # open the selected story — and PROVE it opened
            t.send_keys("Enter")
            time.sleep(8)
            opened_screen = "\n".join(t.capture_lines())
            if opened_screen == list_screen:
                fail.append("Enter did not change the screen — story never opened; back-check would be vacuous")
            print("after Enter: screen changed =", opened_screen != list_screen)
            # go back
            t.send_keys("b")
            time.sleep(4)
            after = selected_story_text(t)
            top_after = top_content(t)
            print("selected after back :", after[:80])
            if not (before and after and before == after):
                fail.append(f"selection NOT restored on back (before='{before[:50]}' after='{after[:50]}')")
            if top_after != top_before:
                fail.append("scroll position NOT restored on back (viewport top differs)")
            elif not fail:
                print("✓ back restored the SAME selected story at the SAME scroll position")
    finally:
        subprocess.run(["tmux","kill-server"],capture_output=True); xvfb.terminate()
    if fail:
        print("\nFAILURES:"); [print("  ✗",f) for f in fail]; sys.exit(1)
    print("\n✓ back-restores-place verification PASSED")

if __name__=="__main__": main()
