// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.UI;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-romy (epic) — wizard v3: geometry signals, Set-of-Marks grounding,
/// sponsor flags, ordering sanity self-test, input scroll window, and the
/// ignorable preview confirm question.
/// </summary>
[Trait("Category", "Unit")]
public class WizardV3Tests
{
    private static LinkInfo Content(
        string url,
        string text,
        int score = 70,
        string? parent = null,
        LinkGeometry? geometry = null,
        bool sponsored = false) => new()
        {
            Url = url,
            DisplayText = text,
            Type = LinkType.Content,
            ImportanceScore = score,
            ParentSelector = parent,
            Geometry = geometry,
            IsSponsored = sponsored,
        };

    // ---- workspace-romy.2: LinkGeometry.Parse ----

    [Theory]
    [InlineData("10,20,300,40,18,700,1", 10, 20, 300, 40, 18, 700, true)]
    [InlineData("0,2400,200,20,12,400,0", 0, 2400, 200, 20, 12, 400, false)]
    public void LinkGeometry_Parse_RoundTrips(
        string attr, int x, int y, int w, int h, int fs, int fw, bool fold)
    {
        var g = LinkGeometry.Parse(attr);

        g.Should().NotBeNull();
        g!.X.Should().Be(x);
        g.Y.Should().Be(y);
        g.Width.Should().Be(w);
        g.Height.Should().Be(h);
        g.FontSize.Should().Be(fs);
        g.FontWeight.Should().Be(fw);
        g.AboveFold.Should().Be(fold);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("a,b,c,d,e,f,g")]
    [InlineData("10,20,0,40,18,700,1")] // zero width
    [InlineData("10,20,300,0,18,700,1")] // zero height
    public void LinkGeometry_Parse_RejectsMalformed(string? attr)
    {
        LinkGeometry.Parse(attr).Should().BeNull();
    }

    [Fact]
    public void LinkGeometry_IsBold_At600AndUp()
    {
        new LinkGeometry(0, 0, 10, 10, 14, 600, true).IsBold.Should().BeTrue();
        new LinkGeometry(0, 0, 10, 10, 14, 400, true).IsBold.Should().BeFalse();
    }

    // ---- workspace-romy.2: geometry importance boost ----

    [Fact]
    public void ApplyGeometryBoost_AboveFoldHeadline_Boosts()
    {
        var headline = new LinkGeometry(0, 100, 600, 40, 20, 700, AboveFold: true);
        LinkExtractor.ApplyGeometryBoost(70, headline).Should().Be(90); // +8 fold, +8 font, +4 bold
    }

    [Fact]
    public void ApplyGeometryBoost_TinyUtilityType_Demotes()
    {
        var railItem = new LinkGeometry(1100, 3000, 180, 14, 11, 400, AboveFold: false);
        LinkExtractor.ApplyGeometryBoost(70, railItem).Should().Be(62); // -8 tiny font
    }

    [Fact]
    public void ApplyGeometryBoost_NullGeometry_NoChange()
    {
        LinkExtractor.ApplyGeometryBoost(70, null).Should().Be(70);
    }

    [Fact]
    public void ApplyGeometryBoost_ClampsToValidRange()
    {
        var big = new LinkGeometry(0, 0, 900, 60, 28, 800, AboveFold: true);
        LinkExtractor.ApplyGeometryBoost(95, big).Should().Be(100);
        var tiny = new LinkGeometry(0, 9000, 100, 10, 9, 400, AboveFold: false);
        LinkExtractor.ApplyGeometryBoost(3, tiny).Should().Be(0);
    }

    // ---- workspace-romy.2/.4: extraction wires geometry + sponsor flag ----

    [Fact]
    public async Task ExtractLinks_ParsesStampedGeometry_AndBoostsImportance()
    {
        var html = """
            <html><body><main>
            <article><h2><a href="/story/big-news-headline-here"
                data-wc-geom="40,120,620,44,21,700,1">A big important news headline for today</a></h2></article>
            </main></body></html>
            """;
        var extractor = new LinkExtractor(Substitute.For<Microsoft.Extensions.Logging.ILogger<LinkExtractor>>());

        var links = await extractor.ExtractLinksAsync(html, "https://news.example.com/");

        var story = links.Single(l => l.Url.Contains("big-news-headline"));
        story.Geometry.Should().NotBeNull();
        story.Geometry!.FontSize.Should().Be(21);
        story.Geometry.AboveFold.Should().BeTrue();
        story.ImportanceScore.Should().Be(100); // boosted + clamped
    }

