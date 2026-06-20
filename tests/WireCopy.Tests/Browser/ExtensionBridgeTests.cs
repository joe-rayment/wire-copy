// Licensed under the MIT License. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Infrastructure.Browser.Extension;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Round-trips the backend &lt;-&gt; extension control protocol (workspace-ozn8 acceptance: "a test
/// client round-trips navigate + domSnapshot") against the real <see cref="ExtensionBridge"/>. A
/// tiny in-test "host" binds the per-tab Unix socket exactly as <c>WireCopy.Web.ExtSession</c> does
/// (4-byte big-endian length + 1 type byte + UTF-8 JSON), so this validates the on-the-wire contract
/// both sides speak.
/// </summary>
public sealed class ExtensionBridgeTests : IDisposable
{
    private const byte TypeJson = 1;
    private readonly string _socketPath = Path.Combine(Path.GetTempPath(), $"wc-ext-test-{Guid.NewGuid():n}.sock");

    [Fact]
    public async Task NavigateAndCapture_RoundTripsDomSnapshot()
    {
        using var listener = Listen(_socketPath);
        await using var bridge = new ExtensionBridge(NullLogger<ExtensionBridge>.Instance);
        bridge.ConnectForTest(_socketPath);

        using var host = await listener.AcceptAsync();

        // Extension announces readiness.
        await SendFrameAsync(host, """{"type":"ready","url":"about:blank","viewport":{"w":1280,"h":800}}""");
        (await bridge.WaitForReadyAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        bridge.IsConnected.Should().BeTrue();

        // Backend asks the host browser to navigate; the host replies with the rendered DOM.
        var navigateTask = bridge.NavigateAndCaptureAsync("https://example.com/world");

        var command = await ReadFrameAsync(host);
        using (var doc = JsonDocument.Parse(command))
        {
            doc.RootElement.GetProperty("type").GetString().Should().Be("navigate");
            doc.RootElement.GetProperty("url").GetString().Should().Be("https://example.com/world");
            var id = doc.RootElement.GetProperty("id").GetInt32();

            await SendFrameAsync(host, JsonSerializer.Serialize(new
            {
                type = "domSnapshot",
                id,
                url = "https://example.com/world",
                html = "<html><body><a href=\"/a\">Story A</a></body></html>",
                viewport = new { w = 414, h = 896 },
            }));
        }

        var snapshot = await navigateTask.WaitAsync(TimeSpan.FromSeconds(5));
        snapshot.Url.Should().Be("https://example.com/world");
        snapshot.Html.Should().Contain("Story A");
        snapshot.ViewportWidth.Should().Be(414);
        snapshot.ViewportHeight.Should().Be(896);
    }

    [Fact]
    public async Task DriveCommand_RoundTripsActionResult()
    {
        using var listener = Listen(_socketPath);
        await using var bridge = new ExtensionBridge(NullLogger<ExtensionBridge>.Instance);
        bridge.ConnectForTest(_socketPath);

        using var host = await listener.AcceptAsync();
        await SendFrameAsync(host, """{"type":"ready"}""");
        (await bridge.WaitForReadyAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        var highlightTask = bridge.HighlightAsync(selector: null, url: "/a", text: "Story A");

        var command = await ReadFrameAsync(host);
        using (var doc = JsonDocument.Parse(command))
        {
            doc.RootElement.GetProperty("type").GetString().Should().Be("highlight");
            doc.RootElement.GetProperty("url").GetString().Should().Be("/a");
            var id = doc.RootElement.GetProperty("id").GetInt32();
            await SendFrameAsync(host, JsonSerializer.Serialize(new { type = "actionResult", id, ok = true }));
        }

        (await highlightTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    [Fact]
    public async Task NavigatedEvent_IsRaised()
    {
        using var listener = Listen(_socketPath);
        await using var bridge = new ExtensionBridge(NullLogger<ExtensionBridge>.Instance);
        bridge.ConnectForTest(_socketPath);

        using var host = await listener.AcceptAsync();
        await SendFrameAsync(host, """{"type":"ready"}""");
        (await bridge.WaitForReadyAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        var navigated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.Navigated += url => navigated.TrySetResult(url);

        await SendFrameAsync(host, """{"type":"navigated","url":"https://example.com/spa-route"}""");

        (await navigated.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("https://example.com/spa-route");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static Socket Listen(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        return listener;
    }

    private static async Task SendFrameAsync(Socket socket, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + 1 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length + 1);
        frame[4] = TypeJson;
        payload.CopyTo(frame, 5);
        await socket.SendAsync(frame, SocketFlags.None);
    }

    private static async Task<string> ReadFrameAsync(Socket socket)
    {
        var header = new byte[4];
        await ReadExactAsync(socket, header);
        var len = BinaryPrimitives.ReadInt32BigEndian(header);
        var buf = new byte[len];
        await ReadExactAsync(socket, buf);
        buf[0].Should().Be(TypeJson);
        return Encoding.UTF8.GetString(buf, 1, len - 1);
    }

    private static async Task ReadExactAsync(Socket socket, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None);
            if (n == 0)
            {
                throw new IOException("socket closed mid-frame");
            }

            read += n;
        }
    }
}
