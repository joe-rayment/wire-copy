// Licensed under the MIT License. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using WireCopy.Infrastructure.BrowserExtension;
using Xunit;

namespace WireCopy.Tests.BrowserExtension;

/// <summary>
/// The browser's native-messaging wire format (workspace-m7aml): a 4-byte length, then that
/// many bytes of UTF-8 JSON. These tests pin the byte-level rules the browser relies on.
/// </summary>
[Trait("Category", "Unit")]
public sealed class NativeMessagingFramingTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // Guards:   A message with accented, CJK and emoji text survives a write-then-read unchanged, and the 4-byte prefix counts BYTES (not characters).
    // Fails if: WriteAsync writes string.Length as the prefix, or either side uses an encoding other than UTF-8.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Framing_RoundTripsMultibyteText
    [Fact]
    public async Task Framing_RoundTripsMultibyteText()
    {
        const string json = "{\"title\":\"café 中文 \U0001F642\"}";
        var expectedBytes = Encoding.UTF8.GetByteCount(json);
        expectedBytes.Should().BeGreaterThan(json.Length, "the sample must contain multibyte characters");

        using var stream = new MemoryStream();
        await NativeMessagingFraming.WriteAsync(stream, json, Ct);

        var written = stream.ToArray();
        written.Length.Should().Be(4 + expectedBytes);
        BinaryPrimitives.ReadUInt32LittleEndian(written).Should().Be((uint)expectedBytes);

        stream.Position = 0;
        (await NativeMessagingFraming.ReadAsync(stream, Ct)).Should().Be(json);
    }

    // Guards:   A 2 MB message from the browser (allowed up to 64 MiB) is read back intact, not cut at the 1 MB reply limit.
    // Fails if: ReadAsync applies the outgoing 1 MB cap to incoming frames, or stops after a short read from the pipe.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Framing_ReadsA2MbFrameIntact
    [Fact]
    public async Task Framing_ReadsA2MbFrameIntact()
    {
        var body = new string('x', (2 * 1024 * 1024) - 10) + "é中\U0001F642";
        var bytes = Encoding.UTF8.GetBytes(body);
        bytes.Length.Should().BeGreaterThan(2 * 1024 * 1024 - 10);

        var frame = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)bytes.Length);
        bytes.CopyTo(frame, 4);

        using var stream = new MemoryStream(frame);
        var read = await NativeMessagingFraming.ReadAsync(stream, Ct);

        read.Should().Be(body);
    }

    // Guards:   A length prefix above the 64 MiB limit is refused straight away, without trying to read or allocate that much.
    // Fails if: The size check is removed, so a hostile prefix makes the host allocate gigabytes or hang waiting for the body.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Framing_RefusesAnOversizePrefix
    [Fact]
    public async Task Framing_RefusesAnOversizePrefix()
    {
        var frame = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)NativeMessagingFraming.MaxIncomingBytes + 1);
        using var stream = new MemoryStream(frame);

        var act = () => NativeMessagingFraming.ReadAsync(stream, Ct);

        await act.Should().ThrowAsync<NativeMessagingFrameException>().WithMessage("*too large*");
        stream.Position.Should().Be(4, "nothing after the prefix should be read");
    }

    // Guards:   A reply over the browser's 1 MB limit is refused (the browser would silently drop it) and nothing is written.
    // Fails if: WriteAsync loses its size check, so an oversize reply is sent and the extension never gets an answer.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Framing_RefusesToWriteMoreThan1Mb
    [Fact]
    public async Task Framing_RefusesToWriteMoreThan1Mb()
    {
        using var stream = new MemoryStream();
        var json = new string('a', NativeMessagingFraming.MaxOutgoingBytes + 1);

        var act = () => NativeMessagingFraming.WriteAsync(stream, json, Ct);

        await act.Should().ThrowAsync<NativeMessagingFrameException>().WithMessage("*too large*");
        stream.Length.Should().Be(0);
    }

    // Guards:   An input that ends before any length prefix (the browser closed the pipe) reads as "no more messages", not an error.
    // Fails if: ReadAsync throws on an empty stream, which would make every normal shutdown look like a protocol failure.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Framing_ReturnsNullOnEmptyStream
    [Fact]
    public async Task Framing_ReturnsNullOnEmptyStream()
    {
        using var stream = new MemoryStream();

        (await NativeMessagingFraming.ReadAsync(stream, Ct)).Should().BeNull();
    }

    // Guards:   A frame whose body stops short of the announced length is reported as a broken frame, never returned as a partial message.
    // Fails if: ReadAsync returns whatever bytes arrived (or an empty string) when the stream ends mid-body.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Framing_ThrowsOnATruncatedBody
    [Fact]
    public async Task Framing_ThrowsOnATruncatedBody()
    {
        var frame = new byte[4 + 10];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 100);
        Encoding.UTF8.GetBytes("0123456789").CopyTo(frame, 4);
        using var stream = new MemoryStream(frame);

        var act = () => NativeMessagingFraming.ReadAsync(stream, Ct);

        await act.Should().ThrowAsync<NativeMessagingFrameException>().WithMessage("*middle of a message*");
    }

    // Guards:   A stream that ends part-way through the 4-byte length prefix is a broken frame, distinct from a clean end of input.
    // Fails if: A short prefix is treated like end-of-stream (null), hiding that the browser died mid-message.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Framing_ThrowsOnATruncatedPrefix
    [Fact]
    public async Task Framing_ThrowsOnATruncatedPrefix()
    {
        using var stream = new MemoryStream([0x05, 0x00]);

        var act = () => NativeMessagingFraming.ReadAsync(stream, Ct);

        await act.Should().ThrowAsync<NativeMessagingFrameException>();
    }
}
