#!/usr/bin/env python3
"""
workspace-42q8.7 — live tmux gate for the schedule-from-the-page flow (g s) and
the reworked :schedules editor, driving the REAL app headful under Xvfb against
LOCAL fixtures (no network, no model calls, never headless).

Outcome-asserted per the Verification Doctrine (drives the real action, asserts
what the user sees + what landed on disk):

  A. g s on a CONFIGURED sectioned page with the cursor moved INTO section 2
     (state change, not the auto-selected first row): the card pre-fills that
     section; the saved recipe pins it (schedules.json content asserted); the
     confirmation names the section, schedule, and next run. Then 's' on a
     section HEADER opens the same card (previously a silent no-op).
  B. g s on a FLAT unconfigured page: no WHAT card (single option), a WholePage
     step with 'All stories' + SingleTopStory is saved, and a flat DocumentOrder
     config file APPEARS for the fixture host.
  C. g s on an AUTO-GROUPED unconfigured page: the DOM sections the user SEES
     are offered and pinned; the derived config file carries them; a forced
     refresh still renders the same section headers (WYSIWYG revisit).
  D. :schedules add-step drift honesty: a bookmark whose URL regex-misses the
     site's seeded config gets 'Use saved layout for /sectioned.html' (the old
     build showed the false 'has no saved layout' here — red/green captured in
     RED_old_build_drift.txt vs GREEN_new_build_drift.txt), 'This page' leads
     the source picker, and the pinned step records the seeded ConfigUrlPattern.
     (H): g s at the launcher shows the guidance hint instead of a silent no-op.
  E. br7w surfaces driven for the first time: per-step edit (take-mode change
     persists to disk), required toggle, remove + the Done guard; run-now on the
     drifted recipe resolves via the DURABLE ConfigUrlPattern (S1 live), fails
     loudly on the tiny fixture articles, updates the row IN PLACE, and the
     launcher shows the failure badge; deleting the config file degrades the
     row to 'needs reconfigure: <section>'.

Frames + manifest under docs/qa/workspace-42q8.7/. Exit non-zero on any FAIL.
"""

import http.server
import json
import os
import shutil
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
os.environ.setdefault(
    "WIRECOPY_DLL", "src/WireCopy.API/bin/Debug/net10.0/WireCopy.API.dll")
from termtest import TermTest  # noqa: E402

DISPLAY = ":93"
SCREEN_W, SCREEN_H = 1600, 900
TERM_W, TERM_H = 140, 42
DATA = os.path.expanduser("~/.local/share/WireCopy")
HIERARCHY_DIR = os.path.join(DATA, "hierarchy")
QA_DIR = "/workspace/docs/qa/workspace-42q8.7"

os.makedirs(QA_DIR, exist_ok=True)
RESULTS = {"bead": "workspace-42q8.7", "assertions": [], "frames": []}

# ---------------------------------------------------------------- fixtures ----

PAGE_TITLE = "Daily Sections Gazette"

ARTICLE = ("<!doctype html><html><head><title>{t}</title></head><body>"
           "<h1>{t}</h1><p>A very short stub article body.</p></body></html>")


def story_list(prefix, names):
    return "\n".join(
        f'<li><a href="/{prefix}/{i}.html">{n}</a></li>'
        for i, n in enumerate(names, 1))


SECTIONED_BODY = f"""
<section>
<h2>World</h2>
<ul>
{story_list('world', ['Global summit reaches accord on trade terms',
                      'Coastal cities brace for the spring flood season',
                      'Election observers arrive ahead of the vote'])}
</ul>
</section>
<section>
<h2>Business</h2>
<ul>
{story_list('biz', ['Chipmaker posts record quarterly earnings again',
                    'Retail giant expands same-day delivery network',
                    'Startups chase the compact fusion power prize'])}
</ul>
</section>
"""

FLAT_BODY = "<ul>\n" + story_list(
    "story", ["Museum reopens after a decade of restoration",
              "City council approves the riverfront overhaul",
              "Observatory spots a comet returning early",
              "Farmers test drought-resistant barley strains",
              "Library digitizes a century of local papers",
              "Rail line trials quiet overnight freight runs"]) + "\n</ul>"


def page(body):
    return (f"<!doctype html><html><head><title>{PAGE_TITLE}</title></head>"
            f"<body><h1>{PAGE_TITLE}</h1>{body}</body></html>").encode()


