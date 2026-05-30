#!/usr/bin/env python3
"""
Terminal UI test harness for WireCopy using tmux.

Launches the app inside a tmux session, sends keystrokes, captures screen
output, and enables automated validation of interactive flows.

Usage:
    # As a library (from other scripts or inline):
    from termtest import TermTest
    with TermTest() as t:
        t.wait_for("Loading")
        t.send_keys(":")           # open command line
        t.send_text("nytimes.com")
        t.send_keys("Enter")
        screen = t.capture()
        assert "nytimes" in screen

    # Quick smoke test:
    python3 scripts/termtest.py

    # With a specific URL:
    python3 scripts/termtest.py https://example.com
"""

import os
import re
import subprocess
import sys
import time

DOTNET = os.environ.get(
    "DOTNET_PATH",
    os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "dotnet"),
)
PROJECT = os.environ.get("WIRECOPY_PROJECT", "src/WireCopy.API")
BUILT_DLL = os.environ.get(
    "WIRECOPY_DLL",
    # workspace-5oe9.15: the project targets net10.0 — the old net9.0 default
    # silently launched a stale DLL (or fell back to a slow `dotnet run`),
    # masking the build under test.
    "src/WireCopy.API/bin/Release/net10.0/WireCopy.API.dll",
)
SESSION_NAME = "termtest"
DEFAULT_WIDTH = 100
DEFAULT_HEIGHT = 35


