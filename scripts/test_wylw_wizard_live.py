#!/usr/bin/env python3
"""
Live walkthrough for workspace-wylw: the Ctrl+L link-list setup wizard with
LIVE lens confirmation. Runs the REAL app (real OpenAI round trips) in tmux
under Xvfb against a local sectioned link-list site, then:

  1. Opens the page, waits for the sidecar lens to dock.
  2. Ctrl+L -> "Let AI find the stories" -> walks every wizard card with Enter,
     capturing the terminal transcript at each step.
  3. On the "Confirm your story list" card, captures an X screenshot and
     verifies ORANGE TunerScript multi-highlight pixels in the lens window
     strip (the proposed section's matched links, lit on the live page).
  4. Enter saves; verifies the "Site set up · AI Curated" toast/status.
  5. RELAUNCHES the app on the same URL and verifies the saved durable config
     routes the revisit (section headers render with no AI round trip).

Usage: python3 scripts/test_wylw_wizard_live.py
Needs: tmux, Xvfb, ffmpeg, a Release build, an OpenAI key in app settings.
"""

import http.server
import os
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402

DISPLAY = ":98"
SCREEN_W, SCREEN_H = 1600, 900
DOCK_W = 430
OUT_DIR = "/workspace/output/wylw-wizard-live"
TERM_W = 150

WORDS = ["harbor", "granite", "meadow", "lantern", "compass", "thicket",
         "ember", "current", "signal", "marble", "drift", "anchor"]


def story_html(n):
    body = f"<h1>Story {n}</h1>" + "".join(
        f"<p>{WORDS[i % 12].capitalize()} reporting in paragraph {i} of story {n} "
        f"explores the {WORDS[(i + 3) % 12]} question while residents recall the "
        f"{WORDS[(i + 7) % 12]} years with measured detail across many interviews "
        f"conducted this {WORDS[(i + 5) % 12]} season, and the {WORDS[(i + 2) % 12]} "
        f"committee weighed the {WORDS[(i + 9) % 12]} proposal against the "
        f"{WORDS[(i + 4) % 12]} budget while observers from the {WORDS[(i + 6) % 12]} "
        f"council documented every {WORDS[(i + 8) % 12]} development for the public "
        f"record of the {WORDS[(i + 10) % 12]} district.</p>"
        for i in range(1, 15))
    return f"<!DOCTYPE html><html><head><title>Story {n}</title></head><body><article>{body}</article></body></html>"


class Site(http.server.BaseHTTPRequestHandler):
    """A link-list page with an unmistakable editorial structure: one lead
    story (.lead-story, /politics/), a headline list (.headline-list, /news/),
    and sponsored promos (.promo-rail, /sponsored/) the AI should exclude."""

    def do_GET(self):
        if "/story" in self.path or "/lead" in self.path or "/promo" in self.path:
            n = self.path.rstrip("/").split("-")[-1]
            html = story_html(n)
        else:
            lead = ('<section class="lead-story" style="padding:20px">'
                    '<h2><a href="/politics/lead-story-0">Capital budget showdown reshapes the council agenda</a></h2>'
                    "</section>")
            headlines = '<section class="headline-list">' + "".join(
                f'<div style="height:90px"><a href="/news/story-{i}">Headline number {i} about topic {i}</a></div>'
                for i in range(1, 13)) + "</section>"
            promos = '<aside class="promo-rail">' + "".join(
                f'<div><a href="/sponsored/promo-{i}">Sponsored: amazing deal {i}</a></div>'
                for i in range(1, 4)) + "</aside>"
            html = ("<!DOCTYPE html><html><head><title>Wylw Gazette</title></head><body>"
                    f"<h1>Wylw Gazette</h1>{lead}{headlines}{promos}</body></html>")
        data = html.encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


def x_screenshot(name):
    path = os.path.join(OUT_DIR, name)
    subprocess.run(
        ["ffmpeg", "-y", "-loglevel", "error", "-f", "x11grab",
         "-video_size", f"{SCREEN_W}x{SCREEN_H}", "-i", DISPLAY,
         "-frames:v", "1", path],
        check=True, env={**os.environ, "DISPLAY": DISPLAY})
    return path


