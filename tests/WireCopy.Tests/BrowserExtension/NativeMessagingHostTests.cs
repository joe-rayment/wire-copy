// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Infrastructure.BrowserExtension;
using Xunit;

namespace WireCopy.Tests.BrowserExtension;

/// <summary>
/// The native-messaging host's message loop (workspace-m7aml) over in-memory pipes, with a
/// fake Reading List so nothing touches a database: what each request type is answered with,
/// that a bad message never ends the loop, and what is recorded in connections.json.
/// </summary>
[Trait("Category", "Unit")]
public sealed class NativeMessagingHostTests : IDisposable
{
    private const string GenericSaveError = "Wire Copy couldn't save this page.";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "wirecopy-host-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _connectionsPath;
    private readonly FakeCapture _capture = new();

    public NativeMessagingHostTests()
    {
        _connectionsPath = Path.Combine(_tempDir, "browser-extension", "connections.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // Guards:   A hello, a save and an unreadable message each get their own correctly shaped reply, in order, and the loop returns when the input ends.
    // Fails if: A reply is dropped, reordered or mis-shaped (wrong type/ok/fields), a garbage message ends or hangs the loop, or RunAsync never returns on end of input.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Host_AnswersHelloSaveAndGarbageInOrderThenReturns
    [Fact]
    public async Task Host_AnswersHelloSaveAndGarbageInOrderThenReturns()
    {
        _capture.Count = 7;
        _capture.Result = new ReadingListCaptureResult("Saved headline", 321, "readability", AlreadySaved: true);

        var input = await FramesAsync(
            """{"type":"hello","extensionVersion":"1.2.3"}""",
            """{"type":"save","url":"https://news.example.com/story","title":"Tab title","html":"<html>page</html>","extensionVersion":"1.2.3"}""",
            "this is {{ not json");

        var replies = await RunAsync(input, "vivaldi");

        replies.Should().HaveCount(3);

        var hello = replies[0];
        hello.GetProperty("ok").GetBoolean().Should().BeTrue();
        hello.GetProperty("type").GetString().Should().Be("hello");
        hello.GetProperty("app").GetString().Should().Be("Wire Copy");
        hello.GetProperty("appVersion").GetString().Should().NotBeNullOrWhiteSpace();
        hello.GetProperty("readingListCount").GetInt32().Should().Be(7);

        var save = replies[1];
        save.GetProperty("ok").GetBoolean().Should().BeTrue();
        save.GetProperty("type").GetString().Should().Be("save");
        save.GetProperty("title").GetString().Should().Be("Saved headline");
        save.GetProperty("words").GetInt32().Should().Be(321);
        save.GetProperty("source").GetString().Should().Be("readability");
        save.GetProperty("alreadySaved").GetBoolean().Should().BeTrue();

        var garbage = replies[2];
        garbage.GetProperty("ok").GetBoolean().Should().BeFalse();
        garbage.GetProperty("type").GetString().Should().Be("unknown");
        garbage.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();

        _capture.Saves.Should().ContainSingle().Which.Should().Be(
            ("https://news.example.com/story", "Tab title", "<html>page</html>"));
    }

    // Guards:   connections.json records the browser that launched the host, and the extension version it reported, once it says hello or saves.
    // Fails if: The host stops calling BrowserConnections.Record, records the wrong browser id, or drops the extension version, so Settings never shows the browser as connected.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Host_RecordsTheBrowserAndExtensionVersion
    [Fact]
    public async Task Host_RecordsTheBrowserAndExtensionVersion()
    {
        File.Exists(_connectionsPath).Should().BeFalse();

        var input = await FramesAsync("""{"type":"hello","extensionVersion":"4.5.6"}""");
        await RunAsync(input, "brave");

        var recorded = BrowserConnections.Read(_connectionsPath);
        recorded.Should().ContainKey("brave");
        recorded["brave"].ExtensionVersion.Should().Be("4.5.6");
        recorded["brave"].LastSeenUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    // Guards:   A bad URL's message reaches the user as written, any other failure gives only the fixed sentence (no internals), and the next message is still answered.
    // Fails if: An ArgumentException message is replaced by the generic text, an unexpected exception's details leak into the reply, or either error stops the loop.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Host_ReportsSaveErrorsAndKeepsAnswering
    [Fact]
    public async Task Host_ReportsSaveErrorsAndKeepsAnswering()
    {
        _capture.OnSave = url => url switch
        {
            "ftp://nope" => throw new ArgumentException("That isn't a web address Wire Copy can save."),
            "https://boom.example.com/" => throw new InvalidOperationException("secret database detail"),
            _ => new ReadingListCaptureResult("Fine", 10, "none", AlreadySaved: false),
        };

        var input = await FramesAsync(
            """{"type":"save","url":"ftp://nope"}""",
            """{"type":"save","url":"https://boom.example.com/"}""",
            """{"type":"hello"}""");

        var replies = await RunAsync(input, "chrome");

        replies.Should().HaveCount(3);

        replies[0].GetProperty("ok").GetBoolean().Should().BeFalse();
        replies[0].GetProperty("type").GetString().Should().Be("save");
        replies[0].GetProperty("error").GetString().Should().Be("That isn't a web address Wire Copy can save.");

        replies[1].GetProperty("ok").GetBoolean().Should().BeFalse();
        replies[1].GetProperty("type").GetString().Should().Be("save");
        replies[1].GetProperty("error").GetString().Should().Be(GenericSaveError);
        replies[1].GetRawText().Should().NotContain("secret");

        replies[2].GetProperty("ok").GetBoolean().Should().BeTrue();
        replies[2].GetProperty("type").GetString().Should().Be("hello");
    }

    // Guards:   A well-formed message of a type the host doesn't know gets a "didn't understand" error carrying that type, and the loop carries on.
    // Fails if: An unknown type is silently ignored (the extension waits forever) or throws out of the loop.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Host_RejectsAnUnknownMessageTypeAndKeepsAnswering
    [Fact]
    public async Task Host_RejectsAnUnknownMessageTypeAndKeepsAnswering()
    {
        var input = await FramesAsync("""{"type":"frobnicate"}""", """{"type":"hello"}""");

        var replies = await RunAsync(input, "chrome");

        replies.Should().HaveCount(2);
        replies[0].GetProperty("ok").GetBoolean().Should().BeFalse();
        replies[0].GetProperty("type").GetString().Should().Be("frobnicate");
        replies[0].GetProperty("error").GetString().Should().Contain("didn't understand");
        replies[1].GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    private static async Task<MemoryStream> FramesAsync(params string[] messages)
    {
        var stream = new MemoryStream();
        foreach (var message in messages)
        {
            await NativeMessagingFraming.WriteAsync(stream, message, Ct);
        }

        stream.Position = 0;
        return stream;
    }

    private async Task<List<JsonElement>> RunAsync(Stream input, string browserId)
    {
        var host = new NativeMessagingHost(_capture, _connectionsPath, browserId, NullLogger.Instance);
        using var output = new MemoryStream();

        var run = host.RunAsync(input, output, Ct);
        (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(run, "the loop must return when the input ends");
        await run;

        output.Position = 0;
        var replies = new List<JsonElement>();
        while (await NativeMessagingFraming.ReadAsync(output, Ct) is { } json)
        {
            replies.Add(JsonDocument.Parse(json).RootElement.Clone());
        }

        return replies;
    }

    private sealed class FakeCapture : IReadingListCapture
    {
        public int Count { get; set; }

        public ReadingListCaptureResult Result { get; set; } = new("Title", 1, "none", AlreadySaved: false);

        public Func<string, ReadingListCaptureResult>? OnSave { get; set; }

        public List<(string Url, string? Title, string? Html)> Saves { get; } = [];

        public Task<ReadingListCaptureResult> SaveAsync(string url, string? pageTitle, string? html, CancellationToken cancellationToken)
        {
            Saves.Add((url, pageTitle, html));
            return Task.FromResult(OnSave is null ? Result : OnSave(url));
        }

        public Task<int> ReadingListCountAsync(CancellationToken cancellationToken) => Task.FromResult(Count);
    }
}
