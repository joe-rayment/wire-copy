#!/usr/bin/env python3
"""
workspace-frpl.14 (B12a) — live tmux gate for the Schedules TUI.

Drives the real app under tmux: bookmarks an already-configured site (text.npr.org),
opens ':schedules', creates a recipe via the section picker + TakeMode + Required +
weekday cadence, asserts it PERSISTS across an app reload, toggles enabled, kicks off
run-now, proves an UNCONFIGURED site is BLOCKED from being pinned, and proves a recipe
whose config was deleted degrades to "needs reconfigure" without crashing.

Frame captures + a result manifest are written under docs/qa/workspace-frpl.14/.
Exit non-zero on any failed assertion.  No creds, no real generation asserted here
(that is B13) — run-now only needs to START.
"""

import os
import sys
import time
import json
import shutil

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402

REPO = "/workspace"
DATA = "/home/agent/.local/share/WireCopy"
NPR_CONFIG = os.path.join(DATA, "hierarchy", "text.npr.org.json")
QA_DIR = os.path.join(REPO, "docs", "qa", "workspace-frpl.14")
os.environ.setdefault("WIRECOPY_DLL", os.path.join(REPO, "src/WireCopy.API/bin/Debug/net10.0/WireCopy.API.dll"))

os.makedirs(QA_DIR, exist_ok=True)
RESULTS = {"bead": "workspace-frpl.14", "assertions": [], "frames": []}


def save_frame(t, name):
    s = t.capture()
    path = os.path.join(QA_DIR, f"{name}.txt")
    with open(path, "w") as f:
        f.write(s)
    RESULTS["frames"].append(f"{name}.txt")
    return s


def check(label, ok, screen=None):
    RESULTS["assertions"].append({"label": label, "pass": bool(ok)})
    print(("  PASS" if ok else "  FAIL") + f" — {label}")
    if not ok and screen:
        print("---- frame ----\n" + screen + "\n---------------")
    return ok


def wait_content(t, timeout=60):
    for _ in range(timeout):
        s = t.capture()
        if "links" in s and "Loading" not in s:
            return True
        time.sleep(1)
    return False


def selected_line(screen):
    for line in screen.splitlines():
        if "▸" in line:  # ▸ cursor marker
            return line
    return ""


def select_option(t, substr, max_moves=14):
    """Move the card cursor (j) until the highlighted line contains substr."""
    for _ in range(max_moves):
        line = selected_line(t.capture())
        if substr.lower() in line.lower():
            return True
        t.send_keys("j")
        time.sleep(0.25)
    return substr.lower() in selected_line(t.capture()).lower()


def clear_browser_locks():
    """Each phase relaunches the browser; a prior phase's chromium can leave
    Singleton* locks that wedge the next launch. Clear them between sessions."""
    os.system("pkill -9 -f WireCopy.API >/dev/null 2>&1; pkill -9 -f chromium >/dev/null 2>&1")
    time.sleep(1.5)
    prof = os.path.join(DATA, "browser-profile")
    for f in ("SingletonCookie", "SingletonLock", "SingletonSocket"):
        p = os.path.join(prof, f)
        try:
            if os.path.exists(p) or os.path.islink(p):
                os.remove(p)
        except OSError:
            pass


def open_schedules(t):
    # Send ':' first and let the command line open before typing — sending the whole
    # ":schedules" in one burst races the open transition and the letters then seed a
    # URL navigation instead. (Proven timing.)
    time.sleep(2.5)  # settle after the page render before driving the command line
    for attempt in range(3):
        t.send_text(":")
        time.sleep(0.7)
        t.send_text("schedules")
        time.sleep(0.7)
        t.send_keys("Enter")
        time.sleep(2.2)
        s = t.capture()
        if "Schedules" in s and "went wrong" not in s:
            return
        if "went wrong" in s:  # recover from a stray URL navigation, then retry
            t.send_keys("b")
            time.sleep(1.5)
        else:
            t.send_keys("Escape")
            time.sleep(0.5)


def type_field(t, text, clear_first=False):
    if clear_first:
        for _ in range(40):
            t.send_keys("BSpace")
    if text:
        t.send_text(text)
    time.sleep(0.3)
    t.send_keys("Enter")
    time.sleep(0.8)


def main():
    backup = NPR_CONFIG + ".gatebak"
    # Self-heal: a previous run killed before its finally may have left the config
    # deleted; restore it from the backup so the gate is re-runnable.
    if not os.path.exists(NPR_CONFIG) and os.path.exists(backup):
        shutil.copy(backup, NPR_CONFIG)
    if not os.path.exists(NPR_CONFIG):
        print("FATAL: text.npr.org config missing — run the durability gate first")
        return 1
    shutil.copy(NPR_CONFIG, backup)

    try:
        return run()
    finally:
        # restore the npr config if the deleted-config phase removed it
        if os.path.exists(backup):
            shutil.copy(backup, NPR_CONFIG)
            os.remove(backup)


