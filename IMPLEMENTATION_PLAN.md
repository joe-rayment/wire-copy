# Detailed Implementation Plan for Remaining Priorities

## Executive Summary

This document provides a comprehensive evaluation and implementation plan for the 5 remaining sustainability/scalability improvements:

- **Priority 7**: Persistence Layer with SQLite
- **Priority 8**: Parallel Processing for Audio Generation
- **Priority 9**: Monitoring, Health Checks & Observability
- **Priority 13**: Intelligent API Rate Limiting
- **Priority 14**: Caching Layer for Articles & Audio
- **Priority 15**: Cookie Encryption & Management

**Recommended Implementation Order**: 7 → 8 → 13 → 9 → 14 → 15

**Estimated Total Effort**: 3-4 days (20-28 hours)

---

## Priority 7: Persistence Layer with SQLite

### 🎯 Objective
Implement a lightweight persistence layer to track scraping sessions, enable resume functionality, and maintain audit history.

### 📊 Current State Analysis

**What Exists:**
- ✅ `ScrapingSession` entity defined (`ScrapingSession.cs`)
- ✅ Entity has all necessary properties (Id, StartedAt, CompletedAt, Articles, Status, etc.)
- ❌ No repository pattern implementation
- ❌ No database context or persistence logic
- ❌ Sessions lost on application restart

**Pain Points:**
1. Cannot resume interrupted scraping sessions
2. No historical tracking of API spending over time
3. Cannot prevent duplicate article processing
4. No audit trail for debugging

### 🛠️ Implementation Plan

#### Phase 1: Add Dependencies (15 min)
```xml
<!-- Add to Infrastructure.csproj -->
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.0" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="9.0.0" />
```

#### Phase 2: Create DbContext (30 min)
```csharp
// File: Infrastructure/Data/AppDbContext.cs
public class AppDbContext : DbContext
{
    public DbSet<ScrapingSession> ScrapingSessions { get; set; }
    public DbSet<Article> Articles { get; set; }
    public DbSet<BudgetHistory> BudgetHistory { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite("Data Source=nyt_scraper.db");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure ScrapingSession
        modelBuilder.Entity<ScrapingSession>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasMany(e => e.Articles)
                  .WithOne()
                  .HasForeignKey("ScrapingSessionId");
            entity.HasIndex(e => e.StartedAt);
            entity.HasIndex(e => e.Status);
        });

        // Configure Article
        modelBuilder.Entity<Article>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Content).HasMaxLength(500000);
            entity.HasIndex(e => e.Url).IsUnique();
            entity.HasIndex(e => e.ScrapedDate);
        });

        // Configure BudgetHistory
        modelBuilder.Entity<BudgetHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Date);
        });
    }
}
```

#### Phase 3: Create Repository Pattern (1 hour)
```csharp
// File: Application/Interfaces/ISessionRepository.cs
public interface ISessionRepository
{
    Task<ScrapingSession?> GetByIdAsync(string id);
    Task<IEnumerable<ScrapingSession>> GetRecentSessionsAsync(int count = 10);
    Task<ScrapingSession?> GetInProgressSessionAsync();
    Task<ScrapingSession> CreateAsync(ScrapingSession session);
    Task UpdateAsync(ScrapingSession session);
    Task<bool> ArticleExistsAsync(string url);
    Task<decimal> GetTotalSpendingAsync(DateTime since);
}

// File: Infrastructure/Data/SessionRepository.cs
public class SessionRepository : ISessionRepository
{
    private readonly AppDbContext _context;
    private readonly ILogger<SessionRepository> _logger;

    public async Task<ScrapingSession> CreateAsync(ScrapingSession session)
    {
        _context.ScrapingSessions.Add(session);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Created session {SessionId}", session.Id);
        return session;
    }

    public async Task<bool> ArticleExistsAsync(string url)
    {
        return await _context.Articles.AnyAsync(a => a.Url == url);
    }

    // ... implement other methods
}
```

#### Phase 4: Add BudgetHistory Entity (30 min)
```csharp
// File: Domain/Entities/BudgetHistory.cs
public class BudgetHistory
{
    public required string Id { get; init; }
    public required DateTime Date { get; init; }
    public required decimal AmountSpent { get; init; }
    public required int CharactersProcessed { get; init; }
    public required string SessionId { get; init; }
    public string? VoiceId { get; init; }
}
```

#### Phase 5: Integrate with BudgetService (30 min)
```csharp
// Update Infrastructure/Audio/BudgetService.cs
public class BudgetService
{
    private readonly ISessionRepository _sessionRepository;

    public async Task RecordExpenseAsync(decimal amount, int characters, string sessionId)
    {
        lock (_lock)
        {
            _totalSpent += amount;
            _logger.LogInformation("Recorded expense: ${Amount:F4}", amount);
        }

        // Persist to database
        var history = new BudgetHistory
        {
            Id = Guid.NewGuid().ToString(),
            Date = DateTime.UtcNow,
            AmountSpent = amount,
            CharactersProcessed = characters,
            SessionId = sessionId
        };
        await _sessionRepository.SaveBudgetHistoryAsync(history);
    }

    public async Task<decimal> GetMonthlySpendingAsync()
    {
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        return await _sessionRepository.GetTotalSpendingAsync(startOfMonth);
    }
}
```

#### Phase 6: Update Program.cs for Session Management (45 min)
```csharp
// In RunProductionWorkflowAsync:

// Check for existing in-progress session
var sessionRepo = services.GetRequiredService<ISessionRepository>();
var existingSession = await sessionRepo.GetInProgressSessionAsync();

if (existingSession != null)
{
    Log.Warning("Found existing in-progress session: {SessionId}", existingSession.Id);
    Log.Information("Resume session? (y/n)");
    // Handle resume logic
}

// Create new session
var session = new ScrapingSession
{
    Id = Guid.NewGuid().ToString(),
    StartedAt = DateTime.UtcNow,
    Articles = new List<Article>(),
    Status = ScrapingStatus.InProgress
};
await sessionRepo.CreateAsync(session);

try
{
    // ... existing scraping logic ...

    // Update session on completion
    session.Status = ScrapingStatus.Completed;
    session.CompletedAt = DateTime.UtcNow;
    session.OutputFilePath = audiobookPath;
    await sessionRepo.UpdateAsync(session);
}
catch (Exception ex)
{
    session.Status = ScrapingStatus.Failed;
    session.ErrorMessage = ex.Message;
    await sessionRepo.UpdateAsync(session);
}
```

#### Phase 7: Add Migration Commands (15 min)
```bash
# Add to CLAUDE.md
dotnet ef migrations add InitialCreate --project src/NYTAudioScraper.Infrastructure
dotnet ef database update --project src/NYTAudioScraper.Infrastructure
```

