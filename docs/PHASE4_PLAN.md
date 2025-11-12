# Phase 4: Security & Enhanced Reliability

## Overview

Phase 3 successfully integrated session management, parallel audio generation, caching, health checks, and performance metrics. Phase 4 focuses on the remaining priorities from the implementation plan, with primary emphasis on security (cookie encryption) and enhanced reliability features.

## Status Summary

### Completed in Phase 3
- ✅ Priority 7: Persistence Layer with SQLite
- ✅ Priority 8: Parallel Processing for Audio Generation (5x speedup)
- ✅ Priority 9: Monitoring, Health Checks & Observability
- ✅ Priority 13: Basic Intelligent API Rate Limiting
- ✅ Priority 14: Caching Layer for Articles & Audio (30-50% cost savings)

### Phase 4 Priorities
- 🎯 **Priority 15: Cookie Encryption & Management** (Primary Focus)
- 🎯 Enhanced Rate Limiting with Retry-After Support
- 🎯 Session Resume Capability
- 🎯 Enhanced Monitoring & Observability

## Priority 15: Cookie Encryption & Management

### Current Security Risk

**Critical Issue**: Cookies are currently stored in plain text at `%AppData%/NYTAudioScraper/cookies.json`

From IMPLEMENTATION_PLAN.md analysis:
- ✅ Cookie persistence to JSON file (NYTAuthService.cs:197-227)
- ✅ Cookie loading and restoration
- ❌ Cookies stored in **plain text**
- ❌ No encryption
- ❌ No expiration handling

**Impact**: If compromised, attacker gains full NYT account access via session tokens.

### Implementation Plan

#### 1. Create Cookie Encryption Service (1 hour)

**File**: `src/NYTAudioScraper.Infrastructure/Security/CookieEncryptionService.cs`

```csharp
public interface ICookieEncryptionService
{
    byte[] Encrypt(string plainText);
    string Decrypt(byte[] cipherText);
}

public class DpapiCookieEncryptionService : ICookieEncryptionService
{
    // Use Data Protection API (DPAPI) for Windows
    // System.Security.Cryptography.ProtectedData

    public byte[] Encrypt(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);
        return ProtectedData.Protect(bytes,
            entropy: null,
            scope: DataProtectionScope.CurrentUser);
    }

    public string Decrypt(byte[] cipherText)
    {
        var bytes = ProtectedData.Unprotect(cipherText,
            entropy: null,
            scope: DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
```

**Cross-platform consideration**: For Linux/macOS, implement alternative using ASP.NET Core Data Protection API.

#### 2. Update Cookie Storage Model (30 minutes)

**File**: `src/NYTAudioScraper.Infrastructure/Browser/Models/CookieStorage.cs`

```csharp
public class CookieStorage
{
    public int Version { get; set; } = 2; // Increment for encrypted format
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public byte[] EncryptedData { get; set; } // Changed from plain cookies list
}

public class CookieData
{
    public List<CookieDto> Cookies { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
}
```

#### 3. Update NYTAuthService (1 hour)

**File**: `src/NYTAudioScraper.Infrastructure/Browser/NYTAuthService.cs`

Changes needed:
- Inject `ICookieEncryptionService`
- Update `SaveCookiesAsync()` to encrypt before saving
- Update `LoadCookiesAsync()` to decrypt after loading
- Add cookie expiration validation
- Handle version migration (v1 plain → v2 encrypted)

