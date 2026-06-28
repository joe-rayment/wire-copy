#!/usr/bin/env python3
"""
Run the no-steer Ctrl+L wizard on techmeme N times per reasoning-effort tier and
tally how CONSISTENTLY it produces a clean layout — the UX signal that a single
run cannot show. For each run records: section count, whether podcasts leaked in
as a SECTION (bad), whether podcasts/sponsors were excluded (good), whether the
lead is the page's featured story, and coverage. Prints a per-tier summary.

Usage: python3 scripts/measure_layout_consistency.py minimal:3 medium:3
"""

import json
import os
import re
import subprocess
import sys

BASE = "/workspace/output/techmeme-judge"
HARNESS = "/workspace/scripts/judge_techmeme_curation.py"
LEAD_MARK = "mythos 5"  # the featured front-page story on this date


def run(effort, i):
    label = f"consistency-{effort}-{i}"
    subprocess.run(
        ["python3", HARNESS, "--label", label, "--effort", effort],
        check=False, capture_output=True, text=True)
    out = os.path.join(BASE, label)
    rec = {"label": label, "ok": False}
    cfg_path = os.path.join(out, "config.json")
    if not os.path.exists(cfg_path):
        return rec
    c = json.load(open(cfg_path))[0]
    secs = c["sections"]
    excl = " ".join(c.get("excludeSelectors", [])).lower()
    saved = ""
    sp = os.path.join(out, "03-saved.txt")
    if os.path.exists(sp):
        saved = open(sp).read()
    m = re.search(r"(\d+) of (\d+) story links covered", open(os.path.join(out, "01-preview.txt")).read()) \
        if os.path.exists(os.path.join(out, "01-preview.txt")) else None
    # lead = first non-blank content line under the first "Top story"/"Lead" section
    lead = ""
    lines = saved.splitlines()
    for j, ln in enumerate(lines):
        if re.search(r"Top story|Lead story|Lead —|Top Story", ln):
            for k in range(j + 1, min(j + 4, len(lines))):
                t = lines[k].split("│")[0].strip()
                if len(t) > 20:
                    lead = t
                    break
            break
    rail_re = re.compile(r"sponsor|promo|advert|podcast|audio|calendar|event", re.I)
    rail_sections = [s["name"] for s in secs if rail_re.search(s["name"])]
    rec.update({
        "ok": True,
        "n_sections": len(secs),
        "rail_as_section": bool(rail_sections),
        "rail_sections": rail_sections,
        "podcasts_excluded": "podcast" in excl,
        "sponsors_excluded": any(k in excl for k in ("radymre", "sponsor", "topcol2", "promo")),
        "lead": lead[:55],
        "coverage": f"{m.group(1)}/{m.group(2)}" if m else "?",
    })
    return rec


def main():
    plan = [a.split(":") for a in sys.argv[1:]] or [["minimal", "3"], ["medium", "3"]]
    results = {}
    for effort, n in plan:
        results[effort] = [run(effort, i + 1) for i in range(int(n))]

    for effort, recs in results.items():
        print(f"\n===== effort={effort} ({len(recs)} runs) =====")
        good = 0
        for r in recs:
            if not r["ok"]:
                print(f"  {r['label']}: FAILED (no config)")
                continue
            clean = (not r["rail_as_section"]) and (r["sponsors_excluded"] or r["podcasts_excluded"]) and bool(r["lead"])
            good += clean
            print(f"  {r['label']}: sections={r['n_sections']} cov={r['coverage']} "
                  f"rail_section={r['rail_sections'] or 'none'} "
                  f"pod_excl={r['podcasts_excluded']} spon_excl={r['sponsors_excluded']} "
                  f"clean={'YES' if clean else 'no'}")
            print(f"      lead: {r['lead']}")
        print(f"  >>> CLEAN (no sponsor/podcast/event SECTION + rails excluded + has lead): {good}/{len(recs)}")


if __name__ == "__main__":
    main()
