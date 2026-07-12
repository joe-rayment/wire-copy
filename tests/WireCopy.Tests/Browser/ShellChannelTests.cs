// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
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
