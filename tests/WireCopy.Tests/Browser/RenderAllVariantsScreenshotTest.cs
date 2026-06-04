// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI;
using Xunit;
using Xunit.Abstractions;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Captures the strip-ANSI rendered box for each <see cref="HumanActionVariant"/>
/// at the configured 80-col terminal width with a worst-case (22-char) subdomain.
/// Acts as a low-fidelity "screenshot" so future copy edits regress against the
/// real layout, in addition to the live screenshot work in
/// /tmp/wirecopy-screenshots/ — see workspace-0b9s QA punch-list #1 / #4.
/// </summary>
[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class RenderAllVariantsScreenshotTest
{
    private readonly ITestOutputHelper _output;

    public RenderAllVariantsScreenshotTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(HumanActionVariant.Captcha)]
    [InlineData(HumanActionVariant.Login)]
    [InlineData(HumanActionVariant.CookieConsent)]
    [InlineData(HumanActionVariant.TwoFactor)]
    [InlineData(HumanActionVariant.Paywall)]
    [InlineData(HumanActionVariant.RegionBlock)]
    [InlineData(HumanActionVariant.RedirectLoop)]
    [InlineData(HumanActionVariant.Generic)]
    public void RenderHumanAction_EmitsClosedBoxForEveryVariant(HumanActionVariant variant)
    {
        const string domain = "subdomain.nytimes.com";
        const string url = "https://subdomain.nytimes.com/2026/05/01/article-very-long-name.html";

        var theme = Substitute.For<IThemeProvider>();
        theme.CurrentTheme.Returns(ThemeName.Phosphor);
        var renderer = new TerminalPageRenderer(theme, NullLogger<TerminalPageRenderer>.Instance);

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            renderer.RenderHumanAction(new HumanActionRequired(variant, domain), url);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var ansi = sw.ToString();
        var rows = SplitIntoVisualRows(ansi);

        // Emit the rendered box so it's visible in test output (xunit -v normal).
        _output.WriteLine($"=== {variant} ===");
        foreach (var row in rows)
        {
            _output.WriteLine(row);
        }

        // Sanity: every variant must produce a closed box.
        var joined = string.Join('\n', rows);
        joined.Should().Contain("╭", "top-left rounded corner is the box header glyph");
        joined.Should().Contain("╮", "top-right rounded corner closes the header");
        joined.Should().Contain("╰", "bottom-left rounded corner");
        joined.Should().Contain("╯", "bottom-right rounded corner closes the footer");

        // Every reconstructed visual row must fit within 80 cols so the right
        // border is reachable on a default terminal.
        var maxRowLen = 0;
        var maxRowText = string.Empty;
        foreach (var row in rows)
        {
            var len = row.Length;
            if (len > maxRowLen)
            {
                maxRowLen = len;
                maxRowText = row;
            }
        }

        maxRowLen.Should().BeLessThanOrEqualTo(
            80,
            $"rendered {variant} box must fit within 80-col terminal — longest row was {maxRowLen}: '{maxRowText}'");
    }

    private static List<string> SplitIntoVisualRows(string ansi)
    {
        // Outside BeginFrame mode the renderer calls Console.SetCursorPosition
        // (a system call) instead of emitting cursor-position escapes — so the
        // captured stdout only carries the text content of each row joined by
        // \x1b[K (clear-to-end-of-line) sentinels. Use that as our row separator.
        var chunks = ansi.Split("\x1b[K");

        var rows = new List<string>();
        foreach (var chunk in chunks)
        {
            // Strip remaining ANSI from this row.
            var plain = Regex.Replace(chunk, @"\x1b\[[0-9;?]*[a-zA-Z]", string.Empty).TrimEnd();
            if (plain.Length > 0)
            {
                rows.Add(plain);
            }
        }

        return rows;
    }
}
