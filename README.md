# TermReader

A terminal-based web reader built with .NET/C#. Browse websites, read articles, manage collections, and generate podcasts -- all from your terminal.

## Features

- **Launcher** -- Bookmark grid with numbered quick-jump shortcuts and a URL bar
- **Link Tree** -- Browse a page's links in a categorized, collapsible tree (content, navigation, external, footer)
- **Reader View** -- Distraction-free article reading with adjustable width, search, and a focus indicator
- **Collections** -- Save articles to reading lists with read/unread tracking and per-item caching
- **Podcast Generation** -- Convert a reading list into an audio podcast via ElevenLabs TTS

## Themes

TermReader ships with four color themes, all built on ANSI 256 colors:

| Theme | Description |
|-------|-------------|
| **Phosphor** (default) | Green-on-black CRT aesthetic |
| **Amber** | Warm amber/gold monochrome |
| **Dracula** | Cool gray with cyan and pink accents |
| **Light** | Dark text on light background |

## Terminal UI

The interface uses Unicode box-drawing characters (`╭╮╰╯─│`), block elements for selection bars and progress indicators, and synchronous terminal animations (title decrypt reveals, sparkle celebrations, color wave sweeps). No external TUI framework is required -- rendering is done directly via ANSI escape sequences and `Console.Write`.

## Project Structure

```
src/
  TermReader.API/                  # CLI entry point
  TermReader.Application/          # Interfaces and use cases
  TermReader.Infrastructure/       # Implementation
    Browser/
      Themes/                      # ThemePalette, BuiltInThemes
      UI/
        Renderers/                 # Screen renderers (Launcher, LinkTree, Article, etc.)
        Components/                # Reusable UI components (Toast, Indicators)
        Animations/                # Sparkle, Decrypt, ColorWave, ProgressBar
```

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download) or later
- A terminal with ANSI 256-color support (most modern terminals)
- Unicode font support (for box-drawing and block characters)

## Build and Run

```bash
# Build
dotnet build

# Run
dotnet run --project src/TermReader.API
```

## Data Storage

Local data is stored under the platform-specific application data directory and is never committed to the repository:

| Platform | Path |
|----------|------|
| Windows | `%LOCALAPPDATA%\TermReader\` |
| Linux | `~/.local/share/TermReader/` |
| macOS | `~/.local/share/TermReader/` |

This includes the SQLite database, encrypted authentication cookies, encryption keys, and cached audio files.

## Key Bindings

### Launcher

| Key | Action |
|-----|--------|
| `Enter` | Open selected bookmark |
| `o` | Go to URL |
| `a` | Add bookmark |
| `d` | Delete bookmark |
| `1`-`9` | Jump to bookmark by number |
| `?` | Help |

### Link Tree

| Key | Action |
|-----|--------|
| `Enter` | Open link |
| `s` | Save to collection |
| `v` | Switch to reader view |
| `R` | Refresh page |
| `?` | Help |

### Reader View

| Key | Action |
|-----|--------|
| `h` / `l` | Decrease / increase content width |
| `s` | Save to collection |
| `o` | Open in system browser |
| `/` | Search |
| `v` | Switch to link tree |
| `b` | Go back |
| `?` | Help |

### Collections

| Key | Action |
|-----|--------|
| `Enter` | Open item |
| `d` | Remove item |
| `J` / `K` | Reorder items |
| `p` | Generate podcast |
| `b` | Go back |

### All Screens

| Key | Action |
|-----|--------|
| `Ctrl+L` | Cycle layout variant |
| `j` / `k` or arrows | Navigate |
| `q` | Quit |

## Layout Variants

Each screen supports alternative layouts, toggled with `Ctrl+L`:

- **Launcher**: Grid (2-column) or List (single-column)
- **Link Tree**: Cards (2-column) or Dense List (1 line per item)
- **Reader**: Comfortable (80-char width) or Full Width
- **Collection Items**: Standard (2-line) or Compact (1-line)

Layout preferences persist between sessions.

## Design System

A comprehensive design specification covering color palettes, spacing rules, component catalog, and animation specs is maintained in [`design-system.md`](design-system.md). Visual mockups for all screens are in the [`mockups/`](mockups/) directory.

## Issue Tracking (Beads)

This project uses [Beads](https://github.com/steveyegge/beads) for issue tracking. Issues are stored locally in the `.beads/` directory alongside the code -- no external service required.

### Setting up Beads on a new machine

```bash
# Install beads
curl -sSL https://raw.githubusercontent.com/steveyegge/beads/main/scripts/install.sh | bash

# Initialize (already done in this repo, but needed if .beads/ is missing)
bd init
```

### Common commands

```bash
bd ready                  # Show issues ready to work (no blockers)
bd list --status=open     # All open issues
bd list --status=in_progress  # Active work
bd show <id>              # Issue details with dependencies
bd create --title="Fix bug" --description="Details" --type=bug --priority=2
bd update <id> --status=in_progress   # Claim work
bd close <id>             # Mark complete
bd dep add <issue> <depends-on>       # Add dependency
bd blocked                # Show blocked issues
bd stats                  # Project health overview
```

### Notes

- The `.beads/` directory is committed to the repo so issues travel with the code
- Priority scale: P0 (critical) through P4 (backlog), default P2
- Issue IDs are short hashes like `docs-1p7` -- use them with any `bd` command
- Run `bd doctor` if something seems off

## License

See [LICENSE](LICENSE) for details.
