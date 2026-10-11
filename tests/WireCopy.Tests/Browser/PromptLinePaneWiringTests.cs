// Licensed under the MIT License. See LICENSE in the repository root.

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Shell;
using WireCopy.Infrastructure.Browser.UI;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-up3zx end-to-end wiring: drives the REAL <see cref="TerminalPageRenderer"/>
/// over a live <see cref="ShellChannel"/> and a genuine Unix-domain socket, and asserts
/// the <c>promptLine</c> field of the actual <c>launcherState</c> / <c>linkTreeState</c>
/// messages the shell receives, as the ':' / '/' prompt opens, is edited and closes.
/// </summary>
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public sealed class PromptLinePaneWiringTests : IAsyncLifetime
{
    private readonly string _socketPath = Path.Combine(
        Path.GetTempPath(), $"wc-prompt-{Guid.NewGuid():N}.sock");

    private RecordingShellServer? _server;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        File.Delete(_socketPath);
    }

    // Guards:   typing at the ':' prompt over the launcher pane reaches the shell as launcherState.promptLine
    //           ({prefix, buffer, cursor}) while the pane stays visible, and closing the prompt sends promptLine null.
    // Fails if: SetPromptLineAsync stops re-sending the launcher state, drops the promptLine field, or never clears it.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PromptLinePaneWiringTests.Launcher_PromptLine_IsMirroredThenCleared"
    [Fact]
    public async Task Launcher_PromptLine_IsMirroredThenCleared()
    {
        _server = await RecordingShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);
        Assert.NotNull(channel);
        var renderer = CreateRenderer(channel);

        var bookmarks = new List<Bookmark>
        {
            Bookmark.Create("Alpha", "https://alpha.example", 0),
            Bookmark.Create("Bravo", "https://bravo.example", 1),
        };
        var options = new RenderOptions { TerminalWidth = 100, TerminalHeight = 35, LayoutVariant = "Grid" };

        var originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            renderer.RenderLauncher(bookmarks, selectedIndex: 0, scrollOffset: 0, options);
            var first = await _server.WaitForNextAsync("launcherState");
            Assert.True(first.GetProperty("visible").GetBoolean());

            await renderer.SetPromptLineAsync(new PromptLineState { Prefix = ":", Buffer = "hel", Cursor = 3 });
            var typed = await _server.WaitForNextAsync("launcherState");
            Assert.True(typed.GetProperty("visible").GetBoolean());
            var prompt = typed.GetProperty("state").GetProperty("promptLine");
            Assert.Equal(":", prompt.GetProperty("prefix").GetString());
            Assert.Equal("hel", prompt.GetProperty("buffer").GetString());
            Assert.Equal(3, prompt.GetProperty("cursor").GetInt32());

            await renderer.SetPromptLineAsync(null);
            var cleared = await _server.WaitForNextAsync("launcherState");
            Assert.True(cleared.GetProperty("visible").GetBoolean());
            Assert.Equal(JsonValueKind.Null, cleared.GetProperty("state").GetProperty("promptLine").ValueKind);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    // Guards:   the '/' search prompt over the link-tree pane reaches the shell as linkTreeState.promptLine, the
    //           pane stays visible, and closing the prompt sends promptLine null.
    // Fails if: SetPromptLineAsync ignores the link-tree pane, drops promptLine from linkTreeState, or never clears it.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PromptLinePaneWiringTests.LinkTree_PromptLine_IsMirroredThenCleared"
    [Fact]
    public async Task LinkTree_PromptLine_IsMirroredThenCleared()
    {
        _server = await RecordingShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);
        Assert.NotNull(channel);
        var renderer = CreateRenderer(channel);

        var page = CreateFlatPage(5);
        var options = new RenderOptions { TerminalWidth = 121, TerminalHeight = 60, ShellPane = true };

        var originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            renderer.RenderHierarchical(page, new NavigationContext(), options);
            var first = await _server.WaitForNextAsync("linkTreeState");
            Assert.True(first.GetProperty("visible").GetBoolean());

            await renderer.SetPromptLineAsync(new PromptLineState { Prefix = "/", Buffer = "wire", Cursor = 4 });
            var typed = await _server.WaitForNextAsync("linkTreeState");
            Assert.True(typed.GetProperty("visible").GetBoolean());
            var prompt = typed.GetProperty("state").GetProperty("promptLine");
            Assert.Equal("/", prompt.GetProperty("prefix").GetString());
            Assert.Equal("wire", prompt.GetProperty("buffer").GetString());
            Assert.Equal(4, prompt.GetProperty("cursor").GetInt32());

            await renderer.SetPromptLineAsync(null);
            var cleared = await _server.WaitForNextAsync("linkTreeState");
            Assert.True(cleared.GetProperty("visible").GetBoolean());
            Assert.Equal(JsonValueKind.Null, cleared.GetProperty("state").GetProperty("promptLine").ValueKind);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    // Guards:   with no HTML pane on screen there is nothing to mirror into, so a prompt keystroke sends the shell
    //           NO message at all (the terminal already draws the prompt itself).
    // Fails if: SetPromptLineAsync pushes a launcher/link-tree state when neither pane has been rendered.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PromptLinePaneWiringTests.NoPaneRendered_PromptLine_SendsNothing"
    [Fact]
    public async Task NoPaneRendered_PromptLine_SendsNothing()
    {
        _server = await RecordingShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);
        Assert.NotNull(channel);
        var renderer = CreateRenderer(channel);

        await renderer.SetPromptLineAsync(new PromptLineState { Prefix = ":", Buffer = "q", Cursor = 1 });
        await renderer.SetPromptLineAsync(null);

        Assert.False(_server.TryTakeNext(TimeSpan.FromMilliseconds(300), out _), "no pane is up, so no state may be sent");
    }

    // Guards:   while a wizard/chooser card is being painted into the terminal, the link-tree HTML pane is hidden
    //           (linkTreeState visible:false) so the user can see the card, and it comes back (visible:true) once the card is gone.
    // Fails if: RenderHierarchical keeps pushing linkTreeState visible:true while OverlayActive is set (the pane then
    //           covers the card), or it never re-shows the pane after the overlay is cleared.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PromptLinePaneWiringTests.LinkTree_PaneIsHiddenWhileOverlayCardIsUp"
    [Fact]
    public async Task LinkTree_PaneIsHiddenWhileOverlayCardIsUp()
    {
        _server = await RecordingShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);
        Assert.NotNull(channel);
        var renderer = CreateRenderer(channel);

        var page = CreateFlatPage(5);
        var options = new RenderOptions { TerminalWidth = 121, TerminalHeight = 60, ShellPane = true };

        var originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            // No card: the pane is shown, as always.
            renderer.OverlayActive = false;
            renderer.RenderHierarchical(page, new NavigationContext(), options);
            var shown = await _server.WaitForNextAsync("linkTreeState");
            Assert.True(shown.GetProperty("visible").GetBoolean());

            // A card is up: the pane is hidden (once, not re-sent on every repaint).
            renderer.OverlayActive = true;
            renderer.RenderHierarchical(page, new NavigationContext(), options);
            var hidden = await _server.WaitForNextAsync("linkTreeState");
            Assert.False(hidden.GetProperty("visible").GetBoolean());

            renderer.RenderHierarchical(page, new NavigationContext(), options);
            Assert.False(_server.TryTakeNext(TimeSpan.FromMilliseconds(300), out var extra), $"a repaint under the card must not re-show the pane (got {extra.Method})");

            // A prompt keystroke under the card must not resurrect the pane either.
            await renderer.SetPromptLineAsync(new PromptLineState { Prefix = ":", Buffer = "x", Cursor = 1 });
            Assert.False(_server.TryTakeNext(TimeSpan.FromMilliseconds(300), out var prompted), $"the hidden pane must not be re-sent on a prompt keystroke (got {prompted.Method})");
            await renderer.SetPromptLineAsync(null);

            // Card gone: the next render brings the pane back.
            renderer.OverlayActive = false;
            renderer.RenderHierarchical(page, new NavigationContext(), options);
            var back = await _server.WaitForNextAsync("linkTreeState");
            Assert.True(back.GetProperty("visible").GetBoolean());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    // Guards:   when a wizard/chooser card is cancelled and the code clears the card WITHOUT repainting the page,
    //           the link-tree HTML pane still comes straight back (linkTreeState visible:true) - the user is not left
    //           looking at the terminal's plain text tree until the next keypress.
    // Fails if: RestorePaneAfterOverlay() does nothing (the pane stays hidden after a cancel), or re-sends the pane
    //           when it was never hidden for a card (a stray visible:true over the reader / launcher).
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PromptLinePaneWiringTests.LinkTree_PaneComesBackWhenOverlayClearsWithoutARender"
    [Fact]
    public async Task LinkTree_PaneComesBackWhenOverlayClearsWithoutARender()
    {
        _server = await RecordingShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);
        Assert.NotNull(channel);
        var renderer = CreateRenderer(channel);

        var page = CreateFlatPage(5);
        var options = new RenderOptions { TerminalWidth = 121, TerminalHeight = 60, ShellPane = true };

        var originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            renderer.OverlayActive = false;
            renderer.RenderHierarchical(page, new NavigationContext(), options);
            var shown = await _server.WaitForNextAsync("linkTreeState");
            Assert.True(shown.GetProperty("visible").GetBoolean());

            // A card is up: pane hidden.
            renderer.OverlayActive = true;
            renderer.RenderHierarchical(page, new NavigationContext(), options);
            var hidden = await _server.WaitForNextAsync("linkTreeState");
            Assert.False(hidden.GetProperty("visible").GetBoolean());

            // The card is cancelled and NO render follows: the pane must still come back.
            renderer.RestorePaneAfterOverlay();
            var back = await _server.WaitForNextAsync("linkTreeState");
            Assert.True(back.GetProperty("visible").GetBoolean());
            Assert.False(renderer.OverlayActive);

            // The pane is visible now, so a second restore is a no-op (nothing re-sent).
            renderer.RestorePaneAfterOverlay();
            Assert.False(_server.TryTakeNext(TimeSpan.FromMilliseconds(300), out var extra), $"a second restore must not re-send the pane (got {extra.Method})");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static TerminalPageRenderer CreateRenderer(ShellChannel channel)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        return new TerminalPageRenderer(
            themeProvider,
            NullLogger<TerminalPageRenderer>.Instance,
            podcastJobManager: null,
            shellChannel: channel);
    }

    private static Page CreateFlatPage(int stories)
    {
        var root = LinkNode.CreateRoot();
        for (var i = 1; i <= stories; i++)
        {
            root.AddChild(new LinkInfo
            {
                DisplayText = $"Story {i}",
                Url = $"https://example.com/story-{i}",
                Type = LinkType.Content,
                ImportanceScore = 50,
            });
        }

        var page = Page.Create(
            "http://127.0.0.1:8163/",
            "<html></html>",
            new PageMetadata { Title = "Flat Wire" });
        page.SetLinkTree(NavigationTree.BuildFromRoot(root));
        return page;
    }

    /// <summary>Shell-side server that records every launcherState / linkTreeState params object in order.</summary>
    private sealed class RecordingShellServer : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly BlockingCollection<(string Method, JsonElement Params)> _messages = new();
        private Socket? _client;

        private RecordingShellServer(Socket listener)
        {
            _listener = listener;
        }

        public static Task<RecordingShellServer> StartAsync(string socketPath)
        {
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            var server = new RecordingShellServer(listener);
            _ = Task.Run(server.AcceptLoopAsync);
            return Task.FromResult(server);
        }

        public bool TryTakeNext(TimeSpan timeout, out (string Method, JsonElement Params) message)
            => _messages.TryTake(out message, timeout);

        public Task<JsonElement> WaitForNextAsync(string expectedMethod)
        {
            return Task.Run(() =>
            {
                if (!_messages.TryTake(out var msg, TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException($"No {expectedMethod} message arrived within 5s");
                }

                Assert.Equal(expectedMethod, msg.Method);
                return msg.Params;
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _client?.Dispose();
            _listener.Dispose();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                _client = await _listener.AcceptAsync(_cts.Token);
                using var stream = new NetworkStream(_client, ownsSocket: false);
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true };
                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is null)
                    {
                        return;
                    }

                    var msg = JsonSerializer.Deserialize<JsonElement>(line);
                    var id = msg.GetProperty("id").GetInt64();
                    var method = msg.GetProperty("method").GetString();
                    if (method is "launcherState" or "linkTreeState")
                    {
                        _messages.Add((method, msg.GetProperty("params").Clone()));
                    }

                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { id, ok = true, result = new { } }));
                }
            }
            catch (OperationCanceledException)
            {
                // Disposal.
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // Torn down mid-read — fine for tests.
            }
        }
    }
}