    [Fact]
    public async Task ExtractLinks_StoryShapedSponsorLink_KeptAndFlagged_ChromeAdDropped()
    {
        var html = """
            <html><body><main>
            <div class="sponsored"><a href="https://vendor.example.com/product-launch">
                This is a story-shaped sponsor post headline about a product</a></div>
            <div class="sponsored"><a href="/subscribe">Subscribe now</a></div>
            <article><a href="/story/real">A real story headline that is long enough</a></article>
            </main></body></html>
            """;
        var extractor = new LinkExtractor(Substitute.For<Microsoft.Extensions.Logging.ILogger<LinkExtractor>>());

        var links = await extractor.ExtractLinksAsync(html, "https://news.example.com/");

        var sponsor = links.SingleOrDefault(l => l.DisplayText.Contains("sponsor post"));
        sponsor.Should().NotBeNull("story-shaped sponsor links must stay visible to the analyzer");
        sponsor!.IsSponsored.Should().BeTrue();
        sponsor.ImportanceScore.Should().BeLessThanOrEqualTo(35);
        links.Should().NotContain(l => l.Url.EndsWith("/subscribe"), "chrome-shaped ad links are still dropped");
        links.Single(l => l.Url.Contains("/story/real")).IsSponsored.Should().BeFalse();
    }

    // ---- workspace-romy.4: prompt emission ----

    [Fact]
    public void VisionGuidance_WithScreenshot_DirectsVisualHierarchyAndBadges()
    {
        var text = OpenAiHierarchyAnalyzer.VisionGuidance(hasScreenshot: true);

        text.Should().Contain("SCREENSHOT");
        text.Should().Contain("numbered badges");
        text.Should().Contain("flag=sponsor");
        text.Should().Contain("vis:");
    }

    [Fact]
    public void VisionGuidance_TextOnly_StillExplainsGeometryAndSponsorFlags()
    {
        var text = OpenAiHierarchyAnalyzer.VisionGuidance(hasScreenshot: false);

        text.Should().NotContain("SCREENSHOT");
        text.Should().Contain("vis:");
        text.Should().Contain("flag=sponsor");
    }

    // ---- workspace-romy.5: ordering sanity ----

    private static SiteHierarchyConfig ConfigWithSections(params HierarchySection[] sections) => new()
    {
        Domain = "x.com",
        UrlPattern = "https://x.com/",
        Sections = sections.ToList(),
        Kind = LayoutKind.AiCurated,
        Version = 3,
        CreatedAt = DateTime.UtcNow,
        ModelVersion = "test",
    };

    private static HierarchySection Section(string name, params string[] selectors) => new()
    {
        Name = name,
        SortOrder = 0,
        ParentSelectors = selectors.ToList(),
    };

    [Fact]
    public void OrderingSanity_WeakLead_Fails()
    {
        var links = new List<LinkInfo>
        {
            Content("https://x.com/promo", "A promo box item that is story shaped", score: 20, parent: "aside.rail"),
            Content("https://x.com/a", "Big lead story headline", score: 95, parent: "section.lead"),
            Content("https://x.com/b", "Second story headline", score: 85, parent: "section.feed"),
            Content("https://x.com/c", "Third story headline", score: 80, parent: "section.feed"),
        };
        var config = ConfigWithSections(Section("Top", "aside.rail"), Section("Feed", "section.feed"));

        var failure = SetupWizard.OrderingSanityFailure(config, links);

        failure.Should().NotBeNull();
        failure!.QuestionId.Should().Be("ordering-sanity-failure");
        failure.Answer.Should().Contain("FIRST section");
    }