### ✅ Acceptance Criteria
- [ ] Database created automatically on first run
- [ ] Sessions persisted with all metadata
- [ ] Can query historical sessions via repository
- [ ] Budget tracking across sessions works
- [ ] Duplicate article detection via URL check
- [ ] Resume functionality (manual for now)

### 🧪 Testing Strategy
```csharp
// Tests/SessionRepositoryTests.cs
[Fact]
public async Task CreateAsync_PersistsSession()
{
    var session = new ScrapingSession { /* ... */ };
    var created = await _repo.CreateAsync(session);
    var retrieved = await _repo.GetByIdAsync(created.Id);
    retrieved.Should().BeEquivalentTo(created);
}

[Fact]
public async Task ArticleExistsAsync_ReturnsTrueForDuplicate()
{
    // ... test duplicate detection
}
```

### ⚠️ Risks & Mitigations
- **Risk**: Database corruption
  - **Mitigation**: Regular backups, WAL mode enabled
- **Risk**: Concurrent access issues
  - **Mitigation**: Use connection pooling, proper locking

### 📦 Deliverables
- `AppDbContext.cs` - EF Core context
- `ISessionRepository.cs` + implementation
- `BudgetHistory.cs` - new entity
- Database migrations
- Integration in Program.cs
- 10+ repository tests

**Estimated Effort**: 4 hours

---

## Priority 8: Parallel Processing for Audio Generation

### 🎯 Objective
Enable concurrent audio generation while respecting ElevenLabs API rate limits and budget constraints.

### 📊 Current State Analysis

**What Exists:**
- ✅ Sequential processing in `Program.cs:342-391`
- ✅ BudgetService with thread-safe locking
- ✅ Circuit breaker for ElevenLabs API
- ❌ No parallel processing
- ❌ No rate limiting coordinator

**Current Performance:**
- 10 articles @ 30s each = **5 minutes** total
- ElevenLabs API supports ~5 concurrent requests
- Potential speedup: **5x faster** (1 minute instead of 5)

### 🛠️ Implementation Plan

#### Phase 1: Add Rate Limiter (45 min)
```csharp
// File: Infrastructure/Audio/RateLimiter.cs
public class RateLimiter
{
    private readonly SemaphoreSlim _semaphore;
    private readonly int _requestsPerMinute;
    private readonly Queue<DateTime> _requestTimestamps = new();
    private readonly object _lock = new();

    public RateLimiter(int maxConcurrent = 3, int requestsPerMinute = 50)
    {
        _semaphore = new SemaphoreSlim(maxConcurrent);
        _requestsPerMinute = requestsPerMinute;
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
    {
        await WaitForAvailableSlotAsync();
        await _semaphore.WaitAsync();

        try
        {
            RecordRequest();
            return await action();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task WaitForAvailableSlotAsync()
    {
        lock (_lock)
        {
            var oneMinuteAgo = DateTime.UtcNow.AddMinutes(-1);
            while (_requestTimestamps.Count > 0 &&
                   _requestTimestamps.Peek() < oneMinuteAgo)
            {
                _requestTimestamps.Dequeue();
            }

            if (_requestTimestamps.Count >= _requestsPerMinute)
            {
                var oldestRequest = _requestTimestamps.Peek();
                var waitTime = oldestRequest.AddMinutes(1) - DateTime.UtcNow;
                if (waitTime > TimeSpan.Zero)
                {
                    await Task.Delay(waitTime);
                }
            }
        }
    }

    private void RecordRequest()
    {
        lock (_lock)
        {
            _requestTimestamps.Enqueue(DateTime.UtcNow);
        }
    }
}
```

#### Phase 2: Create Parallel Audio Generator (1 hour)
```csharp
// File: Infrastructure/Audio/ParallelAudioGenerator.cs
public class ParallelAudioGenerator : IAudioGenerator
{
    private readonly IAudioGenerator _innerGenerator;
    private readonly RateLimiter _rateLimiter;
    private readonly BudgetService _budgetService;
    private readonly ILogger<ParallelAudioGenerator> _logger;

    public async Task<Dictionary<string, byte[]>> GenerateBatchAsync(
        IEnumerable<(string Id, string Content)> articles,
        string voiceId,
        CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentDictionary<string, byte[]>();
        var errors = new ConcurrentBag<(string Id, Exception Error)>();

        await Parallel.ForEachAsync(
            articles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 3,
                CancellationToken = cancellationToken
            },
            async (article, ct) =>
            {
                try
                {
                    // Check budget before processing
                    var cost = EstimateCost(article.Content);
                    if (!_budgetService.CanAfford(cost))
                    {
                        _logger.LogWarning(
                            "Skipping article {Id} - would exceed budget",
                            article.Id);
                        return;
                    }

                    // Use rate limiter to prevent API throttling
                    var audioData = await _rateLimiter.ExecuteAsync(async () =>
                    {
                        return await _innerGenerator.GenerateAudioAsync(
                            article.Content,
                            voiceId,
                            ct);
                    });

                    results[article.Id] = audioData;
                    _budgetService.RecordExpense(cost);

                    _logger.LogInformation(
                        "Generated audio for article {Id} ({Size:N0} bytes)",
                        article.Id,
                        audioData.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to generate audio for article {Id}",
                        article.Id);
                    errors.Add((article.Id, ex));
                }
            });

        if (errors.Any())
        {
            _logger.LogWarning(
                "Failed to generate audio for {Count} articles",
                errors.Count);
        }

        return results.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public decimal EstimateCost(string text)
        => _innerGenerator.EstimateCost(text);

    public Task<byte[]> GenerateAudioAsync(
        string text,
        string voiceId,
        CancellationToken cancellationToken = default)
        => _innerGenerator.GenerateAudioAsync(text, voiceId, cancellationToken);
}
```

#### Phase 3: Update Program.cs for Parallel Processing (1 hour)
```csharp
// In RunProductionWorkflowAsync:

var parallelGenerator = services.GetRequiredService<ParallelAudioGenerator>();

// Prepare articles for batch processing
var articlesToProcess = articleList
    .Select(a => (a.Id, a.Content))
    .ToList();

Log.Information("Generating audio for {Count} articles in parallel", articlesToProcess.Count);

var audioBatch = await parallelGenerator.GenerateBatchAsync(
    articlesToProcess,
    voiceId,
    cancellationToken);

// Save audio files
foreach (var (articleId, audioData) in audioBatch)
{
    var article = articleList.First(a => a.Id == articleId);
    var audioFilePath = Path.Combine(outputDir, $"{articleId}.mp3");
    await File.WriteAllBytesAsync(audioFilePath, audioData);
    audioFiles.Add(audioFilePath);

    // Get metadata and create chapter
    var metadata = await audioProcessor.GetMetadataAsync(audioFilePath);
    var chapter = new AudioChapter
    {
        Title = article.Title,
        ArticleId = article.Id,
        StartTimeMs = currentTimeMs,
        DurationMs = metadata.DurationMs,
        AudioFilePath = audioFilePath
    };
    chapters.Add(chapter);
    currentTimeMs += metadata.DurationMs;
}
```

