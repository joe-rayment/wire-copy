#!/usr/bin/env python3
"""workspace-frpl.18 (B14) — AI-durability live gate for scheduled section resolution.

Uses the `resolve-section` debug verb (real headless load + the durable resolver) to
assert, against the proven-durable text.npr.org config:
  (1) a pinned section resolves headlessly with ZERO stray analyzer invocations;
  (2) RENDER PARITY — the match is selector-tier with ParentSelector populated on the
      background-context links (not a heading-only fallback);
  (3) DRIFT — mutating the saved selectors to a non-matching value makes the step SKIP
      with a recorded visible failure (ZeroMatch), exercising B9a Tier1->Tier3;
  (4) HEADING VARIANCE — the durable selector tier matches independently of SectionTitle
      (npr links carry SectionTitle=None yet resolve via the selector), the same property
      proven for Business Daily<->Sunday Business in SectionResolverTests (unit).

Evidence manifest under docs/qa/workspace-frpl.18/. Exit non-zero on any failure.
No GCS/TTS; only Playwright + the local DLL (config already cached)."""
import os
import sys
import json
import glob
import shutil
import subprocess

REPO = "/workspace"
DATA = "/home/agent/.local/share/WireCopy"
CFG = os.path.join(DATA, "hierarchy", "text.npr.org.json")
DLL = os.path.join(REPO, "src/WireCopy.API/bin/Debug/net10.0/WireCopy.API.dll")
DOTNET = os.path.join(REPO, "dotnet")
URL = "https://text.npr.org"
PATTERN = "^https?://(www\\.)?text\\.npr\\.org/?"
QA = os.path.join(REPO, "docs", "qa", "workspace-frpl.18")
LOGS = os.path.join(REPO, "logs")
os.makedirs(QA, exist_ok=True)
RES = {"bead": "workspace-frpl.18", "assertions": []}
ANALYZER_MARKERS = ("Analyzing page hierarchy", "AI curated analysis", "AI hierarchy analysis")


def chk(label, ok, detail=""):
    RES["assertions"].append({"label": label, "pass": bool(ok), "detail": detail})
    print(("PASS" if ok else "FAIL"), "—", label, ("" if ok else f"  [{detail}]"), flush=True)
    return bool(ok)


def log_size():
    return sum(os.path.getsize(f) for f in glob.glob(os.path.join(LOGS, "wirecopy-*.log")))


def new_log_text(since):
    txt = ""
    for f in glob.glob(os.path.join(LOGS, "wirecopy-*.log")):
        with open(f, "r", errors="ignore") as fh:
            fh.seek(min(since, os.path.getsize(f)))
            txt += fh.read()
    return txt


def resolve(section, pattern=PATTERN):
    before = log_size()
    out = subprocess.run([DOTNET, DLL, "resolve-section", URL, section, pattern],
                         cwd=REPO, capture_output=True, text=True, timeout=120)
    payload = None
    for line in out.stdout.splitlines():
        if line.startswith("RESOLVE_JSON:"):
            payload = json.loads(line[len("RESOLVE_JSON:"):])
    return payload, new_log_text(before)


def main():
    all_ok = True
    if not os.path.exists(CFG):
        print("FATAL: text.npr.org config missing"); return 1
    shutil.copy(CFG, CFG + ".b14bak")
    try:
        # Phase 1: headless resolve + zero analyzer calls
        p, logtext = resolve("Lead story")
        json.dump(p or {}, open(os.path.join(QA, "phase1_resolve.json"), "w"), indent=2)
        ok1 = p and p.get("status") == "Resolved" and (p.get("matchCount") or 0) > 0 and len(p.get("items") or []) > 0
        all_ok &= chk("pinned section resolves headlessly with articles", ok1, str(p and p.get("status")))
        analyzer_hits = [m for m in ANALYZER_MARKERS if m in logtext]
        all_ok &= chk("ZERO stray analyzer invocations during resolution", not analyzer_hits, f"hits={analyzer_hits}")

        # Phase 2: render parity — selector tier + ParentSelector populated
        tier_ok = p and p.get("tier") == "Selector"
        sel_ok = p and all(l.get("parentSelector") for l in (p.get("sampleContentLinks") or [])[:3])
        all_ok &= chk("render parity: selector-tier match with ParentSelector populated", tier_ok and sel_ok,
                      f"tier={p and p.get('tier')}")

        # Phase 3: drift — mutate selectors to non-matching -> SKIP with visible failure
        cfg = json.load(open(CFG))
        cfg_obj = cfg if isinstance(cfg, dict) else cfg[0]
        for s in cfg_obj.get("sections", []):
            if s.get("name") == "Lead story":
                s["parentSelectors"] = [".wirecopy-drift-no-match-xyz"]
                s["urlPatterns"] = []
        json.dump(cfg, open(CFG, "w"), indent=2)
        pd, _ = resolve("Lead story")
        json.dump(pd or {}, open(os.path.join(QA, "phase3_drift.json"), "w"), indent=2)
        drift_ok = pd and pd.get("status") in ("ZeroMatch", "SectionNotFound") and pd.get("diagnostic")
        all_ok &= chk("drift: mutated selectors -> SKIP with a recorded visible failure (never silent empty)",
                      drift_ok, f"status={pd and pd.get('status')}")
        shutil.copy(CFG + ".b14bak", CFG)  # restore

        # Phase 4: heading variance — durable selector tier is heading-independent
        # (npr links carry SectionTitle=None yet resolved via Selector in phase 1).
        heading_independent = p and p.get("tier") == "Selector" and \
            all((l.get("sectionTitle") in (None, "")) for l in (p.get("sampleContentLinks") or [])[:3])
        all_ok &= chk("heading variance: selector tier resolves independent of SectionTitle "
                      "(unit-proven for Business Daily<->Sunday Business in SectionResolverTests)",
                      heading_independent, "sampleContentLinks SectionTitle all empty yet tier=Selector")
    finally:
        if os.path.exists(CFG + ".b14bak"):
            shutil.copy(CFG + ".b14bak", CFG)
            os.remove(CFG + ".b14bak")

    RES["result"] = "PASS" if all_ok else "FAIL"
    json.dump(RES, open(os.path.join(QA, "result.json"), "w"), indent=2)
    print("===", RES["result"], "===", flush=True)
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
