using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Infrastructure;
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
            Log.Information("Starting NYT Audio Scraper");

            var host = CreateHostBuilder(args).Build();

            // Demonstrate DI resolution and logging
            await RunApplicationAsync(host.Services);

            Log.Information("Application completed successfully");
            return 0;
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

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                // Register infrastructure services
                services.AddInfrastructure(context.Configuration);
            });

    private static async Task RunApplicationAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("Resolving services from DI container...");

        // Resolve all services to verify DI is working
        var scraperService = services.GetRequiredService<IScraperService>();
        var audioGenerator = services.GetRequiredService<IAudioGenerator>();
        var audioProcessor = services.GetRequiredService<IAudioProcessor>();
        var chapterMarker = services.GetRequiredService<IChapterMarker>();
        var fileStorage = services.GetRequiredService<IFileStorage>();

        logger.LogInformation("All services resolved successfully!");
        logger.LogInformation("ScraperService: {Service}", scraperService.GetType().Name);
        logger.LogInformation("AudioGenerator: {Service}", audioGenerator.GetType().Name);
        logger.LogInformation("AudioProcessor: {Service}", audioProcessor.GetType().Name);
        logger.LogInformation("ChapterMarker: {Service}", chapterMarker.GetType().Name);
        logger.LogInformation("FileStorage: {Service}", fileStorage.GetType().Name);

        // Test services
        logger.LogInformation("Testing service methods...");
        await scraperService.AuthenticateAsync();
        var articles = await scraperService.ScrapeArticlesAsync(maxArticles: 5);
        logger.LogInformation("Scraper returned {Count} articles", articles.Count());

        var cost = audioGenerator.EstimateCost("This is a test text for cost estimation.");
        logger.LogInformation("Estimated audio cost: ${Cost:F4}", cost);

        var outputDir = fileStorage.GetOutputDirectory();
        logger.LogInformation("Output directory: {Directory}", outputDir);

        logger.LogInformation("All tests passed! Application scaffolding is working correctly.");
    }

    private static IConfiguration GetConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>(optional: true)
            .Build();
    }
}
