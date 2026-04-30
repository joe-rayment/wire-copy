# Design Critique: WireCopy Design System & Mockups

Reviewed: 2026-03-18

---

## 1. Executive Summary

The design system is remarkably thorough -- it specifies exact ANSI codes, character sets, animation frame timings, and per-screen color mappings. The mockups are detailed and internally consistent with each other, covering most normal states plus empty states and scroll indicators. However, the system has three fundamental tensions that need resolution before implementation: (1) the animation and toast subsystems require a background rendering thread that does not exist in the current architecture, (2) the design system document contradicts its own mockups in several places and also contradicts the current code on key details, and (3) the scope of the layout variant system is drastically larger than the mockups suggest -- the mockups show simple cycling toasts but the design system specifies 3 variants per screen, which is effectively 5 new renderers that need building. The quality ceiling is high, but the gap between what is specified and what the codebase can currently support is also large.

---

## 2. Blockers

### B1. Toast Notifications Require a Background Timer -- Architecture Does Not Support One

**[BLOCKER]** The toast system (toast.txt, design-system.md Section 3.5) specifies toasts that auto-dismiss after 2-5 seconds, render as overlays on top of existing content, and restore the underlying characters on dismiss. The current rendering architecture is entirely synchronous and event-driven: `TerminalInputHandler.WaitForInputAsync()` blocks on a key press or resize event. There is no background tick or render loop. The `RenderHelpers.WriteLine()` method does direct `Console.Write` with cursor repositioning -- there is no retained-mode screen buffer.

This means:
- There is no mechanism to fire a "dismiss toast" event after N seconds without a keypress.
- Rendering a toast overlay requires saving the characters underneath, but `RenderHelpers` does not maintain a screen buffer. It only tracks `_linesWritten`.
- "Any keypress dismisses the topmost toast" (toast.txt line 156) would need to be woven into the input handler, but the input handler returns `NavigationCommand` objects, not raw keys. A toast dismiss is not a navigation command.

**Proposed alternative**: Instead of time-based auto-dismiss, display the toast on the next full render (which happens after every keypress anyway) and keep it visible for 1 render cycle. If the user presses another key, the next render clears it. This is simpler and fits the existing architecture. For truly timed toasts, you would need to introduce a `CancellationTokenSource` timeout racing against the key channel in `WaitForInputAsync`, which is doable but non-trivial.

### B2. Animations Require a Frame Loop That Does Not Exist

**[BLOCKER]** The animation specifications (design-system.md Section 4, animations.txt) describe frame-by-frame animations at 50-100ms intervals: sparkle bursts (10 frames at 80ms), decrypt effects (8 frames at 50ms), color waves (10 frames at 60ms), typewriter reveals (12+ frames), and breathing bars (15 frames at 120ms). The current codebase has no animation infrastructure whatsoever. Rendering is strictly "render once per input event."

Implementing these animations would require:
1. A dedicated animation loop that runs on a separate thread (or at minimum, a `Task` with `Task.Delay` calls).
2. Partial screen updates -- the animations only touch specific regions (e.g., the title line, the status bar separator), so the animation loop needs to be able to update specific cursor positions without re-rendering the entire screen.
3. Thread safety around `Console.Write` -- currently all Console writes happen on the main thread. Concurrent writes from an animation thread would corrupt output.
4. A mechanism to cancel/interrupt animations on user input.

This is a significant architectural addition. The design system should acknowledge this and specify the infrastructure needed, not just the visual effect.

**Recommendation**: Implement animations as a final phase, after all static UI is working. Start with the simplest animation (cache pulse -- 3 frames, color-only changes on a single line) to validate the approach before attempting the complex sparkle/decrypt effects.

### B3. AccentFg ANSI Code Mismatch Between Design System and Current Code

**[BLOCKER]** The design system (line 46) specifies `AccentFg` as ANSI 51 (`#00ffff`, bright cyan). The current code in `BuiltInThemes.cs` (line 40) sets `AccentFg` to ANSI 43 (`~#00afaf`, dark desaturated cyan). The design-inspiration.md document explicitly calls this out: "AccentFg is already set to ANSI 43 (~#00afaf), but this is too dark."

The design system states: "This document is the single source of truth; when it conflicts with existing code, the design system wins." However, changing ANSI 43 to ANSI 51 will affect every place `AccentFg` is currently used, and the visual impact on existing screens needs to be verified. ANSI 51 is significantly brighter and more saturated than 43 -- it could be jarring in contexts where the current muted cyan works well.

