#!/usr/bin/env python3
"""
Screenshot utility for TermReader testing and validation.

Takes reference screenshots of websites using Playwright's Chromium browser.
Used to compare real website rendering against TermReader's terminal output.

Usage:
    # Basic screenshot
    python3 scripts/screenshot.py https://example.com

    # Full-page screenshot
    python3 scripts/screenshot.py https://example.com --full-page

    # Custom viewport size
    python3 scripts/screenshot.py https://example.com --width 1440 --height 900

    # With cookies from TermReader's cookie store
    python3 scripts/screenshot.py https://nytimes.com --cookies

    # Custom output path
    python3 scripts/screenshot.py https://example.com -o /tmp/my-screenshot.png

    # Wait for specific selector before screenshotting
    python3 scripts/screenshot.py https://example.com --wait-for "article"

    # Multiple URLs
    python3 scripts/screenshot.py https://example.com https://wikipedia.org
"""

import argparse
import json
import os
import re
import sys
import time
from pathlib import Path
from urllib.parse import urlparse

# Ensure playwright is available
try:
    from playwright.sync_api import sync_playwright
except ImportError:
    print("Error: playwright not installed. Run: pip3 install playwright")
    print("Then: python3 -m playwright install chromium")
    sys.exit(1)


SCREENSHOTS_DIR = Path(__file__).parent.parent / "screenshots"
COOKIE_STORE_PATHS = [
    Path.home() / ".local" / "share" / "TermReader" / "cookies.json",
    Path(os.environ.get("LOCALAPPDATA", "")) / "TermReader" / "cookies.json",
    Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share"))
    / "TermReader"
    / "cookies.json",
]


def sanitize_filename(url: str) -> str:
    """Convert a URL into a safe filename."""
    parsed = urlparse(url)
    domain = parsed.netloc.replace("www.", "")
    path = parsed.path.strip("/").replace("/", "_")
    name = f"{domain}_{path}" if path else domain
    name = re.sub(r"[^\w\-.]", "_", name)
    name = re.sub(r"_+", "_", name).strip("_")
    return name[:100]


def load_termreader_cookies(domain: str) -> list[dict]:
    """Load cookies from TermReader's encrypted cookie store."""
    for cookie_path in COOKIE_STORE_PATHS:
        if cookie_path.exists():
            try:
                with open(cookie_path) as f:
                    cookies = json.load(f)
                # Filter cookies for the target domain
                matching = []
                for cookie in cookies:
                    cookie_domain = cookie.get("domain", cookie.get("Domain", ""))
                    if domain in cookie_domain or cookie_domain.lstrip(".") in domain:
                        matching.append(
                            {
                                "name": cookie.get("name", cookie.get("Name", "")),
                                "value": cookie.get(
                                    "value", cookie.get("Value", "")
                                ),
                                "domain": cookie_domain,
                                "path": cookie.get("path", cookie.get("Path", "/")),
                            }
                        )
                if matching:
                    print(f"  Loaded {len(matching)} cookies for {domain}")
                return matching
            except (json.JSONDecodeError, KeyError) as e:
                print(f"  Warning: Failed to parse cookies from {cookie_path}: {e}")
    return []


def take_screenshot(
    url: str,
    output_path: Path | None = None,
    full_page: bool = False,
    width: int = 1280,
    height: int = 900,
    use_cookies: bool = False,
    wait_for: str | None = None,
    wait_ms: int = 2000,
    browser=None,
) -> Path:
    """Take a screenshot of a URL and return the output path."""
    own_browser = browser is None
    pw_context = None

    if own_browser:
        pw_context = sync_playwright().start()
        browser = pw_context.chromium.launch(headless=True)

    try:
        context = browser.new_context(
            viewport={"width": width, "height": height},
            user_agent=(
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/131.0.0.0 Safari/537.36"
            ),
        )

        # Inject cookies if requested
        if use_cookies:
            domain = urlparse(url).netloc.replace("www.", "")
            cookies = load_termreader_cookies(domain)
            if cookies:
                context.add_cookies(cookies)

        page = context.new_page()

        # Navigate
        try:
            page.goto(url, wait_until="networkidle", timeout=30000)
        except Exception:
            # Fall back to domcontentloaded if networkidle times out
            try:
                page.goto(url, wait_until="domcontentloaded", timeout=15000)
            except Exception as e:
                print(f"  Warning: Navigation issue: {e}")

        # Wait for specific selector if requested
        if wait_for:
            try:
                page.wait_for_selector(wait_for, timeout=10000)
            except Exception:
                print(f"  Warning: Selector '{wait_for}' not found within timeout")

        # Additional wait for JS rendering
        if wait_ms > 0:
            page.wait_for_timeout(wait_ms)

        # Determine output path
        if output_path is None:
            SCREENSHOTS_DIR.mkdir(parents=True, exist_ok=True)
            timestamp = time.strftime("%Y%m%d-%H%M%S")
            filename = f"{sanitize_filename(url)}_{timestamp}.png"
            output_path = SCREENSHOTS_DIR / filename

        # Take screenshot
        page.screenshot(path=str(output_path), full_page=full_page)
        context.close()

        return output_path

    finally:
        if own_browser:
            browser.close()
            if pw_context:
                pw_context.stop()


def main():
    parser = argparse.ArgumentParser(
        description="Take reference screenshots of websites for TermReader testing"
    )
    parser.add_argument("urls", nargs="+", help="URLs to screenshot")
    parser.add_argument(
        "-o", "--output", help="Output file path (only for single URL)"
    )
    parser.add_argument(
        "--full-page", action="store_true", help="Capture full page (not just viewport)"
    )
    parser.add_argument("--width", type=int, default=1280, help="Viewport width")
    parser.add_argument("--height", type=int, default=900, help="Viewport height")
    parser.add_argument(
        "--cookies",
        action="store_true",
        help="Inject cookies from TermReader's cookie store",
    )
    parser.add_argument(
        "--wait-for", help="CSS selector to wait for before screenshotting"
    )
    parser.add_argument(
        "--wait-ms",
        type=int,
        default=2000,
        help="Additional wait time in ms after page load (default: 2000)",
    )

    args = parser.parse_args()

    if args.output and len(args.urls) > 1:
        print("Error: --output can only be used with a single URL")
        sys.exit(1)

    print(f"Taking {len(args.urls)} screenshot(s)...")

    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)

        for url in args.urls:
            print(f"\n  {url}")
            output = Path(args.output) if args.output else None

            try:
                path = take_screenshot(
                    url=url,
                    output_path=output,
                    full_page=args.full_page,
                    width=args.width,
                    height=args.height,
                    use_cookies=args.cookies,
                    wait_for=args.wait_for,
                    wait_ms=args.wait_ms,
                    browser=browser,
                )
                print(f"  -> {path}")
            except Exception as e:
                print(f"  Error: {e}")

        browser.close()

    print("\nDone.")


if __name__ == "__main__":
    main()
