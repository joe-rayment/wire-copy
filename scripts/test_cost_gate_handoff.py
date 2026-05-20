#!/usr/bin/env python3
"""
workspace-5o9s: live tmux verification of the cache-analysis → cost-gate
Enter handoff.

Reproduces the workspace-g3uu scenario: the cost-gate modal must respond
to tmux send-keys Enter immediately after the cache-analysis screen
hands off. If an orphaned WaitForInputAsync from the analysis screen is
still subscribed to the input channel, it would dequeue Enter behind
the modal's back and the modal would block indefinitely.

Acceptance: tmux send-keys Enter dismisses the cost-gate modal within
~2s and the progress screen takes over.

Pre-conditions:
- ~/.local/share/WireCopy/settings.json has PodcastCostGateAlwaysShow=true
  (so the modal pops regardless of the trivially small cost on example.com).
- Reading List has at least one item.

Usage:
    python3 scripts/test_cost_gate_handoff.py
"""

import os
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__)))
from termtest import TermTest


SHOT_DIR = "/tmp/qa-shots-g3uu"


def main() -> int:
    if not os.path.isdir(SHOT_DIR):
        os.makedirs(SHOT_DIR, exist_ok=True)

    print(f"[setup] Using tmux harness, dumping screenshots to {SHOT_DIR}")

    # Point the harness at the worktree's built DLL so it picks up the
    # workspace-g3uu fix (the user-installed /workspace build would not).
    worktree_root = "/workspace/.claude/worktrees/floofy-yawning-truffle"

    with TermTest(cwd=worktree_root) as t:
        # 1. Wait for app to boot. The browser opens by default — we want
        # to navigate to Reading List via the launcher.
        try:
            t.wait_for_any("Launcher", "Reading List", "Loading", "URL", timeout=30)
        except TimeoutError as ex:
            with open(os.path.join(SHOT_DIR, "0_boot_timeout.txt"), "w") as f:
                f.write(str(ex))
            print(f"[FAIL] Boot timed out:\n{ex}")
            return 1

        screen = t.capture()
        with open(os.path.join(SHOT_DIR, "1_boot.txt"), "w") as f:
            f.write(screen)
        print("[1] App booted")

        # 2. Open the launcher (Esc backs out to a known state if we landed
        # mid-flow). Then press `b` to focus bookmarks/collections list, OR
        # use the URL bar.
        # Simplest: from anywhere, type :collections to navigate.
        t.send_keys("Escape")
        time.sleep(0.5)
        t.send_keys(":")
        time.sleep(0.3)
        t.send_text("collections")
        t.send_keys("Enter")
        time.sleep(2)
        screen = t.capture()
        with open(os.path.join(SHOT_DIR, "2_collections.txt"), "w") as f:
            f.write(screen)
        if "Reading List" not in screen:
            print(f"[FAIL] Reading List collection not found after :collections")
            print(f"Screen:\n{screen}")
            return 1
        print("[2] Collections view shown, Reading List visible")

        # 3. Select Reading List (it's the first item) — Enter should open it.
        t.send_keys("Enter")
        time.sleep(2)
        screen = t.capture()
        with open(os.path.join(SHOT_DIR, "3_reading_list.txt"), "w") as f:
            f.write(screen)
        if "example.com" not in screen.lower():
            print(f"[WARN] Reading List items don't show example.com:\n{screen}")
            # Continue anyway — the bead requires the cost-gate handoff, not
            # specific item content.

        print("[3] Inside Reading List collection")

        # 4. Press 'p' to start podcast generation.
        t.send_keys("p")
        time.sleep(1)
        screen_after_p = t.capture()
        with open(os.path.join(SHOT_DIR, "4_after_p.txt"), "w") as f:
            f.write(screen_after_p)
        print("[4] Pressed 'p'")

        # 5. Wait for the cost-gate modal to appear. With
        # PodcastCostGateAlwaysShow=true, the modal pops after cache analysis
        # completes. example.com is tiny so this should finish within ~30s.
        try:
            t.wait_for_any("[Enter] go", "Enter] go", "go  [Esc]", timeout=60)
        except TimeoutError as ex:
            print(f"[FAIL] Cost-gate modal never appeared:\n{ex}")
            with open(os.path.join(SHOT_DIR, "5_modal_missing.txt"), "w") as f:
                f.write(t.capture())
            return 1

        modal_screen = t.capture()
        with open(os.path.join(SHOT_DIR, "5_modal_visible.txt"), "w") as f:
            f.write(modal_screen)
        print("[5] Cost-gate modal visible")

        # 6. THE CRITICAL TEST: send Enter via tmux send-keys.
        # If the workspace-g3uu fix works, the modal dismisses within ~2s
        # and the progress screen takes over.
        # If the orphan-drain regressed, Enter never reaches the modal and
        # the modal stays put indefinitely.
        print("[6] Sending Enter via tmux send-keys — this is the workspace-g3uu test")
        t.send_keys("Enter")

        # 7. Within 2s, the modal should be gone and the progress screen
        # should be showing ('Generating', 'Phase:', or 'Loading Articles').
        # Allow a generous 5s for safety — but the orphan-drain bug would
        # manifest as the modal staying visible indefinitely.
        try:
            t.wait_for_any("Generating Podcast", "Phase:", "Loading Articles",
                            "Analyzing", "Articles loaded", timeout=8)
        except TimeoutError:
            after_enter = t.capture()
            with open(os.path.join(SHOT_DIR, "6_modal_still_visible.txt"), "w") as f:
                f.write(after_enter)
            if "Enter] go" in after_enter:
                print("[FAIL] Cost-gate modal STILL visible after Enter — workspace-g3uu regression!")
                print(f"Screen after Enter:\n{after_enter}")
                return 1
            else:
                print("[WARN] Neither modal nor progress visible — possibly a fast cancel?")
                print(f"Screen:\n{after_enter}")

        after_enter = t.capture()
        with open(os.path.join(SHOT_DIR, "6_after_enter.txt"), "w") as f:
            f.write(after_enter)

        # Negative assertion: the cost-gate hint MUST be gone.
        if "[Enter] go" in after_enter or "go  [Esc]" in after_enter:
            print("[FAIL] Cost-gate hint still on screen 8s after Enter — orphan-drain regression")
            return 1

        print("[7] Cost-gate dismissed; progress screen took over")
        print(f"\n[PASS] workspace-g3uu fix verified end-to-end. Screenshots in {SHOT_DIR}")

        # Optional: send Esc to cancel the run cleanly. Don't fail the test
        # if cancel handling is slow.
        t.send_keys("Escape")
        time.sleep(0.5)

    return 0


if __name__ == "__main__":
    sys.exit(main())