    [Fact]
    public void OrderingSanity_SponsorSectionAboveStories_Fails()
    {
        var links = new List<LinkInfo>
        {
            Content("https://x.com/lead", "Big lead story headline here", score: 95, parent: "section.lead"),
            Content("https://s.com/ad1", "Sponsored product post that looks like a story", score: 35, parent: "div.sponsor", sponsored: true),
            Content("https://s.com/ad2", "Another sponsored post in the same slot", score: 35, parent: "div.sponsor", sponsored: true),
            Content("https://x.com/b", "Second real story headline", score: 85, parent: "section.feed"),
        };
        var config = ConfigWithSections(
            Section("Top Story", "section.lead"),
            Section("Posts", "div.sponsor"),
            Section("Feed", "section.feed"));

        var failure = SetupWizard.OrderingSanityFailure(config, links);

        failure.Should().NotBeNull();
        failure!.Answer.Should().Contain("flag=sponsor");
    }

    [Fact]
    public void OrderingSanity_DroppedHighScoreLinks_Fails()
    {
        var links = Enumerable.Range(0, 10)
            .Select(i => Content($"https://x.com/s{i}", $"Story number {i} with a long headline", score: 80, parent: "section.river"))
            .Append(Content("https://x.com/lead", "The lead story headline", score: 95, parent: "section.lead"))
            .ToList();
        var config = ConfigWithSections(Section("Top Story", "section.lead"));

        var failure = SetupWizard.OrderingSanityFailure(config, links);

        failure.Should().NotBeNull();
        failure!.Answer.Should().Contain("high-importance");
    }

    [Fact]
    public void OrderingSanity_SoundConfig_Passes()
    {
        var links = new List<LinkInfo>
        {
            Content("https://x.com/lead", "Big lead story headline here", score: 95, parent: "section.lead"),
            Content("https://x.com/b", "Second story headline", score: 80, parent: "section.feed"),
            Content("https://x.com/c", "Third story headline", score: 75, parent: "section.feed"),
            Content("https://s.com/ad", "Sponsored post that looks like a story", score: 35, parent: "div.sponsor", sponsored: true),
        };
        var config = ConfigWithSections(
            Section("Top Story", "section.lead"),
            Section("Feed", "section.feed"));

        SetupWizard.OrderingSanityFailure(config, links).Should().BeNull();
    }

    [Fact]
    public void OrderingSanity_AboveFoldBigFontLead_PassesEvenWithModestScore()
    {
        var hero = new LinkGeometry(0, 80, 800, 60, 24, 700, AboveFold: true);
        var links = new List<LinkInfo>
        {
            Content("https://x.com/lead", "Hero story headline", score: 50, parent: "section.hero", geometry: hero),
            Content("https://x.com/a", "Other story one headline", score: 90, parent: "section.feed"),
            Content("https://x.com/b", "Other story two headline", score: 90, parent: "section.feed"),
            Content("https://x.com/c", "Other story three headline", score: 90, parent: "section.feed"),
        };
        var config = ConfigWithSections(Section("Top", "section.hero"), Section("Feed", "section.feed"));

        SetupWizard.OrderingSanityFailure(config, links).Should().BeNull();
    }

    // ---- workspace-romy.6: input scroll window ----

    [Fact]
    public void ScrollWindow_ShortInput_NoScrolling()
    {
        var (window, start) = TerminalInputHandler.ScrollWindow("hello", cursorPos: 5, maxWidth: 20);

        window.Should().Be("hello");
        start.Should().Be(0);
    }

    [Fact]
    public void ScrollWindow_CursorPastWidth_SlidesAndMarksLeftEdge()
    {
        var text = new string('a', 100);
        var (window, start) = TerminalInputHandler.ScrollWindow(text, cursorPos: 100, maxWidth: 20);

        start.Should().Be(81, "cursor sits in the last cell of the 20-wide window");
        window.Should().StartWith("…");
        window.Length.Should().Be(19);
    }

    [Fact]
    public void ScrollWindow_CursorInMiddle_ShowsBothEllipses()
    {
        var text = new string('x', 200);
        var (window, start) = TerminalInputHandler.ScrollWindow(text, cursorPos: 100, maxWidth: 20);

        window.Should().StartWith("…").And.EndWith("…");
        (100 - start).Should().BeInRange(0, 19, "cursor must be inside the window");
    }

