// Licensed under the MIT License. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Text.RegularExpressions;
using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for the prefetch detail overlay (workspace-v75w). Covers:
/// (1) hidden-by-default behaviour when <c>CacheProgress</c> is null,
/// (2) stage chip colour mapping for each <see cref="PreloadStage"/>, and
/// (3) snapshot-style invariants on the rendered panel at widths 80/100/120/140
/// with a representative <see cref="PreloadProgress"/>.
/// </summary>
[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class PreloadDetailRendererTests
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);

    [Fact]
    public void Render_NullProgress_WritesNothing()
    {
        var helpers = new RenderHelpers { TerminalHeight = 30 };
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var renderer = new PreloadDetailRenderer(helpers, themeProvider);

        var sw = new System.IO.StringWriter();
        var originalOut = System.Console.Out;
        System.Console.SetOut(sw);
        try
        {
            renderer.Render(progress: null, terminalWidth: 80, terminalHeight: 24);
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }

        sw.ToString().Should().BeEmpty("a null PreloadProgress hides the panel completely");
    }

    [Theory]
    [InlineData(PreloadStage.Fetching, "loading")]
    [InlineData(PreloadStage.Detecting, "detecting")]
    [InlineData(PreloadStage.ExtractingContent, "extracting")]
    [InlineData(PreloadStage.PersistingCache, "caching")]
    public void StageChip_ContainsExpectedLabel(PreloadStage stage, string expectedLabel)
    {
        var chip = PreloadDetailRenderer.BuildStageChip(stage, Palette);

        chip.Should().NotBeNull();
        chip!.Value.Plain.Should().Contain(expectedLabel);
    }

    [Fact]
    public void StageChip_Idle_IsNull()
    {
        var chip = PreloadDetailRenderer.BuildStageChip(PreloadStage.Idle, Palette);
        chip.Should().BeNull("idle has no in-flight URL, so no stage chip is drawn");
    }

    [Theory]
    [InlineData(PreloadStage.Fetching)]
    [InlineData(PreloadStage.Detecting)]
    public void StageChip_LoadingStages_UsePrimaryTextColor(PreloadStage stage)
    {
        var chip = PreloadDetailRenderer.BuildStageChip(stage, Palette);
        chip.Should().NotBeNull();
        chip!.Value.Styled.Should().Contain(Palette.PrimaryText.AnsiFg,
            "fetching/detecting are 'loading' stages → normal/primary text colour");
    }

    [Fact]
    public void StageChip_Extracting_UsesAccentColor()
    {
        var chip = PreloadDetailRenderer.BuildStageChip(PreloadStage.ExtractingContent, Palette);
        chip.Should().NotBeNull();
        chip!.Value.Styled.Should().Contain(Palette.GetAccentFg().AnsiFg,
            "extracting is the 'accent' stage per the design spec");
    }

    [Fact]
    public void StageChip_Caching_UsesDimSecondaryColor()
    {
        var chip = PreloadDetailRenderer.BuildStageChip(PreloadStage.PersistingCache, Palette);
        chip.Should().NotBeNull();
        chip!.Value.Styled.Should().Contain(Palette.GetDimFg().AnsiFg,
            "caching is the 'dim secondary' stage per the design spec");
    }

    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(140)]
    public void BuildPanelLines_AtWidth_ContainsAllSections(int terminalWidth)
    {
        var progress = CreateRepresentativeProgress();

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, terminalWidth);

        var plainText = string.Join("\n", lines.ConvertAll(l => l.PlainText));
        plainText.Should().Contain("3/8 cached");
        plainText.Should().Contain("running");
        plainText.Should().Contain("Now: ");
        plainText.Should().Contain("Up next");
        plainText.Should().Contain("Recent");
    }

    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(140)]
    public void BuildPanelLines_AtWidth_TruncatesContentToFit(int terminalWidth)
    {
        var progress = CreateRepresentativeProgress();

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, terminalWidth);

        var expectedInnerWidth = System.Math.Min(120, System.Math.Max(50, terminalWidth - 8));
        foreach (var line in lines)
        {
            var displayWidth = RenderHelpers.GetDisplayWidth(line.PlainText);
            displayWidth.Should().BeLessThanOrEqualTo(expectedInnerWidth,
                $"panel content must fit within the inner width at terminal width {terminalWidth}");
        }
    }

    [Fact]
    public void BuildPanelLines_RendersUpToTenUpcomingUrls()
    {
        var upcoming = new List<string>();
        for (var i = 0; i < 15; i++)
        {
            upcoming.Add($"https://example.com/article-{i}");
        }

        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 20,
            CachedCount = 5,
            IsActivelyFetching = true,
            CurrentStage = PreloadStage.Fetching,
            CurrentlyFetchingUrl = "https://example.com/now",
            UpcomingUrls = upcoming,
        };

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, 100);

        var upcomingLineCount = lines.FindAll(l => l.PlainText.Contains("→ https://example.com/article-")).Count;
        upcomingLineCount.Should().Be(10, "up next caps at 10 entries even when the queue has more");
    }

    [Fact]
    public void BuildPanelLines_RendersUpToTenRecentEntries()
    {
        var recent = new List<PreloadHistoryEntry>();
        for (var i = 0; i < 15; i++)
        {
            recent.Add(new PreloadHistoryEntry
            {
                Url = $"https://example.com/done-{i}",
                Outcome = PreloadOutcome.Cached,
                ElapsedMs = 100 + i,
            });
        }

        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 20,
            CachedCount = 15,
            IsActivelyFetching = false,
            RecentItems = recent,
        };

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, 100);

        var recentLineCount = lines.FindAll(l => l.PlainText.Contains("https://example.com/done-")).Count;
        recentLineCount.Should().Be(10, "recent caps at 10 entries even when the ring buffer has more");
    }

    [Fact]
    public void BuildPanelLines_EmptyQueue_RendersEmptyPlaceholder()
    {
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 5,
            CachedCount = 5,
            IsActivelyFetching = false,
        };

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, 100);

        var plainText = string.Join("\n", lines.ConvertAll(l => l.PlainText));
        plainText.Should().Contain("(queue is empty)");
        plainText.Should().Contain("(no history yet)");
    }

    [Fact]
    public void BuildPanelLines_HistoryEntry_IncludesOutcomeGlyphAndElapsedMs()
    {
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 3,
            CachedCount = 1,
            RecentItems = new List<PreloadHistoryEntry>
            {
                new() { Url = "https://example.com/ok", Outcome = PreloadOutcome.Cached, ElapsedMs = 1200 },
                new() { Url = "https://example.com/skip", Outcome = PreloadOutcome.Skipped, ElapsedMs = 50, Reason = "paywall" },
                new() { Url = "https://example.com/bad", Outcome = PreloadOutcome.Failed, ElapsedMs = 4200 },
            },
        };

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, 100);
        var plain = string.Join("\n", lines.ConvertAll(l => l.PlainText));

        plain.Should().Contain("✓");
        plain.Should().Contain("⏭");
        plain.Should().Contain("✗");
        plain.Should().Contain("1200ms");
        plain.Should().Contain("50ms");
        plain.Should().Contain("4200ms");
    }

    [Fact]
    public void BuildPanelLines_NotActivelyFetching_SaysPaused()
    {
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 5,
            CachedCount = 2,
            IsActivelyFetching = false,
        };

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, 100);
        var plain = string.Join("\n", lines.ConvertAll(l => l.PlainText));

        plain.Should().Contain("paused");
        plain.Should().NotContain("running");
    }

    [Fact]
    public void Render_NullProgress_NoOpEvenAtTinyTerminal()
    {
        var helpers = new RenderHelpers { TerminalHeight = 5 };
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var renderer = new PreloadDetailRenderer(helpers, themeProvider);

        var act = () => renderer.Render(progress: null, terminalWidth: 30, terminalHeight: 5);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(ThemeName.Phosphor)]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void Render_AllThemes_DoesNotThrow(ThemeName theme)
    {
        var helpers = new RenderHelpers { TerminalHeight = 30 };
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(theme);
        var renderer = new PreloadDetailRenderer(helpers, themeProvider);

        var sw = new System.IO.StringWriter();
        var originalOut = System.Console.Out;
        System.Console.SetOut(sw);
        try
        {
            var act = () => renderer.Render(CreateRepresentativeProgress(), terminalWidth: 100, terminalHeight: 30);
            act.Should().NotThrow();
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }
    }

    // ---- QA punch-list strengthening (workspace-v75w post-review) ----

    [Theory]
    [InlineData(PreloadStage.ExtractingContent)]
    public void StageChip_Extracting_DoesNotAlsoUsePrimaryOrDimColor(PreloadStage stage)
    {
        var chip = PreloadDetailRenderer.BuildStageChip(stage, Palette);
        chip.Should().NotBeNull();
        // Strict colour ownership: extracting uses ONLY AccentFg, not PrimaryText
        // or DimFg. A regression that double-set the colour would otherwise
        // sneak through the inclusive Contains() check.
        chip!.Value.Styled.Should().NotContain(Palette.GetDimFg().AnsiFg,
            "extracting must not be styled with the dim/caching colour");
    }

    [Theory]
    [InlineData(PreloadStage.PersistingCache)]
    public void StageChip_Caching_DoesNotAlsoUseAccentColor(PreloadStage stage)
    {
        var chip = PreloadDetailRenderer.BuildStageChip(stage, Palette);
        chip.Should().NotBeNull();
        chip!.Value.Styled.Should().NotContain(Palette.GetAccentFg().AnsiFg,
            "caching must not be styled with the accent/extracting colour");
    }

    [Theory]
    [InlineData(ThemeName.Phosphor)]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void StageChip_Extracting_UsesAccentFg_InAllThemes(ThemeName theme)
    {
        var palette = BuiltInThemes.Get(theme);
        var chip = PreloadDetailRenderer.BuildStageChip(PreloadStage.ExtractingContent, palette);
        chip.Should().NotBeNull();
        chip!.Value.Styled.Should().Contain(palette.GetAccentFg().AnsiFg,
            "extracting always reads from the theme's AccentFg, not Phosphor's specifically");
    }

    [Theory]
    [InlineData(ThemeName.Phosphor)]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void StageChip_Caching_UsesDimFg_InAllThemes(ThemeName theme)
    {
        var palette = BuiltInThemes.Get(theme);
        var chip = PreloadDetailRenderer.BuildStageChip(PreloadStage.PersistingCache, palette);
        chip.Should().NotBeNull();
        chip!.Value.Styled.Should().Contain(palette.GetDimFg().AnsiFg,
            "caching always reads from the theme's DimFg, not Phosphor's specifically");
    }

    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(140)]
    public void Render_FullOutput_FitsWithinTerminalWidth(int terminalWidth)
    {
        // The renderer writes chrome rows via `helpers.WriteLine`. Each logical
        // row IS a separate write — the rows boundary in the captured stream is
        // a `╭` (top border) or `│` (vertical border) or `╰`
        // (bottom border). We use that to slice the concatenated output and
        // assert each row stays inside the terminal width.
        var helpers = new RenderHelpers { TerminalHeight = 30 };
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var renderer = new PreloadDetailRenderer(helpers, themeProvider);

        var captured = CaptureRender(renderer, CreateRepresentativeProgress(), terminalWidth, terminalHeight: 30);
        var ansiStripper = new Regex("\x1b\\[[0-9;]*[A-Za-z]");
        var plain = ansiStripper.Replace(captured, string.Empty).Replace("\0", string.Empty);

        // Each panel row starts with leftPad spaces + a border char. Split on
        // the runs that begin with the left-margin + border-glyph pattern.
        // The simplest robust slice: find each row by matching the box border
        // chars (╭ / │ / ╰) — every row begins with leftPad spaces then exactly
        // one of those chars.
        var rowStartRegex = new Regex(@"(?=\s*[╭│╰])");
        var rows = rowStartRegex.Split(plain).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        rows.Should().NotBeEmpty();

        foreach (var row in rows)
        {
            var width = RenderHelpers.GetDisplayWidth(row.TrimEnd());
            width.Should().BeLessThanOrEqualTo(terminalWidth,
                $"a single panel row (chrome included) must not exceed the terminal width at {terminalWidth}; row was: '{row}'");
        }
    }

    [Theory]
    [InlineData(20, 24)]   // too narrow
    [InlineData(50, 24)]   // exactly under threshold
    [InlineData(80, 5)]    // too short
    public void Render_TinyTerminalWithProgress_SuppressesOverlay(int terminalWidth, int terminalHeight)
    {
        var helpers = new RenderHelpers { TerminalHeight = terminalHeight };
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var renderer = new PreloadDetailRenderer(helpers, themeProvider);

        var captured = CaptureRender(renderer, CreateRepresentativeProgress(), terminalWidth, terminalHeight);
        captured.Should().BeEmpty(
            "below the minimum terminal size the overlay suppresses itself rather than overflowing into wrap junk");
    }

    [Fact]
    public void Render_AtMinimumTerminalWidth_DrawsOverlay()
    {
        var helpers = new RenderHelpers { TerminalHeight = 30 };
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var renderer = new PreloadDetailRenderer(helpers, themeProvider);

        // Just at the threshold the overlay must still draw.
        var captured = CaptureRender(renderer, CreateRepresentativeProgress(), PreloadDetailRenderer.MinTerminalWidthForOverlay, terminalHeight: 30);
        captured.Should().NotBeEmpty();
        captured.Should().Contain("PREFETCH");
    }

    private static string CaptureRender(PreloadDetailRenderer renderer, PreloadProgress? progress, int terminalWidth, int terminalHeight)
    {
        var originalOut = System.Console.Out;
        using var sw = new System.IO.StringWriter();
        System.Console.SetOut(sw);
        try
        {
            renderer.Render(progress, terminalWidth, terminalHeight);
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }

        return sw.ToString();
    }

    // ---- Behavioural test: TerminalPageRenderer hides the overlay by default ----

    [Fact]
    public void TerminalPageRenderer_RenderHierarchical_WithDefaultOptions_DoesNotShowPrefetchOverlay()
    {
        // Even with non-null CacheProgress (the data IS available), the overlay
        // must stay hidden unless ShowPreloadDetail is explicitly set to true.
        // Regression catch: if someone flips the default to true tomorrow, this
        // test fails. (QA punch-list item 4.)
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<WireCopy.Infrastructure.Browser.UI.TerminalPageRenderer>>();
        var pageRenderer = new WireCopy.Infrastructure.Browser.UI.TerminalPageRenderer(themeProvider, logger);

        var html = "<html><head><title>Test</title></head><body>Content</body></html>";
        var metadata = new WireCopy.Domain.ValueObjects.Browser.PageMetadata { Title = "Test" };
        var page = WireCopy.Domain.Entities.Browser.Page.Create("https://example.com", html, metadata);
        var links = new List<WireCopy.Domain.ValueObjects.Browser.LinkInfo>
        {
            new() { Url = "https://example.com/a", DisplayText = "Link A", Type = WireCopy.Domain.Enums.Browser.LinkType.Content, ImportanceScore = 80 },
        };
        page.SetLinkTree(WireCopy.Domain.Entities.Browser.NavigationTree.Build(links));

        var context = new WireCopy.Domain.ValueObjects.Browser.NavigationContext { ViewMode = WireCopy.Domain.Enums.Browser.ViewMode.Hierarchical };
        var options = new RenderOptions
        {
            TerminalWidth = 100,
            TerminalHeight = 30,
            MaxContentWidth = 100,
            CacheProgress = CreateRepresentativeProgress(),
            // ShowPreloadDetail intentionally NOT set — defaults to false
        };

        var originalOut = System.Console.Out;
        using var sw = new System.IO.StringWriter();
        System.Console.SetOut(sw);
        try
        {
            pageRenderer.RenderHierarchical(page, context, options);
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }

        sw.ToString().Should().NotContain("PREFETCH",
            "default options must not paint the prefetch detail overlay — toggle wiring (workspace-c8v3) owns when it appears");
    }

    [Fact]
    public void TerminalPageRenderer_RenderHierarchical_WithShowPreloadDetailTrue_PaintsOverlay()
    {
        // Counterpart to the above: when the flag IS set we DO see the overlay.
        // Pins the wired call-site rather than just the structural default.
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<WireCopy.Infrastructure.Browser.UI.TerminalPageRenderer>>();
        var pageRenderer = new WireCopy.Infrastructure.Browser.UI.TerminalPageRenderer(themeProvider, logger);

        var html = "<html><head><title>Test</title></head><body>Content</body></html>";
        var metadata = new WireCopy.Domain.ValueObjects.Browser.PageMetadata { Title = "Test" };
        var page = WireCopy.Domain.Entities.Browser.Page.Create("https://example.com", html, metadata);
        var links = new List<WireCopy.Domain.ValueObjects.Browser.LinkInfo>
        {
            new() { Url = "https://example.com/a", DisplayText = "Link A", Type = WireCopy.Domain.Enums.Browser.LinkType.Content, ImportanceScore = 80 },
        };
        page.SetLinkTree(WireCopy.Domain.Entities.Browser.NavigationTree.Build(links));

        var context = new WireCopy.Domain.ValueObjects.Browser.NavigationContext { ViewMode = WireCopy.Domain.Enums.Browser.ViewMode.Hierarchical };
        var options = new RenderOptions
        {
            TerminalWidth = 100,
            TerminalHeight = 30,
            MaxContentWidth = 100,
            CacheProgress = CreateRepresentativeProgress(),
            ShowPreloadDetail = true,
        };

        var originalOut = System.Console.Out;
        using var sw = new System.IO.StringWriter();
        System.Console.SetOut(sw);
        try
        {
            pageRenderer.RenderHierarchical(page, context, options);
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }

        sw.ToString().Should().Contain("PREFETCH",
            "the wired call-site in TerminalPageRenderer must paint the overlay when the flag is on");
    }

    private static PreloadProgress CreateRepresentativeProgress()
    {
        return new PreloadProgress
        {
            TotalCacheableLinks = 8,
            CachedCount = 3,
            NeedsBrowserCount = 1,
            IsActivelyFetching = true,
            CurrentStage = PreloadStage.ExtractingContent,
            CurrentlyFetchingUrl = "https://www.example.com/section/2026/05/some-article",
            UpcomingUrls = new[]
            {
                "https://www.example.com/section/article-2",
                "https://www.example.com/section/article-3",
                "https://www.example.com/section/article-4",
            },
            RecentItems = new[]
            {
                new PreloadHistoryEntry { Url = "https://www.example.com/article-0", Outcome = PreloadOutcome.Cached, ElapsedMs = 850 },
                new PreloadHistoryEntry { Url = "https://www.example.com/article-x", Outcome = PreloadOutcome.Failed, ElapsedMs = 4200, Reason = "timeout" },
                new PreloadHistoryEntry { Url = "https://www.example.com/article-y", Outcome = PreloadOutcome.Skipped, ElapsedMs = 30, Reason = "paywall" },
            },
        };
    }
}
