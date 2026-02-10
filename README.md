# TermReader

A .NET 9 terminal-based web browser with Helix-style keybindings and an integrated audio pipeline for generating audiobooks from web articles.

Browse any website from your terminal with keyboard-only navigation, reader view for distraction-free reading, and optional text-to-speech conversion to M4B audiobook files.

> **Note**: When scraping websites, always respect robots.txt and Terms of Service. This project is for educational and personal use.

## Quick Start

```bash
git clone https://github.com/joe-rayment/newspaper_reader.git
cd newspaper_reader
dotnet build
dotnet run --project src/TermReader.API -- browse https://news.ycombinator.com
```

## Features

- **Terminal web browser** with hierarchical link display and reader view
- **Helix-style keybindings** (j/k, h/l, gg/G) for fast keyboard navigation
- **Reader view** strips ads, navigation, and clutter for clean article text
- **In-page search** with `/` to find text, `n`/`N` to jump between matches
- **URL command mode** with `:` to navigate to any URL without restarting
- **Page caching** for instant back/forward navigation
- **Smart link classification** groups links into content, navigation, and footer sections
- **Selenium fallback** for JavaScript-heavy sites, with WebDriver session reuse
- **Audio pipeline** generates M4B audiobook files with chapter markers via Eleven Labs TTS

## Keybindings

### Navigation

| Key | Action |
|-----|--------|
| `j` / `Down` | Move to next link |
| `k` / `Up` | Move to previous link |
| `h` / `Left` | Collapse section or go back |
| `l` / `Right` | Expand section |
| `Enter` | Follow selected link |
| `b` / `Backspace` | Go back to previous page |
| `Space` | Toggle expand/collapse current section |
| `gg` | Jump to top |
| `G` | Jump to bottom |
| `Ctrl+d` | Page down |
| `Ctrl+u` | Page up |

### Views and Search

| Key | Action |
|-----|--------|
| `v` / `Tab` | Toggle between link view and reader view |
| `r` | Switch to reader view |
| `t` | Switch to link tree view |
| `:` | Open URL command line (type a URL and press Enter) |
| `/` | Search within current view |
| `n` | Next search match |
| `N` | Previous search match |

### General

| Key | Action |
|-----|--------|
| `q` / `Escape` | Quit browser |
| `F5` | Refresh current page |
| `?` | Show help |
| `Ctrl+C` | Force quit |

## Tested Sites

| Site | Notes |
|------|-------|
| news.ycombinator.com | Link tree works well, reader view on articles |
| example.com | Simple pages render correctly |
| macleans.ca | Selenium fallback for JS content |
| nytimes.com | Requires authentication cookie |

## Audio Mode

Generate audiobook files from scraped articles using Eleven Labs text-to-speech.

### Setup

```bash
cd src/TermReader.API
dotnet user-secrets init
dotnet user-secrets set "ElevenLabs:ApiKey" "your-api-key"
```

### Usage

```bash
# Scrape and generate audio for 5 articles
dotnet run --project src/TermReader.API -- --count 5

# Scrape only (no audio generation)
dotnet run --project src/TermReader.API -- --count 5 --scrape-only

# Show help
dotnet run --project src/TermReader.API -- --help
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
