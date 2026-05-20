// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-i3kh — the progress screen must append "· {elapsed}" to each
/// finished article line so the user can see how long each step took. This
/// is one of the parent workspace-rz1c acceptance items.
/// </summary>
[Trait("Category", "Unit")]
public class PerArticleElapsedTests
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);

    [Fact]
    public void FormatElapsedSuffix_SubMinute_RendersSeconds()
    {
        var status = new PodcastCommandHandler.ArticleStatus
        {
            Title = "Article 1",
            State = PodcastCommandHandler.ArticleState.Completed,
            StartedAtUtc = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
            FinishedAtUtc = new DateTime(2026, 5, 20, 12, 0, 32, DateTimeKind.Utc),
        };

        PodcastProgressScreens.FormatElapsedSuffix(status).Should().Be("32s");
    }

    [Fact]
    public void FormatElapsedSuffix_OneMinuteFourteenSeconds_RendersMinutesAndSeconds()
    {
        var status = new PodcastCommandHandler.ArticleStatus
        {
            Title = "Article 1",
            State = PodcastCommandHandler.ArticleState.Completed,
            StartedAtUtc = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
            FinishedAtUtc = new DateTime(2026, 5, 20, 12, 1, 14, DateTimeKind.Utc),
        };

        PodcastProgressScreens.FormatElapsedSuffix(status).Should().Be("1m 14s");
    }

    [Fact]
    public void FormatElapsedSuffix_ExactMinutes_OmitsSeconds()
    {
        var status = new PodcastCommandHandler.ArticleStatus
        {
            Title = "Article 1",
            State = PodcastCommandHandler.ArticleState.Completed,
            StartedAtUtc = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
            FinishedAtUtc = new DateTime(2026, 5, 20, 12, 3, 0, DateTimeKind.Utc),
        };

        PodcastProgressScreens.FormatElapsedSuffix(status).Should().Be("3m");
    }

    [Fact]
    public void FormatElapsedSuffix_NoStartTime_ReturnsEmpty()
    {
        // While Pending or before the start-timestamp was captured, no
        // elapsed should render — empty string keeps the line clean.
        var status = new PodcastCommandHandler.ArticleStatus
        {
            Title = "Article 1",
            State = PodcastCommandHandler.ArticleState.Pending,
        };

        PodcastProgressScreens.FormatElapsedSuffix(status).Should().Be(string.Empty);
    }

    [Fact]
    public void FormatElapsedSuffix_StartButNoFinish_ReturnsEmpty()
    {
        // Article is still in flight — no elapsed yet.
        var status = new PodcastCommandHandler.ArticleStatus
        {
            Title = "Article 1",
            State = PodcastCommandHandler.ArticleState.Processing,
            StartedAtUtc = DateTime.UtcNow,
        };

        PodcastProgressScreens.FormatElapsedSuffix(status).Should().Be(string.Empty);
    }

    [Fact]
    public void FormatElapsedSuffix_NegativeElapsed_ReturnsEmpty()
    {
        // Defensive: clock skew or a misordered timestamp pair shouldn't
        // render "-5s" — empty is the honest answer.
        var status = new PodcastCommandHandler.ArticleStatus
        {
            Title = "Article 1",
            State = PodcastCommandHandler.ArticleState.Completed,
            StartedAtUtc = new DateTime(2026, 5, 20, 12, 0, 10, DateTimeKind.Utc),
            FinishedAtUtc = new DateTime(2026, 5, 20, 12, 0, 5, DateTimeKind.Utc),
        };

        PodcastProgressScreens.FormatElapsedSuffix(status).Should().Be(string.Empty);
    }

    /// <summary>
    /// End-to-end render check: the per-article line for a completed article
    /// must contain the "· {elapsed}" suffix as a single visible string.
    /// </summary>
    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void RenderProgressContent_CompletedArticle_ShowsElapsedSuffix()
    {
        var statuses = new[]
        {
            new PodcastCommandHandler.ArticleStatus
            {
                Title = "Completed in 47 seconds",
                State = PodcastCommandHandler.ArticleState.Completed,
                StartedAtUtc = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
                FinishedAtUtc = new DateTime(2026, 5, 20, 12, 0, 47, DateTimeKind.Utc),
            },
        };

        var output = CaptureRender(h => PodcastProgressScreens.RenderProgressContent(
            h, Palette,
            new PodcastProgress { Phase = PodcastPhase.GeneratingAudio, CurrentArticle = 1, TotalArticles = 1 },
            animFrame: 0, statuses, terminalWidth: 100, terminalHeight: 40));

        output.Should().Contain("· 47s",
            "the elapsed suffix MUST land on the completed article line — the bead's user-visible acceptance");
    }

    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void RenderProgressContent_ProcessingArticle_DoesNotShowElapsedSuffix()
    {
        var statuses = new[]
        {
            new PodcastCommandHandler.ArticleStatus
            {
                Title = "Still in flight",
                State = PodcastCommandHandler.ArticleState.Processing,
                StartedAtUtc = DateTime.UtcNow,
            },
        };

        var output = CaptureRender(h => PodcastProgressScreens.RenderProgressContent(
            h, Palette,
            new PodcastProgress { Phase = PodcastPhase.GeneratingAudio, CurrentArticle = 1, TotalArticles = 1 },
            animFrame: 0, statuses, terminalWidth: 100, terminalHeight: 40));

        output.Should().NotContain("· ",
            "an in-flight article must NOT advertise an elapsed suffix — only finished work gets a duration");
    }

    private static string CaptureRender(Action<RenderHelpers> action, int terminalHeight = 30)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var helpers = new RenderHelpers { TerminalHeight = terminalHeight };
            helpers.Clear();
            action(helpers);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return sw.ToString();
    }
}
