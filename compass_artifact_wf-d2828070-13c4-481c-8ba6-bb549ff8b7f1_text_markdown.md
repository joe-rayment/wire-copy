# NYT Audio Scraper Project Plan for Claude Code
## A Comprehensive Development Guide (2024-2025)

---

## Executive Summary

This document provides a complete project plan for building a NYT article scraper with audio generation and chapter markers using Claude Code's multi-agent architecture. The system will scrape articles from NYT Today's Paper, generate high-quality audio using Eleven Labs, and create an audiobook-style M4B file with chapter markers for morning walks.

**Critical Legal Notice**: NYT explicitly prohibits automated scraping in both robots.txt and Terms of Service, even for subscribers. This project is designed for educational purposes and personal use only. Users proceed at their own risk and should consider requesting official API access from NYT.

---

## 1. Project Overview & Architecture

### System Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Docker Container                          │
│  ┌──────────────────────────────────────────────────────┐  │
│  │           Application Layer (C# .NET 8)               │  │
│  │                                                        │  │
│  │  ┌──────────────┐  ┌──────────────┐  ┌────────────┐ │  │
│  │  │   Scraper    │  │    Audio     │  │   File     │ │  │
│  │  │   Service    │→ │  Generator   │→ │ Management │ │  │
│  │  └──────────────┘  └──────────────┘  └────────────┘ │  │
│  │         ↓                  ↓                 ↓        │  │
│  │  ┌──────────────┐  ┌──────────────┐  ┌────────────┐ │  │
│  │  │  Playwright  │  │ Eleven Labs  │  │  FFmpeg    │ │  │
│  │  │  (Browser)   │  │    Client    │  │  + ATL.NET │ │  │
│  │  └──────────────┘  └──────────────┘  └────────────┘ │  │
│  └──────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

### Architecture Pattern: **Clean Architecture with Vertical Slices**

**Why This Hybrid:**
- **Clean Architecture** for core domain logic (article parsing, audio processing)
- **Vertical Slices** for feature workflows (scrape → generate → package)
- Excellent separation of concerns while maintaining feature cohesion
- Easy to test each component independently

### Project Structure

```
TermReader/
├── src/
│   ├── Domain/                          # Core business logic
│   │   ├── Entities/
│   │   │   ├── Article.cs
│   │   │   ├── AudioChapter.cs
│   │   │   └── ScrapingSession.cs
│   │   └── ValueObjects/
│   │       ├── ArticleContent.cs
│   │       └── AudioMetadata.cs
│   │
│   ├── Application/                     # Use cases
│   │   ├── Interfaces/
│   │   │   ├── IScraperService.cs
│   │   │   ├── IAudioGenerator.cs
│   │   │   ├── IChapterMarker.cs
│   │   │   └── IFileStorage.cs
│   │   ├── Features/
│   │   │   ├── ScrapeArticles/
│   │   │   │   ├── ScrapeArticlesCommand.cs
│   │   │   │   └── ScrapeArticlesHandler.cs
│   │   │   ├── GenerateAudio/
│   │   │   │   ├── GenerateAudioCommand.cs
│   │   │   │   └── GenerateAudioHandler.cs
│   │   │   └── CreateAudiobook/
│   │   │       ├── CreateAudiobookCommand.cs
│   │   │       └── CreateAudiobookHandler.cs
│   │   └── DTOs/
│   │       └── ArticleDto.cs
│   │
│   ├── Infrastructure/                  # External concerns
│   │   ├── Browser/
│   │   │   ├── PlaywrightScraperService.cs
│   │   │   └── BrowserConfiguration.cs
│   │   ├── Audio/
│   │   │   ├── ElevenLabsAudioGenerator.cs
│   │   │   ├── FFmpegProcessor.cs
│   │   │   └── ATLChapterMarker.cs
│   │   ├── Storage/
│   │   │   └── LocalFileStorage.cs
│   │   └── Authentication/
│   │       └── NYTAuthService.cs
│   │
│   └── API/                             # Entry point
│       ├── Program.cs
│       ├── appsettings.json
│       ├── Controllers/ (or Endpoints/)
│       └── Middleware/
│
├── tests/
│   ├── Domain.UnitTests/
│   ├── Application.UnitTests/
│   ├── Infrastructure.UnitTests/
│   │   ├── Fixtures/
│   │   │   ├── sample-nyt-page.html
│   │   │   └── mock-article-data.json
│   │   └── Mocks/
│   │       ├── MockHttpClient.cs
│   │       └── MockFileSystem.cs
│   └── Integration.Tests/
│       └── EndToEndTests.cs
│
├── docker/
│   ├── Dockerfile
│   ├── docker-compose.yml
│   └── .dockerignore
│
├── .github/
│   ├── workflows/
│   │   ├── ci.yml
│   │   └── pr-validation.yml
│   └── CODEOWNERS
│
├── docs/
│   ├── architecture.md
│   └── api-documentation.md
│
├── .editorconfig
├── .gitignore
└── README.md
```

---

## 2. Detailed Component Breakdown

### 2.1 Scraper Service (Infrastructure Layer)

**Responsibilities:**
- Authenticate to NYT as subscriber
- Navigate to Today's Paper section
- Extract article URLs, titles, authors, content
- Handle dynamic content loading
- Implement respectful rate limiting

**Technology Choice: Selenium with Undetected ChromeDriver**

**Why Not Playwright:**
- Playwright for .NET is easily detected via CDP (Chrome DevTools Protocol) signatures
- No C# port of Patchright (undetected Playwright variant)
- Selenium + Undetected ChromeDriver provides better anti-detection for C#

**Key Implementation Details:**

```csharp
public class PlaywrightScraperService : IScraperService
{
    private readonly ILogger<PlaywrightScraperService> _logger;
    private readonly BrowserConfiguration _config;
    private IPlaywright _playwright;
    private IBrowser _browser;

    // Constructor with DI
    public PlaywrightScraperService(
        ILogger<PlaywrightScraperService> logger,
        IOptions<BrowserConfiguration> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    public async Task<IEnumerable<Article>> ScrapeArticlesAsync(
        CancellationToken ct = default)
    {
        // Implementation with anti-detection measures
    }
}
```

**Anti-Detection Measures:**
- Run in headed mode (not headless) when possible
- Random delays between actions (2-5 seconds)
- Human-like typing speed with variation
- Realistic user agent strings
- Cookie persistence between sessions
- Respect rate limits (max 1 request per 3-5 seconds)

**NuGet Packages:**
- `Selenium.WebDriver` v4.33.0
- `Selenium.WebDriver.ChromeDriver` v131.0+
- `Selenium.UndetectedChromeDriver` (latest)

### 2.2 Audio Generation Service (Infrastructure Layer)

**Responsibilities:**
- Interface with Eleven Labs API
- Generate audio for each article/chapter
- Handle API rate limits and errors
- Stream audio for long content
- Manage audio quality settings

**Technology: Eleven Labs API via ElevenLabs-DotNet**

**Key Implementation:**

```csharp
public class ElevenLabsAudioGenerator : IAudioGenerator
{
    private readonly ElevenLabsClient _client;
    private readonly ILogger<ElevenLabsAudioGenerator> _logger;
    private readonly AudioConfiguration _config;

    public async Task<byte[]> GenerateAudioAsync(
        string text, 
        string voiceId,
        CancellationToken ct = default)
    {
        var request = new TextToSpeechRequest(voice, text);
        
        // Use streaming for long content
        using var outputStream = new MemoryStream();
        var clip = await _client.TextToSpeechEndpoint.TextToSpeechAsync(
            request,
            partialClipCallback: async (partial) =>
            {
                await outputStream.WriteAsync(partial.ClipData, ct);
            },
            cancellationToken: ct
        );
        
        return outputStream.ToArray();
    }
}
```

**Voice Selection:**
- Eleven v3 model for highest quality
- Select professional news reader voice
- Consider turbo models for faster generation if needed

**Cost Management:**
- Estimate character count before generation
- Implement character counting and cost estimation
- Log usage per scraping session
- Typical costs: ~$0.30 per 1,000 characters (Creator tier)

**NuGet Packages:**
- `ElevenLabs-DotNet` v3.6.0+

### 2.3 Audio Processing Service (Infrastructure Layer)

**Responsibilities:**
- Concatenate multiple audio files
- Convert to optimal format (M4B)
- Normalize audio levels (-16 dB LKFS for podcasts)
- Optimize for mobile playback

**Technology: FFMpegCore + NAudio**

**Key Implementation:**

```csharp
public class FFmpegProcessor : IAudioProcessor
{
    public async Task<string> ConvertToAudiobookAsync(
        List<string> inputFiles,
        string outputPath,
        CancellationToken ct = default)
    {
        // Concatenate all chapters
        var concatenated = await ConcatenateAudioFiles(inputFiles);
        
        // Convert to M4B with optimal settings
        await FFMpegArguments
            .FromFileInput(concatenated)
            .OutputToFile(outputPath, false, options => options
                .WithAudioCodec("aac")        // AAC codec
                .WithAudioBitrate(64)         // 64kbps for voice
                .WithAudioSamplingRate(44100) // 44.1kHz
                .WithCustomArgument("-ac 1")  // Mono
                .ForceFormat("ipod"))         // M4B format
            .ProcessAsynchronously(cancellationToken: ct);
        
        return outputPath;
    }
}
```

**Audio Settings:**
- Format: M4B (MPEG-4 Audio Book)
- Codec: AAC-LC (Low Complexity)
- Bitrate: 64 kbps mono (optimal for voice)
- Sample Rate: 44.1 kHz
- File size: ~28 MB per hour

**NuGet Packages:**
- `FFMpegCore` v5.2.0+
- `NAudio` v2.2.1

### 2.4 Chapter Marker Service (Infrastructure Layer)

**Responsibilities:**
- Add chapter markers to M4B file
- Include chapter titles, timestamps
- Ensure compatibility with iOS, Android

**Technology: ATL.NET (Audio Tools Library)**

**Key Implementation:**

```csharp
public class ATLChapterMarker : IChapterMarker
{
    public async Task AddChaptersAsync(
        string audioFile,
        List<ChapterInfo> chapters,
        CancellationToken ct = default)
    {
        var audioData = new AudioDataManager(
            AudioDataIOFactory.GetInstance().GetDataReader(audioFile));
        
        var tag = new TagData { Chapters = new List<ChapterInfo>() };
        
        int currentTime = 0;
        foreach (var chapter in chapters)
        {
            tag.Chapters.Add(new ChapterInfo
            {
                StartTime = (uint)currentTime,
                EndTime = (uint)(currentTime + chapter.DurationMs),
                Title = chapter.Title
            });
            currentTime += chapter.DurationMs;
        }
        
        audioData.UpdateTagInFile(tag, MetaDataIOFactory.TAG_ID3V2);
    }
}
```

**Chapter Format:**
- Primary: M4B with Nero chapters (simple, metadata-based)
- Alternative: MP3 with ID3v2 CHAP/CTOC frames
- Each article = one chapter
- Include article title and author in chapter name

**Library:**
- ATL.NET (install from GitHub: https://github.com/Zeugma440/atldotnet)

### 2.5 Authentication Service

**Responsibilities:**
- Authenticate to NYT using subscriber credentials
- Persist session cookies
- Handle session expiration
- Secure credential storage

**Implementation Pattern:**

```csharp
public class NYTAuthService : IAuthService
{
    public async Task<bool> AuthenticateAsync(
        string email,
        string password,
        CancellationToken ct = default)
    {
        // Navigate to login page
        await _page.GotoAsync("https://myaccount.nytimes.com/auth/login");
        
        // Human-like form filling with delays
        await _page.FillAsync("input[name='email']", email);
        await Task.Delay(_random.Next(500, 1000), ct);
        
        await _page.FillAsync("input[name='password']", password);
        await Task.Delay(_random.Next(500, 1000), ct);
        
        await _page.ClickAsync("button[type='submit']");
        
        // Verify authentication success
        await _page.WaitForURLAsync("**/article/**");
        
        // Save cookies for future use
        await SaveCookiesAsync();
        
        return true;
    }
}
```

---

## 3. Multi-Agent Workflow Design for Claude Code

### Agent Tier System

**Tier 1: Opus Agent (Orchestrator & Reviewer)**
- Reviews all code written by smaller agents
- Provides architectural guidance
- Makes critical decisions
- Reviews tests and provides feedback
- Coordinates between agents

**Tier 2: Sonnet Agents (Feature Implementation)**
- Implement specific features/components
- Write unit and integration tests
- Handle moderate complexity tasks
- Report progress to Opus

**Tier 3: Haiku Agents (Simple Tasks)**
- Write boilerplate code
- Create DTOs and models
- Write simple unit tests
- Documentation tasks
- Configuration files

### Task Allocation Strategy

#### Phase 1: Foundation (Week 1)

**Opus Agent Tasks:**
- Review project structure
- Define interfaces and contracts
- Architectural decisions
- Review all code from Sonnet/Haiku agents

**Sonnet Agent 1: Project Setup**
- Create solution structure
- Configure dependency injection
- Set up logging (Serilog)
- Configure appsettings.json
- Docker setup

**Sonnet Agent 2: Domain Layer**
- Create entity classes (Article, AudioChapter)
- Define value objects
- Create domain interfaces

**Haiku Agent 1: Configuration**
- Create DTOs
- Write configuration classes
- Set up .editorconfig, .gitignore

#### Phase 2: Scraper Implementation (Week 2)

**Opus Agent:**
- Review scraper architecture
- Provide anti-detection guidance
- Review HTML parsing logic

**Sonnet Agent 1: Browser Automation**
- Implement ScraperService
- Set up Selenium with anti-detection
- Implement authentication flow
- Write integration tests with mock HTML

**Sonnet Agent 2: Content Parsing**
- Implement HTML parsing logic
- Extract article metadata
- Handle edge cases (missing data)
- Write unit tests

**Haiku Agent 1: Test Fixtures**
- Create mock HTML files
- Generate test data
- Document HTML structure

#### Phase 3: Audio Generation (Week 3)

**Opus Agent:**
- Review audio generation strategy
- Approve Eleven Labs integration
- Review error handling

**Sonnet Agent 1: Eleven Labs Integration**
- Implement ElevenLabsAudioGenerator
- Handle API errors and retries (Polly)
- Implement streaming for long content
- Write tests with mocked API responses

**Sonnet Agent 2: Audio Processing**
- Implement FFmpegProcessor
- Audio concatenation logic
- Format conversion to M4B
- Audio quality optimization

**Haiku Agent 1: Audio Utilities**
- File naming conventions
- Temporary file management
- Cost estimation utility

#### Phase 4: Chapter Markers (Week 3-4)

**Opus Agent:**
- Review chapter marker implementation
- Verify format compatibility

**Sonnet Agent 1: Chapter Implementation**
- Implement ATLChapterMarker
- Calculate chapter timestamps
- Add chapter metadata
- Test on multiple devices

**Haiku Agent 1: Testing Utilities**
- Create chapter verification scripts
- Document device compatibility

#### Phase 5: Integration & End-to-End (Week 4)

**Opus Agent:**
- Review entire workflow
- Conduct code review
- Approve for deployment

**Sonnet Agent 1: Orchestration**
- Implement main workflow (MediatR handlers)
- Error handling and logging
- Progress reporting
- End-to-end integration tests

**Sonnet Agent 2: API Layer**
- Create API endpoints/minimal APIs
- Request validation
- Response formatting
- API documentation

#### Phase 6: Docker & Deployment (Week 5)

**Opus Agent:**
- Review Docker configuration
- Security audit
- Approve deployment strategy

**Sonnet Agent 1: Docker Setup**
- Multi-stage Dockerfile
- docker-compose.yml
- Environment variable configuration
- Volume management

**Haiku Agent 1: Documentation**
- README.md
- Deployment guide
- Configuration guide

### Agent Review Workflow

```
Haiku/Sonnet Agent completes task
     ↓
Commits to feature branch
     ↓
Opens Draft PR
     ↓
Opus Agent reviews (batch of 3-5 PRs)
     ↓
Provides feedback (request changes OR approve)
     ↓
[If changes needed]
     Haiku/Sonnet implements changes
     ↓
     Re-commits
     ↓
     Opus re-reviews
     ↓
[If approved]
     Merge to main
```

**Batch Review Strategy:**
- Opus reviews 3-5 completed tasks at once
- More efficient than reviewing each individually
- Provides feedback in batch
- Agents iterate based on feedback

---

## 4. Development Phases with Testing Strategy

### Phase 1: Foundation & Setup

**Tasks:**
- Project structure creation
- Dependency injection setup
- Configuration management
- Logging setup

**Testing Strategy:**
- Unit tests for configuration loading
- Verify DI container resolves all services
- Test logging output

**Success Criteria:**
- Application starts successfully
- All services resolve from DI
- Configuration loads from appsettings.json
- Logs written to file and console

**Test Example:**
```csharp
[Fact]
public void ConfigureServices_AllDependencies_ResolveSuccessfully()
{
    // Arrange
    var services = new ServiceCollection();
    var config = new ConfigurationBuilder().Build();
    
    // Act
    Program.ConfigureServices(services, config);
    var provider = services.BuildServiceProvider();
    
    // Assert
    Assert.NotNull(provider.GetService<IScraperService>());
    Assert.NotNull(provider.GetService<IAudioGenerator>());
    Assert.NotNull(provider.GetService<IChapterMarker>());
}
```

### Phase 2: Scraper Development

**Tasks:**
- Browser automation setup
- NYT authentication
- Article extraction
- Content parsing

**Testing Strategy:**
- **Unit Tests:** Test parsing logic with mock HTML
- **Integration Tests:** Test browser automation with mock server
- **Fixtures:** Store real NYT HTML samples (sanitized)

**Mock HTML Approach:**
```csharp
[Fact]
public async Task ScrapeArticles_ValidHTML_ExtractsCorrectData()
{
    // Arrange
    var mockHtml = File.ReadAllText("Fixtures/nyt-today-paper.html");
    var mockHttp = Substitute.For<IHttpClient>();
    mockHttp.GetAsync(Arg.Any<string>()).Returns(mockHtml);
    
    var scraper = new ScraperService(mockHttp);
    
    // Act
    var articles = await scraper.ScrapeArticlesAsync();
    
    // Assert
    Assert.NotEmpty(articles);
    Assert.All(articles, a => Assert.NotNull(a.Title));
}
```

**Success Criteria:**
- Authenticates successfully with test credentials
- Extracts 10+ articles from Today's Paper
- Parses title, author, content correctly
- No authentication failures
- Tests pass with mock HTML

### Phase 3: Audio Generation

**Tasks:**
- Eleven Labs API integration
- Audio streaming implementation
- Error handling and retries
- Cost estimation

**Testing Strategy:**
- **Unit Tests:** Mock Eleven Labs client responses
- **Integration Tests:** Use small sample text (consume minimal credits)
- **Error Handling Tests:** Test API failures, rate limits

**Mock API Approach:**
```csharp
[Fact]
public async Task GenerateAudio_ValidText_ReturnsAudioData()
{
    // Arrange
    var mockClient = Substitute.For<IElevenLabsClient>();
    var fakeAudio = new byte[] { 0x52, 0x49, 0x46, 0x46 }; // RIFF header
    mockClient.GenerateAudioAsync(Arg.Any<string>(), Arg.Any<string>())
              .Returns(fakeAudio);
    
    var generator = new AudioGenerator(mockClient);
    
    // Act
    var audio = await generator.GenerateAudioAsync("Test text", "voice-id");
    
    // Assert
    Assert.NotEmpty(audio);
    Assert.Equal(0x52, audio[0]); // Verify RIFF header
}
```

**Success Criteria:**
- Generates audio for sample article
- Handles API errors gracefully
- Implements retry logic (3 attempts with exponential backoff)
- Accurate cost estimation
- Audio quality meets requirements (64kbps, clear)

### Phase 4: Audio Processing & Chapter Markers

**Tasks:**
- FFmpeg integration
- Audio concatenation
- M4B conversion
- Chapter marker addition

**Testing Strategy:**
- **Unit Tests:** Verify chapter calculation logic
- **Integration Tests:** Create sample M4B with chapters
- **Device Tests:** Verify playback on iOS/Android

**Chapter Verification:**
```csharp
[Fact]
public async Task AddChapters_ValidAudio_CreatesCorrectMarkers()
{
    // Arrange
    var audioFile = "test-audiobook.m4b";
    var chapters = new List<ChapterInfo>
    {
        new() { Title = "Chapter 1", DurationMs = 180000 },
        new() { Title = "Chapter 2", DurationMs = 240000 }
    };
    
    var marker = new ATLChapterMarker();
    
    // Act
    await marker.AddChaptersAsync(audioFile, chapters);
    
    // Assert
    var audioData = new AudioDataManager(/* ... */);
    audioData.ReadFromFile();
    Assert.Equal(2, audioData.Tag.Chapters.Count);
    Assert.Equal("Chapter 1", audioData.Tag.Chapters[0].Title);
}
```

**Success Criteria:**
- Audio files concatenate successfully
- M4B file created with proper format
- Chapter markers visible in Apple Podcasts (iOS)
- Chapter markers work in AntennaPod (Android)
- File size optimized (~28 MB/hour)

### Phase 5: End-to-End Integration

**Tasks:**
- Complete workflow orchestration
- Error handling and logging
- Progress reporting
- Performance optimization

**Testing Strategy:**
- **End-to-End Test:** Full workflow with mock dependencies
- **Performance Tests:** Time each phase
- **Error Scenario Tests:** Handle failures gracefully

**E2E Test Structure:**
```csharp
[Fact(Skip = "Integration test - runs slowly")]
public async Task EndToEnd_MockDependencies_CompletesSuccessfully()
{
    // Arrange: Mock all external dependencies
    var mockScraper = CreateMockScraper();
    var mockAudioGen = CreateMockAudioGenerator();
    var mockProcessor = CreateMockProcessor();
    
    var orchestrator = new WorkflowOrchestrator(
        mockScraper, mockAudioGen, mockProcessor);
    
    // Act
    var result = await orchestrator.ExecuteAsync();
    
    // Assert
    Assert.True(result.Success);
    Assert.NotNull(result.OutputFile);
    Assert.True(File.Exists(result.OutputFile));
}
```

**Success Criteria:**
- Complete workflow executes without errors
- Output M4B file contains all articles
- All chapters accessible
- Total execution time < 15 minutes (for 10 articles)
- Comprehensive error logging

### Phase 6: Dockerization

**Tasks:**
- Create Dockerfile
- Docker Compose setup
- Volume management
- Environment configuration

**Testing Strategy:**
- **Container Build:** Verify image builds successfully
- **Runtime Test:** Container starts and executes workflow
- **Volume Test:** Output files persist to host

**Docker Test:**
```bash
# Build test
docker build -t termreader .

# Run test
docker run --rm \
  -e NYT_EMAIL="test@example.com" \
  -e NYT_PASSWORD="password" \
  -e ELEVEN_LABS_API_KEY="key" \
  -v ./output:/app/output \
  termreader
  
# Verify output
ls -lh ./output/*.m4b
```

**Success Criteria:**
- Docker image builds in < 5 minutes
- Image size < 500 MB
- Container runs successfully
- Audio file appears in mounted volume
- No security warnings

---

## 5. GitHub Workflow Recommendations

### Branching Strategy: **Trunk-Based Development**

```
main (protected)
  ├─ feat/001-project-setup
  ├─ feat/002-scraper-implementation
  ├─ feat/003-audio-generation
  ├─ feat/004-chapter-markers
  ├─ feat/005-integration
  └─ feat/006-docker-deployment
```

**Branch Naming Convention:**
```
feat/<number>-<short-description>
fix/<number>-<short-description>
chore/<number>-<short-description>
```

### Commit Strategy for AI Agents

**Principles:**
1. **Atomic commits:** One logical change per commit
2. **Frequent commits:** After each passing test
3. **Conventional Commits:** Standard format for all messages

**Commit Message Format:**
```
<type>[optional scope]: <description>

[optional body]

[optional footer]
```

**Examples:**
```bash
feat(scraper): add NYT authentication flow
test(scraper): add unit tests for HTML parsing
fix(audio): handle API timeout errors
refactor(scraper): extract URL validation logic
docs(readme): add installation instructions
```

### Pull Request Workflow

**1. Agent Creates PR:**
```markdown
## Description
Implements browser automation for NYT scraping using Selenium

## Changes
- Created ScraperService with anti-detection measures
- Added authentication flow with cookie persistence
- Implemented rate limiting (3-5 second delays)
- Created unit tests with mock HTML fixtures

## Testing
- [x] All unit tests pass (23/23)
- [x] Integration tests pass (5/5)
- [x] Code coverage: 87%

## Checklist
- [x] Tests written and passing
- [x] No compiler warnings
- [x] Conventional commits used
- [x] Documentation updated
```

**2. Opus Agent Reviews (Batch):**
- Reviews 3-5 PRs at once
- Provides consolidated feedback
- Requests changes or approves

**3. Changes Implemented:**
- Agent addresses feedback
- Commits fixes
- Requests re-review

**4. Merge:**
- Squash and merge (keeps history clean)
- Delete feature branch

### Branch Protection Rules

```yaml
Branch: main
Required:
  ✓ Require pull request before merging
  ✓ Require 1 approval (Opus agent)
  ✓ Require status checks to pass:
    - unit-tests
    - integration-tests
    - code-quality
  ✓ Require branches to be up to date
  ✓ No force pushes
  ✓ No deletions
```

### GitHub Actions CI/CD

**.github/workflows/ci.yml:**
```yaml
name: CI

on:
  pull_request:
  push:
    branches: [main]

jobs:
  test:
    runs-on: ubuntu-latest
    
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '8.0.x'
      
      - name: Restore dependencies
        run: dotnet restore
      
      - name: Build
        run: dotnet build --no-restore --configuration Release
      
      - name: Run Unit Tests
        run: dotnet test --no-build --filter Category=Unit
      
      - name: Run Integration Tests
        run: dotnet test --no-build --filter Category=Integration
      
      - name: Code Coverage
        run: dotnet test --collect:"XPlat Code Coverage"
      
      - name: Upload Coverage to Codecov
        uses: codecov/codecov-action@v3
        with:
          file: ./coverage.xml
  
  docker:
    runs-on: ubuntu-latest
    needs: test
    
    steps:
      - uses: actions/checkout@v4
      
      - name: Build Docker Image
        run: docker build -t termreader:${{ github.sha }} .
      
      - name: Test Docker Image
        run: |
          docker run --rm \
            -e NYT_EMAIL="test@example.com" \
            -e NYT_PASSWORD="test" \
            -e ELEVEN_LABS_API_KEY="test" \
            termreader:${{ github.sha }} \
            --dry-run
```

### Pre-Commit Hooks

**.pre-commit-config.yaml:**
```yaml
repos:
  - repo: https://github.com/pre-commit/pre-commit-hooks
    rev: v5.0.0
    hooks:
      - id: trailing-whitespace
      - id: end-of-file-fixer
      - id: check-yaml
      - id: check-json
      
  - repo: local
    hooks:
      - id: dotnet-format
        name: dotnet format
        entry: dotnet format
        language: system
        types: [c#]
```

---

## 6. Technology Stack with Specific Recommendations

### Core Framework
- **.NET 8.0** (LTS support until November 2026)
- **C# 12** language features
- **ASP.NET Core 8.0** (if building API)

### Architecture & Patterns
- **MediatR** v12+ (CQRS/command handling)
- **FluentValidation** v11+ (input validation)
- **Mapster** (object mapping - faster than AutoMapper)

### Browser Automation
- **Selenium.WebDriver** v4.33.0
- **Selenium.WebDriver.ChromeDriver** v131.0+
- **Selenium.UndetectedChromeDriver** (latest)

**Alternative (if detection not critical):**
- **Microsoft.Playwright** v1.55.0

### Audio Generation
- **ElevenLabs-DotNet** v3.6.0+ (official C# SDK)

### Audio Processing
- **FFMpegCore** v5.2.0+ (FFmpeg wrapper)
- **NAudio** v2.2.1 (audio manipulation)
- **ATL.NET** (from GitHub - chapter markers)

### Logging
- **Serilog** v4.0+ (structured logging)
- **Serilog.AspNetCore** v8.0+
- **Serilog.Sinks.Console**
- **Serilog.Sinks.File**
- **Serilog.Enrichers.Environment**

### Resilience & HTTP
- **Polly** v8.0+ (retry, circuit breaker, timeout)
- **Microsoft.Extensions.Http.Resilience**

### Testing
- **xUnit** v2.8+ (test framework)
- **NSubstitute** v5.1+ (mocking - preferred over Moq)
- **FluentAssertions** v6.12+ (readable assertions)
- **Bogus** v35+ (test data generation)

### Configuration & DI
- **Microsoft.Extensions.DependencyInjection** v8.0+
- **Microsoft.Extensions.Configuration** v8.0+
- **Microsoft.Extensions.Options** v8.0+

### Docker
- **Base Image:** `mcr.microsoft.com/dotnet/sdk:8.0` (build)
- **Runtime Image:** `mcr.microsoft.com/dotnet/aspnet:8.0` (runtime)
- **Browser:** Selenium with Chrome (headed mode in Docker with Xvfb)

### NuGet Package Summary

```xml
<ItemGroup>
  <!-- Core Framework -->
  <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.0" />
  
  <!-- Architecture -->
  <PackageReference Include="MediatR" Version="12.4.0" />
  <PackageReference Include="FluentValidation" Version="11.9.0" />
  <PackageReference Include="Mapster" Version="7.4.0" />
  
  <!-- Browser Automation -->
  <PackageReference Include="Selenium.WebDriver" Version="4.33.0" />
  <PackageReference Include="Selenium.WebDriver.ChromeDriver" Version="131.0.6778.139" />
  <PackageReference Include="Selenium.UndetectedChromeDriver" Version="*" />
  
  <!-- Audio -->
  <PackageReference Include="ElevenLabs-DotNet" Version="3.6.0" />
  <PackageReference Include="FFMpegCore" Version="5.2.0" />
  <PackageReference Include="NAudio" Version="2.2.1" />
  
  <!-- Logging -->
  <PackageReference Include="Serilog" Version="4.0.0" />
  <PackageReference Include="Serilog.AspNetCore" Version="8.0.0" />
  <PackageReference Include="Serilog.Sinks.Console" Version="5.0.0" />
  <PackageReference Include="Serilog.Sinks.File" Version="5.0.0" />
  
  <!-- Resilience -->
  <PackageReference Include="Polly" Version="8.4.0" />
  <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="8.0.0" />
  
  <!-- Testing -->
  <PackageReference Include="xUnit" Version="2.8.0" />
  <PackageReference Include="xUnit.runner.visualstudio" Version="2.8.0" />
  <PackageReference Include="NSubstitute" Version="5.1.0" />
  <PackageReference Include="FluentAssertions" Version="6.12.0" />
  <PackageReference Include="Bogus" Version="35.3.0" />
</ItemGroup>
```

---

## 7. Security Considerations

### 7.1 Credential Management

**DO NOT:**
- ❌ Hard-code credentials in source code
- ❌ Commit secrets to Git repository
- ❌ Include credentials in Docker images
- ❌ Log credentials (even in debug mode)

**DO:**
- ✅ Use environment variables for credentials
- ✅ Use .NET User Secrets for local development
- ✅ Use Azure Key Vault or AWS Secrets Manager in production
- ✅ Implement credential rotation

**Implementation:**

```csharp
// appsettings.json (NO SECRETS HERE)
{
  "NYT": {
    "BaseUrl": "https://www.nytimes.com"
  },
  "ElevenLabs": {
    "BaseUrl": "https://api.elevenlabs.io/v1"
  }
}

// Environment variables (Docker/Production)
NYT_EMAIL=user@example.com
NYT_PASSWORD=secure_password
ELEVEN_LABS_API_KEY=your_api_key

// User Secrets (Development)
dotnet user-secrets init
dotnet user-secrets set "NYT:Email" "user@example.com"
dotnet user-secrets set "NYT:Password" "password"
dotnet user-secrets set "ElevenLabs:ApiKey" "key"
```

**Configuration Loading:**

```csharp
public class NYTConfiguration
{
    public required string Email { get; init; }
    public required string Password { get; init; }
    public required string BaseUrl { get; init; }
}

// Program.cs
services.Configure<NYTConfiguration>(options =>
{
    options.Email = builder.Configuration["NYT:Email"] 
        ?? throw new InvalidOperationException("NYT Email not configured");
    options.Password = builder.Configuration["NYT:Password"]
        ?? throw new InvalidOperationException("NYT Password not configured");
    options.BaseUrl = builder.Configuration["NYT:BaseUrl"]!;
});
```

### 7.2 Docker Security

**Multi-Stage Build (reduces attack surface):**

```dockerfile
# syntax=docker/dockerfile:1

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["TermReader/TermReader.csproj", "TermReader/"]
RUN dotnet restore "TermReader/TermReader.csproj"
COPY . .
WORKDIR "/src/TermReader"
RUN dotnet build -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final

# Install Chrome and dependencies for Selenium
RUN apt-get update && apt-get install -y \
    chromium \
    chromium-driver \
    xvfb \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN useradd -m -u 1000 appuser && \
    mkdir -p /app/output && \
    chown -R appuser:appuser /app

WORKDIR /app
USER appuser

COPY --from=publish --chown=appuser:appuser /app/publish .

# Expose no ports (runs as background job)
ENTRYPOINT ["dotnet", "TermReader.dll"]
```

**Security Best Practices:**
- ✅ Use official Microsoft base images
- ✅ Run as non-root user
- ✅ Scan images for vulnerabilities (Trivy, Snyk)
- ✅ Keep base images updated
- ✅ Minimize image layers
- ✅ Use `.dockerignore` to exclude sensitive files

### 7.3 API Key Protection

**Eleven Labs API Key:**
- Store in environment variables only
- Rotate periodically
- Monitor usage for anomalies
- Use separate keys for dev/prod

**Rate Limiting:**
- Implement application-level rate limiting
- Respect Eleven Labs API limits
- Monitor credit consumption

### 7.4 NYT Authentication

**Session Management:**
- Persist cookies securely (encrypted)
- Clear cookies on application restart
- Never log authentication tokens
- Implement session expiration handling

### 7.5 Secure Coding Practices

**Input Validation:**
```csharp
public class ScrapeArticlesCommand
{
    [Required]
    [Range(1, 100)]
    public int MaxArticles { get; init; } = 10;
    
    [Required]
    [Url]
    public string? BaseUrl { get; init; }
}

// Validate with FluentValidation
public class ScrapeArticlesValidator : AbstractValidator<ScrapeArticlesCommand>
{
    public ScrapeArticlesValidator()
    {
        RuleFor(x => x.MaxArticles).InclusiveBetween(1, 100);
        RuleFor(x => x.BaseUrl).Must(BeValidNYTUrl);
    }
}
```

**SQL Injection Prevention:**
- Use parameterized queries (if database added)
- Use ORM (Entity Framework Core) with proper escaping

**XSS Prevention:**
- Sanitize HTML content when parsing
- Encode output if displaying in UI

---

## 8. Potential Challenges & Mitigation Strategies

### Challenge 1: NYT Bot Detection

**Risk: HIGH**
- NYT uses sophisticated anti-bot measures
- Selenium/Playwright easily detected
- Account may be terminated

**Mitigation Strategies:**

1. **Use Selenium with Undetected ChromeDriver**
   - Better anti-detection than Playwright in C#
   - Run in headed mode when possible
   - Use residential proxies if needed

2. **Human-Like Behavior**
   ```csharp
   // Random delays between actions
   private async Task HumanDelay()
   {
       await Task.Delay(_random.Next(2000, 5000));
   }
   
   // Gradual typing
   foreach (var c in email)
   {
       await page.Keyboard.TypeAsync(c.ToString());
       await Task.Delay(_random.Next(50, 150));
   }
   ```

3. **Respectful Rate Limiting**
   - Minimum 3-5 seconds between requests
   - Scrape during off-peak hours
   - Limit to 10-20 articles per session

4. **Session Persistence**
   - Reuse cookies to avoid repeated logins
   - Maintain long-lived sessions

5. **Legal Compliance**
   - Only scrape as authenticated subscriber
   - Personal use only
   - Consider requesting API access from NYT

### Challenge 2: Dynamic Content Loading

**Risk: MEDIUM**
- Articles load via JavaScript
- Infinite scroll or pagination
- Content may not be immediately available

**Mitigation Strategies:**

1. **Wait for Content**
   ```csharp
   await page.WaitForSelectorAsync("article", new() {
       State = WaitForSelectorState.Visible,
       Timeout = 30000
   });
   
   await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
   ```

2. **Handle Lazy Loading**
   ```csharp
   // Scroll to trigger lazy-loaded content
   await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
   await Task.Delay(2000);
   ```

3. **Retry Logic**
   ```csharp
   var retryPolicy = Policy
       .Handle<PlaywrightException>()
       .WaitAndRetryAsync(3, attempt => 
           TimeSpan.FromSeconds(Math.Pow(2, attempt)));
   
   await retryPolicy.ExecuteAsync(async () =>
       await page.GotoAsync(url));
   ```

### Challenge 3: Eleven Labs API Costs

**Risk: MEDIUM**
- Unexpected high costs if not monitored
- API rate limits during peak usage
- Cost varies by character count

**Mitigation Strategies:**

1. **Cost Estimation**
   ```csharp
   public class CostEstimator
   {
       public (int chars, decimal cost, double hours) Estimate(string text)
       {
           var chars = text.Length;
           var costPerChar = 0.0003m; // $0.30 per 1000 chars
           var cost = chars * costPerChar;
           var hours = (chars / 5.0) / 150.0 / 60.0; // 150 WPM
           
           return (chars, cost, hours);
       }
   }
   ```

2. **Budget Limits**
   ```csharp
   if (estimatedCost > maxBudgetPerRun)
   {
       _logger.LogWarning(
           "Estimated cost ${Cost} exceeds budget ${Budget}",
           estimatedCost, maxBudgetPerRun);
       
       throw new BudgetExceededException();
   }
   ```

3. **Character Limits**
   - Summarize very long articles
   - Skip non-essential content (ads, comments)
   - Implement max characters per article

4. **Caching**
   - Cache generated audio for articles
   - Avoid regenerating same content

### Challenge 4: Browser Automation in Docker

**Risk: MEDIUM**
- Headless browsers challenging in Docker
- Display server (X11) required for headed mode
- Chrome/Chromium dependencies

**Mitigation Strategies:**

1. **Use Xvfb (Virtual Framebuffer)**
   ```dockerfile
   RUN apt-get update && apt-get install -y \
       chromium \
       chromium-driver \
       xvfb \
       && rm -rf /var/lib/apt/lists/*
   ```

2. **Run Chrome with Xvfb**
   ```bash
   xvfb-run -a dotnet TermReader.dll
   ```

3. **Docker Compose with Display**
   ```yaml
   services:
     scraper:
       build: .
       environment:
         - DISPLAY=:99
       volumes:
         - ./output:/app/output
       command: xvfb-run -a dotnet TermReader.dll
   ```

4. **Increase Shared Memory**
   ```yaml
   services:
     scraper:
       shm_size: '2gb'  # Prevent Chrome crashes
   ```

### Challenge 5: Testing Without Real NYT Access

**Risk: LOW**
- CI/CD can't access NYT without credentials
- Tests need to work offline
- HTML structure may change

**Mitigation Strategies:**

1. **HTML Fixtures**
   ```csharp
   // Store real HTML samples (with PII removed)
   var html = File.ReadAllText("Fixtures/nyt-today-paper-2024-10-23.html");
   ```

2. **Mock HTTP Responses**
   ```csharp
   var mockHttp = Substitute.For<IHttpClient>();
   mockHttp.GetAsync(Arg.Any<string>()).Returns(fixtureHtml);
   ```

3. **Structural Tests**
   ```csharp
   [Fact]
   public void ParseArticle_HasExpectedSelectors_ExtractsData()
   {
       // Test that parser handles expected HTML structure
       var doc = new HtmlDocument();
       doc.LoadHtml(fixtureHtml);
       
       Assert.NotNull(doc.DocumentNode.SelectSingleNode("//h1"));
       Assert.NotNull(doc.DocumentNode.SelectSingleNode("//article"));
   }
   ```

4. **Manual Integration Test Flag**
   ```csharp
   [Fact(Skip = "Requires real NYT credentials")]
   public async Task Integration_RealNYT_ScrapesSuccessfully()
   {
       // Only run manually with real credentials
   }
   ```

### Challenge 6: Audio File Size

**Risk: LOW**
- Large audio files slow to transfer
- Storage costs
- Bandwidth for mobile users

**Mitigation Strategies:**

1. **Optimal Compression**
   - 64 kbps mono for voice content
   - AAC codec (better than MP3 at same bitrate)
   - Target: ~28 MB per hour

2. **Streaming Support**
   - Support HTTP range requests
   - Enable progressive download
   - Consider HLS for large files

3. **Cleanup Old Files**
   ```csharp
   public async Task CleanupOldFilesAsync()
   {
       var files = Directory.GetFiles(outputDir, "*.m4b")
           .Where(f => File.GetCreationTime(f) < DateTime.Now.AddDays(-7));
       
       foreach (var file in files)
       {
           File.Delete(file);
           _logger.LogInformation("Deleted old file: {File}", file);
       }
   }
   ```

### Challenge 7: Long-Running AI Agent Development

**Risk: MEDIUM**
- Agents may get stuck on issues
- Code may diverge from requirements
- Merge conflicts between agents

**Mitigation Strategies:**

1. **Clear Task Boundaries**
   - Each agent has well-defined scope
   - Minimal overlap between agents
   - Clear interfaces/contracts defined upfront

2. **Frequent Commits**
   - Commit after each passing test
   - Atomic commits easy to review/revert
   - Reduces risk of large failures

3. **Checkpoint System**
   - Save state after each completed phase
   - Can revert to last known good state
   - Tag releases in Git

4. **Progress Monitoring**
   - Agents report progress in PR comments
   - Test pass rates tracked
   - Opus reviews regularly (batch of 3-5)

5. **Failure Recovery**
   ```
   If stuck after 3 attempts:
     1. Log issue in PR
     2. Request Opus review
     3. Human intervention if needed
   ```

---

## 9. Deployment & Operations

### Running Locally (Development)

```bash
# Set up user secrets
dotnet user-secrets init
dotnet user-secrets set "NYT:Email" "your-email@example.com"
dotnet user-secrets set "NYT:Password" "your-password"
dotnet user-secrets set "ElevenLabs:ApiKey" "your-api-key"

# Run application
dotnet run --project src/TermReader

# Run tests
dotnet test
```

### Running with Docker

```bash
# Build image
docker build -t termreader:latest .

# Run container
docker run --rm \
  -e NYT_EMAIL="your-email@example.com" \
  -e NYT_PASSWORD="your-password" \
  -e ELEVEN_LABS_API_KEY="your-api-key" \
  -v $(pwd)/output:/app/output \
  termreader:latest

# Check output
ls -lh output/
```

### Docker Compose Setup

**docker-compose.yml:**
```yaml
version: '3.8'

services:
  nyt-scraper:
    build:
      context: .
      dockerfile: Dockerfile
    environment:
      - NYT_EMAIL=${NYT_EMAIL}
      - NYT_PASSWORD=${NYT_PASSWORD}
      - ELEVEN_LABS_API_KEY=${ELEVEN_LABS_API_KEY}
      - DISPLAY=:99
    volumes:
      - ./output:/app/output
      - ./logs:/app/logs
    shm_size: '2gb'
    command: xvfb-run -a dotnet TermReader.dll

# Run with:
# docker-compose up
```

### Scheduled Execution (Cron)

**On Linux/Mac:**
```bash
# Run daily at 6 AM
0 6 * * * cd /path/to/project && docker-compose up -d
```

**On Windows (Task Scheduler):**
```powershell
# Create scheduled task to run daily
$action = New-ScheduledTaskAction -Execute "docker-compose" -Argument "up -d"
$trigger = New-ScheduledTaskTrigger -Daily -At 6am
Register-ScheduledTask -Action $action -Trigger $trigger -TaskName "TermReader"
```

### Monitoring & Logging

**Serilog Configuration:**
```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "theme": "Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme::Code, Serilog.Sinks.Console"
        }
      },
      {
        "Name": "File",
        "Args": {
          "path": "logs/nyt-scraper-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 7,
          "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
        }
      }
    ]
  }
}
```

**Key Metrics to Log:**
- Articles scraped count
- Audio generation duration
- File size generated
- API credits consumed
- Errors encountered
- Execution time per phase

---

## 10. Success Metrics & Acceptance Criteria

### Functional Requirements

- ✅ Authenticates to NYT as subscriber
- ✅ Scrapes 10+ articles from Today's Paper
- ✅ Extracts title, author, content correctly
- ✅ Generates audio using Eleven Labs
- ✅ Creates M4B file with chapter markers
- ✅ Chapters work on iOS (Apple Podcasts/Books)
- ✅ Chapters work on Android (AntennaPod)
- ✅ Audio quality is clear and understandable

### Performance Requirements

- ✅ Complete workflow in < 15 minutes (for 10 articles)
- ✅ Audio file size: ~28 MB per hour
- ✅ No memory leaks (stable memory usage)
- ✅ Docker image size < 500 MB

### Code Quality Requirements

- ✅ Unit test coverage > 80%
- ✅ All integration tests pass
- ✅ No compiler warnings
- ✅ All linters pass (dotnet format)
- ✅ Follows Clean Architecture principles
- ✅ All interfaces documented (XML comments)

### Security Requirements

- ✅ No credentials in source code
- ✅ No credentials in Docker image
- ✅ Runs as non-root user in Docker
- ✅ No security vulnerabilities (Trivy scan)
- ✅ Proper error handling (no stack traces exposed)

### AI Agent Workflow Requirements

- ✅ All commits follow Conventional Commits
- ✅ Atomic commits (< 500 lines changed)
- ✅ All PRs have clear descriptions
- ✅ Opus reviews all code before merge
- ✅ Tests pass before each merge
- ✅ No merge conflicts

---

## 11. Timeline & Milestones

### Phase 1: Foundation (Week 1)
- **Day 1-2:** Project setup, DI, logging, configuration
- **Day 3-4:** Domain entities, interfaces, project structure
- **Day 5:** Testing setup, CI/CD pipeline
- **Milestone:** Application starts, DI resolves, logs work

### Phase 2: Scraper (Week 2)
- **Day 1-2:** Browser automation setup, anti-detection
- **Day 3-4:** NYT authentication, HTML parsing
- **Day 5:** Integration tests, fixture creation
- **Milestone:** Scrapes 10+ articles successfully

### Phase 3: Audio Generation (Week 3)
- **Day 1-2:** Eleven Labs integration, error handling
- **Day 3:** FFmpeg integration, audio concatenation
- **Day 4-5:** Format conversion, optimization
- **Milestone:** Generates M4B file

### Phase 4: Chapter Markers (Week 3-4)
- **Day 1-2:** ATL.NET integration, chapter calculation
- **Day 3:** Device testing (iOS, Android)
- **Day 4:** Bug fixes, compatibility
- **Milestone:** Chapters work on all devices

### Phase 5: Integration (Week 4)
- **Day 1-2:** Workflow orchestration, MediatR
- **Day 3:** End-to-end testing
- **Day 4:** Performance optimization
- **Day 5:** Error handling, logging
- **Milestone:** Complete workflow executes

### Phase 6: Docker & Deployment (Week 5)
- **Day 1-2:** Dockerfile, multi-stage build
- **Day 3:** Docker Compose, volume management
- **Day 4:** Browser in Docker (Xvfb)
- **Day 5:** Documentation, deployment guide
- **Milestone:** Production-ready Docker image

---

## 12. Reference Documentation

### Essential Reading for Claude Code Agents

1. **C# & .NET**
   - https://learn.microsoft.com/en-us/dotnet/csharp/
   - https://learn.microsoft.com/en-us/aspnet/core/

2. **Clean Architecture**
   - https://github.com/jasontaylordev/CleanArchitecture
   - Milan Jovanović's blog: https://www.milanjovanovic.tech/

3. **Browser Automation**
   - Selenium: https://www.selenium.dev/documentation/
   - Playwright: https://playwright.dev/dotnet/

4. **Audio Processing**
   - Eleven Labs: https://elevenlabs.io/docs/api-reference/introduction
   - FFmpeg: https://ffmpeg.org/documentation.html
   - ATL.NET: https://github.com/Zeugma440/atldotnet

5. **Testing**
   - xUnit: https://xunit.net/
   - NSubstitute: https://nsubstitute.github.io/

6. **Docker**
   - .NET Docker Guide: https://learn.microsoft.com/en-us/dotnet/core/docker/

---

## 13. Critical Notes for Claude Code

### Multi-Agent Coordination

**Opus Agent (YOU) Responsibilities:**
- Read this entire document before starting
- Assign tasks to Sonnet/Haiku agents based on Phase breakdown
- Review code in batches (3-5 PRs at once)
- Provide specific, actionable feedback
- Make architectural decisions
- Resolve conflicts between agents

**Sonnet Agent Guidelines:**
- Focus on one component at a time
- Write tests first (TDD)
- Commit after each passing test
- Request Opus review when stuck (after 3 attempts)
- Update PR description with progress

**Haiku Agent Guidelines:**
- Handle simple, well-defined tasks
- Follow established patterns
- Don't make architectural decisions
- Request Sonnet/Opus review if uncertain

### Common Pitfalls to Avoid

1. **Over-Engineering**
   - Don't add unnecessary abstractions
   - Keep it simple initially
   - Refactor when patterns emerge

2. **Testing External Services**
   - Never call real APIs in tests
   - Always mock external dependencies
   - Use fixtures for HTML parsing tests

3. **Credential Exposure**
   - Never commit secrets
   - Always use environment variables
   - Check .gitignore before commits

4. **Ignoring Rate Limits**
   - Respect NYT's servers
   - Respect Eleven Labs API limits
   - Implement proper delays

5. **Skipping Error Handling**
   - Every API call needs try-catch
   - Log all errors with context
   - Implement retry logic

---

## 14. Getting Started Checklist

**Before Starting Development:**

- [ ] Review entire project plan
- [ ] Understand Clean Architecture principles
- [ ] Read about NYT legal considerations
- [ ] Familiarize with Eleven Labs pricing
- [ ] Set up GitHub repository
- [ ] Configure branch protection rules
- [ ] Install required tools (Docker, .NET 8)

**Phase 1 Kickoff:**

- [ ] Assign Sonnet Agent 1 to project setup
- [ ] Assign Sonnet Agent 2 to domain layer
- [ ] Assign Haiku Agent 1 to configuration
- [ ] Create feature branches
- [ ] Set up CI/CD pipeline

**Ongoing:**

- [ ] Review PRs in batches
- [ ] Monitor test coverage
- [ ] Track progress in project board
- [ ] Update this document as needed
- [ ] Celebrate milestones! 🎉

---

## Conclusion

This comprehensive project plan provides everything needed to build a production-quality NYT audio scraper using modern C# practices and Claude Code's multi-agent architecture. The plan emphasizes:

- **Clean, maintainable architecture** with proper separation of concerns
- **Robust testing strategies** that work without external dependencies
- **Security best practices** for credential management
- **Practical anti-detection measures** for web scraping
- **Efficient multi-agent workflows** for long-running AI development
- **Clear GitHub practices** for code review and versioning

**Key Success Factor:** Follow TDD religiously—tests drive implementation, not the other way around. This prevents AI agents from "gaming" tests and ensures robust, maintainable code.

**Legal Reminder:** NYT prohibits automated scraping. This project is for educational purposes and personal subscriber use only. Consider requesting official API access.

**Next Steps:**
1. Create GitHub repository
2. Review this plan with all agents
3. Begin Phase 1: Foundation
4. Iterate with frequent commits and reviews

Good luck with your Claude Code development! 🚀