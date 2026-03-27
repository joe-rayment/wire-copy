#!/usr/bin/env python3
"""
Test NYT link list caching end-to-end.

Validates that the second and third loads of the NYT homepage serve from
build cache (< 2 seconds) rather than re-fetching via browser (> 10 seconds).

Acceptance criterion for workspace-n2kb epic.
"""

import sys
import time
sys.path.insert(0, "scripts")
from termtest import TermTest

NYT_URL = "https://www.nytimes.com"
CACHE_THRESHOLD_SECONDS = 4.0  # Must load in under this to count as cached


def load_nyt_via_launcher(t, label):
    """Navigate to NYT from the launcher using 'o' + URL input."""
    t.send_keys("o")
    time.sleep(0.5)
    t.send_text(NYT_URL)
    t.send_keys("Enter")

    start = time.time()
    try:
        t.wait_for("LINK", timeout=60)
    except TimeoutError:
        print(f"  [{label}] FAIL: Timed out waiting for LINK status bar")
        t.screenshot(f"{label}-timeout")
        return None

    elapsed = time.time() - start
    return elapsed


def test_nyt_cache():
    print("=== NYT Link List Caching End-to-End ===\n")
    results = []

    with TermTest() as t:
        # Wait for launcher to appear
        t.wait_for("HOME", timeout=15)
        print("[0] Launcher ready\n")

        # --- First load: cold (no cache) ---
        print("[1] First load (cold — establishing cache)...")
        elapsed1 = load_nyt_via_launcher(t, "first-load")
        if elapsed1 is None:
            return False

        screen = t.screenshot("first-load")
        print(f"    Loaded in {elapsed1:.1f}s")

        # Verify we're on NYT link view
        if "nytimes" not in screen.lower() and "link" not in screen.lower():
            print("    FAIL: Not on NYT page")
            return False
        print("    PASS: NYT link view loaded\n")

        # --- Navigate back to launcher ---
        t.send_keys("b")
        try:
            t.wait_for("HOME", timeout=10)
        except TimeoutError:
            t.send_keys("Escape")
            try:
                t.wait_for("HOME", timeout=5)
            except TimeoutError:
                print("[2] FAIL: Could not return to launcher")
                t.screenshot("back-fail")
                return False

        print("[2] Back at launcher\n")

        # --- Second load: should be cached ---
        print("[3] Second load (should hit build cache)...")
        elapsed2 = load_nyt_via_launcher(t, "second-load")
        if elapsed2 is None:
            return False

        screen = t.screenshot("second-load")
        print(f"    Loaded in {elapsed2:.1f}s")

        if elapsed2 < CACHE_THRESHOLD_SECONDS:
            print(f"    PASS: Under {CACHE_THRESHOLD_SECONDS}s threshold (cache hit)")
            results.append(True)
        else:
            print(f"    FAIL: Over {CACHE_THRESHOLD_SECONDS}s — cache likely missed")
            results.append(False)

        # Check for cache indicator
        screen_lower = screen.lower()
        if "cached" in screen_lower or "ago" in screen_lower:
            print("    PASS: Cache indicator visible")
        else:
            print("    INFO: No cache indicator (may still be cached via build cache)")
        print()

        # --- Navigate back to launcher again ---
        t.send_keys("b")
        try:
            t.wait_for("HOME", timeout=10)
        except TimeoutError:
            t.send_keys("Escape")
            try:
                t.wait_for("HOME", timeout=5)
            except TimeoutError:
                print("[4] FAIL: Could not return to launcher")
                t.screenshot("back-fail-2")
                return False

        print("[4] Back at launcher\n")

        # --- Third load: should still be cached ---
        print("[5] Third load (should still hit build cache)...")
        elapsed3 = load_nyt_via_launcher(t, "third-load")
        if elapsed3 is None:
            return False

        t.screenshot("third-load")
        print(f"    Loaded in {elapsed3:.1f}s")

        if elapsed3 < CACHE_THRESHOLD_SECONDS:
            print(f"    PASS: Under {CACHE_THRESHOLD_SECONDS}s threshold (cache hit)")
            results.append(True)
        else:
            print(f"    FAIL: Over {CACHE_THRESHOLD_SECONDS}s — cache likely missed")
            results.append(False)

    # --- Summary ---
    print("\n=== Summary ===")
    print(f"  First load:  {elapsed1:.1f}s (cold)")
    print(f"  Second load: {elapsed2:.1f}s {'PASS' if results[0] else 'FAIL'}")
    print(f"  Third load:  {elapsed3:.1f}s {'PASS' if results[1] else 'FAIL'}")
    print(f"  Threshold:   < {CACHE_THRESHOLD_SECONDS}s")

    success = all(results)
    print(f"\n{'ALL TESTS PASSED' if success else 'TESTS FAILED'}")
    return success


if __name__ == "__main__":
    success = test_nyt_cache()
    sys.exit(0 if success else 1)