More importantly, the current code uses `AccentFg` in the cache indicator suffix (`PromptFg` for "cached" text in `CollectionRenderer.cs` line 184), while the design system says the cache badge should use `AccentFg` (line 51 mentions `ToastBorderFg` at ANSI 51). The status bar renderer (line 88) uses a hardcoded `"\x1b[38;5;220m"` for the amber active color rather than a palette property, which contradicts the design system's "no ad-hoc color literals" rule (line 27-28).

**Resolution needed**: The ANSI 43-to-51 change must be explicitly planned as a migration step. All current usages of `AccentFg` and any ad-hoc ANSI codes in renderer code need to be inventoried and updated. The `StatusBarRenderer` amber color literal on line 88 needs to become a theme palette property.

---

## 3. Problems

### P1. Design System Section 5 (Screens) Contradicts Mockups on Key Details

**[PROBLEM]** The design system's screen-by-screen specs in Section 5 describe the *current* implementation for loading, error, and bot challenge screens (minimal, unboxed text at lines 996-1034), while the mockups in `loading-error.txt` show a completely redesigned centered-and-boxed layout. The design system says "single source of truth" but then presents two different designs for the same screens.

Specific contradictions:
- Design system Section 5.6 (line 996): Loading screen is `⠇ Loading...` with URL, unboxed, near top.
  Mockup loading-error.txt (lines 26-34): Loading screen is a centered rounded box with breathing room.
- Design system Section 5.7 (line 1007): Error screen is plain text with `Something went wrong...`.
  Mockup loading-error.txt (lines 80-92): Error is a centered box with `Page failed to load` headline and structured action hints.
- Design system Section 5.8 (line 1023): Bot challenge is `⠇ Bot challenge detected` unboxed.
  Mockup loading-error.txt (lines 163-178): Bot challenge is a centered box with amber spinner and breathing bar.

**Fix**: The design system must be updated to match the mockups for these screens. Delete the minimal descriptions in Section 5.6-5.8 and replace with the boxed designs.

### P2. Status Bar Key Hint Color Inconsistency Between Design System and Mockups

**[PROBLEM]** The mockups (launcher.txt line 41, linktree.txt line 50, reader.txt line 48, etc.) consistently show key shortcuts in `[cyan]` (ANSI 51) with descriptions in `[med]` (ANSI 34). The design system Section 3.8 (line 516) specifies keys in `PrimaryText` (ANSI 46, bright green) and actions in `SecondaryText` (ANSI 34).

The current code in `StatusBarRenderer.cs` line 310-311 uses `PrimaryText` for keys, matching the design system but NOT the mockups. The launcher footer (line 367) uses a different format (`SecondaryText` brackets with `PrimaryText` key) -- also different from the mockups.

The mockups explicitly use `[cyan]` for the key portion, which would mean using `AccentFg` (ANSI 51) for key letters. This is a substantial visual change that affects every screen.

**Resolution**: Decide whether keys should be green (current code + design system) or cyan (mockups). The cyan approach is more visually distinctive and aligns with the "accent color for interactive elements" philosophy. But the design system text and the mockup annotations disagree, so one must be corrected.

### P3. Layout Variant System Scope Is Much Larger Than Mockups Suggest

**[PROBLEM]** The design system (Section 6) specifies 13 layout variants across 5 screens (Launcher: 3, LinkTree: 3, Reader: 3, CollectionItems: 2, CollectionList: 1, plus a cycling mechanism). The mockups show these variants as static ASCII art. But implementing them means:

- **Launcher Variant B** (single-column list): An entirely new layout path in `LauncherRenderer`, since the current code is tightly coupled to the 2-column grid (see `BuildCellLine`, `ComputeLayout`).
- **Launcher Variant C** (3-column compact): Another new layout path, with truncation logic for no-domain display.
- **LinkTree Variant B** (dense list, 1 line per item): Fundamentally different from the current card rendering, which assumes multi-line cells. The `LinkTreeRenderer` would need a parallel rendering path.
- **LinkTree Variant C** (magazine): Yet another path.
- **Reader Variants**: Relatively easy (just changing `MaxContentWidth`), but Variant C "Narrow" requires centering logic.
- **CollectionItems Variant B** (compact 1-line): Domain right-alignment is non-trivial with variable-width title text.