#### Phase 4: Add Configuration (15 min)
```json
// appsettings.json
"Audio": {
  "ParallelProcessing": {
    "MaxConcurrentRequests": 3,
    "RequestsPerMinute": 50,
    "Enabled": true
  }
}
```

### ✅ Acceptance Criteria
- [ ] Parallel processing respects API rate limits
- [ ] Budget checking is thread-safe
- [ ] Failed articles don't block others
- [ ] Graceful degradation if API throttles
- [ ] Performance improvement measured
- [ ] Can disable via configuration

### 🧪 Testing Strategy
```csharp
[Fact]
public async Task GenerateBatchAsync_ProcessesInParallel()
{
    var articles = Enumerable.Range(1, 10)
        .Select(i => (Id: $"article-{i}", Content: $"Content {i}"));

    var stopwatch = Stopwatch.StartNew();
    var results = await _generator.GenerateBatchAsync(articles, "voice-id");
    stopwatch.Stop();

    results.Should().HaveCount(10);
    stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    // Sequential would take ~30s, parallel ~10s
}

[Fact]
public async Task RateLimiter_EnforcesLimit()
{
    // Test that rate limiter prevents exceeding limits
}
```

### ⚠️ Risks & Mitigations
- **Risk**: API rate limit violations
  - **Mitigation**: Conservative defaults, token bucket algorithm
- **Risk**: Budget overspending due to concurrency
  - **Mitigation**: Thread-safe budget checks before each request
- **Risk**: Memory pressure with large batches
  - **Mitigation**: Limit max concurrent to 3-5

### 📦 Deliverables
- `RateLimiter.cs` - Token bucket rate limiter
- `ParallelAudioGenerator.cs` - Parallel wrapper
- Updated `Program.cs` integration
- Configuration section
- Performance benchmarks
- 8+ tests for parallel logic

**Estimated Effort**: 3 hours

---

## Priority 13: Intelligent API Rate Limiting

### 🎯 Objective
Implement adaptive rate limiting that respects `Retry-After` headers and adjusts based on API responses.

### 📊 Current State Analysis

**What Exists:**
- ✅ Fixed 3-second delays (`RateLimitDelayMs`)
- ✅ Circuit breaker after 5 failures
- ✅ Exponential backoff retry (2^n seconds)
- ❌ No `Retry-After` header parsing
- ❌ No adaptive rate adjustment

**API Limits:**
- ElevenLabs: 50 requests/minute (Creator tier)
- NYT: Unknown, but conservative 3s delays work

### 🛠️ Implementation Plan

#### Phase 1: Enhanced Rate Limiter (1 hour)
```csharp
// File: Infrastructure/Resilience/AdaptiveRateLimiter.cs
public class AdaptiveRateLimiter
{
    private readonly SemaphoreSlim _semaphore;
    private readonly ILogger<AdaptiveRateLimiter> _logger;
    private int _currentDelayMs;
    private int _minDelayMs;
    private int _maxDelayMs;
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly object _lock = new();

    public AdaptiveRateLimiter(
        int minDelayMs = 1000,
        int maxDelayMs = 10000,
        int maxConcurrent = 3,
        ILogger<AdaptiveRateLimiter> logger)
    {
        _minDelayMs = minDelayMs;
        _maxDelayMs = maxDelayMs;
        _currentDelayMs = minDelayMs;
        _semaphore = new SemaphoreSlim(maxConcurrent);
        _logger = logger;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<Task<HttpResponseMessage>> httpCall,
        Func<HttpResponseMessage, Task<T>> processResponse)
    {
        await _semaphore.WaitAsync();

        try
        {
            await EnforceDelayAsync();

            var response = await httpCall();

            // Adjust rate based on response
            AdjustRateFromResponse(response);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await HandleRateLimitAsync(response);
                    // Retry after waiting
                    response = await httpCall();
                }
            }

            return await processResponse(response);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task EnforceDelayAsync()
    {
        lock (_lock)
        {
            var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
            var requiredDelay = TimeSpan.FromMilliseconds(_currentDelayMs);

            if (timeSinceLastRequest < requiredDelay)
            {
                var waitTime = requiredDelay - timeSinceLastRequest;
                Task.Delay(waitTime).Wait();
            }

            _lastRequestTime = DateTime.UtcNow;
        }
    }

    private void AdjustRateFromResponse(HttpResponseMessage response)
    {
        lock (_lock)
        {
            if (response.IsSuccessStatusCode)
            {
                // Gradually decrease delay on success (up to 10%)
                _currentDelayMs = Math.Max(
                    _minDelayMs,
                    (int)(_currentDelayMs * 0.95));

                _logger.LogDebug(
                    "Success - decreased delay to {Delay}ms",
                    _currentDelayMs);
            }
            else if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Double delay on rate limit
                _currentDelayMs = Math.Min(
                    _maxDelayMs,
                    _currentDelayMs * 2);

                _logger.LogWarning(
                    "Rate limited - increased delay to {Delay}ms",
                    _currentDelayMs);
            }
        }
    }

    private async Task HandleRateLimitAsync(HttpResponseMessage response)
    {
        // Parse Retry-After header
        TimeSpan waitTime;

        if (response.Headers.RetryAfter?.Delta.HasValue == true)
        {
            waitTime = response.Headers.RetryAfter.Delta.Value;
            _logger.LogWarning(
                "Rate limited. Retry-After header specifies {Seconds}s wait",
                waitTime.TotalSeconds);
        }
        else if (response.Headers.RetryAfter?.Date.HasValue == true)
        {
            waitTime = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            _logger.LogWarning(
                "Rate limited. Retry-After date is {Date}",
                response.Headers.RetryAfter.Date.Value);
        }
        else
        {
            // Fallback to exponential backoff
            waitTime = TimeSpan.FromMilliseconds(_currentDelayMs);
            _logger.LogWarning(
                "Rate limited. No Retry-After header, using {Seconds}s wait",
                waitTime.TotalSeconds);
        }

        await Task.Delay(waitTime);
    }
}
```

#### Phase 2: Integrate with AudioGenerator (30 min)
```csharp
// Update Infrastructure/Audio/AudioGenerator.cs
public class AudioGenerator : IAudioGenerator
{
    private readonly AdaptiveRateLimiter _rateLimiter;

    public async Task<byte[]> GenerateAudioAsync(...)
    {
        return await _rateLimiter.ExecuteAsync(
            httpCall: async () => await _httpClient.PostAsync(url, content, cancellationToken),
            processResponse: async (response) =>
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(cancellationToken);
            });
    }
}
```

