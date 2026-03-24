#!/usr/bin/env python3
"""
Test the podcast confirmation screen keystroke handling.

Validates workspace-o4oj: pressing keys on the podcast screen should NOT
exit back to the collection view.
"""

import sys
import time
sys.path.insert(0, "scripts")
from termtest import TermTest


def test_podcast_screen():
    print("=== Testing Podcast Screen Keystroke Handling ===\n")

    with TermTest(url="https://example.com") as t:
        # 1. Wait for page to load
        t.wait_for_any("Example Domain", "LinkView", "Error", timeout=30)
        print("[1] Page loaded")

        # 2. Save this page to reading list
        t.send_keys("s")
        time.sleep(2)
        print("[2] Saved page")

        # 3. Open collections via command
        t.send_keys(":")
        time.sleep(0.5)
        t.send_text("collections")
        t.send_keys("Enter")
        time.sleep(2)
        screen = t.screenshot("collections")

        if "collections" not in screen.lower():
            print("[3] FAIL: Could not open collections")
            return False
        print("[3] Collections view open")

        # 4. Open the first collection (Reading List)
        t.send_keys("Enter")
        time.sleep(2)
        screen = t.screenshot("reading list items")

        # Verify we're in collection items view
        if "reading list" not in screen.lower() and "item" not in screen.lower():
            print("[4] WARN: May not be in Reading List, trying anyway")
        else:
            print("[4] In Reading List")

        # 5. Press 'p' to open podcast screen
        print("\n[5] Pressing 'p' to open podcast screen...")
        t.send_keys("p")
        time.sleep(3)
        screen = t.screenshot("podcast screen")

        on_podcast = (
            "generate podcast" in screen.lower()
            or "credentials" in screen.lower()
            or "openai" in screen.lower()
        )
        print(f"    On podcast screen? {on_podcast}")

        if not on_podcast:
            print("[FAIL] Could not reach podcast screen")
            return False

        # 6. Test unhandled keys - each should NOT exit the screen
        print("\n[6] Testing unhandled keys...")
        all_pass = True

        unhandled_keys = ["j", "x", "w", "m", "1", "2", "z"]
        for key in unhandled_keys:
            t.send_keys(key)
            time.sleep(0.5)
            screen = t.capture()
            still_on = (
                "generate podcast" in screen.lower()
                or "credentials" in screen.lower()
                or "openai" in screen.lower()
            )
            status = "PASS" if still_on else "FAIL"
            print(f"    Key '{key}': [{status}]")
            if not still_on:
                t.screenshot(f"FAILED after '{key}'")
                all_pass = False
                break

        if not all_pass:
            print("\n[FAIL] An unhandled key exited the podcast screen!")
            return False

        # 7. Test ':' key opens bucket prompt
        print("\n[7] Testing ':' key...")
        t.send_keys(":")
        time.sleep(1)
        screen = t.screenshot("after colon")
        colon_ok = "bucket" in screen.lower() or "gcs" in screen.lower()
        print(f"    ':' shows bucket prompt? {colon_ok}")

        # Cancel the prompt
        t.send_keys("Escape")
        time.sleep(1)

        # Verify still on podcast screen
        screen = t.capture()
        still_on = "generate podcast" in screen.lower() or "credentials" in screen.lower()
        print(f"    Still on podcast screen after Esc from prompt? {still_on}")

        if not still_on:
            print("[FAIL] Exited podcast screen after cancelling ':' prompt!")
            t.screenshot("after escape from colon")
            return False

        # 8. Test Enter prompts for API key (since TTS not configured)
        print("\n[8] Testing Enter (should prompt for API key)...")
        t.send_keys("Enter")
        time.sleep(1)
        screen = t.screenshot("after Enter")
        enter_ok = "api key" in screen.lower() or "openai" in screen.lower() or "platform" in screen.lower()
        print(f"    Enter shows API key prompt? {enter_ok}")

        # Cancel the API key prompt
        t.send_keys("Escape")
        time.sleep(1)

        # Verify still on podcast screen
        screen = t.capture()
        still_on = "generate podcast" in screen.lower() or "credentials" in screen.lower()
        print(f"    Still on podcast screen after cancelling API prompt? {still_on}")

        if not still_on:
            t.screenshot("after escape from api key prompt")

        # 9. Exit via Escape
        print("\n[9] Exiting with Escape...")
        t.send_keys("Escape")
        time.sleep(1)
        screen = t.screenshot("after final Escape")
        left = "generate podcast" not in screen.lower()
        print(f"    Exited podcast screen? {left}")

        print(f"\n=== {'ALL TESTS PASSED' if all_pass else 'SOME TESTS FAILED'} ===")
        return all_pass


if __name__ == "__main__":
    success = test_podcast_screen()
    sys.exit(0 if success else 1)
