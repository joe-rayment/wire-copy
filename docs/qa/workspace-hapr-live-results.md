# workspace-hapr live verification results (workspace-1izo retry)

**Captured:** 2026-05-22
**Bead:** workspace-1izo (retry of workspace-hrrf after workspace-j0b8 unblocked macleans loading)
**Script:** `scripts/test_macleans_ai_curated.py`
**Outcome:** AI Curated diagnostics + analyzer logging verified live on a substitute site (BBC News). macleans.ca itself still unreachable from this CI IP — see "Why a substitute" below.

## Why a substitute

The workspace-1izo retry first attempted the bead-named site `https://macleans.ca`. With workspace-j0b8's headless-fallback (auto-fall-back when `LaunchBrowserAsync` hits "Missing X server") the page now loads far enough for `HumanActionDetector` to fire — the screen shows:

```
╭────────────────────────────────────────────────╮
│ ⡇ Site is showing a CAPTCHA                    │
│ Solve it in the browser window, then press R   │
│ https://macleans.ca                            │
│ macleans.ca — Shift+O:open  b:back             │
╰────────────────────────────────────────────────╯
```

j0b8 fixed the original "Page navigated mid-load" symptom, but the underlying issue is now site-side bot blocking on this CI IP (same class as NYT blocking — known per `feedback_selenium_setup.md`). workspace-iv9g's HumanActionWatcher polled (`logs/wirecopy-20260522.log:11:04:57.958 [INF] Human-action watcher: gate resolved for https://macleans.ca`) then re-detected the captcha on retry. No human is available to solve in headless mode, so this CI cannot exercise macleans.

To still validate the workspace-hapr deliverable (diagnostic INFO line + `DetectDegenerateRanking`), the retry ran with `TARGET_URL=https://www.bbc.com/news`. BBC News is a comparable article-list page (≥100 content links, multi-section layout) that's not bot-blocked in this CI.

## Live capture — Document Order baseline (BBC News)

128 links across 3 sections. Top of the rendered tree:

```
╭─ Home - BBC News ────────────────────────────────────────────────────────────╮
│ www.bbc.co.uk · 128 links · 3 sections                                       │
╰──────────────────────────────────────────────────────────────────────────────╯
```

First five visible link titles (heuristic capture from rendered tree, two-column layout):

1. Live. 'My job not necessarily to select the 26 most talented players' - Tuchel on England squad
2. Parents of Southport survivors say anonymity has erased their girls from the story
3. Andrew investigation could look into sexual misconduct allegations
4. Stop blaming young people for being unemployed, says Amazon's UK boss
5. Guardiola says 'nothing is eternal' as Man City confirm exit after 10 years

Full capture: `/tmp/qa-shots-1izo-bbc2/1_doc_order.txt`.

## Live capture — AI Curated (BBC News)

Re-organized into 5 AI-detected sections. Top section: **"Top live coverage & sport (8)"**. First five visible link titles:

1. Live. 'My job not necessarily to select the 26 most talented players' - Tuchel on England squad
2. Live. Heat warnings upgraded as parts of UK to be hit by bank holiday heatwave
3. Live. Guardiola statement after he steps down as Man City boss
4. Guardiola says 'nothing is eternal' as Man City confirm exit after 10 years
5. Carrick confirmed as Man Utd permanent boss

Full capture: `/tmp/qa-shots-1izo-bbc2/4_after_ai_curated.txt`.

**Top-5 comparison: DIFFERENT.** The AI ranking promoted live-coverage entries to the top and merged the football/sport block, distinct from doc-order's mixed politics/sport/health ordering. Section headers also changed (DocumentOrder had its own 3 sections; AI Curated produced 5).

## AiCuratedStrategy log line (live INFO from workspace-hapr's diagnostic)

```
2026-05-22 11:09:24.775 +00:00 [INF] AiCuratedStrategy: invoking analyzer for https://www.bbc.co.uk/news (cached=false, ttlExpired=false)
2026-05-22 11:09:30.130 +00:00 [INF] AiCuratedStrategy result for https://www.bbc.co.uk/news: 37 ranked stories, 17 excluded, 5 sections, fromCache=false. First10=url:https://www.bbc.co.uk/sport/football/live/cd6pwdj0v90t,url:https://www.bbc.co.uk/news/live/cr4pkz6z0gpt,url:https://www.bbc.co.uk/sport/football/live/c0m2pvzp420t,url:https://www.bbc.co.uk/sport/football/articles/cx21y7023qxo,url:https://www.bbc.co.uk/sport/football/articles/cd9pk7g3kelo,url:https://www.bbc.co.uk/sport/football/articles/ce9p4v50pkko,url:https://www.bbc.co.uk/news/articles/c0j2q988x17o,url:https://www.bbc.co.uk/news/articles/c707l2jx0z4o,url:https://www.bbc.co.uk/news/articles/c0l2x5351n4o,url:https://www.bbc.co.uk/news/articles/c4g0ryrewdxo
```

## Verdict against the bead's acceptance criteria

workspace-hapr asked: "Does `DetectDegenerateRanking` fire for the real macleans output, or does the AI return a non-trivial-but-still-bad ranking that the detector missed?"

**On BBC News (substitute):** `DetectDegenerateRanking` would return `None` — the AI produced 37 ranked + 17 excluded + 5 sections, with a top-5 that differs from doc order. The detector correctly stays silent for a non-degenerate result. The diagnostic INFO line (workspace-hapr's first deliverable) is emitted with all 10 First10 keys + the (counts, fromCache) tuple, exactly as designed.

**On macleans.ca specifically:** the question cannot be answered from this CI because the site is bot-blocked at the network layer. If macleans.ca is the site where the user originally observed the "no reorder" complaint, the reproduction needs to happen on the user's machine where their browser session/cookies bypass the CAPTCHA. The diagnostic log line will surface the (cached, ranked, excluded, sections) tuple in that environment too.

## Open follow-ups

- No degenerate-output observation yet exists for a real site (only synthetic unit-test inputs). If the user reproduces the original macleans symptom on their machine, capture `logs/wirecopy-*.log` for the `AiCuratedStrategy result` line and the rendered tree. The (ranked, excluded, sections) tuple in the INFO line distinguishes the failure shape.
- If a future site DOES produce `MatchesDocumentOrder` or `EmptyRanking`, the user-visible Summary should read e.g. `AI curated · N stories (no reordering — matches document order)` per workspace-hapr's wiring. That branch of the user-visible copy remains unobserved in live capture.

## Artifacts

- `/tmp/qa-shots-1izo-bbc2/1_doc_order.txt` — Document Order baseline (BBC News).
- `/tmp/qa-shots-1izo-bbc2/2_chooser.txt` — chooser at open after Ctrl+L.
- `/tmp/qa-shots-1izo-bbc2/3_ai_curated_preview.txt` — preview frame when AI Curated rotated into the chooser.
- `/tmp/qa-shots-1izo-bbc2/4_after_ai_curated.txt` — AI Curated rendered tree (final state after Enter).
- `/tmp/qa-shots-1izo/0_load_failed.txt` — original macleans.ca attempt that hit the CAPTCHA.
- `/workspace/logs/wirecopy-20260522.log` — full app log including the INFO line above and the macleans HumanActionWatcher events.