#### Phase 3: Add Rate Limit Metrics (30 min)
```csharp
// File: Infrastructure/Resilience/RateLimitMetrics.cs
public class RateLimitMetrics
{
    public int TotalRequests { get; private set; }
    public int RateLimitedRequests { get; private set; }
    public int SuccessfulRequests { get; private set; }
    public TimeSpan AverageDelay { get; private set; }
    public DateTime LastRateLimitedAt { get; private set; }

    public void RecordRequest(bool success, bool rateLimited, TimeSpan delay)
    {
        TotalRequests++;
        if (success) SuccessfulRequests++;
        if (rateLimited)
        {
            RateLimitedRequests++;
            LastRateLimitedAt = DateTime.UtcNow;
        }
        // Update average delay
    }

    public string GetSummary() =>
        $"Requests: {TotalRequests} " +
        $"(Success: {SuccessfulRequests}, " +
        $"Rate Limited: {RateLimitedRequests}) " +
        $"Avg Delay: {AverageDelay.TotalMilliseconds:F0}ms";
}
```

### ✅ Acceptance Criteria
- [ ] Respects `Retry-After` headers from ElevenLabs
- [ ] Adaptive delay adjustment based on responses
- [ ] Metrics tracking for rate limit events
- [ ] Graceful handling of 429 responses
- [ ] Configurable min/max delays

### 🧪 Testing Strategy
```csharp
[Fact]
public async Task HandleRateLimitAsync_RespectsRetryAfterHeader()
{
    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));

    var stopwatch = Stopwatch.StartNew();
    await _rateLimiter.HandleRateLimitAsync(response);
    stopwatch.Stop();

    stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(4.5));
}
```

### 📦 Deliverables
- `AdaptiveRateLimiter.cs`
- `RateLimitMetrics.cs`
- Integration with AudioGenerator
- Configuration options
- 6+ tests

**Estimated Effort**: 2 hours

---

## Priority 9: Monitoring, Health Checks & Observability

### 🎯 Objective
Add comprehensive monitoring, health checks, and observability to track application health and performance.

### 📊 Current State Analysis

**What Exists:**
- ✅ Serilog structured logging
- ✅ Console and file log sinks
- ❌ No health checks
- ❌ No metrics collection
- ❌ No performance counters

### 🛠️ Implementation Plan

#### Phase 1: Add Health Check Infrastructure (1 hour)
```csharp
// Add packages to API.csproj
<PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="9.0.0" />
<PackageReference Include="AspNetCore.HealthChecks.System" Version="8.0.1" />

// File: Infrastructure/Health/FFmpegHealthCheck.cs
public class FFmpegHealthCheck : IHealthCheck
{
    private readonly ILogger<FFmpegHealthCheck> _logger;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                return HealthCheckResult.Healthy(
                    "FFmpeg is available",
                    new Dictionary<string, object>
                    {
                        ["version"] = output.Split('\n')[0]
                    });
            }

            return HealthCheckResult.Unhealthy("FFmpeg returned non-zero exit code");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFmpeg health check failed");
            return HealthCheckResult.Unhealthy(
                "FFmpeg is not available",
                ex);
        }
    }
}

// File: Infrastructure/Health/ChromeDriverHealthCheck.cs
public class ChromeDriverHealthCheck : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(...)
    {
        // Similar check for chromedriver availability
    }
}

// File: Infrastructure/Health/ElevenLabsHealthCheck.cs
public class ElevenLabsHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(...)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ElevenLabs");
            var response = await client.GetAsync("v1/user", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("ElevenLabs API is accessible");
            }

            return HealthCheckResult.Degraded(
                $"ElevenLabs API returned {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "ElevenLabs API is not accessible",
                ex);
        }
    }
}

// File: Infrastructure/Health/DiskSpaceHealthCheck.cs
public class DiskSpaceHealthCheck : IHealthCheck
{
    private readonly long _minimumFreeMB;

    public DiskSpaceHealthCheck(long minimumFreeMB = 1000)
    {
        _minimumFreeMB = minimumFreeMB;
    }

    public Task<HealthCheckResult> CheckHealthAsync(...)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory)!);
        var freeMB = drive.AvailableFreeSpace / (1024 * 1024);

        if (freeMB >= _minimumFreeMB)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                $"{freeMB:N0} MB free",
                new Dictionary<string, object>
                {
                    ["freeSpaceMB"] = freeMB,
                    ["totalSpaceMB"] = drive.TotalSize / (1024 * 1024)
                }));
        }

        return Task.FromResult(HealthCheckResult.Degraded(
            $"Low disk space: {freeMB:N0} MB free (minimum: {_minimumFreeMB} MB)"));
    }
}
```

#### Phase 2: Register Health Checks (30 min)
```csharp
// In DependencyInjection.cs
services.AddHealthChecks()
    .AddCheck<FFmpegHealthCheck>("ffmpeg")
    .AddCheck<ChromeDriverHealthCheck>("chromedriver")
    .AddCheck<ElevenLabsHealthCheck>("elevenlabs")
    .AddCheck<DiskSpaceHealthCheck>("disk_space")
    .AddCheck<DatabaseHealthCheck>("database");

// In Program.cs, before running workflow:
var healthCheckService = services.GetRequiredService<HealthCheckService>();
var healthReport = await healthCheckService.CheckHealthAsync();

foreach (var entry in healthReport.Entries)
{
    var status = entry.Value.Status switch
    {
        HealthStatus.Healthy => "✓",
        HealthStatus.Degraded => "⚠",
        HealthStatus.Unhealthy => "✗",
        _ => "?"
    };

    Log.Information(
        "{Status} {Name}: {Description}",
        status,
        entry.Key,
        entry.Value.Description);
}

if (healthReport.Status == HealthStatus.Unhealthy)
{
    Log.Error("Health check failed. Cannot proceed.");
    return 1;
}
```

