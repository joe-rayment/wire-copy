// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-6dzj — the cancelled status message must tell the user how many
/// articles they got out of the run, not just "Podcast cancelled". Pins the
/// counting + pluralisation logic in
/// <see cref="PodcastProgressScreens.BuildCancelledStatusMessage"/>.
/// </summary>
[Trait("Category", "Unit")]
public class CancelledStatusMessageTests
{
    [Fact]
    public void Zero_Articles_Completed_RendersPluralWithZero()
    {
        var statuses = new[]
        {
            new PodcastCommandHandler.ArticleStatus { Title = "A", State = PodcastCommandHandler.ArticleState.Pending },
            new PodcastCommandHandler.ArticleStatus { Title = "B", State = PodcastCommandHandler.ArticleState.Pending },
        };

        var msg = PodcastProgressScreens.BuildCancelledStatusMessage(statuses);

        msg.Should().Be("Cancelled — 0 articles completed",
            "we still tell the user something even when nothing finished — silence reads as a freeze");
    }

    [Fact]
    public void One_Article_Completed_UsesSingular()
    {
        var statuses = new[]
        {
            new PodcastCommandHandler.ArticleStatus { Title = "A", State = PodcastCommandHandler.ArticleState.Completed },
            new PodcastCommandHandler.ArticleStatus { Title = "B", State = PodcastCommandHandler.ArticleState.Processing },
            new PodcastCommandHandler.ArticleStatus { Title = "C", State = PodcastCommandHandler.ArticleState.Pending },
        };

        var msg = PodcastProgressScreens.BuildCancelledStatusMessage(statuses);

        msg.Should().Be("Cancelled — 1 article completed",
            "singular form when exactly one article completed");
    }

    [Fact]
    public void Cached_Counts_As_Completed()
    {
        // workspace-6dzj: a cache hit is real value to the user — the cached
        // audio is still in the M4B-to-be. Count Cached articles as completed
        // so the message reflects what the user actually got.
        var statuses = new[]
        {
            new PodcastCommandHandler.ArticleStatus { Title = "A", State = PodcastCommandHandler.ArticleState.Cached },
            new PodcastCommandHandler.ArticleStatus { Title = "B", State = PodcastCommandHandler.ArticleState.Completed },
            new PodcastCommandHandler.ArticleStatus { Title = "C", State = PodcastCommandHandler.ArticleState.Processing },
        };

        var msg = PodcastProgressScreens.BuildCancelledStatusMessage(statuses);

        msg.Should().Be("Cancelled — 2 articles completed",
            "Cached must count alongside Completed — the cache hit is real partial progress");
    }

    [Fact]
    public void Failed_And_Processing_Do_Not_Count()
    {
        var statuses = new[]
        {
            new PodcastCommandHandler.ArticleStatus { Title = "A", State = PodcastCommandHandler.ArticleState.Completed },
            new PodcastCommandHandler.ArticleStatus { Title = "B", State = PodcastCommandHandler.ArticleState.Failed },
            new PodcastCommandHandler.ArticleStatus { Title = "C", State = PodcastCommandHandler.ArticleState.Processing },
            new PodcastCommandHandler.ArticleStatus { Title = "D", State = PodcastCommandHandler.ArticleState.Pending },
        };

        var msg = PodcastProgressScreens.BuildCancelledStatusMessage(statuses);

        msg.Should().Be("Cancelled — 1 article completed",
            "Failed/Processing/Pending must NOT count — only finished work the user can use");
    }

    [Fact]
    public void EmptyStatuses_RendersZero()
    {
        // Defensive: if the run was cancelled before any per-article state
        // was tracked (e.g. during the cache-analysis pre-phase), the
        // status array could be empty. Don't crash.
        var msg = PodcastProgressScreens.BuildCancelledStatusMessage(Array.Empty<PodcastCommandHandler.ArticleStatus>());

        msg.Should().Be("Cancelled — 0 articles completed");
    }
}