The `ILayoutVariantProvider` interface (design system line 1058-1063) is clean, but the spec doesn't address how variant switching interacts with:
- Scroll position (does changing layout reset scroll? It should preserve the selected item, which means index-to-visual-position mapping changes).
- Cell height calculations used in `LauncherCommandHandler` (the command handler uses `LauncherRenderer.ComputeLayout` -- variants would need to parameterize this).
- The breadcrumb status bar (listed as "Future Enhancement" in statusbar.txt line 162 but part of the design system).

**Recommendation**: Phase the implementation. Implement Variant A for all screens first (since that matches the current code most closely), then add one variant at a time. The layout cycling toast (layout-switcher.txt) should be implemented early as it is simple and provides the UI chrome for switching even before additional variants exist.

### P4. ThemePalette Needs 8 New Properties But No Migration Plan for Existing Themes

**[PROBLEM]** The design system (Section 1.2, lines 77-100) lists 8 new nullable `ThemeColor?` properties for `ThemePalette`: `CelebrationFg`, `SuccessFg`, `WarningFg`, `ToastBorderFg`, `ToastCelebrationBorderFg`, `ProgressFilledFg`, `ProgressEmptyFg`, `ProgressActiveFg`. With fallback getters.

The current `ThemePalette.cs` is a `record` with `required init` properties (except `AccentFg` and `DimFg`, which are nullable). Adding 8 more nullable properties is straightforward -- but `BuiltInThemes.cs` defines 4 themes, and the design system only provides explicit values for Phosphor (full), Amber (4 properties), Dracula (4 properties), and Light (4 properties). The remaining 4 new properties per non-Phosphor theme would fall back to other colors, but the fallback chain is specified only for the Phosphor theme values.

**Fix**: The design system should provide a complete color table for all 4 themes showing both the explicit value and the resolved fallback for every new property. Without this, implementers will have to guess at theme-appropriate celebration/toast/progress colors for Amber, Dracula, and Light.

### P5. Mockup Color Key Uses `[green]` for Key Hints But Design System Says PrimaryText

**[PROBLEM]** The statusbar.txt mockup (lines 25, 57, etc.) shows key shortcuts in `[green]` (ANSI 46, PrimaryText). The launcher.txt mockup (lines 29, 41) shows them in `[cyan]` (ANSI 51, AccentFg). The reading-list.txt mockup (line 46) shows them in `[cyan]`. These are inconsistent with each other.

The statusbar.txt file is the authoritative status bar specification and it uses `[green]` for keys. But the launcher footer (statusbar.txt does not cover it) uses `[cyan]` in the launcher.txt mockup. This suggests the intent may be: launcher footer uses cyan keys, status bar uses green keys. But that distinction is not documented anywhere.

---

## 4. Concerns

### C1. Toast Overlay Content Restoration Is Fragile

**[CONCERN]** The design system specifies that toasts "save the characters underneath" and "restore the original content on dismiss" (Section 3.5, line 431). In a terminal where the screen is composed of direct `Console.Write` calls with no retained buffer, this means the toast renderer must either:
1. Read back the screen content via terminal escape sequences (unreliable, not universally supported).
2. Maintain a shadow buffer of all written content.
3. Simply trigger a full re-render on toast dismiss.

Option 3 is the most pragmatic (and the current code already re-renders on every input), but it means the toast cannot truly auto-dismiss in the background without causing a full re-render, which brings us back to the background thread problem (Blocker B1).

### C2. The "Decrypt" Page Load Effect May Feel Slow

**[CONCERN]** The decrypt animation (Section 4.3, animations.txt) takes 400ms (8 frames at 50ms). For pages loaded from cache, the design system correctly says to skip it. But for network-loaded pages, the user has already waited for the page to load (potentially seconds). Adding 400ms of "visual decryption" on top of the actual load time may feel like artificial delay rather than delight. The user wanted speed; the decrypt effect communicates "look how fast I'm decoding this" but actually adds latency.

**Consideration**: Users who are power-users of a terminal browser are likely to find this charming exactly once and annoying thereafter. Consider making it toggle-able or only triggering it on the very first page load of a session.

### C3. Celebration Pink May Look Off in Non-Phosphor Themes

**[CONCERN]** The inspiration document makes a strong case for hot pink (ANSI 198) as green's complement. But the Dracula theme specifies pink (ANSI 212, `#ff87d7`) and the Light theme specifies purple (ANSI 128, `#af00d7`). In Dracula, pink is already the `HeaderTitleFg` (ANSI 212), which means the "celebration" color would not feel special -- it matches the header color. The surprise factor depends on the color being alien to the palette.