class TermTest:
    """Context manager that runs WireCopy in a tmux session."""

    def __init__(
        self,
        url: str | None = None,
        width: int = DEFAULT_WIDTH,
        height: int = DEFAULT_HEIGHT,
        cwd: str = "/workspace",
    ):
        self.url = url
        self.width = width
        self.height = height
        self.cwd = cwd
        self._started = False

    # -- lifecycle -----------------------------------------------------------

    def __enter__(self):
        self.start()
        return self

    def __exit__(self, *exc):
        self.stop()

    def start(self):
        """Kill any stale session, then launch the app in a fresh tmux."""
        subprocess.run(
            ["tmux", "kill-session", "-t", SESSION_NAME],
            capture_output=True,
        )

        # Use pre-built DLL for speed; fall back to dotnet run
        dll_path = os.path.join(self.cwd, BUILT_DLL)
        if os.path.exists(dll_path):
            cmd = f"{DOTNET} {dll_path} browse"
        else:
            cmd = f"{DOTNET} run --project {PROJECT} -- browse"
        if self.url:
            cmd += f" {self.url}"

        subprocess.check_call(
            [
                "tmux", "new-session",
                "-d",                       # detached
                "-s", SESSION_NAME,
                "-x", str(self.width),
                "-y", str(self.height),
                cmd,
            ],
            cwd=self.cwd,
        )
        self._started = True

    def stop(self):
        """Kill the tmux session."""
        if self._started:
            subprocess.run(
                ["tmux", "kill-session", "-t", SESSION_NAME],
                capture_output=True,
            )
            self._started = False

    # -- input ---------------------------------------------------------------

    def send_keys(self, *keys: str, delay: float = 0.15):
        """Send one or more tmux key names (e.g. 'Enter', 'Escape', 'j').

        For literal text, use send_text() instead.
        """
        for key in keys:
            subprocess.check_call(
                ["tmux", "send-keys", "-t", SESSION_NAME, key],
            )
            if delay:
                time.sleep(delay)

    def send_text(self, text: str, delay: float = 0.05):
        """Type literal text character by character."""
        for ch in text:
            subprocess.check_call(
                ["tmux", "send-keys", "-t", SESSION_NAME, "-l", ch],
            )
            if delay:
                time.sleep(delay)

    # -- output --------------------------------------------------------------

    def capture(self, strip_ansi: bool = True) -> str:
        """Capture the current tmux pane content as a string."""
        raw = subprocess.check_output(
            ["tmux", "capture-pane", "-t", SESSION_NAME, "-p"],
        ).decode("utf-8", errors="replace")

        if strip_ansi:
            raw = re.sub(r"\x1b\[[0-9;]*[A-Za-z]", "", raw)
            raw = re.sub(r"\x1b\][^\x07]*\x07", "", raw)  # OSC sequences

        return raw

    def capture_lines(self, strip_ansi: bool = True) -> list[str]:
        """Capture as a list of lines (trailing blanks stripped)."""
        text = self.capture(strip_ansi=strip_ansi)
        lines = text.split("\n")
        # Strip trailing blank lines
        while lines and not lines[-1].strip():
            lines.pop()
        return lines

    # -- waiting -------------------------------------------------------------

    def wait_for(
        self,
        text: str,
        timeout: float = 30.0,
        interval: float = 0.5,
        case_sensitive: bool = False,
    ) -> str:
        """Poll the screen until `text` appears. Returns the full screen content.

        Raises TimeoutError if not found within `timeout` seconds.
        """
        target = text if case_sensitive else text.lower()
        deadline = time.time() + timeout
        last_screen = ""

        while time.time() < deadline:
            screen = self.capture()
            last_screen = screen
            check = screen if case_sensitive else screen.lower()
            if target in check:
                return screen
            time.sleep(interval)

        raise TimeoutError(
            f"Timed out waiting for '{text}' after {timeout}s.\n"
            f"Last screen:\n{last_screen}"
        )

    def wait_for_any(
        self, *texts: str, timeout: float = 30.0, interval: float = 0.5
    ) -> tuple[str, str]:
        """Wait for any of the given texts to appear. Returns (matched_text, screen)."""
        deadline = time.time() + timeout
        last_screen = ""

        while time.time() < deadline:
            screen = self.capture()
            last_screen = screen
            lower = screen.lower()
            for t in texts:
                if t.lower() in lower:
                    return t, screen
            time.sleep(interval)

        raise TimeoutError(
            f"Timed out waiting for any of {texts} after {timeout}s.\n"
            f"Last screen:\n{last_screen}"
        )

    def wait_until_gone(
        self, text: str, timeout: float = 15.0, interval: float = 0.5
    ) -> str:
        """Wait until `text` disappears from screen. Returns final screen."""
        deadline = time.time() + timeout
        last_screen = ""

        while time.time() < deadline:
            screen = self.capture()
            last_screen = screen
            if text.lower() not in screen.lower():
                return screen
            time.sleep(interval)

        raise TimeoutError(
            f"'{text}' still present after {timeout}s.\n"
            f"Last screen:\n{last_screen}"
        )

    # -- assertions ----------------------------------------------------------

    def assert_screen_contains(self, text: str, msg: str = ""):
        """Assert the current screen contains the given text."""
        screen = self.capture()
        if text.lower() not in screen.lower():
            raise AssertionError(
                f"Screen does not contain '{text}'{': ' + msg if msg else ''}.\n"
                f"Screen:\n{screen}"
            )

    def assert_screen_not_contains(self, text: str, msg: str = ""):
        """Assert the current screen does NOT contain the given text."""
        screen = self.capture()
        if text.lower() in screen.lower():
            raise AssertionError(
                f"Screen unexpectedly contains '{text}'{': ' + msg if msg else ''}.\n"
                f"Screen:\n{screen}"
            )

    # -- helpers -------------------------------------------------------------

    def screenshot(self, label: str = "") -> str:
        """Capture and print the screen with an optional label. Returns content."""
        screen = self.capture()
        header = f"=== SCREEN{': ' + label if label else ''} ==="
        print(header)
        print(screen)
        print("=" * len(header))
        return screen


# ---------------------------------------------------------------------------
# Standalone smoke test
# ---------------------------------------------------------------------------

def smoke_test(url: str = "https://example.com"):
    print(f"Starting WireCopy with {url}...")

    with TermTest(url=url) as t:
        try:
            # Wait for page to load (either content or error)
            matched, screen = t.wait_for_any(
                "Example Domain", "LINK", "Error", "Loading",
                timeout=20,
            )
            print(f"Initial load matched: '{matched}'")
            t.screenshot("after load")

            # Try pressing some keys
            t.send_keys("?")  # help overlay
            time.sleep(1)
            t.screenshot("help overlay")

            t.send_keys("Escape")  # close help
            time.sleep(0.5)
            t.screenshot("after closing help")

            print("\nSmoke test PASSED")

        except Exception as e:
            print(f"\nSmoke test FAILED: {e}")
            try:
                t.screenshot("on failure")
            except Exception:
                pass
            raise


if __name__ == "__main__":
    target_url = sys.argv[1] if len(sys.argv) > 1 else "https://example.com"
    smoke_test(target_url)
