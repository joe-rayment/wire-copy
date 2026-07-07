#!/usr/bin/env python3
"""
Gate for workspace-v2m8.1/.2/.3/.4 — the preview speaks the full label grammar.

Drives the real app on the pinned techmeme fixture with real keys (tmux,
headful under Xvfb — never headless), through the exact journey the user
reported broken on live techmeme (2026-07-07 review):

  1. g l on a configured site: the summary offers "Fix links by hand" directly
     and the Refine row makes NO "(keeps your fixes)" claim while the ledger is
     empty (v2m8.3/.4); the cursor defaults to the fix row.
  2. Refine -> seeded preview (ZERO model calls); the hint teaches a/x/m/i/u.
  3. 'x' on a mid-list story row HIDES exactly that row (never the old
     "shares its only identifier" refusal) and the cursor lands on the row
     that visually replaced it — NOT back on the top header (v2m8.1/.2).
  4. 'm' routes a row under More; 'a' ranks a row [ 1] and reorders; 'z'
     undoes the rank (ledger included).
  5. Enter saves; the persisted JSON carries ad + menu + article labels.
  6. Re-entering g l now shows "(keeps your 3 fixes)" and "Fix links by hand"
     opens the label card directly, seeded with the saved badges.
  7. The whole run makes ZERO analyzer calls (marks are deterministic).

Usage: python3 scripts/test_preview_marks_live.py
Needs: tmux, Xvfb, a Release build.
"""

import json
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402
from keys import choose_layout, summary_refine, summary_fix_links  # noqa: E402
from test_label_mode_live import serve_fixture, seed_config, log_size, log_tail_since  # noqa: E402

DISPLAY = ":93"
SCREEN_W, SCREEN_H = 1600, 900
TERM_W = 150


def cursor_row(t) -> str:
    """The (truncated) text of the ▸-focused row on screen."""
    for line in t.capture().splitlines():
        if "▸" in line:
            return line.split("▸", 1)[1].strip()
    return ""


def headline_after_bullet(row: str) -> str:
    text = row.split("•", 1)[1] if "•" in row else row
    text = text.split("│", 1)[0]
    return " ".join(text.split())


def bullet_rows(screen: str) -> list[str]:
    return [headline_after_bullet(l) for l in screen.splitlines() if "•" in l]