**Recommendation**: For Dracula, use a *different* celebration color than the existing header pink. Consider bright yellow (ANSI 226) or bright white flash, which would be more surprising in a Dracula context. The spec should be explicit about this.

### C4. 3-Column Launcher Grid (Variant C) Has Very Narrow Cells

**[CONCERN]** The launcher.txt mockup Variant C (line 161-168) shows 3 columns with ~25 chars per cell. At 80 columns terminal width, after subtracting 2 margin and 2 dividers, each cell gets ~25 characters. Bookmark names like "HACKER NEWS" (11 chars) fit fine, but "NEWS.YCOMBINATOR.COM" (20 chars) barely fits as a domain, and longer names like "ARS TECHNICA" (12 chars) are fine but "MARGINAL REVOLUTION" (19 chars) is already truncated in the mockup to "MARGINAL REV...". This variant is labeled for 100+ width terminals in the mockup notes, but the design system Variant C (line 1096) says "3 columns or 2 if < 90 chars." At 80 columns with 3 columns, the cells are too narrow for meaningful content.

**Fix**: Either raise the 3-column threshold to 100+ characters (as the mockup note suggests), or document that Variant C automatically drops to 2 columns at 80.

### C5. The "AI Layout" Badge Is Confusing Terminology

**[CONCERN]** The status bar shows "AI layout" (statusbar.txt line 105, 109) when AI-generated hierarchy is active. Separately, the layout switching system uses "Layout A/B/C" (layout-switcher.txt). Having both "AI layout" and "Layout B" on the same status bar line (as shown in the status bar mockup line 138) could confuse users: is "AI layout" a layout variant, or something different? The design system does clarify they are separate concepts (layout-switcher.txt lines 187-193), but the user sees both on the same line.

**Suggestion**: Rename "AI layout" to "AI hierarchy" or "AI tree" to distinguish it clearly from the layout variant system.

### C6. Breathing Cursor Not Specified in Design System

**[CONCERN]** The inspiration document (Section 7.7) describes a "breathing cursor" where the selection background oscillates between ANSI 22 and 28 on a 2-second cycle. This is not included in the design system or any mockup. If it is intended to be implemented, it has the same background-thread problem as animations. If it is not intended, it should be explicitly listed as "not included" to avoid confusion.

### C7. The Design System Claims All Corners Must Be Rounded But Sub-Section Headers Already Are

**[CONCERN]** The consistency rule (Section 7.6, line 1289) says "All box corners are rounded, never sharp." The current code in `Borders.cs` already uses rounded corners (`\u256d`, `\u256e`, `\u2570`, `\u256f`) for all boxes. This is good -- no migration needed. But the status bar separator uses a plain `─` line, not a box, so the rule is satisfied trivially. The concern is that the mockups also show `╭─ Section Name (8) ─╮` for sub-section headers in the link tree (linktree.txt line 41), which is an *open-bottom* box. The `Borders.SectionHeader()` method (Borders.cs line 55) already implements this. So this is actually fine -- just noting it for completeness.

---

## 5. Suggestions

### S1. Implement the Status Bar Color Change for AccentFg Before Anything Else

**[SUGGESTION]** The single highest-impact, lowest-risk change is upgrading `AccentFg` from ANSI 43 to ANSI 51 in the Phosphor theme. This brightens the cache indicators and any future accent usage. It requires changing one number in `BuiltInThemes.cs` (line 40) and is immediately visible. Do this first to establish the new color vocabulary.

### S2. Add a `RenderContext` Object Instead of Growing Parameter Lists

**[SUGGESTION]** The `RenderStatusBar` method already takes 8 parameters. Adding toast state, animation state, layout variant, and more will make these signatures unwieldy. Consider introducing a `RenderContext` struct that bundles all the state needed for a single render pass (terminal dimensions, theme, navigation context, cache progress, toast queue, active animation, layout variant). Each renderer gets the context and picks what it needs.

### S3. Use the Existing Resize Detection Channel for Toast Timing

**[SUGGESTION]** `TerminalInputHandler` already races a resize channel against the key channel using `Task.WhenAny`. A third channel for timer events (toast expiry, animation ticks) could be added to the same `WhenAny` race. When a timer fires, the handler returns a `NavigationCommand` of type `TimerTick` or `ToastExpired`, which triggers a re-render. This avoids a separate background rendering thread while still enabling timed behavior. This is the pragmatic path to implementing toasts and animations.

### S4. The Mockup for "Podcast Button with Musical Note" Should Be the Default

