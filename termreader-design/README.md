# TermReader Design System

A better reading experience. **TermReader** is a .NET 9 terminal-based web browser and reader, built for long-form reading without the noise of a normal browser. It runs entirely inside a terminal emulator (Ghostty preferred), uses Helix-style keybindings (`j/k/h/l/gg/G`), and can turn a reading list into an M4B audiobook via ElevenLabs TTS.

This design system is the single source of truth for how the product looks, reads, and feels. It's a phosphor-green CRT aesthetic with **playful pink** accents — serious about reading, not serious about itself.

---

## Sources

Everything here is distilled from the TermReader repo:

- Repository: `joe-rayment/newspaper_reader` (private, main branch)
- `README.md` — product overview, features, keybindings
- `design-system.md` (65 KB) — the canonical in-app spec: palette, typography, components, animations, screen-by-screen rules
- `design-inspiration.md` — reference-direction notes
- `design-critique.md` — open issues against the current UI
- `mockups/*.txt` and `mockups/*.html` — ASCII and HTML renderings of each screen
- `mockups/template.css` — the existing Phosphor 2.0 CSS token set (reused directly here)
- `src/TermReader.*` — C# source (Domain / Application / Infrastructure / API / Persistence)

> Nothing from the repo is pre-bundled. Files referenced here were read on demand and the styles were ported 1:1 from `mockups/template.css`.

---

## Index

| File / folder | What's in it |
|---|---|
| `README.md` | This document — brand context, content fundamentals, visual foundations, iconography |
| `colors_and_type.css` | CSS variables for color + type, utility classes (`.tr-g`, `.tr-title`, `.tr-sel`, …), terminal frame styles |
| `SKILL.md` | Agent-SKILL-compatible entry point |
| `preview/` | Design System cards — typography, color, spacing, components, brand |
| `ui_kits/terminal/` | React-JSX kit: Terminal frame + screens (Launcher, Link Tree, Reader, Reading List, Toast, Loading/Error). `index.html` is an interactive click-thru prototype. |
| `assets/` | Logos, braille-spinner reference, icon glossary |
| `fonts/` | (empty) — uses system mono stack; see fonts note below |

---

## The product, in a sentence

Open a terminal. Type a URL or pick a bookmark. Read the article without ads, popups, or tracking, with keyboard-only navigation. Save interesting pieces to a reading list. Turn the reading list into a podcast.

### Surfaces

Only one product, one surface: the **terminal TUI**. There is no web app, mobile app, or marketing site in the repo. Every screen is rendered with ANSI escape sequences into a 80×24 (or larger) text grid. No mouse. No graphics. No images in-app.

Screens:

1. **Launcher** — bookmark grid with a URL bar
2. **Link Tree** — categorized view of a page's links (content / navigation / external / footer)
3. **Reader View** — the article, stripped to just text, with a focus indicator
4. **Collections (list)** — all reading lists
5. **Collection Items (Reading List)** — saved articles within one list, with a Podcast CTA
6. **Loading / Error / Bot Challenge** — centered modal boxes with rounded corners
7. **Help overlay** — keybindings

---

## Content Fundamentals

**Tone:** dry, literate, honest. Assumes a keyboard-comfortable reader who already uses a terminal and prefers `j` to scrolling. No marketing-speak, no exclamation marks, no "Hey there!" anything. Copy is instructional or informational; rarely promotional.

**Voice:** second-person imperative for actions ("Press `s` to save"), third-person declarative for status ("Cache warmed 24/24 articles"). Avoid first-person "we" entirely — the tool doesn't have a personality, it has keybindings.

**Casing rules**

| Context | Rule | Example |
|---|---|---|
| Page / screen titles in the header box | Sentence case | `Ars Technica: Science` |
| Bookmark card names (Launcher) | ALL CAPS with 0.08em tracking | `ARS TECHNICA`, `HACKER NEWS` |
| Link tree group headers | ALL CAPS | `▼ NAVIGATION (12)` |
| Article titles | Title Case as published | `SpaceX Starship Reaches Orbit on Its First Full Flight` |
| Status-bar mode label | PascalCase, one word | `LinkView`, `ReaderView`, `Collections`, `ReadingList`, `Launcher` |
| Key-hint actions | lowercase, verb | `open`, `save`, `save-all`, `browser`, `back` |
| Toast messages | Sentence case | `Cache warmed`, `Bookmark added`, `Save failed` |
| Error headlines | Sentence case, blunt | `Page failed to load`, `Network unreachable`, `Page not found (404)` |

