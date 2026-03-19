# TermReader Design System

Comprehensive specification for terminal UI implementation. All values are exact and
implementable. This document is the single source of truth; when it conflicts with
existing code, the design system wins.

Last updated: 2026-03-18

---

## Table of Contents

1. [Color Palette](#1-color-palette)
2. [Typography & Spacing](#2-typography--spacing)
3. [Component Catalog](#3-component-catalog)
4. [Animation Specifications](#4-animation-specifications)
5. [Screen-by-Screen Design Specifications](#5-screen-by-screen-design-specifications)
6. [AI Layout System](#6-ai-layout-system)
7. [Consistency Rules](#7-consistency-rules)

---

## 1. Color Palette

### 1.1 Phosphor Theme (Default)

Every color role maps to exactly one ANSI 256 code. No ad-hoc color literals in
renderer code; all colors come through `ThemePalette`.

#### Semantic Color Roles

| Role | ANSI 256 | Hex | Usage |
|------|----------|-----|-------|
| **PrimaryText** | 46 | `#00ff00` | Body text, item names, active key labels |
| **SecondaryText** | 34 | `#00af00` | Subtitles, domains, metadata, inactive hints |
| **TertiaryText** (DimFg) | 28 | `#008700` | Decorative elements, version strings, disabled text |
| **HeaderTitleFg** | 48 | `#00ff87` | Page titles, header text inside rounded boxes |
| **HeaderBorderFg** | 35 | `#00af5f` | Box-drawing borders, accent bars, card separators |
| **LinkContent** | 48 | `#00ff87` | Content/article link text (slightly brighter than body) |
| **LinkNavigation** | 40 | `#00d700` | Navigation links |
| **LinkExternal** | 40 | `#00d700` | External links |
| **LinkFooter** | 40 | `#00d700` | Footer links |
| **SelectedItemFg** | 15 | `#ffffff` | Text on selection highlight |
| **SelectedItemBg** | 22 | `#005f00` | Selection highlight background |
| **FocusIndicatorFg** | 22 | `#005f00` | Reader view left-margin indicator (▎) |
| **AccentFg** | 51 | `#00ffff` | Interactive hints, cache badges, key shortcuts, cyan accent |
| **CelebrationFg** | 198 | `#ff0087` | Podcast complete sparkle, one-time celebrations only |
| **SuccessFg** | 48 | `#00ff87` | Completion messages, checkmarks |
| **WarningFg** | 214 | `#ffaf00` | Budget warnings, active caching indicator |
| **ErrorFg** | 203 | `#ff5f5f` | Error messages, failed operations |
| **StatusBarSeparatorFg** | 35 | `#00af5f` | Horizontal rule above status bar |
| **StatusBarTextFg** | 46 | `#00ff00` | Mode label in status bar |
| **PromptFg** | 46 | `#00ff00` | Prompt text, active search query prefix |
| **PromptLabelFg** | 34 | `#00af00` | Prompt label text |
| **SearchHighlightFg** | 0 | `#000000` | Text inside search match highlight |
| **SearchHighlightBg** | 46 | `#00ff00` | Background of search match highlight |
| **ReadItemFg** | 34 | `#00af00` | Read/completed items (dimmer than unread) |
| **ToastBorderFg** | 51 | `#00ffff` | Toast notification border (accent cyan) |
| **ToastCelebrationBorderFg** | 198 | `#ff0087` | Celebration toast border (hot pink) |
| **ProgressFilledFg** | 46 | `#00ff00` | Filled portion of progress bars |
| **ProgressEmptyFg** | 34 | `#00af00` | Empty/remaining portion of progress bars |
| **ProgressActiveFg** | 214 | `#ffaf00` | Currently-processing segment of progress bar |

#### Background Color: Terminal Default

The app never sets a global background color. It relies on the user's terminal
background (expected: black or near-black for Phosphor). The only explicit backgrounds
are:
- `SelectedItemBg` (ANSI 22): Selection highlight
- `SearchHighlightBg` (ANSI 46): Search match
- Terminal default (black): Everything else

### 1.1.1 AccentFg Migration Note

> **Migration**: `AccentFg` changes from ANSI 43 (`#00afaf`, dark desaturated
> cyan) to ANSI 51 (`#00ffff`, bright cyan) in `BuiltInThemes.cs`. This is a
> single-line change per theme -- update the `ThemeColor` constructor argument
> for `AccentFg` in each theme definition. No data migration is needed. All
> existing usages of `AccentFg` (cache indicators, status badges) will
> automatically pick up the brighter value. Verify that hardcoded ANSI escape
> sequences in renderer code (e.g., `"\x1b[38;5;220m"` in `StatusBarRenderer`)
> are replaced with palette property references before making this change, to
> avoid visual inconsistency between palette-driven and hardcoded colors.

### 1.2 New ThemePalette Properties Required

The following properties must be added to `ThemePalette`:

```
ThemeColor? CelebrationFg       // Hot pink for celebrations
ThemeColor? SuccessFg            // Completion/success messages
ThemeColor? WarningFg            // Warnings, active progress
ThemeColor? ToastBorderFg        // Toast notification border
ThemeColor? ToastCelebrationBorderFg  // Celebration toast border
ThemeColor? ProgressFilledFg     // Filled progress segment
ThemeColor? ProgressEmptyFg      // Empty progress segment
ThemeColor? ProgressActiveFg     // Active/in-progress segment
```

Each should have a fallback getter method (like `GetAccentFg()`) that returns a
sensible default if null:

| Property | Fallback |
|----------|----------|
| CelebrationFg | AccentFg |
| SuccessFg | HeaderTitleFg |
| WarningFg | ErrorFg |
| ToastBorderFg | AccentFg |
| ToastCelebrationBorderFg | CelebrationFg |
| ProgressFilledFg | PromptFg |
| ProgressEmptyFg | SecondaryText |
| ProgressActiveFg | WarningFg |

### 1.2.1 Complete Theme Color Specifications for New Properties

All 4 themes must have **explicit values** for the new properties. The table
below provides the exact ANSI 256 code for each property in each theme. These
are not fallbacks -- they are the canonical values to set in `BuiltInThemes.cs`.

| Property | Phosphor | Amber | Dracula | Light |
|---|---|---|---|---|
| **CelebrationFg** | 198 (hot pink) | 208 (orange) | 212 (pink) | 161 (dark pink) |
| **ToastBorderFg** | 51 (cyan) | 214 (gold) | 98 (purple) | 32 (blue) |
| **SuccessFg** | 48 (bright green) | 220 (amber) | 114 (green) | 28 (dark green) |
| **WarningFg** | 214 (gold) | 203 (red) | 220 (yellow) | 208 (orange) |
| **ProgressFilledFg** | 46 (green) | 220 (amber) | 80 (cyan) | 25 (blue) |
| **ProgressEmptyFg** | 22 (dark green) | 94 (dark amber) | 237 (dark gray) | 249 (light gray) |
| **ProgressActiveFg** | 51 (cyan) | 208 (orange) | 212 (pink) | 32 (light blue) |

Notes:
- Dracula `CelebrationFg` (212) intentionally matches Dracula's pink accent.
  Although the critique noted overlap with `HeaderTitleFg`, the pink-on-dark
  palette is the Dracula identity; using a different color would feel off-brand.
- Light theme uses muted, saturated variants that read well on white/light backgrounds.
- `ToastCelebrationBorderFg` is not listed -- it always matches `CelebrationFg`
  for the same theme (already specified in the fallback table above).

### 1.3 Adapting for Other Themes

Each theme maps the semantic roles to its own palette. Rules for creating a new
theme adaptation:

| Semantic Concept | Phosphor | Amber | Dracula | Light |
|------------------|----------|-------|---------|-------|
| Primary hue | Green | Amber/Gold | Cool Gray/White | Dark Gray |
| Accent hue | Cyan | Warm Orange | Cyan | Blue |
| Celebration hue | Hot Pink | Bright Red | Pink | Purple |
| Warning hue | Amber | Bright Amber | Yellow | Orange |
| Error hue | Red | Red | Red | Red |
| Selection bg | Dark primary | Bright primary | Dark gray | Primary blue |
| Border color | Mid primary | Deep primary | Purple | Mid gray |

#### Amber Theme Celebration/Accent Values

```
AccentFg:               ANSI 208 (#ff8700)   warm orange
CelebrationFg:          ANSI 196 (#ff0000)   bright red flash
WarningFg:              ANSI 220 (#ffd700)   bright amber
ToastBorderFg:          ANSI 208 (#ff8700)   warm orange
```

#### Dracula Theme Celebration/Accent Values

```
AccentFg:               ANSI 80  (#5fd7d7)   cyan
CelebrationFg:          ANSI 212 (#ff87d7)   pink (Dracula pink)
WarningFg:              ANSI 220 (#ffd700)   yellow
ToastBorderFg:          ANSI 80  (#5fd7d7)   cyan
```

#### Light Theme Celebration/Accent Values

```
AccentFg:               ANSI 32  (#0087d7)   blue
CelebrationFg:          ANSI 128 (#af00d7)   purple
WarningFg:              ANSI 166 (#d75f00)   orange
ToastBorderFg:          ANSI 32  (#0087d7)   blue
```

### 1.4 Luminance Hierarchy

Every screen must maintain this brightness ordering (brightest to dimmest):

1. **Selected item text** (ANSI 15, white) -- always the brightest on screen
2. **Active/focused element** (PrimaryText, ANSI 46) -- brightest non-selected
3. **Headlines/titles** (HeaderTitleFg, ANSI 48) -- slightly brighter green
4. **Accent elements** (AccentFg, ANSI 51) -- bright but different hue
5. **Body text** (PrimaryText, ANSI 46) -- standard reading brightness
6. **Secondary info** (SecondaryText, ANSI 34) -- noticeably dimmer
7. **Borders/separators** (HeaderBorderFg, ANSI 35) -- structural, not attention-grabbing
8. **Decoration/disabled** (DimFg, ANSI 28) -- barely visible, background texture
9. **Selection background** (SelectedItemBg, ANSI 22) -- dark, acts as backdrop

---

## 2. Typography & Spacing

### 2.1 Line Spacing Rules

| Context | Rule |
|---------|------|
| Between header box and content | 0 blank lines (header bottom border acts as separator) |
| Above a top-level group header (link tree) | 1 blank line (built into the group header rendering) |
| Between list items (short terminal, <30 lines) | 0 blank lines |
| Between list items (tall terminal, >=30 lines) | 1 separator line: `   ╶──╴` |
| Between article paragraphs (reader view) | 1 blank line |
| Above status bar | 0 blank lines (horizontal rule acts as separator) |
| Between sections in launcher | URL bar has 1 blank line above and below |
| Inside rounded boxes | 0 blank lines; text on middle line with 1-char horizontal padding |
| Before podcast CTA button | 1 blank line |
| After podcast CTA button | 1 blank line |
| Toast notification gap from edge | 1 line from top of screen, 2 chars from right edge |

### 2.2 Padding Rules

| Element | Horizontal Padding | Vertical Padding |
|---------|-------------------|------------------|
| Rounded box (header) | 1 char each side (`│ text │`) | 0 (single text line) |
| Rounded box with subtitle | 1 char each side | 0 (title line + subtitle line) |
| Launcher card cell | 6 chars left indent, 1 char right | 1 line top, 1 line bottom (5-line mode) |
| Launcher card cell (compact) | 6 chars left indent, 1 char right | 0 (3-line mode) |
| Link tree card | 1 char left (accent bar or space), 0 right | 1 line top, 1 line bottom (5-line mode) |
| Collection list item | 3 chars left indent | 0 |
| Collection item (2-line) | 3 chars left indent, 5 chars domain sub-indent | 0 |
| Reader view text | 2 chars left indent | 0 |
| Status bar content | 1 char left indent | 0 |
| URL bar | Centered, 75% of width (min 30, max 70) | 1 blank line above, 1 below |
| Podcast CTA button | Centered, clamped width (min 40, max 72) | Depends on tier |
| Toast notification | 1 char padding inside box | 0 |

### 2.3 Content Width Rules

| Context | Width Rule |
|---------|-----------|
| Overall usable width | `terminal_width - 2` |
| Reader view `MaxContentWidth` | User-adjustable via `h`/`l`, default 80, min 40, max terminal_width |
| Reader view text indentation | 2 chars from left edge of content area |
| Link tree card title | `cell_width - 2` (1 for accent bar, 1 for space) |
| Launcher card title | `cell_width - 6 - 1` (6 indent, 1 right pad) |
| Status bar | `terminal_width - 1` |
| URL bar inner width | `bar_width - 4` (2 for borders, 2 for padding) |

### 2.4 Column Thresholds

| View | Column Threshold | Below Threshold | At/Above Threshold |
|------|-----------------|-----------------|---------------------|
| Launcher | 40 chars usable width | 1 column | 2 columns |
| Link Tree | 50 chars usable width | 1 column | 2 columns |
| Collections | Always 1 column | -- | -- |
| Reader | Always 1 column | -- | -- |

### 2.5 Cell Height Modes

| View | Compact Trigger | Compact Height | Standard Height |
|------|----------------|----------------|-----------------|
| Launcher | available_height < 15 | 3 lines | 5 lines |
| Link Tree | available_height < 15 | 3 lines | 5 lines |
| Link Tree Group Header | available_height < 10 (card_height=1) | 1 line | 2-3 lines |

### 2.6 Indentation Levels

| Level | Chars | Usage |
|-------|-------|-------|
| 0 | 0 | Borders, full-width elements |
| 1 | 1 | Accent bars, list bullet position |
| 2 | 2 | Reader text, general content inset |
| 3 | 3 | Collection list items, selection offset |
| 4 | 5 | Collection item domain line |
| 5 | 6 | Launcher card content (badge + pad) |

---

## 3. Component Catalog

### 3.1 Cards (Launcher Bookmarks, Link Tree Items)

#### Structure (5-line standard mode)

```
Line 0: [accent_bar_or_space] [                          padding                          ]
Line 1: [accent_bar_or_space] [badge] [pad] [TITLE TEXT UPPERCASE (launcher) / Title (links)]
Line 2: [accent_bar_or_space] [      pad   ] [subtitle / domain / wrapped title line 2    ]
Line 3: [accent_bar_or_space] [      pad   ] [metadata: author + date                    ]
Line 4: [accent_bar_or_space] [────────────────────── separator rule ──────────────────────]
```

#### Structure (3-line compact mode)

```
Line 0: [accent_bar_or_space] [TITLE TEXT or title text]
Line 1: [accent_bar_or_space] [subtitle / domain / metadata]
Line 2: [accent_bar_or_space] [────── separator rule ──────]
```

#### States

| State | Accent Bar | Background | Text Color | Border Color |
|-------|-----------|------------|------------|--------------|
| Normal | none (space) | terminal default | PrimaryText (title), SecondaryText+Dim (domain) | SecondaryText+Dim (separator) |
| Selected | `▌` in HeaderBorderFg (ANSI 35) | SelectedItemBg (ANSI 22) | SelectedItemFg+Bold (ANSI 15) | HeaderBorderFg (ANSI 35) |
| Normal (launcher) | none (space) | terminal default | PrimaryText+Bold (name), SecondaryText+Dim (domain) | none |
| Selected (launcher) | `▌` in HeaderBorderFg (ANSI 35) | SelectedItemBg (ANSI 22) | SelectedItemFg+Bold (ANSI 15), SecondaryText (domain) | none |

#### Badge Rendering (Launcher Only)

- Bookmarks 1-9: `[1]`..`[9]` in SecondaryText
- Bookmark 10+: no badge
- Reading List tile: `[c]` in SecondaryText
- Badge is placed at indent position, before the title

#### Cache Indicator (Link Tree Only)

Not currently rendered on individual link tree cards. Cache status is shown
per-item in Collection Items view only (` . cached` suffix in SecondaryText,
with `cached` text in PromptFg/AccentFg when not selected).

### 3.2 Rounded Boxes

#### When to Use

- Page/section headers (top of Launcher, Link Tree, Collections, Collection Items)
- URL bar on Launcher screen
- Toast notifications
- Never for individual list items or cards (cards use accent bars and separator rules)

#### Box Drawing Characters

```
Top-left:     ╭  (U+256D)
Top-right:    ╮  (U+256E)
Bottom-left:  ╰  (U+2570)
Bottom-right: ╯  (U+256F)
Horizontal:   ─  (U+2500)
Vertical:     │  (U+2502)
```

#### Standard Rounded Box

```
╭──────────────────────────────────────╮
│ Title Text                           │
╰──────────────────────────────────────╯
```

- Border color: HeaderBorderFg
- Title color: HeaderTitleFg
- Inner padding: 1 char each side

#### Rounded Box with Subtitle

```
╭─ Page Title ──────────────────────────╮
│ author . date . domain . 47 links     │
╰───────────────────────────────────────╯
```

- Top border embeds title: `╭─ Title ─...─╮`
- Subtitle on middle line in SecondaryText
- Title text in the top border is rendered in HeaderBorderFg (the border characters)
  with the title text itself unstyled (inherits border color)

### 3.3 Selection Bar

The selection indicator is a left-edge accent bar combined with a full-width
background highlight.

#### Characters

| Character | Unicode | Usage |
|-----------|---------|-------|
| `▌` | U+258C | Left accent bar (selected items in link tree, collection list) |
| `▌` | U+258C | Launcher card accent bar (3 lines tall, lines 1-3 in 5-line mode) |

#### Color Rules

| Context | Accent Bar Color | Highlight Background | Highlight Foreground |
|---------|-----------------|---------------------|---------------------|
| Link Tree (selected) | HeaderBorderFg (ANSI 35) | SelectedItemBg (ANSI 22) | SelectedItemFg (ANSI 15) |
| Collection List (selected) | SelectedItemFg (ANSI 15) | SelectedItemBg (ANSI 22) | SelectedItemFg (ANSI 15) |
| Collection Items (selected) | SelectedItemFg (ANSI 15) | SelectedItemBg (ANSI 22) | SelectedItemFg (ANSI 15) |
| Launcher (selected) | HeaderBorderFg (ANSI 35) | SelectedItemBg (ANSI 22) | SelectedItemFg (ANSI 15) |

#### Focus vs Unfocused

Currently TermReader is single-pane (no split panels), so there is no
unfocused-panel state. If panels are added in the future:
- Focused panel: accent bar in AccentFg (ANSI 51), bright border
- Unfocused panel: accent bar in SecondaryText (ANSI 34), dim border

### 3.4 Progress Bars

#### Eighth-Block Smooth Progress Bar (New, for Podcast Generation)

Characters (left to right, increasing fill):

```
▏ (U+258F) 1/8 block
▎ (U+258E) 2/8 block
▍ (U+258D) 3/8 block
▌ (U+258C) 4/8 block
▋ (U+258B) 5/8 block
▊ (U+258A) 6/8 block
▉ (U+2589) 7/8 block
█ (U+2588) 8/8 block (full)
```

#### Layout

```
▕███████████▍          ▏ 62%  Chunk 5/8
```

- Left cap: `▕` (U+2595, right 1/8 block)
- Right cap: `▏` (U+258F, left 1/8 block)
- Filled: `█` in ProgressFilledFg (ANSI 46)
- Partial: appropriate eighth-block char in ProgressFilledFg
- Empty: space character (no fill)
- Active segment: the partial block character in ProgressActiveFg (ANSI 214)
- Bar width: 20 characters (160 sub-positions at 1/8 resolution)
- Percentage and chunk info follow the bar, in SecondaryText

#### Existing Segment-Style Progress Bar (Cache Preloading)

Characters:
```
▰ (U+25B0) filled segment
▱ (U+25B1) empty segment
```

- Bar width: 10 segments
- Filled: PromptFg (ANSI 46)
- Active (in-progress): ANSI 220 (amber/yellow)
- Empty: SecondaryText (ANSI 34)
- Followed by `cached_count/total` in SecondaryText

### 3.5 Toast Notifications

#### Position

- Top-right corner of terminal
- 1 line from top, 2 characters from right edge
- Width: content width + 4 (2 chars border, 2 chars padding)
- Max width: 40 characters

#### Structure

```
╭───────────────────────╮
│ ✦ Cache warmed (12)   │
╰───────────────────────╯
```

#### Color Rules

| Toast Type | Border Color | Icon | Text Color |
|-----------|-------------|------|------------|
| Info (cache complete, status) | ToastBorderFg (ANSI 51) | `✦` | PrimaryText |
| Celebration (podcast ready) | ToastCelebrationBorderFg (ANSI 198) | `♫` | PrimaryText, then AccentFg |
| Warning | WarningFg (ANSI 214) | `⚡` | PrimaryText |
| Error | ErrorFg (ANSI 203) | `✗` | ErrorFg |

#### Duration and Behavior

> **ARCHITECTURAL CONSTRAINT**: The app uses a synchronous input loop with no
> background thread or timer. Toasts cannot auto-dismiss on a timer. Instead,
> toasts dismiss on the **next user keypress** or the **next screen redraw**
> (whichever comes first). Only **one toast visible at a time** -- a new toast
> replaces the current one. For critical notifications that must not be missed,
> use **sticky mode**: the toast persists across redraws until the user
> explicitly presses `Esc` to dismiss it.

- Dismissed on next keypress or next screen redraw (NOT on a timer)
- Only one toast visible at a time; new toast replaces existing
- Does not steal focus
- Does not block input (the keypress that dismisses the toast is also processed normally)
- Rendered as overlay on the next render pass; cleared on the subsequent render pass
- No animation on appear (instant); no animation on dismiss (instant clear)
- **Sticky mode** (for critical notifications): toast persists until user presses `Esc`.
  Use for: error toasts requiring acknowledgment, podcast completion.
  Indicated by a small `[Esc]` hint inside the toast border.

### 3.6 Status Bar

#### Structure (3 Lines Total)

```
Line 0: ──────────────────────────────────── (separator, full width)
Line 1: [←] ModeName  key:action key:action key:action
Line 2:  domain.com                     ▰▰▰▰▱▱▱▱ 4/8 cached
```

#### Separator

- Character: `─` (U+2500)
- Color: StatusBarSeparatorFg (ANSI 35)
- Width: `terminal_width - 1`

#### Line 1: Mode Label + Key Hints

- Back arrow (if can go back): `[←]` in SecondaryText, followed by space
- Mode label: `ModeName` in StatusBarTextFg (ANSI 46)
- Key hints: right-aligned, adaptive (tries largest tier first, shrinks until fits)
- Hint format: `key:action` where `key` is AccentFg (ANSI 51), `:action` is SecondaryText (ANSI 34)
- Hints separated by single space

#### Line 2: Contextual Vitals

- Left side: domain name (SecondaryText) OR reader position (`L42/380 W72 85%` in SecondaryText)
- Right side: segments separated by `│` (SecondaryText)
  - Search query: `/query` in PromptFg + `(n/N)` in SecondaryText
  - AI layout badge: `AI layout` in SecondaryText
  - Cache progress bar or status badge
  - Cache usage warning (if >= 90%): `cache 95%` in PromptFg
  - Status message: transient text in PromptFg

#### Mode Labels

| ViewMode | Label |
|----------|-------|
| Hierarchical | `LinkView` |
| Readable | `ReaderView` |
| CollectionList | `Collections` |
| CollectionItems | `Reading List` |
| Launcher | `Launcher` |

### 3.7 Indicators

#### Read/Unread Status

| State | Character | Unicode | Color |
|-------|-----------|---------|-------|
| Unread | `●` | U+25CF | LinkContent (ANSI 48) |
| Read | `○` | U+25CB | ReadItemFg (ANSI 34) |

#### Collapse/Expand

| State | Character | Unicode | Context |
|-------|-----------|---------|---------|
| Expanded | `▼` | U+25BC | Group header, links with children |
| Collapsed | `▶` | U+25B6 | Group header, links with children |

#### Other Indicators

| Indicator | Character | Unicode | Color | Usage |
|-----------|-----------|---------|-------|-------|
| Default collection | `★` | U+2605 | PromptFg (ANSI 46) | Marks default save collection |
| Selection arrow | `→` | U+2192 | inherited | Legacy single-node link tree selection |
| Checkmark | `✔` | U+2714 | SuccessFg | Completion confirmation |
| Scroll up | `▲` | U+25B2 | SecondaryText+Dim | More items above |
| Scroll down | `▼` | U+25BC | SecondaryText+Dim | More items below |
| Cache suffix | ` · cached` | | PromptFg for ` · cached` (normal), plain text (selected) | Per-item cache indicator |
| Loading spinner | `⠇` | U+2847 | PromptFg | Loading screen |
| Podcast play icon | `▶▶` | U+25B6 x2 | inherited | Podcast CTA button |

### 3.8 Key Hints

> **Universal Rule**: Key hints **ALWAYS** use `AccentFg` (ANSI 51 in Phosphor)
> for the key character and `SecondaryText` (ANSI 34) for the description text.
> This applies to **all screens** -- status bar hints, launcher footer hints,
> error screen action hints, toast hints, and any future hint surfaces.
>
> Format: `{AccentFg}key{Reset} {SecondaryText}action{Reset}`
>
> Example (Phosphor): `\x1b[38;5;51mEnter\x1b[0m\x1b[38;5;34m:open\x1b[0m`

#### Format Styles

**Status bar style** (compact):
```
key:action
```
- `key` in AccentFg (ANSI 51)
- `:action` in SecondaryText (ANSI 34)
- Separated by single space
- Adaptive: tries full tier, medium tier, minimal tier based on available width

**Launcher footer style** (bracketed):
```
[Enter] open  [o] go to url  [:] config  [?] help
```
- Brackets `[ ]` in SecondaryText (ANSI 34)
- Key letter inside brackets in AccentFg (ANSI 51)
- Action text in SecondaryText+Dim (ANSI 34 + dim attribute)
- Separated by 2 spaces

#### Visibility Rules

- Status bar hints: always visible (except in loading/error/challenge screens)
- Launcher footer hints: always visible on launcher screen
- Inline hints on focused items: NOT currently implemented (future enhancement)
- Help overlay (`?`): separate screen, not part of the design system yet

### 3.9 CTA Button (Podcast)

Three rendering tiers based on terminal size:

#### Full Slab (5 lines) -- terminal height >= 24 AND width >= 52

```
                                                    (blank line)
        ██████████████████████████████████████████  (top fill, SelectedItemBg)
         ▶▶  Generate Podcast              [p]     (content, on SelectedItemBg)
        ██████████████████████████████████████████  (bottom fill, SelectedItemBg)
                                                    (blank line)
```

#### Compact Slab (3 lines) -- terminal height >= 20 AND width >= 37

```
                                                    (blank line)
         ▶▶  Generate Podcast              [p]     (content, on SelectedItemBg)
                                                    (blank line)
```

#### Inline (1 line) -- all smaller terminals

```
                   ▶▶  Generate Podcast  [p]       (no background fill)
```

#### Button States

| State | Background | Text Color | Hint Text | Bold |
|-------|-----------|------------|-----------|------|
| Idle | SelectedItemBg | SelectedItemFg | `[p]` in PrimaryText | Yes |
| Pressed | SelectedItemBg | SelectedItemFg | `[p]` in SelectedItemFg | No |
| Selected (focused) | SelectedItemFg (inverted) | SelectedItemBg (inverted) | `[Enter]` in SelectedItemBg | No |
| Disabled | SelectedItemBg | SecondaryText | `[p]` in SecondaryText | No (dimmed) |
| Unconfigured | SelectedItemBg | SecondaryText | `Setup required` in SecondaryText | No (dimmed) |

#### Button Width

- Computed: `clamp(terminal_width - 12, 40, 72)`
- Centered horizontally in the usable width

### 3.10 Scroll Indicators

```
                    ▲ 3 more above
                    ▼ 5 more below
```

- Centered in usable width
- Color: SecondaryText + Dim attribute
- Character: `▲` (U+25B2) for above, `▼` (U+25BC) for below
- Format: `{arrow} {count} more {above|below}`

### 3.11 Item Separators

Between list items in Collections and Collection Items views (only when
terminal height >= 30):

```
   ╶──╴
```

- Characters: `╶` (U+2576) + `──` + `╴` (U+2574)
- Color: SecondaryText
- Left indent: 3 chars (collection list) or 5 chars (collection items)

### 3.12 Column Dividers

Between left and right columns in 2-column grid layouts:

```
│    normal row
┼    separator/rule row
```

- `│` (U+2502) for content lines
- `┼` (U+253C) for bottom separator lines
- Color: SecondaryText

---

## 4. Animation Specifications

> **ARCHITECTURAL CONSTRAINT**: Animations run **synchronously** during the
> render cycle, blocking input briefly. The app has no background render thread
> or frame timer. After a triggering event (e.g., podcast complete), the next
> render call executes animation frames via `Thread.Sleep()` between
> `Console.Write()` calls with cursor repositioning. Total duration **MUST**
> stay under **2 seconds**. Implementation pattern:
>
> ```csharp
> // Inside the render method, after detecting the trigger condition:
> for (int frame = 0; frame < frameCount; frame++)
> {
>     Console.SetCursorPosition(col, row);
>     Console.Write(frameContent[frame]);
>     Thread.Sleep(frameDelayMs); // e.g., 50-100ms
> }
> // Then continue with normal rendering
> ```
>
> This means: (1) user input is blocked for the animation's duration,
> (2) only targeted cursor regions are updated (no full-screen redraws per frame),
> (3) animations that exceed 2 seconds will feel unresponsive.

### 4.1 Podcast Complete Celebration

**Trigger**: Podcast generation finishes successfully.

**Total Duration**: 1600ms

**Scope**: Status bar area (lines H-3 through H-1 where H = terminal height) plus
a 3-line toast in the top-right corner.

#### Phase 1: Flash (0-200ms)

- Frame rate: single frame at 0ms, restore at 200ms
- Effect: Status bar separator line briefly changes to AccentFg (ANSI 51)
- Characters: same `─` characters, color change only

#### Phase 2: Sparkle (200-1000ms)

- Frame rate: 80ms per frame (10 frames)
- Effect: 6 random positions in the toast text area cycle through sparkle characters
- Character sequence per position: `·` -> `+` -> `*` -> `✦` -> `✧` -> `✦` -> `*` -> `+` -> `·` -> original
- Color sequence per position: SecondaryText(34) -> PrimaryText(46) -> AccentFg(51) -> CelebrationFg(198) -> White(15) -> CelebrationFg(198) -> AccentFg(51) -> PrimaryText(46) -> SecondaryText(34)
- Positions are random within the toast content line, selected once at animation start

#### Phase 3: Typewriter Reveal (1000-1400ms)

- Frame rate: per-character, total 400ms spread across message length
- Effect: Toast message text types out character by character
- Message format: `♫ Podcast ready: "{title}" ({duration}m)`
- Each character appears in CelebrationFg (198), then settles to PrimaryText (46) after 2 frames
- Cursor character: `█` (U+2588) in AccentFg (51), shown at end of revealed text

#### Phase 4: Settle (1400-1600ms)

- Frame rate: 100ms per frame (2 frames)
- Effect: Sparkle characters return to spaces, cursor disappears
- Toast remains visible after animation completes (sticky mode: dismissed on Esc)
- Toast text settles to SecondaryText color

### 4.2 Cache Item Complete (Subtle Pulse)

**Trigger**: A single item finishes caching in the background.

**Total Duration**: 300ms

**Frame Rate**: 100ms per frame (3 frames)

**Scope**: Status bar cache progress indicator only (right side of line 2).

**Effect**:
- Frame 1: The progress count text (`5/12 cached`) changes to AccentFg (ANSI 51)
- Frame 2: The progress count text changes to PrimaryText (ANSI 46)
- Frame 3: The progress count text returns to SecondaryText (ANSI 34)

**Characters**: No character changes. Color change only.

### 4.3 Page Load Decrypt Effect (Title Reveal)

**Trigger**: New page finishes loading and title is about to be rendered.

**Total Duration**: 400ms

**Frame Rate**: 50ms per frame (8 frames)

**Scope**: Title text inside the header rounded box (line 1 of the 3-line header).
Only the title text characters; border characters are unaffected.

**Characters used**: `░` (U+2591), `▒` (U+2592), `▓` (U+2593), random ASCII
uppercase letters.

**Effect**: Each character position in the title goes through this sequence:
- Frames 1-2: Random character from `░▒▓` in DimFg (ANSI 28)
- Frames 3-4: Random uppercase letter in SecondaryText (ANSI 34)
- Frames 5-6: Random uppercase letter in PrimaryText (ANSI 46)
- Frames 7-8: Correct character in HeaderTitleFg (ANSI 48)

Characters resolve left-to-right with a 1-character-per-frame wave offset.
Position `i` starts resolving at frame `max(0, frame - i/3)`, creating a
sweep effect.

**When to skip**: If the page was loaded from cache (`IsFromCache == true`),
skip the decrypt effect entirely -- cached pages should appear instantly to
reinforce the "speed" benefit of caching.

### 4.4 Color Wave on Status Bar (Cache Warm Complete)

**Trigger**: Background preloading finishes for all cacheable links on a page.

**Total Duration**: 600ms

**Frame Rate**: 60ms per frame (10 frames)

**Scope**: Status bar separator line (line H-3, the `─────` line).

**Effect**: A 5-character-wide "wave" of bright color sweeps left-to-right
across the separator line.

**Color sequence within wave (center to edge)**:
- Center character: AccentFg (ANSI 51)
- +/-1 from center: PrimaryText (ANSI 46)
- +/-2 from center: HeaderBorderFg (ANSI 35)
- Outside wave: StatusBarSeparatorFg (ANSI 35, normal)

**Wave speed**: ~8 characters per frame. The wave traverses the full
terminal width in ~10 frames.

**Characters**: No character changes. Same `─` throughout. Color only.

### 4.5 Podcast Generation Progress Bar (Smooth Eighth-Block)

**Trigger**: Podcast generation is in progress. Updated on each chunk completion.

**Total Duration**: Continuous (not an animation per se; updates per chunk)

**Frame Rate**: On-demand (re-render when progress changes)

**Scope**: Rendered in the main content area during podcast generation, centered.

**Layout**:
```
Generating podcast...

  ▕████████████▍          ▏ 62%  Chunk 5/8

  Current: "Chapter Title Here"
```

**Bar construction**:
- Total bar width: 30 characters (240 eighth-positions)
- Fill level: `floor(progress * 240)` eighth-positions
- Full blocks: `fill_level / 8` full `█` characters in ProgressFilledFg (ANSI 46)
- Partial block: `fill_level % 8` selects from `▏▎▍▌▋▊▉█` in ProgressActiveFg (ANSI 214)
- Empty positions: space characters
- Left cap `▕` and right cap `▏` in SecondaryText (ANSI 34)
- Percentage in PrimaryText (ANSI 46)
- Chunk info in SecondaryText (ANSI 34)
- Current chapter title in SecondaryText+Dim

---

## 5. Screen-by-Screen Design Specifications

### 5.1 Launcher Screen

#### Visual Hierarchy (Brightest to Dimmest)

1. Selected bookmark name (white on dark green, bold)
2. Bookmark names (PrimaryText, bold)
3. URL bar placeholder text (when focused: white on dark green)
4. "TermReader" header title (HeaderTitleFg)
5. Badges `[1]`, `[c]` (SecondaryText)
6. Domain subtitles (SecondaryText+Dim)
7. URL bar placeholder (when unfocused: SecondaryText+Dim)
8. Footer key hints (mixed PrimaryText/SecondaryText)
9. Version string (SecondaryText+Dim)
10. Borders (HeaderBorderFg)

#### Default Focus Position

- First bookmark (index 0), NOT the URL bar
- URL bar is index -1 (selectable but not default)

#### Key Hint Visibility

- Footer hints always visible (not in status bar -- launcher has its own footer)
- Format: `[Enter] open  [o] go to url  [:] config  [?] help`
- Adaptive: drops down to `[Enter] open  [o] go to url  [?] help` then `[?] help`

#### Layout

- Header: 3-line rounded box ("TermReader", "{n} bookmarks")
- URL bar: centered rounded box, ~75% width
- Grid: 2 columns (or 1 if narrow), 5-line cells (or 3-line if short)
- Footer: horizontal rule + hint line

#### Color Mapping

| Element | Color |
|---------|-------|
| Header border | HeaderBorderFg |
| Header title "TermReader" | HeaderTitleFg |
| Header subtitle "{n} bookmarks" | SecondaryText |
| URL bar border (unfocused) | HeaderBorderFg |
| URL bar border (focused) | SelectedItemFg |
| URL bar text (unfocused) | SecondaryText+Dim |
| URL bar text (focused) | SelectedItemFg on SelectedItemBg |
| Bookmark badge `[n]` | SecondaryText |
| Bookmark name (normal) | PrimaryText+Bold |
| Bookmark name (selected) | SelectedItemFg+Bold on SelectedItemBg |
| Bookmark domain (normal) | SecondaryText+Dim |
| Bookmark domain (selected) | SecondaryText on SelectedItemBg |
| Column divider | SecondaryText `│` |
| Footer rule | HeaderBorderFg |
| Footer key brackets | SecondaryText |
| Footer key letter | AccentFg (ANSI 51) |
| Footer action text | SecondaryText+Dim |
| Version string | SecondaryText+Dim |
| Scroll indicator | SecondaryText+Dim |

#### Empty State

When no bookmarks exist:
- Center "Your bookmarks await" in PrimaryText+Bold
- Below: "Press [a] to add your first site" with bracket formatting
- Below: `[c]  READING LIST` card

### 5.2 Link Tree Screen

#### Visual Hierarchy

1. Selected article title (white+bold on dark green, with accent bar)
2. Content link titles (LinkContent, ANSI 48)
3. Page title in header (HeaderTitleFg)
4. Group header names (PrimaryText+Bold when expanded, SecondaryText when collapsed)
5. Navigation/external/footer links (LinkNavigation, ANSI 40)
6. Metadata (author, date) under titles (SecondaryText+Dim)
7. Header subtitle (SecondaryText)
8. Card separator rules (SecondaryText+Dim)
9. Borders (HeaderBorderFg)
10. Status bar

#### Default Focus Position

- First visible content link (first non-group-header node)

#### Key Hint Visibility

- Status bar line 1: adaptive key hints
- Full: `Enter:open s:save A:save-all R:refresh v:reader ?:help`
- Compact: `?:help`

#### Layout

- Header: 3-line rounded box with title and subtitle
- Content: 2-column grid of cards (or 1-column if narrow)
- Group headers: full-width, not paired in columns
- Status bar: 3-line footer

#### Color Mapping

| Element | Color |
|---------|-------|
| Card title (Content type) | LinkContent (ANSI 48) |
| Card title (Navigation) | LinkNavigation (ANSI 40) |
| Card title (External) | LinkExternal (ANSI 40) |
| Card title (Footer) | LinkFooter (ANSI 40) |
| Card title (selected) | SelectedItemFg+Bold on SelectedItemBg |
| Accent bar (selected) | HeaderBorderFg (ANSI 35) `▌` |
| Card metadata | SecondaryText+Dim |
| Card separator rule (normal) | SecondaryText+Dim `─` |
| Card separator rule (selected) | HeaderBorderFg on SelectedItemBg |
| Group header (expanded) | PrimaryText+Bold |
| Group header (collapsed) | SecondaryText |
| Group header (selected) | SelectedItemFg on SelectedItemBg with accent bar |
| Collapse indicator `▼`/`▶` | Same as group header text |
| Sub-section header border | HeaderBorderFg `╭─ ... ─╮` |
| Sub-section header (selected) | SelectedItemFg on SelectedItemBg |

### 5.3 Reader View Screen

#### Visual Hierarchy

1. Focus indicator line (▎ in FocusIndicatorFg, marks current read position)
2. Search highlights (black on green, maximum contrast)
3. Article body text (inherits PrimaryText from line cache)
4. Status bar reader position (L42/380 W72 85%)

#### Default Focus Position

- Scroll offset 0 (top of article)
- Focus indicator at viewport_height / 3 (roughly upper third)

#### Key Hint Visibility

- Status bar line 1: adaptive key hints
- Full: `s:save o:browser h/l:width R:refresh v:links b:back ?:help`

#### Layout

- No header box (maximizes reading space)
- Content: full terminal width, text wrapped to MaxContentWidth
- Content indented 2 characters from left edge
- Focus indicator `▎` replaces the first space character on the focus line
- Status bar: 3 lines

#### Color Mapping

| Element | Color |
|---------|-------|
| Body text | PrimaryText (via line cache, pre-formatted) |
| Focus indicator `▎` | FocusIndicatorFg (ANSI 22) |
| Search match | SearchHighlightFg (ANSI 0) on SearchHighlightBg (ANSI 46) |
| Status bar position info | SecondaryText |

### 5.4 Collection List Screen

#### Visual Hierarchy

1. Selected collection name (white on dark green, with accent bar)
2. Collection names (PrimaryText)
3. "Collections" header title (HeaderTitleFg)
4. Item counts `(12)` (SecondaryText)
5. Default collection star `★` (PromptFg)
6. "No collections yet" empty state (SecondaryText)
7. Item separators (SecondaryText)

#### Default Focus Position

- First collection (index 0)

#### Layout

- 1 blank line
- Header: 3-line rounded box "Collections"
- 1 blank line
- List items: single column, 1-2 lines per item
- Status bar: 3 lines

#### Color Mapping

| Element | Color |
|---------|-------|
| Collection name (normal) | PrimaryText |
| Collection name (selected) | SelectedItemFg on SelectedItemBg |
| Item count `(n)` (normal) | SecondaryText |
| Star `★` (normal) | PromptFg |
| Accent bar (selected) | SelectedItemFg `▌` |
| Item separator | SecondaryText `╶──╴` |
| "more collections" indicator | SecondaryText |

### 5.5 Collection Items Screen

#### Visual Hierarchy

1. Selected item title (white on dark green, with accent bar)
2. Podcast CTA button (bright, prominent)
3. Unread item titles with `●` marker (PrimaryText + LinkContent)
4. Collection name in header (HeaderTitleFg)
5. Read item titles with `○` marker (ReadItemFg + ReadItemFg)
6. Domain lines (SecondaryText)
7. Cache indicator `· cached` (PromptFg/AccentFg)
8. Status bar cache progress

#### Default Focus Position

- First unread item if any exist; otherwise first item (index 0)
- Podcast CTA button is navigable but not default focus

#### Layout

- 1 blank line
- Header: 3-line rounded box "{Collection Name} ({n} items)"
- 1 blank line
- Podcast CTA button (if items exist): 1/3/5 lines depending on terminal size
- List items: 2 lines each (title + domain), optional separators
- Status bar: 3 lines

#### Color Mapping

| Element | Color |
|---------|-------|
| Unread marker `●` | LinkContent (ANSI 48) |
| Read marker `○` | ReadItemFg (ANSI 34) |
| Unread title (normal) | PrimaryText |
| Read title (normal) | ReadItemFg |
| Title (selected) | SelectedItemFg on SelectedItemBg |
| Domain (normal) | SecondaryText |
| Domain (selected) | on SelectedItemBg (plain text) |
| Cache badge (normal) | PromptFg `· cached` |
| Cache badge (selected) | plain text on SelectedItemBg |
| Accent bar (selected) | SelectedItemFg `▌` |

### 5.6 Loading Screen

Centered rounded box with animated braille spinner. See `loading-error.txt`
Screen 1 for the full mockup.

```
            ╭──────────────────────────────────────╮
            │                                      │
            │        ⠹ Loading page...            │
            │                                      │
            │  example.com/path/to/page            │
            │                                      │
            ╰──────────────────────────────────────╯
```

- Centered horizontally and vertically (roughly 1/3 from top)
- Rounded box border in HeaderBorderFg
- Animated braille spinner (⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏) in PromptFg
- "Loading page..." in PrimaryText
- URL in SecondaryText (split across lines if long)
- Box width: ~40 chars
- No key hints (user just waits)

### 5.7 Error Screen

Centered rounded box with error headline and action hints. See
`loading-error.txt` Screen 2 for the full mockup.

```
            ╭──────────────────────────────────────╮
            │                                      │
            │        Page failed to load           │
            │                                      │
            │  Connection timed out after 30s      │
            │                                      │
            │  example.com/path/to/page            │
            │                                      │
            │  R:retry    b:go back    o:browser   │
            │                                      │
            ╰──────────────────────────────────────╯
```

- Error headline in ErrorFg (ANSI 203)
- Error details in SecondaryText
- URL in SecondaryText
- Action hints at bottom: keys in AccentFg (ANSI 51), descriptions in SecondaryText

### 5.8 Bot Challenge Screen

Centered rounded box with amber spinner and breathing bar. See
`loading-error.txt` Screen 3 for the full mockup.

```
            ╭──────────────────────────────────────╮
            │                                      │
            │     ⠹ Bot challenge detected         │
            │                                      │
            │  A CAPTCHA or verification page      │
            │  appeared in the browser window.     │
            │                                      │
            │  Please solve it to continue.        │
            │                                      │
            │  example.com/path/to/page            │
            │                                      │
            │  ▏▎▍▌▋▊▉█▉▊▋▌▍▎▏                    │
            │  Waiting for page to resolve...      │
            │                                      │
            ╰──────────────────────────────────────╯
```

- Spinner in WarningFg (ANSI 214, amber) to indicate "needs your attention"
- "Bot challenge detected" in PrimaryText
- Instructions in SecondaryText
- Animated breathing bar in DimFg
- Auto-resolves when challenge is passed (no action keys needed)

---

## 6. AI Layout System

### 6.1 Overview

The layout variant system lets users preview and select between different visual
arrangements of each screen, so they can easily see what different scenarios
look like in terms of layout and choose the winner. Variants are defined
per-screen and cover different information densities, grid arrangements, and
visual styles.

> **Phase 1 Scope**: 2 variants per screen only (current layout + 1 alternative).
> Full variant expansion (3+ variants per screen) is Phase 2 and will not be
> implemented until Phase 1 is validated with real usage. This keeps the initial
> implementation manageable: 4 new layout paths instead of 10+.

### 6.2 Cycling Mechanism

- **Key combo**: `Ctrl+L` cycles forward through layout variants for the current screen
- **Reverse**: `Ctrl+Shift+L` cycles backward (if supported by terminal; otherwise wrap forward)
- **Lock in**: The selected variant is persisted immediately via `UserSettingsStore`
  with key `Layout:{ScreenName}` (e.g., `Layout:Launcher`, `Layout:LinkTree`)
- **Status bar badge**: When a non-default variant is active, show variant name
  in the status bar line 2 right side: `[Layout: Grid]` or similar in SecondaryText
- **Layout name flash**: On switch, display the layout name **large and centered**
  for 500ms using synchronous animation (see Section 4 constraint), then settle
  into the status bar badge. The flash uses a centered rounded box:
  ```
  ╭───────────────────────╮
  │   Layout: Dense List  │
  ╰───────────────────────╯
  ```
  rendered at vertical center of the terminal, then cleared on the next full redraw.
- **First-use hint**: On first use of any screen (tracked per-screen via
  `UserSettingsStore` key `HintSeen:Layout:{ScreenName}`), show a one-time hint
  in the status bar: `Tip: Ctrl+L to try different layouts` in SecondaryText.
  The hint is dismissed after the user presses any key and never shown again
  for that screen.

### 6.3 Implementation Architecture

```
// In Application/Interfaces/Browser/
public interface ILayoutVariantProvider
{
    string GetCurrentVariant(ViewMode mode);
    string CycleVariant(ViewMode mode);       // returns new variant name
    IReadOnlyList<string> GetVariants(ViewMode mode);
}
```

Each renderer's `ComputeLayout()` method gains an optional `string variant`
parameter. The variant name selects a specific set of layout constants.

### 6.4 Launcher Variants

**Phase 1**: Grid (A) + List (B). Compact (C) deferred to Phase 2.

#### Variant A: "Grid" (Default)

Current layout. 2-column grid with 5-line (or 3-line compact) bookmark cards.
Balanced information density with domain subtitles and badge shortcuts.

- Columns: 2 (or 1 if narrow)
- Cell height: 5 standard, 3 compact
- Shows: badge, name (uppercase), domain

#### Variant B: "List" (Phase 1)

Single-column list with more items visible. Each item is 2 lines (name + domain).
Better for users with many bookmarks who want to scan quickly.

- Columns: 1
- Cell height: 2 (name line + domain line with separator)
- Shows: badge, name (mixed case), domain, item separator `╶──╴`
- Fits ~2x more items on screen than Grid variant
- Selection: accent bar `▌` + full-width highlight (like collection items)

#### Variant C: "Compact" (Phase 2 -- deferred)

3-column grid with minimal cards. Name only, no domain. Maximum density
for power users who know their bookmarks by name.

- Columns: 3 (or 2 if < 90 chars, or 1 if < 45 chars)
- Cell height: 2 (name + separator)
- Shows: badge, name (uppercase, truncated aggressively)
- No domain line
- Selection: accent bar + highlight

### 6.5 Link Tree Variants

**Phase 1**: Cards (A) + Dense List (B). Magazine (C) deferred to Phase 2.

#### Variant A: "Cards" (Default)

Current layout. 2-column card grid with wrapped titles, metadata subtitles,
and separator rules between cards.

- Columns: 2 (or 1 if narrow)
- Cell height: 5 standard, 3 compact
- Shows: title (wrapped to 2 lines), author + date, separator rule

#### Variant B: "Dense List" (Phase 1)

Single-column list, 1 line per item. Maximum link density for scanning
large pages. Group headers still get their own line.

- Columns: 1
- Cell height: 1 (title only, truncated to fit)
- Shows: collapse indicator, type-colored title, right-aligned date (if room)
- Selection: accent bar + full-width highlight
- Group headers: uppercase, with child count, thin rule below if expanded
- Fits 3-4x more links on screen

#### Variant C: "Magazine" (Phase 2 -- deferred)

Single-column with generous spacing. Each item gets 3 lines: title, author/date,
and a blank line separator. Comfortable reading-oriented browsing.

- Columns: 1
- Cell height: 3 (title, subtitle, blank)
- Shows: title (full width, can be longer), author + date + domain
- Selection: accent bar + highlight
- No separator rules (blank line provides separation)
- Better for article-heavy pages

### 6.6 Reader View Variants

**Phase 1**: Comfortable (A) + Full Width (B). Narrow (C) deferred to Phase 2.

#### Variant A: "Comfortable" (Default)

Current layout. MaxContentWidth default 80, adjustable with h/l.

- Content width: 80 chars (default)
- Left margin: 2 chars
- Focus indicator: `▎` at viewport_height / 3

#### Variant B: "Full Width" (Phase 1)

Uses full terminal width minus minimal margins. For users who prefer
maximum text per line on wide terminals.

- Content width: terminal_width - 4
- Left margin: 2 chars
- Focus indicator: same position

#### Variant C: "Narrow" (Phase 2 -- deferred)

Narrow column for focused reading, similar to a book's column width.
Content centered in terminal.

- Content width: 60 chars (or terminal_width - 4 if narrower)
- Left margin: `(terminal_width - 60) / 2`
- Focus indicator: same relative position

### 6.7 Collection Items Variants

**Phase 1**: Standard (A) + Compact (B).

#### Variant A: "Standard" (Default)

Current layout. 2-line items with title + domain, podcast CTA button above list.

#### Variant B: "Compact" (Phase 1)

1-line items: marker + title + domain on same line. Podcast CTA as inline
(1 line). Maximum density for large reading lists.

- Line format: `● Title Here                    domain.com · cached`
- Title and domain separated by variable padding
- Domain right-aligned
- Much higher item density

### 6.8 Collection List Variants

Only one layout makes sense for a simple list of collection names. No variants.

### 6.9 Phase 1 Layout Variants Summary

| Screen | Default (A) | Alternative (B) |
|--------|------------|-----------------|
| Launcher | Grid (2-column) | List (single-column) |
| Link Tree | Cards (2-column grid) | Dense List (1 line per item) |
| Reader | Comfortable (80-char width) | Full Width (terminal width - 4) |
| Collection Items | Standard (2-line items) | Compact (1-line items) |

Layout choice persists per-screen via `UserSettingsStore` (key:
`Layout:{ScreenName}`, value: `"A"` or `"B"`). Default is `"A"` for all
screens if no preference is set.

---

## 7. Consistency Rules

### 7.1 Green Shade Usage (Phosphor Theme)

| ANSI Code | Hex | Role Name | When to Use | When NOT to Use |
|-----------|-----|-----------|-------------|-----------------|
| 46 | `#00ff00` | PrimaryText | Body text, item names, key shortcut letters, mode labels, prompts | Headlines (use 48), borders (use 35) |
| 48 | `#00ff87` | HeaderTitleFg, LinkContent, SuccessFg | Page titles in headers, content-type link text, success messages | Body text (too bright for sustained reading), borders |
| 40 | `#00d700` | LinkNavigation/External/Footer | Non-content link types in link tree | Primary body text, headers, anything interactive |
| 35 | `#00af5f` | HeaderBorderFg, StatusBarSeparatorFg | All box-drawing borders, separator rules, accent bars on selected items | Text content, link names |
| 34 | `#00af00` | SecondaryText, PromptLabelFg, ReadItemFg | Subtitles, domains, metadata, read items, hint action text, item counts | Primary headings, interactive elements |
| 28 | `#008700` | DimFg | Version strings, decorative characters, disabled text | Anything the user needs to read to use the app |
| 22 | `#005f00` | SelectedItemBg, FocusIndicatorFg | Selection highlight background, reader focus bar | Text foreground (too dark to read on black) |

### 7.2 Accent Color Rules (Cyan, ANSI 51)

**USE for**:
- Key shortcut letters in brackets when emphasis is needed (future enhancement)
- Toast notification borders (non-celebration)
- Cache status badges and indicators
- Interactive element highlights
- The "active" segment of progress bars (when combined with WarningFg for the
  single in-progress segment)

**DO NOT use for**:
- Body text (would break the green terminal aesthetic)
- Borders (borders should be green-family)
- Selection highlight text (white is higher contrast)
- Error states (use ErrorFg/red)
- Headers (use HeaderTitleFg green)

**Frequency**: Accent cyan should appear on every screen but sparingly.
Typically 1-3 elements per screen. It should draw the eye to actionable
or status-relevant information.

### 7.3 Celebration Color Rules (Pink, ANSI 198)

**USE for**:
- Podcast generation complete sparkle animation
- Podcast complete toast border
- The single-frame flash during celebration phase 1
- The typewriter cursor during celebration phase 3

**DO NOT use for**:
- Static UI elements (never persistent pink on screen)
- Error states
- Warning states
- Selection highlights
- Any element that stays on screen for more than 5 seconds

**Frequency**: The celebration color should appear at most once per user
session (typically when a podcast finishes generating). Its impact depends
on its rarity. If it appeared frequently, it would lose its surprise value.

### 7.4 Dimming Rules

Three levels of dimming via ANSI attribute + color choice:

| Level | Method | Effect | Usage |
|-------|--------|--------|-------|
| Slight dim | Use SecondaryText (ANSI 34) instead of PrimaryText (ANSI 46) | Noticeably less bright | Metadata, subtitles, non-primary info |
| Medium dim | Use SecondaryText (ANSI 34) + Dim attribute (`\x1b[2m`) | Significantly darker | Domains under items, action text in hints, decorative separators |
| Heavy dim | Use DimFg (ANSI 28) | Nearly invisible on black | Version strings, background decoration, disabled elements |

**Rules**:
- Never use Dim attribute on PrimaryText (it makes bright green muddy; use SecondaryText instead)
- Dim attribute stacks with color -- always apply color first, then Dim
- Always reset (`\x1b[0m`) after a Dim span; Dim does not self-terminate at line end in all terminals

### 7.5 Emphasis Rules

Two levels of emphasis:

| Level | Method | Usage |
|-------|--------|-------|
| Standard emphasis | Bold attribute (`\x1b[1m`) + PrimaryText | Item names, headers |
| Maximum emphasis | SelectedItemFg (white, ANSI 15) | Selected item text, focused input text |

**Rules**:
- Bold is used for: bookmark names, card titles (launcher), group header text (expanded)
- Bold is NOT used for: body text in reader view, metadata subtitles, domains
- White (ANSI 15) is reserved exclusively for SelectedItemFg -- never used
  for non-selected text in the Phosphor theme

### 7.6 Border and Separator Rules

| Context | Characters | Color |
|---------|-----------|-------|
| Page/screen header box | `╭─╮│╰─╯` | HeaderBorderFg (ANSI 35) |
| URL bar box | `╭─╮│╰─╯` | HeaderBorderFg (unfocused), SelectedItemFg (focused) |
| Status bar separator | `─` full width | StatusBarSeparatorFg (ANSI 35) |
| Card bottom rule (normal) | `─` full cell width | SecondaryText+Dim |
| Card bottom rule (selected) | `─` full cell width | HeaderBorderFg on SelectedItemBg |
| Column divider (content row) | `│` | SecondaryText |
| Column divider (rule row) | `┼` | SecondaryText |
| List item separator | `╶──╴` | SecondaryText |
| Sub-section header | `╭─ Title ──╮` | SecondaryText (normal), SelectedItemFg (selected) |
| Horizontal rule (general) | `─` | HeaderBorderFg |
| Status bar segment divider | `│` | SecondaryText |
| Footer horizontal rule | `─` | HeaderBorderFg |

**Rules**:
- All box corners are rounded (`╭╮╰╯`), never sharp (`┌┐└┘`)
- Borders are never Bold
- Border color (HeaderBorderFg) is always between SecondaryText and PrimaryText
  in brightness -- visible but not attention-grabbing
- Separator rules between cards use Dim attribute; structural borders do not

### 7.7 ANSI Reset Rules

- Every styled span must end with `\x1b[0m` (full reset)
- Never assume a style attribute carries from a previous write -- each
  `WriteLine()` call should be self-contained in its styling
- When combining multiple attributes (e.g., Bold + color + background),
  apply them in order: background, foreground, attribute
  (`\x1b[48;5;22m\x1b[38;5;15m\x1b[1m`)
- After `\x1b[0m`, all attributes (bold, dim, color, background) are cleared

### 7.8 Width Calculation Rules

- Always use `RenderHelpers.GetDisplayWidth()` for display width, never `.Length`
- ANSI escape sequences consume 0 display columns
- CJK characters consume 2 display columns
- Emoji (surrogate pairs) consume 2 display columns
- When building a padded line, compute padding as:
  `max(0, target_width - GetDisplayWidth(content))`
- When truncating, use `RenderHelpers.TruncateText()` which respects
  multi-byte character boundaries

---

## Appendix A: ANSI Escape Code Quick Reference

```
Reset:          \x1b[0m
Bold:           \x1b[1m
Dim:            \x1b[2m
Italic:         \x1b[3m      (not widely used in TermReader)
Underline:      \x1b[4m      (not used)
Reverse:        \x1b[7m      (available via Selection.ReverseVideo)
FG 256-color:   \x1b[38;5;{n}m
BG 256-color:   \x1b[48;5;{n}m
Cursor move:    \x1b[{row};{col}H
Clear to EOL:   \x1b[K
```

## Appendix B: Unicode Character Reference

All Unicode characters used in the TermReader UI:

| Character | Unicode | Name | Usage |
|-----------|---------|------|-------|
| `╭` | U+256D | Arc down and right | Box top-left corner |
| `╮` | U+256E | Arc down and left | Box top-right corner |
| `╰` | U+2570 | Arc up and right | Box bottom-left corner |
| `╯` | U+256F | Arc up and left | Box bottom-right corner |
| `─` | U+2500 | Light horizontal | Borders, rules, separators |
| `│` | U+2502 | Light vertical | Box sides, column dividers |
| `┼` | U+253C | Light cross | Column divider at rule intersection |
| `╶` | U+2576 | Light right | Item separator left cap |
| `╴` | U+2574 | Light left | Item separator right cap |
| `▌` | U+258C | Left half block | Selection accent bar |
| `▎` | U+258E | Left 1/4 block | Reader focus indicator |
| `▏` | U+258F | Left 1/8 block | Progress bar right cap |
| `▕` | U+2595 | Right 1/8 block | Progress bar left cap |
| `▍` | U+258D | Left 3/8 block | Progress bar partial fill |
| `▋` | U+258B | Left 5/8 block | Progress bar partial fill |
| `▊` | U+258A | Left 3/4 block | Progress bar partial fill |
| `▉` | U+2589 | Left 7/8 block | Progress bar partial fill |
| `█` | U+2588 | Full block | Progress bar full, typewriter cursor |
| `░` | U+2591 | Light shade | Decrypt animation |
| `▒` | U+2592 | Medium shade | Decrypt animation |
| `▓` | U+2593 | Dark shade | Decrypt animation |
| `●` | U+25CF | Black circle | Unread item marker |
| `○` | U+25CB | White circle | Read item marker |
| `▼` | U+25BC | Down triangle | Expanded indicator, scroll down |
| `▶` | U+25B6 | Right triangle | Collapsed indicator, play icon |
| `▲` | U+25B2 | Up triangle | Scroll up indicator |
| `→` | U+2192 | Right arrow | Legacy selection arrow |
| `★` | U+2605 | Black star | Default collection marker |
| `▰` | U+25B0 | Black parallelogram | Filled progress segment |
| `▱` | U+25B1 | White parallelogram | Empty progress segment |
| `✦` | U+2726 | Four-pointed star | Sparkle animation, toast icon |
| `✧` | U+2727 | Four-pointed star outline | Sparkle animation |
| `✔` | U+2714 | Heavy check mark | Completion indicator |
| `♫` | U+266B | Beamed eighth notes | Podcast toast icon |
| `·` | U+00B7 | Middle dot | Metadata separator, sparkle animation |
| `⠇` | U+2847 | Braille dots-1-2-3 | Loading spinner |
| `⚡` | U+26A1 | High voltage | Warning toast icon |
| `✗` | U+2717 | Ballot X | Error toast icon |

## Appendix C: File Paths for Implementation

All files that must be modified or created to implement this design system:

**Modify:**
- `/workspace/src/TermReader.Infrastructure/Browser/Themes/ThemePalette.cs` -- add new color role properties
- `/workspace/src/TermReader.Infrastructure/Browser/Themes/BuiltInThemes.cs` -- update all 4 themes with new colors; change AccentFg ANSI 43 to 51 in Phosphor
- `/workspace/src/TermReader.Infrastructure/Browser/UI/Renderers/StatusBarRenderer.cs` -- layout variant badge, toast rendering support
- `/workspace/src/TermReader.Infrastructure/Browser/UI/Renderers/LauncherRenderer.cs` -- layout variants
- `/workspace/src/TermReader.Infrastructure/Browser/UI/Renderers/LinkTreeRenderer.cs` -- layout variants
- `/workspace/src/TermReader.Infrastructure/Browser/UI/Renderers/ArticleRenderer.cs` -- reader width variants
- `/workspace/src/TermReader.Infrastructure/Browser/UI/Renderers/CollectionRenderer.cs` -- compact variant
- `/workspace/src/TermReader.Infrastructure/Browser/UI/Renderers/PodcastCtaRenderer.cs` -- eighth-block progress bar
- `/workspace/src/TermReader.Infrastructure/Browser/UI/Components/Indicators.cs` -- new indicator characters
- `/workspace/src/TermReader.Infrastructure/Browser/UI/TerminalPageRenderer.cs` -- animation orchestration hooks

**Create:**
- `/workspace/src/TermReader.Application/Interfaces/Browser/ILayoutVariantProvider.cs` -- layout variant interface
- `/workspace/src/TermReader.Infrastructure/Browser/UI/LayoutVariantProvider.cs` -- implementation with UserSettingsStore persistence
- `/workspace/src/TermReader.Infrastructure/Browser/UI/Components/Toast.cs` -- toast notification component
- `/workspace/src/TermReader.Infrastructure/Browser/UI/Animations/SparkleAnimation.cs` -- sparkle effect
- `/workspace/src/TermReader.Infrastructure/Browser/UI/Animations/DecryptAnimation.cs` -- title decrypt reveal
- `/workspace/src/TermReader.Infrastructure/Browser/UI/Animations/ColorWaveAnimation.cs` -- status bar wave
- `/workspace/src/TermReader.Infrastructure/Browser/UI/Animations/EighthBlockProgressBar.cs` -- smooth progress bar
- `/workspace/src/TermReader.Infrastructure/Browser/UI/Animations/AnimationRunner.cs` -- shared animation loop with frame timing