def main():
    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/v2m8-marks-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)
    lock = f"/tmp/.X{DISPLAY.lstrip(':')}-lock"
    if os.path.exists(lock):
        os.remove(lock)
    xvfb = subprocess.Popen(
        ["Xvfb", DISPLAY, "-screen", "0", f"{SCREEN_W}x{SCREEN_H}x24"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    os.environ["DISPLAY"] = DISPLAY

    srv, port = serve_fixture()
    url = f"http://127.0.0.1:{port}/"
    seed_path = seed_config(port)

    failures = []
    log_offset = log_size()
    try:
        with TermTest(url=url, width=TERM_W, height=45) as t:
            t.wait_for("Techmeme", timeout=120)
            time.sleep(6)

            # ---- v2m8.3/.4: the no-fixes summary card ------------------------
            choose_layout(t)
            t.wait_for("Refine the layout with AI", timeout=25)
            time.sleep(0.5)
            screen = t.capture()
            if "keeps your" in screen:
                failures.append("summary claims '(keeps your …)' with an empty ledger")
            if "Fix links by hand" not in screen:
                failures.append("summary card has no direct 'Fix links by hand' option")
            if "Fix links by hand" not in cursor_row(t):
                failures.append(f"summary default cursor is {cursor_row(t)!r}, expected the fix row")

            # ---- seeded preview, marking grammar in the hint ------------------
            summary_refine(t)
            t.wait_for("Your new layout", timeout=45)
            time.sleep(1)
            screen = t.screenshot("1 seeded preview")
            if "a article" not in screen or "x ad" not in screen:
                failures.append("preview hint does not teach the marking keys")

            # ---- 'x' on a mid-list story: drops + cursor on successor --------
            # Source-name rows repeat their text (CNBC, Axios…), so removal is
            # asserted by the visible-count DROPPING, not text disappearance.
            t.send_keys("Down", "Down", "Down")
            time.sleep(0.6)
            marked = headline_after_bullet(cursor_row(t))
            t.send_keys("Down")
            time.sleep(0.4)
            successor = headline_after_bullet(cursor_row(t))
            t.send_keys("Up")
            time.sleep(0.4)
            before_count = bullet_rows(t.capture()).count(marked)

            t.send_keys("x")
            time.sleep(1.2)
            screen = t.screenshot("2 after x")
            after_x_focus = headline_after_bullet(cursor_row(t))
            after_count = bullet_rows(screen).count(marked)
            print(f"x: marked={marked[:40]!r} ({before_count}->{after_count} visible) "
                  f"successor={successor[:40]!r} focus-after={after_x_focus[:40]!r}")
            if after_count >= before_count:
                failures.append(
                    f"the x-marked story still renders ({before_count} -> {after_count} rows with that text)")
            if "shares its only identifier" in screen or "Can't drop" in screen:
                failures.append("'x' was refused — the exact-URL fallback did not engage")
            if successor[:25] not in after_x_focus:
                failures.append(
                    f"cursor after x sits on {after_x_focus[:40]!r}, expected the successor {successor[:40]!r}")

            # ---- 'm' routes under More ---------------------------------------
            menu_target = after_x_focus
            before_count = bullet_rows(t.capture()).count(menu_target)
            t.send_keys("m")
            time.sleep(1.2)
            screen = t.screenshot("3 after m")
            after_count = bullet_rows(screen).count(menu_target)
            if after_count >= before_count:
                failures.append(
                    f"the m-marked row still renders as a story ({before_count} -> {after_count})")

            # ---- 'a' ranks + badges + 'z' undoes ------------------------------
            a_target = headline_after_bullet(cursor_row(t))
            t.send_keys("a")
            time.sleep(1.2)
            screen = t.screenshot("4 after a")
            if "[ 1]" not in screen:
                failures.append("no [ 1] badge on the preview after 'a'")
            rows = bullet_rows(screen)
            if rows and a_target[:25] not in rows[0]:
                failures.append(f"rank-1 row is {rows[0][:40]!r}, expected {a_target[:40]!r} first")

            t.send_keys("z")
            time.sleep(1.2)
            screen = t.capture()
            if "[ 1]" in screen:
                failures.append("'z' did not undo the article rank (badge still shows)")

            t.send_keys("a")  # re-mark for the persistence checks
            time.sleep(1.2)

            # ---- save + persisted ledger --------------------------------------
            t.send_keys("Enter")
            t.wait_for("Site set up", timeout=20)
            time.sleep(1)

            with open(seed_path) as f:
                cfg = json.load(f)[-1]
            labels = cfg.get("userLabels", [])
            kinds = sorted(l["kind"] for l in labels)
            print(f"persisted: {len(labels)} labels kinds={kinds} sections={[s['name'] for s in cfg['sections']]}")
            if kinds != [0, 1, 2]:  # Article, Ad, Menu
                failures.append(f"persisted ledger kinds are {kinds}, expected [0, 1, 2]")
            if not any(l.get("rank") == 1 for l in labels):
                failures.append("no rank-1 article label persisted")

            # ---- v2m8.3 with fixes + v2m8.4 direct label entry ----------------
            choose_layout(t)
            t.wait_for("Refine the layout with AI", timeout=25)
            time.sleep(0.5)
            screen = t.screenshot("5 summary with fixes")
            if "keeps your 3 fixes" not in screen:
                failures.append("summary does not say '(keeps your 3 fixes)' after 3 marks")
            summary_fix_links(t)
            t.wait_for("Mark the links", timeout=20)
            time.sleep(0.8)
            screen = t.screenshot("6 label card via direct entry")
            if "[ 1]" not in screen:
                failures.append("direct label entry does not show the saved [ 1] badge")
            t.send_keys("Escape")
            time.sleep(0.5)
            t.send_keys("Escape")
            time.sleep(0.5)

        # ---- zero analyzer calls across the whole gate ------------------------
        tail = log_tail_since(log_offset)
        analyzer_calls = len(re.findall(r"invoking analyzer|Generalizing \d+ user label", tail, re.IGNORECASE))
        if analyzer_calls:
            failures.append(f"gate made {analyzer_calls} analyzer call(s) — marks must be deterministic")
    finally:
        srv.shutdown()
        xvfb.terminate()
        subprocess.run(["tmux", "kill-server"], capture_output=True)
        try:
            os.remove(seed_path)
        except OSError:
            pass

    print()
    if failures:
        print("V2M8 PREVIEW-MARKS GATE FAILED:")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)
    print("V2M8 PREVIEW-MARKS GATE PASSED")


if __name__ == "__main__":
    main()
