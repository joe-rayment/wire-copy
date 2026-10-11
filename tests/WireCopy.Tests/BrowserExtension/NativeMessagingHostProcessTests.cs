// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using WireCopy.Infrastructure.BrowserExtension;
using Xunit;

namespace WireCopy.Tests.BrowserExtension;

/// <summary>
/// The native-messaging host as a real process (workspace-m7aml): <c>WireCopy.API.dll</c> is
/// started exactly as a browser starts it (extension origin as the only argument, framed JSON
/// on stdin/stdout) against a throwaway data folder, so the wiring that unit tests fake —
/// argument dispatch, DI, the real database, a clean stdout — is exercised for real.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NativeMessagingHostProcessTests
{
    private const string ExtensionOrigin = "chrome-extension://cmidfakmpcnmaohapmcoigenlcjlahim/";
    private const string ArticleUrl = "https://news.example.com/2026/10/11/harbour-lights-return";
    private const string ArticleHeadline = "Harbour lights return";

    private static readonly string[] Paragraphs =
    [
        "After nearly a decade in the dark, the string of lamps along the old harbour wall was switched on again last night, and several hundred residents gathered on the quay to watch the glow spread slowly across the water.",
        "The restoration was paid for by a fund built from small donations, with local schools, fishing families and a handful of retired engineers all contributing money, time and a surprising amount of patient ladder work over the summer.",
        "Councillors said the lamps had been removed after storm damage in the early part of the last decade, and that the cost of replacing them kept slipping down the list of priorities until the community stepped in.",
        "Each lantern now uses a low-energy bulb that is shielded from above, so the light falls on the stonework and the water rather than into the sky, which keeps the harbour friendly to the seabirds that nest nearby.",
        "Fishermen on the quay said the lamps matter for more than looks, because the pale light along the wall helps boats find the narrow harbour mouth in fog, something the modern navigation screens do not fully replace.",
        "Shopkeepers along the front reported a brisk evening, with the cafe selling out of hot chocolate by eight and the chip shop staying open an hour later than usual to serve the lingering crowd that had gathered.",
        "A short ceremony included a reading by the youngest volunteer, a speech from the harbourmaster and a minute of quiet for the lamplighter whose family had tended the original lights for three generations before they went dark.",
        "The harbour committee plans to keep the lamps lit every evening until midnight through the winter, and has already begun raising money for new benches so that visitors have somewhere to sit and enjoy the view.",
    ];

    // Guards:   The real host process, started the way a browser starts it, answers a framed hello and a framed save, prints nothing else on stdout, exits 0, and the page really lands in the database and connections.json.
    // Fails if: Anything but protocol frames reaches stdout (a log line, a banner), the host hangs or crashes on end of input, the native-messaging-host argument is no longer dispatched, or the save does not reach the database.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter NativeMessagingHostProcess_SavesAPageOverRealStdio
    [SkippableFact]
    public async Task NativeMessagingHostProcess_SavesAPageOverRealStdio()
    {
        var root = FindRepoRoot();
        Skip.If(root is null, "Could not find WireCopy.sln above the test binaries.");

        var dotnetDir = Path.Combine(root!, ".dotnet", RuntimeIdentifierFolder());
        var dotnet = Path.Combine(dotnetDir, "dotnet");
        Skip.IfNot(File.Exists(dotnet), $"The vendored SDK is not at {dotnet}; run from a checkout that has .dotnet/.");

        var apiDll = Path.Combine(root!, "src", "WireCopy.API", "bin", "Debug", "net10.0", "WireCopy.API.dll");
        Skip.IfNot(File.Exists(apiDll), $"{apiDll} is missing; build it first: ./dotnet build src/WireCopy.API -c Debug");

        var dataHome = Path.Combine(Path.GetTempPath(), "wirecopy-host-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataHome);
        try
        {
            var (stdout, stderr, exitCode) = await RunHostAsync(dotnet, dotnetDir, apiDll, dataHome);

            // Exactly two frames, and nothing else on stdout.
            using var replies = new MemoryStream(stdout);
            var helloJson = await NativeMessagingFraming.ReadAsync(replies, CancellationToken.None);
            var saveJson = await NativeMessagingFraming.ReadAsync(replies, CancellationToken.None);
            replies.Position.Should().Be(replies.Length, $"stdout must hold exactly two frames; stderr was: {stderr}");
            helloJson.Should().NotBeNull();
            saveJson.Should().NotBeNull();

            var hello = JsonDocument.Parse(helloJson!).RootElement;
            hello.GetProperty("ok").GetBoolean().Should().BeTrue($"hello reply was {helloJson}; stderr: {stderr}");
            hello.GetProperty("type").GetString().Should().Be("hello");
            hello.GetProperty("readingListCount").GetInt32().Should().Be(0);

            var save = JsonDocument.Parse(saveJson!).RootElement;
            save.GetProperty("ok").GetBoolean().Should().BeTrue($"save reply was {saveJson}; stderr: {stderr}");
            save.GetProperty("type").GetString().Should().Be("save");
            save.GetProperty("title").GetString().Should().Be(ArticleHeadline);
            save.GetProperty("source").GetString().Should().Be("readability");
            save.GetProperty("words").GetInt32().Should().BeGreaterThan(150);

            exitCode.Should().Be(0, $"the host should exit cleanly when stdin closes; stderr: {stderr}");

            // The page is really on the Reading List in the host's own database.
            var dbPath = Path.Combine(dataHome, "WireCopy", "wirecopy.db");
            File.Exists(dbPath).Should().BeTrue($"expected the host to create {dbPath}");
            CountItemsWithUrl(dbPath, ArticleUrl).Should().Be(1);

            // And the connection was recorded for the Settings card.
            var connectionsPath = Path.Combine(dataHome, "WireCopy", "browser-extension", "connections.json");
            File.Exists(connectionsPath).Should().BeTrue($"expected the host to write {connectionsPath}");
            BrowserConnections.Read(connectionsPath).Should().HaveCount(1);
        }
        finally
        {
            Directory.Delete(dataHome, recursive: true);
        }
    }

    private static async Task<(byte[] Stdout, string Stderr, int ExitCode)> RunHostAsync(
        string dotnet, string dotnetDir, string apiDll, string dataHome)
    {
        var startInfo = new ProcessStartInfo(dotnet)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(apiDll);
        startInfo.ArgumentList.Add(ExtensionOrigin);
        startInfo.Environment["XDG_DATA_HOME"] = dataHome;
        startInfo.Environment["DOTNET_ROOT"] = dotnetDir;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the host process.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using var stdoutBuffer = new MemoryStream();
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer, timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            var stdin = process.StandardInput.BaseStream;
            await NativeMessagingFraming.WriteAsync(
                stdin, JsonSerializer.Serialize(new { type = "hello", extensionVersion = "0.0.1-test" }), timeout.Token);
            await NativeMessagingFraming.WriteAsync(
                stdin,
                JsonSerializer.Serialize(new { type = "save", url = ArticleUrl, title = "Harbour lights (tab)", html = ArticleHtml(), extensionVersion = "0.0.1-test" }),
                timeout.Token);
            stdin.Close();

            await process.WaitForExitAsync(timeout.Token);
            await stdoutTask;
            return (stdoutBuffer.ToArray(), await stderrTask, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The native-messaging host did not exit within 30 seconds of stdin closing.");
        }
    }

    private static string ArticleHtml()
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<title>Harbour lights (tab)</title>");
        html.Append("<meta property=\"og:type\" content=\"article\">");
        html.Append("<meta property=\"og:title\" content=\"Harbour lights return\">");
        html.Append("</head><body><nav><a href=\"/\">Home</a> <a href=\"/news\">News</a></nav>");
        html.Append("<article><h1>").Append(ArticleHeadline).Append("</h1>");
        foreach (var paragraph in Paragraphs)
        {
            html.Append("<p>").Append(paragraph).Append("</p>");
        }

        html.Append("</article><footer>Copyright Example News</footer></body></html>");
        return html.ToString();
    }

    private static int CountItemsWithUrl(string dbPath, string url)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM CollectionItems WHERE Url = $url";
        command.Parameters.AddWithValue("$url", url);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WireCopy.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }

    private static string RuntimeIdentifierFolder()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
        return os + "-" + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
    }
}