**[SUGGESTION]** The reading-list.txt mockup (line 110-116) shows an alternative CTA using `♪` instead of `▶▶`. The musical note is more distinctive, thematically correct (it is a podcast feature), and aligns with the design-inspiration.md recommendation (Section 7.3: "Podcast-related items get a `♪` prefix in cyan"). The `▶▶` icon reads as generic "play" and does not convey "podcast" specifically.

### S5. Specify Minimum Terminal Dimensions

**[SUGGESTION]** The mockups assume 80x24 but the design system references behaviors at various thresholds (30-line separators, 20-line compact CTA, 15-line compact cells, 50-char loading box). The minimum supported terminal size is never explicitly stated. Consider documenting: "Minimum supported: 50 columns x 15 rows. Below this, the app remains functional but visual quality degrades." This sets expectations and gives implementers a floor for testing.

### S6. Document the Exact Key Hint Color Decision

**[SUGGESTION]** Pick one approach for key hint styling and document it clearly:
- Option A: Keys in `PrimaryText` (green, ANSI 46), matching the current code and design system text. Pro: consistent green aesthetic.
- Option B: Keys in `AccentFg` (cyan, ANSI 51), matching the mockups. Pro: keys visually pop, clearly interactive.
If Option B, update the design system Section 3.8. If Option A, update the mockups.

---

## 6. Missing Pieces

### M1. No Help Overlay Mockup

The design system (Section 3.8, line 535) mentions "Help overlay (?): separate screen, not part of the design system yet." Every screen has `?:help` as a key hint. Users will press it. What do they see? This needs a mockup.

### M2. No Prompt/Input Mode Mockup

The design system references "text input (URL bar, search, command mode)" in the toast spec (line 168) but there is no mockup for:
- The URL bar in active input mode (cursor, typed text, cancel behavior).
- The search prompt (`/query` input).
- The command mode (`:layout grid2`).

These are critical interaction states. Currently the input handler shows a simple prompt, but the design system should specify how prompts look with the new visual language (colors, cursor character, position).

### M3. No Mockup for Very Small Terminals (< 60 columns)

Several components have compact modes triggered at narrow widths, but no mockup shows what the entire screen looks like at, say, 50x15. At that size, the launcher URL bar, header box, and footer might barely fit. The 3-line compact cell mode, inline CTA, and minimal hint tier should all be exercised in a small-terminal mockup.

### M4. No Mockup for the Reading List with Podcast Generation In-Progress

The animations.txt file (lines 180-262) shows the podcast generation progress screen in detail, but there is no standalone mockup file for this screen. It replaces the collection items view during generation. The status bar behavior, key hints (Esc to cancel), and the transition back to the normal view after completion are not mocked.

### M5. Collection List Grid Variant (B) Selection Rendering

The collections.txt Variant B (lines 119-152) shows cards with rounded borders, but the selected card has `[sel-bg]` inside the card while the border stays `[dim]`. This raises a question: does the border change color when selected? The mockup shows the border staying dim, which means the selection indicator is only the inner background color. But there is no accent bar `▌` on the selected card -- it relies purely on background color change. This is inconsistent with every other selection mechanism in the system, which uses the accent bar.

### M6. Keyboard Shortcut for Layout Switching Conflicts

The design system says `Ctrl+L` for layout cycling. But in many terminal emulators, `Ctrl+L` is a standard shortcut for "clear screen" (same as the `clear` command). The terminal app may or may not intercept it. The design system should document terminal compatibility and provide a fallback (the `:layout` command is the fallback, but it should be more prominently noted).

### M7. No Specification for Toast Stacking Limit Change

The toast mockup (toast.txt lines 132-139) says toasts stack vertically with a limit of 3. But the design system (Section 3.5, line 433) says "Only one toast visible at a time; new toast replaces existing." These directly contradict each other.

---

## 7. Implementation Priority

Recommended order, highest visual impact first, lowest risk first:

### Phase 1: Color and Character Updates (1-2 days)

1. **Upgrade AccentFg from ANSI 43 to ANSI 51** in Phosphor theme. Verify all existing usages look good.
2. **Add 8 new ThemePalette properties** with fallback getters. Set explicit values for Phosphor; leave other themes using fallbacks initially.
3. **Remove hardcoded ANSI codes** from renderers (the `"\x1b[38;5;220m"` in StatusBarRenderer line 88, any others). Route all colors through the palette.
4. **Replace sharp corners with rounded corners** -- verify this is already done (it appears to be, per Borders.cs). No-op if confirmed.