    [Fact]
    public void ScrollWindow_CursorAtStartOfLongInput_MarksRightEdgeOnly()
    {
        var text = new string('y', 100);
        var (window, start) = TerminalInputHandler.ScrollWindow(text, cursorPos: 0, maxWidth: 20);

        start.Should().Be(0);
        window.Should().NotStartWith("…").And.EndWith("…");
    }

    // ---- workspace-romy.1: PNG telemetry ----

    [Fact]
    public void TryReadPngSize_ParsesIhdrDimensions()
    {
        // Minimal PNG header: signature + IHDR chunk with 640x480.
        var png = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x02, 0x80, 0x00, 0x00, 0x01, 0xE0,
        };

        ScreenshotCapture.TryReadPngSize(png).Should().Be((640, 480));
        ScreenshotCapture.TryReadPngSize(null).Should().BeNull();
        ScreenshotCapture.TryReadPngSize(new byte[] { 1, 2, 3 }).Should().BeNull();
    }

    // ---- workspace-romy.3: Set-of-Marks ----

    [Fact]
    public void BuildSetupMarks_MirrorsAnalyzerIndexSpace()
    {
        var links = new List<LinkInfo>
        {
            Content("https://x.com/a", "Story A headline"),
            new() { Url = "https://x.com/nav", DisplayText = "Nav", Type = LinkType.Navigation, ImportanceScore = 30 },
            Content("https://x.com/b", "Story B headline"),
        };

        var marks = StrategyChooserHandler.BuildSetupMarks(links);

        // Content links only, numbered by their position among content links.
        marks.Should().HaveCount(2);
        marks[0].Should().Be(new ScreenshotMark(0, "https://x.com/a"));
        marks[1].Should().Be(new ScreenshotMark(1, "https://x.com/b"));
    }

    [Fact]
    public void BuildSetupMarks_CapsAtEightyByImportance_KeepsOriginalIndices()
    {
        var links = Enumerable.Range(0, 120)
            .Select(i => Content($"https://x.com/s{i}", $"Story {i} headline text", score: i))
            .ToList();

        var marks = StrategyChooserHandler.BuildSetupMarks(links);

        marks.Should().HaveCount(80);
        marks.Select(m => m.Index).Should().BeInAscendingOrder();
        marks.Should().Contain(m => m.Index == 119, "highest-importance links keep their analyzer index");
        marks.Should().NotContain(m => m.Index < 40, "the 40 lowest-importance links fall past the cap");
    }

    // ---- workspace-romy.8: confidence + confirm question parsing ----

    private static List<LinkInfo> ParseLinks() => new()
    {
        Content("https://x.com/a", "Lead story headline", score: 95, parent: "section.lead"),
        Content("https://x.com/b", "Feed story headline", score: 80, parent: "section.feed"),
    };

    [Fact]
    public void ParsePatternFromAnswers_ReadsConfidenceAndConfirmQuestion()
    {
        var json =
            "{\"sections\":[{\"name\":\"Lead\",\"parent_selectors\":[\"section.lead\"],\"url_patterns\":[],\"story_indices\":[0],\"start_collapsed\":false}]," +
            "\"exclude_selectors\":[],\"exclude_url_patterns\":[],\"exclude_indices\":[]," +
            "\"confidence\":0.55," +
            "\"confirm_question\":{\"prompt\":\"Which is the main story?\",\"options\":[" +
            "{\"label\":\"The lead\",\"parent_selector\":\"section.lead\",\"url_pattern\":\"\"}," +
            "{\"label\":\"The feed\",\"parent_selector\":\"section.feed\",\"url_pattern\":\"\"}]}}";

        var result = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, ParseLinks(), "https://x.com/", "gpt-5-mini");

        result.Confidence.Should().Be(0.55);
        result.ConfirmQuestion.Should().NotBeNull();
        result.ConfirmQuestion!.Prompt.Should().Be("Which is the main story?");
        result.ConfirmQuestion.Options.Should().HaveCount(2);
        result.Config.Sections.Should().ContainSingle();
    }

    [Fact]
    public void ParsePatternFromAnswers_LegacyResponseWithoutConfidence_DefaultsConfident()
    {
        var json =
            "{\"sections\":[{\"name\":\"Lead\",\"parent_selectors\":[\"section.lead\"],\"url_patterns\":[],\"story_indices\":[0],\"start_collapsed\":false}]," +
            "\"exclude_selectors\":[],\"exclude_url_patterns\":[],\"exclude_indices\":[]}";

        var result = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, ParseLinks(), "https://x.com/", "gpt-5-mini");

        result.Confidence.Should().Be(1.0);
        result.ConfirmQuestion.Should().BeNull();
    }

    // ---- workspace-romy.8: preview card question rows ----

    [Fact]
    public void BuildPreviewCard_WithConfirmQuestion_AppendsAnswerRows_CursorStaysOnSection()
    {
        var config = ConfigWithSections(Section("Top", "section.lead"));
        var links = ParseLinks();
        var question = new SetupQuestion
        {
            Id = "confirm",
            Prompt = "Is this the main story?",
            Kind = SetupQuestionKind.PickMain,
            Options = new List<SetupOption>
            {
                new() { Label = "Yes, the lead", ParentSelector = "section.lead" },
                new() { Label = "No, the feed", ParentSelector = "section.feed" },
            },
        };

        var card = SetupWizard.BuildPreviewCard(config, links, previousCovered: null, confirmQuestion: question);

        // workspace-5vqk.3: one section header + its one matched headline + two answer rows.
        card.Options.Should().HaveCount(4, "section header + one headline row + two answer rows");
        card.Options[2].Label.Should().StartWith("AI asks ·");
        card.Cursor.Should().Be(0, "Enter must quick-accept without touching the question");
        card.Prompt.Should().Contain("Is this the main story?");
    }

    [Fact]
    public void BuildPreviewCard_CoverageDelta_ShownAfterAdjustment()
    {
        var config = ConfigWithSections(Section("Top", "section.lead"), Section("Feed", "section.feed"));
        var card = SetupWizard.BuildPreviewCard(config, ParseLinks(), previousCovered: 1);

        card.Footnote.Should().Contain("2 of 2").And.Contain("was 1");
    }

    [Fact]
    public void BuildPreviewCard_NoQuestion_KeepsPlainPrompt()
    {
        var config = ConfigWithSections(Section("Top", "section.lead"));
        var card = SetupWizard.BuildPreviewCard(config, ParseLinks());

        card.Prompt.Should().NotContain("AI is unsure");
        // workspace-5vqk.3: section header + its one matched headline row.
        card.Options.Should().HaveCount(2);
        card.Options[1].Label.Should().Contain("Lead story headline");
    }

    // ---- workspace-romy.10: volatile-id selector sanitization ----

    [Theory]
    [InlineData("div.item#260611p108", "div.item")]
    [InlineData("div.clus > div.item#260611p93 > div.mlk", "div.clus > div.item > div.mlk")]
    [InlineData("div.itc2#260611p44 > div.item#0i1 > strong.L5", "div.itc2 > div.item > strong.L5")]
    [InlineData("div#hiring > div.rnbody", "div#hiring > div.rnbody")] // structural id kept
    [InlineData("div#topcol2", "div#topcol2")] // single digit = structural
    [InlineData("#260611p44 > div.ii", "div.ii")] // bare volatile compound dropped
    [InlineData("section.lead", "section.lead")]
    public void StripVolatileIds_RemovesDateStampedIds_KeepsStructure(string input, string expected)
    {
        SelectorDerivation.StripVolatileIds(input).Should().Be(expected);
    }

    [Fact]
    public void ParsePatternFromAnswers_SanitizesVolatileIdSelectors()
    {
        // The memeorandum failure mode: model returns per-item date-stamped
        // ids that match one link today and nothing on a revisit.
        var json =
            "{\"sections\":[{\"name\":\"Lead\",\"parent_selectors\":[\"div.clus > div.item#260611p108\"],\"url_patterns\":[],\"story_indices\":[0],\"start_collapsed\":false}]," +
            "\"exclude_selectors\":[\"div.jsrn#260611ad1\"],\"exclude_url_patterns\":[],\"exclude_indices\":[]}";

        var result = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, ParseLinks(), "https://x.com/", "gpt-5-mini");

        result.Config.Sections[0].ParentSelectors.Should().ContainSingle()
            .Which.Should().Be("div.clus > div.item");
        result.Config.ExcludeSelectors.Should().ContainSingle()
            .Which.Should().Be("div.jsrn");
    }

    // ---- workspace-romy.8: wizard flow with a confirm question ----

    private static SetupQuestion ConfirmQuestion() => new()
    {
        Id = "confirm",
        Prompt = "Which is the main story?",
        Kind = SetupQuestionKind.PickMain,
        Options = new List<SetupOption>
        {
            new() { Label = "The lead box", ParentSelector = "section.lead" },
            new() { Label = "The feed top", ParentSelector = "section.feed" },
        },
    };

    private static IInputHandler InputSequence(params CommandType[] commands)
    {
        var input = Substitute.For<IInputHandler>();
        input.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(
                new WireCopy.Application.DTOs.Browser.NavigationCommand { Type = commands[0] },
                commands.Skip(1).Select(c => new WireCopy.Application.DTOs.Browser.NavigationCommand { Type = c }).ToArray());
        return input;
    }

    private static IHierarchyAnalyzer AnalyzerWithQuestionThenClean(
        SiteHierarchyConfig config, List<IReadOnlyList<SetupAnswer>> answersLog)
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.ProposeSetupQuestionsAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SiteSetupProposal { ProposedPattern = new ProposedPattern() });
        var inferCalls = 0;
        analyzer.InferPatternFromAnswersAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                answersLog.Add(ci.Arg<IReadOnlyList<SetupAnswer>>().ToList());
                inferCalls++;
                return new InferredPattern
                {
                    Config = config,
                    Confidence = inferCalls == 1 ? 0.5 : 1.0,
                    ConfirmQuestion = inferCalls == 1 ? ConfirmQuestion() : null,
                };
            });
        return analyzer;
    }

    [Fact]
    public async Task ConfirmQuestion_PlainEnter_QuickAcceptsWithoutAnswering()
    {
        var links = ParseLinks();
        var config = ConfigWithSections(Section("Top", "section.lead"), Section("Feed", "section.feed"));
        var answersLog = new List<IReadOnlyList<SetupAnswer>>();
        var analyzer = AnalyzerWithQuestionThenClean(config, answersLog);
        var input = InputSequence(CommandType.ActivateLink); // Enter on cursor 0 = section row

        var budget = new ModelRoundTripBudget();
        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new Infrastructure.Browser.UI.Components.SetupWizardOverlay.State(),
            links, "https://x.com/", null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null, applyPreview: null, lens: null, CancellationToken.None);

        result.Config.Should().NotBeNull("Enter on a section row saves immediately, question ignored");
        budget.Used.Should().Be(2, "ignoring the question must not spend extra model calls");
    }

    [Fact]
    public async Task ConfirmQuestion_AnswerRow_TriggersOneMoreInference_ThenSaves()
    {
        var links = ParseLinks();
        var config = ConfigWithSections(Section("Top", "section.lead"), Section("Feed", "section.feed"));
        var answersLog = new List<IReadOnlyList<SetupAnswer>>();
        var analyzer = AnalyzerWithQuestionThenClean(config, answersLog);

        // workspace-5vqk.3: preview rows are now [0]=Top header, [1]=Top's headline,
        // [2]=Feed header, [3]=Feed's headline, [4]=answer 1, [5]=answer 2. Four
        // Downs land on the first answer row; then Enter saves the re-previewed config.
        var input = InputSequence(
            CommandType.MoveDown, CommandType.MoveDown, CommandType.MoveDown, CommandType.MoveDown,
            CommandType.ActivateLink, CommandType.ActivateLink);

        var budget = new ModelRoundTripBudget();
        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new Infrastructure.Browser.UI.Components.SetupWizardOverlay.State(),
            links, "https://x.com/", null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null, applyPreview: null, lens: null, CancellationToken.None);

        result.Config.Should().NotBeNull();
        budget.Used.Should().Be(3, "silent propose + infer + the answered confirm question");
        answersLog.Last().Should().Contain(a => a.QuestionId == "confirm" && a.Answer.Contains("The lead box"));
    }
}