#### Phase 3: Add Performance Metrics (1.5 hours)
```csharp
// File: Infrastructure/Metrics/PerformanceMetrics.cs
public class PerformanceMetrics
{
    private readonly ConcurrentDictionary<string, List<TimeSpan>> _timings = new();
    private readonly ConcurrentDictionary<string, long> _counters = new();

    public IDisposable Measure(string operation)
    {
        return new MetricScope(this, operation);
    }

    public void RecordTiming(string operation, TimeSpan duration)
    {
        _timings.AddOrUpdate(
            operation,
            new List<TimeSpan> { duration },
            (_, list) => { list.Add(duration); return list; });
    }

    public void Increment(string counter, long value = 1)
    {
        _counters.AddOrUpdate(counter, value, (_, current) => current + value);
    }

    public MetricsSummary GetSummary()
    {
        return new MetricsSummary
        {
            Operations = _timings.ToDictionary(
                kvp => kvp.Key,
                kvp => new OperationMetrics
                {
                    Count = kvp.Value.Count,
                    Average = TimeSpan.FromMilliseconds(kvp.Value.Average(t => t.TotalMilliseconds)),
                    Min = kvp.Value.Min(),
                    Max = kvp.Value.Max(),
                    P95 = CalculatePercentile(kvp.Value, 0.95)
                }),
            Counters = _counters.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };
    }

    private class MetricScope : IDisposable
    {
        private readonly PerformanceMetrics _metrics;
        private readonly string _operation;
        private readonly Stopwatch _stopwatch;

        public MetricScope(PerformanceMetrics metrics, string operation)
        {
            _metrics = metrics;
            _operation = operation;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _metrics.RecordTiming(_operation, _stopwatch.Elapsed);
        }
    }
}

// Usage in Program.cs:
var metrics = services.GetRequiredService<PerformanceMetrics>();

using (metrics.Measure("scraping"))
{
    articles = await scraper.ScrapeArticlesAsync(options.ArticleCount);
}

using (metrics.Measure("audio_generation"))
{
    // ... generate audio
}

// At the end:
var summary = metrics.GetSummary();
Log.Information("Performance Summary:");
foreach (var (operation, stats) in summary.Operations)
{
    Log.Information(
        "  {Operation}: {Count} calls, avg {Avg}ms, p95 {P95}ms",
        operation,
        stats.Count,
        stats.Average.TotalMilliseconds,
        stats.P95.TotalMilliseconds);
}
```

#### Phase 4: Add Structured Logging Enhancements (30 min)
```csharp
// Update appsettings.json with enrichers
"Serilog": {
  "Enrich": ["FromLogContext", "WithMachineName", "WithThreadId"],
  "Properties": {
    "Application": "NYTAudioScraper",
    "Environment": "Production"
  }
}

// Add correlation IDs to logging
public class CorrelationIdMiddleware
{
    public static string CurrentCorrelationId { get; set; } = Guid.NewGuid().ToString();

    public static IDisposable BeginScope(string operation)
    {
        CurrentCorrelationId = Guid.NewGuid().ToString();
        return LogContext.PushProperty("CorrelationId", CurrentCorrelationId);
    }
}

// Usage:
using (CorrelationIdMiddleware.BeginScope("scraping_session"))
{
    // All logs in this scope will have the same CorrelationId
    Log.Information("Starting scraping session");
    // ...
}
```

### ✅ Acceptance Criteria
- [ ] Health checks run before workflow starts
- [ ] FFmpeg, ChromeDriver, ElevenLabs connectivity verified
- [ ] Disk space checked
- [ ] Performance metrics collected
- [ ] Metrics summary logged at end
- [ ] Correlation IDs in all logs

### 🧪 Testing Strategy
```csharp
[Fact]
public async Task FFmpegHealthCheck_ReturnsHealthy_WhenInstalled()
{
    var healthCheck = new FFmpegHealthCheck(_logger);
    var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());
    result.Status.Should().Be(HealthStatus.Healthy);
}
```

### 📦 Deliverables
- 5 health check classes
- `PerformanceMetrics.cs`
- Integration in Program.cs
- Enhanced logging configuration
- 10+ tests

**Estimated Effort**: 3 hours

---

## Priority 14: Caching Layer for Articles & Audio

### 🎯 Objective
Implement caching to avoid re-scraping articles and re-generating audio for duplicate content.

### 📊 Current State Analysis

**What Exists:**
- ❌ No article caching
- ❌ No audio caching
- ❌ Re-generates everything on each run

**Potential Savings:**
- Avoid re-scraping same article: **3s/article**
- Avoid re-generating audio: **$0.30/1000 chars + 30s/article**

### 🛠️ Implementation Plan

#### Phase 1: Add Caching Infrastructure (45 min)
```csharp
// Add package
<PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="9.0.0" />

// File: Infrastructure/Caching/ArticleCache.cs
public interface IArticleCache
{
    Task<Article?> GetAsync(string url);
    Task SetAsync(string url, Article article, TimeSpan? expiration = null);
    Task<bool> ExistsAsync(string url);
}

public class ArticleCache : IArticleCache
{
    private readonly IMemoryCache _cache;
    private readonly ISessionRepository _repository;
    private readonly ILogger<ArticleCache> _logger;

    public ArticleCache(
        IMemoryCache cache,
        ISessionRepository repository,
        ILogger<ArticleCache> logger)
    {
        _cache = cache;
        _repository = repository;
        _logger = logger;
    }

    public async Task<Article?> GetAsync(string url)
    {
        // Check memory cache first
        if (_cache.TryGetValue(url, out Article? cachedArticle))
        {
            _logger.LogDebug("Article cache hit (memory): {Url}", url);
            return cachedArticle;
        }

        // Check database
        var article = await _repository.GetArticleByUrlAsync(url);
        if (article != null)
        {
            _logger.LogDebug("Article cache hit (database): {Url}", url);

            // Populate memory cache
            var cacheOptions = new MemoryCacheEntryOptions()
                .SetSlidingExpiration(TimeSpan.FromHours(1));
            _cache.Set(url, article, cacheOptions);

            return article;
        }

        _logger.LogDebug("Article cache miss: {Url}", url);
        return null;
    }

    public async Task SetAsync(string url, Article article, TimeSpan? expiration = null)
    {
        // Save to memory cache
        var cacheOptions = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(expiration ?? TimeSpan.FromHours(24));
        _cache.Set(url, article, cacheOptions);

        // Save to database
        await _repository.SaveArticleAsync(article);

        _logger.LogDebug("Cached article: {Url}", url);
    }

    public async Task<bool> ExistsAsync(string url)
    {
        if (_cache.TryGetValue(url, out _))
        {
            return true;
        }

        return await _repository.ArticleExistsAsync(url);
    }
}
```

