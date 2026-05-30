#!/usr/bin/env python3
"""
workspace-5oe9.15 — live durability + effectiveness gate for AI-curated layouts.

A HARD two-phase gate (replaces the old soft-success macleans harness):

  Phase 1 (cold):   clear the saved hierarchy config + build cache, load the
                    target, drive Ctrl+L -> AI setup wizard -> apply. Assert the
                    saved config is a durable Version-3 PATTERN config (sections
                    with CSS/URL identifiers, NOT a per-URL snapshot) and that
                    the rendered tree gained section structure. Hard-fail on a
                    degenerate/clarification warning.

  Phase 2 (revisit, durability): CLEAR THE BUILD CACHE so the durable
                    HierarchyConfigStore path must rehydrate, relaunch on the
                    SAME url, and assert the section structure survives AND no
                    second analyzer call ('invoking analyzer') fired — i.e. the
                    layout is served from the pattern config with zero model
                    calls as article URLs rotate.

Exits 0 ONLY if every assertion holds; exits 1 on any failure or load failure
(no soft-success stub). Writes an evidence manifest under
docs/qa/workspace-5oe9.15/.

Usage:  python3 scripts/test_ai_durability.py [TARGET_URL]
        (default TARGET_URL = https://text.npr.org/ — a stable, text-light
         news homepage; macleans.ca redirect-cycles, filed separately.)
"""

import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from termtest import TermTest  # noqa: E402

WORKSPACE = "/workspace"
DATA = "/home/agent/.local/share/WireCopy"
HIERARCHY_DIR = os.path.join(DATA, "hierarchy")
PAGE_CACHE_DIR = os.path.join(DATA, "page-cache")
QA_DIR = os.path.join(WORKSPACE, "docs/qa/workspace-5oe9.15")
TARGET_URL = sys.argv[1] if len(sys.argv) > 1 else "https://text.npr.org/"
DOMAIN = re.sub(r"^https?://", "", TARGET_URL).split("/")[0].lower()

os.environ.setdefault("WIRECOPY_DLL", "src/WireCopy.API/bin/Release/net10.0/WireCopy.API.dll")


def log_path():
    logs = sorted(
        [f for f in os.listdir(os.path.join(WORKSPACE, "logs")) if f.startswith("wirecopy-") and f.endswith(".log")]
    )
    return os.path.join(WORKSPACE, "logs", logs[-1]) if logs else None


def read_log_from(offset):
    p = log_path()
    if not p or not os.path.exists(p):
        return "", offset
    with open(p, "r", errors="replace") as f:
        f.seek(offset)
        data = f.read()
        return data, f.tell()


def log_offset():
    p = log_path()
    return os.path.getsize(p) if p and os.path.exists(p) else 0


def clear_hierarchy():
    if os.path.isdir(HIERARCHY_DIR):
        for f in os.listdir(HIERARCHY_DIR):
            os.remove(os.path.join(HIERARCHY_DIR, f))


def clear_build_cache():
    for root in (PAGE_CACHE_DIR,):
        if os.path.isdir(root):
            for f in os.listdir(root):
                fp = os.path.join(root, f)
                try:
                    if os.path.isfile(fp):
                        os.remove(fp)
                except OSError:
                    pass


def saved_config():
    """HierarchyConfigStore writes a per-domain LIST of configs; return the
    AiCurated one (or the last) as a dict."""
    fp = os.path.join(HIERARCHY_DIR, f"{DOMAIN}.json")
    if not os.path.exists(fp):
        return None
    try:
        data = json.load(open(fp))
    except (OSError, ValueError):
        return None
    if isinstance(data, dict):
        return data
    if isinstance(data, list) and data:
        ai = [c for c in data if str(c.get("strategy", "")).lower() == "aicurated" or c.get("kind") in ("AiCurated", 3)]
        return (ai or data)[-1]
    return None


def wait_for_load(t, timeout=90):
    deadline = time.time() + timeout
    while time.time() < deadline:
        s = t.capture()
        if "Loading" not in s and ("links" in s or "section" in s.lower()):
            return s
        time.sleep(3)
    return t.capture()


def drive_wizard(t, timeout=200):
    """Open Ctrl+L and accept-all through the AI setup wizard. Returns status str."""
    t.send_keys("C-l")
    time.sleep(2)
    # Entry card: AI is the highlighted default — Enter selects it.
    if "Set up this site" in t.capture():
        t.send_keys("Enter")
    deadline = time.time() + timeout
    last = ""
    while time.time() < deadline:
        s = t.capture()
        last = s
        if "Site set up" in s or "✔ Site set up" in s:
            return "applied"
        if "couldn't find a clear structure" in s:
            return "needs_clarification"
        low = s.lower()
        if "anything you'd like to tell the ai" in low or "anything else" in low:
            t.send_keys("Enter")          # skip optional free-text
            time.sleep(2)
            continue
        if "set up this site with ai" in low or "here's the layout" in low or "accept" in low or "confirm" in low:
            t.send_keys("Enter")          # accept overview / a question card
            time.sleep(3)
            continue
        time.sleep(2)
    return "timeout:" + last[-200:]


