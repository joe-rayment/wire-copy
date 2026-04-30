# WireCopy

A .NET 9 terminal-based web browser with Helix-style keybindings, a distraction-free reader view, and an optional pipeline that turns saved articles into M4B audiobooks.

Browse any website from your terminal with keyboard-only navigation, save articles to reading lists, and (optionally) generate narrated audio with chapter markers from your collections.

> **Note:** When scraping websites, always respect robots.txt and Terms of Service. This project is for educational and personal use.

## Quick Start

```bash
git clone https://github.com/joe-rayment/wire-copy.git
cd wire-copy
dotnet build
dotnet run --project src/WireCopy.API
```

See [docs/SETUP.md](docs/SETUP.md) for full setup, including credential configuration.

## Features

- **Launcher** — Bookmark grid with numbered quick-jump shortcuts and a URL bar
- **Link Tree** — Browse a page's links in a categorized, collapsible tree (content, navigation, external, footer)
- **Reader View** — Distraction-free article reading with adjustable width, search, and a focus indicator
- **Collections** — Save articles to reading lists with read/unread tracking and per-item caching
- **Podcast Generation** — Convert a reading list into a narrated M4B with chapter markers
- **Helix-style keybindings** (`j`/`k`, `h`/`l`, `gg`/`G`) for fast keyboard navigation
- **In-page search** with `/` to find text, `n`/`N` to jump between matches
- **Page caching** for instant back/forward navigation
- **Smart link classification** groups links into content, navigation, and footer sections
- **Anti-detection browsing** via Patchright (patched Playwright) for sites with bot protection

## Themes

WireCopy ships with four color themes, all built on ANSI 256 colors:

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

## Audio / Podcast Mode

Generate narrated M4B files from your saved articles. Two API keys are required:

- **OpenAI** — text-to-speech (`tts-1`, `nova` voice by default)
- **Anthropic** — page-structure analysis for cleaner article extraction

```bash
cd src/WireCopy.API
dotnet user-secrets init
dotnet user-secrets set "OpenAiTts:ApiKey" "sk-..."
dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."
```

Cloud publishing of podcast feeds via Google Cloud Storage is supported but optional — see [docs/cookie-encryption.md](docs/cookie-encryption.md) and [docs/data-storage.md](docs/data-storage.md) for details on credential handling.

### Cost management

Both APIs enforce per-session budget limits configured in `appsettings.json` (`OpenAiTts:MaxBudgetUsd`, `Anthropic:MaxBudgetUsd`). Generated audio is cached on disk to avoid regeneration when re-running with the same content.

## Authentication for paywalled sites

For sites requiring login, WireCopy supports paste-once session cookies that are encrypted at rest with ASP.NET DataProtection. See [docs/cookie-encryption.md](docs/cookie-encryption.md) for the flow.

## Configuration

Configuration is loaded from `appsettings.json` and can be overridden with environment variables, `dotnet user-secrets`, or a local `secrets.json` (gitignored — see [`secrets.json.example`](secrets.json.example)).

| Setting | Description | Default |
|---------|-------------|---------|
| `OpenAiTts:ApiKey` | OpenAI API key | (required for audio) |
| `OpenAiTts:Voice` | TTS voice | `nova` |
| `OpenAiTts:MaxBudgetUsd` | Max spend per session | `1.00` |
| `Anthropic:ApiKey` | Anthropic API key | (required for audio) |
| `Anthropic:Model` | Claude model for analysis | see `appsettings.json` |
| `Anthropic:MaxBudgetUsd` | Max spend per session | `0.10` |
| `Browser:Headless` | Run browser headless | `false` |
| `Browser:ImplicitWaitSeconds` | Page-element timeout | `30` |
| `Podcast:Title` | Podcast feed title | `WireCopy Podcast` |

## Project Structure

```
src/
├── WireCopy.Domain/          # Entities (Bookmarks, Browser, Collections, Credentials)
├── WireCopy.Application/     # Service interfaces and DTOs
├── WireCopy.Persistence/     # EF Core DbContext, repositories, UnitOfWork
├── WireCopy.Infrastructure/  # External integrations
│   ├── Browser/                # Patchright automation, link extraction, reader view
│   │   ├── UI/                 # Terminal renderer, input handler
│   │   └── Cache/              # Page and content caches
│   ├── Podcast/                # OpenAI TTS, FFmpeg, M4B chapter markers, GCS publishing
│   └── Configuration/          # Options classes and validators
└── WireCopy.API/             # Console application entry point

tests/
└── WireCopy.Tests/           # Unit and integration tests, organized by feature area

docs/                           # Setup, testing, architecture, cookie encryption, design
```

## Development

```bash
# Build
dotnet build

# Fast unit tests (~15s)
./scripts/test.sh

# Full suite including integration tests (~90s)
./scripts/test.sh --all

# Format
dotnet format
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for development conventions.

## Docker

```bash
docker build -t wirecopy:latest .
docker run --rm \
  -e OpenAiTts__ApiKey="sk-..." \
  -e Anthropic__ApiKey="sk-ant-..." \
  -v $(pwd)/output:/app/output \
  wirecopy:latest
```

## Design

A comprehensive design specification covering color palettes, spacing rules, component catalog, and animation specs is maintained in [`docs/design/design-system.md`](docs/design/design-system.md). Visual mockups for all screens are in [`docs/design/mockups/`](docs/design/mockups/).

## Issue Tracking (Beads)

This project uses [Beads](https://github.com/steveyegge/beads) for issue tracking. Issues are stored locally in the `.beads/` directory alongside the code — no external service required. Beads is optional for contributors; you can also open standard GitHub issues.

```bash
bd ready                  # Issues ready to work on
bd list --status=open     # All open issues
bd show <id>              # Issue details
bd create --title="..." --description="..." --type=bug --priority=2
bd update <id> --status=in_progress
bd close <id>
```

## Technology Stack

- **.NET 9.0** with C# 12
- **[Patchright](https://github.com/Kaliiiiiiiiii-Vinyzu/patchright-dotnet)** — patched Playwright for .NET (CDP-leak patched, ARM64-native)
- **HtmlAgilityPack** for HTML parsing and content extraction
- **OpenAI .NET SDK** for text-to-speech
- **Anthropic .NET SDK** for page-structure analysis
- **FFMpegCore** for audio processing
- **z440.atl.core** (ATL.NET) for M4B chapter markers
- **Entity Framework Core 9** + **SQLite** for local persistence
- **ASP.NET DataProtection** for cookie / credential encryption at rest
- **Google.Cloud.Storage** for optional podcast feed publishing
- **Terminal.Gui 2** for the terminal UI shell
- **Serilog** for structured logging

## License

[MIT](LICENSE) — see the LICENSE file.
