# Terminal reality check

Every component in this system has to be implementable inside a real terminal
emulator (target: Ghostty, but it should work on iTerm2 / xterm-256 too). That
imposes hard limits the web doesn't.

## Available

- **256 fg + 256 bg colors per cell** (ANSI 256). True-color (24-bit) is also
  available in modern terminals, but stick to ANSI 256 for portability.
- **Cell attributes:** bold, italic (font-dependent), underline, reverse video,
  blink (avoid), strikethrough.
- **Per-cell background fills** — block selections and multi-line washes are
  one bg color across N cells. No alpha.
- **Unicode** — box-drawing (`─│┌┐└┘├┤┬┴┼╭╮╰╯═║╔╗╚╝╠╣╦╩╬`), eighth-blocks
  (`▏▎▍▌▋▊▉█`), shade blocks (`░▒▓`), braille (`⠁⠂⠃…⢿⣿`), arrows, geometric
  shapes (`●○◆▶◀▲▼`), stars (`★☆✦✧`).
- **Animation** by **palette swap** — change a cell's fg/bg color on a tick.
  This is how cmatrix, htop's CPU bars, lolcat all work.
- **Animation** by **glyph swap** — change a cell's character on a tick.
  Spinners (`⠹⠸⠼⠴⠦⠧⠇⠏`), marching chevrons (`▶▶▶` shifted by one cell), blinking
  cursor, pulsing dots.
- **Sub-cell horizontal width** via eighth-blocks: a 10-cell bar can show 80
  distinct fill levels.

## NOT available

- ❌ **No alpha / opacity.** `rgba(…, 0.09)` does not exist. A cell either has
  bg color X or it doesn't.
- ❌ **No box-shadow, no glow, no blur.** A "pulsing border" must be drawn with
  `╔═╗` glyphs whose fg color cycles each tick.
- ❌ **No border-radius, no CSS borders.** Rounded corners are the literal
  characters `╭ ╮ ╰ ╯`. Square corners are `┌ ┐ └ ┘`.
- ❌ **No font-size variation.** Every cell is the same size. Hierarchy comes
  from color, weight (bold), case (UPPERCASE), and ASCII art. **A "big logo"
  is an ASCII-art block 5–7 rows tall, not a 42px font.**
- ❌ **No padding inside a cell.** No `padding: 6px` between glyph and border.
  Use empty cells (` `).
- ❌ **No CSS gradients.** Gradients are dithered with `░▒▓█` shade blocks if
  truly needed (rare — usually just don't).
- ❌ **No CSS transform / translate / rotate / scale.** A "marching" chevron
  shifts position by re-rendering it one cell to the right.
- ❌ **No images** unless you opt in to sixel or kitty graphics protocol. Don't
  rely on these for core UI; many users disable them.
- ❌ **No sub-pixel positioning.** Everything snaps to the cell grid.

## Translation table

When a web preview file uses something impossible, translate to:

| Web (preview)                           | Terminal (real)                                     |
|----------------------------------------|-----------------------------------------------------|
| `border: 1px solid #5fff5f`            | `┌─┐ │ └─┘` glyphs in `--tr-dim`                   |
| `border-radius: 10px`                  | `╭─╮ │ ╰─╯` glyphs                                  |
| `box-shadow: 0 0 18px rgba(...)`       | Border glyphs cycle fg color across N tick frames   |
| `animation: pulse … border-color`      | Same — palette swap on the border row glyphs       |
| `transform: translateX(3px)` (chevron) | Render `▶▶▶` shifted by one or more cells           |
| `background: rgba(0,215,0,0.10)` wash  | `--tr-sel-bg` (ANSI 22 `#005f00`) full-cell fill    |
| `font-size: 42px` (logo)               | ASCII-art glyph block, ~5 rows tall                 |
| `linear-gradient(...)` progress        | `▏▎▍▌▋▊▉█` eighth-block bar                         |
| `<svg>` icon                           | Unicode glyph: `★ ▶ ● ◆ ⚠ ✦ ↗`                       |
| `display: inline-block; padding: …`    | Pad with spaces to fixed cell width                 |

## Hierarchy without size

Since every cell is the same size, a "dominant" element earns dominance through:

1. **Negative space** — empty rows above and below it.
2. **Border weight** — `═══` double > `━━━` thick > `───` thin > nothing.
3. **Color saturation** — the only bright pink on a green page draws every eye.
4. **Reverse video** — flipping fg↔bg on a region is genuinely emphatic.
5. **ASCII-art scale** — a wordmark made from 5×7 cell glyphs feels "big"
   without changing font-size.
6. **Animation** — the only moving thing on the page is the dominant thing.
   Use sparingly: one element per screen.

## Authoring rules

- Build previews in HTML so contributors can see them in a browser.
- Every preview must be **achievable** with the rules above. If you reach for
  CSS borders, shadows, or font-size, stop and pick a terminal-native technique
  from the table.
- `colors_and_type.css` defines the palette in true-color hex; the comments
  next to each var note the ANSI 256 mapping. Implementations should use the
  ANSI mapping, not the hex.
