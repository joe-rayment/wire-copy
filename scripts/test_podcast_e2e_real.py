#!/usr/bin/env python3
"""
Canonical end-to-end test for workspace-l0op.

Verifies that pressing `p` on a populated Reading List with valid credentials
produces a real, playable .m4a podcast file AND a publicly reachable feed.xml.

This is the test that should have run before workspace-mhwa / workspace-reym
closed. No example.com URLs, no internal-handoff shortcuts — every artifact
the user would inspect is verified:

  - Frame-by-frame transcript captured via tmux
  - Final .m4a file exists and ffprobe reports a valid duration + size
  - Published feed.xml is reachable from the public internet (HTTP HEAD)
  - The audio file referenced by the feed is reachable

Usage:
    python3 scripts/test_podcast_e2e_real.py

Exit codes:
    0 - All artifacts verified
    1 - Setup failure (e.g. couldn't navigate to Reading List)
    2 - Generation failed (error screen reached)
    3 - .m4a missing or invalid
    4 - Feed.xml not reachable or invalid
"""
import json
import os
import re
import subprocess
import sys
import time
import urllib.request
import urllib.error

sys.path.insert(0, os.path.join(os.path.dirname(__file__)))
from termtest import TermTest

WORKTREE = os.environ.get(
    "WIRECOPY_WORKTREE",
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
)
DATA_DIR = "/home/agent/.local/share/WireCopy"
OUTPUT_DIR = os.path.join(DATA_DIR, "output")
SHOTS_DIR = os.environ.get(
    "PODCAST_E2E_SHOTS",
    "/home/agent/.claude/jobs/4ac9dcc1/qa-shots-e2e-real",
)
SETTINGS_FILE = os.path.join(DATA_DIR, "settings.json")


def log(msg):
    print(f"[e2e] {msg}", flush=True)


def save_frame(name, screen):
    path = os.path.join(SHOTS_DIR, name + ".txt")
    with open(path, "w") as f:
        f.write(screen)


def load_settings():
    with open(SETTINGS_FILE) as f:
        return json.load(f)


def get_bucket_name():
    s = load_settings()
    return s["Settings"].get("GcsBucketName", {}).get("Value")


def verify_audio(path):
    """Return (ok, info) where info is a dict with duration/size or error."""
    if not os.path.exists(path):
        return False, {"error": f"file not found: {path}"}
    try:
        out = subprocess.check_output(
            [
                "ffprobe", "-v", "error",
                "-show_entries", "format=duration,size,format_name",
                "-of", "json", path,
            ],
            text=True,
        )
        meta = json.loads(out)["format"]
        duration = float(meta.get("duration", 0))
        size = int(meta.get("size", 0))
        if duration < 1.0:
            return False, {"error": f"duration too short ({duration}s)", **meta}
        if size < 1000:
            return False, {"error": f"file too small ({size} bytes)", **meta}
        return True, {"duration": duration, "size": size, "format": meta.get("format_name")}
    except subprocess.CalledProcessError as e:
        return False, {"error": f"ffprobe failed: {e}"}


def extract_feed_url(screen):
    """Pull the feed URL the success screen advertises (path includes a
    podcast-id segment, not root)."""
    m = re.search(r"(https://storage\.googleapis\.com/\S+/feed\.xml)", screen)
    return m.group(1) if m else None


def verify_feed(url):
    """HEAD the published feed URL; return (ok, info)."""
    try:
        req = urllib.request.Request(url, method="HEAD")
        with urllib.request.urlopen(req, timeout=15) as resp:
            return True, {
                "url": url,
                "status": resp.status,
                "content_type": resp.headers.get("Content-Type"),
                "content_length": resp.headers.get("Content-Length"),
            }
    except urllib.error.HTTPError as e:
        return False, {"url": url, "error": f"HTTP {e.code}"}
    except Exception as e:
        return False, {"url": url, "error": str(e)}


def verify_first_audio_in_feed(url):
    """GET the feed body, parse the first <enclosure url> and HEAD it."""
    try:
        with urllib.request.urlopen(url, timeout=15) as resp:
            body = resp.read().decode("utf-8", errors="replace")
    except Exception as e:
        return False, {"error": f"feed GET failed: {e}"}

    match = re.search(r'<enclosure[^>]*url="([^"]+)"', body)
    if not match:
        return False, {"error": "no <enclosure> tag in feed.xml"}
    audio_url = match.group(1)

    try:
        req = urllib.request.Request(audio_url, method="HEAD")
        with urllib.request.urlopen(req, timeout=15) as resp:
            return True, {
                "url": audio_url,
                "status": resp.status,
                "content_type": resp.headers.get("Content-Type"),
                "content_length": resp.headers.get("Content-Length"),
            }
    except urllib.error.HTTPError as e:
        return False, {"url": audio_url, "error": f"HTTP {e.code}"}
    except Exception as e:
        return False, {"url": audio_url, "error": str(e)}


