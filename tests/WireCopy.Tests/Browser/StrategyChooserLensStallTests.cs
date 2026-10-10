// Licensed under the MIT License. See LICENSE in the repository root.

// Guards:   label mode must still show its first card and accept keys when the browser's lens
//           lock is held by an abandoned page screenshot (the wizard used to freeze, leaving the
//           stale chooser card on screen).
// Fails if: a lens hook (arm click / highlight / clear / poll / disarm) is awaited without a
//           time bound, so one stuck GetLensPageAsync call hangs RunLabelModeAsync forever.
// Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~StrategyChooserLensStall"

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-6jk65 — the real <see cref="IBrowserSession"/> serialises
/// <c>GetLensPageAsync</c> and <c>CaptureScreenshotAsync</c> behind one lock, and
/// an abandoned (capped-out) screenshot keeps holding it. The session here is a
/// double whose lens/screenshot calls never complete, standing in for that held
/// lock; the wizard's lens built by <c>StrategyChooserHandler.BuildWizardLens</c>
/// must degrade to "no highlight" instead of hanging label mode.
/// </summary>
[Trait("Category", "Unit")]
public class StrategyChooserLensStallTests
{
    private static NavigationCommand Key(char c) => new() { Type = CommandType.NoOp, RawKeyChar = c };

    private static LinkInfo Story(string url, string text) => new()
    {
        Url = url,
        DisplayText = text,
        Type = LinkType.Content,
        ImportanceScore = 85,
        ParentSelector = "div.river a",
    };

    [Fact]
    public async Task LabelMode_DoesNotStall_WhenLensLockIsHeld()
    {
        var session = Substitute.For<IBrowserSession>();
        session.GetLensPageAsync().Returns(new TaskCompletionSource<IPage?>().Task);
        session.CaptureScreenshotAsync(Arg.Any<IReadOnlyList<ScreenshotMark>?>())
            .Returns(new TaskCompletionSource<byte[]?>().Task);

        var links = new List<LinkInfo>
        {
            Story("https://x.com/story/alpha", "Alpha headline about a long interesting thing"),
            Story("https://x.com/story/beta", "Beta headline about a long interesting thing"),
        };
        var config = new SiteHierarchyConfig
        {
            Domain = "x.com",
            UrlPattern = "^https?://x\\.com",
            Sections = new List<HierarchySection>
            {
                new() { Name = "Headlines", SortOrder = 0, UrlPatterns = new List<string> { "/story" } },
            },
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "test",
            Kind = LayoutKind.AiCurated,
            Version = 3,
        };

        var lens = StrategyChooserHandler.BuildWizardLens(session, links, NullLogger.Instance);
        lens.Should().NotBeNull("a session exists, so the wizard gets lens hooks");

        var input = Substitute.For<IInputHandler>();
        input.AnimationController.AnimationState.Returns(new AnimationState());
        input.WaitForInputAsync(Arg.Any<CancellationToken>()).Returns(
            Key('a'),
            new NavigationCommand { Type = CommandType.ActivateLink });

        var renders = 0;
        Task Render(CancellationToken _)
        {
            Interlocked.Increment(ref renders);
            return Task.CompletedTask;
        }

        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => SetupWizard.RunLabelModeAsync(
            input, Render, new SetupWizardOverlay.State(), links, config, lens, cts.Token));

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(6)));
        var completed = ReferenceEquals(finished, run);
        cts.Cancel();

        completed.Should().BeTrue("label mode must give up on a stuck lens call instead of freezing");
        var outcome = await run;
        outcome.Should().NotBeNull();
        outcome!.Labels.Should().ContainSingle(l =>
            l.Kind == LinkLabelKind.Article && l.Url == "https://x.com/story/alpha" && l.Rank == 1);
        renders.Should().BeGreaterThan(0, "the first label card must render");
    }
}
