// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser.Shell;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Real-transport tests for <see cref="ShellChannel"/>: a miniature in-test shell server
/// speaks the JSON-lines protocol over a genuine Unix-domain socket.
/// </summary>
public sealed class ShellChannelTests : IAsyncLifetime
{
    private readonly string _socketPath = Path.Combine(
        Path.GetTempPath(), $"wc-shellchannel-{Guid.NewGuid():N}.sock");

    private MiniShellServer? _server;

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
    public void TryConnect_EmptyPath_ReturnsNull()
    {
        Assert.Null(ShellChannel.TryConnect(null, NullLogger.Instance));
        Assert.Null(ShellChannel.TryConnect(string.Empty, NullLogger.Instance));
    }

    [Fact]
    public void TryConnect_NoServer_ReturnsNullWithoutThrowing()
    {
        Assert.Null(ShellChannel.TryConnect(_socketPath, NullLogger.Instance));
    }

    [Fact]
    public async Task Hello_ReturnsEndpoint_AndCachesIt()
    {
        _server = await MiniShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);
        Assert.NotNull(channel);

        var first = await channel!.GetCdpEndpointAsync();
        var second = await channel.GetCdpEndpointAsync();

        Assert.Equal("http://127.0.0.1:9999", first);
        Assert.Equal(first, second);
        Assert.Equal(1, _server.HelloCount);
        Assert.True(channel.IsConnected);
    }

    [Fact]
    public async Task SetPaneVisible_RoundTripsParams()
    {
        _server = await MiniShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);

        var ok = await channel!.SetPaneVisibleAsync(true);

        Assert.True(ok);
        Assert.Equal(JsonValueKind.True, _server.LastSetPaneVisible?.ValueKind);
    }

    [Fact]
    public async Task SendLauncherState_RoundTripsParams_IncludingNullUrlAndUrlTyping()
    {
        // workspace-0jxo: the launcherState params must arrive at the shell intact
        // over the real socket — a null slot URL stays JSON null, and the URL-typing
        // payload survives the round-trip (both are load-bearing for the HTML pane).
        _server = await MiniShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);

        var state = new LauncherViewState
        {
            Slots = new List<LauncherSlot>
            {
                new() { Index = 0, Kind = "bookmark", Title = "ALPHA", Url = "https://alpha.example", Hint = "alpha.example", Badge = "1" },
                new() { Index = 1, Kind = "readingList", Title = "READING LIST", Url = null, Hint = "nothing saved yet", Star = true, Badge = "2" },
            },
            SelectedIndex = -1,
            UrlTyping = new LauncherUrlTyping { Buffer = "al", Cursor = 2 },
            Paging = new LauncherPaging { Offset = 0, Visible = 2, Total = 2 },
            BookmarkCount = 1,
            Footer = new LauncherFooter(),
            Tagline = "All copy, no nonsense",
            Version = "v1.0",
        };

        await channel!.SendLauncherStateAsync(true, state);

        for (var i = 0; i < 100 && _server.LastLauncherState is null; i++)
        {
            await Task.Delay(20);
        }

        var p = _server.LastLauncherState!.Value;
        Assert.True(p.GetProperty("visible").GetBoolean());

        var s = p.GetProperty("state");
        Assert.Equal(-1, s.GetProperty("selectedIndex").GetInt32());

        var slots = s.GetProperty("slots");
        Assert.Equal(2, slots.GetArrayLength());
        Assert.Equal("readingList", slots[1].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, slots[1].GetProperty("url").ValueKind);
        Assert.True(slots[1].GetProperty("star").GetBoolean());

        var typing = s.GetProperty("urlTyping");
        Assert.Equal("al", typing.GetProperty("buffer").GetString());
        Assert.Equal(2, typing.GetProperty("cursor").GetInt32());
    }

    [Fact]
    public async Task SendLauncherState_Hidden_SendsNullState()
    {
        _server = await MiniShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);

        await channel!.SendLauncherStateAsync(false, null);

        for (var i = 0; i < 100 && _server.LastLauncherState is null; i++)
        {
            await Task.Delay(20);
        }

        var p = _server.LastLauncherState!.Value;
        Assert.False(p.GetProperty("visible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("state").ValueKind);
    }

    // Guards:   the ':' / '/' prompt line mirrored into the HTML panes (workspace-up3zx) arrives at the shell
    //           under the camelCase name `promptLine` as { prefix, buffer, cursor } on BOTH the launcherState and
    //           the linkTreeState messages, and a pane with no prompt active sends `promptLine: null`.
    // Fails if: the property is renamed or dropped from LauncherViewState / LinkTreeViewState, the wire casing
    //           changes (e.g. PromptLine / Prefix), or a missing prompt serialises as an empty object, not null.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PromptLine_SerialisesCamelCase"
    [Fact]
    public async Task PromptLine_SerialisesCamelCase_OnLauncherAndLinkTreeState()
    {
        _server = await MiniShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);

        var prompt = new PromptLineState { Prefix = ":", Buffer = "hel", Cursor = 3 };
        await channel!.SendLauncherStateAsync(true, new LauncherViewState
        {
            Slots = new List<LauncherSlot>(),
            Paging = new LauncherPaging { Offset = 0, Visible = 0, Total = 0 },
            Footer = new LauncherFooter(),
            Tagline = "t",
            Version = "v",
            PromptLine = prompt,
        });
        await channel.SendLinkTreeStateAsync(true, new LinkTreeViewState
        {
            Title = "Wire",
            Items = new List<LinkTreeItem>(),
            PromptLine = prompt,
        });

        for (var i = 0; i < 100 && (_server.LastLauncherState is null || _server.LastLinkTreeState is null); i++)
        {
            await Task.Delay(20);
        }

        foreach (var sent in new[] { _server.LastLauncherState!.Value, _server.LastLinkTreeState!.Value })
        {
            var line = sent.GetProperty("state").GetProperty("promptLine");
            Assert.Equal(":", line.GetProperty("prefix").GetString());
            Assert.Equal("hel", line.GetProperty("buffer").GetString());
            Assert.Equal(3, line.GetProperty("cursor").GetInt32());
        }

        // No prompt active: the key is present and null (the pane's "hide the prompt row" signal).
        await channel.SendLauncherStateAsync(true, new LauncherViewState
        {
            Slots = new List<LauncherSlot>(),
            Paging = new LauncherPaging { Offset = 0, Visible = 0, Total = 0 },
            Footer = new LauncherFooter(),
            Tagline = "t",
            Version = "v",
        });

        for (var i = 0; i < 100 && _server.LastLauncherState!.Value.GetProperty("state").GetProperty("promptLine").ValueKind != JsonValueKind.Null; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(
            JsonValueKind.Null,
            _server.LastLauncherState!.Value.GetProperty("state").GetProperty("promptLine").ValueKind);
    }

    [Fact]
    public async Task ServerError_ReturnsFalse_NotThrow()
    {
        _server = await MiniShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);

        var ok = await channel!.CreatePageAsync("boom"); // server errors on tag "boom"

        Assert.False(ok);
        Assert.True(channel.IsConnected); // an error reply is not a lost connection
    }

    [Fact]
    public async Task ServerGone_RequestsFailSoft_AndChannelReportsDisconnected()
    {
        _server = await MiniShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);
        Assert.NotNull(channel);
        await _server.DisposeAsync();
        _server = null;

        var ok = await channel!.SetPaneVisibleAsync(false);

        Assert.False(ok);
        for (var i = 0; i < 50 && channel.IsConnected; i++)
        {
            await Task.Delay(20);
        }

        Assert.False(channel.IsConnected);
    }

    [Fact]
    public async Task ModeEvent_RaisesModeChanged()
    {
        _server = await MiniShellServer.StartAsync(_socketPath);
        await using var channel = ShellChannel.TryConnect(_socketPath, NullLogger.Instance);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel!.ModeChanged += m => tcs.TrySetResult(m);
        await channel.GetCdpEndpointAsync(); // ensures the server has our connection

        await _server.BroadcastAsync("""{"event":"mode","params":{"mode":"browser"}}""");

        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(tcs.Task, done);
        Assert.Equal("browser", await tcs.Task);
    }

    /// <summary>Miniature shell-side server: accepts one client, answers the protocol.</summary>
    private sealed class MiniShellServer : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();
        private Socket? _client;

        private MiniShellServer(Socket listener)
        {
            _listener = listener;
        }

        public int HelloCount { get; private set; }

        public JsonElement? LastSetPaneVisible { get; private set; }

        public JsonElement? LastLauncherState { get; private set; }

        public JsonElement? LastLinkTreeState { get; private set; }

        public static Task<MiniShellServer> StartAsync(string socketPath)
        {
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            var server = new MiniShellServer(listener);
            _ = Task.Run(server.AcceptLoopAsync);
            return Task.FromResult(server);
        }

        public async Task BroadcastAsync(string line)
        {
            for (var i = 0; i < 100 && _client is null; i++)
            {
                await Task.Delay(20);
            }

            await _client!.SendAsync(Encoding.UTF8.GetBytes(line + "\n"));
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
                    object reply;
                    switch (method)
                    {
                        case "hello":
                            HelloCount++;
                            reply = new { id, ok = true, result = new { cdpEndpoint = "http://127.0.0.1:9999" } };
                            break;
                        case "setPane":
                            LastSetPaneVisible = msg.GetProperty("params").GetProperty("visible").Clone();
                            reply = new { id, ok = true, result = new { visible = true } };
                            break;
                        case "launcherState":
                            LastLauncherState = msg.GetProperty("params").Clone();
                            reply = new { id, ok = true, result = new { } };
                            break;
                        case "linkTreeState":
                            LastLinkTreeState = msg.GetProperty("params").Clone();
                            reply = new { id, ok = true, result = new { } };
                            break;
                        case "createPage" when msg.GetProperty("params").GetProperty("tag").GetString() == "boom":
                            reply = new { id, ok = false, error = "boom" };
                            break;
                        default:
                            reply = new { id, ok = true, result = new { } };
                            break;
                    }

                    await writer.WriteLineAsync(JsonSerializer.Serialize(reply));
                }
            }
            catch (OperationCanceledException)
            {
                // Disposal.
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // Client/listener torn down mid-read — fine for tests.
            }
        }
    }
}