**I vs you:** Always "you". Never "we" or "I". Copy addresses the reader directly only when giving an instruction. Most copy has no pronoun at all — it's just a label or a status.

**Emoji:** **Never.** Emoji don't render reliably across terminals, don't fit the phosphor aesthetic, and would break the ANSI color palette. Use Unicode box-drawing, geometric, and braille characters instead (see Iconography).

**Punctuation quirks**

- **Separator:** center dot `·` (U+00B7) between metadata fields — `Eric Berger · Today · arstechnica.com`
- **Ellipsis:** single glyph `…` (U+2026), never three periods
- **Dashes:** em dash `—` (U+2014) for breaks, hyphen `-` in compounds
- **Suffix labels:** `· cached`, `· 47 links`, `· 3 sections` — always space + dot + space + lowercase label
- **Counts in parentheses:** `Tech News (12 items)`, `NAVIGATION (12)`

**Keybinding format**

Always render key names in brackets with `[cyan]` and the action in `[med]` dim, separated by `:`:

```
[Enter]:open   [s]:save   [?]:help
```

Chord keys (`Ctrl+L`, `Ctrl+D`) are spelled out — no `⌘` or `^`.

**Length discipline**

- Toasts: single line, truncated if needed, 40-char max box
- Status bar hints: adaptive tiers (full → medium → minimal); shrink first, wrap never
- Error detail lines: one idea per line, wrapped at ~32 chars inside the modal box
- Article byline: `Author · Date · domain.com` — nothing else

**Sample copy lifted from the app**

- Empty bookmarks: `Your bookmarks await` / `Press [a] to add your first site`
- Empty collection: `Nothing saved yet — press s on any article to start your list`
- Loading: `Loading page...`, `Waiting for page...`, `Retrying with browser...`
- Celebration: `Podcast ready! 12 chapters · 2h 15m`
- End of article: `— end —`, `1,247 words · ~5 min read`

---

## Visual Foundations

### The one rule

Every pixel should look like it could be drawn by ANSI escape codes into a monospace terminal grid. No smooth gradients. No images. No anti-aliased shapes. No rounded UI pill buttons. The *chrome* of a page (the outer HTML document wrapping the terminal, in mockups) can have modern web affordances — rounded corners, card shadows — but the app itself is text-on-black.

### Colors

All colors map to ANSI 256. The palette has three layers:

1. **Green scale (primary)** — warm, pure-green family, no teal shift. `bright pink #ff87d7` titles, `#00d700` body, `#00af00` metadata, `#5f875f` muted, `#005f00` structural.
2. **Accent trio** — `#00ffff` cyan (*interactive keys only*), `#ffaf00` amber (warnings, progress), `#ff5f5f` red (errors).
3. **Celebration** — `#ff5fd7` hot pink. Rare. Only for achievement moments (podcast complete, first-run confetti). Using it on anything mundane breaks the brand.

There are four themes (**Phosphor** default, **Amber**, **Dracula**, **Light**) but the *semantic* roles stay constant. Phosphor is the canonical theme for all design work.

### Typography

Monospace, always. `--tr-font-mono` stack prefers Berkeley Mono, falls back through JetBrains Mono → Fira Code → SF Mono → system mono. **No webfonts are bundled** — the app lives inside a terminal, so it uses whatever font the user has set in Ghostty/iTerm/etc.

Display hierarchy in mockups is built from **color + weight + casing**, not font-size changes:

