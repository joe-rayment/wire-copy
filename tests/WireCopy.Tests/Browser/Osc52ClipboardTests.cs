// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

public class Osc52ClipboardTests
{
    [Fact]
    public void BuildSequence_EmitsOsc52WithBase64Payload()
    {
        // base64("hi") == "aGk="
        Osc52Clipboard.BuildSequence("hi").Should().Be("\x1b]52;c;aGk=\x07");
    }

    [Fact]
    public void BuildSequence_RoundTripsUtf8()
    {
        var seq = Osc52Clipboard.BuildSequence("café ☕")!;
        seq.Should().StartWith("\x1b]52;c;").And.EndWith("\x07");
        var b64 = seq["\x1b]52;c;".Length..^1];
        Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Should().Be("café ☕");
    }

    [Fact]
    public void Copy_WritesTheSequence_AndReturnsTrue()
    {
        var writer = new StringWriter();
        Osc52Clipboard.Copy("data", writer).Should().BeTrue();
        writer.ToString().Should().Be("\x1b]52;c;ZGF0YQ==\x07");
    }

    [Fact]
    public void OversizedPayload_IsRefused()
    {
        var huge = new string('x', Osc52Clipboard.MaxBase64Length); // base64 grows ~4/3 → over the cap
        Osc52Clipboard.BuildSequence(huge).Should().BeNull();
        Osc52Clipboard.Copy(huge, new StringWriter()).Should().BeFalse();
    }
}
