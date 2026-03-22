# Architecture Documentation

## Overview

NYT Audio Scraper follows **Clean Architecture** principles with clear separation between layers and dependency inversion throughout. This document describes the architectural patterns, design decisions, and best practices implemented in Phases 1 and 2.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Layer Structure](#layer-structure)
3. [Design Patterns](#design-patterns)
4. [Interface Contracts](#interface-contracts)
5. [Dependency Injection](#dependency-injection)
6. [Error Handling Strategy](#error-handling-strategy)
7. [Logging and Observability](#logging-and-observability)
8. [Testing Strategy](#testing-strategy)
9. [Configuration Management](#configuration-management)

---

## Architecture Overview

### Clean Architecture Principles

```
┌─────────────────────────────────────────────┐
│            API Layer (Console App)           │
│  - Entry point                               │
│  - CLI argument handling                     │
│  - Host configuration                        │
└──────────────────┬──────────────────────────┘
                   │ depends on
┌──────────────────▼──────────────────────────┐
│         Application Layer (Interfaces)       │
│  - Interface definitions (contracts)         │
│  - DTOs                                      │
│  - No implementation details                 │
└──────────────────┬──────────────────────────┘
                   │ depends on
┌──────────────────▼──────────────────────────┐
│          Domain Layer (Entities)             │
│  - Business entities                         │
│  - Value objects                             │
│  - No dependencies on other layers           │
└──────────────────────────────────────────────┘

┌─────────────────────────────────────────────┐
│        Infrastructure Layer                  │
│  - Implementations of interfaces             │
│  - External service integrations             │
│  - Database, HTTP clients, File I/O          │
│  - Depends on Application & Domain           │
└─────────────────────────────────────────────┘
```

### Key Principles

1. **Dependency Inversion**: High-level modules don't depend on low-level modules. Both depend on abstractions.
2. **Separation of Concerns**: Each layer has a single, well-defined responsibility.
3. **Interface Segregation**: Interfaces are focused and minimal.
4. **Single Responsibility**: Each class has one reason to change.
5. **Open/Closed**: Open for extension, closed for modification (via decorator pattern).

---

## Layer Structure

### 1. Domain Layer (`TermReader.Domain`)

**Purpose**: Core business logic and entities

**Contains**:
- `Entities/`: Business entities (Article, ScrapingSession, AudioGenerationResult)
- `ValueObjects/`: Immutable value types
- `Enums/`: Domain enums (ScrapingStatus, etc.)

**Rules**:
- ❌ No dependencies on other projects
- ❌ No infrastructure concerns
- ✅ Pure C# classes
- ✅ Immutable where possible

**Example**:
```csharp
// Domain/Entities/Article.cs
public class Article
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public required string Url { get; init; }
    public required string Author { get; init; }
    public required DateTime ScrapedDate { get; init; }
}
```

### 2. Application Layer (`TermReader.Application`)

**Purpose**: Interface definitions and contracts

**Contains**:
- `Interfaces/`: All service interfaces (IBudgetService, IAudioGenerator, etc.)
- `DTOs/`: Data transfer objects
- `Exceptions/`: Application-specific exceptions

**Rules**:
- ✅ Only depends on Domain layer
- ✅ Defines contracts (interfaces)
- ❌ No implementation details
- ❌ No infrastructure concerns

**Example**:
```csharp
// Application/Interfaces/IBudgetService.cs
public interface IBudgetService
{
    decimal MaxBudget { get; set; }
    decimal TotalSpent { get; }
    decimal RemainingBudget { get; }
    bool CanAfford(decimal estimatedCost);
    void RecordExpense(decimal amount);
    void Reset();
    BudgetSummary GetSummary();
}
```

### 3. Infrastructure Layer (`TermReader.Infrastructure`)

**Purpose**: Implementation of interfaces and external integrations

**Contains**:
- `Audio/`: Audio generation implementations
- `Browser/`: Web scraping with Selenium
- `Caching/`: Two-tier caching implementation
- `Configuration/`: Configuration models and validation
- `Http/`: HTTP client wrappers and resilience
- `Parsing/`: HTML parsing logic
- `Persistence/`: Database (EF Core, repositories)
- `Storage/`: File system operations

**Rules**:
- ✅ Implements Application layer interfaces
- ✅ Depends on Application and Domain
- ✅ Contains all external dependencies
- ✅ Registered in DependencyInjection.cs

**Example**:
```csharp
// Infrastructure/Audio/BudgetService.cs
public class BudgetService : IBudgetService
{
    private readonly ILogger<BudgetService> _logger;
    private decimal _totalSpent;
    private decimal _maxBudget;

    public BudgetService(ILogger<BudgetService> logger)
    {
        _logger = logger;
    }

    public bool CanAfford(decimal estimatedCost)
    {
        return (TotalSpent + estimatedCost) <= MaxBudget;
    }

    // ... implementation
}
```

### 4. API Layer (`TermReader.API`)

**Purpose**: Entry point and host configuration

**Contains**:
- `Program.cs`: Application entry point
- `CommandOptions.cs`: CLI argument parsing

**Rules**:
- ✅ References Infrastructure (for DI registration)
- ✅ Minimal logic (composition root)
- ✅ Host and DI configuration
- ❌ No business logic

---

## Design Patterns

### 1. Repository Pattern

**Purpose**: Abstract data access logic

**Implementation**:
```csharp
// Generic repository for CRUD operations
public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<IEnumerable<T>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    Task AddAsync(T entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(T entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(T entity, CancellationToken cancellationToken = default);
}

// Specialized repositories extend base
public interface IArticleRepository : IRepository<Article>
{
    Task<Article?> GetByUrlAsync(string url, CancellationToken cancellationToken = default);
    Task<bool> ExistsByUrlAsync(string url, CancellationToken cancellationToken = default);
}
```

**Benefits**:
- Testable (easy to mock)
- Centralizes data access logic
- Provides consistent API

### 2. Unit of Work Pattern

**Purpose**: Manage transactions and coordinate changes across repositories

**Implementation**:
```csharp
public interface IUnitOfWork : IDisposable
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);
    Task CommitTransactionAsync(CancellationToken cancellationToken = default);
    Task RollbackTransactionAsync(CancellationToken cancellationToken = default);
}
```

**Usage**:
```csharp
public class ArticleCache
{
    private readonly IArticleRepository _articleRepository;
    private readonly IUnitOfWork _unitOfWork;

    public async Task SetAsync(string key, Article value)
    {
        await _articleRepository.AddAsync(value);
        await _unitOfWork.SaveChangesAsync(); // Persist changes
    }
}
```

**Benefits**:
- Separates persistence from entity operations
- Enables transactional workflows
- Single responsibility (Repository = entities, UnitOfWork = persistence)

### 3. Decorator Pattern

**Purpose**: Add behavior to objects without modifying them

**Example - Resilient Audio Generator**:
```csharp
public class ResilientAudioGenerator : IAudioGenerator
{
    private readonly AudioGenerator _innerGenerator;
    private readonly IBudgetService _budgetService;

    public ResilientAudioGenerator(
        AudioGenerator innerGenerator,
        IBudgetService budgetService)
    {
        _innerGenerator = innerGenerator;
        _budgetService = budgetService;
    }

    public async Task<byte[]> GenerateAudioAsync(string text, string voiceId)
    {
        // Add resilience behavior
        var cost = _innerGenerator.EstimateCost(text);
        if (!_budgetService.CanAfford(cost))
        {
            throw new InvalidOperationException("Insufficient budget");
        }

        // Delegate to inner generator
        var result = await _innerGenerator.GenerateAudioAsync(text, voiceId);

        _budgetService.RecordExpense(cost);
        return result;
    }
}
```

**Registration**:
```csharp
services.AddSingleton<AudioGenerator>();
services.AddSingleton<IAudioGenerator, ResilientAudioGenerator>();
```

**Benefits**:
- Add cross-cutting concerns (logging, caching, retry logic)
- Maintain Open/Closed principle
- Stack decorators for composition

### 4. Options Pattern

**Purpose**: Type-safe configuration with validation

**Implementation**:
```csharp
// Configuration model
public class ElevenLabsConfiguration
{
    public const string SectionName = "ElevenLabs";
    public required string ApiKey { get; init; }
    public string BaseUrl { get; init; } = "https://api.elevenlabs.io/v1";
}

// Validator
public class ElevenLabsConfigurationValidator : IValidateOptions<ElevenLabsConfiguration>
{
    public ValidateOptionsResult Validate(string? name, ElevenLabsConfiguration options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            errors.Add("ApiKey is required");

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
            errors.Add("BaseUrl must be a valid URL");

        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}

// Registration
services.Configure<ElevenLabsConfiguration>(
    configuration.GetSection(ElevenLabsConfiguration.SectionName));
services.AddSingleton<IValidateOptions<ElevenLabsConfiguration>,
    ElevenLabsConfigurationValidator>();
services.AddOptions<ElevenLabsConfiguration>().ValidateOnStart();
```

**Benefits**:
- Type-safe configuration access
- Validation at startup (fail-fast)
- Works with appsettings.json, environment variables, user secrets

---

## Interface Contracts

### Phase 1 Interfaces

#### IBudgetService
**Purpose**: Track ElevenLabs API spending
```csharp
public interface IBudgetService
{
    decimal MaxBudget { get; set; }
    decimal TotalSpent { get; }
    decimal RemainingBudget { get; }
    bool CanAfford(decimal estimatedCost);
    void RecordExpense(decimal amount);
    void Reset();
    BudgetSummary GetSummary();
}
```

#### IArticleParser
**Purpose**: Parse NYT HTML into Article entities
```csharp
public interface IArticleParser
{
    Article? ParseArticle(string html, string url);
}
```

#### INYTAuthService
**Purpose**: Authenticate with NYT website
```csharp
public interface INYTAuthService
{
    Task<bool> AuthenticateAsync(IWebDriver driver, CancellationToken cancellationToken = default);
}
```

#### IRateLimiter
**Purpose**: Control concurrent operations and enforce delays
```csharp
public interface IRateLimiter : IDisposable
{
    Task AcquireAsync(CancellationToken cancellationToken = default);
    void Release();
    Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default);
    int AvailableSlots { get; }
}
```

#### IParallelAudioGenerator
**Purpose**: Generate audio for multiple articles with rate limiting
```csharp
public interface IParallelAudioGenerator
{
    Task<AudioGenerationResult> GenerateAudioForArticlesAsync(
        IEnumerable<Article> articles,
        string voiceId,
        CancellationToken cancellationToken = default);
    decimal EstimateTotalCost(IEnumerable<Article> articles);
    int EstimateTotalCharacters(IEnumerable<Article> articles);
}
```

### Phase 2 Interfaces

#### IUnitOfWork
**Purpose**: Manage database transactions
```csharp
public interface IUnitOfWork : IDisposable
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);
    Task CommitTransactionAsync(CancellationToken cancellationToken = default);
    Task RollbackTransactionAsync(CancellationToken cancellationToken = default);
}
```

---

## Dependency Injection

### Registration Strategy

All services registered in `Infrastructure/DependencyInjection.cs`:

```csharp
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configuration with validation
        services.Configure<NYTConfiguration>(
            configuration.GetSection(NYTConfiguration.SectionName));
        services.AddSingleton<IValidateOptions<NYTConfiguration>,
            NYTConfigurationValidator>();
        services.AddOptions<NYTConfiguration>().ValidateOnStart();

        // Database
        services.AddDbContext<AppDbContext>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));

        // Services (use interfaces!)
        services.AddSingleton<IBudgetService, BudgetService>();
        services.AddSingleton<IArticleParser, ArticleParser>();
        services.AddSingleton<INYTAuthService, NYTAuthService>();
        services.AddSingleton<IRateLimiter, RateLimiter>();
        services.AddSingleton<IParallelAudioGenerator, ParallelAudioGenerator>();

        // Audio generation with decorator
        services.AddSingleton<AudioGenerator>(); // Concrete for decorator
        services.AddSingleton<IAudioGenerator, ResilientAudioGenerator>();

        return services;
    }
}
```

### Service Lifetimes

- **Singleton**: Stateless services, shared instances
  - Budget tracking, parsers, audio generators
- **Scoped**: Per-request/session services
  - Repositories, Unit of Work, database contexts
- **Transient**: New instance every time
  - Rarely used (overhead)

---

## Error Handling Strategy

### 1. Exception Specificity

**Bad**:
```csharp
try
{
    // operation
}
catch // Bare catch - anti-pattern!
{
    _logger.LogError("Error occurred");
    return false;
}
```

**Good**:
```csharp
try
{
    // operation
}
catch (WebDriverTimeoutException ex)
{
    _logger.LogError(ex, "Timeout waiting for element");
    return false;
}
catch (NoSuchElementException ex)
{
    _logger.LogError(ex, "Element not found");
    return false;
}
catch (WebDriverException ex)
{
    _logger.LogError(ex, "WebDriver error");
    return false;
}
```

### 2. Result Objects for Partial Failures

**AudioGenerationResult** tracks both successes and failures:
```csharp
public class AudioGenerationResult
{
    public Dictionary<string, byte[]> SuccessfulGenerations { get; init; }
    public Dictionary<string, string> FailedGenerations { get; init; }

    public int SuccessCount => SuccessfulGenerations.Count;
    public int FailureCount => FailedGenerations.Count;
    public bool AllSuccessful => FailedGenerations.Count == 0;
    public bool AnySuccessful => SuccessfulGenerations.Count > 0;
}
```

**Usage**:
```csharp
var result = await parallelGenerator.GenerateAudioForArticlesAsync(articles, voiceId);

if (!result.AllSuccessful)
{
    _logger.LogWarning("{FailureCount} of {Total} articles failed",
        result.FailureCount, result.TotalProcessed);

    foreach (var failure in result.FailedGenerations)
    {
        _logger.LogError("Article {Id} failed: {Error}",
            failure.Key, failure.Value);
    }
}
```

### 3. Configuration Validation

Validate at startup, not runtime:
```csharp
services.AddOptions<ElevenLabsConfiguration>().ValidateOnStart();
```

Application fails fast with clear error messages if configuration is invalid.

---

## Logging and Observability

### Structured Logging

Use Serilog with structured parameters:

**Bad**:
```csharp
_logger.LogInformation($"Saved {count} changes"); // String interpolation
```

**Good**:
```csharp
_logger.LogInformation("Saved {Count} changes to database", count);
```

### Log Levels

- **Debug**: Detailed information for debugging
- **Information**: General informational messages
- **Warning**: Unexpected but recoverable situations
- **Error**: Errors and exceptions
- **Fatal**: Critical failures requiring immediate attention

### HttpResilienceLogger

Dedicated logger for HTTP resilience events:
```csharp
public class HttpResilienceLogger
{
    public void LogRetry(DelegateResult<HttpResponseMessage> outcome,
        TimeSpan delay, int retryCount, string? endpoint);
    public void LogCircuitBreakerOpen(DelegateResult<HttpResponseMessage> outcome,
        TimeSpan breakDuration, string? endpoint);
    public void LogCircuitBreakerReset(string? endpoint);
    public void LogCircuitBreakerHalfOpen(string? endpoint);
}
```

**Benefits**:
- Centralized resilience logging
- Consistent log format
- Easy to add telemetry/metrics

---

## Testing Strategy

### Unit Tests

Test interfaces with mocks:
```csharp
[Fact]
public async Task GenerateAudioForArticlesAsync_WithSuccessfulGeneration_ReturnsSuccessResult()
{
    // Arrange
    var mockAudioGenerator = Substitute.For<IAudioGenerator>();
    var mockRateLimiter = Substitute.For<IRateLimiter>();

    mockAudioGenerator.GenerateAudioAsync(...).Returns(audioData);
    mockRateLimiter.ExecuteAsync(...).Returns(callInfo =>
        callInfo.Arg<Func<Task<byte[]>>>()());

    var generator = new ParallelAudioGenerator(
        mockAudioGenerator, mockRateLimiter, logger);

    // Act
    var result = await generator.GenerateAudioForArticlesAsync(articles, voiceId);

    // Assert
    result.AllSuccessful.Should().BeTrue();
    await mockAudioGenerator.Received(1).GenerateAudioAsync(...);
}
```

### Integration Tests

Test repository + database:
```csharp
[Fact]
public async Task SaveChangesAsync_WithPendingChanges_PersistsToDatabase()
{
    // Arrange
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}")
        .Options;
    var context = new AppDbContext(options);
    var unitOfWork = new UnitOfWork(context, logger);

    context.Articles.Add(article);

    // Act
    var result = await unitOfWork.SaveChangesAsync();

    // Assert
    result.Should().Be(1);
    context.Articles.Should().ContainSingle();
}
```

---

## Configuration Management

### Hierarchy

1. **appsettings.json**: Default configuration
2. **appsettings.{Environment}.json**: Environment-specific overrides
3. **User Secrets**: Local development credentials (not committed)
4. **Environment Variables**: Production credentials
5. **Command-line Arguments**: Runtime overrides

### Example

```json
// appsettings.json
{
  "ElevenLabs": {
    "BaseUrl": "https://api.elevenlabs.io/v1",
    "DefaultVoiceId": "21m00Tcm4TlvDq8ikWAM",
    "Model": "eleven_multilingual_v2",
    "CostPerCharacter": 0.0003
  },
  "NYT": {
    "BaseUrl": "https://www.nytimes.com",
    "MaxArticles": 10,
    "RateLimitDelayMs": 3000
  }
}
```

```bash
# User secrets (development)
dotnet user-secrets set "ElevenLabs:ApiKey" "your-api-key"
dotnet user-secrets set "Auth:Email" "your-email@example.com"
dotnet user-secrets set "Auth:Password" "your-password"
```

```bash
# Environment variables (production)
export ELEVEN_LABS_API_KEY="your-api-key"
export AUTH_EMAIL="your-email@example.com"
export AUTH_PASSWORD="your-password"
```

---

## Best Practices Summary

### ✅ DO

- Use interfaces for all services
- Inject dependencies via constructor
- Validate configuration at startup
- Use structured logging
- Test through interfaces (mocking)
- Follow naming conventions (I prefix for interfaces)
- Use meaningful exception types
- Return result objects for partial failures
- Separate concerns (Repository vs Unit of Work)

### ❌ DON'T

- Use concrete types in constructors
- Put business logic in Infrastructure
- Swallow exceptions silently
- Use bare catch blocks
- Use Console.WriteLine for logging
- Mix persistence (SaveChanges) with entity operations
- Create circular dependencies
- Commit credentials to source control

---

## Future Improvements

### Phase 3 Candidates

1. **Application Services Layer**
   - Move orchestration logic from Infrastructure to Application
   - Create use case handlers (commands/queries)

2. **Event Sourcing**
   - Track state changes as events
   - Enable audit trail and replay

3. **Mediator Pattern**
   - Decouple request/response handling
   - Use MediatR library

4. **CQRS** (Command Query Responsibility Segregation)
   - Separate read and write models
   - Optimize for different access patterns

---

## References

- [Clean Architecture by Robert C. Martin](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html)
- [Microsoft .NET Architecture Guides](https://docs.microsoft.com/en-us/dotnet/architecture/)
- [Repository and Unit of Work Patterns](https://docs.microsoft.com/en-us/aspnet/mvc/overview/older-versions/getting-started-with-ef-5-using-mvc-4/implementing-the-repository-and-unit-of-work-patterns-in-an-asp-net-mvc-application)
- [Options Pattern in .NET](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options)
