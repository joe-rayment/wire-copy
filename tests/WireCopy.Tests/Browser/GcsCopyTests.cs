// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Width-aware copy tests for the GCS setup flow (workspace-ur5h).
///
/// <para>
/// The user's bead frames the prior fix (cgnt) as a catastrophic failure on
/// every criterion: subtitles truncated mid-word, copy strings that don't fit
/// at 100 cols, no width assertions. This test class is the regression gate
/// that ensures we don't ship truncated copy again.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class GcsCopyTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(54)] // common — fieldWidth 60 - 6
    [InlineData(80)]
    [InlineData(100)]
    public void FitOrShorten_PrefersPrimaryWhenItFits(int maxLen)
    {
        var primary = new string('x', maxLen);
        var fallback = "fallback";
        GcsCopy.FitOrShorten(primary, fallback, maxLen).Should().Be(primary);
    }

    [Fact]
    public void FitOrShorten_FallsBackWhenPrimaryTooLong()
    {
        var primary = "this is a very long primary string that does not fit anywhere";
        var fallback = "short";
        GcsCopy.FitOrShorten(primary, fallback, 10).Should().Be(fallback);
    }

    [Fact]
    public void FitOrShorten_HardCropsWhenBothTooLong()
    {
        var primary = "primary primary primary";
        var fallback = "fallback fallback";
        var result = GcsCopy.FitOrShorten(primary, fallback, 5);
        result.Length.Should().BeLessThanOrEqualTo(5);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    public void MaxCopyWidth_NeverExceedsAbsoluteCap(int fieldWidth)
    {
        var width = GcsCopy.MaxCopyWidth(fieldWidth);
        width.Should().BeLessThanOrEqualTo(GcsCopy.MaxAbsoluteWidth - 6);
    }

    [Fact]
    public void MaxCopyWidth_ShrinksWithFieldWidth()
    {
        GcsCopy.MaxCopyWidth(40).Should().BeLessThan(GcsCopy.MaxCopyWidth(60));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(74)]
    public void WrapToWidth_AllLinesFitWithinMaxLen(int maxLen)
    {
        const string text = "WireCopy uploads your podcast audio plus RSS feed to Google Cloud Storage. " +
                             "A service-account key is a small JSON file Google gives you that lets " +
                             "WireCopy authenticate without a password.";
        var lines = GcsCopy.WrapToWidth(text, maxLen);
        lines.Should().NotBeEmpty();
        foreach (var line in lines)
        {
            line.Length.Should().BeLessThanOrEqualTo(maxLen);
        }
    }

    [Fact]
    public void WrapToWidth_HandlesOverlongWord()
    {
        // A URL-style token that exceeds maxLen. WrapToWidth must split it,
        // not return a single line longer than maxLen. The bead's framing:
        // "wrap or shorten — never truncate". Splitting an overlong word is
        // the well-defined behaviour.
        const string text = "console.cloud.google.com/iam-admin/serviceaccounts is a very long URL";
        var lines = GcsCopy.WrapToWidth(text, 20);
        foreach (var line in lines)
        {
            line.Length.Should().BeLessThanOrEqualTo(20);
        }
    }

    [Fact]
    public void WrapToWidth_PreservesAllWords()
    {
        const string text = "alpha beta gamma delta";
        var lines = GcsCopy.WrapToWidth(text, 10);
        var joined = string.Join(' ', lines);
        joined.Should().Contain("alpha")
            .And.Contain("beta")
            .And.Contain("gamma")
            .And.Contain("delta");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void WrapToWidth_HandlesEmptyOrNull(string? text)
    {
        GcsCopy.WrapToWidth(text!, 20).Should().BeEmpty();
    }
}