def serve(pages: dict[str, bytes]):
    """Serve a {path: html} dict on an ephemeral port; unknown story paths get a stub article."""

    class H(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            body = pages.get(self.path)
            if body is None and self.path.endswith(".html"):
                name = self.path.strip("/").replace("/", " ").replace(".html", "")
                body = ARTICLE.format(t=name).encode()
            if body is None:
                self.send_response(404)
                self.end_headers()
                return
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *a):
            pass

    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv, srv.server_address[1]


def seed_sectioned_config(port: int, path_pattern: str = "/sectioned\\.html") -> str:
    """A durable two-section config for the fixture host, pinned to one path."""
    domain = f"127.0.0.1:{port}"
    config = [{
        "domain": domain,
        "urlPattern": f"^http://127\\.0\\.0\\.1:{port}{path_pattern}",
        "sections": [
            {"name": "World", "sortOrder": 0, "parentSelectors": [],
             "urlPatterns": ["/world/"], "startCollapsed": False, "maxLinks": None},
            {"name": "Business", "sortOrder": 1, "parentSelectors": [],
             "urlPatterns": ["/biz/"], "startCollapsed": False, "maxLinks": None},
        ],
        "createdAt": "2026-07-10T00:00:00Z",
        "modelVersion": "42q8-gate-seed",
        "kind": 3,
        "version": 3,
        "strategy": "AiCurated",
        "excludeSelectors": [],
        "excludeUrlPatterns": [],
        "excludeSectionTitles": [],
        "needsReanalyze": False,
    }]
    os.makedirs(HIERARCHY_DIR, exist_ok=True)
    path = os.path.join(HIERARCHY_DIR, f"{domain.replace(':', '_')}.json")
    with open(path, "w") as f:
        json.dump(config, f, indent=2)
    return path


def hierarchy_path(port: int) -> str:
    return os.path.join(HIERARCHY_DIR, f"127.0.0.1_{port}.json")


# ----------------------------------------------------------------- helpers ----

def save_frame(t, name):
    s = t.capture()
    with open(os.path.join(QA_DIR, f"{name}.txt"), "w") as f:
        f.write(s)
    RESULTS["frames"].append(f"{name}.txt")
    return s


def check(label, ok, screen=None):
    RESULTS["assertions"].append({"label": label, "pass": bool(ok)})
    print(("  PASS" if ok else "  FAIL") + f" — {label}")
    if not ok and screen:
        print("---- frame ----\n" + screen + "\n---------------")
    return bool(ok)


def wait_tree(t, marker, timeout=90):
    for _ in range(timeout):
        s = t.capture()
        if marker in s and "Loading" not in s:
            time.sleep(2.0)  # settle: first paint → interactive
            return True
        time.sleep(1)
    return False


def selected_line(screen):
    for line in screen.splitlines():
        if "▸" in line:
            return line
    return ""


def select_option(t, substr, max_moves=40):
    for _ in range(max_moves):
        if substr.lower() in selected_line(t.capture()).lower():
            return True
        t.send_keys("j")
        time.sleep(0.25)
    return substr.lower() in selected_line(t.capture()).lower()


def type_field(t, text, clear_first=False):
    if clear_first:
        for _ in range(50):
            t.send_keys("BSpace", delay=0.02)
    if text:
        t.send_text(text)
    time.sleep(0.3)
    t.send_keys("Enter")
    time.sleep(0.9)


def chord_gs(t):
    t.send_keys("g")
    time.sleep(0.4)
    t.send_keys("s")
    time.sleep(1.2)


def open_schedules(t):
    time.sleep(2.0)
    for _ in range(3):
        t.send_text(":")
        time.sleep(0.7)
        t.send_text("schedules")
        time.sleep(0.7)
        t.send_keys("Enter")
        time.sleep(2.2)
        s = t.capture()
        if "Schedules" in s and "went wrong" not in s:
            return True
        t.send_keys("Escape")
        time.sleep(0.5)
    return False


def clear_browser_locks():
    os.system("pkill -9 -f WireCopy.API >/dev/null 2>&1; pkill -9 -f chrome >/dev/null 2>&1")
    time.sleep(1.5)
    prof = os.path.join(DATA, "browser-profile")
    for f in ("SingletonCookie", "SingletonLock", "SingletonSocket"):
        p = os.path.join(prof, f)
        try:
            if os.path.exists(p) or os.path.islink(p):
                os.remove(p)
        except OSError:
            pass


