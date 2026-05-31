#!/usr/bin/env python3
"""B12a single-session probe for the remaining acceptance items: a recipe created in
a PRIOR process is present after reload (persistence), space toggles enabled, R starts
a run, and an UNCONFIGURED site is blocked from being pinned. Frames -> docs/qa."""
import sys, time, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest
import test_schedules_tui as g


def main():
    with TermTest(url="https://text.npr.org", width=120, height=40) as t:
        if not g.wait_content(t):
            print("ERR load"); return 1
        g.open_schedules(t)
        s = g.save_frame(t, "06_after_reload")
        print("STEP persists across reload ->", "NPR Brief" in s)

        g.select_option(t, "NPR Brief")
        before = g.selected_line(t.capture())
        t.send_keys("Space"); time.sleep(0.8)
        after = g.selected_line(t.capture())
        g.save_frame(t, "07_toggled")
        print("STEP space toggles enabled ->", ("○" in after) != ("○" in before), "| before:", before.strip()[:20], "after:", after.strip()[:20])
        t.send_keys("Space"); time.sleep(0.6)  # back on

        g.select_option(t, "NPR Brief")
        t.send_keys("R"); time.sleep(1.5)
        s = g.save_frame(t, "08_run_now")
        # run-now kicks off in the background (status is transient, behind the
        # overlay); the verifiable signal is no crash + the screen stays intact.
        print("STEP run-now starts (no crash) ->", "NPR Brief" in s and "went wrong" not in s)

        # unconfigured-site block: selecting Techmeme (no saved config) must NOT add a
        # step — the Sources list stays at zero steps ("needs at least one required").
        t.send_keys("a"); time.sleep(0.8)
        g.type_field(t, "Should Not Save")
        g.select_option(t, "Add a source")
        t.send_keys("Enter"); time.sleep(0.8)
        if g.select_option(t, "techmeme"):
            t.send_keys("Enter"); time.sleep(1.2)
            s = g.save_frame(t, "09_unconfigured_blocked")
            blocked = "Pick a section" not in s and "needs at least one required step" in s
            print("STEP unconfigured blocked (no step added) ->", blocked)
        else:
            print("STEP unconfigured blocked -> techmeme not in picker (FAIL)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
