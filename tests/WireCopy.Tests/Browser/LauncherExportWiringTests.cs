// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Shell;
using WireCopy.Infrastructure.Browser.UI;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-0jxo end-to-end wiring: drives the REAL <see cref="TerminalPageRenderer"/>
/// (through the production <see cref="IPageRenderer"/> render path) over a live
/// <see cref="ShellChannel"/> and a genuine Unix-domain socket, and asserts the
/// actual <c>launcherState</c> messages the shell receives — not a stand-in. This
/// covers the choke point (RenderLauncher emits visible:true with the view-model;
/// a following non-launcher render emits visible:false) that the isolated
/// view-model tests cannot.
/// </summary>
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public sealed class LauncherExportWiringTests : IAsyncLifetime
{
    private readonly string _socketPath = Path.Combine(
        Path.GetTempPath(), $"wc-launcher-export-{Guid.NewGuid():N}.sock");

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

    [Fact]
    public async Task RenderLauncher_ThenNonLauncher_EmitsVisibleTrueThenFalse()
    {
        _server = await RecordingShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);
        Assert.NotNull(channel);

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var renderer = new TerminalPageRenderer(
            themeProvider,
            NullLogger<TerminalPageRenderer>.Instance,
            podcastJobManager: null,
            shellChannel: channel);

        var bookmarks = new List<Bookmark>
        {
            Bookmark.Create("Alpha", "https://alpha.example", 0),
            Bookmark.Create("Bravo", "https://bravo.example", 1),
        };
        var options = new RenderOptions { TerminalWidth = 100, TerminalHeight = 35, LayoutVariant = "Grid" };

        // Drive the real launcher render (selecting the first tile).
        var originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            renderer.RenderLauncher(bookmarks, selectedIndex: 0, scrollOffset: 0, options);

            var first = await _server.WaitForNextAsync();
            Assert.True(first.GetProperty("visible").GetBoolean());
            var state = first.GetProperty("state");
            Assert.Equal("bookmark", state.GetProperty("slots")[0].GetProperty("kind").GetString());
            Assert.Equal("readingList", state.GetProperty("slots")[1].GetProperty("kind").GetString());
            Assert.Equal(2, state.GetProperty("bookmarkCount").GetInt32());
            Assert.Equal(0, state.GetProperty("selectedIndex").GetInt32());

            // Now render a non-launcher screen — the launcher-leave signal.
            renderer.RenderError("boom", "https://alpha.example");

            var second = await _server.WaitForNextAsync();
            Assert.False(second.GetProperty("visible").GetBoolean());
            Assert.Equal(JsonValueKind.Null, second.GetProperty("state").ValueKind);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    /// <summary>Shell-side server that records every launcherState params object in order.</summary>
    private sealed class RecordingShellServer : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly System.Collections.Concurrent.BlockingCollection<JsonElement> _launcherStates = new();
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

        public Task<JsonElement> WaitForNextAsync()
        {
            return Task.Run(() =>
            {
                if (!_launcherStates.TryTake(out var el, TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("No launcherState message arrived within 5s");
                }

                return el;
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
                    if (method == "launcherState")
                    {
                        _launcherStates.Add(msg.GetProperty("params").Clone());
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
