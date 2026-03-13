#!/usr/bin/env python3
"""
Screenshot utility for TermReader testing and validation.

Uses Chrome's DevTools Protocol (CDP) directly via websocket, with the same
anti-detection measures as BrowserSession.cs (--disable-blink-features,
webdriver property masking, realistic user agent). No chromedriver required.

Playwright was replaced because NYT bot protection blocks its automation
fingerprint. This script launches Chromium with anti-detection flags and
communicates via CDP, bypassing both Playwright and Selenium's driver
requirements.

Usage:
    # Basic screenshot
    python3 scripts/screenshot.py https://example.com

    # Full-page screenshot (via CDP)
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

    # Save page source alongside screenshot
    python3 scripts/screenshot.py https://example.com --save-html
"""

import argparse
import base64
import glob
import json
import math
import os
import re
import shutil
import signal
import subprocess
import sys
import time
from pathlib import Path
from urllib.parse import urlparse

try:
    import websocket
except ImportError:
    print("Error: websocket-client not installed. Run: pip3 install websocket-client")
    sys.exit(1)


SCREENSHOTS_DIR = Path(__file__).parent.parent / "screenshots"
COOKIE_STORE_PATHS = [
    Path.home() / ".local" / "share" / "TermReader" / "cookies.json",
    Path(os.environ.get("LOCALAPPDATA", "")) / "TermReader" / "cookies.json",
    Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share"))
    / "TermReader"
    / "cookies.json",
]

# Matches BrowserSession.cs user agent
DEFAULT_USER_AGENT = (
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
    "AppleWebKit/537.36 (KHTML, like Gecko) "
    "Chrome/131.0.0.0 Safari/537.36"
)


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
    """Load cookies from TermReader's cookie store."""
    for cookie_path in COOKIE_STORE_PATHS:
        if cookie_path.exists():
            try:
                with open(cookie_path) as f:
                    cookies = json.load(f)
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


def _find_chrome() -> str | None:
    """Find a usable Chrome/Chromium binary."""
    # Check PATH candidates
    for name in [
        "chromium-browser",
        "chromium",
        "google-chrome-stable",
        "google-chrome",
    ]:
        path = shutil.which(name)
        if path:
            try:
                result = subprocess.run(
                    [path, "--version"],
                    capture_output=True,
                    text=True,
                    timeout=5,
                )
                if result.returncode == 0:
                    return path
            except Exception:
                pass

    # Check Playwright's bundled Chromium (works on arm64)
    pw_cache = Path.home() / ".cache" / "ms-playwright"
    if pw_cache.exists():
        dirs = sorted(pw_cache.glob("chromium-*"), reverse=True)
        for d in dirs:
            chrome = d / "chrome-linux" / "chrome"
            if chrome.exists() and os.access(str(chrome), os.X_OK):
                return str(chrome)

    # Check Selenium cache
    cache_dir = Path.home() / ".cache" / "selenium" / "chrome"
    paths = sorted(glob.glob(str(cache_dir / "linux64" / "*" / "chrome")), reverse=True)
    for p in paths:
        if os.path.isfile(p) and os.access(p, os.X_OK):
            return p

    return None


