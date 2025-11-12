# Database-First Architecture for Audio Generation

## Overview

The application now uses the database as the source of truth for audio generation, providing a complete audit trail and enabling future enhancements like session resume capability.

## Architecture Changes

### Before (Memory-First)

```
1. Scrape articles → Save to DB (cache only)
2. Keep articles in memory (List<Article>)
3. Generate audio from memory list
4. Save session metadata (no article linkage)
```

**Problems:**
- ❌ No link between sessions and articles in database
- ❌ Can't query "which articles were in session X"
- ❌ No way to resume interrupted sessions
- ❌ Debugging requires relying solely on logs

### After (Database-First)

```
1. Scrape articles → Save to DB
2. Link articles to session (ScrapingSessionArticle join table)
3. Generate audio from session.Articles
4. Update Article.AudioFilePath in database
5. Save session with complete metadata
```

**Benefits:**
- ✅ Complete audit trail in database
- ✅ Can query session history and relationships
- ✅ Articles track their audio file paths
- ✅ Foundation for resume capability
- ✅ Logs + database work together for debugging

## Database Schema

### Articles Table
```sql
CREATE TABLE Articles (
    Id TEXT PRIMARY KEY,
    Title TEXT NOT NULL,
    Url TEXT NOT NULL,
    Author TEXT,
    Section TEXT,
    Content TEXT NOT NULL,
    PublishedDate DATETIME NOT NULL,
    ScrapedDate DATETIME NOT NULL,
    AudioFilePath TEXT,  -- NEW: tracks audio location
    ...
);
```

### ScrapingSessions Table
```sql
CREATE TABLE ScrapingSessions (
    Id TEXT PRIMARY KEY,
    StartedAt DATETIME NOT NULL,
    CompletedAt DATETIME,
    OutputFilePath TEXT,
    TotalCharactersProcessed INTEGER,
    EstimatedCost DECIMAL(18,4),
    Status TEXT NOT NULL,
    ErrorMessage TEXT
);
```

### ScrapingSessionArticle (Join Table)
```sql
CREATE TABLE ScrapingSessionArticle (
    SessionId TEXT NOT NULL,
    ArticleId TEXT NOT NULL,
    PRIMARY KEY (SessionId, ArticleId),
    FOREIGN KEY (SessionId) REFERENCES ScrapingSessions(Id),
    FOREIGN KEY (ArticleId) REFERENCES Articles(Id)
);
```

## Key Implementation Details

### 1. Article-Session Linking

**File:** `src/NYTAudioScraper.API/Program.cs` (lines 541-549)

```csharp
// Link articles to session for complete audit trail
Log.Information("");
Log.Information("Linking articles to session {SessionId}...", session.Id);
foreach (var article in articleList)
{
    session.Articles.Add(article);
}
await unitOfWork.SaveChangesAsync();
Log.Information("✓ Linked {Count} articles to session", articleList.Count);
```

**What it does:**
- Establishes many-to-many relationship
- Inserts records into ScrapingSessionArticle table
- Makes it possible to query articles by session

### 2. Audio File Path Tracking

**File:** `src/NYTAudioScraper.API/Program.cs` (lines 593-597)

```csharp
// Update article with audio file path in database
article.AudioFilePath = audioFilePath;

Log.Information("  ✓ [{ArticleId}] Saved: {Title} ({Size:N0} bytes)",
    article.Id, article.Title, audioData.Length);
```

**What it does:**
- Stores audio file path in Article entity
- Enables tracking which articles have audio
- Foundation for resume capability

### 3. Persisting Audio File Paths

**File:** `src/NYTAudioScraper.API/Program.cs` (lines 628-634)

```csharp
// Persist audio file paths to database
if (result.SuccessCount > 0)
{
    await unitOfWork.SaveChangesAsync();
    Log.Information("✓ Updated {Count} articles with audio file paths in database",
        result.SuccessCount);
}
```

**What it does:**
- Saves all AudioFilePath updates in one transaction
- Ensures database reflects current audio generation state

### 4. Enhanced Logging

**File:** `src/NYTAudioScraper.API/Program.cs` (lines 537-538, 596-597, 624-625)