def read_schedules():
    p = os.path.join(DATA, "schedules.json")
    if not os.path.exists(p):
        return {"recipes": []}
    with open(p) as f:
        return json.load(f)


def recipe_by_name(name):
    return next((r for r in read_schedules().get("recipes", []) if r.get("name") == name), None)


# ------------------------------------------------------------------ phases ----

def phase_a(port):
    """g s on a configured sectioned page — cursor moved into section 2."""
    print("\n== Phase A: g s on a configured sectioned page ==")
    ok = True
    cfg_path = seed_sectioned_config(port)
    cfg_before = open(cfg_path).read()
    url = f"http://127.0.0.1:{port}/sectioned.html"
    clear_browser_locks()
    with TermTest(url=url, width=TERM_W, height=TERM_H) as t:
        if not check("A0 page renders the configured sections", wait_tree(t, "Business"), t.capture()):
            return False

        # Move the cursor INTO Business (World hdr → w1..w3 → Business hdr → b1).
        for _ in range(5):
            t.send_keys("j")
            time.sleep(0.2)

        chord_gs(t)
        s = save_frame(t, "A1_gs_card")
        ok &= check("A1 card opens with the cursor's section pre-filled first",
                    "Add to schedule" in s and "Business" in selected_line(s)
                    and "the section you're on" in selected_line(s), s)
        ok &= check("A1b card offers the whole page too", "All stories" in s, s)

        t.send_keys("Enter")           # WHAT: Business
        time.sleep(0.8)
        select_option(t, "Whole section")
        t.send_keys("Enter")           # HOW MANY
        time.sleep(0.8)
        s = save_frame(t, "A2_schedule_pick")
        ok &= check("A2 schedule picker offers 'New schedule'", "New schedule" in s, s)
        select_option(t, "New schedule")
        t.send_keys("Enter")
        time.sleep(0.9)
        type_field(t, "Biz Daily", clear_first=True)   # name (prefilled with page title)
        select_option(t, "Every day")
        t.send_keys("Enter")           # cadence preset
        time.sleep(0.8)
        type_field(t, "")              # time 07:00 default
        time.sleep(1.2)

        s = save_frame(t, "A3_confirmation")
        ok &= check("A3 confirmation names section, schedule and next run",
                    "Added Business" in s and "Biz Daily" in s and "next run" in s, s)

        r = recipe_by_name("Biz Daily")
        step = (r or {}).get("steps", [{}])[0]
        ok &= check("A4 schedules.json pins the CURSOR'S section (Business, whole section, required)",
                    r is not None and step.get("sectionName") == "Business"
                    and step.get("scope", "PinnedSection") == "PinnedSection"
                    and step.get("takeMode") == "WholeSection"
                    and step.get("required") is True
                    and step.get("sourceUrl") == url,
                    json.dumps(step, indent=2))
        ok &= check("A5 the seeded config was reused, not overwritten",
                    open(cfg_path).read() == cfg_before)

        # 's' on a section header routes into the same card.
        t.send_keys("g")
        time.sleep(0.3)
        t.send_keys("g")               # jump to top: World header
        time.sleep(0.8)
        t.send_keys("s")
        time.sleep(1.2)
        s = save_frame(t, "A6_s_on_header")
        ok &= check("A6 's' on the World section header opens the card preselected on World",
                    "Add to schedule" in s and "World" in selected_line(s), s)
        t.send_keys("Escape")
        time.sleep(0.5)
    return ok


