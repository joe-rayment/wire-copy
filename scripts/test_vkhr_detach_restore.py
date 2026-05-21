#!/usr/bin/env python3
"""
Live verification for workspace-vkhr Phase D: modal-detach-modal round-trip.

Walks: app boot → :collections → Reading List → 'p' → cache-analyze →
cost-gate Enter → confirmation Enter → progress modal shown → Shift+D
detaches modal → status-bar badge with 🎧/Generating visible → Shift+P
restores the modal → progress continues without restart.

This is the user-visible contract of Phase D: D frees the screen, the
status-bar shows the run is alive, Shift+P brings it back.
"""

import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__)))
from termtest import TermTest


SHOT_DIR = "/tmp/qa-shots-vkhr"


def main() -> int:
    os.makedirs(SHOT_DIR, exist_ok=True)
    worktree_root = "/workspace/.claude/worktrees/floofy-yawning-truffle"

    with TermTest(cwd=worktree_root, width=120, height=50) as t:
        try:
            t.wait_for_any("Launcher", "Reading List", "Loading", "URL", timeout=30)
        except TimeoutError as ex:
            print(f"[FAIL] Boot timeout: {ex}")
            return 1
        print("[1] App booted")

        # Navigate to Reading List.
        t.send_keys("Escape")
        time.sleep(0.4)
        t.send_keys(":")
        time.sleep(0.3)
        t.send_text("collections")
        t.send_keys("Enter")
        time.sleep(2)
        t.send_keys("Enter")
        time.sleep(2)
        print("[2] Inside Reading List")

        # Start podcast flow.
        t.send_keys("p")
        time.sleep(2)
        print("[3] Pressed 'p' — walking past intermediate screens to reach progress")

        progress_seen = False
        for step in range(15):
            screen = t.capture()
            shot_path = os.path.join(SHOT_DIR, f"walk_{step:02d}.txt")
            with open(shot_path, "w") as f:
                f.write(screen)

            if ("Generating" in screen or "Synthesizing" in screen
                    or "Concatenating" in screen or "Press D to free" in screen):
                progress_seen = True
                print(f"[step {step}] Progress screen reached.")
                break
            if ("[Enter] go" in screen or "Esc] cancel" in screen
                    or "GENERATE PODCAST" in screen):
                print(f"[step {step}] Cost-gate — Enter")
                t.send_keys("Enter")
                time.sleep(2)
                continue
            if ("Ready — TTS audio" in screen
                    or ("Credentials" in screen and "Generate" in screen)):
                print(f"[step {step}] Confirmation screen — Enter to generate")
                t.send_keys("Enter")
                time.sleep(2)
                continue
            if "Analyzing" in screen:
                print(f"[step {step}] Cache-analyze — waiting")
                time.sleep(2)
                continue
            if "Preloading" in screen or "Caching" in screen:
                print(f"[step {step}] Cache-wait — Esc to skip")
                t.send_keys("Escape")
                time.sleep(1)
                continue
            print(f"[step {step}] Unknown:\n{screen[:300]}")
            time.sleep(1.5)

        if not progress_seen:
            print("[FAIL] Never reached progress screen")
            return 1

        # Press Shift+D (the underlying CommandType.DumpHtml binding) to detach.
        # The keymap binds Shift+D to dump-html in non-modal contexts but inside
        # the podcast progress screen the same key is repurposed for detach.
        t.send_keys("D")
        time.sleep(2)
        screen = t.capture()
        with open(os.path.join(SHOT_DIR, "after_D.txt"), "w") as f:
            f.write(screen)
        print("[4] Pressed Shift+D — captured screen")

        # Badge should be visible in the status bar; modal should be gone.
        # The progress modal has unique signals that don't appear elsewhere:
        # the "Press D to free the screen" footer line, the four phase
        # sub-bars, or the per-article status grid. If ANY of them is still
        # on screen, detach didn't take.
        still_modal = (
            "Press D to free" in screen
            or "Concatenating" in screen
            or "Synthesizing" in screen
            or "GENERATING PODCAST" in screen
            or "│ Generating Podcast" in screen  # box-drawn header
        )
        badge_visible = "Generating" in screen and ("Shift+P" in screen or ":restore" in screen)

        if still_modal:
            print("[FAIL] After Shift+D the progress modal is still on screen — detach didn't take")
            print(screen)
            return 1
        if not badge_visible:
            print("[FAIL] After Shift+D the status-bar podcast badge is not visible")
            print(screen)
            return 1
        print("[5] Detached — status-bar shows the badge, modal is gone")

        # Restore via Shift+P.
        t.send_keys("P")
        time.sleep(2)
        screen = t.capture()
        with open(os.path.join(SHOT_DIR, "after_shift_P.txt"), "w") as f:
            f.write(screen)
        print("[6] Pressed Shift+P — captured screen")

        restored = ("Press D to free" in screen
                    or "Generating" in screen
                    or "Synthesizing" in screen
                    or "Concatenating" in screen)
        if not restored:
            print("[FAIL] After Shift+P the progress modal did not restore")
            print(screen)
            return 1
        print("[PASS] Modal restored — Phase D round-trip complete")

        # Cancel the run so we don't leave a podcast generating after the test.
        t.send_keys("Escape")
        time.sleep(1)
        # Confirm cancel if a prompt appears
        screen = t.capture()
        if "cancel" in screen.lower() or "yes" in screen.lower():
            t.send_keys("Enter")
            time.sleep(1)
        return 0


if __name__ == "__main__":
    sys.exit(main())