def navigate_to_reading_list(t):
    """Robust navigation per launcher-tile-navigation memory."""
    t.wait_for("READING LIST", timeout=20)
    time.sleep(2)
    # Wrap up many times to land on URL bar
    for _ in range(8):
        t.send_keys("k")
        time.sleep(0.05)
    t.send_keys("Down")
    time.sleep(0.2)
    t.send_keys("Right")
    time.sleep(0.2)
    t.send_keys("Enter")
    time.sleep(3)
    screen = t.capture()
    if "Generate Podcast" not in screen and "GENERATE PODCAST" not in screen:
        return False, screen
    return True, screen


def wait_for_terminal_state(t, timeout=180):
    """Poll for success / error / cancel; capture frames along the way."""
    markers = [
        "Podcast Ready",
        "Listen now",
        "Subscribe at",
        "generated and published",
        "failed",
        "Failed",
        "Cancelled",
        "✗",
    ]
    deadline = time.time() + timeout
    last_capture = 0
    while time.time() < deadline:
        elapsed = time.time() - deadline + timeout
        screen = t.capture()
        if elapsed - last_capture >= 3:
            save_frame(f"03_progress_{int(elapsed):03d}s", screen)
            last_capture = elapsed
        for m in markers:
            if m in screen:
                save_frame(f"04_terminal_{int(elapsed):03d}s", screen)
                return screen, elapsed
        time.sleep(1)
    return t.capture(), timeout


def main():
    os.makedirs(SHOTS_DIR, exist_ok=True)
    bucket = get_bucket_name()
    if not bucket:
        log("ERROR: no GcsBucketName in settings.json")
        return 1

    log(f"Bucket: {bucket}")
    log(f"Output dir before: {os.listdir(OUTPUT_DIR) if os.path.exists(OUTPUT_DIR) else '<missing>'}")
    log(f"Shots dir: {SHOTS_DIR}")

    with TermTest(width=120, height=40, cwd=WORKTREE) as t:
        ok, _ = navigate_to_reading_list(t)
        if not ok:
            save_frame("setup_failure", t.capture())
            log("ERROR: couldn't navigate to Reading List")
            return 1
        save_frame("01_reading_list", t.capture())
        log("Reached Reading List with CTA")

        log("T0: press p")
        t.send_keys("p")
        # First frame as fast as possible
        time.sleep(0.2)
        save_frame("02_after_p_0.2s", t.capture())

        # Watch progress for up to 180s
        final_screen, elapsed = wait_for_terminal_state(t, timeout=180)
        log(f"Reached terminal state at t≈{elapsed:.0f}s")

        # Did we hit success?
        success_markers = [
            "Podcast Ready",
            "Listen now",
            "Subscribe at",
            "generated and published",
        ]
        is_success = any(m in final_screen for m in success_markers)
        if not is_success:
            log("ERROR: did not reach success screen")
            log(final_screen[-1500:])
            return 2

        save_frame("05_success_screen", final_screen)

    # Verify the .m4a artifact
    output_files = [f for f in os.listdir(OUTPUT_DIR) if f.endswith((".m4a", ".m4b"))]
    log(f"Output dir after: {output_files}")
    if not output_files:
        log("ERROR: no .m4a/.m4b file produced")
        return 3

    audio_path = os.path.join(OUTPUT_DIR, output_files[0])
    ok, audio_info = verify_audio(audio_path)
    log(f"Audio verification: ok={ok} info={audio_info}")
    if not ok:
        return 3

    # The success screen advertises the actual feed URL (path includes a
    # per-podcast id segment); read it off the captured screen so the test
    # verifies exactly what the user would copy from the UI.
    feed_url = extract_feed_url(final_screen)
    if not feed_url:
        log("ERROR: success screen did not advertise a feed URL")
        return 4
    log(f"Feed URL from success screen: {feed_url}")

    # Verify the published feed
    log("Verifying public feed...")
    ok, feed_info = verify_feed(feed_url)
    log(f"Feed verification: ok={ok} info={feed_info}")
    if not ok:
        return 4

    # Verify the first audio file in the feed is reachable
    log("Verifying first audio in feed is publicly reachable...")
    ok, audio_pub_info = verify_first_audio_in_feed(feed_url)
    log(f"Audio public verification: ok={ok} info={audio_pub_info}")
    if not ok:
        return 4

    # Write a manifest of artifacts for the bead's close-reason
    manifest = {
        "audio_local": {"path": audio_path, **audio_info},
        "feed_public": feed_info,
        "audio_public": audio_pub_info,
        "frames_dir": SHOTS_DIR,
        "frame_count": len([f for f in os.listdir(SHOTS_DIR) if f.endswith(".txt")]),
    }
    manifest_path = os.path.join(SHOTS_DIR, "manifest.json")
    with open(manifest_path, "w") as f:
        json.dump(manifest, f, indent=2)
    log(f"Manifest: {manifest_path}")
    log("ALL ARTIFACTS VERIFIED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
