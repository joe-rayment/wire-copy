#!/usr/bin/env python3
"""
Live verification for workspace-sfhy: when TTS is configured the Generate
row on the confirmation screen must be the default focus.

Walks: app boot → :collections → Enter (Reading List) → 'p' → step past any
analyze/cost-gate screens until the confirmation screen appears, then
screenshots and looks for the focus marker (▌) on the Generate row.
"""

import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__)))
from termtest import TermTest


SHOT_DIR = "/tmp/qa-shots-sfhy"
MARKER = "▌"


def screen_has_confirmation(screen: str) -> bool:
    """Confirmation screen has both the box header AND credential rows."""
    if "Generate Podcast" not in screen:
        return False
    # The credential block is unique to the confirmation screen.
    return "OpenAI TTS API key" in screen or "GCS bucket" in screen


def screen_is_progress(screen: str) -> bool:
    return "Generating" in screen or "Synthesizing" in screen or "Concatenating" in screen


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

        # Navigate to Reading List collection.
        t.send_keys("Escape")
        time.sleep(0.4)
        t.send_keys(":")
        time.sleep(0.3)
        t.send_text("collections")
        t.send_keys("Enter")
        time.sleep(2)
        screen = t.capture()
        with open(os.path.join(SHOT_DIR, "2_collections.txt"), "w") as f:
            f.write(screen)
        if "Reading List" not in screen:
            print("[FAIL] No Reading List visible")
            print(screen)
            return 1
        print("[2] Collections view")

        t.send_keys("Enter")
        time.sleep(2)
        screen = t.capture()
        with open(os.path.join(SHOT_DIR, "3_reading_list.txt"), "w") as f:
            f.write(screen)
        print("[3] Inside Reading List")

        # Start podcast flow.
        t.send_keys("p")
        time.sleep(2)
        print("[4] Pressed 'p' — walking past intermediate screens to reach confirm")

        for step in range(8):
            screen = t.capture()
            shot_path = os.path.join(SHOT_DIR, f"5_step_{step:02d}.txt")
            with open(shot_path, "w") as f:
                f.write(screen)

            if screen_has_confirmation(screen):
                print(f"[step {step}] Reached confirmation screen.")
                break
            if screen_is_progress(screen):
                print(f"[step {step}] Generation already in progress — confirm step was skipped (selectedIndex==Generate + Enter fired by accident?). Screen:")
                print(screen)
                return 1
            if "Analyzing" in screen or "0 of" in screen or "cache" in screen.lower()[:200]:
                print(f"[step {step}] Cache-analyze screen — waiting...")
                time.sleep(2)
                continue
            if ("[Enter] go" in screen or "Esc] cancel" in screen
                    or "GENERATE PODCAST" in screen or "est. $" in screen):
                print(f"[step {step}] Cost-gate modal — pressing Enter to proceed")
                t.send_keys("Enter")
                time.sleep(2)
                continue
            if "Preload" in screen or "Preloading" in screen or "Caching" in screen:
                print(f"[step {step}] Cache-wait screen — pressing Esc to skip wait")
                t.send_keys("Escape")
                time.sleep(1)
                continue

            # Unknown intermediate state — wait a beat
            print(f"[step {step}] Unknown screen, waiting:\n{screen[:400]}")
            time.sleep(1.5)
        else:
            print("[FAIL] Never reached the confirmation screen after 8 steps")
            return 1

        # On the confirmation screen now.
        screen = t.capture()
        with open(os.path.join(SHOT_DIR, "6_confirm.txt"), "w") as f:
            f.write(screen)
        print("=== confirmation SCREEN ===")
        print(screen)
        print("===========================")

        lines = screen.splitlines()
        generate_lines = [
            ln for ln in lines
            if "Generate" in ln
            and ("Publish" in ln or "Locally" in ln or "set OpenAI" in ln)
        ]
        if not generate_lines:
            print("[FAIL] Confirmation screen reached but no Generate CTA row found")
            return 1

        focused = [ln for ln in generate_lines if MARKER in ln]
        if focused:
            print(f"[PASS] Generate row carries focus marker ▌:")
            for ln in focused:
                print(f"   {ln.rstrip()}")
            return 0

        print("[FAIL] Generate row found but no ▌ marker on it")
        for ln in generate_lines:
            print(f"   {ln.rstrip()}")
        print("\nLines with ▌:")
        for ln in lines:
            if MARKER in ln:
                print(f"   {ln.rstrip()}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
