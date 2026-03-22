# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

TermReader - A general-purpose terminal browser with Helix-like keybindings, reader view, and audio generation capabilities. Built as a .NET 9 application that can browse the web from your terminal, extract readable content, and optionally generate audiobook-style M4B files with chapter markers.

**Note**: When scraping websites, always respect robots.txt and Terms of Service. This project is for educational and personal use.

## Architecture

**Pattern**: Clean Architecture
- Clean Architecture for separation of concerns
- Domain-driven design for core business logic
- Dependency injection for service composition

### Project Structure

```
src/
├── TermReader.Domain/           # Core business logic
│   ├── Entities/               # Article, AudioChapter, ScrapingSession, Browser entities
│   ├── ValueObjects/           # ArticleContent, AudioMetadata, Browser value objects
│   └── Enums/                  # ViewMode, LinkType, NodeCollapseState
├── TermReader.Application/      # Application layer
│   ├── Interfaces/             # IScraperService, IAudioGenerator, Browser interfaces
│   └── DTOs/                   # Data transfer objects (Browser DTOs)
├── TermReader.Infrastructure/   # External integrations
│   ├── Browser/                # Selenium automation, navigation, page loading, UI rendering
│   ├── Audio/                  # ElevenLabs, FFmpeg, ATL.NET, BudgetService
│   ├── Storage/                # File management (LocalFileStorage)
│   ├── Parsing/                # HTML parsing (ArticleParser)
│   └── Configuration/          # Configuration models
└── TermReader.API/             # Entry point (Console app)

tests/
└── TermReader.Tests/           # Unit and integration tests
    ├── DependencyInjectionTests.cs
    ├── ArticleParserTests.cs
    ├── BudgetServiceTests.cs
    ├── LocalFileStorageTests.cs
    ├── AudioGeneratorTests.cs
    └── Browser/                # Browser component tests
```

## Technology Stack

### Core
- .NET 9.0 (C# 12)
- Microsoft.Extensions.* (Configuration, DependencyInjection, Logging, Hosting)
- CommandLineParser (CLI argument parsing)

### Browser Automation
- **Selenium.WebDriver** with standard ChromeDriver and manual anti-detection measures
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
- Coverlet (code coverage collection)

## Common Commands

### Development
```bash
# Restore dependencies
dotnet restore

# Build
dotnet build --configuration Release

# Run terminal browser
dotnet run --project src/TermReader.API -- browse
dotnet run --project src/TermReader.API -- browse https://example.com

# Run audio scraper mode
dotnet run --project src/TermReader.API -- --count 5

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
dotnet user-secrets set "Auth:Email" "your-email@example.com"
dotnet user-secrets set "Auth:Password" "your-password"
dotnet user-secrets set "ElevenLabs:ApiKey" "your-api-key"
```

### Docker
```bash
# Build image
docker build -t termreader:latest .

# Run container
docker run --rm \
  -e AUTH_EMAIL="your-email@example.com" \
  -e AUTH_PASSWORD="your-password" \
  -e ELEVEN_LABS_API_KEY="your-api-key" \
  -v $(pwd)/output:/app/output \
  termreader:latest

# Docker Compose
docker-compose up
```

## Key Implementation Considerations

### Anti-Detection Measures
- Use Selenium with standard ChromeDriver plus manual anti-detection techniques
- Disable automation flags: `--disable-blink-features=AutomationControlled`
- JavaScript execution to mask webdriver property
- Run in headed mode when possible (configurable via Browser:Headless setting)
- Fixed delays between actions (3-5 seconds, configurable via RateLimitDelayMs)
- Cookie persistence in AppData directory for session reuse
- Realistic user agent strings

### Audio Processing
- Stream large content to avoid memory issues
- Character count estimation before generation (cost control)
- Target audio settings: AAC, 64kbps, mono, 44.1kHz
- Use Nero chapters format for M4B compatibility

### Testing Strategy
- **Tests must reflect real-world usage.** Never mock away or remove layers just to make tests pass. If a test can only pass by replacing the component that would actually fail in production, the test is worse than useless — it provides false confidence. A crash caused by missing GCP credentials went undetected because every test mocked `ICloudStorageClient`, hiding the fact that `StorageClient.CreateAsync()` throws `InvalidOperationException` (not the exceptions the code caught). This wasted significant time and resources.
- When testing error handling, ensure tests exercise the actual exception types that real dependencies throw — not just the ones you expect. Use integration tests or realistic fakes where mocks would hide failure modes.
- Mock external network calls (never call real APIs in tests), but don't mock away error-handling boundaries. If a component catches specific exception types, test that it handles all exception types the real dependency can throw.
- Store HTML fixtures for parser testing
- Integration tests marked with appropriate Skip attributes
- Test coverage target: >80%

### Security
- Never commit credentials (use environment variables or user secrets)
- Validate all inputs with appropriate validation logic
- Run Docker containers as non-root user
- Sanitize file names to prevent path traversal attacks
- Disk space validation before file writes

### Error Handling
- Implement retry logic with exponential backoff (3 attempts)
- Comprehensive logging with Serilog (structured logs)
- Budget limits for Eleven Labs API (~$0.30 per 1000 characters)
- Handle API rate limits gracefully

## Docker Setup Notes

Browser automation in Docker requires:
- Xvfb (virtual framebuffer) for headed Chrome
- `shm_size: '2gb'` to prevent Chrome crashes
- Run with `xvfb-run -a dotnet TermReader.API.dll`
- Multi-stage build: sdk for build, runtime for deployment

## Development Workflow

### Branching
- Trunk-based development
- Branch naming: `feat/<number>-<description>`, `fix/<number>-<description>`

### Commits
- Use Conventional Commits format
- Atomic commits (one logical change per commit)
- Examples: `feat(browser): add reader view mode`, `test(audio): add FFmpeg integration tests`

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

- Always respect website robots.txt and Terms of Service when scraping
- Respect rate limits (3-5 second delays minimum)
- Never log credentials or authentication tokens
- Test HTML parsing with fixtures to avoid hitting live sites during development
- Chapter markers use M4B format with Nero chapters (compatible with iOS/Android)


<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:ca08a54f -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

## Session Completion

**When ending a work session**, you MUST complete ALL steps below. Work is NOT complete until `git push` succeeds.

**MANDATORY WORKFLOW:**

1. **File issues for remaining work** - Create issues for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **PUSH TO REMOTE** - This is MANDATORY:
   ```bash
   git pull --rebase
   bd dolt push
   git push
   git status  # MUST show "up to date with origin"
   ```
5. **Clean up** - Clear stashes, prune remote branches
6. **Verify** - All changes committed AND pushed
7. **Hand off** - Provide context for next session

**CRITICAL RULES:**
- Work is NOT complete until `git push` succeeds
- NEVER stop before pushing - that leaves work stranded locally
- NEVER say "ready to push when you are" - YOU must push
- If push fails, resolve and retry until it succeeds
<!-- END BEADS INTEGRATION -->
