# TermReader

A .NET 9 terminal-based web browser with Helix-style keybindings and an integrated audio pipeline for generating audiobooks from web articles.

Browse any website from your terminal with keyboard-only navigation, reader view for distraction-free reading, and optional text-to-speech conversion to M4B audiobook files.

> **Note**: When scraping websites, always respect robots.txt and Terms of Service. This project is for educational and personal use.

## Quick Start

```bash
git clone https://github.com/joe-rayment/newspaper_reader.git
cd newspaper_reader
dotnet build
dotnet run --project src/TermReader.API
```

## Features

- **Launcher** -- Bookmark grid with numbered quick-jump shortcuts and a URL bar
- **Link Tree** -- Browse a page's links in a categorized, collapsible tree (content, navigation, external, footer)
- **Reader View** -- Distraction-free article reading with adjustable width, search, and a focus indicator
- **Collections** -- Save articles to reading lists with read/unread tracking and per-item caching
- **Podcast Generation** -- Convert a reading list into an audio podcast via ElevenLabs TTS
- **Helix-style keybindings** (j/k, h/l, gg/G) for fast keyboard navigation
- **In-page search** with `/` to find text, `n`/`N` to jump between matches
- **Page caching** for instant back/forward navigation
- **Smart link classification** groups links into content, navigation, and footer sections
- **Selenium fallback** for JavaScript-heavy sites, with WebDriver session reuse

## Themes

TermReader ships with four color themes, all built on ANSI 256 colors:

| Theme | Description |
|-------|-------------|
| **Phosphor** (default) | Green-on-black CRT aesthetic |
| **Amber** | Warm amber/gold monochrome |
| **Dracula** | Cool gray with cyan and pink accents |
| **Light** | Dark text on light background |

## Keybindings

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
| `j` / `k` or arrows | Navigate links |
| `h` / `l` | Collapse / expand sections |
| `Enter` | Follow selected link |
| `s` | Save to collection |
| `v` | Switch to reader view |
| `R` | Refresh page |
| `b` | Go back |
| `/` | Search |
| `?` | Help |

### Reader View

| Key | Action |
|-----|--------|
| `j` / `k` or arrows | Scroll |
| `h` / `l` | Decrease / increase content width |
| `Ctrl+d` / `Ctrl+u` | Page down / up |
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
| `gg` / `G` | Jump to top / bottom |
| `q` | Quit |
| `?` | Help |

## Layout Variants

Each screen supports alternative layouts, toggled with `Ctrl+L`:

- **Launcher**: Grid (2-column) or List (single-column)
- **Link Tree**: Cards (2-column) or Dense List (1 line per item)
- **Reader**: Comfortable (80-char width) or Full Width
- **Collection Items**: Standard (2-line) or Compact (1-line)

Layout preferences persist between sessions.

## Audio Mode

Generate audiobook files from scraped articles using Eleven Labs text-to-speech.

### Setup

```bash
cd src/TermReader.API
dotnet user-secrets init
dotnet user-secrets set "ElevenLabs:ApiKey" "your-api-key"
```

### Cost Management

Eleven Labs charges ~$0.30 per 1,000 characters. The application estimates costs before generating audio, enforces configurable budget limits, and caches generated audio to avoid regeneration.

## Authentication

For sites requiring login, provide a session cookie on first run:

1. Open Chrome and navigate to the site
2. Log in to your account
3. Open DevTools (F12) > Application tab > Cookies
4. Copy the session cookie value
5. Paste when prompted

The cookie is encrypted and stored locally for future sessions.

## Configuration

Configuration is loaded from `appsettings.json` and can be overridden with environment variables or user secrets.

| Setting | Description | Default |
|---------|-------------|---------|
| `ElevenLabs:ApiKey` | Eleven Labs API key | (required for audio) |
| `ElevenLabs:VoiceId` | Voice ID for narration | `21m00Tcm4TlvDq8ikWAM` |
| `Audio:OutputDirectory` | Audio file output directory | `./output` |
| `Audio:BudgetLimitUsd` | Max spend per session | `5.00` |
| `Browser:Headless` | Run Chrome headless | `false` |

## Project Structure

```
src/
├── TermReader.Domain/          # Core entities and value objects
├── TermReader.Application/     # Service interfaces and DTOs
├── TermReader.Infrastructure/  # External integrations
│   ├── Browser/                # Page loading, link extraction, reader view
│   │   └── UI/                 # Terminal renderer, input handler
│   ├── Audio/                  # ElevenLabs, FFmpeg, chapter markers
│   └── Configuration/          # Settings and validation
└── TermReader.API/             # Console application entry point

tests/
└── TermReader.Tests/           # Unit and integration tests
```

## Development

```bash
# Build
dotnet build

# Run tests
dotnet test

# Code formatting
dotnet format
```

## Docker

```bash
docker build -t termreader:latest .
docker run --rm \
  -e ELEVEN_LABS_API_KEY="your-api-key" \
  -v $(pwd)/output:/app/output \
  termreader:latest
```

## Design System

A comprehensive design specification covering color palettes, spacing rules, component catalog, and animation specs is maintained in [`design-system.md`](design-system.md). Visual mockups for all screens are in the [`mockups/`](mockups/) directory.

## Issue Tracking (Beads)

This project uses [Beads](https://github.com/steveyegge/beads) for issue tracking. Issues are stored locally in the `.beads/` directory alongside the code -- no external service required.

### Setting up Beads on a new machine

1. Install the `bd` binary from [github.com/steveyegge/beads](https://github.com/steveyegge/beads) (download the release for your platform and place it on your `PATH`)
2. The `.beads/` directory is already in the repo -- no `bd init` needed after cloning

### Common commands

```bash
bd ready                  # Show issues ready to work (no blockers)
bd list --status=open     # All open issues
bd show <id>              # Issue details with dependencies
bd create --title="Fix bug" --description="Details" --type=bug --priority=2
bd update <id> --status=in_progress   # Claim work
bd close <id>             # Mark complete
bd dep add <issue> <depends-on>       # Add dependency
bd stats                  # Project health overview
```

### Notes

- The `.beads/` directory is committed to the repo so issues travel with the code
- Priority scale: P0 (critical) through P4 (backlog), default P2
- Issue IDs are short hashes like `docs-1p7` -- use them with any `bd` command
- Run `bd doctor` if something seems off

## Technology Stack

- **.NET 9.0** with C# 12
- **Selenium WebDriver** for JavaScript-heavy site fallback
- **HtmlAgilityPack** for HTML parsing and content extraction
- **ElevenLabs-DotNet** for text-to-speech
- **FFMpegCore** for audio processing
- **ATL.NET** for M4B chapter markers
- **Serilog** for structured logging
- **Polly** for retry/resilience

## License

Educational use only.
