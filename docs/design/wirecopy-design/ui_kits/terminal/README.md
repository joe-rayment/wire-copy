# Wire Copy UI Kit — Terminal

Pixel-faithful recreation of the Wire Copy TUI as React/JSX components rendered inside a web `terminal frame`. Phosphor theme only.

## Files

- `index.html` — interactive click-thru demo: Launcher → Link Tree → Reader → Reading List with modal overlays (Loading, Toast, Error).
- `TerminalFrame.jsx` — 80×24 monospace grid, black background, `● ● ●` window chrome, handles the box + the 2-line status bar.
- `Launcher.jsx` — bookmark grid + URL bar + footer hints.
- `LinkTree.jsx` — 2-column link grid with group headers + cache progress.
- `Reader.jsx` — centered article column with focus indicator `▎` and search highlighting.
- `ReadingList.jsx` — collection items with podcast CTA slab.
- `Modals.jsx` — centered rounded boxes for loading / error / bot challenge.
- `Toast.jsx` — top-right overlay with info / celebration / error variants.
- `atoms.jsx` — tiny shared pieces (KeyHint, Badge, SelectionRow, Separator).

## Notes

- Strictly Unicode glyphs (no SVG, no emoji).
- Every color comes from `colors_and_type.css` tokens.
- State in `index.html` simulates the keyboard flow: press `1`–`6` / `Enter` / `v` / `b` / `s` / `p` / `?` or click items.
- Not wired to real navigation — just demonstrates the visual vocabulary.