#### Phase 2: Audio Caching (1 hour)
```csharp
// File: Infrastructure/Caching/AudioCache.cs
public interface IAudioCache
{
    Task<byte[]?> GetAsync(string contentHash);
    Task SetAsync(string contentHash, byte[] audioData);
    string ComputeHash(string content);
}

public class AudioCache : IAudioCache
{
    private readonly string _cacheDirectory;
    private readonly ILogger<AudioCache> _logger;

    public AudioCache(ILogger<AudioCache> logger)
    {
        _logger = logger;
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NYTAudioScraper",
            "AudioCache");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public string ComputeHash(string content)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }

    public async Task<byte[]?> GetAsync(string contentHash)
    {
        var filePath = Path.Combine(_cacheDirectory, $"{contentHash}.mp3");

        if (File.Exists(filePath))
        {
            _logger.LogInformation(
                "Audio cache hit: {Hash} ({Size:N0} bytes)",
                contentHash,
                new FileInfo(filePath).Length);

            return await File.ReadAllBytesAsync(filePath);
        }

        _logger.LogDebug("Audio cache miss: {Hash}", contentHash);
        return null;
    }

    public async Task SetAsync(string contentHash, byte[] audioData)
    {
        var filePath = Path.Combine(_cacheDirectory, $"{contentHash}.mp3");
        await File.WriteAllBytesAsync(filePath, audioData);

        _logger.LogInformation(
            "Cached audio: {Hash} ({Size:N0} bytes)",
            contentHash,
            audioData.Length);
    }

    public void CleanOldCache(TimeSpan maxAge)
    {
        var threshold = DateTime.UtcNow - maxAge;
        var files = Directory.GetFiles(_cacheDirectory, "*.mp3");

        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            if (fileInfo.LastAccessTime < threshold)
            {
                File.Delete(file);
                _logger.LogInformation("Deleted old cache file: {File}", file);
            }
        }
    }
}
```

#### Phase 3: Integrate with Scraping & Audio Generation (1 hour)
```csharp
// In ScraperService.ScrapeArticleByUrlAsync:
public async Task<Article?> ScrapeArticleByUrlAsync(
    string url,
    CancellationToken cancellationToken = default)
{
    // Check cache first
    var cachedArticle = await _articleCache.GetAsync(url);
    if (cachedArticle != null)
    {
        _logger.LogInformation(
            "Using cached article: {Title}",
            cachedArticle.Title);
        return cachedArticle;
    }

    // ... existing scraping logic ...

    if (article != null)
    {
        // Cache the scraped article
        await _articleCache.SetAsync(url, article);
    }

    return article;
}

// In AudioGenerator.GenerateAudioAsync:
public async Task<byte[]> GenerateAudioAsync(
    string text,
    string voiceId,
    CancellationToken cancellationToken = default)
{
    // Check cache first
    var contentHash = _audioCache.ComputeHash(text + voiceId);
    var cachedAudio = await _audioCache.GetAsync(contentHash);

    if (cachedAudio != null)
    {
        _logger.LogInformation(
            "Using cached audio ({Size:N0} bytes)",
            cachedAudio.Length);
        return cachedAudio;
    }

    // ... existing generation logic ...

    // Cache the generated audio
    await _audioCache.SetAsync(contentHash, audioData);

    return audioData;
}
```

#### Phase 4: Add Cache Management CLI Options (30 min)
```csharp
// Add to CommandOptions:
[Option("clear-cache", Required = false, HelpText = "Clear article and audio caches")]
public bool ClearCache { get; set; }

[Option("cache-stats", Required = false, HelpText = "Show cache statistics")]
public bool ShowCacheStats { get; set; }

// In Program.cs:
if (options.ClearCache)
{
    var articleCache = services.GetRequiredService<IArticleCache>();
    var audioCache = services.GetRequiredService<IAudioCache>();

    articleCache.Clear();
    audioCache.Clear();

    Log.Information("Cache cleared");
    return 0;
}
```

### ✅ Acceptance Criteria
- [ ] Articles cached in memory + database
- [ ] Audio cached on disk
- [ ] Cache hit logs clearly visible
- [ ] Cost savings from cache hits tracked
- [ ] Cache cleanup for old entries
- [ ] CLI options for cache management

### 🧪 Testing Strategy
```csharp
[Fact]
public async Task ArticleCache_GetAsync_ReturnsCachedArticle()
{
    var article = new Article { /* ... */ };
    await _cache.SetAsync("url", article);

    var cached = await _cache.GetAsync("url");

    cached.Should().BeEquivalentTo(article);
}

[Fact]
public async Task AudioCache_ComputeHash_ReturnsSameHashForSameContent()
{
    var hash1 = _cache.ComputeHash("test content");
    var hash2 = _cache.ComputeHash("test content");

    hash1.Should().Be(hash2);
}
```

### 📦 Deliverables
- `IArticleCache` + implementation
- `IAudioCache` + implementation
- Integration in ScraperService and AudioGenerator
- Cache CLI commands
- 8+ tests

**Estimated Effort**: 3 hours

---

## Priority 15: Cookie Encryption & Management

### 🎯 Objective
Encrypt stored cookies and improve session management for better security and reliability.

### 📊 Current State Analysis

**What Exists:**
- ✅ Cookie persistence to JSON file (`NYTAuthService.cs:197-227`)
- ✅ Cookie loading and restoration
- ❌ Cookies stored in **plain text**
- ❌ No encryption
- ❌ No expiration handling

**Security Risk:**
- Cookies contain session tokens
- Stored unencrypted in `%AppData%/NYTAudioScraper/cookies.json`
- If compromised, attacker gains NYT account access

### 🛠️ Implementation Plan

#### Phase 1: Add Encryption Service (1 hour)
```csharp
// Add package
<PackageReference Include="System.Security.Cryptography.ProtectedData" Version="9.0.0" />

// File: Infrastructure/Security/CookieEncryptionService.cs
public interface ICookieEncryptionService
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
}

public class CookieEncryptionService : ICookieEncryptionService
{
    private readonly byte[] _entropy;
    private readonly ILogger<CookieEncryptionService> _logger;

    public CookieEncryptionService(ILogger<CookieEncryptionService> logger)
    {
        _logger = logger;

        // Generate or load entropy (additional secret)
        var entropyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NYTAudioScraper",
            ".entropy");

        if (File.Exists(entropyPath))
        {
            _entropy = File.ReadAllBytes(entropyPath);
        }
        else
        {
            _entropy = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(_entropy);

            Directory.CreateDirectory(Path.GetDirectoryName(entropyPath)!);
            File.WriteAllBytes(entropyPath, _entropy);

            _logger.LogInformation("Created new encryption entropy");
        }
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var encryptedBytes = ProtectedData.Protect(
                plainBytes,
                _entropy,
                DataProtectionScope.CurrentUser);

            return Convert.ToBase64String(encryptedBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt data");
            throw;
        }
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return cipherText;

        try
        {
            var encryptedBytes = Convert.FromBase64String(cipherText);
            var decryptedBytes = ProtectedData.Unprotect(
                encryptedBytes,
                _entropy,
                DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt data");
            throw new CryptographicException("Failed to decrypt cookie data", ex);
        }
    }
}
```