### Phase 2: Static UI Redesign (3-5 days)

5. **Loading/Error/Bot Challenge screens** -- implement the centered-box design from loading-error.txt. These are isolated screens with no interaction; pure rendering.
6. **Toast notification component** -- build the renderer (box drawing at specific cursor positions), but wire it to a simple "show on next render, clear on next render" lifecycle. Skip auto-dismiss timing for now.
7. **Eighth-block progress bar** for podcast generation. Pure rendering, no animation.
8. **Braille spinner animation** -- this is the simplest animation (single character change at a known cursor position) and validates the timer-tick approach. Add a timer channel to `TerminalInputHandler`.

### Phase 3: Layout Variants (5-8 days)

9. **ILayoutVariantProvider** interface and UserSettingsStore integration.
10. **Layout cycling toast** (the small overlay from layout-switcher.txt). Uses the toast component from Phase 2.
11. **Launcher Variant B** (list mode). Lowest-effort new layout.
12. **LinkTree Variant B** (dense list). Highest value -- power users want this.
13. **CollectionItems Variant B** (compact). Moderate effort.
14. **Launcher Variant C** (3-column). Only for wide terminals.
15. **Reader Variants B/C**. Trivial (just width constants).

### Phase 4: Animations (3-5 days, highest risk)

16. **Timer tick infrastructure** in `TerminalInputHandler` (if not done in Phase 2).
17. **Cache pulse animation** (3 frames, color-only, single line). Simplest animation.
18. **Color wave on status bar** (10 frames, color-only, single line). Medium complexity.
19. **Page load decrypt effect** (8 frames, character + color changes, single line region). Medium.
20. **Podcast celebration** (sparkle + typewriter, multi-region). Most complex, do last.

### Phase 5: Polish (2-3 days)

21. **Toast auto-dismiss timing** (requires the timer tick to be working).
22. **Toast stacking** (if desired -- resolve the contradiction with design system first).
23. **Status bar breadcrumb style** (marked as future enhancement in the mockup).
24. **Help overlay screen**.

---

## 8. Specific Mockup Feedback

### launcher.txt

- **Good**: Three variants (A, B, C) are well-differentiated. Empty state and scroll indicators are covered. URL bar focused state is specified.
- **Issue**: Variant A line 29 shows badge numbers in `[cyan]`, but the design system Section 3.1 (line 270) says badges are in `SecondaryText` (ANSI 34, medium green). This is a visual contradiction. The cyan badges look better -- update the design system.
- **Issue**: The mockup shows `[1]` through `[6]` badges, but the interaction notes say "1-9: jump directly to bookmark by badge number." The Reading List tile uses `[c]`. The mockup shows all this correctly, but the design system does not mention the `[c]` badge for the reading list tile in the badge rendering spec (it does at line 272, so this is actually OK).
- **Issue**: The mockup Variant A shows the column divider `│` centered between columns, but the current code (`LauncherRenderer.cs` line 89) places it with `SecondaryText` coloring. The mockup annotates it as `[dim]` (ANSI 22-28), which is different from `SecondaryText` (ANSI 34). Minor, but a consistent color choice is needed.
- **Missing**: No mockup for the Reading List tile appearance (what does `[c] READING LIST` look like in the grid vs list layouts?). The empty state shows it but not the normal state alongside other bookmarks.

### linktree.txt

- **Good**: Shows search highlighting in context, section headers, collapsed sections, and group headers. Two variants with clear differentiation.
- **Issue**: The status bar on line 50-51 uses a 2-line format that is consistent with the status bar spec, but the cache progress bar uses `▰▰▰▰▰▰▰▱▱▱` characters without any color annotation for the filled vs empty segments. The statusbar.txt file (lines 58-62) clarifies filled is `[green]` and empty is `[med]`, but the linktree mockup should be self-consistent.
- **Issue**: Variant A shows 5-line cells (blank, title-row1, title-row2, author, separator) but the selected card on line 31-33 shows 3 content lines plus the separator. The accent bar `▌` appears on lines 1-3 of the selected card, but the separator line (line 34) does not have an accent bar. The design system Section 3.1 (line 266) says "showAccent" for lines nameLineIdx through accentEndIdx. Is the separator included or excluded from the highlight background? The mockup shows it with `[dim]┼` not highlighted, but that seems inconsistent with the launcher mockup where the separator row has a column divider cross.
- **Issue**: Variant B (single-column detail) shows URLs on line 2 of each item. The current `LinkTreeRenderer` does not extract or display per-link URLs. This variant requires data that the current `LinkTree` model may not carry at the item level.
- **Missing**: No mockup for LinkTree Variant C ("Magazine") from the design system. The design system specifies 3 variants but only 2 are mocked.

