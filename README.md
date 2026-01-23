# NYT Audio Scraper

A .NET 9 application that scrapes articles from the New York Times Today's Paper, generates high-quality audio narration using Eleven Labs, and creates audiobook-style M4B files with chapter markers for mobile playback.

> **Legal Notice**: The New York Times prohibits automated scraping in their robots.txt and Terms of Service. This project is for **educational purposes and personal subscriber use only**. Do not redistribute scraped content.

## Features

- Scrapes articles from NYT Today's Paper with anti-detection measures
- Generates natural-sounding audio using Eleven Labs text-to-speech
- Creates M4B audiobook files with chapter markers (iOS/Android compatible)
- SQLite database for tracking scraped articles
- Budget management for API costs
- Cookie-based authentication with encrypted storage

## Requirements

- .NET 9.0 SDK
- Chrome browser (for Selenium automation)
- FFmpeg (for audio processing)
- Eleven Labs API key
- NYT subscription (for authenticated access)

## Quick Start

### Audio Mode (Audio Scraper)

#### 1. Clone and Build

```bash
git clone https://github.com/joe-rayment/newspaper_reader.git
cd newspaper_reader
dotnet restore
dotnet build
```

#### 2. Configure Credentials

```bash
cd src/NYTAudioScraper.API
dotnet user-secrets init
dotnet user-secrets set "ElevenLabs:ApiKey" "your-api-key"
```

#### 3. Run

```bash
# Scrape and generate audio for 5 articles
dotnet run --project src/NYTAudioScraper.API -- --count 5

# Scrape only (no audio generation)
dotnet run --project src/NYTAudioScraper.API -- --count 5 --scrape-only

# Show help
dotnet run --project src/NYTAudioScraper.API -- --help
```

### Browser Mode (Terminal-Based Web Browser) 🚀 NEW

Navigate the web with keyboard-only controls in your terminal using Helix-style vim keybindings.

#### Quick Start

```bash
# Launch browser (will prompt for URL)
dotnet run --project src/NYTAudioScraper.API browse

# Enter URL when prompted:
# Enter URL: https://wikipedia.org
```

#### Navigation Keybindings

| Key | Action | Description |
|-----|--------|-------------|
| `j` | Move down | Next link in tree |
| `k` | Move up | Previous link in tree |
| `h` | Collapse/Back | Collapse node or navigate back |
| `l` | Expand/Forward | Expand node or navigate forward |
| `Enter` | Select | Follow selected link |
| `v` or `Tab` | Toggle view | Switch between link tree and reader view |
| `Space` | Toggle collapse | Expand/collapse current section |
| `b` | Back | Navigate to previous page |
| `Ctrl+d` | Page down | Scroll down half page |
| `Ctrl+u` | Page up | Scroll up half page |
| `q` | Quit | Exit browser |

#### Features

- **Hierarchical Link Display**: Main content links expanded, navigation menus collapsed
- **Reader Mode**: Clean article text without ads, navigation, or clutter
- **Smart Link Classification**: Automatically categorizes links by context
- **History Tracking**: Navigate back/forward through browsing history
- **Vim-like Navigation**: Build muscle memory with Helix editor-style keybindings

#### Example Session

```bash
$ dotnet run --project src/NYTAudioScraper.API browse

Enter URL: https://news.ycombinator.com

# Link view displays:
▼ Main Content (30 stories)
  → [1] Show HN: I built a terminal web browser
  → [2] Ask HN: Best practices for keyboard navigation
  ...

▶ Navigation (8 links)

# Press 'j' to move down, Enter to open story
# Press 'v' to switch to reader view for clean article text
# Press 'b' to go back to previous page
```

### 4. Authentication

On first run, you'll be prompted to provide your NYT-S cookie:

1. Open Chrome and go to https://www.nytimes.com
2. Log in to your NYT account
3. Open DevTools (F12) → Application tab → Cookies
4. Copy the value of the `NYT-S` cookie
5. Paste when prompted

The cookie is encrypted and stored locally for future sessions.

## Configuration

Configuration is loaded from `appsettings.json` and can be overridden with environment variables or user secrets.

| Setting | Description | Default |
|---------|-------------|---------|
| `ElevenLabs:ApiKey` | Your Eleven Labs API key | (required) |
| `ElevenLabs:VoiceId` | Voice ID for narration | `21m00Tcm4TlvDq8ikWAM` |
| `Audio:OutputDirectory` | Where to save audio files | `./output` |
| `Audio:BudgetLimitUsd` | Max spend per session | `5.00` |
| `Browser:Headless` | Run Chrome headless | `false` |

## Project Structure

```
src/
├── NYTAudioScraper.Domain/          # Core entities and value objects
├── NYTAudioScraper.Application/     # Service interfaces and DTOs
├── NYTAudioScraper.Infrastructure/  # External integrations
│   ├── Browser/                     # Selenium scraper, authentication
│   ├── Audio/                       # ElevenLabs, FFmpeg, chapter markers
│   ├── Persistence/                 # SQLite database (EF Core)
│   └── Configuration/               # Settings and validation
└── NYTAudioScraper.API/             # Console application entry point

tests/
└── NYTAudioScraper.Tests/           # Unit and integration tests
```

## Database

Articles are stored in SQLite at:
- **macOS**: `~/Library/Application Support/NYTAudioScraper/nytaudioscraper.db`
- **Linux**: `~/.local/share/NYTAudioScraper/nytaudioscraper.db`
- **Windows**: `%LOCALAPPDATA%\NYTAudioScraper\nytaudioscraper.db`

Query articles:
```bash
sqlite3 "$HOME/Library/Application Support/NYTAudioScraper/nytaudioscraper.db" \
  "SELECT Title, Section, PublishedDate FROM Articles ORDER BY ScrapedDate DESC;"
```

## Development

### Run Tests

```bash
dotnet test
```

### Code Formatting

```bash
dotnet format
```

### Docker

```bash
docker build -t nyt-audio-scraper:latest .
docker run --rm \
  -e ELEVEN_LABS_API_KEY="your-api-key" \
  -v $(pwd)/output:/app/output \
  nyt-audio-scraper:latest
```

## Cost Management

Eleven Labs charges ~$0.30 per 1,000 characters. The application:
- Estimates costs before generating audio
- Enforces configurable budget limits
- Caches generated audio to avoid regeneration
- Logs character counts and costs per session

## Technology Stack

- **.NET 9.0** with C# 12
- **Selenium WebDriver** for browser automation
- **ElevenLabs-DotNet** for text-to-speech
- **FFMpegCore** for audio processing
- **ATL.NET** for M4B chapter markers
- **Entity Framework Core** with SQLite
- **Serilog** for structured logging
- **Polly** for retry/resilience

## License

Educational use only. Not affiliated with The New York Times.