class CDPBrowser:
    """Minimal CDP browser interface for taking screenshots."""

    def __init__(self, width: int = 1280, height: int = 900):
        self.width = width
        self.height = height
        self.process: subprocess.Popen | None = None
        self.ws: websocket.WebSocket | None = None
        self._msg_id = 0
        self._debug_port = 9222
        self._session_id: str | None = None

    def start(self) -> None:
        """Launch Chrome with anti-detection flags and connect via CDP."""
        chrome_path = _find_chrome()
        if not chrome_path:
            raise RuntimeError(
                "Could not find Chrome/Chromium. Install chromium-browser or "
                "run: python3 -m playwright install chromium"
            )

        # Find an available port
        import socket

        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.bind(("", 0))
            self._debug_port = s.getsockname()[1]

        args = [
            chrome_path,
            "--headless=new",
            "--no-sandbox",
            "--disable-dev-shm-usage",
            "--disable-gpu",
            # Anti-detection (matching BrowserSession.cs)
            "--disable-blink-features=AutomationControlled",
            f"--window-size={self.width},{self.height}",
            f"--remote-debugging-port={self._debug_port}",
            "--remote-allow-origins=*",
            f"--user-agent={DEFAULT_USER_AGENT}",
        ]

        self.process = subprocess.Popen(
            args, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE
        )

        # Wait for Chrome to start and expose the debugging port
        ws_url = self._wait_for_ws_url(timeout=10)
        self.ws = websocket.create_connection(ws_url, timeout=30)

    def _wait_for_ws_url(self, timeout: int = 10) -> str:
        """Poll Chrome's /json/version endpoint until available."""
        import urllib.request
        import urllib.error

        deadline = time.time() + timeout
        url = f"http://127.0.0.1:{self._debug_port}/json/version"

        while time.time() < deadline:
            try:
                resp = urllib.request.urlopen(url, timeout=2)
                data = json.load(resp)
                ws = data.get("webSocketDebuggerUrl")
                if ws:
                    return ws
            except (urllib.error.URLError, ConnectionRefusedError, OSError):
                pass
            time.sleep(0.3)

        raise RuntimeError(f"Chrome did not start within {timeout}s")

    def _send(self, method: str, params: dict | None = None) -> dict:
        """Send a CDP command and return the result."""
        self._msg_id += 1
        msg = {"id": self._msg_id, "method": method, "params": params or {}}
        if self._session_id:
            msg["sessionId"] = self._session_id
        self.ws.send(json.dumps(msg))

        # Read responses until we get our id back
        while True:
            raw = self.ws.recv()
            resp = json.loads(raw)
            if resp.get("id") == self._msg_id:
                if "error" in resp:
                    raise RuntimeError(
                        f"CDP error: {resp['error'].get('message', resp['error'])}"
                    )
                return resp.get("result", {})

    def new_page(self) -> None:
        """Create a new browser tab and attach to it."""
        result = self._send("Target.createTarget", {"url": "about:blank"})
        target_id = result["targetId"]
        result = self._send(
            "Target.attachToTarget", {"targetId": target_id, "flatten": True}
        )
        self._session_id = result["sessionId"]

        # Enable required domains
        self._send("Page.enable")
        self._send("Network.enable")

        # Mask webdriver detection (matching BrowserSession.cs)
        self._send(
            "Page.addScriptToEvaluateOnNewDocument",
            {
                "source": """
                    Object.defineProperty(navigator, 'webdriver', {get: () => undefined});
                    window.navigator.chrome = {runtime: {}};
                """
            },
        )

        # Set viewport
        self._send(
            "Emulation.setDeviceMetricsOverride",
            {
                "width": self.width,
                "height": self.height,
                "deviceScaleFactor": 1,
                "mobile": False,
            },
        )

    def navigate(self, url: str, timeout: int = 30) -> None:
        """Navigate to a URL and wait for load."""
        self._send("Page.navigate", {"url": url})
        # Wait for load event
        deadline = time.time() + timeout
        while time.time() < deadline:
            raw = self.ws.recv()
            event = json.loads(raw)
            if event.get("method") == "Page.loadEventFired":
                return
        print("  Warning: Page load event not received within timeout")

    def set_cookies(self, cookies: list[dict]) -> None:
        """Set cookies via CDP Network.setCookies."""
        cdp_cookies = []
        for c in cookies:
            cdp_cookies.append(
                {
                    "name": c["name"],
                    "value": c["value"],
                    "domain": c["domain"],
                    "path": c.get("path", "/"),
                }
            )
        if cdp_cookies:
            self._send("Network.setCookies", {"cookies": cdp_cookies})

    def wait_for_selector(self, selector: str, timeout: int = 10) -> bool:
        """Wait for a CSS selector to appear in the DOM."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            result = self._send(
                "Runtime.evaluate",
                {
                    "expression": f"!!document.querySelector('{selector}')",
                    "returnByValue": True,
                },
            )
            if result.get("result", {}).get("value"):
                return True
            time.sleep(0.5)
        return False

    def screenshot(self, full_page: bool = False) -> bytes:
        """Take a screenshot and return PNG bytes."""
        if full_page:
            # Get full page dimensions
            metrics = self._send("Page.getLayoutMetrics")
            cw = math.ceil(metrics["contentSize"]["width"])
            ch = math.ceil(metrics["contentSize"]["height"])

            # Temporarily resize viewport to full page
            self._send(
                "Emulation.setDeviceMetricsOverride",
                {
                    "width": cw,
                    "height": ch,
                    "deviceScaleFactor": 1,
                    "mobile": False,
                },
            )

            result = self._send(
                "Page.captureScreenshot",
                {"format": "png", "captureBeyondViewport": True},
            )

            # Reset viewport
            self._send(
                "Emulation.setDeviceMetricsOverride",
                {
                    "width": self.width,
                    "height": self.height,
                    "deviceScaleFactor": 1,
                    "mobile": False,
                },
            )
        else:
            result = self._send("Page.captureScreenshot", {"format": "png"})

        return base64.b64decode(result["data"])

    def get_page_source(self) -> str:
        """Get the page's HTML source."""
        result = self._send(
            "Runtime.evaluate",
            {
                "expression": "document.documentElement.outerHTML",
                "returnByValue": True,
            },
        )
        return result.get("result", {}).get("value", "")

    def stop(self) -> None:
        """Close browser and clean up."""
        if self.ws:
            try:
                self.ws.close()
            except Exception:
                pass
            self.ws = None
        if self.process:
            try:
                self.process.terminate()
                self.process.wait(timeout=5)
            except Exception:
                try:
                    self.process.kill()
                except Exception:
                    pass
            self.process = None