### reader.txt

- **Good**: Two variants shown clearly. Focus indicator `▎` positioned correctly. Search highlighting demonstrated. End-of-article state shown.
- **Issue**: The width adjustment keys are shown as `h/l` for narrowing/widening. But `h` and `l` in Helix/vim-like bindings typically mean "move left" and "move right." In the link tree, `h/l` collapse/expand sections or move between columns. The overloading of these keys may confuse users transitioning between views. The design system should document the per-view key binding table comprehensively.
- **Issue**: The `-- end --` marker at line 83 is not specified in the design system or the reader section. What color is it? The mockup says `[med]` but the design system's reader color mapping (lines 910-913) does not include an "end marker" role.
- **Minor**: Variant B shows `▎` flush against the left margin (line 108, `[dim]▎[/][green]After a nominal...`), but Variant A shows `▎` with a 4-char indent (line 37, `[dim]▎[/]  [green]After a nominal...`). The focus indicator positioning logic differs between variants -- this needs to be documented per-variant.

### collections.txt

- **Good**: Empty state, compact mode, scroll indicators, and two variants covered.
- **Issue**: Variant B (grid cards, lines 119-153) introduces rounded-border cards for each collection. This is a new component not used elsewhere in the collection rendering -- individual items use accent bars, not bordered cards. Implementing this requires new rendering logic in `CollectionRenderer` that does not currently exist.
- **Issue**: Variant B selected card (line 129) shows `[white][sel-bg]` inside the card borders. But the accent bar `▌` is absent. This is the only selection pattern in the entire design that does not use the accent bar. This should either be made consistent (add `▌` to the left edge of the selected card) or explicitly documented as an intentional exception.

### reading-list.txt

- **Good**: Comprehensive -- covers idle/selected/disabled/unconfigured CTA states, inline CTA for small terminals, empty collection, split view variant, and the musical note alternative.
- **Issue**: The split view Variant B (lines 153-187) places the podcast CTA in a left sidebar of ~18 chars width. The CTA text "GENERATE PODCAST" is stacked vertically. This variant is marked for 100+ width terminals. But at 100 columns, the right panel gets ~80 chars, which is comfortable. At 80 columns (the default mockup size), the right panel gets ~60 chars, which means titles truncate aggressively. The fallback from Variant B to Variant A on narrow terminals is mentioned in notes (line 187) but the threshold is not specified.
- **Issue**: The podcast button selected state (lines 74-78) says "Colors invert: white background, dark text." But in a terminal, setting the foreground to `SelectedItemBg` (ANSI 22, dark green) and background to `SelectedItemFg` (ANSI 15, white) would produce dark-green-on-white text. This is visually correct but very different from every other selection pattern in the app (which is white-on-dark-green). The inversion is intentional for the "focused button" feel, but it should be called out more explicitly in the design system's CTA button states (Section 3.9, line 571).
- **Missing**: No mockup showing what happens when the podcast CTA is navigated to via j/k. Is the CTA between the header and the first item? Can you j/k past it? The interaction notes (line 195-203) do not address this.

### statusbar.txt

- **Good**: Extremely thorough -- 11 states plus breadcrumb future enhancement, eighth-block progress bar detail, and key hint tiers for all 5 modes.
- **Issue**: State 6 "Paywall Detected" (line 95-96) shows `I:login to cache` with `I` in `[green]` (PrimaryText). But `I` is not listed in any of the key hint tiers for LinkView mode. The paywall hint appears to be a dynamic addition to line 2, not part of the standard hint tier system. This is fine architecturally (the current code does this in `FormatCacheIndicator`) but should be documented as a special case.
- **Issue**: The eighth-block progress bar detail (lines 182-199) shows two styles: `▰/▱` (current) and `█/▏` (proposed). The text says "Both styles work" but does not make a recommendation. The design system Section 3.4 specifies both, with the eighth-block bar for podcast generation and the segment bar for cache preloading. This is clear in the design system but the status bar mockup could be clearer about which bar goes where.
- **Issue**: State 11 (line 156-158) shows `cache 95%` as a warning when cache disk usage is >= 90%. But the design system says this should be in `PromptFg` (ANSI 46, green). A *warning* rendered in green does not communicate urgency. It should use `WarningFg` (ANSI 214, amber).

