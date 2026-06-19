// <copyright file="WebPaneFramingTests.cs" company="Wire Copy">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>

using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class WebPaneFramingTests
{
    [Fact]
    public void Encode_WritesBigEndianLength_TypeByte_ThenPayload()
    {
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var frame = PaneFraming.Encode(PaneFraming.TypeFrame, payload);

        // [4-byte BE length = payload + 1 type byte][type][payload]
        BinaryPrimitives.ReadInt32BigEndian(frame).Should().Be(payload.Length + 1);
        frame[4].Should().Be(PaneFraming.TypeFrame);
        frame.Length.Should().Be(4 + 1 + payload.Length);
        frame[5..].Should().Equal(payload);
    }

    [Theory]
    [InlineData(PaneFraming.TypeFrame)]
    [InlineData(PaneFraming.TypeInput)]
    [InlineData(PaneFraming.TypeControl)]
    public void EncodeThenTryDecode_RoundTrips(byte type)
    {
        var payload = Encoding.UTF8.GetBytes("{\"kind\":\"mode\",\"mode\":\"snapshot\"}");

        var frame = PaneFraming.Encode(type, payload);
        var ok = PaneFraming.TryDecode(frame, out var decodedType, out var decodedPayload);

        ok.Should().BeTrue();
        decodedType.Should().Be(type);
        decodedPayload.Should().Equal(payload);
    }

    [Fact]
    public void Encode_EmptyPayload_RoundTrips()
    {
        var frame = PaneFraming.Encode(PaneFraming.TypeControl, Array.Empty<byte>());

        var ok = PaneFraming.TryDecode(frame, out var type, out var payload);

        ok.Should().BeTrue();
        type.Should().Be(PaneFraming.TypeControl);
        payload.Should().BeEmpty();
    }

    [Fact]
    public void TryDecode_ShortBuffer_ReturnsFalse()
    {
        PaneFraming.TryDecode(new byte[] { 0, 0, 1 }, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_TruncatedPayload_ReturnsFalse()
    {
        var full = PaneFraming.Encode(PaneFraming.TypeFrame, new byte[] { 1, 2, 3, 4, 5 });

        // Drop the last two payload bytes — the declared length now exceeds the buffer.
        PaneFraming.TryDecode(full[..^2], out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_OversizedLength_ReturnsFalse()
    {
        var frame = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(frame, PaneFraming.MaxPayload + 1);

        PaneFraming.TryDecode(frame, out _, out _).Should().BeFalse();
    }
}