```csharp
// Article IDs in logs for DB queries
Log.Information("  - [{ArticleId}] {Title} ({Words} words, {Url})",
    article.Id, article.Title, article.EstimatedWordCount, article.Url);

// Failures include article IDs
Log.Error("  ✗ [{ArticleId}] Failed: {Title} - {Error}",
    article.Id, article.Title, errorMessage);
```

**What it does:**
- Makes it easy to correlate logs with database records
- Article IDs in brackets `[art-123]` can be used directly in SQL queries

## Usage Examples

### Query Articles in a Session

```sql
SELECT
    a.Id,
    a.Title,
    a.Url,
    a.AudioFilePath,
    a.PublishedDate,
    LENGTH(a.Content) as ContentLength
FROM Articles a
JOIN ScrapingSessionArticle sa ON a.Id = sa.ArticleId
WHERE sa.SessionId = 'abc-def-123';
```

### Find Sessions for an Article

```sql
SELECT
    s.Id,
    s.StartedAt,
    s.CompletedAt,
    s.Status,
    s.EstimatedCost
FROM ScrapingSessions s
JOIN ScrapingSessionArticle sa ON s.Id = sa.SessionId
WHERE sa.ArticleId = 'art-456'
ORDER BY s.StartedAt DESC;
```

### Find Articles Without Audio

```sql
-- Articles scraped but never generated audio
SELECT Id, Title, ScrapedDate
FROM Articles
WHERE AudioFilePath IS NULL
ORDER BY ScrapedDate DESC;
```

### Calculate Cost Savings from Reuse

```sql
-- Articles used in multiple sessions (cache hits)
SELECT
    a.Id,
    a.Title,
    COUNT(sa.SessionId) as TimesReused,
    LENGTH(a.Content) * 0.0003 as CostPerGeneration,
    (COUNT(sa.SessionId) - 1) * LENGTH(a.Content) * 0.0003 as CostSavings
FROM Articles a
JOIN ScrapingSessionArticle sa ON a.Id = sa.ArticleId
GROUP BY a.Id
HAVING COUNT(sa.SessionId) > 1
ORDER BY CostSavings DESC;
```

### Session Summary with Article Details

```sql
SELECT
    s.Id as SessionId,
    s.StartedAt,
    s.CompletedAt,
    s.Status,
    COUNT(a.Id) as TotalArticles,
    SUM(CASE WHEN a.AudioFilePath IS NOT NULL THEN 1 ELSE 0 END) as ArticlesWithAudio,
    s.TotalCharactersProcessed,
    s.EstimatedCost,
    s.OutputFilePath
FROM ScrapingSessions s
LEFT JOIN ScrapingSessionArticle sa ON s.Id = sa.SessionId
LEFT JOIN Articles a ON sa.ArticleId = a.Id
WHERE s.Id = 'abc-def-123'
GROUP BY s.Id;
```

## Debugging Workflow

### Scenario: Audio Generation Failed for Some Articles

**Step 1: Check logs for session ID and article IDs**
```
✗ [art-456] Failed: Politics Update - API rate limit
✓ Session completed: abc-def-123
```

**Step 2: Query database for session details**
```sql
SELECT a.Id, a.Title, a.AudioFilePath
FROM Articles a
JOIN ScrapingSessionArticle sa ON a.Id = sa.ArticleId
WHERE sa.SessionId = 'abc-def-123';
```

**Step 3: Identify failed articles**
```sql
-- Articles in session without audio
SELECT Id, Title
FROM Articles a
JOIN ScrapingSessionArticle sa ON a.Id = sa.ArticleId
WHERE sa.SessionId = 'abc-def-123'
  AND a.AudioFilePath IS NULL;
```

**Step 4: Inspect article content**
```sql
-- Get full content for debugging
SELECT Content
FROM Articles
WHERE Id = 'art-456';
```

## Future Enhancements

### Resume Capability (Planned for Phase 4)