### toast.txt

- **Good**: Five toast types with clear color coding. Positioning and stacking documented. Timing per type. Implementation notes.
- **Issue**: Contradicts design system on stacking (see M7 above). The mockup says max 3 toasts stacked; the design system says only one at a time.
- **Issue**: The "expanded variant" celebration toast (lines 69-72) is 2 lines of content inside the box (4 lines total with borders). But the standard max height is documented as "3 lines tall (border + text + border)." The expanded variant violates the toast's own sizing rule.
- **Issue**: Toast positioning is "1 line from top, 2 chars from right edge" (line 128), but the "positioned in context" example (lines 34-36) shows the toast overlapping the header box borders. This means the toast must be rendered *after* the header, which complicates the overlay approach if the header is rendered by a different component. The rendering order needs to be specified.

### animations.txt

- **Good**: Extremely detailed frame-by-frame storyboards. Clear timing. Multiple animation types.
- **Issue**: Animation 1 (podcast celebration, line 20-78) shows 17 frames over 1500ms. Frame 6-16 is the typewriter reveal at ~80ms per character for "Podcast ready!" (14 characters). At 80ms per character, that is 1120ms for 14 characters. But the spec says this phase is 1000-1400ms. This math works only if the per-character delay is ~75ms (1050ms/14). The design system (Section 4.1 Phase 3) says "total 400ms spread across message length." If the message is "Podcast ready: 'Title' (47m)" (30+ chars), 400ms / 30 = 13ms per character, which is far too fast to see a typewriter effect. The timing math is inconsistent between the mockup storyboard and the design system spec. Pick one and fix the other.
- **Issue**: Animation 2 (decrypt, line 82-136) shows the body content also decrypting, not just the title. But the design system (Section 4.3, line 685) says "Only the title text characters; border characters are unaffected." The mockup shows body text also resolving (lines 96-98 show scrambled body, lines 106-108 show partially resolved body, lines 116-118 show fully resolved body). This is a scope creep from "title only" to "full page." Full-page decrypt would be much more expensive to render and much more visually disruptive. The design system's "title only" scope is the right call; update the mockup.
- **Issue**: Animation 5 (loading spinner, line 266-295) says "The current code uses `⠇` (static)." This is an implementation note, not a design spec. The static spinner is already in the codebase; animating it requires the timer infrastructure. Note the dependency.

### loading-error.txt

- **Good**: Five screen variants (loading, error, bot challenge, interactive refresh, manual login). Clear color coding. Breathing bar animation for waiting states.
- **Issue**: The "Content Extraction Error" (lines 131-143) offers `I:login` as first action. But `I` is the key for interactive refresh, not login. In the current codebase, `I` triggers the headed browser with manual intervention. The mockup says "login" but the action is really "interactive refresh that enables login." The label should match the action: `I:refresh` or `I:interactive`.
- **Issue**: The design principles (lines 262-275) say "Box width: ~40 chars (fits in 50-column minimum terminal)." But the boxes shown (e.g., line 82) are drawn at 40 inner chars + 2 border chars = 42. With centering padding, that is at least 42 characters. In a 50-column terminal with 2-char margins, usable width is 48. The box would be left-aligned rather than centered. This is fine functionally but the centering math should account for very narrow terminals.

### layout-switcher.txt

- **Good**: Simple, clear cycling mechanism. All 5 screens covered. Persistence via UserSettingsStore. Command-mode alternative.
- **Issue**: The toast boxes have inconsistent widths. The launcher toast (line 28) is 25 chars wide, the link tree toast (line 73) is 29 chars wide, the reader toast (line 84) is 27 chars wide. These should all be the same width (use the widest needed) to avoid the toast visually "jumping" when cycling through layouts.
- **Issue**: The "Inline Selector (Not Recommended)" alternative (lines 148-162) is correctly rejected, but it presents a pattern that could be useful for future multi-option selections (e.g., theme switching). Consider keeping the design for reference even if not implementing it now.
- **Issue**: No animation is specified for the layout switch itself. When you press Ctrl+L and the layout changes, does the content area update instantly (potential flicker as the full screen re-renders) or is there a transition? The mockup implies instant switching. Given the current re-render-on-every-input architecture, this will cause a full screen clear and redraw, which may produce visible flicker. The `ClearRemainingLines()` approach in `RenderHelpers` helps, but the transition from 2-column to 1-column layout will still cause a visible content shift.

---

*End of critique.*
