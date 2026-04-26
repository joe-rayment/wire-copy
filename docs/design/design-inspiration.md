# TermReader Design Inspiration: Retro Terminal with Modern Delight

Research compiled 2026-03-18. This document covers color palettes, animation techniques, UX patterns, and specific implementation ideas for making TermReader feel retro yet surprisingly modern.

---

## Table of Contents

1. [Current State Analysis](#1-current-state-analysis)
2. [Accent Color Recommendations](#2-accent-color-recommendations)
3. [Exemplary TUI Applications](#3-exemplary-tui-applications)
4. [Animation & Visual Effects Techniques](#4-animation--visual-effects-techniques)
5. [Unicode Character Toolkit](#5-unicode-character-toolkit)
6. [UX Patterns for Guiding Users](#6-ux-patterns-for-guiding-users)
7. [Concrete Ideas for TermReader](#7-concrete-ideas-for-termreader)
8. [Sources](#8-sources)

---

## 1. Current State Analysis

### Existing Theme System

TermReader uses 256-color ANSI codes via `ThemeColor(ConsoleColor, byte AnsiCode)`. The Phosphor theme currently has:

| Role | ANSI 256 | Approximate Hex | Description |
|------|----------|-----------------|-------------|
| PrimaryText | 46 | #00ff00 | Classic CRT green |
| SecondaryText | 34 | #00af00 | Dimmer green |
| HeaderBorderFg | 35 | ~#00af5f | Subtle border green |
| SelectedItemBg | 22 | #005f00 | Dark green selection |
| AccentFg | 43 | ~#00afaf | Cyan accent (already exists!) |
| DimFg | 28 | ~#008700 | Very dim green |
| ErrorFg | 203 | ~#ff5f5f | Red for errors |

**Observation**: The palette is almost entirely green-on-green. The existing cyan accent (ANSI 43) is a good start but is a dark, desaturated cyan -- it does not pop. The secondary colors (SecondaryText, LinkNavigation, LinkExternal, LinkFooter) are all dark green variants, creating a visually flat hierarchy.

---

## 2. Accent Color Recommendations

### Color Theory Context

Green's complement on the RGB color wheel is **magenta** (red + blue). The split-complementary neighbors are **hot pink/rose** and **violet/purple**. These provide maximum contrast while remaining aesthetically harmonious.

For a retro terminal, the best accent candidates are:

### Option A: Hot Cyan (Recommended Primary Accent)

- **ANSI 256 code**: 51 (#00ffff, pure cyan) or 87 (#5fffff, slightly softer)
- **Why it works**: Cyan is the classic complementary partner to the green-on-black terminal. It reads as "data highlight" or "interactive element" without breaking the retro feel. It is reminiscent of dual-phosphor terminals that mixed green and cyan. Every cyberpunk palette pairs cyan with green on dark backgrounds.
- **Use for**: Interactive hints, key shortcut labels, cache indicators, the "active" state of UI elements.
- **Current state**: AccentFg is already set to ANSI 43 (~#00afaf), but this is too dark. Bumping to ANSI 51 or 87 would make it pop while staying in the same hue family.

### Option B: Hot Pink / Magenta (Recommended "Surprise" Accent)

- **ANSI 256 code**: 198 (#ff0087) or 205 (#ff5faf) or 213 (#ff87ff)
- **Why it works**: Magenta is green's true complement. A sudden flash of hot pink in a green terminal is deeply surprising -- it feels like a glitch in the matrix, a neon sign flickering to life. This is the "modern touch the user wouldn't expect." Dracula theme uses pink (#FF79C6) as its header color for exactly this reason. Cyberpunk design pairs neon magenta with acid green as a foundational combination.
- **Use for**: Celebration moments only (podcast complete, cache warmed). Sparingly. The surprise factor depends on rarity.
- **Contrast**: Hot magenta on black has a 5.7:1 contrast ratio (AA compliant). On green text it is maximally distinct.

### Option C: Warm Amber/Gold (Subtle Warmth)

- **ANSI 256 code**: 214 (#ffaf00) or 220 (#ffd700)
- **Why it works**: Amber evokes the other classic phosphor terminal color. Using it as an accent in the green theme creates a "dual phosphor" feel -- as if the terminal has two display modes bleeding together.
- **Use for**: Warnings, budget indicators, or as an alternative to pink for users who want a less "cyberpunk" feel.

### Recommended Palette Extension for Phosphor Theme

```
Primary green:    ANSI 46  (#00ff00)  -- body text
Bright green:     ANSI 48  (#00ff87)  -- headlines, emphasis
Dim green:        ANSI 34  (#00af00)  -- secondary text
Dark green:       ANSI 22  (#005f00)  -- backgrounds, borders
Cyan accent:      ANSI 51  (#00ffff)  -- interactive elements, hints
                  or ANSI 87 (#5fffff) -- softer alternative
Pink flare:       ANSI 198 (#ff0087)  -- celebrations, one-time events
                  or ANSI 205 (#ff5faf) -- softer, more approachable
White highlight:  ANSI 15  (#ffffff)  -- selected items, maximum emphasis
```

---

## 3. Exemplary TUI Applications

### lazygit
- **What makes it work**: Panel-based layout with clear focus indicators. The selected panel gets a colored border; unselected panels get dim borders. Information hierarchy is crystal clear -- you always know where you are.
- **Technique**: Bright border on focused panel, dim border on unfocused. Status bar shows context-sensitive key hints.
- **Applicable to TermReader**: The focused-panel-bright-border pattern could highlight the active view (link tree vs. article vs. launcher).

### btop++
- **What makes it work**: Dense information displayed beautifully through Unicode block characters for graphs. Uses braille characters (U+2800-U+28FF) for high-resolution sparkline graphs. Color gradients across graph bars (green -> yellow -> red for CPU usage).
- **Technique**: Braille-pattern graphs, block-element bar charts, color gradient encoding of values.
- **Applicable to TermReader**: Cache warmth or podcast generation progress could use braille-pattern progress visualization.

### k9s
- **What makes it work**: Full skinning system with 256-color support. Real-time data updates without flicker. Resource-type-aware color coding.
- **Technique**: Custom skins via YAML, color-coded resource states, breadcrumb navigation.
- **Applicable to TermReader**: Breadcrumb-style navigation path in the status bar.

### Charm (Bubble Tea / Lip Gloss ecosystem)
- **What makes it work**: CSS-like declarative styling. Rounded borders (using Unicode ╭╮╰╯). Padding and margin create visual breathing room. Gradient borders using multiple colors blended across border characters.
- **Technique**: Composable style objects with border, padding, margin, alignment. Automatic color downsampling for terminal compatibility. Border gradient support.
- **Applicable to TermReader**: The concept of giving UI elements padding and breathing room, even in a terminal, is transformative. A panel with 1-character padding feels dramatically more "designed" than edge-to-edge text.

### Textual (Python TUI framework)
- **What makes it work**: Toast notifications that slide in from the edge. CSS-like styling with transitions and animations. Widget focus system with visible focus rings.
- **Technique**: Toast overlays, focus ring animations, smooth transitions.
- **Applicable to TermReader**: Toast-style notifications for events like "Cache warmed for 12 pages" or "Podcast ready."

### Yazi (file manager)
- **What makes it work**: Image previews in terminal (via Sixel/Kitty protocol). Smooth scrolling. Minimalist but information-dense layout.
- **Applicable to TermReader**: The discipline of showing just enough information, with clear hierarchy.

---

## 4. Animation & Visual Effects Techniques

### What Is Possible in a Terminal

Terminals support animation through:
1. **Cursor repositioning**: `\x1b[{row};{col}H` to move cursor to any position
2. **Color changes**: ANSI 256-color or 24-bit color on any character
3. **Character replacement**: Overwrite any cell with a new character
4. **Timing**: `Thread.Sleep()` or `Task.Delay()` between frames
5. **Alternate screen buffer**: `\x1b[?1049h` to draw without disturbing main content

These primitives are enough to create surprisingly rich animations.

### Technique 1: Sparkle/Glitter Effect

**How it works**: Randomly select N character positions in a region. Over several frames, cycle each position through a sequence of sparkle characters (e.g., `.` -> `+` -> `*` -> `✦` -> `✧` -> `*` -> `+` -> `.` -> original). Apply color cycling simultaneously (dim green -> bright green -> cyan -> white -> cyan -> bright green -> dim green).

**Characters**: `. + * ✦ ✧ ✶ ❇ ✨ ·`

**Duration**: 0.5-1.5 seconds total, 50-100ms per frame.

**Use case**: When a podcast finishes generating, sparkle the "Complete" text or the podcast title.

### Technique 2: Color Wave / Sweep

**How it works**: A "wave" of color sweeps across a line or region of text. Each character temporarily shifts to a brighter color (or accent color) as the wave passes, then returns to its original color. The wave moves at ~5-10 characters per frame.

**Implementation**: For each frame, calculate wave position. Characters within the wave width get the accent color; characters behind the wave return to normal. Use sine-wave falloff for smooth edges.

**Use case**: When cache preloading completes for a collection, sweep a subtle cyan wave across the status bar text.

### Technique 3: Progressive Reveal / Decrypt

**How it works**: Text appears to "decode" from random characters to the final text. Each character position starts as a random glyph and over N frames converges on the correct character. Inspired by movie-style decryption sequences.

**Characters**: Start with `░▒▓█` or random alphanumerics, converge to actual text.

**Use case**: When loading a new page, the title could "decrypt" into place rather than just appearing. This is the quintessential "retro terminal doing something unexpectedly cool" moment.

### Technique 4: Braille-Pattern Progress Animation

**How it works**: Use the 2x4 pixel grid of braille characters (U+2800-U+28FF) to create a high-resolution progress bar. Each character cell provides 8 sub-pixels. A progress bar can have 8x the resolution of a regular character-based bar.

**Characters**: ` ⠁⠃⠇⡇⡏⡟⡿⣿` (progressive fill from empty to full)

**Variation**: Animated "liquid fill" effect where the fill level oscillates slightly around the target, like liquid sloshing.

**Use case**: Podcast generation progress bar, showing fine-grained progress for each chunk.

### Technique 5: Expanding Ring / Radial Burst

**How it works**: From a center point (e.g., a button or status indicator), characters radiate outward in a ring pattern. The ring uses block shading characters that get lighter as they expand: `█` -> `▓` -> `▒` -> `░` -> ` `. Color transitions from bright accent to dim.

**Use case**: When user triggers podcast generation, a brief radial burst from the CTA button.

### Technique 6: Typing / Typewriter Effect

**How it works**: Text appears character by character with a cursor, as if being typed. Add random micro-pauses for realism. The cursor blinks between characters.

**Characters**: `█` or `▌` as cursor.

**Use case**: Status messages that feel like the terminal is "talking to you." E.g., when podcast starts: `Generating podcast▌` with characters appearing one by one.

### Technique 7: Particle Cascade (Confetti Alternative)

**How it works**: Characters fall from a point (or rise from a point), using physics-like trajectories. Each particle is a small Unicode character (`.`, `·`, `*`, `✦`, `✧`) with randomized velocity and gravity. Particles fade out by cycling through dimmer colors.

**Duration**: 1-2 seconds.

**Use case**: Podcast generation complete celebration. Characters cascade from the notification text.

### Technique 8: Gradient Text / Rainbow Shimmer

**How it works**: Apply a color gradient across a line of text, then slowly shift the gradient's phase so colors appear to flow through the text. Using 256-color ANSI, you can create smooth gradients across the green spectrum (ANSI 22 -> 28 -> 34 -> 40 -> 46 -> 48 -> 50 -> 51 for green-to-cyan).

**Use case**: The app title or a "success" message that shimmers with a flowing gradient.

---

## 5. Unicode Character Toolkit

### Box Drawing (U+2500-U+257F)

```
Light:    ─ │ ┌ ┐ └ ┘ ├ ┤ ┬ ┴ ┼
Heavy:    ━ ┃ ┏ ┓ ┗ ┛ ┣ ┫ ┳ ┻ ╋
Double:   ═ ║ ╔ ╗ ╚ ╝ ╠ ╣ ╦ ╩ ╬
Rounded:  ╭ ╮ ╰ ╯  (modern touch -- rounded corners feel surprisingly "designed")
Dashed:   ┄ ┅ ┆ ┇ ┈ ┉ ┊ ┋
```

**Design note**: Rounded corners (╭╮╰╯) are the single highest-impact change for making a TUI feel modern. They are universally supported in modern terminal emulators and instantly signal "this was designed, not just functional."

### Block Elements (U+2580-U+259F)

```
Full:     █ (full block)
Halves:   ▀ (upper half) ▄ (lower half) ▌ (left half) ▐ (right half)
Eighths:  ▏▎▍▌▋▊▉█ (left 1/8 through full -- perfect for smooth progress bars)
Shades:   ░ (25%) ▒ (50%) ▓ (75%) (great for gradients, shadows, depth)
Quadrants: ▖▗▘▙▚▛▜▝▞▟ (4 sub-pixels per cell)
```

**Design note**: The eighth-block characters (▏▎▍▌▋▊▉█) enable butter-smooth progress bars with 8x the resolution of character-based bars. Combined with color, they can show gradient-filled progress bars.

### Braille Patterns (U+2800-U+28FF)

```
Empty: ⠀  Full: ⣿
Each character = 2x4 dot grid = 8 sub-pixels
256 possible patterns per character
```

**Design note**: Braille patterns provide the highest resolution available in terminal graphics -- effectively doubling both horizontal and vertical resolution. Excellent for sparkline graphs, waveform displays, and fine-grained progress visualization. A standard 80x24 terminal becomes 160x96 "pixels" with braille.

### Stars, Sparkles, and Decorative Symbols

```
Stars:     ✦ ✧ ✶ ★ ☆ ✯ ✱ ✴ ✹ ⋆
Sparkles:  ❇ ❈ ✨ ✺ ❉
Dots:      · • ● ○ ◦ ◉ ◎
Arrows:    → ← ↑ ↓ ▸ ▹ ▾ ▿ ◂ ◃ ▴ ▵ ➜ ➤
Diamonds:  ◆ ◇ ◈ ♦
Musical:   ♪ ♫ ♩ (relevant for podcast/audio features!)
Misc:      ⚡ ☰ ⌂ ⚙ ⏎ ⏏ ⌘
```

**Design note**: The musical notes (♪ ♫) are thematically perfect for podcast features. Using `♪` as a prefix for podcast-related items creates an instant visual association.

### Powerline / Status Bar Characters

```
Separators:  ▸   (triangular separators for status bar segments)
             ╱ ╲  (diagonal separators)
Rounded:     (  ) (using rounded box-drawing for pill-shaped badges)
```

---

## 6. UX Patterns for Guiding Users

### Pattern 1: "Brightest Element Wins" -- Visual Hierarchy Through Luminance

The most important actionable item should be the brightest element on screen. In a green terminal:
- **Primary action**: Bright green (ANSI 46) or white (ANSI 15)
- **Secondary content**: Medium green (ANSI 34)
- **Tertiary/decorative**: Dim green (ANSI 28 or 22)
- **Call-to-action accent**: Cyan (ANSI 51) or pink (ANSI 198) -- stands out by being a different hue entirely

This creates a natural visual gravity toward the most important element without explicit "buttons."

### Pattern 2: Focus Follows Context

The default selection/focus should always be on the most likely next action:
- After navigating to a page with a reading list: focus the first unread item.
- After completing podcast generation: focus shifts to "Play" or "Open folder."
- After a search with results: focus the first result.
- In the launcher: the most recently visited bookmark is pre-selected.

### Pattern 3: Inline Hints That Disappear

Show key shortcut hints next to the focused element, but only for the focused element. Unfocused elements show no hints. This reduces clutter while being maximally helpful.

Example:
```
  The Attention Merchants             [Enter] open  [x] podcast
> The Information: A History ◀────── hints only shown for focused item
  Daemon
```

### Pattern 4: Progressive Disclosure

Start with the minimal UI. As the user demonstrates expertise (number of sessions, commands used), subtly reveal more:
- First visit: Show a brief key-hint overlay
- Subsequent visits: Show only the status bar hints
- Power users: Hints can be toggled off entirely

### Pattern 5: Status Bar as Living Dashboard

The status bar should be the "always-on" information strip. Segment it with separators and use color coding:

```
╭─────────────────────────────────────────────────────────────────────╮
│  ⌂ Home ▸ nytimes.com ▸ Technology    12/47 items    ♪ 3 cached   │
╰─────────────────────────────────────────────────────────────────────╯
```

- Breadcrumb path in primary color
- Item counter in secondary color
- Cache/podcast badge in accent color (cyan)
- Use `▸` as segment separator for a modern feel

### Pattern 6: Toast Notifications (Non-Blocking)

For background events (cache complete, podcast ready), display a brief toast that:
- Appears in a corner (top-right is standard)
- Uses a contrasting accent color (cyan border, or pink for celebrations)
- Auto-dismisses after 3-5 seconds
- Does NOT steal focus or require user interaction
- Uses rounded-corner box drawing for the toast border

Example toast:
```
                                           ╭─────────────────────╮
                                           │ ✦ Cache warmed (12) │
                                           ╰─────────────────────╯
```

---

## 7. Concrete Ideas for TermReader

### 7.1 "Podcast Complete" Celebration (The Showstopper)

When a podcast finishes generating, this is the moment for maximum delight:

1. **Phase 1 -- Flash** (200ms): The status bar briefly flashes bright cyan or white.
2. **Phase 2 -- Sparkle** (800ms): 5-10 random positions near the notification text cycle through sparkle characters (`· * ✦ ✧ ❇`) in cycling colors (green -> cyan -> white -> cyan -> green).
3. **Phase 3 -- Reveal** (400ms): The completion message types out character-by-character: `♫ Podcast ready: "The Attention Merchants" (47m)▌`
4. **Phase 4 -- Settle** (200ms): Sparkles fade, message stays in accent color for 5 seconds, then dims to secondary color.

Total duration: ~1.6 seconds. Brief enough to be delightful, not annoying.

**Why this feels "incongruous"**: Terminal users expect static text output. Particle effects and typewriter reveals feel like they belong in a GUI, not a terminal. The surprise is the medium itself doing something unexpected.

### 7.2 Cache Warm Indicator

When background preloading completes:
- A subtle color wave sweeps across the status bar left-to-right (green -> bright cyan -> green).
- The cache counter badge updates with a brief "pulse" (brighter for one frame, then settles).
- No sparkles, no particles -- this is a background event that deserves acknowledgment, not celebration.

### 7.3 Accent Color Integration

Upgrade the Phosphor theme's accent usage:

- **Key hints**: Render shortcut keys in bright cyan (ANSI 51) instead of the current dim cyan (ANSI 43). E.g., `[x]` in cyan where `x` is the key.
- **Interactive indicators**: The `· cached` suffix on cached items should be cyan, not dim green.
- **Musical note prefix**: Podcast-related items get a `♪` prefix in cyan.
- **Link hover/selection**: When an item is selected, show its shortcut hint in cyan to draw the eye.

### 7.4 Rounded Corner Upgrade

Replace all box-drawing borders from sharp corners to rounded:
- `┌┐└┘` -> `╭╮╰╯`
- This single change has an outsized aesthetic impact. Rounded corners read as "modern" and "designed" in a context where users expect only angular, utilitarian shapes.

### 7.5 Progress Bar for Podcast Generation

Replace any simple percentage display with a smooth braille or eighth-block progress bar:

```
Generating ▕▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░▏ 62%  Chunk 5/8
```

Using eighth-block characters for sub-character precision:
```
▕████████████▍          ▏ 62%
```

Color the filled portion with a gradient: dark green (start) -> bright green (current position) -> dim green (upcoming).

### 7.6 Page Load "Decrypt" Effect

When navigating to a new page, instead of the title simply appearing:
1. The title area fills with random characters (░▒▓ or random glyphs) in dim green.
2. Over 300-500ms, characters "resolve" left-to-right into the actual page title.
3. The final title settles in bright green.

This creates the feeling of a terminal "decoding" the page, reinforcing the retro-hacker aesthetic while being genuinely fun to watch.

### 7.7 Breathing Cursor / Focus Indicator

Instead of a static highlight for the selected item, use a subtle "breathing" effect:
- The selection background color gently oscillates between two close ANSI values (e.g., ANSI 22 and ANSI 28) on a 2-second cycle.
- This creates the impression of a living, responsive interface without being distracting.
- Modern terminals can handle redrawing a single line at 1-2 fps without any performance concern.

### 7.8 Launch Screen / Welcome Banner

On first launch or on the launcher screen, display the app name in a FIGlet-style ASCII art banner, potentially with a color gradient:

```
  ╭──────────────────────────────────────╮
  │                                      │
  │   ▀█▀ █▀▀ █▀█ █▄▀▄█                 │
  │    █  ██▄ █▀▄ █ ▀ █                 │
  │    ▀  ▀▀▀ ▀ ▀ ▀   ▀  reader        │
  │                                      │
  ╰──────────────────────────────────────╯
```

With a slow color gradient cycling through greens, giving the impression of phosphor glow.

---

## 8. Sources

### Terminal UI Design & Tools
- [awesome-tuis: Curated list of TUI projects](https://github.com/rothgar/awesome-tuis)
- [TUI Studio: Visual Terminal UI Design Tool](https://tui.studio/)
- [awesome-ratatui: TUI apps built with Ratatui](https://github.com/ratatui/awesome-ratatui)
- [lazygit: Terminal UI for Git](https://github.com/jesseduffield/lazygit)
- [k9s: Kubernetes Terminal UI](https://k9scli.io/)

### Retro Terminal Aesthetic
- [Retro Terminal aesthetic.fyi](https://aesthetic.fyi/retro-terminal/)
- [cool-retro-term: CRT terminal emulator](https://github.com/Swordfish90/cool-retro-term)
- [Classic Hacker Terminal Aesthetic](https://grokipedia.com/page/Classic_Hacker_Terminal_Aesthetic)
- [Green Monochrome CRT Phosphor Theme for Zed](https://github.com/Takk8IS/green-monochrome-monitor-crt-phosphor-theme-for-zed)

### Color Schemes & Palettes
- [Dracula Theme](https://draculatheme.com/)
- [Catppuccin Terminal Color Scheme](https://terminalcolors.com/themes/catppuccin/)
- [Let's Create a Terminal Color Scheme (Ham Vocke)](https://hamvocke.com/blog/lets-create-a-terminal-color-scheme/)
- [How to Create a Cyberpunk Color Palette](https://pageflows.com/resources/cyberpunk-color-palette/)
- [Cyberpunk Neon Themes](https://github.com/Roboron3042/Cyberpunk-Neon)
- [Color Theory and Complementary Colors (IxDF)](https://ixdf.org/literature/topics/complementary-colors)

### Animation & Effects Libraries
- [TerminalTextEffects (TTE): Terminal Visual Effects Engine](https://github.com/ChrisBuilds/terminaltexteffects)
- [TTE Effects Showroom](https://chrisbuilds.github.io/terminaltexteffects/showroom/)
- [tachyonfx: Shader-like effects for Ratatui](https://github.com/ratatui/tachyonfx)
- [notcurses: Blingful TUI library](https://github.com/dankamongmen/notcurses)
- [gradient-string: Color gradients in terminal output](https://github.com/bokub/gradient-string)
- [From Pixels to Characters (GitHub Copilot CLI Banner)](https://github.blog/engineering/from-pixels-to-characters-the-engineering-behind-github-copilot-clis-animated-ascii-banner/)

### .NET Terminal Libraries
- [Spectre.Console: Beautiful .NET Console Apps](https://github.com/spectreconsole/spectre.console)
- [Spectre.Console FIGlet Widget](https://spectreconsole.net/console/widgets/figlet)
- [Spectre.Console Live Display](https://spectreconsole.net/live/live-display)
- [ShellProgressBar for .NET](https://www.nuget.org/packages/ShellProgressBar)

### Styling & Layout
- [Lip Gloss: Terminal Style Definitions](https://github.com/charmbracelet/lipgloss)
- [Bubble Tea: TUI Framework](https://github.com/charmbracelet/bubbletea)
- [retro-futuristic-ui-design](https://github.com/Imetomi/retro-futuristic-ui-design)

### Unicode References
- [Box Drawing Characters (Wikipedia)](https://en.wikipedia.org/wiki/Box-drawing_characters)
- [Block Elements (Wikipedia)](https://en.wikipedia.org/wiki/Block_Elements)
- [Braille Patterns for Terminal Graphics](https://asherfalcon.com/blog/posts/4)
- [Unicode Graphics Beyond Braille](https://dernocua.github.io/notes/unicode-graphics.html)
- [Star & Sparkle Unicode Symbols](https://www.i2symbol.com/symbols/stars)
- [Braille Progress Bars](https://github.com/Cygra/interesting-unicode-symbols/issues/1)
- [Better CLI Progress Bars with Unicode Blocks](https://mike42.me/blog/2018-06-make-better-cli-progress-bars-with-unicode-block-characters)

### UX Patterns
- [Button States: Communicate Interaction (NN/g)](https://www.nngroup.com/articles/button-states-communicate-interaction/)
- [UX of Toast Notifications](https://benrajalu.net/articles/ux-of-notification-toasts)
- [Terminal Color Schemes Part 1 (extrema.is)](https://www.extrema.is/blog/2022/02/21/terminal-color-schemes-part-1)