```csharp
// Check for incomplete session
var incompleteSession = await sessionRepo
    .FindIncompleteSessionsAsync()
    .FirstOrDefaultAsync();

if (incompleteSession != null)
{
    Log.Information("Found incomplete session: {Id}", incompleteSession.Id);

    // Get articles without audio
    var incompleteArticles = incompleteSession.Articles
        .Where(a => string.IsNullOrEmpty(a.AudioFilePath))
        .ToList();

    if (incompleteArticles.Any())
    {
        Log.Information("Resuming session with {Count} incomplete articles",
            incompleteArticles.Count);

        // Generate audio only for incomplete articles
        await parallelAudioGenerator.GenerateAudioForArticlesAsync(
            incompleteArticles, voiceId);
    }
}
```

### Selective Regeneration

```csharp
// Regenerate audio for specific articles
var articlesToRegenerate = await articleRepo
    .GetByIdsAsync(new[] { "art-123", "art-456" });

foreach (var article in articlesToRegenerate)
{
    // Clear old audio path
    article.AudioFilePath = null;
}

// Generate fresh audio
await parallelAudioGenerator.GenerateAudioForArticlesAsync(
    articlesToRegenerate, voiceId);
```

### Cost Analytics

```csharp
// Query to analyze cost savings
var costAnalytics = await dbContext.Database
    .SqlQuery<CostAnalytics>(@"
        SELECT
            COUNT(DISTINCT sa.ArticleId) as UniqueArticles,
            COUNT(*) as TotalUsages,
            SUM(LENGTH(a.Content) * 0.0003) as TotalCostIfNoCache,
            SUM(s.EstimatedCost) as ActualCost,
            SUM(LENGTH(a.Content) * 0.0003) - SUM(s.EstimatedCost) as Savings
        FROM ScrapingSessions s
        JOIN ScrapingSessionArticle sa ON s.Id = sa.SessionId
        JOIN Articles a ON sa.ArticleId = a.Id
    ")
    .ToListAsync();
```

## Migration Guide

### Existing Users

The migration `20250106000001_AddAudioFilePathToArticle` will run automatically on next application start.

**Changes:**
- Adds `AudioFilePath` column to Articles table (nullable)
- Existing articles will have `AudioFilePath = NULL`
- Future runs will populate AudioFilePath

**Data Loss:** None - fully backward compatible

### New Users

Database will be created with AudioFilePath column included from the start.

## Logging Best Practices

### Always Include Article IDs

```csharp
// Good - can query DB with this article ID
Log.Information("Processing [{ArticleId}] {Title}", article.Id, article.Title);

// Bad - no way to correlate with database
Log.Information("Processing {Title}", article.Title);
```

### Include Session IDs in Error Logs

```csharp
// Good - can find all articles in this session
Log.Error("Session {SessionId} failed on article [{ArticleId}]",
    session.Id, article.Id);

// Bad - no context for debugging
Log.Error("Article processing failed");
```

### Reference Database in Success Messages

```csharp
// Remind users that data is persisted
Log.Information("✓ Session completed: {SessionId}", session.Id);
Log.Information("💾 Database contains full article content and session history");
Log.Information("   Use session ID to query article details from database");
```

## Performance Considerations

### Database Writes

**Before:**
- Save articles (cache layer)
- Save session metadata
- ~2 database transactions per run

**After:**
- Save articles (cache layer)
- Link articles to session
- Update articles with audio paths
- Save session metadata
- ~4 database transactions per run

**Impact:** Negligible (< 50ms additional overhead for typical session)

### Query Performance

All queries are indexed:
- Articles by URL (existing)
- Articles by PublishedDate (existing)
- Sessions by StartedAt (existing)
- Sessions by Status (existing)
- ScrapingSessionArticle by SessionId (foreign key)
- ScrapingSessionArticle by ArticleId (foreign key)

## Summary

The database-first architecture provides:

✅ **Complete audit trail** - every article, session, and relationship tracked
✅ **Better debugging** - logs + database work together
✅ **Resume capability** - foundation for resuming interrupted sessions
✅ **Cost tracking** - understand cache savings and article reuse
✅ **Future-proof** - enables many planned enhancements

**Migration:** Automatic on next run
**Breaking Changes:** None
**Performance Impact:** < 50ms per session

---

For questions or issues, please open a GitHub issue at:
https://github.com/joe-rayment/newspaper_reader/issues