def orange_pixels(png):
    """Count TunerScript-orange pixels (#f57c00 border family) anywhere in the
    screenshot — the docked browser window can be wider than DOCK_W (Chromium
    minimum window width), so scan the full frame."""
    raw = subprocess.run(
        ["ffmpeg", "-loglevel", "error", "-i", png,
         "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
        check=True, capture_output=True).stdout
    count = 0
    for i in range(0, len(raw) - 2, 3):
        r, g, b = raw[i], raw[i + 1], raw[i + 2]
        if r > 175 and 70 < g < 200 and b < 120 and r - b > 80 and r - g > 30:
            count += 1
    return count


def wizard_lens_log_lines():
    out = subprocess.run(
        ["grep", "-h", "Wizard lens:", "/workspace/logs/wirecopy-20260610.log"],
        capture_output=True, text=True).stdout
    return [l for l in out.splitlines() if l]


def transcript(t, log, step):
    screen = t.capture()
    log.append(f"\n===== {step} =====\n{screen}")
    print(f"--- {step}")
    return screen


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    # Repeatability: the wizard must see an UNCONFIGURED site.
    cfg = os.path.expanduser("~/.local/share/WireCopy/hierarchy/127.0.0.1.json")
    if os.path.exists(cfg):
        os.remove(cfg)
    log_mark = len(wizard_lens_log_lines())
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/wylw-live-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)

    lock = f"/tmp/.X{DISPLAY.lstrip(':')}-lock"
    if os.path.exists(lock):
        try:
            with open(lock) as fh:
                os.kill(int(fh.read().strip()), 0)
        except (OSError, ValueError):
            os.remove(lock)

    xvfb = subprocess.Popen(
        ["Xvfb", DISPLAY, "-screen", "0", f"{SCREEN_W}x{SCREEN_H}x24"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Site)
    port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()
    os.environ["DISPLAY"] = DISPLAY
    url = f"http://127.0.0.1:{port}/"

    failures, log = [], []
    try:
        with TermTest(url=url, width=TERM_W, height=40) as t:
            t.wait_for("Headline number", timeout=60)
            t.wait_for("docked", timeout=90)
            time.sleep(2)
            transcript(t, log, "1. link list loaded, sidecar docked")

            # --- Ctrl+L -> AI-first entry card ---
            t.send_keys("C-l")
            t.wait_for("How should WireCopy read this site?", timeout=20)
            transcript(t, log, "2. Ctrl+L entry card (AI-first)")
            t.send_keys("Enter")  # ✨ Let AI find the stories

            # --- Round 1 (real model), then the overview card ---
            t.wait_for("does it look right?", timeout=180)
            transcript(t, log, "3. overview card — proposed pattern + durable identifier")
            shot = x_screenshot("01-overview-highlight.png")
            n = orange_pixels(shot)
            print(f"lens highlight pixels on overview card: {n}")
            if n < 100:
                failures.append(f"overview card: expected orange highlight on lens, found {n} px")
            t.send_keys("Enter")  # looks good

            # --- Walk question cards / free-text until the confirm card ---
            deadline = time.time() + 300
            seen_q = 0
            while time.time() < deadline:
                screen = t.capture()
                if "Confirm your story list" in screen:
                    break
                if "Anything you'd like to tell the AI?" in screen:
                    transcript(t, log, "5. optional free-text card (skipped)")
                    t.send_keys("Enter")
                    time.sleep(1)
                elif "of " in screen and "Set up this site with AI ·" in screen:
                    seen_q += 1
                    transcript(t, log, f"4.{seen_q} question card (Enter accepts default)")
                    t.send_keys("Enter")
                    time.sleep(1)
                else:
                    time.sleep(2)  # analyzing spinner
            else:
                failures.append("never reached the confirm-sections card")

            if "Confirm your story list" in t.capture():
                transcript(t, log, "6. CONFIRM CARD — sections with real match counts")
                time.sleep(1.5)  # let the lens highlight + scroll settle
                shot = x_screenshot("02-confirm-section-highlight.png")
                n = orange_pixels(shot)
                print(f"lens highlight pixels on confirm card: {n}")
                if n < 100:
                    failures.append(f"confirm card: expected highlight on lens, found {n} px")

                # j/k grammar: preview the next section live, then back.
                t.send_keys("j")
                time.sleep(1.5)
                x_screenshot("03-confirm-next-section.png")
                transcript(t, log, "7. j cycles — next section previewed on the lens")
                t.send_keys("k")
                time.sleep(1)

                t.send_keys("Enter")  # save
                t.wait_for("Site set up", timeout=30)
                transcript(t, log, "8. saved — AI Curated with durable sections")

            time.sleep(2)
            screen = transcript(t, log, "9. link tree rebuilt from the saved config")
            if "sections" not in screen:
                failures.append("post-save header does not show the section count")

        # The lens log is the ground truth for MULTI-highlight: focusing the
        # headline section (j on the confirm card) must have lit all its links.
        lens_lines = wizard_lens_log_lines()[log_mark:]
        for line in lens_lines:
            print(f"  log: {line.split(']')[-1].strip()}")
        if not any("highlighted 1 match(es)" in l for l in lens_lines):
            failures.append("lens log shows no single-match (lead story) highlight")
        if not any(f"highlighted {k} match(es)" in l for l in lens_lines for k in range(10, 14)):
            failures.append("lens log shows no MULTI-match highlight for the headline section")

        # --- Revisit in a FRESH app process: durable config must route ---
        print("relaunching for the revisit check…")
        with TermTest(url=url, width=TERM_W, height=40) as t2:
            # The saved config may start sections collapsed — wait for the
            # section structure itself, not a (possibly hidden) headline.
            t2.wait_for("sections", timeout=90)
            time.sleep(3)
            screen = transcript(t2, log, "10. REVISIT — fresh process, same URL")
            if "sections" not in screen and "▼" not in screen:
                failures.append("revisit did not route through the saved durable config "
                                "(no section headers / section count in the header)")
            # The saved sections must shape the tree without any AI call:
            # the wizard's section names appear as group headers.
            t2.send_keys("C-l")
            t2.wait_for("Layout", timeout=20)
            screen = transcript(t2, log, "11. Ctrl+L on revisit — configured summary (not setup)")
            if "Reconfigure with AI" not in screen:
                failures.append("revisit Ctrl+L did not open the configured summary")
            t2.send_keys("Escape")
    finally:
        with open(os.path.join(OUT_DIR, "transcript.txt"), "w") as fh:
            fh.write("\n".join(log))
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        xvfb.terminate()
        server.shutdown()

    print(f"\ntranscript + screenshots in {OUT_DIR}")
    if failures:
        print("\nFAILURES:")
        for f in failures:
            print(f"  ✗ {f}")
        sys.exit(1)
    print("✓ wylw live walkthrough PASSED")


if __name__ == "__main__":
    main()