```csharp
private async Task SaveCookiesAsync(IEnumerable<Cookie> cookies)
{
    var cookieData = new CookieData
    {
        Cookies = cookies.Select(ToCookieDto).ToList(),
        Metadata = new Dictionary<string, string>
        {
            ["user_agent"] = _config.UserAgent,
            ["last_used"] = DateTime.UtcNow.ToString("O")
        }
    };

    var json = JsonSerializer.Serialize(cookieData);
    var encrypted = _encryptionService.Encrypt(json);

    var storage = new CookieStorage
    {
        Version = 2,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(30),
        EncryptedData = encrypted
    };

    await File.WriteAllTextAsync(_cookiePath,
        JsonSerializer.Serialize(storage));
}

private async Task<IEnumerable<Cookie>?> LoadCookiesAsync()
{
    if (!File.Exists(_cookiePath)) return null;

    var json = await File.ReadAllTextAsync(_cookiePath);
    var storage = JsonSerializer.Deserialize<CookieStorage>(json);

    // Check version
    if (storage.Version == 1)
    {
        // Migrate from plain text
        _logger.LogInformation("Migrating cookies from v1 to v2 (encrypted)");
        // ... migration logic
    }

    // Check expiration
    if (storage.ExpiresAt.HasValue && storage.ExpiresAt < DateTime.UtcNow)
    {
        _logger.LogInformation("Cookies expired, will re-authenticate");
        return null;
    }

    var decrypted = _encryptionService.Decrypt(storage.EncryptedData);
    var cookieData = JsonSerializer.Deserialize<CookieData>(decrypted);

    return cookieData.Cookies.Select(FromCookieDto);
}
```

#### 4. Add Cookie Management Commands (30 minutes)

**File**: `src/NYTAudioScraper.Infrastructure/Browser/CookieManager.cs`

```csharp
public interface ICookieManager
{
    Task<CookieInfo> GetCookieInfoAsync();
    Task ClearCookiesAsync();
}

public class CookieInfo
{
    public bool Exists { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsExpired { get; set; }
    public bool IsEncrypted { get; set; }
    public int Version { get; set; }
}
```

Add CLI commands:
```bash
dotnet run --project src/NYTAudioScraper.API -- --cookie-info
dotnet run --project src/NYTAudioScraper.API -- --clear-cookies
```

#### 5. Testing (30 minutes)

**File**: `tests/NYTAudioScraper.Tests/Security/CookieEncryptionServiceTests.cs`

Test cases:
- ✅ Encrypt/decrypt round-trip preserves data
- ✅ Encrypted data is not readable as plain text
- ✅ Decryption fails with wrong user context (DPAPI validation)
- ✅ Cookie expiration validation
- ✅ Version migration from v1 to v2

### Estimated Effort
**Total: 3.5 hours**

### Security Benefits
- 🔒 Cookies encrypted at rest using DPAPI (Windows) or ASP.NET Core Data Protection (Linux/macOS)
- ⏰ Automatic cookie expiration (30-day default)
- 🔄 Graceful migration from plain text to encrypted format
- 🧹 Cookie management commands for troubleshooting

---

## Enhanced Rate Limiting with Retry-After Support

### Current State
Basic rate limiting implemented in Phase 3:
- Token bucket algorithm
- Max 3 concurrent requests
- 1000ms minimum delay between requests
- Located in `src/NYTAudioScraper.Infrastructure/Audio/RateLimiter.cs`

### Enhancement Plan (1 hour)

**File**: `src/NYTAudioScraper.Infrastructure/Audio/EnhancedRateLimiter.cs`

Add support for:
- Respect `Retry-After` header from ElevenLabs API
- Dynamic rate adjustment based on 429 responses
- Exponential backoff for consecutive rate limit hits
- Per-endpoint rate limiting (different limits for different APIs)

```csharp
public class EnhancedRateLimiter : IRateLimiter
{
    private readonly Dictionary<string, DateTime> _retryAfterMap = new();
    private readonly Dictionary<string, int> _consecutiveRateLimits = new();

    public async Task<T> ExecuteAsync<T>(
        string endpoint,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        // Check if we need to wait due to previous Retry-After
        if (_retryAfterMap.TryGetValue(endpoint, out var retryAfter))
        {
            var delay = retryAfter - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                _logger.LogInformation(
                    "Waiting {Delay}s due to Retry-After header",
                    delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        try
        {
            return await base.ExecuteAsync(action, cancellationToken);
        }
        catch (HttpRequestException ex) when (IsRateLimitError(ex))
        {
            HandleRateLimitResponse(endpoint, ex);
            throw;
        }
    }

    private void HandleRateLimitResponse(string endpoint, HttpRequestException ex)
    {
        // Parse Retry-After header
        // Track consecutive rate limits
        // Adjust rate limiter parameters
    }
}
```

### Benefits
- ⚡ Respect API rate limit signals
- 📉 Reduce unnecessary retry attempts
- 🎯 Better API quota utilization

---

## Session Resume Capability