def phase_b(port):
    """g s on a flat unconfigured page — whole-page step + config file appears."""
    print("\n== Phase B: g s on a flat unconfigured page ==")
    ok = True
    url = f"http://127.0.0.1:{port}/flat.html"
    cfg = hierarchy_path(port)
    if os.path.exists(cfg):
        os.remove(cfg)
    clear_browser_locks()
    with TermTest(url=url, width=TERM_W, height=TERM_H) as t:
        if not check("B0 flat page renders", wait_tree(t, "Museum reopens"), t.capture()):
            return False
        chord_gs(t)
        s = save_frame(t, "B1_take_card_directly")
        ok &= check("B1 flat page skips the WHAT card (single option) straight to HOW MANY",
                    "How many stories?" in s and "Add to schedule" not in s, s)
        select_option(t, "top story")
        t.send_keys("Enter")
        time.sleep(0.9)
        select_option(t, "New schedule")
        t.send_keys("Enter")
        time.sleep(0.9)
        type_field(t, "Flat Top", clear_first=True)
        select_option(t, "Every day")
        t.send_keys("Enter")
        time.sleep(0.8)
        type_field(t, "")
        time.sleep(1.2)

        s = save_frame(t, "B2_confirmation")
        ok &= check("B2 confirmation names 'All stories'", "Added All stories" in s and "Flat Top" in s, s)
        r = recipe_by_name("Flat Top")
        step = (r or {}).get("steps", [{}])[0]
        ok &= check("B3 schedules.json holds a WholePage/top-story step",
                    r is not None and step.get("scope") == "WholePage"
                    and step.get("sectionName") == "All stories"
                    and step.get("takeMode") == "SingleTopStory",
                    json.dumps(step, indent=2))
        ok &= check("B4 a flat DocumentOrder config file appeared for the host",
                    os.path.exists(cfg) and json.load(open(cfg))[0]["sections"] == [],
                    cfg)
    return ok


def phase_c(port):
    """g s on an auto-grouped unconfigured page — visible sections pinnable + WYSIWYG revisit."""
    print("\n== Phase C: g s on an auto-grouped unconfigured page ==")
    ok = True
    url = f"http://127.0.0.1:{port}/sectioned.html"
    cfg = hierarchy_path(port)
    if os.path.exists(cfg):
        os.remove(cfg)
    clear_browser_locks()
    with TermTest(url=url, width=TERM_W, height=TERM_H) as t:
        if not check("C0 unconfigured page auto-groups by its DOM headings", wait_tree(t, "Business"), t.capture()):
            return False
        for _ in range(5):
            t.send_keys("j")
            time.sleep(0.2)
        chord_gs(t)
        s = save_frame(t, "C1_gs_card_autogroups")
        ok &= check("C1 the DOM sections the user SEES are offered (cursor's first)",
                    "Business" in selected_line(s) and "World" in s and "All stories" in s, s)
        t.send_keys("Enter")
        time.sleep(0.8)
        select_option(t, "Whole section")
        t.send_keys("Enter")
        time.sleep(0.8)
        select_option(t, "New schedule")
        t.send_keys("Enter")
        time.sleep(0.9)
        type_field(t, "Auto Biz", clear_first=True)
        select_option(t, "Every day")
        t.send_keys("Enter")
        time.sleep(0.8)
        type_field(t, "")
        time.sleep(1.2)

        r = recipe_by_name("Auto Biz")
        step = (r or {}).get("steps", [{}])[0]
        ok &= check("C2 the pinned step references the derived section by name",
                    r is not None and step.get("sectionName") == "Business"
                    and step.get("scope", "PinnedSection") == "PinnedSection",
                    json.dumps(step, indent=2))
        derived = json.load(open(cfg))[0]["sections"] if os.path.exists(cfg) else None
        ok &= check("C3 the derived config carries BOTH detected sections in order",
                    derived is not None and [x["name"] for x in derived] == ["World", "Business"],
                    json.dumps(derived, indent=2))

        # WYSIWYG revisit: force refresh (Shift+R) — the saved config must
        # reproduce the exact section headers the user saw when they pinned.
        t.send_keys("R")
        time.sleep(1.0)
        ok &= check("C4 revisit still renders the same section headers",
                    wait_tree(t, "Business") and "World" in t.capture(), t.capture())
        save_frame(t, "C5_revisit_sections")
    return ok


def phase_hint():
    """g s at the launcher: guidance, no card, no crash."""
    print("\n== Phase H: g s at the launcher ==")
    ok = True
    clear_browser_locks()
    with TermTest(width=TERM_W, height=TERM_H) as t:  # boots at the launcher
        time.sleep(8)
        chord_gs(t)
        s = save_frame(t, "H1_launcher_hint")
        ok &= check("H1 g s at the launcher explains itself (no card, no crash)",
                    "link list first" in s and "Add to schedule" not in s, s)
    return ok


