// <copyright file="Program.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


using CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;
using NYTAudioScraper.Infrastructure;
using NYTAudioScraper.Infrastructure.Audio;
using NYTAudioScraper.Infrastructure.Metrics;
using NYTAudioScraper.Infrastructure.Persistence;
using Serilog;

namespace NYTAudioScraper.API;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(GetConfiguration())
            .CreateLogger();

        try
        {
            // Parse command line arguments
            var parser = new Parser(with => with.HelpWriter = Console.Error);
            var result = parser.ParseArguments<CommandOptions>(args);

            return await result.MapResult(
                async (CommandOptions opts) => await RunWithOptionsAsync(opts),
                errs => Task.FromResult(1)); // Return error code 1 for parsing errors
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static async Task<int> RunWithOptionsAsync(CommandOptions options)
    {
        Log.Information("Starting NYT Audio Scraper");
        Log.Information("Options: {@Options}", options);

        // Validate command options
        var validationErrors = options.Validate();
        if (validationErrors.Any())
        {
            Log.Error("Command validation failed:");
            foreach (var error in validationErrors)
            {
                Log.Error("  - {Error}", error);
            }
            return 1; // Return error code for validation failure
        }

        var host = CreateHostBuilder(options).Build();

        // Handle cookie management commands (these are standalone commands that exit after execution)
        if (options.CookieInfo)
        {
            return await HandleCookieInfoAsync(host.Services);
        }

        if (options.ClearCookies)
        {
            return await HandleClearCookiesAsync(host.Services);
        }

        if (options.TestMode)
        {
            // Run existing test workflow
            await RunApplicationAsync(host.Services);
        }
        else
        {
            // Run production workflow with options
            await RunProductionWorkflowAsync(host.Services, options);
        }

        Log.Information("Application completed successfully");
        return 0;
    }

    private static async Task<int> HandleCookieInfoAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        try
        {
            var cookieManager = scopedServices.GetRequiredService<ICookieManager>();
            var info = await cookieManager.GetCookieInfoAsync();

            if (info == null || !info.Exists)
            {
                Log.Information("No cookies found");
                Log.Information("Cookie file path: {Path}", info?.FilePath ?? "Unknown");
                return 0;
            }

            Console.WriteLine("\n╔═══════════════════════════════════════╗");
            Console.WriteLine("║        Cookie Information             ║");
            Console.WriteLine("╚═══════════════════════════════════════╝\n");

            Console.WriteLine($"File Path:    {info.FilePath}");
            Console.WriteLine($"Version:      v{info.Version} ({(info.IsEncrypted ? "Encrypted" : "Plain Text")})");
            Console.WriteLine($"Created At:   {info.CreatedAt:yyyy-MM-dd HH:mm:ss UTC}");

            if (info.ExpiresAt.HasValue)
            {
                var expiryStatus = info.IsExpired ? "EXPIRED" : "Valid";
                var expiryColor = info.IsExpired ? "❌" : "✓";
                Console.WriteLine($"Expires At:   {info.ExpiresAt:yyyy-MM-dd HH:mm:ss UTC} ({expiryColor} {expiryStatus})");

                if (!info.IsExpired)
                {
                    var timeRemaining = info.ExpiresAt.Value - DateTime.UtcNow;
                    Console.WriteLine($"Time Remaining: {timeRemaining.Days} days, {timeRemaining.Hours} hours");
                }
            }

            if (info.CookieCount.HasValue)
            {
                Console.WriteLine($"Cookie Count: {info.CookieCount}");
            }

            Console.WriteLine();

            if (!info.IsEncrypted)
            {
                Log.Warning("⚠️  Cookies are stored in plain text (v1 format)");
                Log.Warning("   They will be automatically migrated to encrypted format (v2) on next authentication");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get cookie information");
            return 1;
        }
    }

    private static async Task<int> HandleClearCookiesAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        try
        {
            var cookieManager = scopedServices.GetRequiredService<ICookieManager>();

            Console.Write("Are you sure you want to clear all stored cookies? (y/N): ");
            var response = Console.ReadLine();

            if (response?.Trim().ToLowerInvariant() != "y")
            {
                Log.Information("Cookie clearing cancelled");
                return 0;
            }

            var cleared = await cookieManager.ClearCookiesAsync();

            if (cleared)
            {
                Log.Information("✓ Cookies cleared successfully");
                Log.Information("  You will need to re-authenticate on the next run");
            }
            else
            {
                Log.Information("No cookies to clear");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clear cookies");
            return 1;
        }
    }

    public static IHostBuilder CreateHostBuilder(CommandOptions options) =>
        Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureAppConfiguration((context, config) =>
            {
                // Add secrets.json to the configuration
                var projectRoot = Directory.GetCurrentDirectory();
                var secretsPath = Path.Combine(projectRoot, "secrets.json");

                if (File.Exists(secretsPath))
                {
                    config.AddJsonFile(secretsPath, optional: true, reloadOnChange: true);
                    Log.Information("Loaded configuration from: {SecretsPath}", secretsPath);
                }
                else
                {
                    Log.Warning("secrets.json not found at: {SecretsPath}", secretsPath);
                }
            })
            .ConfigureServices((context, services) =>
            {
                // Register infrastructure services
                services.AddInfrastructure(context.Configuration);

                // Register command options as singleton so services can access them
                services.AddSingleton(options);
            });

    private static async Task RunApplicationAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        Log.Information("Starting NYT Audio Scraper Test Workflow");
        Log.Information("========================================");

        try
        {
            // Get required services
            var scraper = scopedServices.GetRequiredService<IScraperService>();
            var audioGenerator = scopedServices.GetRequiredService<IAudioGenerator>();
            var audioProcessor = scopedServices.GetRequiredService<IAudioProcessor>();
            var chapterMarker = scopedServices.GetRequiredService<IChapterMarker>();
            var fileStorage = scopedServices.GetRequiredService<IFileStorage>();
            var budgetService = scopedServices.GetRequiredService<IBudgetService>();

            Log.Information("✓ All services resolved successfully");

            // Set budget for testing
            budgetService.MaxBudget = 5.0m; // $5 limit for testing
            Log.Information("Budget set to ${MaxBudget:F2}", budgetService.MaxBudget);

            // Step 1: Scrape articles
            Log.Information("");
            Log.Information("Step 1: Scraping NYT articles...");
            var articles = await scraper.ScrapeArticlesAsync(maxArticles: 2);
            var articleList = articles.ToList();

            if (articleList.Count == 0)
            {
                Log.Warning("No articles scraped. This may be due to:");
                Log.Warning("  - Missing or invalid NYT credentials (check user secrets)");
                Log.Warning("  - NYT login page changed (scraper needs updating)");
                Log.Warning("  - Network issues or rate limiting");
                Log.Information("");
                Log.Information("Testing with mock article data instead...");

                // Create mock article for testing
                articleList.Add(new Article
                {
                    Id = "test-article-1",
                    Title = "Test Article: The Future of AI",
                    Url = "https://www.nytimes.com/test",
                    Author = "Test Author",
                    Section = "Technology",
                    Content = "This is a test article about artificial intelligence. " +
                             "Artificial intelligence is transforming the world in unprecedented ways. " +
                             "From healthcare to transportation, AI is making an impact. " +
                             "This test content is long enough to generate a meaningful audio file.",
                    PublishedDate = DateTime.UtcNow,
                    ScrapedDate = DateTime.UtcNow
                });
            }

            Log.Information("✓ Retrieved {Count} article(s)", articleList.Count);
            foreach (var article in articleList)
            {
                Log.Information("  - {Title} ({Words} words)", article.Title, article.EstimatedWordCount);
            }

            // Step 2: Generate audio for each article
            Log.Information("");
            Log.Information("Step 2: Generating audio files...");

            var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "output");
            Directory.CreateDirectory(outputDir);

            var audioFiles = new List<string>();
            var chapters = new List<AudioChapter>();
            var currentTimeMs = 0;

            foreach (var article in articleList)
            {
                try
                {
                    var estimatedCost = audioGenerator.EstimateCost(article.Content);
                    Log.Information("  Processing: {Title}", article.Title);
                    Log.Information("    Estimated cost: ${Cost:F4}", estimatedCost);

                    if (!budgetService.CanAfford(estimatedCost))
                    {
                        Log.Warning("    ⚠ Skipping - would exceed budget");
                        continue;
                    }

                    // Generate audio
                    var audioData = await audioGenerator.GenerateAudioAsync(
                        article.Content,
                        "21m00Tcm4TlvDq8ikWAM"); // Default voice ID from config

                    // Save audio file
                    var audioFilePath = Path.Combine(outputDir, $"{article.Id}.mp3");
                    await File.WriteAllBytesAsync(audioFilePath, audioData);
                    audioFiles.Add(audioFilePath);

                    Log.Information("    ✓ Generated audio: {Size:N0} bytes", audioData.Length);
                    Log.Information("    ✓ Saved to: {Path}", audioFilePath);

                    // Get audio metadata for chapter timing
                    var metadata = await audioProcessor.GetMetadataAsync(audioFilePath);
                    var durationMs = metadata.DurationMs;

                    // Create chapter entry
                    var chapter = new AudioChapter
                    {
                        Title = article.Title,
                        ArticleId = article.Id,
                        StartTimeMs = currentTimeMs,
                        DurationMs = durationMs,
                        AudioFilePath = audioFilePath
                    };
                    chapters.Add(chapter);
                    currentTimeMs += durationMs;

                    Log.Information("    Chapter: {Start:F1}s - {End:F1}s",
                        chapter.StartTimeMs / 1000.0,
                        chapter.EndTimeMs / 1000.0);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "    ✗ Error processing article: {Title}", article.Title);
                }
            }

            if (audioFiles.Count == 0)
            {
                Log.Warning("No audio files generated. Cannot create audiobook.");
                Log.Information("");
                Log.Information("Budget Summary: {Summary}", budgetService.GetSummary());
                return;
            }

            // Step 3: Create M4B audiobook
            Log.Information("");
            Log.Information("Step 3: Creating M4B audiobook...");
            var audiobookPath = Path.Combine(outputDir, $"nyt-audiobook-{DateTime.Now:yyyyMMdd-HHmmss}.m4b");

            try
            {
                var createdPath = await audioProcessor.CreateAudiobookAsync(audioFiles, audiobookPath);
                Log.Information("✓ Audiobook created: {Path}", createdPath);

                var audiobookMetadata = await audioProcessor.GetMetadataAsync(createdPath);
                Log.Information("  Duration: {Duration:F1} minutes", audiobookMetadata.DurationMinutes);
                Log.Information("  File size: {Size:F2} MB", audiobookMetadata.FileSizeMB);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "✗ Error creating audiobook");
                Log.Warning("This may be due to FFmpeg not being installed.");
                Log.Warning("Install FFmpeg: brew install ffmpeg");
                return;
            }

            // Step 4: Add chapter markers
            Log.Information("");
            Log.Information("Step 4: Adding chapter markers...");
            try
            {
                await chapterMarker.AddChaptersAsync(audiobookPath, chapters);
                Log.Information("✓ Added {Count} chapter marker(s)", chapters.Count);
                foreach (var chapter in chapters)
                {
                    Log.Information("  - {Title} @ {Time:F1}s",
                        chapter.Title,
                        chapter.StartTimeMs / 1000.0);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "✗ Error adding chapter markers");
            }

            // Final summary
            Log.Information("");
            Log.Information("========================================");
            Log.Information("Test Workflow Complete!");
            Log.Information("========================================");
            Log.Information("Output directory: {Dir}", outputDir);
            Log.Information("Audiobook file: {File}", Path.GetFileName(audiobookPath));
            Log.Information("Budget used: {Summary}", budgetService.GetSummary());
            Log.Information("");
            Log.Information("You can now play the audiobook in any M4B-compatible player.");
            Log.Information("Recommended: Apple Books, VLC, or any audiobook player");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error in test workflow");
            throw;
        }
    }

    private static async Task RunProductionWorkflowAsync(IServiceProvider services, CommandOptions options)
    {
        // Create a scope for scoped services (AppDbContext, etc.)
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        // Initialize database
        var dbContext = scopedServices.GetRequiredService<AppDbContext>();
        await dbContext.InitializeDatabaseAsync();
        Log.Information("✓ Database initialized");

        // Run health checks
        Log.Information("");
        Log.Information("Running health checks...");
        var healthCheckService = scopedServices.GetRequiredService<HealthCheckService>();
        var healthReport = await healthCheckService.CheckHealthAsync();

        foreach (var entry in healthReport.Entries)
        {
            var icon = entry.Value.Status switch
            {
                HealthStatus.Healthy => "✓",
                HealthStatus.Degraded => "⚠",
                HealthStatus.Unhealthy => "✗",
                _ => "?"
            };

            var level = entry.Value.Status switch
            {
                HealthStatus.Healthy => "Information",
                HealthStatus.Degraded => "Warning",
                HealthStatus.Unhealthy => "Error",
                _ => "Information"
            };

            if (entry.Value.Status == HealthStatus.Healthy)
                Log.Information("{Icon} {Name}: {Description}", icon, entry.Key, entry.Value.Description);
            else if (entry.Value.Status == HealthStatus.Degraded)
                Log.Warning("{Icon} {Name}: {Description}", icon, entry.Key, entry.Value.Description);
            else
                Log.Error("{Icon} {Name}: {Description}", icon, entry.Key, entry.Value.Description);
        }

        if (healthReport.Status == HealthStatus.Unhealthy)
        {
            Log.Error("");
            Log.Error("Health checks failed. Cannot proceed with workflow.");
            Log.Error("Please fix the issues above and try again.");
            return;
        }

        if (healthReport.Status == HealthStatus.Degraded)
        {
            Log.Warning("");
            Log.Warning("Some health checks reported warnings. Proceeding anyway...");
        }
        else
        {
            Log.Information("All health checks passed ✓");
        }
        Log.Information("");

        // Initialize performance metrics
        var metrics = new PerformanceMetrics();

        // Get services
        var scraper = scopedServices.GetRequiredService<IScraperService>();
        var audioGenerator = scopedServices.GetRequiredService<IAudioGenerator>();
        var parallelAudioGenerator = scopedServices.GetRequiredService<IParallelAudioGenerator>();
        var audioProcessor = scopedServices.GetRequiredService<IAudioProcessor>();
        var chapterMarker = scopedServices.GetRequiredService<IChapterMarker>();
        var budgetService = scopedServices.GetRequiredService<IBudgetService>();
        var sessionRepo = scopedServices.GetRequiredService<IScrapingSessionRepository>();
        var unitOfWork = scopedServices.GetRequiredService<IUnitOfWork>();

        // Set budget from command line
        budgetService.MaxBudget = options.Budget;
        Log.Information("Budget set to ${MaxBudget:F2}", budgetService.MaxBudget);

        // Create scraping session
        var session = new ScrapingSession
        {
            Id = Guid.NewGuid().ToString(),
            StartedAt = DateTime.UtcNow,
            Status = ScrapingStatus.InProgress,
            Articles = new List<Article>()
        };

        await sessionRepo.AddAsync(session);
        await unitOfWork.SaveChangesAsync();
        Log.Information("✓ Created scraping session: {SessionId}", session.Id);

        try
        {
            // Determine output directory
            var outputDir = !string.IsNullOrEmpty(options.OutputPath)
                ? options.OutputPath
                : Path.Combine(Directory.GetCurrentDirectory(), "output");
            Directory.CreateDirectory(outputDir);

            // Step 1: Get articles
            Log.Information("");
            Log.Information("Step 1: Scraping articles...");
            IEnumerable<Article> articles;

            using (metrics.Measure("scraping"))
            {
                if (!string.IsNullOrEmpty(options.ArticleUrl))
                {
                    Log.Information("Single article mode: {Url}", options.ArticleUrl);
                    var article = await scraper.ScrapeArticleByUrlAsync(options.ArticleUrl);
                    articles = article != null ? new[] { article } : Enumerable.Empty<Article>();
                }
                else if (!string.IsNullOrEmpty(options.Section))
                {
                    var sections = options.Section.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    Log.Information("Scraping {Count} articles from sections: {Sections}",
                        options.ArticleCount, string.Join(", ", sections));
                    articles = await scraper.ScrapeArticlesBySectionsAsync(options.ArticleCount, sections);
                }
                else
                {
                    Log.Information("Scraping {Count} articles from default sections", options.ArticleCount);
                    articles = await scraper.ScrapeArticlesAsync(options.ArticleCount);
                }
            }

            var articleList = articles.ToList();
            metrics.Increment("articles_scraped", articleList.Count);
        if (articleList.Count == 0)
        {
            Log.Warning("No articles found to process");
            return;
        }

        Log.Information("✓ Retrieved {Count} article(s)", articleList.Count);
        foreach (var article in articleList)
        {
            Log.Information("  - [{ArticleId}] {Title} ({Words} words, {Url})",
                article.Id, article.Title, article.EstimatedWordCount, article.Url);
        }

        // Link articles to session for complete audit trail
        Log.Information("");
        Log.Information("Linking articles to session {SessionId}...", session.Id);
        foreach (var article in articleList)
        {
            session.Articles.Add(article);
        }
        await unitOfWork.SaveChangesAsync();
        Log.Information("✓ Linked {Count} articles to session", articleList.Count);

        // Step 2: Generate audio for articles (parallel processing)
        Log.Information("");
        Log.Information("Step 2: Generating audio files (parallel processing)...");

        var audioFiles = new List<string>();
        var chapters = new List<AudioChapter>();
        var currentTimeMs = 0;

        var voiceId = options.VoiceId ?? "21m00Tcm4TlvDq8ikWAM"; // Default voice

        AudioGenerationResult result;
        using (metrics.Measure("audio_generation"))
        {
            // Use parallel audio generator for concurrent processing
            result = await parallelAudioGenerator.GenerateAudioForArticlesAsync(
                articleList,
                voiceId,
                cancellationToken: default);
        }

        metrics.Increment("audio_generated", result.SuccessCount);
        metrics.Increment("audio_failed", result.FailureCount);

        // Log results
        var audioGenMetrics = metrics.GetSummary().Operations["audio_generation"];
        Log.Information("✓ Audio generation completed in {Elapsed:F1}s", audioGenMetrics.Average.TotalSeconds);
        Log.Information("  Success: {SuccessCount}/{Total} articles", result.SuccessCount, result.TotalProcessed);
        if (result.FailureCount > 0)
        {
            Log.Warning("  Failed: {FailureCount}/{Total} articles", result.FailureCount, result.TotalProcessed);
        }

        // Process successful generations
        foreach (var (articleId, audioData) in result.SuccessfulGenerations)
        {
            var article = articleList.First(a => a.Id == articleId);

            // Save audio file
            var audioFilePath = Path.Combine(outputDir, $"{articleId}.mp3");
            await File.WriteAllBytesAsync(audioFilePath, audioData);
            audioFiles.Add(audioFilePath);

            // Update article with audio file path in database
            article.AudioFilePath = audioFilePath;

            Log.Information("  ✓ [{ArticleId}] Saved: {Title} ({Size:N0} bytes)",
                article.Id, article.Title, audioData.Length);

            // Get audio metadata for chapter timing
            var metadata = await audioProcessor.GetMetadataAsync(audioFilePath);
            var durationMs = metadata.DurationMs;

            // Create chapter entry
            var chapter = new AudioChapter
            {
                Title = article.Title,
                ArticleId = article.Id,
                StartTimeMs = currentTimeMs,
                DurationMs = durationMs,
                AudioFilePath = audioFilePath
            };
            chapters.Add(chapter);
            currentTimeMs += durationMs;

            Log.Information("    Chapter: {Start:F1}s - {End:F1}s",
                chapter.StartTimeMs / 1000.0,
                chapter.EndTimeMs / 1000.0);
        }

        // Log failures
        foreach (var (articleId, errorMessage) in result.FailedGenerations)
        {
            var article = articleList.First(a => a.Id == articleId);
            Log.Error("  ✗ [{ArticleId}] Failed: {Title} - {Error}",
                article.Id, article.Title, errorMessage);
        }

        // Persist audio file paths to database
        if (result.SuccessCount > 0)
        {
            await unitOfWork.SaveChangesAsync();
            Log.Information("✓ Updated {Count} articles with audio file paths in database",
                result.SuccessCount);
        }

        if (audioFiles.Count == 0)
        {
            Log.Warning("No audio files generated. Cannot create audiobook.");
            Log.Information("");
            Log.Information("Budget Summary: {Summary}", budgetService.GetSummary());
            return;
        }

        // Step 3: Create M4B audiobook
        Log.Information("");
        Log.Information("Step 3: Creating M4B audiobook...");
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var audiobookPath = Path.Combine(outputDir, $"nyt-audiobook-{timestamp}.m4b");

        try
        {
            string createdPath;
            using (metrics.Measure("audiobook_creation"))
            {
                createdPath = await audioProcessor.CreateAudiobookAsync(audioFiles, audiobookPath);
            }

            Log.Information("✓ Audiobook created: {Path}", createdPath);

            var audiobookMetadata = await audioProcessor.GetMetadataAsync(createdPath);
            Log.Information("  Duration: {Duration:F1} minutes", audiobookMetadata.DurationMinutes);
            Log.Information("  File size: {Size:F2} MB", audiobookMetadata.FileSizeMB);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "✗ Error creating audiobook");
            Log.Warning("This may be due to FFmpeg not being installed.");
            Log.Warning("Install FFmpeg: brew install ffmpeg");
            return;
        }

        // Step 4: Add chapter markers
        Log.Information("");
        Log.Information("Step 4: Adding chapter markers...");
        try
        {
            using (metrics.Measure("chapter_markers"))
            {
                await chapterMarker.AddChaptersAsync(audiobookPath, chapters);
            }

            Log.Information("✓ Added {Count} chapter marker(s)", chapters.Count);
            foreach (var chapter in chapters)
            {
                Log.Information("  - {Title} @ {Time:F1}s",
                    chapter.Title,
                    chapter.StartTimeMs / 1000.0);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "✗ Error adding chapter markers");
        }

        // Clean up individual MP3 files
        foreach (var file in audioFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to delete temporary file: {File}", file);
            }
        }

            // Performance summary
            Log.Information("");
            Log.Information("========================================");
            Log.Information("Performance Summary");
            Log.Information("========================================");

            var summary = metrics.GetSummary();
            foreach (var (operation, stats) in summary.Operations.OrderBy(x => x.Key))
            {
                Log.Information("{Operation}:", operation);
                Log.Information("  Average: {Avg:F1}s", stats.Average.TotalSeconds);
                if (stats.Count > 1)
                {
                    Log.Information("  Min/Max: {Min:F1}s / {Max:F1}s", stats.Min.TotalSeconds, stats.Max.TotalSeconds);
                    Log.Information("  P95: {P95:F1}s", stats.P95.TotalSeconds);
                }
            }

            Log.Information("");
            Log.Information("Counters:");
            foreach (var (counter, value) in summary.Counters.OrderBy(x => x.Key))
            {
                Log.Information("  {Counter}: {Value}", counter, value);
            }

            // Final summary
            Log.Information("");
            Log.Information("========================================");
            Log.Information("Audiobook Created Successfully!");
            Log.Information("========================================");
            Log.Information("Output: {Path}", audiobookPath);
            Log.Information("Budget used: {Summary}", budgetService.GetSummary());
            Log.Information("");
            Log.Information("You can now play the audiobook in any M4B-compatible player.");
            Log.Information("Recommended: Apple Books, VLC, or any audiobook player");

            // Update session on successful completion
            session.Status = ScrapingStatus.Completed;
            session.CompletedAt = DateTime.UtcNow;
            session.OutputFilePath = audiobookPath;
            session.TotalCharactersProcessed = articleList.Sum(a => a.Content.Length);
            session.EstimatedCost = budgetService.TotalSpent;

            await sessionRepo.UpdateAsync(session);
            await unitOfWork.SaveChangesAsync();

            Log.Information("");
            Log.Information("========================================");
            Log.Information("Session Summary");
            Log.Information("========================================");
            Log.Information("Session ID: {SessionId}", session.Id);
            Log.Information("Articles in session: {Count}", session.Articles.Count);
            Log.Information("Articles with audio: {Count}",
                session.Articles.Count(a => !string.IsNullOrEmpty(a.AudioFilePath)));
            Log.Information("Total characters: {Total:N0}", session.TotalCharactersProcessed);
            Log.Information("Total cost: ${Cost:F4}", session.EstimatedCost);
            Log.Information("Duration: {Duration:F1} minutes",
                (session.CompletedAt.Value - session.StartedAt).TotalMinutes);
            Log.Information("");
            Log.Information("💾 Database contains full article content and session history");
            Log.Information("   Use session ID to query article details from database");
            Log.Information("✓ Session completed successfully: {SessionId}", session.Id);
        }
        catch (Exception ex)
        {
            // Update session on failure
            session.Status = ScrapingStatus.Failed;
            session.ErrorMessage = ex.Message;
            session.CompletedAt = DateTime.UtcNow;

            await sessionRepo.UpdateAsync(session);
            await unitOfWork.SaveChangesAsync();
            Log.Error("✗ Session failed: {SessionId} - {Error}", session.Id, ex.Message);

            throw; // Re-throw to maintain existing error handling
        }
    }

    private static IConfiguration GetConfiguration()
    {
        // Get the directory where the application assembly is located
        var basePath = AppContext.BaseDirectory;

        // Find the project root directory (where secrets.json should be)
        var projectRoot = Directory.GetCurrentDirectory();
        var secretsPath = Path.Combine(projectRoot, "secrets.json");

        var builder = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables();

        // Add secrets.json if it exists
        if (File.Exists(secretsPath))
        {
            Log.Debug("Loading secrets from: {SecretsPath}", secretsPath);
            builder.AddJsonFile(secretsPath, optional: true, reloadOnChange: true);
        }
        else
        {
            Log.Warning("secrets.json not found at: {SecretsPath}", secretsPath);
        }

        // User secrets (for backwards compatibility)
        builder.AddUserSecrets<Program>(optional: true);

        return builder.Build();
    }
}
