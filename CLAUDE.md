# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

NYT Audio Scraper - A C# .NET 8 application that scrapes articles from NYT Today's Paper, generates high-quality audio using Eleven Labs, and creates audiobook-style M4B files with chapter markers for mobile playback.

**Critical Legal Notice**: NYT explicitly prohibits automated scraping in both robots.txt and Terms of Service. This project is for educational purposes and personal subscriber use only.

## Architecture

**Pattern**: Clean Architecture with Vertical Slices
- Clean Architecture for core domain logic (article parsing, audio processing)
- Vertical Slices for feature workflows (scrape → generate → package)

### Project Structure

```
src/
├── Domain/                      # Core business logic
│   ├── Entities/               # Article, AudioChapter, ScrapingSession
│   └── ValueObjects/           # ArticleContent, AudioMetadata
├── Application/                 # Use cases & features
│   ├── Interfaces/             # IScraperService, IAudioGenerator, etc.
│   ├── Features/               # ScrapeArticles/, GenerateAudio/, CreateAudiobook/
│   └── DTOs/
├── Infrastructure/              # External integrations
│   ├── Browser/                # Selenium scraper with anti-detection
│   ├── Audio/                  # ElevenLabs, FFmpeg, ATL.NET
│   ├── Storage/                # File management
│   └── Authentication/         # NYT authentication
└── API/                        # Entry point (Program.cs)

tests/
├── Domain.UnitTests/
├── Application.UnitTests/
├── Infrastructure.UnitTests/
│   └── Fixtures/               # sample-nyt-page.html, mock data
└── Integration.Tests/
```

## Technology Stack

### Core
- .NET 8.0 (C# 12)
- MediatR (CQRS/command handling)
- FluentValidation (input validation)

### Browser Automation
- **Selenium.WebDriver** with UndetectedChromeDriver (anti-detection measures)
- Run in headed mode with human-like delays (2-5 seconds between actions)
- Respectful rate limiting: max 1 request per 3-5 seconds

### Audio
- **ElevenLabs-DotNet** (text-to-speech generation)
- **FFMpegCore** (audio processing, M4B conversion)
- **ATL.NET** (chapter markers - GitHub: Zeugma440/atldotnet)
- Audio format: M4B, AAC codec, 64kbps mono, ~28 MB/hour

### Logging & Resilience
- Serilog (structured logging)
- Polly (retry logic, circuit breaker)

### Testing
- xUnit (test framework)
- NSubstitute (mocking)
- FluentAssertions (readable assertions)
- Bogus (test data generation)

## Common Commands

### Development
```bash
# Restore dependencies
dotnet restore

# Build
dotnet build --configuration Release

# Run application
dotnet run --project src/NYTAudioScraper

# Run tests
dotnet test

# Run unit tests only
dotnet test --filter Category=Unit

# Run integration tests only
dotnet test --filter Category=Integration

# Code formatting
dotnet format
```

### Configuration (User Secrets)
```bash
# Initialize user secrets
dotnet user-secrets init

# Set credentials
dotnet user-secrets set "NYT:Email" "your-email@example.com"
dotnet user-secrets set "NYT:Password" "your-password"
dotnet user-secrets set "ElevenLabs:ApiKey" "your-api-key"
```

### Docker
```bash
# Build image
docker build -t nyt-audio-scraper:latest .

# Run container
docker run --rm \
  -e NYT_EMAIL="your-email@example.com" \
  -e NYT_PASSWORD="your-password" \
  -e ELEVEN_LABS_API_KEY="your-api-key" \
  -v $(pwd)/output:/app/output \
  nyt-audio-scraper:latest

# Docker Compose
docker-compose up
```

## Key Implementation Considerations

### Anti-Detection Measures
- Use Selenium with UndetectedChromeDriver (Playwright easily detected via CDP signatures)
- Run in headed mode when possible (not headless)
- Random delays between actions (2-5 seconds)
- Human-like typing speed with variation
- Cookie persistence between sessions
- Realistic user agent strings

### Audio Processing
- Stream large content to avoid memory issues
- Character count estimation before generation (cost control)
- Target audio settings: AAC, 64kbps, mono, 44.1kHz
- Use Nero chapters format for M4B compatibility

### Testing Strategy
- Mock all external dependencies (never call real APIs in tests)
- Store HTML fixtures for parser testing (Fixtures/nyt-today-paper.html)
- Integration tests marked with `[Fact(Skip = "Requires real NYT credentials")]`
- Test coverage target: >80%

### Security
- Never commit credentials (use environment variables or user secrets)
- Validate all inputs with FluentValidation
- Run Docker containers as non-root user
- Encrypt cookie storage for session persistence

### Error Handling
- Implement retry logic with exponential backoff (3 attempts)
- Comprehensive logging with Serilog (structured logs)
- Budget limits for Eleven Labs API (~$0.30 per 1000 characters)
- Handle API rate limits gracefully

## Docker Setup Notes

Browser automation in Docker requires:
- Xvfb (virtual framebuffer) for headed Chrome
- `shm_size: '2gb'` to prevent Chrome crashes
- Run with `xvfb-run -a dotnet NYTAudioScraper.dll`
- Multi-stage build: sdk for build, aspnet for runtime

## Development Workflow

### Branching
- Trunk-based development
- Branch naming: `feat/<number>-<description>`, `fix/<number>-<description>`

### Commits
- Use Conventional Commits format
- Atomic commits (one logical change per commit)
- Examples: `feat(scraper): add NYT authentication flow`, `test(audio): add FFmpeg integration tests`

### Testing
- Write tests first (TDD)
- All tests must pass before merge
- Use fixtures instead of real external services
- Mark slow/external tests with Skip attribute

## Cost Management

Eleven Labs API pricing considerations:
- Creator tier: ~$0.30 per 1,000 characters
- Implement character counting before generation
- Log usage per scraping session
- Set budget limits per run
- Cache generated audio to avoid regeneration

## Important Notes

- NYT scraping is legally restricted - subscriber use only, no redistribution
- Respect rate limits (3-5 second delays minimum)
- Never log credentials or authentication tokens
- Test HTML parsing with fixtures to avoid hitting live site during development
- Chapter markers use M4B format with Nero chapters (compatible with iOS/Android)