### Use Case
Allow resuming interrupted scraping sessions without re-processing completed articles.

### Current State
- Session tracking in database (Phase 3)
- Articles associated with sessions
- Audio generation status tracked
- No resume capability on interruption

### Implementation Plan (2 hours)

#### 1. Add Resume Detection (30 minutes)

**File**: `src/NYTAudioScraper.Application/Interfaces/IScraperService.cs`

```csharp
public interface IScraperService
{
    Task<List<Article>> ScrapeArticlesAsync(DateTime date);
    Task<string?> FindIncompleteSessionAsync(); // New
    Task<Session> LoadSessionAsync(string sessionId); // New
}
```

#### 2. Update Program.cs Workflow (1 hour)

```csharp
// Check for incomplete sessions
var incompleteSessionId = await sessionRepo.FindIncompleteAsync();

if (incompleteSessionId != null)
{
    Log.Information("Found incomplete session: {SessionId}", incompleteSessionId);
    Log.Information("Resume? (y/n)");

    var response = Console.ReadLine();
    if (response?.ToLower() == "y")
    {
        var session = await sessionRepo.GetByIdAsync(incompleteSessionId);
        var completedArticles = session.Articles
            .Where(a => a.AudioFilePath != null)
            .ToList();

        Log.Information("Resuming: {Completed}/{Total} articles completed",
            completedArticles.Count, session.Articles.Count);

        // Continue with remaining articles
        var remainingArticles = session.Articles
            .Where(a => a.AudioFilePath == null)
            .ToList();

        // ... generate audio for remaining articles
    }
}
```

#### 3. Add CLI Option (30 minutes)

```csharp
[Option("resume", Required = false,
    HelpText = "Resume an incomplete session by ID")]
public string? ResumeSessionId { get; set; }

[Option("list-sessions", Required = false,
    HelpText = "List recent scraping sessions")]
public bool ListSessions { get; set; }
```

### Benefits
- 💾 Save progress on interruption
- ⚡ Skip already-processed articles
- 💰 Avoid duplicate API costs

---

## Enhanced Monitoring & Observability

### Current State (Phase 3)
- ✅ Performance metrics (PerformanceMetrics.cs)
- ✅ Health checks (FFmpeg, disk space, database)
- ✅ Structured logging with Serilog
- ✅ Operation timing with P95 percentiles

### Enhancements (1.5 hours)

#### 1. Add Metrics Export (1 hour)

**File**: `src/NYTAudioScraper.Infrastructure/Metrics/MetricsExporter.cs`

```csharp
public interface IMetricsExporter
{
    Task ExportAsync(MetricsSummary summary, string outputPath);
}

public class JsonMetricsExporter : IMetricsExporter
{
    public async Task ExportAsync(MetricsSummary summary, string outputPath)
    {
        var export = new
        {
            timestamp = DateTime.UtcNow,
            operations = summary.Operations,
            counters = summary.Counters,
            system = new
            {
                cpu_usage = GetCpuUsage(),
                memory_mb = GetMemoryUsage(),
                disk_free_gb = GetDiskSpace()
            }
        };

        await File.WriteAllTextAsync(outputPath,
            JsonSerializer.Serialize(export,
                new JsonSerializerOptions { WriteIndented = true }));
    }
}
```

Output location: `output/metrics_{timestamp}.json`

#### 2. Add Health Check Dashboard (30 minutes)

**File**: `src/NYTAudioScraper.Infrastructure/Health/HealthCheckReporter.cs`

```csharp
public static class HealthCheckReporter
{
    public static void PrintDashboard(HealthReport report)
    {
        Console.WriteLine("\n╔═══════════════════════════════════════╗");
        Console.WriteLine("║         System Health Check           ║");
        Console.WriteLine("╚═══════════════════════════════════════╝\n");

        foreach (var (name, entry) in report.Entries.OrderBy(e => e.Key))
        {
            var icon = entry.Status switch
            {
                HealthStatus.Healthy => "✓",
                HealthStatus.Degraded => "⚠",
                HealthStatus.Unhealthy => "✗",
                _ => "?"
            };

            Console.WriteLine($"{icon} {name}: {entry.Description}");

            if (entry.Data.Any())
            {
                foreach (var (key, value) in entry.Data)
                {
                    Console.WriteLine($"    {key}: {value}");
                }
            }
        }

        Console.WriteLine();
    }
}
```