def phase_d(port_drift, drift_name, drift_bookmark_url):
    """:schedules add-step: 'This page' first + drift honesty."""
    print("\n== Phase D: :schedules drift honesty ==")
    ok = True
    url = f"http://127.0.0.1:{port_drift}/flat.html"
    clear_browser_locks()
    with TermTest(url=url, width=TERM_W, height=TERM_H) as t:
        if not check("D0 page renders", wait_tree(t, "Museum reopens"), t.capture()):
            return False
        if not check("D2 :schedules opens", open_schedules(t), t.capture()):
            return False
        t.send_keys("a")
        time.sleep(0.9)
        type_field(t, "Drift Daily", clear_first=True)
        select_option(t, "Add a source")
        t.send_keys("Enter")
        time.sleep(0.9)
        s = save_frame(t, "D3_site_picker")
        ok &= check("D3 'This page' is offered FIRST; the drifted bookmark is listed",
                    "This page" in s and drift_name in s, s)
        found = select_option(t, drift_name)
        ok &= check("D3b the picker cursor reaches the drifted bookmark", found, t.capture())
        t.send_keys("Enter")
        time.sleep(1.2)
        s = save_frame(t, "D4_drift_offer")
        ok &= check("D4 the site's saved layout is OFFERED (the old build lied 'has no saved layout')",
                    "Site is set up" in s and "Use saved layout for /sectioned.html" in s
                    and "has no saved layout" not in s, s)
        t.send_keys("Enter")           # use the saved layout
        time.sleep(0.9)
        s = save_frame(t, "D5_section_picker")
        ok &= check("D5 its sections become pickable (plus All stories)",
                    "World" in s and "Business" in s and "All stories" in s, s)
        select_option(t, "World")
        t.send_keys("Enter")
        time.sleep(0.8)
        select_option(t, "Whole section")
        t.send_keys("Enter")
        time.sleep(0.8)
        select_option(t, "Required")
        t.send_keys("Enter")
        time.sleep(0.9)
        select_option(t, "Done")
        t.send_keys("Enter")
        time.sleep(0.8)
        select_option(t, "Weekdays")
        t.send_keys("Enter")
        time.sleep(0.8)
        type_field(t, "")              # time default
        type_field(t, "")              # output name default
        time.sleep(1.2)
        s = save_frame(t, "D6_saved")
        ok &= check("D6 recipe saved and listed", "Drift Daily" in s, s)

        r = recipe_by_name("Drift Daily")
        step = (r or {}).get("steps", [{}])[0]
        ok &= check("D7 the step records the seeded ConfigUrlPattern (durable identity)",
                    r is not None and "/sectioned\\.html" in step.get("configUrlPattern", "")
                    and step.get("sourceUrl") == drift_bookmark_url,
                    json.dumps(step, indent=2))
        t.send_keys("Escape")
        time.sleep(0.5)
    return ok


