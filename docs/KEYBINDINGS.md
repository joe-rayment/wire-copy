# WireCopy — complete keyboard reference

Every binding in the app, by context. This is the exhaustive list (more than the
in-app `?` help). Bindings are defined in
`src/WireCopy.Infrastructure/Browser/UI/TerminalInputHandler.cs`; each screen's
handler decides which commands it honours, so a key can do different things in
different screens (the **Overloaded keys** section lists those).

> **⟳ Changed in the keybinding pass (`workspace-1dmr`)** rows are flagged `⟳`.
> Design rules applied: prefer Helix overlap; a capital (`Shift+`) letter is only
> used when its lowercase does a *related* thing; no `Ctrl+` combo that fights a
> universal terminal convention.

---

## Motion & view — work in every list/tree/reader screen

| Key | Action | Helix |
|-----|--------|-------|
| `j` / `k` (or `↓` / `↑`) | Down / up | ✓ |
| `h` / `l` (or `←` / `→`) | Collapse / expand group (in reader: narrow / widen) | ✓ |
| `gg` / `G` | Jump to top / bottom | ✓ (`gg`; `G` is vim, Helix uses `ge`) |
| `Ctrl+D` / `Ctrl+U` | Half-page down / up | ✓ |
| `{` / `}` | Paragraph up / down | — |
| `5j`, `10G` | Count-prefixed motion | ✓ |
| `Enter` | Activate / open the selected item | — |
| `b` / `Backspace` / `Esc` | Go back (history) | — |
| `Shift+B` | Go forward (history) | — | ⟳ *was the dead `Shift+L`* |
| `v` / `Tab` | Switch view (tree ⇄ reader) | — |
| `r` / `t` | Reader view / tree view | — |
| `:` | Command line (see **Command line** below) | ✓ (`:`) |
| `/` · `n` · `N` | Search · next · prev match | ✓ |
| `?` | Help (this list, in-app) | — |
| `Ctrl+P` | Cycle theme | (collides w/ "previous"/print — left as-is) |
| `q` / `Ctrl+C` | Quit | ✓ (`Ctrl+C`) |

---

## Link tree (a site's links)

| Key | Action | |
|-----|--------|--|
| `Enter` | Follow the selected link | |
| `Space` | Toggle expand/collapse · toggle multi-select | |
| `s` / `Shift+S` | Save selected to default list / to a chosen list | |
| `A` | Save **all** visible links to a list | *(capital kept: in the `s`/`S` save family)* |
| `g l` | Set up site layout (AI wizard) — also `:layout` | ⟳ *was `Ctrl+L`* |
| `|` | Dock / undock the live browser sidecar | ⟳ *was `Shift+O`* |
| `y` | Adopt the sidecar's current page into the app | |
| `Shift+R` | Force refresh (bypass cache) | |
| `Shift+I` | Interactive refresh (log in by hand in the browser) | |
| `\` | Prefetch progress panel | |

## Reader view (an article)

| Key | Action | |
|-----|--------|--|
| `j` / `k`, `Ctrl+D` / `Ctrl+U` | Scroll · page | |
| `[` / `]` / `0` | Narrow / widen / reset width | |
| `e` | Regenerate article layout (re-run AI extractor) | ⟳ *was `Shift+E`* |
| `Shift+E` | Tune article layout visually (headline / body / ignore) | ⟳ *was `L`* — now an `e`/`E` pair |
| `f` · `<` / `>` | Speed-read on/off · slower / faster | |
| `s` / `o` | Save to list / open in system browser | |
| `|` | Dock / undock the live browser sidecar | ⟳ |

## Reading list

| Key | Action |
|-----|--------|
| `Enter` / `d` | Open / remove item |
| `Shift+J` / `Shift+K` | Reorder items (pairs with `j`/`k`) |
| `Shift+X` | Clear the whole list (with confirmation) |
| `p` / `Shift+P` | Generate podcast / restore a background run |

## Launcher (bookmark grid)

| Key | Action |
|-----|--------|
| `Enter` | Open the selected bookmark |
| `1`–`9` | Jump to bookmark by number |
| `o` | Open the URL bar |
| `a` / `d` | Add / delete bookmark |
| `Shift+J` / `Shift+K` | Reorder bookmarks |
| `c` | Setup / settings (**not** Collections here — see overloads) |
| `:rename` | Rename the selected bookmark |
| `Ctrl+P` | Cycle theme |

---

## Setup wizard (after `g l`)

| Key | Action |
|-----|--------|
| `↑` / `↓` (or `k` / `j`) | Preview the previous / next section |
| `Enter` | Save the layout exactly as shown |
| `Space` | Refine — point at the lead, or type a plain-English change |
| `z` | Undo the last refine (restore the previous layout) |
| `Esc` | Discard, leave the site unconfigured |

## Article-layout tuner (after `Shift+E`)

| Key | Action |
|-----|--------|
| `j` / `k` (or `↓` / `↑`) | Next / previous candidate element |
| `Space` | Cycle the element's role (headline / body / ignore) |
| `Enter` | Confirm and save |
| `Esc` / `q` | Cancel |

---

## Command line (`:` …)

Open with `:`. Available everywhere.

| Command | Action |
|---------|--------|
| `:<url>` or `:open <url>` / `:go` / `:o` | Open a URL |
| `:back` / `:b`, `:forward` / `:f`, `:home` | History back / forward / home |
| `:add` | Add the current page as a bookmark |
| `:rename <name>` | Rename the selected bookmark |
| `:collections` / `:readlater` | Open reading lists |
| `:new <name>` | Create a reading list |
| `:clear`, `:export`, `:cache` | Clear / export a list · cache controls |
| `:podcast` | Podcast generation |
| `:schedule` / `:schedules` | Recurring-episode recipes |
| `:layout` | Set up site layout (AI) — same as `g l` |
| `:layout clear` | Forget the saved link-list layout for this site |
| `:layout reset` | Forget the tuned/AI **article** selectors for this site |
| `:reanalyze` | Re-run layout analysis |
| `:dump` / `:dump-html` | Dump raw page HTML (debug) | ⟳ *was the `D` key* |
| `:config`, `:set <k> <v>`, `:settings` | Configuration |
| `:cred` / `:credentials`, `:cookies` | Paywall login credentials / cookies |
| `:help` / `:h`, `:quit` / `:q` | Help · quit |

---

## Mouse

| Action | Effect |
|--------|--------|
| Scroll wheel | Scroll / move selection (SGR mouse sequences) |

---

## Overloaded keys (same key, context-dependent)

| Key | Meaning by screen |
|-----|-------------------|
| `c` | **Launcher** → Setup/settings · **elsewhere** → open Collections |
| `d` | **Launcher** → delete bookmark · **list** → delete collection · **items** → remove item |
| `h` / `l` | **tree** → collapse/expand · **reader** → narrow/widen width · **launcher** → grid left/right |
| `Space` | **tree** → expand/select · **reader** → toggle speed-read · **wizard** → refine |
| `o` | **launcher** → URL bar · **tree** → open selected link · **reader** → open page in system browser |

---

## Notes / known rough edges

- `c` doing two different things (Setup vs Collections) is a genuine overload — left
  unchanged in this pass; worth a future split.
- `Ctrl+P` = cycle theme keeps the "previous/print" connotation; left as-is.
- `Shift+R` / `Shift+I` (refresh / interactive-refresh) are a mutually-related
  capital pair, so they satisfy the "capital only if paired" rule; `A` (save-all)
  lives in the `s`/`S` save family.
- The app has **no user-remappable keybinding config** — all bindings are in code.