### Benefits
- 📊 Export metrics for analysis
- 📈 System resource tracking
- 🎨 Improved health check visualization

---

## Implementation Order

### Week 1: Security (Priority 15)
**Day 1-2**: Cookie Encryption Implementation
1. ✅ Create `ICookieEncryptionService` with DPAPI implementation
2. ✅ Update cookie storage model with encryption
3. ✅ Update `NYTAuthService` to use encryption
4. ✅ Add version migration (v1 → v2)
5. ✅ Write unit tests

**Day 3**: Cookie Management
1. ✅ Create `ICookieManager` interface and implementation
2. ✅ Add CLI commands (`--cookie-info`, `--clear-cookies`)
3. ✅ Update documentation

### Week 2: Reliability & Observability
**Day 4**: Enhanced Rate Limiting
1. ✅ Implement `EnhancedRateLimiter` with Retry-After support
2. ✅ Add dynamic rate adjustment
3. ✅ Integration testing with mock API responses

**Day 5**: Session Resume
1. ✅ Add session resume detection
2. ✅ Update Program.cs workflow
3. ✅ Add CLI options

**Day 6**: Enhanced Monitoring
1. ✅ Create metrics exporter
2. ✅ Add health check dashboard
3. ✅ Update documentation

### Week 3: Testing & Polish
**Day 7**: Integration testing
- End-to-end testing with cookie encryption
- Resume capability testing
- Rate limiting behavior validation

**Day 8**: Documentation
- Update README with new features
- Add security documentation
- Update IMPLEMENTATION_PLAN.md with Phase 4 completion

---

## Success Criteria

### Security
- ✅ Cookies encrypted at rest using DPAPI
- ✅ Automatic expiration handling (30-day default)
- ✅ Migration from plain text to encrypted format works seamlessly
- ✅ Cookie management commands available

### Reliability
- ✅ Enhanced rate limiting respects Retry-After headers
- ✅ Session resume capability works for interrupted runs
- ✅ No data loss on interruption

### Observability
- ✅ Metrics exported to JSON for analysis
- ✅ Health check dashboard shows system status clearly
- ✅ System resource tracking included

### Testing
- ✅ >80% code coverage maintained
- ✅ All tests pass
- ✅ Integration tests cover new features

---

## Risk Assessment

### Low Risk
- Cookie encryption (well-established DPAPI pattern)
- Metrics export (straightforward JSON serialization)
- Health check dashboard (UI improvement)

### Medium Risk
- Enhanced rate limiting (requires careful testing with API)
- Session resume (complex state management)
- Cross-platform encryption (DPAPI Windows-only)

### Mitigation Strategies
- **Rate limiting**: Mock API responses in tests, gradual rollout
- **Session resume**: Comprehensive state validation, rollback on error
- **Cross-platform**: Use ASP.NET Core Data Protection API as fallback

---

## Estimated Total Effort

| Task | Effort |
|------|--------|
| Cookie Encryption (Priority 15) | 3.5 hours |
| Enhanced Rate Limiting | 1.0 hour |
| Session Resume Capability | 2.0 hours |
| Enhanced Monitoring | 1.5 hours |
| Integration Testing | 2.0 hours |
| Documentation | 1.0 hour |
| **TOTAL** | **11 hours** (~1.5 weeks part-time)

---

## Next Steps

1. **Review this plan** and confirm priorities
2. **Start with Priority 15** (Cookie Encryption) as it addresses a security risk
3. **Implement in order** to maintain working state at each step
4. **Test thoroughly** before moving to next feature
5. **Commit frequently** with clear messages
6. **Create PR** when Phase 4 is complete

---

## Questions for Review

1. Should cookie expiration be configurable (currently 30 days)?
2. Should we add telemetry opt-in for anonymous usage statistics?
3. Should session resume be automatic or require user confirmation?
4. Should metrics export be automatic or opt-in via CLI flag?

---

**Plan Created**: 2025-11-06
**Target Branch**: `claude/phase4-cookie-encryption-011CUoUW4FY4VsxQq1m1ZBds`
**Base Branch**: `main` (Phase 3 merged)