- Title: `--tr-bright` (#ff87d7) + bold
- Body: `--tr-green` (#00d700) + regular
- Meta: `--tr-med` (#00af00) + regular
- Hint: `--tr-muted` (#5f875f) + regular

Size only changes when you leave the terminal (e.g. a document-level `<h1>`).

### Backgrounds

- **App:** terminal default (black). The app never paints a full-screen background.
- **Selection:** ANSI 22 dark green (`#005f00`) — the only background the app ever paints, other than search hits.
- **Search hits:** ANSI 24 blue-teal (`#005f87`) with white text — the single non-green color used on a background.
- **Mockup page chrome (this design system):** near-black `#0a0a0a` with the terminal inset on pure `#000`.
- **No gradients. Ever.** Not in the app, not in the mockups.

### Spacing

Cell-based. `1 cell = 1 monospace character width ≈ 9px at 15px base font`. Line spacing is generous in mockup frames (1.7) so the grid reads clearly; in-app it's whatever the terminal renders (no CSS line-height to tune).

- Reader text: 2-char left indent
- Collection items: 3-char indent, 5-char sub-indent for domains
- Launcher cards: 6-char indent (badge + pad)
- Box padding inside rounded frames: 1 char each side, 0 vertical
- Status bar: always 2 lines (separator + content)

### Corner radii / borders

In-app: **box-drawing characters**, not CSS.

- `╭ ╮ ╰ ╯` rounded corners (U+256D, E, F, 2570)
- `─` horizontal, `│` vertical, `┼` cross
- Border color: `--tr-dim` (#005f00) for structural; `--tr-muted` for card separator rules

Document chrome (outside the terminal): 6px rounded corners, `1px solid #333`.

Cards **never** use drop shadows. Cards are built from:

- A left `▌` accent bar (when selected)
- A full-row selection background
- A separator line `─` below the last row

That's it. No border, no shadow, no inset glow.

### Animations

Animations are **synchronous** — they block input because the app has no render thread. Total duration must stay under **2 seconds**.

| Animation | Duration | Uses |
|---|---|---|
| Page-load decrypt reveal | 400 ms (8 frames × 50 ms) | Title resolves from `░▒▓` noise → random letters → correct text, sweeping left-to-right |
| Podcast celebration | 1600 ms total (flash → sparkle → typewriter → settle) | Only fires on successful podcast generation |
| Cache-warm color wave | 600 ms (10 frames × 60 ms) | Bright wave sweeps across the status-bar separator |
| Cache-item pulse | 300 ms (3 frames × 100 ms) | Progress count flashes cyan → green → settled |
| Breathing bar | ~3.6 s loop | Bot-challenge / login wait; `▏▎▍▌▋▊▉█` grows and shrinks |
| Layout flash | 500 ms | Layout name appears centered on `Ctrl+L`, then clears |

No easing curves — everything is frame-stepped with fixed `Thread.Sleep()`. No fades (alpha doesn't exist in the terminal), no bounces. Character or color swaps only.

### Hover / press states

There is **no mouse**. The equivalents are:

- **Focus (keyboard selection):** `▌` accent bar on the left + full-row `--tr-sel-bg` fill + text becomes `--tr-white`. This is the *only* focus treatment in the app.
- **Press (key pressed):** the toast, decrypt reveal, or color-wave animation fires; the key itself doesn't "depress" because it's in the key hint bar, not a button.

### Transparency, blur, shadows

**None.** The terminal doesn't composite layers, so neither do the mockups. Everything is opaque text on opaque black.

### Imagery

None. The app never shows an image. In this design system's HTML mockup pages, we use:

- Box-drawing ASCII blocks for layouts
- `▲▼▌█░▒▓` for indicators and progress
- Braille dots `⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏` for spinners

If a mockup needs a logo or illustration, it's rendered in ASCII.

### Layout rules (in-app)

- Usable width: `terminal_width - 2`
- Status bar is always fixed at the bottom: 2 lines
- Launcher + Link Tree: 2-column grid (1 column under 40/50 char widths)
- Collections + Collection Items + Reader: always 1 column
- Content is never horizontally scrollable — it wraps or truncates with `…`

### Transparency & blur

Not applicable. Terminals don't have z-layers.

### Color vibe of imagery

Phosphor-green CRT monochrome, warm pink highlights. Think: 1984 terminal that had a mild pink tint leak into the electron gun. Not neon. Not cyberpunk. Not Matrix-green `#00ff00`. The body green is deliberately *desaturated* (#00d700 not #00ff00) so long-form reading doesn't fatigue the eyes.

---

## Iconography

**The app has no icon font.** Everything is **Unicode glyphs** rendered as text at the terminal's cell size.

### Character vocabulary (use these, not emoji, not SVG)

| Glyph | Unicode | Role |
|---|---|---|
| `●` | U+25CF | Unread item marker (green) |
| `○` | U+25CB | Read item marker (dim green) |
| `▶` | U+25B6 | Collapsed section / podcast play icon (doubled: `▶▶`) |
| `▼` | U+25BC | Expanded section / scroll-down indicator |
| `▲` | U+25B2 | Scroll-up indicator |
| `▌` | U+258C | Selection accent bar (left-edge) |
| `▎` | U+258E | Reader focus indicator (1/4 block) |
| `★` | U+2605 | Default collection marker |
| `→` | U+2192 | Directional arrow |
| `✔` | U+2714 | Success checkmark |
| `✗` | U+2717 | Error |
| `✦` | U+2726 | Celebration sparkle (podcast complete) |
| `✧` | U+2727 | Celebration sparkle (secondary) |
| `♫` | U+266B | Podcast / audio indicator |
| `·` | U+00B7 | Metadata separator |
| `…` | U+2026 | Truncation |
| `⚡` | U+26A1 | Warning toast |
| `↩` | U+21A9 | Undo toast |
| `╭ ╮ ╰ ╯ ─ │` | U+256D–70, 2500, 2502 | Rounded box drawing |
| `┼ ╶ ╴` | U+253C, 2576, 2574 | Column divider, light item separator |
| `█ ▉ ▊ ▋ ▌ ▍ ▎ ▏` | U+2588–F | Eighth-block progress bars |
| `░ ▒ ▓` | U+2591–3 | Decrypt-reveal noise characters |
| `▰ ▱` | U+25B0, 25B1 | Cache progress (filled / empty) |
| `⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏` | U+2800 range | Braille spinner frames |
| `⠇` | U+2847 | Primary loading spinner glyph |

### Rules

1. **No emoji.** Terminals render them inconsistently; many mono fonts don't ship them.
2. **No SVG.** The app doesn't have a renderer for raster or vector graphics.
3. **No icon font.** Would add a webfont dependency; the terminal doesn't need it.
4. **Unicode only.** If you need a new icon, find one in the above blocks (Geometric Shapes, Block Elements, Box Drawing, Miscellaneous Symbols, Braille Patterns). If it doesn't exist as Unicode, it doesn't exist in TermReader.
5. **Color is the icon's state.** A `●` in green is unread; a `●` in `--tr-muted` is disabled. The glyph doesn't change.

### Logo

There is no raster logo. The brand mark is the wordmark `TermReader` set in bright phosphor green, optionally framed by a rounded box:

```
╭─ TermReader ─────────────────────────╮
│ A better reading experience          │
╰──────────────────────────────────────╯
```

See `assets/logo.txt` and `assets/logo.html`.

---

## Fonts — note to the user

**No webfonts are bundled.** The app is terminal-native, so it inherits the user's terminal font. The mockups in this design system use the user's system mono stack in this order: Berkeley Mono → JetBrains Mono → Fira Code → Cascadia Code → SF Mono → Menlo → Consolas → DejaVu Sans Mono.

If you want a canonical webfont for marketing pages / non-terminal surfaces, **JetBrains Mono** (Google Fonts, free) is the closest match to the Phosphor look and is what these mockups will resolve to on most machines. Let me know and I'll wire it into `fonts/` as a formal choice.

---

## Caveats

- The source repo uses C#/.NET; the UI is rendered as text to stdout. There is no React/JSX component tree upstream, so the UI Kit is a **visual recreation in React** faithful to the ASCII mockups and `design-system.md`, not a port of the renderer.
- All visuals are Unicode; no logo file exists in the repo, so the brand mark in `assets/` is my wordmark rendering.
- Theme variants (Amber, Dracula, Light) are documented in `design-system.md` but not recreated as preview cards here — Phosphor is the canonical theme. Ping me if you want the other three visualized.
