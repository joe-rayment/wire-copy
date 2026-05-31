#!/usr/bin/env python3
"""Single-session probe for the B12a create flow (one browser launch, < 90s).
Opens :schedules, creates a recipe over text.npr.org via the pickers, verifies it
lands in the list. Saves frames to docs/qa/workspace-frpl.14/."""
import sys, time, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest
import test_schedules_tui as g

QA = g.QA_DIR
os.makedirs(QA, exist_ok=True)


def main():
    with TermTest(url="https://text.npr.org", width=120, height=40) as t:
        if not g.wait_content(t):
            print("ERR: page did not load"); return 1
        print("STEP loaded OK")
        g.open_schedules(t)
        s = g.save_frame(t, "01_empty_list")
        print("STEP open :schedules ->", "Schedules" in s and "No schedules yet" in s)

        t.send_keys("a"); time.sleep(0.8)
        g.type_field(t, "NPR Brief")
        g.select_option(t, "Add a source")
        t.send_keys("Enter"); time.sleep(0.8)
        s = t.capture(); print("STEP site picker ->", "npr.org" in s)
        g.select_option(t, "npr.org"); t.send_keys("Enter"); time.sleep(0.8)

        s = g.save_frame(t, "03_section_picker")
        print("STEP section picker ->", "Lead story" in s and "Main feed" in s)
        g.select_option(t, "Lead story"); t.send_keys("Enter"); time.sleep(0.6)
        g.select_option(t, "Whole section"); t.send_keys("Enter"); time.sleep(0.6)
        g.select_option(t, "Required"); t.send_keys("Enter"); time.sleep(0.8)

        s = g.save_frame(t, "04_sources_with_step")
        print("STEP step added ->", "Lead story" in s and "required" in s)
        g.select_option(t, "Done"); t.send_keys("Enter"); time.sleep(0.8)
        g.select_option(t, "Weekdays"); t.send_keys("Enter"); time.sleep(0.6)
        g.type_field(t, "")  # time default 07:00
        g.type_field(t, "")  # output name default
        time.sleep(1.2)
        s = g.save_frame(t, "05_saved_list")
        ok = "NPR Brief" in s and "Mon" in s
        print("STEP saved+listed ->", ok)
        print("RESULT", "OK" if ok else "FAIL")
        return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
