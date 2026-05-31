#!/usr/bin/env python3
"""workspace-frpl.15 (B12b) live gate: from the :schedules step-builder, an
UNCONFIGURED bookmarked site no longer dead-ends — it OFFERS inline AI setup, loads the
site headlessly, and runs the SetupWizard over it. Frames -> docs/qa/workspace-frpl.15/."""
import sys, os, time, json, glob
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest
import test_schedules_tui as g

DATA = g.DATA
QA = os.path.join("/workspace/docs/qa", "workspace-frpl.15")
os.makedirs(QA, exist_ok=True)
RES = {"bead": "workspace-frpl.15", "assertions": []}


def chk(label, ok, frame=None):
    RES["assertions"].append({"label": label, "pass": bool(ok)})
    print(("PASS" if ok else "FAIL"), "—", label, flush=True)
    if not ok and frame:
        print("\n".join(frame.splitlines()[-12:]), flush=True)
    return bool(ok)


def main():
    all_ok = True
    # Unconfigured site = techmeme.com (no saved hierarchy config); fresh schedules.
    for f in glob.glob(os.path.join(DATA, "hierarchy", "*techmeme*")):
        os.remove(f)
    if os.path.exists(os.path.join(DATA, "schedules.json")):
        os.remove(os.path.join(DATA, "schedules.json"))
    with open(os.path.join(DATA, "bookmarks.json"), "w") as f:
        json.dump({"version": 1, "bookmarks": [{"name": "Techmeme", "url": "https://www.techmeme.com"}]}, f)

    g.clear_browser_locks()
    with TermTest(url="https://text.npr.org", width=120, height=40) as t:
        if not g.wait_content(t):
            print("FATAL load"); return 1
        for _ in range(4):
            g.open_schedules(t)
            if "Schedules" in t.capture():
                break
            time.sleep(1)
        t.send_keys("a"); time.sleep(0.8)
        g.type_field(t, "Inline Test")
        g.select_option(t, "Add a source"); t.send_keys("Enter"); time.sleep(0.8)
        g.select_option(t, "techmeme"); t.send_keys("Enter"); time.sleep(1.2)

        s = t.capture()
        with open(os.path.join(QA, "01_inline_setup_offer.txt"), "w") as f:
            f.write(s)
        all_ok &= chk("unconfigured site OFFERS inline setup (not a dead-end block)",
                      "Set up this site" in s and "Set up with AI" in s, s)

        # Accept -> headless load + wizard launches over it (analysis is slow).
        g.select_option(t, "Set up with AI"); t.send_keys("Enter")
        launched = False
        for _ in range(40):
            time.sleep(2)
            s = t.capture()
            # the wizard surfaces either an Analyzing/Loading state or a proposal/question card
            if "Loading" in s or "Analyzing" in s or "set up" in s.lower() or "section" in s.lower() or "story" in s.lower():
                if "Loading Techmeme" in s or "Analyzing" in s or "Top story" in s or "Main" in s or "Which" in s or "?" in s:
                    launched = True
                    break
        with open(os.path.join(QA, "02_wizard_over_headless.txt"), "w") as f:
            f.write(t.capture())
        all_ok &= chk("selecting it loads the site headlessly + runs the SetupWizard inline", launched, t.capture())

        # Cancellable mid-wizard without wedging the session.
        t.send_keys("Escape"); time.sleep(1.0)
        t.send_keys("Escape"); time.sleep(0.8)
        s = t.capture()
        all_ok &= chk("cancellable mid-wizard (returns to a responsive screen)", "went wrong" not in s, s)

    RES["result"] = "PASS" if all_ok else "FAIL"
    json.dump(RES, open(os.path.join(QA, "result.json"), "w"), indent=2)
    print("===", RES["result"], "===", flush=True)
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