def phase_e(port_drift, drift_name):
    """br7w per-step surfaces + run-now via the durable lookup + badge + needs-reconfigure."""
    print("\n== Phase E: per-step edits, run-now, badge, needs-reconfigure ==")
    ok = True
    cfg_path = os.path.join(HIERARCHY_DIR, f"127.0.0.1_{port_drift}.json")
    url = f"http://127.0.0.1:{port_drift}/flat.html"
    clear_browser_locks()
    with TermTest(url=url, width=TERM_W, height=TERM_H) as t:
        if not check("E00 page renders", wait_tree(t, "Museum reopens"), t.capture()):
            return False
        if not check("E0 :schedules opens", open_schedules(t), t.capture()):
            return False

        # --- edit walk (br7w.1 surfaces, first live drive). Order matters: the
        # remove/re-add test runs FIRST so the take-mode change is what lands on
        # disk at Done — proving per-step edits actually persist.
        select_option(t, "Drift Daily")
        t.send_keys("e")
        time.sleep(0.9)
        type_field(t, "")              # keep the name

        # remove + Done guard: x on the only step, Done must block
        select_option(t, "World")
        t.send_keys("x")
        time.sleep(0.8)
        s = save_frame(t, "E1_removed")
        ok &= check("E1 step removed (status confirms)", "Removed step" in s, s)
        select_option(t, "Done")
        t.send_keys("Enter")
        time.sleep(0.8)
        s = t.capture()
        ok &= check("E2 Done blocks with no required step", "REQUIRED" in s or "required" in s, s)

        # re-add via the bookmark path (drift offer again)
        select_option(t, "Add a source")
        t.send_keys("Enter")
        time.sleep(0.9)
        found = select_option(t, drift_name)
        ok &= check("E5b the picker cursor reaches the drifted bookmark again", found, t.capture())
        t.send_keys("Enter")
        time.sleep(1.2)
        t.send_keys("Enter")           # use saved layout offer
        time.sleep(0.9)
        select_option(t, "World")
        t.send_keys("Enter")
        time.sleep(0.8)
        select_option(t, "Whole section")
        t.send_keys("Enter")
        time.sleep(0.8)
        select_option(t, "Required")
        t.send_keys("Enter")
        time.sleep(0.9)

        # per-step menu: change the take mode to Top 2
        select_option(t, "World")
        t.send_keys("Enter")
        time.sleep(0.9)
        s = save_frame(t, "E3_step_menu")
        ok &= check("E3 per-step menu opens (take / required / remove)",
                    "How many stories" in s and "Remove this step" in s, s)
        t.send_keys("Enter")           # change take mode
        time.sleep(0.8)
        select_option(t, "Top N")
        t.send_keys("Enter")
        time.sleep(0.8)
        type_field(t, "2", clear_first=True)
        s = save_frame(t, "E4_top2_label")
        ok &= check("E4 sources row reflects 'top 2'", "top 2" in s, s)

        # required toggle → optional on the row → toggle back (floor stays strict)
        select_option(t, "World")
        t.send_keys("Enter")
        time.sleep(0.9)
        select_option(t, "toggle")
        t.send_keys("Enter")
        time.sleep(0.8)
        s = t.capture()
        ok &= check("E5 required toggled to optional on the row", "optional" in s, s)
        select_option(t, "World")
        t.send_keys("Enter")
        time.sleep(0.9)
        select_option(t, "toggle")
        t.send_keys("Enter")
        time.sleep(0.8)

        select_option(t, "Done")
        t.send_keys("Enter")
        time.sleep(0.8)
        select_option(t, "Weekdays")
        t.send_keys("Enter")
        time.sleep(0.8)
        type_field(t, "")
        type_field(t, "")
        time.sleep(1.2)
        r = recipe_by_name("Drift Daily")
        step = (r or {}).get("steps", [{}])[0]
        ok &= check("E6 the per-step edits PERSISTED to disk (Top 2, required)",
                    r is not None and step.get("takeMode") == "TopN"
                    and step.get("takeCount") == 2 and step.get("required") is True,
                    json.dumps(step, indent=2))

        # --- run-now: resolution must ride the DURABLE ConfigUrlPattern (the
        # sourceUrl regex-misses the config on purpose). The tiny stub articles
        # then fail generation LOUDLY — the row updates in place and the
        # launcher badges the failure.
        select_option(t, "Drift Daily")
        t.send_keys("R")
        time.sleep(1.5)
        s = save_frame(t, "E7_run_now_started")
        ok &= check("E7 run-now starts in the background", "Running" in s or "background" in s, s)

        terminal = False
        for _ in range(60):            # ≤ 3 min; each keypress re-reads the store
            t.send_keys("j")
            time.sleep(3)
            row = t.capture()
            if "last run failed" in row or "last run partial" in row or "last run ok" in row:
                terminal = True
                break
        s = save_frame(t, "E8_run_row_updated")
        ok &= check("E8 the row updates IN PLACE to a terminal result", terminal, s)
        ok &= check("E8b the tiny-article run failed loudly (never a silent empty episode)",
                    "last run failed" in s, s)

        # --- needs-reconfigure: delete the config file, re-read the list in place
        if os.path.exists(cfg_path):
            os.remove(cfg_path)
        t.send_keys("j")
        time.sleep(2.0)
        s = save_frame(t, "E10_needs_reconfigure")
        ok &= check("E10 deleted config degrades the row to 'needs reconfigure: World'",
                    "reconfigure" in s.lower() and "World" in s, s)

        # --- launcher badge: leave the schedules screen (the failed run was NOT
        # acknowledged — ack happens on OPEN, before the run finished), back out
        # of the page to the launcher, and the badge must be there.
        t.send_keys("Escape")
        time.sleep(1.5)
        t.send_keys("b")
        time.sleep(2.5)
        s = save_frame(t, "E11_launcher_badge")
        ok &= check("E11 the launcher badges the failed run BY NAME",
                    ("Scheduled run failed" in s and "Drift Daily" in s) or "need attention" in s, s)
        ok &= check("E12 the failure is a GENERATION-stage one — resolution via the durable "
                    "ConfigUrlPattern passed the quality floor (S1 live)",
                    "contributed no articles" not in s and "No articles resolved" not in s, s)
    return ok