def take_screenshot(
    url: str,
    output_path: Path | None = None,
    full_page: bool = False,
    width: int = 1280,
    height: int = 900,
    use_cookies: bool = False,
    wait_for: str | None = None,
    wait_ms: int = 2000,
    save_html: bool = False,
    browser: CDPBrowser | None = None,
) -> Path:
    """Take a screenshot of a URL and return the output path."""
    own_browser = browser is None

    if own_browser:
        browser = CDPBrowser(width=width, height=height)
        browser.start()

    try:
        browser.new_page()

        # Inject cookies before navigation
        if use_cookies:
            domain = urlparse(url).netloc.replace("www.", "")
            cookies = load_termreader_cookies(domain)
            if cookies:
                browser.set_cookies(cookies)

        # Navigate
        browser.navigate(url)

        # Wait for specific selector if requested
        if wait_for:
            if not browser.wait_for_selector(wait_for):
                print(f"  Warning: Selector '{wait_for}' not found within timeout")

        # Additional wait for JS rendering
        if wait_ms > 0:
            time.sleep(wait_ms / 1000.0)

        # Determine output path
        if output_path is None:
            SCREENSHOTS_DIR.mkdir(parents=True, exist_ok=True)
            timestamp = time.strftime("%Y%m%d-%H%M%S")
            filename = f"{sanitize_filename(url)}_{timestamp}.png"
            output_path = SCREENSHOTS_DIR / filename

        # Take screenshot
        png_data = browser.screenshot(full_page=full_page)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_bytes(png_data)

        # Save HTML source if requested
        if save_html:
            html_path = output_path.with_suffix(".html")
            html_source = browser.get_page_source()
            html_path.write_text(html_source, encoding="utf-8")
            print(f"  -> {html_path} (HTML)")

        return output_path

    finally:
        if own_browser:
            browser.stop()


def main():
    parser = argparse.ArgumentParser(
        description="Take reference screenshots of websites for TermReader testing"
    )
    parser.add_argument("urls", nargs="+", help="URLs to screenshot")
    parser.add_argument(
        "-o", "--output", help="Output file path (only for single URL)"
    )
    parser.add_argument(
        "--full-page",
        action="store_true",
        help="Capture full page via CDP (not just viewport)",
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
    parser.add_argument(
        "--save-html",
        action="store_true",
        help="Save page source HTML alongside screenshot",
    )

    args = parser.parse_args()

    if args.output and len(args.urls) > 1:
        print("Error: --output can only be used with a single URL")
        sys.exit(1)

    print(f"Taking {len(args.urls)} screenshot(s)...")

    browser = CDPBrowser(width=args.width, height=args.height)
    browser.start()

    try:
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
                    save_html=args.save_html,
                    browser=browser,
                )
                print(f"  -> {path}")
            except Exception as e:
                print(f"  Error: {e}")
    finally:
        browser.stop()

    print("\nDone.")


if __name__ == "__main__":
    main()