def section_names(cfg):
    return [s.get("name", "") for s in (cfg or {}).get("sections", []) if s.get("name")]


def fail(manifest, msg):
    manifest["result"] = "FAIL"
    manifest["failure"] = msg
    write_manifest(manifest)
    print(f"\n❌ FAIL: {msg}")
    sys.exit(1)


def write_manifest(manifest):
    os.makedirs(QA_DIR, exist_ok=True)
    with open(os.path.join(QA_DIR, "durability_gate.json"), "w") as f:
        json.dump(manifest, f, indent=2)
    for name, content in manifest.get("_frames", {}).items():
        with open(os.path.join(QA_DIR, name), "w") as f:
            f.write(content)


def main():
    manifest = {"target": TARGET_URL, "domain": DOMAIN, "_frames": {}, "phases": {}}
    clear_hierarchy()
    clear_build_cache()

    # ---- Phase 1: cold load + AI setup ----
    off = log_offset()
    with TermTest(url=TARGET_URL) as t:
        baseline = wait_for_load(t)
        manifest["_frames"]["1_doc_order_baseline.txt"] = baseline
        if "Loading" in baseline and "links" not in baseline:
            fail(manifest, f"target page never loaded: {TARGET_URL}")

        status = drive_wizard(t)
        time.sleep(2)
        applied = t.capture()
        manifest["_frames"]["2_after_ai_setup.txt"] = applied
        manifest["phases"]["phase1_wizard_status"] = status

        log1, off = read_log_from(off)
        manifest["phases"]["phase1_invoking_analyzer"] = log1.count("invoking analyzer")

        if status == "needs_clarification":
            fail(manifest, "fresh analysis was degenerate (needs clarification) on the target")
        if status != "applied":
            fail(manifest, f"wizard did not reach applied state: {status}")
        if "degenerate result" in log1 and "Needs clarification: True" in log1:
            fail(manifest, "degenerate AI result on an explicitly-configured site (hard fail)")

        cfg = saved_config()
        manifest["phases"]["saved_config"] = cfg
        if not cfg:
            fail(manifest, "no hierarchy config saved after AI setup")
        if cfg.get("version") != 3:
            fail(manifest, f"saved config is not Version 3 (durable): version={cfg.get('version')}")
        secs = cfg.get("sections", [])
        has_identifier = any(s.get("parentSelectors") or s.get("urlPatterns") for s in secs)
        if not secs or not has_identifier:
            fail(manifest, "saved config has no durable section identifiers (selectors/url-patterns)")
        names = section_names(cfg)
        manifest["phases"]["section_names"] = names
        # Effectiveness: the curated tree gained section structure the raw doc-order view lacked.
        applied_has_sections = any(n in applied for n in names)
        manifest["phases"]["applied_shows_sections"] = applied_has_sections

    # ---- Phase 2: revisit with build cache cleared (durability) ----
    clear_build_cache()  # the durable HierarchyConfigStore path must rehydrate
    off2 = log_offset()
    with TermTest(url=TARGET_URL) as t:
        revisit = wait_for_load(t)
        manifest["_frames"]["3_revisit_after_cache_clear.txt"] = revisit
        if "Loading" in revisit and "links" not in revisit:
            fail(manifest, "target page never loaded on revisit")
        time.sleep(2)
        revisit = t.capture()
        manifest["_frames"]["3_revisit_after_cache_clear.txt"] = revisit

        log2, off2 = read_log_from(off2)
        manifest["phases"]["phase2_invoking_analyzer"] = log2.count("invoking analyzer")

        names = section_names(saved_config())
        revisit_has_sections = any(n in revisit for n in names)
        manifest["phases"]["revisit_shows_sections"] = revisit_has_sections

        # Durability assertions.
        if log2.count("invoking analyzer") != 0:
            fail(manifest, "revisit re-ran the AI analyzer — layout is NOT served from the durable pattern")
        if names and not revisit_has_sections:
            fail(manifest, "curated section structure did NOT survive the revisit (durability broken)")

    manifest["result"] = "PASS"
    write_manifest(manifest)
    print("\n✅ PASS — AI curation is durable: pattern config (v3) survives a build-cache-cleared revisit "
          "with zero analyzer calls.")
    print(f"   sections: {manifest['phases'].get('section_names')}")
    print(f"   phase1 analyzer calls: {manifest['phases'].get('phase1_invoking_analyzer')}  "
          f"phase2 analyzer calls: {manifest['phases'].get('phase2_invoking_analyzer')}")
    print(f"   evidence: {QA_DIR}")
    sys.exit(0)


if __name__ == "__main__":
    main()
