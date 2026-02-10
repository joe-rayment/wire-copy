// <copyright file="Program.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


using CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities;
using TermReader.Infrastructure;
using TermReader.Infrastructure.Audio;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Metrics;
using TermReader.Infrastructure.Persistence;
using TermReader.Infrastructure.Podcast;
using Serilog;

namespace TermReader.API;

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
            // Parse command line arguments with verb support
            var parser = new Parser(with => with.HelpWriter = Console.Error);
            var result = parser.ParseArguments<BrowseOptions, CommandOptions>(args);

            return await result.MapResult(
                async (BrowseOptions opts) => await RunBrowseVerbAsync(opts),
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
        Log.Information("Starting TermReader");
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

        if (!string.IsNullOrEmpty(options.ImportCookiesPath))
        {
            return await HandleImportCookiesAsync(host.Services, options.ImportCookiesPath);
        }

        if (options.BrowseMode)
        {
            // Run terminal browser mode
            await RunBrowserModeAsync(host.Services, options);
        }
        else if (options.TestMode)
        {
            // Run existing test workflow
            await RunApplicationAsync(host.Services);
        }
        else if (options.AudioOnly)
        {
            // Run audio-only workflow (skip scraping, use database articles)
            await RunAudioOnlyWorkflowAsync(host.Services, options);
        }
        else
        {
            // Run production workflow with options
            await RunProductionWorkflowAsync(host.Services, options);
        }

        Log.Information("Application completed successfully");
        return 0;
    }

    private static async Task<int> RunBrowseVerbAsync(BrowseOptions options)
    {
        // Validate browse options
        var validationErrors = options.Validate();
        if (validationErrors.Any())
        {
            foreach (var error in validationErrors)
            {
                Console.Error.WriteLine($"Error: {error}");
            }

            return 1;
        }

        // Prompt for URL if not provided
        var url = options.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Write("Enter URL to browse (or press Enter for https://news.ycombinator.com): ");
            var input = Console.ReadLine()?.Trim();
            url = string.IsNullOrWhiteSpace(input) ? "https://news.ycombinator.com" : input;

            // Validate the entered URL
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Console.Error.WriteLine($"Error: Invalid URL '{url}'. Must be a valid HTTP/HTTPS URL.");
                return 1;
            }
        }

        // Create a minimal host for browser services
        var host = CreateBrowseHostBuilder().Build();

        try
        {
            var browser = host.Services.GetRequiredService<IBrowserService>();
            await browser.RunAsync(url);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in browser mode");
            return 1;
        }
    }

    private static IHostBuilder CreateBrowseHostBuilder() =>
        Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureAppConfiguration((context, config) =>
            {
                var basePath = AppContext.BaseDirectory;
                config.SetBasePath(basePath);
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
            })
            .ConfigureServices((context, services) =>
            {
                // Only register terminal browser services for browse mode
                services.AddTerminalBrowser();
            });

    private static async Task<int> HandleCookieInfoAsync(IServiceProvider services)
    {
        try
        {
            var cookieImporter = services.GetRequiredService<CookieImporter>();
            var info = await cookieImporter.GetCookieInfoAsync();

            if (!info.HasCookies)
            {
                Log.Information("No cookies stored");
                if (!string.IsNullOrEmpty(info.Message))
                {
                    Log.Information(info.Message);
                }
                return 0;
            }

            Console.WriteLine("\n╔═══════════════════════════════════════╗");
            Console.WriteLine("║        Cookie Information             ║");
            Console.WriteLine("╚═══════════════════════════════════════╝\n");

            Console.WriteLine($"Cookie Count:   {info.CookieCount}");
            Console.WriteLine($"Auth Cookie:    {(info.HasAuthCookie ? "✓ Present" : "✗ Missing")}");

            if (info.CreatedAt.HasValue)
            {
                Console.WriteLine($"Created At:     {info.CreatedAt:yyyy-MM-dd HH:mm:ss UTC}");
            }

            if (info.ExpiresAt.HasValue)
            {
                var expiryStatus = info.IsExpired ? "EXPIRED" : "Valid";
                var expiryColor = info.IsExpired ? "❌" : "✓";
                Console.WriteLine($"Expires At:     {info.ExpiresAt:yyyy-MM-dd HH:mm:ss UTC} ({expiryColor} {expiryStatus})");

                if (!info.IsExpired)
                {
                    Console.WriteLine($"Time Remaining: {info.DaysUntilExpiration} days");
                }
            }

            Console.WriteLine();

            if (!info.HasAuthCookie)
            {
                Log.Warning("⚠️  No authentication cookies found");
                Log.Warning("   You may need to re-import cookies to access subscriber content");
            }

            if (info.IsExpired)
            {
                Log.Warning("⚠️  Cookies have expired");
                Log.Warning("   Please import fresh cookies using --import-cookies");
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
        try
        {
            var cookieImporter = services.GetRequiredService<CookieImporter>();

            Console.Write("Are you sure you want to clear all stored cookies? (y/N): ");
            var response = Console.ReadLine();

            if (response?.Trim().ToLowerInvariant() != "y")
            {
                Log.Information("Cookie clearing cancelled");
                return 0;
            }

            var cleared = await cookieImporter.ClearCookiesAsync();

            if (cleared)
            {
                Log.Information("✓ Cookies cleared successfully");
                Log.Information("  You will need to import new cookies or re-authenticate on the next run");
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

    private static async Task<int> HandleImportCookiesAsync(IServiceProvider services, string cookieFilePath)
    {
        try
        {
            var cookieImporter = services.GetRequiredService<CookieImporter>();

            Log.Information("Importing cookies from: {FilePath}", cookieFilePath);

            var result = await cookieImporter.ImportFromJsonAsync(cookieFilePath);

            if (!result.Success)
            {
                Log.Error("Cookie import failed: {Error}", result.ErrorMessage);
                return 1;
            }

            Console.WriteLine("\n╔═══════════════════════════════════════╗");
            Console.WriteLine("║     Cookie Import Successful          ║");
            Console.WriteLine("╚═══════════════════════════════════════╝\n");

            Console.WriteLine($"Cookies Imported:  {result.CookieCount}");
            Console.WriteLine($"Auth Cookie:       {(result.HasAuthCookie ? "✓ Present" : "✗ Missing")}");

            if (result.ExpiresAt.HasValue)
            {
                Console.WriteLine($"Expires At:        {result.ExpiresAt:yyyy-MM-dd HH:mm:ss UTC}");
                Console.WriteLine($"Time Remaining:    {result.DaysUntilExpiration} days");
            }

            Console.WriteLine();

            if (result.HasAuthCookie)
            {
                Log.Information("✓ Authentication cookies found - you should be able to access subscriber content");
                Log.Information("  Run the scraper with --skip-login to use these cookies without re-authenticating");
            }
            else
            {
                Log.Warning("⚠️  No authentication cookies detected");
                Log.Warning("   Make sure you exported cookies after logging in to nytimes.com");
                Log.Warning("   You may not be able to access subscriber-only content");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to import cookies");
            return 1;
        }
    }

    private static async Task RunBrowserModeAsync(IServiceProvider services, CommandOptions options)
    {
        Log.Information("Starting Terminal Browser Mode");
        Log.Information("==============================");
        Log.Information("");

        try
        {
            var browser = services.GetRequiredService<IBrowserService>();

            // Run the browser loop with optional initial URL
            await browser.RunAsync(options.BrowseUrl);

            Log.Information("");
            Log.Information("Browser session ended");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in browser mode");
            throw;
        }
    }

    public static IHostBuilder CreateHostBuilder(CommandOptions options) =>
        Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureAppConfiguration((context, config) =>
            {
                // Ensure appsettings.json is loaded from the correct location
                var basePath = AppContext.BaseDirectory;
                config.SetBasePath(basePath);
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

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

                // Register terminal browser services (if browse mode is enabled)
                if (options.BrowseMode)
                {
                    services.AddTerminalBrowser();
                }

                // Register command options as singleton so services can access them
                services.AddSingleton(options);
            });

    private static async Task RunApplicationAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        Log.Information("Starting TermReader Test Workflow");
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
            var audiobookPath = Path.Combine(outputDir, $"audiobook-{DateTime.Now:yyyyMMdd-HHmmss}.m4b");

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

    private static async Task RunAudioOnlyWorkflowAsync(IServiceProvider services, CommandOptions options)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        // Initialize database
        var dbContext = scopedServices.GetRequiredService<AppDbContext>();
        await dbContext.InitializeDatabaseAsync();
        Log.Information("Database initialized");

        Log.Information("");
        Log.Information("========================================");
        Log.Information("Audio-Only Mode");
        Log.Information("========================================");
        Log.Information("Generating audio from previously scraped articles");
        Log.Information("");

        // Get services
        var articleRepo = scopedServices.GetRequiredService<IArticleRepository>();
        var parallelAudioGenerator = scopedServices.GetRequiredService<IParallelAudioGenerator>();
        var audioProcessor = scopedServices.GetRequiredService<IAudioProcessor>();
        var chapterMarker = scopedServices.GetRequiredService<IChapterMarker>();
        var budgetService = scopedServices.GetRequiredService<IBudgetService>();
        var unitOfWork = scopedServices.GetRequiredService<IUnitOfWork>();
        var mp3Tagger = scopedServices.GetRequiredService<IMp3Tagger>();
        var rssFeedGenerator = scopedServices.GetRequiredService<IRssFeedGenerator>();
        var chaptersJsonGenerator = scopedServices.GetRequiredService<IChaptersJsonGenerator>();
        var metrics = new PerformanceMetrics();

        // Set budget from command line
        budgetService.MaxBudget = options.Budget;
        Log.Information("Budget set to ${MaxBudget:F2}", budgetService.MaxBudget);

        // Step 1: Query articles from database
        Log.Information("");
        Log.Information("Step 1: Querying articles from database...");

        IEnumerable<Article> articles;

        if (options.PublishedToday)
        {
            var today = DateTime.UtcNow.Date;
            Log.Information("Filtering by scraped date: Today ({Date:yyyy-MM-dd})", today);
            articles = await articleRepo.GetByScrapedDateAsync(
                today,
                options.Section,
                options.ArticleCount);
        }
        else
        {
            // No date filter - get recent articles
            Log.Information("No date filter specified - using recently scraped articles");
            articles = await articleRepo.GetRecentlyScrapedAsync(options.ArticleCount);

            // Apply section filter if provided
            if (!string.IsNullOrWhiteSpace(options.Section))
            {
                var sections = options.Section.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                articles = articles.Where(a => a.Section != null && sections.Contains(a.Section));
            }
        }

        var articleList = articles.ToList();

        if (articleList.Count == 0)
        {
            Log.Warning("No articles found matching the specified criteria");
            Log.Information("Criteria:");
            if (options.PublishedToday)
            {
                Log.Information("  Scraped date: today ({Date:yyyy-MM-dd})", DateTime.UtcNow.Date);
            }
            if (!string.IsNullOrWhiteSpace(options.Section))
            {
                Log.Information("  Sections: {Sections}", options.Section);
            }
            Log.Information("  Max count: {Count}", options.ArticleCount);
            return;
        }

        Log.Information("Found {Count} article(s) matching criteria", articleList.Count);
        foreach (var article in articleList)
        {
            var hasAudio = !string.IsNullOrEmpty(article.AudioFilePath) ? "has audio" : "needs audio";
            Log.Information("  - [{ArticleId}] {Title} ({Words} words, {Status})",
                article.Id, article.Title, article.EstimatedWordCount, hasAudio);
        }

        // Step 2: Determine output directory
        var outputDir = !string.IsNullOrEmpty(options.OutputPath)
            ? options.OutputPath
            : Path.Combine(Directory.GetCurrentDirectory(), "output");
        Directory.CreateDirectory(outputDir);

        // Step 3: Separate articles into those with/without audio
        Log.Information("");
        Log.Information("Step 2: Checking existing audio files...");

        var articlesNeedingAudio = new List<Article>();
        var articlesWithExistingAudio = new List<Article>();

        foreach (var article in articleList)
        {
            if (!string.IsNullOrEmpty(article.AudioFilePath) && File.Exists(article.AudioFilePath))
            {
                articlesWithExistingAudio.Add(article);
                Log.Information("  [{ArticleId}] Using existing audio: {Title}",
                    article.Id, article.Title);
            }
            else
            {
                articlesNeedingAudio.Add(article);
                if (!string.IsNullOrEmpty(article.AudioFilePath))
                {
                    Log.Warning("  [{ArticleId}] Audio file missing, will regenerate: {Title}",
                        article.Id, article.Title);
                }
            }
        }

        Log.Information("");
        Log.Information("Audio summary:");
        Log.Information("  Existing audio files: {Count}", articlesWithExistingAudio.Count);
        Log.Information("  Need generation: {Count}", articlesNeedingAudio.Count);

        // Step 4: Generate audio for articles that need it
        AudioGenerationResult? result = null;
        if (articlesNeedingAudio.Count > 0)
        {
            Log.Information("");
            Log.Information("Step 3: Generating audio files (parallel processing)...");

            var voiceId = options.VoiceId ?? "21m00Tcm4TlvDq8ikWAM";

            using (metrics.Measure("audio_generation"))
            {
                result = await parallelAudioGenerator.GenerateAudioForArticlesAsync(
                    articlesNeedingAudio,
                    voiceId,
                    cancellationToken: default);
            }

            metrics.Increment("audio_generated", result.SuccessCount);
            metrics.Increment("audio_failed", result.FailureCount);

            var audioGenMetrics = metrics.GetSummary().Operations["audio_generation"];
            Log.Information("Audio generation completed in {Elapsed:F1}s", audioGenMetrics.Average.TotalSeconds);
            Log.Information("  Success: {SuccessCount}/{Total} articles", result.SuccessCount, result.TotalProcessed);

            if (result.FailureCount > 0)
            {
                Log.Warning("  Failed: {FailureCount}/{Total} articles", result.FailureCount, result.TotalProcessed);
            }

            // Save newly generated audio files
            foreach (var (articleId, audioData) in result.SuccessfulGenerations)
            {
                var article = articlesNeedingAudio.First(a => a.Id == articleId);

                var audioFilePath = Path.Combine(outputDir, $"{articleId}.mp3");
                await File.WriteAllBytesAsync(audioFilePath, audioData);

                // Update article with audio file path
                article.AudioFilePath = audioFilePath;

                Log.Information("  [{ArticleId}] Saved: {Title} ({Size:N0} bytes)",
                    article.Id, article.Title, audioData.Length);
            }

            // Log failures
            foreach (var (articleId, errorMessage) in result.FailedGenerations)
            {
                var article = articlesNeedingAudio.First(a => a.Id == articleId);
                Log.Error("  [{ArticleId}] Failed: {Title} - {Error}",
                    article.Id, article.Title, errorMessage);
            }

            // Persist audio file paths to database
            if (result.SuccessCount > 0)
            {
                await unitOfWork.SaveChangesAsync();
                Log.Information("Updated {Count} articles with audio file paths in database",
                    result.SuccessCount);
            }
        }
        else
        {
            Log.Information("");
            Log.Information("Step 3: Skipped - all articles have existing audio files");
        }

        // Step 5: Collect all audio files for M4B creation
        Log.Information("");
        Log.Information("Step 4: Preparing audiobook...");

        var allArticlesForAudiobook = articleList
            .Where(a => !string.IsNullOrEmpty(a.AudioFilePath) && File.Exists(a.AudioFilePath))
            .ToList();

        if (allArticlesForAudiobook.Count == 0)
        {
            Log.Warning("No audio files available to create audiobook.");
            Log.Information("Budget Summary: {Summary}", budgetService.GetSummary());
            return;
        }

        // Build chapter list and collect audio file paths
        var audioFiles = new List<string>();
        var chapters = new List<AudioChapter>();
        var currentTimeMs = 0;

        foreach (var article in allArticlesForAudiobook)
        {
            audioFiles.Add(article.AudioFilePath!);

            var metadata = await audioProcessor.GetMetadataAsync(article.AudioFilePath!);
            var durationMs = metadata.DurationMs;

            var chapter = new AudioChapter
            {
                Title = article.Title,
                ArticleId = article.Id,
                StartTimeMs = currentTimeMs,
                DurationMs = durationMs,
                AudioFilePath = article.AudioFilePath!
            };
            chapters.Add(chapter);
            currentTimeMs += durationMs;

            Log.Information("  Chapter: {Title} @ {Start:F1}s - {End:F1}s",
                chapter.Title,
                chapter.StartTimeMs / 1000.0,
                chapter.EndTimeMs / 1000.0);
        }

        Log.Information("Prepared {Count} chapters for audiobook", chapters.Count);

        string outputFilePath;

        if (options.PodcastMode)
        {
            // Podcast mode: Create single M4A with chapters + RSS feed
            Log.Information("");
            Log.Information("Step 5: Creating combined podcast file with chapters...");

            var dateStr = DateTime.Now.ToString("yyyy-MM-dd");
            var displayDateStr = DateTime.Now.ToString("MMM dd, yyyy");
            var podcastFileName = $"{dateStr}-nyt-todays-paper.m4a";
            var chaptersFileName = $"{dateStr}-chapters.json";
            var podcastPath = Path.Combine(outputDir, podcastFileName);

            // Create combined M4A using existing AudioProcessor
            try
            {
                string createdPath;
                using (metrics.Measure("podcast_creation"))
                {
                    createdPath = await audioProcessor.CreateAudiobookAsync(audioFiles, podcastPath);
                }

                Log.Information("Combined audio created: {Path}", createdPath);

                var podcastMetadata = await audioProcessor.GetMetadataAsync(createdPath);
                Log.Information("  Duration: {Duration:F1} minutes", podcastMetadata.DurationMinutes);
                Log.Information("  File size: {Size:F2} MB", podcastMetadata.FileSizeMB);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating combined podcast file");
                Log.Warning("This may be due to FFmpeg not being installed.");
                Log.Warning("Install FFmpeg: brew install ffmpeg");
                return;
            }

            // Add chapter markers to M4A
            Log.Information("");
            Log.Information("Step 6: Adding chapter markers...");
            try
            {
                using (metrics.Measure("chapter_markers"))
                {
                    await chapterMarker.AddChaptersAsync(podcastPath, chapters);
                }

                Log.Information("Added {Count} chapter marker(s)", chapters.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error adding chapter markers");
            }

            // Generate chapters.json (Podcasting 2.0 format)
            Log.Information("");
            Log.Information("Step 7: Generating chapters JSON...");
            var chaptersPath = Path.Combine(outputDir, chaptersFileName);
            await chaptersJsonGenerator.SaveJsonAsync(chapters, chaptersPath);
            Log.Information("Chapters JSON saved: {Path}", chaptersPath);

            // Generate RSS feed with single combined episode
            Log.Information("");
            Log.Information("Step 8: Generating RSS feed...");

            var podcastFileInfo = new FileInfo(podcastPath);
            var podcastAudioMetadata = await audioProcessor.GetMetadataAsync(podcastPath);

            var combinedEpisode = new CombinedPodcastEpisode(
                Title: $"NYT Today's Paper - {displayDateStr}",
                Description: "Daily articles from The New York Times Today's Paper",
                AudioFileName: podcastFileName,
                ChaptersFileName: chaptersFileName,
                PubDate: DateTime.UtcNow,
                FileSizeBytes: podcastFileInfo.Length,
                DurationSeconds: (int)(podcastAudioMetadata.DurationMs / 1000),
                Guid: $"nyt-{dateStr}",
                ChapterTitles: chapters.Select(c => c.Title).ToList());

            var podcastInfo = new PodcastInfo(
                Title: "NYT Today's Paper",
                Description: "Daily articles from The New York Times Today's Paper",
                Author: "The New York Times");

            var feedPath = Path.Combine(outputDir, "feed.xml");
            await rssFeedGenerator.SaveCombinedFeedAsync(combinedEpisode, podcastInfo, feedPath);

            // Clean up individual MP3 files
            Log.Information("");
            Log.Information("Cleaning up temporary files...");
            foreach (var audioFile in audioFiles)
            {
                try
                {
                    File.Delete(audioFile);
                    Log.Debug("Deleted: {File}", audioFile);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete temporary file: {File}", audioFile);
                }
            }

            outputFilePath = podcastPath;

            Log.Information("");
            Log.Information("✓ Created combined podcast: {Path}", podcastPath);
            Log.Information("✓ Added {Count} chapter markers", chapters.Count);
            Log.Information("✓ Generated chapters JSON: {ChaptersPath}", chaptersPath);
            Log.Information("✓ Generated RSS feed: {FeedPath}", feedPath);
            Log.Information("");
            Log.Information("Next steps:");
            Log.Information("  1. Upload {AudioFile}, {ChaptersFile}, and feed.xml to your hosting", podcastFileName, chaptersFileName);
            Log.Information("  2. Edit feed.xml and replace {{{{BASE_URL}}}} with your host URL");
            Log.Information("  3. Subscribe in Apple Podcasts using the feed URL");
            Log.Information("");
            Log.Information("Chapters (in playback order):");
            for (int i = 0; i < chapters.Count; i++)
            {
                var chapter = chapters[i];
                var startTime = TimeSpan.FromMilliseconds(chapter.StartTimeMs);
                Log.Information("  {Index}. [{Time}] {Title}",
                    i + 1,
                    startTime.ToString(@"mm\:ss"),
                    chapter.Title.Length > 50 ? chapter.Title[..47] + "..." : chapter.Title);
            }
        }
        else
        {
            // Standard mode: Create M4B audiobook
            Log.Information("");
            Log.Information("Step 5: Creating M4B audiobook...");
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var audiobookPath = Path.Combine(outputDir, $"audiobook-{timestamp}.m4b");

            try
            {
                string createdPath;
                using (metrics.Measure("audiobook_creation"))
                {
                    createdPath = await audioProcessor.CreateAudiobookAsync(audioFiles, audiobookPath);
                }

                Log.Information("Audiobook created: {Path}", createdPath);

                var audiobookMetadata = await audioProcessor.GetMetadataAsync(createdPath);
                Log.Information("  Duration: {Duration:F1} minutes", audiobookMetadata.DurationMinutes);
                Log.Information("  File size: {Size:F2} MB", audiobookMetadata.FileSizeMB);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating audiobook");
                Log.Warning("This may be due to FFmpeg not being installed.");
                Log.Warning("Install FFmpeg: brew install ffmpeg");
                return;
            }

            // Step 6: Add chapter markers
            Log.Information("");
            Log.Information("Step 6: Adding chapter markers...");
            try
            {
                using (metrics.Measure("chapter_markers"))
                {
                    await chapterMarker.AddChaptersAsync(audiobookPath, chapters);
                }

                Log.Information("Added {Count} chapter marker(s)", chapters.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error adding chapter markers");
            }

            // Clean up newly generated MP3 files (keep existing ones untouched)
            if (result != null)
            {
                foreach (var (articleId, _) in result.SuccessfulGenerations)
                {
                    var audioFilePath = Path.Combine(outputDir, $"{articleId}.mp3");
                    try
                    {
                        File.Delete(audioFilePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to delete temporary file: {File}", audioFilePath);
                    }
                }
            }

            outputFilePath = audiobookPath;
        }

        // Final summary
        Log.Information("");
        Log.Information("========================================");
        Log.Information(options.PodcastMode ? "Podcast Created Successfully!" : "Audiobook Created Successfully!");
        Log.Information("========================================");
        Log.Information("Output: {Path}", outputFilePath);
        Log.Information("Total articles: {Total}", allArticlesForAudiobook.Count);
        Log.Information("  - Existing audio: {Existing}", articlesWithExistingAudio.Count);
        Log.Information("  - Newly generated: {New}", result?.SuccessCount ?? 0);
        if (result != null && result.FailureCount > 0)
        {
            Log.Warning("  - Failed generation: {Failed}", result.FailureCount);
        }
        Log.Information("Budget used: {Summary}", budgetService.GetSummary());
        Log.Information("");
        if (options.PodcastMode)
        {
            Log.Information("Your podcast files are ready for upload.");
        }
        else
        {
            Log.Information("You can now play the audiobook in any M4B-compatible player.");
            Log.Information("Recommended: Apple Books, VLC, or any audiobook player");
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
        var articleRepo = scopedServices.GetRequiredService<IArticleRepository>();
        var unitOfWork = scopedServices.GetRequiredService<IUnitOfWork>();
        var mp3Tagger = scopedServices.GetRequiredService<IMp3Tagger>();
        var rssFeedGenerator = scopedServices.GetRequiredService<IRssFeedGenerator>();
        var chaptersJsonGenerator = scopedServices.GetRequiredService<IChaptersJsonGenerator>();

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
            var newArticleCount = 0;
            var existingArticleCount = 0;

            foreach (var article in articleList)
            {
                // Check if article already exists in database
                var existingArticle = await articleRepo.GetByIdAsync(article.Id);

                if (existingArticle != null)
                {
                    // Use existing article from database
                    session.Articles.Add(existingArticle);
                    existingArticleCount++;
                    Log.Debug("Using existing article: {Title}", existingArticle.Title);
                }
                else
                {
                    // Add new article
                    await articleRepo.AddAsync(article);
                    session.Articles.Add(article);
                    newArticleCount++;
                    Log.Debug("Adding new article: {Title}", article.Title);
                }
            }

            await unitOfWork.SaveChangesAsync();
            Log.Information(
                "✓ Linked {Total} articles to session ({New} new, {Existing} existing)",
                articleList.Count,
                newArticleCount,
                existingArticleCount);

            // Check if user wants to skip audio generation (for testing scraping only)
            if (options.ScrapeOnly)
            {
                Log.Information("");
                Log.Information("Scrape-only mode enabled - skipping audio generation");
                Log.Information("Articles successfully scraped and saved to database");
                Log.Information("Use without --scrape-only flag to generate audio");
                return;
            }

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

            string outputFilePath;

            if (options.PodcastMode)
            {
                // Podcast mode: Create single M4A with chapters + RSS feed
                Log.Information("");
                Log.Information("Step 3: Creating combined podcast file with chapters...");

                var dateStr = DateTime.Now.ToString("yyyy-MM-dd");
                var displayDateStr = DateTime.Now.ToString("MMM dd, yyyy");
                var podcastFileName = $"{dateStr}-nyt-todays-paper.m4a";
                var chaptersFileName = $"{dateStr}-chapters.json";
                var podcastPath = Path.Combine(outputDir, podcastFileName);

                // Create combined M4A using existing AudioProcessor
                try
                {
                    string createdPath;
                    using (metrics.Measure("podcast_creation"))
                    {
                        createdPath = await audioProcessor.CreateAudiobookAsync(audioFiles, podcastPath);
                    }

                    Log.Information("✓ Combined audio created: {Path}", createdPath);

                    var podcastMetadata = await audioProcessor.GetMetadataAsync(createdPath);
                    Log.Information("  Duration: {Duration:F1} minutes", podcastMetadata.DurationMinutes);
                    Log.Information("  File size: {Size:F2} MB", podcastMetadata.FileSizeMB);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "✗ Error creating combined podcast file");
                    Log.Warning("This may be due to FFmpeg not being installed.");
                    Log.Warning("Install FFmpeg: brew install ffmpeg");
                    return;
                }

                // Add chapter markers to M4A
                Log.Information("");
                Log.Information("Step 4: Adding chapter markers...");
                try
                {
                    using (metrics.Measure("chapter_markers"))
                    {
                        await chapterMarker.AddChaptersAsync(podcastPath, chapters);
                    }

                    Log.Information("✓ Added {Count} chapter marker(s)", chapters.Count);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "✗ Error adding chapter markers");
                }

                // Generate chapters.json (Podcasting 2.0 format)
                Log.Information("");
                Log.Information("Step 5: Generating chapters JSON...");
                var chaptersPath = Path.Combine(outputDir, chaptersFileName);
                await chaptersJsonGenerator.SaveJsonAsync(chapters, chaptersPath);
                Log.Information("✓ Chapters JSON saved: {Path}", chaptersPath);

                // Generate RSS feed with single combined episode
                Log.Information("");
                Log.Information("Step 6: Generating RSS feed...");

                var podcastFileInfo = new FileInfo(podcastPath);
                var podcastAudioMetadata = await audioProcessor.GetMetadataAsync(podcastPath);

                var combinedEpisode = new CombinedPodcastEpisode(
                    Title: $"NYT Today's Paper - {displayDateStr}",
                    Description: "Daily articles from The New York Times Today's Paper",
                    AudioFileName: podcastFileName,
                    ChaptersFileName: chaptersFileName,
                    PubDate: DateTime.UtcNow,
                    FileSizeBytes: podcastFileInfo.Length,
                    DurationSeconds: (int)(podcastAudioMetadata.DurationMs / 1000),
                    Guid: $"nyt-{dateStr}",
                    ChapterTitles: chapters.Select(c => c.Title).ToList());

                var podcastInfo = new PodcastInfo(
                    Title: "NYT Today's Paper",
                    Description: "Daily articles from The New York Times Today's Paper",
                    Author: "The New York Times");

                var feedPath = Path.Combine(outputDir, "feed.xml");
                await rssFeedGenerator.SaveCombinedFeedAsync(combinedEpisode, podcastInfo, feedPath);

                // Clean up individual MP3 files
                Log.Information("");
                Log.Information("Cleaning up temporary files...");
                foreach (var audioFile in audioFiles)
                {
                    try
                    {
                        File.Delete(audioFile);
                        Log.Debug("Deleted: {File}", audioFile);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to delete temporary file: {File}", audioFile);
                    }
                }

                outputFilePath = podcastPath;

                Log.Information("");
                Log.Information("✓ Created combined podcast: {Path}", podcastPath);
                Log.Information("✓ Added {Count} chapter markers", chapters.Count);
                Log.Information("✓ Generated chapters JSON: {ChaptersPath}", chaptersPath);
                Log.Information("✓ Generated RSS feed: {FeedPath}", feedPath);
                Log.Information("");
                Log.Information("Next steps:");
                Log.Information("  1. Upload {AudioFile}, {ChaptersFile}, and feed.xml to your hosting", podcastFileName, chaptersFileName);
                Log.Information("  2. Edit feed.xml and replace {{{{BASE_URL}}}} with your host URL");
                Log.Information("  3. Subscribe in Apple Podcasts using the feed URL");
                Log.Information("");
                Log.Information("Chapters (in playback order):");
                for (int i = 0; i < chapters.Count; i++)
                {
                    var chapter = chapters[i];
                    var startTime = TimeSpan.FromMilliseconds(chapter.StartTimeMs);
                    Log.Information("  {Index}. [{Time}] {Title}",
                        i + 1,
                        startTime.ToString(@"mm\:ss"),
                        chapter.Title.Length > 50 ? chapter.Title[..47] + "..." : chapter.Title);
                }
            }
            else
            {
                // Standard mode: Create M4B audiobook
                Log.Information("");
                Log.Information("Step 3: Creating M4B audiobook...");
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var audiobookPath = Path.Combine(outputDir, $"audiobook-{timestamp}.m4b");

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

                outputFilePath = audiobookPath;
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

            if (summary.Counters.Any())
            {
                Log.Information("");
                Log.Information("Counters:");
                foreach (var (counter, value) in summary.Counters.OrderBy(x => x.Key))
                {
                    Log.Information("  {Counter}: {Value}", counter, value);
                }
            }

            // Final summary
            Log.Information("");
            Log.Information("========================================");
            Log.Information(options.PodcastMode ? "Podcast Created Successfully!" : "Audiobook Created Successfully!");
            Log.Information("========================================");
            Log.Information("Output: {Path}", outputFilePath);
            Log.Information("Budget used: {Summary}", budgetService.GetSummary());
            Log.Information("");
            if (options.PodcastMode)
            {
                Log.Information("Your podcast files are ready for upload.");
            }
            else
            {
                Log.Information("You can now play the audiobook in any M4B-compatible player.");
                Log.Information("Recommended: Apple Books, VLC, or any audiobook player");
            }

            // Update session on successful completion
            session.Status = ScrapingStatus.Completed;
            session.CompletedAt = DateTime.UtcNow;
            session.OutputFilePath = outputFilePath;
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
