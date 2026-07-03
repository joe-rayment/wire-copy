// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-4z4f.2: podcast generation was invisible until deep inside the
/// Collections view. The first time a NON-EMPTY collection is opened in a
/// session, a one-shot transient announcement teaches the `p` key. The nudge
/// must never fire for empty collections (non-actionable) and must not repeat.
/// </summary>
[Trait("Category", "Unit")]
public class PodcastNudgeTests
{
    private readonly NavigationService _navService;

    public PodcastNudgeTests()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        _navService = new NavigationService(logger);
    }

    private static Collection CreateCollectionWithItems(string name, int itemCount)
    {
        var collection = Collection.Create(name);
        for (var i = 0; i < itemCount; i++)
        {
            collection.AddItem($"https://example.com/{i}", $"Article {i}");
        }

        return collection;
    }

    [Fact]
    public void EnterCollection_NonEmpty_FirstTime_AnnouncesPodcastNudge()
    {
        var collection = CreateCollectionWithItems("Reading List", 3);

        _navService.EnterCollections();
        _navService.EnterCollection(collection);

        var context = _navService.CurrentContext;
        context.StatusMessage.Should().Be("p: make a podcast from this list",
            "opening a non-empty collection for the first time must teach the podcast key");
        context.ActiveAnnouncement.Should().NotBeNull();
        context.ActiveAnnouncement!.Glyph.Should().Be("🎧");
    }

    [Fact]
    public void EnterCollection_Empty_DoesNotNudge()
    {
        var collection = Collection.Create("Reading List");

        _navService.EnterCollections();
        _navService.EnterCollection(collection);

        _navService.CurrentContext.StatusMessage.Should().BeNull(
            "the nudge must never advertise a non-actionable feature on an empty collection");
    }

    [Fact]
    public void EnterCollection_SecondTime_DoesNotRepeatNudge()
    {
        var collection = CreateCollectionWithItems("Reading List", 2);

        _navService.EnterCollections();
        _navService.EnterCollection(collection);
        _navService.ClearStatusMessage();

        _navService.ExitToCollectionList();
        _navService.EnterCollection(collection);

        _navService.CurrentContext.StatusMessage.Should().BeNull(
            "the nudge is a one-shot per session — repeats would be nagging");
    }

    [Fact]
    public void EnterCollection_EmptyThenNonEmpty_NudgesOnTheNonEmptyOpen()
    {
        // An empty open must not consume the one-shot: the nudge should still
        // fire later when a collection with items is opened.
        var empty = Collection.Create("Empty");
        var populated = CreateCollectionWithItems("Reading List", 1);

        _navService.EnterCollections();
        _navService.EnterCollection(empty);
        _navService.CurrentContext.StatusMessage.Should().BeNull();

        _navService.ExitToCollectionList();
        _navService.EnterCollection(populated);

        _navService.CurrentContext.StatusMessage.Should().Be("p: make a podcast from this list");
    }
}
