// <copyright file="Program.cs" company="Wire Copy">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>

using CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Bookmarks;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Collections;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Scheduling;
using WireCopy.Persistence;
using Serilog;
using Serilog.Events;

namespace WireCopy.API;

public class Program
{
    private const string DefaultUrl = "https://news.ycombinator.com";

    public static async Task<int> Main(string[] args)
    {
        // No arguments: launch browse mode with default URL
        if (args.Length == 0)
        {
            return await RunBrowseAsync(new BrowseOptions());
        }

        // Handle "browse" verb and bare URL explicitly, since CommandLineParser
        // with a single verb type treats the verb name as a positional value.
        if (args.Length >= 1 && args[0].Equals("browse", StringComparison.OrdinalIgnoreCase))
        {
            var url = args.Length > 1 ? args[1] : null;
            return await RunBrowseAsync(new BrowseOptions { Url = url });
        }

        // workspace-frpl.18 (B14): unattended section-resolution debug verb (no TUI;
        // the browser itself is headful as always). Loads a URL via the same
        // unattended path scheduled runs use and prints the resolution
        // (status, match tier, items, sample link selectors) as JSON so the durability
        // gate can assert selector-tier resolution, render parity, and drift-skip.
        if (args.Length >= 1 && args[0].Equals("resolve-section", StringComparison.OrdinalIgnoreCase))
        {
            return await RunResolveSectionAsync(args);
        }

        // workspace-frpl.17 (B13): run a saved recipe to completion (the SAME gate +
        // RecipeRunPipeline + orchestrator + real publish path the scheduler/run-now use)
        // and print the finalized run's feed URL — a reliable, non-interactive e2e entry.
        if (args.Length >= 1 && args[0].Equals("run-recipe", StringComparison.OrdinalIgnoreCase))
        {
            return await RunRecipeAsync(args);
        }

        // Single arg that looks like a URL: shortcut for browse <url>
        if (args.Length == 1 && !args[0].StartsWith('-') &&
            Uri.TryCreate(args[0], UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return await RunBrowseAsync(new BrowseOptions { Url = args[0] });
        }

        // Fall through to help/error for unrecognized arguments
        var parser = new Parser(with => with.HelpWriter = Console.Error);
        var result = parser.ParseArguments<BrowseOptions>(args);

        return await result.MapResult(
            async (BrowseOptions opts) => await RunBrowseAsync(opts),
            errs => Task.FromResult(1));
    }

    private static async Task<int> RunRecipeAsync(string[] args)
    {
        Environment.SetEnvironmentVariable("npm_config_loglevel", "silent");
        var recipeName = args.Length > 1 ? args[1] : null;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.File("logs/wirecopy-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        var host = CreateBrowseHostBuilder().Build();
        try
        {
            await host.StartAsync();
            using (var scope = host.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().InitializeDatabaseAsync();
            }

            host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                .CreateLogger("BrowserVisibility")
                .LogInformation("{Resolution}", host.Services.GetRequiredService<IOptions<BrowserConfiguration>>().Value.DescribeVisibilityResolution());

            var store = host.Services.GetRequiredService<WireCopy.Application.Interfaces.Scheduling.IScheduleStore>();
            var recipes = await store.GetAllAsync();
            var recipe = recipeName is null
                ? recipes.FirstOrDefault()
                : recipes.FirstOrDefault(r => string.Equals(r.Name, recipeName, StringComparison.OrdinalIgnoreCase));
            if (recipe is null)
            {
                Console.WriteLine("RUN_RESULT:" + System.Text.Json.JsonSerializer.Serialize(new { status = "NoRecipe", recipeName }));
                return 2;
            }

            var runNow = host.Services.GetRequiredService<WireCopy.Application.Interfaces.Scheduling.IScheduleRunNow>();
            await runNow.RunAsync(recipe);

            var repo = host.Services.GetRequiredService<WireCopy.Application.Interfaces.Scheduling.IScheduledRunRepository>();
            var finished = (await repo.GetUnacknowledgedFinishedRunsAsync())
                .Where(r => r.RecipeId == recipe.Id)
                .OrderByDescending(r => r.StartedAtUtc)
                .FirstOrDefault();
            Console.WriteLine("RUN_RESULT:" + System.Text.Json.JsonSerializer.Serialize(new
            {
                status = finished?.Status.ToString() ?? "Unknown",
                feedUrl = finished?.TargetFeedUrl,
                localPath = finished?.TargetLocalPath,
                itemCount = finished?.ItemCount ?? 0,
                error = finished?.ErrorMessage,
            }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("RUN_RESULT:" + System.Text.Json.JsonSerializer.Serialize(new { status = "Error", error = ex.Message }));
            return 1;
        }
        finally
        {
            try
            {
                await host.StopAsync();
            }
            catch (Exception)
            {
                // shutting down
            }

            host.Dispose();
        }
    }

    private static async Task<int> RunResolveSectionAsync(string[] args)
    {
        Environment.SetEnvironmentVariable("npm_config_loglevel", "silent");
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: resolve-section <url> <sectionName> [configUrlPattern]");
            return 2;
        }

        var url = args[1];
        var sectionName = args[2];
        var configUrlPattern = args.Length > 3 ? args[3] : string.Empty;

        // Information level so the durability gate can grep the log for any stray
        // analyzer invocation during a (config-cached) unattended resolution.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.File("logs/wirecopy-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        var host = CreateBrowseHostBuilder().Build();
        try
        {
            await host.StartAsync();
            using (var scope = host.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().InitializeDatabaseAsync();
            }

            host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                .CreateLogger("BrowserVisibility")
                .LogInformation("{Resolution}", host.Services.GetRequiredService<IOptions<BrowserConfiguration>>().Value.DescribeVisibilityResolution());

            var loader = host.Services.GetRequiredService<WireCopy.Application.Interfaces.Scheduling.IUnattendedSectionLoader>();
            var resolver = host.Services.GetRequiredService<WireCopy.Application.Interfaces.Scheduling.ISectionResolver>();

            var load = await loader.LoadLinksAndConfigAsync(url);
            object payload;
            if (load.Outcome != WireCopy.Application.DTOs.Scheduling.LoadOutcome.Ok || load.Config is null)
            {
                payload = new
                {
                    loadOutcome = load.Outcome.ToString(),
                    status = load.Config is null ? "NoConfig" : "LoadNotOk",
                    hasConfig = load.Config is not null,
                };
            }
            else
            {
                var domain = new Uri(url).Host;
                var step = WireCopy.Domain.ValueObjects.Scheduling.RecipeStep.Create(
                    url, domain, configUrlPattern, sectionName, required: true);
                var res = resolver.Resolve(load.Config, load.Links, step);
                var sample = load.Links
                    .Where(l => l.Type == WireCopy.Domain.Enums.Browser.LinkType.Content && !l.IsGroupHeader)
                    .Take(6)
                    .Select(l => new { url = l.Url, parentSelector = l.ParentSelector, sectionTitle = l.SectionTitle })
                    .ToList();
                payload = new
                {
                    loadOutcome = "Ok",
                    status = res.Status.ToString(),
                    tier = res.Tier?.ToString(),
                    matchCount = res.MatchCount,
                    items = res.Items.Select(i => new { url = i.Url, title = i.Title }).ToList(),
                    diagnostic = res.Diagnostic,
                    sampleContentLinks = sample,
                };
            }

            Console.WriteLine("RESOLVE_JSON:" + System.Text.Json.JsonSerializer.Serialize(payload));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("RESOLVE_JSON:" + System.Text.Json.JsonSerializer.Serialize(new { status = "Error", error = ex.Message }));
            return 1;
        }
        finally
        {
            try
            {
                await host.StopAsync();
            }
            catch (Exception)
            {
                // shutting down
            }

            host.Dispose();
        }
    }

    private static async Task<int> RunBrowseAsync(BrowseOptions options)
    {
        // workspace-nk8a: silence Playwright's polite "BEWARE: your OS is not officially
        // supported" log line that the bundled Node driver writes via console.log on
        // unsupported platforms (ARM64 Linux, etc). The TUI uses absolute cursor
        // positioning, so any unsolicited stdout from the driver corrupts the frame.
        // The Node driver's logPolitely() suppresses output when npm_config_loglevel
        // is silent/error/warn — no functional effect on real Playwright errors,
        // which surface via exceptions.
        Environment.SetEnvironmentVariable("npm_config_loglevel", "silent");

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

        // Reconfigure Serilog to file-only for browse mode (suppress console output for TUI).
        // workspace-v3pz: also fan out to the in-memory ring so `:logs` can show them.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.File("logs/wirecopy-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .WriteTo.Sink(LogBuffer)
            .CreateLogger();

        // Pass null when no URL provided to trigger the launcher home screen
        var url = string.IsNullOrWhiteSpace(options.Url) ? null : options.Url;

        // Create a minimal host for browser services
        var host = CreateBrowseHostBuilder().Build();

        try
        {
            // Start the host so registered IHostedService implementations
            // (cookie warmup, podcast output-folder purge) actually run.
            await host.StartAsync();

            // Initialize database once at startup
            using (var scope = host.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await dbContext.InitializeDatabaseAsync();
            }

            // workspace-9k27.6: if a previous session crashed while the sidecar had
            // tiled the terminal, put the user's terminal window back (macOS only;
            // instant no-op when no recovery record exists).
            await WireCopy.Infrastructure.Browser.TerminalTileRecovery.RecoverAtStartupAsync(
                host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                    .CreateLogger("TerminalTileRecovery"));

            // workspace-9k27.7: capture the terminal's identity NOW, while it is
            // guaranteed to be the frontmost app — the old first-browser-launch
            // capture recorded whatever app the user had switched to by then and
            // cached it forever, sending focus to the wrong app on every refocus.
            await host.Services.GetRequiredService<IBrowserSessionControl>().CaptureTerminalIdentityAsync();

            // No startup warmup: the browser is always headful (never-headless law), and a
            // headed warmup here would pop a visible Chrome window before the user navigates
            // anywhere. The browser launches lazily on first use instead (workspace-9k27.10 —
            // this preserves the pre-cleanup behavior, where the old (always-false) warmup gate
            // never fired).
            host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                .CreateLogger("BrowserVisibility")
                .LogInformation("{Resolution}", host.Services.GetRequiredService<IOptions<BrowserConfiguration>>().Value.DescribeVisibilityResolution());

            var browser = host.Services.GetRequiredService<IBrowserService>();
            await browser.RunAsync(url);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in browser mode");
            return 1;
        }
        finally
        {
            try
            {
                await host.StopAsync();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error stopping host during cleanup");
            }

            host.Dispose();
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// workspace-frpl.19 (B15) — host shutdown grace for the in-process scheduler.
    /// On Ctrl+C / host StopAsync, the BackgroundService stoppingToken is cancelled
    /// and the host waits at most this long for <see cref="WireCopy.Infrastructure.Scheduling.SchedulerHostedService"/>
    /// to unwind. That is enough for an in-flight scheduled run to observe the linked
    /// token, abort TTS/publish, finalize its ScheduledRun row to Interrupted (NOT
    /// leave it Running for the next-startup orphan sweep), and release the B0
    /// generation gate. It is deliberately a CANCELLATION budget, not a
    /// run-to-completion budget (a full generation can take minutes; we cancel, we
    /// don't wait it out). The startup-only OutputFolderPurgeStartupService never
    /// runs on shutdown, so a just-finished artifact is never purged out from under
    /// a published feed on the way down.
    /// </summary>
    internal static readonly TimeSpan SchedulerShutdownTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// workspace-v3pz: the single in-memory log ring shared between Serilog
    /// (<c>WriteTo.Sink</c>) and DI (<see cref="ILogBuffer"/>), so the in-app
    /// <c>:logs</c> viewer shows exactly what was logged this session.
    /// </summary>
    internal static readonly WireCopy.Infrastructure.Logging.RingBufferLogSink LogBuffer = new(2000);

    internal static IHostBuilder CreateBrowseHostBuilder() =>
        Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureAppConfiguration((context, config) =>
            {
                var basePath = AppContext.BaseDirectory;
                config.SetBasePath(basePath);
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

                // Re-add environment variables AFTER the JSON file so env vars win.
                // Host.CreateDefaultBuilder adds env vars before our ConfigureAppConfiguration
                // callback runs, so without this the JSON file would override env vars
                // (e.g. Browser__Headless=true would be ignored).
                config.AddEnvironmentVariables();
            })
            .ConfigureServices((context, services) =>
            {
                services.AddTerminalBrowser();
                services.AddSingleton<ILogBuffer>(LogBuffer); // workspace-v3pz
                services.AddPersistence();
                services.AddCollections();
                services.AddBookmarks();
                services.AddPodcast();
                services.AddScheduling();

                // workspace-frpl.19 (B15): give the scheduler time to cancel an
                // in-flight run gracefully (finalize Interrupted + release the gate)
                // on host shutdown instead of being abandoned mid-generation.
                services.Configure<HostOptions>(o => o.ShutdownTimeout = SchedulerShutdownTimeout);
            });
}