#### Phase 2: Update Cookie Storage (45 min)
```csharp
// Update NYTAuthService to use encryption
private async Task SaveCookiesAsync(IWebDriver driver, CancellationToken cancellationToken)
{
    try
    {
        var cookies = driver.Manage().Cookies.AllCookies
            .Select(c => new CookieData
            {
                Name = c.Name,
                Value = c.Value, // Will be encrypted
                Domain = c.Domain,
                Path = c.Path,
                Expiry = c.Expiry
            })
            .ToList();

        var directory = Path.GetDirectoryName(_cookieFilePath);
        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Serialize to JSON
        var json = JsonSerializer.Serialize(cookies, new JsonSerializerOptions { WriteIndented = true });

        // Encrypt the entire JSON
        var encryptedJson = _encryptionService.Encrypt(json);

        // Save encrypted data
        await File.WriteAllTextAsync(_cookieFilePath, encryptedJson, cancellationToken);

        _logger.LogDebug("Saved and encrypted {Count} cookies to {CookieFilePath}",
            cookies.Count, _cookieFilePath);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to save cookies to {CookieFilePath}", _cookieFilePath);
    }
}

private async Task<bool> TryLoadCookiesAsync(IWebDriver driver, CancellationToken cancellationToken)
{
    try
    {
        if (!File.Exists(_cookieFilePath))
        {
            _logger.LogDebug("No saved cookies found at {CookieFilePath}", _cookieFilePath);
            return false;
        }

        // Read encrypted data
        var encryptedJson = await File.ReadAllTextAsync(_cookieFilePath, cancellationToken);

        // Decrypt
        var json = _encryptionService.Decrypt(encryptedJson);

        // Deserialize
        var cookies = JsonSerializer.Deserialize<List<CookieData>>(json);

        if (cookies == null || cookies.Count == 0)
        {
            _logger.LogDebug("Cookie file is empty or invalid");
            return false;
        }

        // Check for expired cookies
        var now = DateTime.UtcNow;
        var validCookies = cookies.Where(c =>
            !c.Expiry.HasValue || c.Expiry.Value > now).ToList();

        if (validCookies.Count < cookies.Count)
        {
            _logger.LogInformation(
                "Removed {Count} expired cookies",
                cookies.Count - validCookies.Count);
        }

        if (validCookies.Count == 0)
        {
            _logger.LogInformation("All cookies expired, re-authentication required");
            return false;
        }

        // ... rest of loading logic ...
    }
    catch (CryptographicException ex)
    {
        _logger.LogError(ex, "Failed to decrypt cookies - may need to re-authenticate");

        // Delete corrupted cookie file
        File.Delete(_cookieFilePath);
        return false;
    }
}
```

#### Phase 3: Add Cookie Management Features (30 min)
```csharp
// File: Infrastructure/Browser/CookieManager.cs
public class CookieManager
{
    private readonly string _cookieFilePath;
    private readonly ICookieEncryptionService _encryptionService;
    private readonly ILogger<CookieManager> _logger;

    public async Task<CookieInfo> GetCookieInfoAsync()
    {
        if (!File.Exists(_cookieFilePath))
        {
            return new CookieInfo
            {
                Exists = false,
                CookieCount = 0
            };
        }

        try
        {
            var encryptedJson = await File.ReadAllTextAsync(_cookieFilePath);
            var json = _encryptionService.Decrypt(encryptedJson);
            var cookies = JsonSerializer.Deserialize<List<CookieData>>(json);

            var now = DateTime.UtcNow;
            var validCount = cookies?.Count(c => !c.Expiry.HasValue || c.Expiry.Value > now) ?? 0;
            var nextExpiry = cookies?
                .Where(c => c.Expiry.HasValue && c.Expiry.Value > now)
                .Min(c => c.Expiry!.Value);

            return new CookieInfo
            {
                Exists = true,
                CookieCount = cookies?.Count ?? 0,
                ValidCookieCount = validCount,
                NextExpiry = nextExpiry,
                FilePath = _cookieFilePath,
                FileSize = new FileInfo(_cookieFilePath).Length
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read cookie info");
            return new CookieInfo { Exists = true, IsCorrupted = true };
        }
    }

    public async Task DeleteCookiesAsync()
    {
        if (File.Exists(_cookieFilePath))
        {
            File.Delete(_cookieFilePath);
            _logger.LogInformation("Deleted cookie file: {Path}", _cookieFilePath);
        }
    }

    public async Task RefreshCookiesAsync(IWebDriver driver)
    {
        // Force re-authentication and save new cookies
        await DeleteCookiesAsync();

        _logger.LogInformation("Cookies deleted. Re-authentication required.");
    }
}

public class CookieInfo
{
    public bool Exists { get; init; }
    public int CookieCount { get; init; }
    public int ValidCookieCount { get; init; }
    public DateTime? NextExpiry { get; init; }
    public string? FilePath { get; init; }
    public long FileSize { get; init; }
    public bool IsCorrupted { get; init; }

    public override string ToString()
    {
        if (!Exists)
            return "No cookies stored";
        if (IsCorrupted)
            return "Cookie file corrupted";

        var expiryInfo = NextExpiry.HasValue
            ? $"Next expiry: {NextExpiry.Value:g}"
            : "No expiration";

        return $"{ValidCookieCount}/{CookieCount} valid cookies. {expiryInfo}";
    }
}
```

#### Phase 4: Add CLI Commands (30 min)
```csharp
// Add to CommandOptions:
[Option("cookie-info", Required = false, HelpText = "Show cookie information")]
public bool ShowCookieInfo { get; set; }

[Option("clear-cookies", Required = false, HelpText = "Delete stored cookies")]
public bool ClearCookies { get; set; }

// In Program.cs:
if (options.ShowCookieInfo)
{
    var cookieManager = services.GetRequiredService<CookieManager>();
    var info = await cookieManager.GetCookieInfoAsync();
    Log.Information("Cookie Info: {Info}", info);
    return 0;
}

if (options.ClearCookies)
{
    var cookieManager = services.GetRequiredService<CookieManager>();
    await cookieManager.DeleteCookiesAsync();
    Log.Information("Cookies cleared");
    return 0;
}
```

### ✅ Acceptance Criteria
- [ ] Cookies encrypted at rest using DPAPI
- [ ] Entropy file generated and protected
- [ ] Expired cookies automatically removed
- [ ] Cookie info CLI command works
- [ ] Graceful handling of corrupted cookie files
- [ ] Re-authentication on decryption failure

### 🧪 Testing Strategy
```csharp
[Fact]
public void Encrypt_Decrypt_RoundTrip_ReturnsOriginal()
{
    var original = "test cookie value";
    var encrypted = _service.Encrypt(original);
    var decrypted = _service.Decrypt(encrypted);

    decrypted.Should().Be(original);
    encrypted.Should().NotBe(original);
}

[Fact]
public async Task LoadCookies_WithExpiredCookies_FiltersExpired()
{
    // Test that expired cookies are not loaded
}
```

