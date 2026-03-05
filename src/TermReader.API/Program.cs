// <copyright file="Program.cs" company="TermReader">
// Educational and personal use only.
// </copyright>

using CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces.Browser;
using TermReader.Infrastructure.Browser;
using Serilog;
using Serilog.Events;

namespace TermReader.API;

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

    private static async Task<int> RunBrowseAsync(BrowseOptions options)
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

        // Reconfigure Serilog to file-only for browse mode (suppress console output for TUI)
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.File("logs/termreader-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        // Pass null when no URL provided to trigger the launcher home screen
        var url = string.IsNullOrWhiteSpace(options.Url) ? null : options.Url;

        // Create a minimal host for browser services
        var host = CreateBrowseHostBuilder().Build();

        try
        {
            // Eagerly warm up the browser session in the background so the first
            // browser-fallback page load avoids the cold-start penalty.
            var session = host.Services.GetRequiredService<IBrowserSession>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await session.WarmUpAsync();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Browser warmup failed (non-fatal)");
                }
            });

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

    private static IHostBuilder CreateBrowseHostBuilder() =>
        Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureLogging(logging => logging.ClearProviders())
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
}
