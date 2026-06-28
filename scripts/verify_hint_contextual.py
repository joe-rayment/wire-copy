#!/usr/bin/env python3
"""
Live verification (workspace-g801) that the contextual sidecar hints actually
appear in the status bar — the user-visible outcome, not a code path.

1. Load a link list (techmeme) headful under Xvfb.
2. Assert the status bar shows the 'dock the live browser · | dock' teach hint
   within a few seconds of landing (sidecar not auto-engaged by default).
3. Press '|' to dock; assert the status bar shows the 'hide · y adopt' hint.
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402

DISPLAY = ":95"
URL = "https://www.techmeme.com/"


def status_tail(t):
    # bottom 3 lines hold the status line(s)
    return "\n".join(t.capture_lines()[-3:])


def main():
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/hint-verify-tmux"
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
    time.sleep(1)

    failures = []
    try:
        with TermTest(url=URL, width=150, height=45) as t:
            t.wait_for("Techmeme", timeout=120)

            # 1) teach hint on entering the link list — poll fast (TTL ~6s), no pre-sleep
            teach = ""
            deadline = time.time() + 25
            while time.time() < deadline:
                s = status_tail(t)
                if "dock the live page" in s.lower() or ("see the live page" in s.lower()):
                    teach = s
                    break
                time.sleep(0.4)
            print("--- teach-hint capture ---\n" + (teach or status_tail(t)))
            if teach and "dock" in teach.lower():
                print("✓ teach hint present: 'See the live page beside the app · | dock'")
            else:
                failures.append("teach hint '| dock' NOT shown on link-list entry")

            # 2) dock with '|', expect 'Live page docked · | hide · y adopt page' (poll fast)
            t.send_keys("|")
            dock = ""
            deadline = time.time() + 20
            while time.time() < deadline:
                s = status_tail(t)
                if "docked" in s.lower() and ("hide" in s.lower() or "adopt" in s.lower()):
                    dock = s
                    break
                time.sleep(0.4)
            print("--- dock-hint capture ---\n" + (dock or status_tail(t)))
            if dock and "hide" in dock.lower():
                print("✓ dock hint present: 'Live page docked · | hide · y adopt page'")
            else:
                failures.append("dock hint 'hide · y adopt' NOT shown after docking")
    finally:
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        xvfb.terminate()

    if failures:
        print("\nFAILURES:")
        for f in failures:
            print("  ✗", f)
        sys.exit(1)
    print("\n✓ contextual hint verification PASSED")


if __name__ == "__main__":
    main()
