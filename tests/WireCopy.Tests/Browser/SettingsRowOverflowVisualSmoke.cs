// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-l6w0 visual smoke. Dumps the SettingsRowRenderer "Output
/// folder" row at three terminal widths so a reviewer can confirm:
/// <list type="bullet">
///   <item>Width 100: the long path renders whole, no truncation.</item>
///   <item>Width 80: the path is middle-truncated with "…", and the
///         <c>[Enter] Change</c> action sits flush at col 80.</item>
///   <item>Width 70: same truncation, more aggressive, action still intact.</item>
/// </list>
/// Output is written to <c>/tmp/wirecopy-settings-row-l6w0.txt</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SettingsRowOverflowVisualSmoke
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);

    private readonly ITestOutputHelper _output;

    public SettingsRowOverflowVisualSmoke(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DumpsOutputFolderRowAtThreeWidths()
    {
        var dumpPath = Path.Combine(Path.GetTempPath(), "wirecopy-settings-row-l6w0.txt");
        using var dump = new StreamWriter(dumpPath);

        const string longPath = "/home/user/.local/share/WireCopy/podcasts/reading-list.m4b";

        foreach (var (label, width) in new[] { ("WIDTH 70", 70), ("WIDTH 80", 80), ("WIDTH 100", 100) })
        {
            var (mainLine, _) = SettingsRowRenderer.Build(
                Palette,
                width: width - 2,
                isSelected: false,
                isWarning: false,
                statusIcon: "●",
                statusColor: Palette.PromptFg.AnsiFg,
                label: "Output folder",
                value: longPath,
                valueColor: Palette.PromptFg.AnsiFg,
                actionLabel: "Change");

            var plain = SettingsRowRenderer.StripAnsi(mainLine);

            dump.WriteLine("========================================");
            dump.WriteLine($"{label}, value={longPath}");
            dump.WriteLine($"Rendered length (ANSI-stripped): {plain.Length}");
            dump.WriteLine($"Ruler: {new string('-', Math.Min(width - 2, 100))}");
            dump.WriteLine(plain);
            dump.WriteLine();

            // Pin invariants per frame so a regression that drops the action
            // or skips truncation fails this single test.
            plain.Length.Should().BeLessThanOrEqualTo(
                width - 2,
                because: $"the row must fit within (width-2)={width - 2} cols at terminal width {width}");

            // Path is 60 chars + 24-char padded label + chrome + action ≈ 105 cols,
            // so even width 100 still requires some truncation. Just assert
            // truncation fires at the problematic widths.
            if (width <= 80)
            {
                plain.Should().Contain("…",
                    because: $"at width {width} the long path must be middle-truncated");
            }

            plain.Should().Contain("[Enter] Change",
                because: $"the action label must remain intact at width {width}");
            plain.Should().Contain("Output folder",
                because: $"the row label must remain intact at width {width}");
        }

        _output.WriteLine($"Visual dump written to: {dumpPath}");
        File.Exists(dumpPath).Should().BeTrue();
    }
}