### 📦 Deliverables
- `ICookieEncryptionService` + implementation
- `CookieManager` class
- Updated `NYTAuthService`
- CLI commands
- Entropy file management
- 8+ tests

**Estimated Effort**: 2.5 hours

---

## Implementation Roadmap

### Week 1: Foundation (Days 1-2)
**Day 1 (4 hours)**
- Morning: Priority 7 - Persistence Layer (4 hours)
  - Phase 1-4: DbContext, Repository, BudgetHistory
  - Basic integration with Program.cs

**Day 2 (4 hours)**
- Morning: Priority 7 - Complete & Test (2 hours)
  - Phase 5-7: Session management, migrations, tests
- Afternoon: Priority 13 - Intelligent Rate Limiting (2 hours)
  - Phase 1-2: AdaptiveRateLimiter, integration

### Week 2: Performance & Scalability (Days 3-4)
**Day 3 (4 hours)**
- Morning: Priority 13 - Complete (1 hour)
  - Phase 3: Metrics, tests
- Afternoon: Priority 8 - Parallel Processing (3 hours)
  - Phase 1-3: RateLimiter, ParallelAudioGenerator, integration

**Day 4 (4 hours)**
- Morning: Priority 8 - Complete & Test (1 hour)
- Afternoon: Priority 9 - Health Checks (3 hours)
  - Phase 1-2: Health check infrastructure, registration

### Week 3: Observability & Optimization (Days 5-6)
**Day 5 (4 hours)**
- Morning: Priority 9 - Complete (2 hours)
  - Phase 3-4: Performance metrics, logging enhancements
- Afternoon: Priority 14 - Caching (2 hours)
  - Phase 1-2: ArticleCache, AudioCache

**Day 6 (4 hours)**
- Morning: Priority 14 - Complete & Test (2 hours)
  - Phase 3-4: Integration, CLI commands
- Afternoon: Priority 15 - Cookie Encryption (2 hours)
  - Phase 1-2: Encryption service, cookie storage

### Week 4: Security & Polish (Day 7)
**Day 7 (2-3 hours)**
- Morning: Priority 15 - Complete (1.5 hours)
  - Phase 3-4: CookieManager, CLI commands
- Afternoon: Final Testing & Documentation (1-1.5 hours)
  - Integration tests
  - Update CLAUDE.md
  - Update README

---

## Dependencies & Prerequisites

### Required NuGet Packages
```xml
<!-- For Priority 7: Persistence -->
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.0" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="9.0.0" />

<!-- For Priority 9: Health Checks -->
<PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="9.0.0" />

<!-- For Priority 14: Caching -->
<PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="9.0.0" />

<!-- For Priority 15: Encryption -->
<PackageReference Include="System.Security.Cryptography.ProtectedData" Version="9.0.0" />
```

### System Dependencies
- SQLite (bundled with EF Core)
- FFmpeg (already required)
- ChromeDriver (already required)

---

## Risk Assessment

### High Risk
1. **Parallel Processing** - Could violate API rate limits
   - **Mitigation**: Conservative defaults (3 concurrent max), rate limiter

2. **Database Migrations** - Could corrupt existing data
   - **Mitigation**: Backup strategy, migration testing

### Medium Risk
3. **Cookie Encryption** - Users may lose sessions if entropy file lost
   - **Mitigation**: Clear error messages, graceful fallback to re-auth

4. **Memory Pressure** - Caching could increase memory usage
   - **Mitigation**: Sliding expiration, cache size limits

### Low Risk
5. **Health Checks** - Minimal risk, mostly informational
6. **Rate Limiting** - Defensive measure, reduces risk

---

## Success Metrics

### Performance
- [ ] Parallel processing achieves 3-5x speedup
- [ ] Cache hit rate >30% after first run
- [ ] Rate limiter prevents all 429 errors

### Reliability
- [ ] Health checks catch 100% of dependency issues before runtime
- [ ] Session resume works for interrupted runs
- [ ] Zero credential exposure from cookie storage

### Cost
- [ ] Cache reduces API spending by 20-40%
- [ ] Budget tracking accurate across sessions
- [ ] No rate limit violations (zero extra costs)

### Maintainability
- [ ] All priorities have >80% test coverage
- [ ] Documentation updated
- [ ] Code analysis passes with zero warnings

---

## Testing Strategy

### Unit Tests (80+ new tests)
- Repository pattern: 15 tests
- Rate limiters: 10 tests
- Parallel processing: 8 tests
- Health checks: 10 tests
- Caching: 15 tests
- Cookie encryption: 10 tests
- Metrics: 5 tests
- Integration: 10 tests

### Integration Tests
- End-to-end with SQLite
- Parallel audio generation with mocks
- Cache persistence across runs
- Health check integration

### Performance Tests
- Benchmark parallel vs sequential
- Cache hit rate measurement
- Rate limiter effectiveness

---

## Rollout Plan

### Phase 1: Core Infrastructure (Week 1)
- Deploy persistence layer
- Enable in production with monitoring

### Phase 2: Performance (Week 2)
- Deploy parallel processing (feature flag)
- Monitor API rate limits closely
- Deploy health checks

### Phase 3: Optimization (Week 3)
- Enable caching (optional feature flag)
- Deploy metrics collection

### Phase 4: Security (Week 4)
- Deploy cookie encryption
- Force cookie refresh for all users
- Monitor for auth failures

---

## Maintenance Plan

### Daily
- Monitor health check failures
- Review rate limit metrics
- Check cache hit rates

### Weekly
- Review performance metrics
- Analyze budget spending trends
- Clean old cache entries

### Monthly
- Database maintenance (VACUUM)
- Review and archive old sessions
- Update dependencies

---

## Appendix: Configuration

### appsettings.json Additions
```json
{
  "Database": {
    "ConnectionString": "Data Source=nyt_scraper.db",
    "AutoMigrate": true
  },
  "Audio": {
    "ParallelProcessing": {
      "Enabled": true,
      "MaxConcurrentRequests": 3,
      "RequestsPerMinute": 50
    }
  },
  "RateLimiting": {
    "Adaptive": true,
    "MinDelayMs": 1000,
    "MaxDelayMs": 10000
  },
  "Caching": {
    "ArticleCache": {
      "Enabled": true,
      "SlidingExpirationHours": 24
    },
    "AudioCache": {
      "Enabled": true,
      "MaxAgeDays": 30,
      "MaxSizeMB": 1000
    }
  },
  "HealthChecks": {
    "DiskSpace": {
      "MinimumFreeMB": 1000
    }
  }
}
```

---

## End of Implementation Plan

**Total Estimated Effort**: 20-28 hours (3-4 days)
**Recommended Team Size**: 1 developer
**Priority Order**: 7 → 8 → 13 → 9 → 14 → 15
**Completion Target**: 4 weeks (part-time work)
