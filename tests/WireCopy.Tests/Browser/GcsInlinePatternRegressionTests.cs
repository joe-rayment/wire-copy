// Licensed under the MIT License. See LICENSE in the repository root.

using System.IO;
using FluentAssertions;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Regression gate for workspace-ur5h: the GCS service-account row and the
/// GCS bucket row must use the same INLINE FormField pattern as the OpenAI
/// API key row, with no full-screen wizard chrome.
///
/// <para>
/// Concrete tripwires:
/// </para>
/// <list type="bullet">
/// <item><description>No <c>Console.Clear()</c> in <c>HandleSetKey</c> /
///   <c>HandleSetBucket</c> / <c>ShowPrerequisiteGateAsync</c> /
///   <c>RunVerifyFromSettingsAsync</c> — the four sites where the previous
///   fix (workspace-cgnt) blanked the screen.</description></item>
/// <item><description>No call to <c>RenderWizardStepHeader</c> from
///   <c>HandleSetKey</c> / <c>HandleSetBucket</c> / <c>RunVerifyFromSettingsAsync</c>
///   — the wizard-header card was the visual anchor that introduced the
///   nine-line vertical void the user reported.</description></item>
/// </list>
///
/// <para>
/// We assert against the source text rather than mocking Console because
/// these handlers are <c>private static</c> and the side-effect (a wiped
/// screen) is exactly what we forbid. Source-level inspection is the
/// minimum-friction tripwire that catches a regression at PR review time.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class GcsInlinePatternRegressionTests
{
    private const string HandlerFileName = "SettingsCommandHandler.cs";

    [Fact]
    public void HandleSetKey_DoesNotCallConsoleClear()
    {
        var body = ExtractMethodBody("HandleSetKey");
        body.Should().NotContain("Console.Clear()", "GCS service-account row must overlay inline like OpenAI");
        body.Should().NotContain("RenderWizardStepHeader", "no wizard-header card on the GCS rows");
    }

    [Fact]
    public void HandleSetBucket_DoesNotCallConsoleClear()
    {
        var body = ExtractMethodBody("HandleSetBucket");
        body.Should().NotContain("Console.Clear()", "GCS bucket row must overlay inline like OpenAI");
        body.Should().NotContain("RenderWizardStepHeader", "no wizard-header card on the GCS rows");
    }

    [Fact]
    public void ShowPrerequisiteGateAsync_DoesNotCallConsoleClear()
    {
        var body = ExtractMethodBody("ShowPrerequisiteGateAsync");
        body.Should().NotContain("Console.Clear()", "prerequisite gate must overlay inline, not wipe");
    }

    [Fact]
    public void RunVerifyFromSettingsAsync_DoesNotCallConsoleClear()
    {
        var body = ExtractMethodBody("RunVerifyFromSettingsAsync");
        body.Should().NotContain("Console.Clear()", "ad-hoc verify panel must overlay inline, not wipe");
        body.Should().NotContain("RenderWizardStepHeader", "verify rerun should not draw a wizard header");
    }

    [Fact]
    public void HandleSetKey_AndBucket_ShareTheOpenAiInlineLayout()
    {
        // The OpenAI API-key handler is the load-bearing baseline. It
        // computes startRow as a vertical-centre offset and overlays the
        // FormField on top of the live Settings rows. Both GCS handlers
        // must do the same — same shape, same anchor.
        var key = ExtractMethodBody("HandleSetKey");
        var bucket = ExtractMethodBody("HandleSetBucket");
        var openai = ExtractMethodBody("HandleSetApiKey");

        const string anchor = "Console.WindowHeight / 2";
        key.Should().Contain(anchor, "GCS service-account row must mirror OpenAI vertical-centre overlay");
        bucket.Should().Contain(anchor, "GCS bucket row must mirror OpenAI vertical-centre overlay");
        openai.Should().Contain(anchor, "OpenAI baseline sanity check");
    }

    private static string ExtractMethodBody(string methodName)
    {
        var source = ReadHandlerSource();

        // Find the method DECLARATION — must be preceded by access modifier
        // / return-type / async qualifiers, not a call site. We search for
        // a few common declaration prefixes to disambiguate from `case
        // SetupRow.Key` and similar non-method matches.
        var declarationMarkers = new[]
        {
            $"private static async Task<char?> {methodName}(",
            $"private static async Task {methodName}(",
            $"internal static async Task {methodName}(",
            $"static async Task<char?> {methodName}(",
            $"static async Task {methodName}(",
        };

        var idx = -1;
        foreach (var marker in declarationMarkers)
        {
            idx = source.IndexOf(marker, System.StringComparison.Ordinal);
            if (idx >= 0)
            {
                break;
            }
        }

        idx.Should().BeGreaterThan(0, $"{methodName} declaration should exist in the handler source");

        // Walk forward to the body's opening brace.
        var braceIdx = source.IndexOf('{', idx);
        braceIdx.Should().BeGreaterThan(0);

        // Match braces to find the close.
        var depth = 0;
        var i = braceIdx;
        while (i < source.Length)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }

            i++;
        }

        var endExclusive = Math.Min(source.Length, i + 1);
        return source[braceIdx..endExclusive];
    }

    private static string ReadHandlerSource()
    {
        // Walk up from the test binary location until we find the handler
        // source — works for `dotnet test`, `dotnet test --no-build`, and
        // direct VSTest runs.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(
                dir,
                "src",
                "WireCopy.Infrastructure",
                "Browser",
                "CommandHandlers",
                HandlerFileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException(
            $"Could not locate {HandlerFileName} relative to {AppContext.BaseDirectory}");
    }
}
