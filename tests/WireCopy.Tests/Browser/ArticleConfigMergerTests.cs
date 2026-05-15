// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for the append-vs-replace decision in
/// <see cref="ArticleConfigMerger.Merge"/> (workspace-v6w3).
///
/// <para>
/// The MVP behaviour (workspace-xusy) replaced any entry that shared a Name
/// and appended otherwise. That loses information when the AI returns the
/// same Name for genuinely different page shapes — e.g. NYT live-blog and
/// NYT article both called "article". The merger now replaces only when
/// Name AND matcher-shape collide; on name-only collisions it appends a
/// renamed entry ("article-2", …) so the unique-name invariant holds.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ArticleConfigMergerTests
{
    private static ArticleSelectorConfig Single(string name, PageTypeMatcher matcher) =>
        new()
        {
            Domain = "example.com",
            PageTypes = new()
            {
                new PageTypeEntry { Name = name, Matcher = matcher },
            },
        };

    [Fact]
    public void Merge_NullExisting_ReturnsFresh()
    {
        var fresh = Single("article", new PageTypeMatcher());
        ArticleConfigMerger.Merge(null, fresh).Should().BeSameAs(fresh);
    }

    [Fact]
    public void Merge_EmptyExisting_ReturnsFresh()
    {
        var existing = new ArticleSelectorConfig { Domain = "example.com" };
        var fresh = Single("article", new PageTypeMatcher());
        ArticleConfigMerger.Merge(existing, fresh).Should().BeSameAs(fresh);
    }

    [Fact]
    public void Merge_SameNameSameMatcher_ReplacesExistingEntry()
    {
        var existingEntry = new PageTypeEntry
        {
            Name = "article",
            Matcher = new PageTypeMatcher { LdJsonType = "NewsArticle" },
            Selectors = new ArticleSelectors { Headline = new() { "h1.old" } },
        };
        var existing = new ArticleSelectorConfig
        {
            Domain = "example.com",
            PageTypes = new() { existingEntry },
        };

        var freshEntry = new PageTypeEntry
        {
            Name = "article",
            Matcher = new PageTypeMatcher { LdJsonType = "NewsArticle" },
            Selectors = new ArticleSelectors { Headline = new() { "h1.new" } },
        };
        var fresh = new ArticleSelectorConfig
        {
            Domain = "example.com",
            PageTypes = new() { freshEntry },
        };

        var merged = ArticleConfigMerger.Merge(existing, fresh);

        merged.PageTypes.Should().HaveCount(1);
        merged.PageTypes[0].Selectors.Headline.Should().Contain("h1.new");
        merged.PageTypes[0].Selectors.Headline.Should().NotContain("h1.old");
    }

    [Fact]
    public void Merge_SameNameDifferentLdJsonType_AppendsWithRenamedEntry()
    {
        // Two NYT page types both called "article" by the AI, but one is
        // NewsArticle and one is LiveBlogPosting → keep both.
        var existing = Single("article", new PageTypeMatcher { LdJsonType = "NewsArticle" });
        var fresh = Single("article", new PageTypeMatcher { LdJsonType = "LiveBlogPosting" });

        var merged = ArticleConfigMerger.Merge(existing, fresh);

        merged.PageTypes.Should().HaveCount(2);
        merged.PageTypes[0].Name.Should().Be("article");
        merged.PageTypes[0].Matcher.LdJsonType.Should().Be("NewsArticle");
        merged.PageTypes[1].Name.Should().Be("article-2");
        merged.PageTypes[1].Matcher.LdJsonType.Should().Be("LiveBlogPosting");
    }

    [Fact]
    public void Merge_SameNameDifferentUrlPattern_AppendsWithRenamedEntry()
    {
        var existing = Single("article", new PageTypeMatcher { UrlPattern = "/news/" });
        var fresh = Single("article", new PageTypeMatcher { UrlPattern = "/opinion/" });

        var merged = ArticleConfigMerger.Merge(existing, fresh);

        merged.PageTypes.Should().HaveCount(2);
        merged.PageTypes[1].Name.Should().Be("article-2");
    }

    [Fact]
    public void Merge_NewName_AppendsAsExtraEntry()
    {
        var existing = Single("article", new PageTypeMatcher());
        var fresh = Single("recipe", new PageTypeMatcher { LdJsonType = "Recipe" });

        var merged = ArticleConfigMerger.Merge(existing, fresh);

        merged.PageTypes.Should().HaveCount(2);
        merged.PageTypes[1].Name.Should().Be("recipe");
    }

    [Fact]
    public void MatcherShapeMatches_BothEmpty_ReturnsTrue()
    {
        ArticleConfigMerger.MatcherShapeMatches(new PageTypeMatcher(), new PageTypeMatcher())
            .Should().BeTrue();
    }

    [Fact]
    public void MatcherShapeMatches_DifferentLdJsonType_ReturnsFalse()
    {
        var a = new PageTypeMatcher { LdJsonType = "NewsArticle" };
        var b = new PageTypeMatcher { LdJsonType = "LiveBlogPosting" };
        ArticleConfigMerger.MatcherShapeMatches(a, b).Should().BeFalse();
    }

    [Fact]
    public void MatcherShapeMatches_OneNullOneEmpty_TreatedAsEqual()
    {
        var a = new PageTypeMatcher();
        var b = new PageTypeMatcher { LdJsonType = null };
        ArticleConfigMerger.MatcherShapeMatches(a, b).Should().BeTrue();
    }

    [Fact]
    public void MakeUniqueName_NoCollision_ReturnsCandidateAsIs()
    {
        var existing = new[] { new PageTypeEntry { Name = "article" } };
        ArticleConfigMerger.MakeUniqueName("recipe", existing).Should().Be("recipe");
    }

    [Fact]
    public void MakeUniqueName_SingleCollision_AppendsTwo()
    {
        var existing = new[] { new PageTypeEntry { Name = "article" } };
        ArticleConfigMerger.MakeUniqueName("article", existing).Should().Be("article-2");
    }

    [Fact]
    public void MakeUniqueName_MultipleCollisions_AppendsIncrementing()
    {
        var existing = new[]
        {
            new PageTypeEntry { Name = "article" },
            new PageTypeEntry { Name = "article-2" },
            new PageTypeEntry { Name = "article-3" },
        };
        ArticleConfigMerger.MakeUniqueName("article", existing).Should().Be("article-4");
    }
}