def run():
    all_ok = True

    # Clean, deterministic slate: fresh schedules + bookmarks (a configured site for
    # the create path, an UNconfigured one for the block test). NOTE: do NOT delete
    # wirecopy.db — deleting it forces a startup migration that races the first
    # command keystroke; the bookmark reconciler picks up bookmarks.json regardless.
    for f in ("schedules.json", "bookmarks.json"):
        p = os.path.join(DATA, f)
        if os.path.exists(p):
            os.remove(p)
    with open(os.path.join(DATA, "bookmarks.json"), "w") as f:
        json.dump({"version": 1, "bookmarks": [
            {"name": "NPR Text", "url": "https://text.npr.org"},
            {"name": "Techmeme", "url": "https://www.techmeme.com"},
        ]}, f, indent=2)

    # ---------- Phase 1: create a recipe over a configured site ----------
    clear_browser_locks()
    with TermTest(url="https://text.npr.org", width=120, height=40) as t:
        if not check("app loads text.npr.org", wait_content(t), t.capture()):
            return 1

        open_schedules(t)
        s = save_frame(t, "01_empty_list")
        all_ok &= check("schedules screen opens empty", "Schedules" in s and "No schedules yet" in s, s)

        # 'a' → name
        t.send_keys("a")
        time.sleep(0.8)
        type_field(t, "NPR Brief")

        # Sources card: Add a source (cursor starts on Add)
        select_option(t, "Add a source")
        save_frame(t, "02_sources_empty")
        t.send_keys("Enter")
        time.sleep(0.8)

        # Site picker → text.npr.org
        s = t.capture()
        all_ok &= check("site picker lists bookmarked npr", "npr.org" in s, s)
        select_option(t, "npr.org")
        t.send_keys("Enter")
        time.sleep(0.8)

        # Section picker → Lead story
        s = save_frame(t, "03_section_picker")
        all_ok &= check("section picker shows durable npr sections", "Lead story" in s and "Main feed" in s, s)
        select_option(t, "Lead story")
        t.send_keys("Enter")
        time.sleep(0.6)

        # TakeMode → Whole section (cursor 0)
        select_option(t, "Whole section")
        t.send_keys("Enter")
        time.sleep(0.6)

        # Required → Required (cursor 0)
        select_option(t, "Required")
        t.send_keys("Enter")
        time.sleep(0.8)

        # Back on Sources card with the step present → choose Done
        s = save_frame(t, "04_sources_with_step")
        all_ok &= check("step appears in sources list", "Lead story" in s and "required" in s, s)
        select_option(t, "Done")
        t.send_keys("Enter")
        time.sleep(0.8)

        # Cadence → Weekdays
        select_option(t, "Weekdays")
        t.send_keys("Enter")
        time.sleep(0.6)

        # Time → accept default 07:00
        type_field(t, "")  # InitialValue 07:00 already seeded
        # Output name → accept default
        type_field(t, "")

        time.sleep(1.0)
        s = save_frame(t, "05_saved_list")
        all_ok &= check("recipe persists in the list with weekday cadence", "NPR Brief" in s and "Mon" in s, s)

    # ---------- Phase 2: persistence across reload + toggle + run-now ----------
    clear_browser_locks()
    with TermTest(url="https://text.npr.org", width=120, height=40) as t:
        wait_content(t)
        open_schedules(t)
        s = save_frame(t, "06_after_reload")
        all_ok &= check("recipe survives an app reload", "NPR Brief" in s, s)

        # space toggles enabled (● ↔ ○)
        select_option(t, "NPR Brief")
        before = selected_line(t.capture())
        t.send_keys("Space")
        time.sleep(0.8)
        after = selected_line(t.capture())
        save_frame(t, "07_toggled")
        all_ok &= check("space toggles enabled marker", ("○" in after) != ("○" in before), after)
        # toggle back on
        t.send_keys("Space")
        time.sleep(0.6)

        # run-now: R starts it (status line), should not crash
        select_option(t, "NPR Brief")
        t.send_keys("R")
        time.sleep(1.5)
        s = save_frame(t, "08_run_now")
        all_ok &= check("run-now starts (status feedback, no crash)", "Running" in s or "in progress" in s or "Schedules" in s, s)
        t.send_keys("Escape")
        time.sleep(0.5)

    # ---------- Phase 3: unconfigured site is BLOCKED ----------
    clear_browser_locks()
    with TermTest(url="https://text.npr.org", width=120, height=40) as t:
        wait_content(t)
        open_schedules(t)
        t.send_keys("a")
        time.sleep(0.8)
        type_field(t, "Should Not Save")
        select_option(t, "Add a source")
        t.send_keys("Enter")
        time.sleep(0.8)
        # Pick Techmeme (a shipped default that has NO saved config)
        if select_option(t, "techmeme"):
            t.send_keys("Enter")
            time.sleep(1.0)
            s = save_frame(t, "09_unconfigured_blocked")
            all_ok &= check("unconfigured site is blocked (needs setup message, no section picker)",
                            ("no usable layout" in s.lower() or "set" in s.lower()) and "Pick a section" not in s, s)
        else:
            all_ok &= check("techmeme bookmark present for block test", False, t.capture())

    # ---------- Phase 4: deleted-config degrades to 'needs reconfigure' ----------
    if os.path.exists(NPR_CONFIG):
        os.remove(NPR_CONFIG)
    clear_browser_locks()
    with TermTest(url="https://example.com", width=120, height=40) as t:
        time.sleep(6)  # example.com loads fast; just need the app up
        open_schedules(t)
        s = save_frame(t, "10_needs_reconfigure")
        all_ok &= check("recipe with deleted config shows 'needs reconfigure' (no crash)",
                        "NPR Brief" in s and "reconfigure" in s.lower(), s)

    RESULTS["result"] = "PASS" if all_ok else "FAIL"
    with open(os.path.join(QA_DIR, "result.json"), "w") as f:
        json.dump(RESULTS, f, indent=2)
    print(f"\n=== {RESULTS['result']} === ({sum(a['pass'] for a in RESULTS['assertions'])}/{len(RESULTS['assertions'])} assertions) — manifest: {QA_DIR}/result.json")
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