def main():
    # One live app instance at a time (shared browser profile + data files):
    # a second concurrent gate corrupts both runs.
    import fcntl
    lock_f = open("/tmp/42q8-sched-gate.lock", "w")
    try:
        fcntl.flock(lock_f, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        print("FATAL: another gate instance is running — refusing to start")
        return 1

    os.environ.pop("TMUX", None)
    os.environ["TMUX_TMPDIR"] = "/tmp/42q8-sched-tmux"
    os.makedirs(os.environ["TMUX_TMPDIR"], exist_ok=True)
    subprocess.run(["tmux", "kill-server"], capture_output=True)
    lock = f"/tmp/.X{DISPLAY.lstrip(':')}-lock"
    if os.path.exists(lock):
        os.remove(lock)
    xvfb = subprocess.Popen(["Xvfb", DISPLAY, "-screen", "0", f"{SCREEN_W}x{SCREEN_H}x24"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    os.environ["DISPLAY"] = DISPLAY

    # Preserve the operator's real data files; run on a clean slate.
    backups = {}
    for name in ("schedules.json", "bookmarks.json"):
        p = os.path.join(DATA, name)
        if os.path.exists(p):
            backups[p] = p + ".42q8bak"
            shutil.copy(p, backups[p])
            os.remove(p)

    pages = {"/sectioned.html": page(SECTIONED_BODY),
             "/flat.html": page(FLAT_BODY),
             "/other-page.html": page(SECTIONED_BODY)}
    srv_a, port_a = serve(pages)
    srv_b, port_b = serve(pages)
    srv_c, port_c = serve(pages)
    srv_d, port_d = serve(pages)

    # Phase D seeds: config pinned to /sectioned.html; bookmark points at
    # /other-page.html on the SAME host → URL-regex miss, site-level hit.
    # The bookmark NAME embeds the port: the bookmark DB accumulates entries
    # across runs, and a stale same-named bookmark (dead port) would be picked
    # by the label matcher instead of this run's.
    seed_sectioned_config(port_d)
    drift_name = f"Drifted {port_d}"
    drift_bookmark = f"http://127.0.0.1:{port_d}/other-page.html"
    with open(os.path.join(DATA, "bookmarks.json"), "w") as f:
        json.dump({"version": 1, "bookmarks": [
            {"name": drift_name, "url": drift_bookmark}]}, f, indent=2)

    all_ok = True
    try:
        all_ok &= phase_a(port_a)
        all_ok &= phase_b(port_b)
        all_ok &= phase_c(port_c)
        all_ok &= phase_hint()
        all_ok &= phase_d(port_d, drift_name, drift_bookmark)
        all_ok &= phase_e(port_d, drift_name)
    finally:
        clear_browser_locks()
        for orig, bak in backups.items():
            if os.path.exists(bak):
                shutil.copy(bak, orig)
                os.remove(bak)
            elif os.path.exists(orig):
                os.remove(orig)  # bak consumed elsewhere; drop the gate's file
        for port in (port_a, port_b, port_c):
            p = hierarchy_path(port)
            if os.path.exists(p):
                os.remove(p)
        pd = hierarchy_path(port_d)
        if os.path.exists(pd):
            os.remove(pd)
        for s in (srv_a, srv_b, srv_c, srv_d):
            s.shutdown()
        xvfb.terminate()

    RESULTS["result"] = "PASS" if all_ok else "FAIL"
    with open(os.path.join(QA_DIR, "result.json"), "w") as f:
        json.dump(RESULTS, f, indent=2)
    passed = sum(a["pass"] for a in RESULTS["assertions"])
    print(f"\n=== {RESULTS['result']} === ({passed}/{len(RESULTS['assertions'])} assertions) — manifest: {QA_DIR}/result.json")
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
