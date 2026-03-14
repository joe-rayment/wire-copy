// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Tests for the content quality gate in ReadableContentExtractor.
/// ValidateContentQuality is internal static and TermReader.Tests has InternalsVisibleTo access.
/// </summary>
[Trait("Category", "Unit")]
public class ContentQualityGateTests
{
    #region ValidateContentQuality unit tests

    [Fact]
    public void ValidateContentQuality_EmptyList_ReturnsFalse()
    {
        ReadableContentExtractor.ValidateContentQuality(new List<string>())
            .Should().BeFalse("empty paragraph list has no content");
    }

    [Fact]
    public void ValidateContentQuality_TotalWordCountBelow100_ReturnsFalse()
    {
        // 3 paragraphs with ~20 words each = ~60 words total (below 100)
        var paragraphs = new List<string>
        {
            "This is a short paragraph with about twenty words in it for the test here now.",
            "Another short paragraph with roughly twenty words to add up for the word count test.",
            "A third short paragraph that also has about twenty words in total for counting."
        };

        ReadableContentExtractor.ValidateContentQuality(paragraphs)
            .Should().BeFalse("total word count is below 100");
    }

    [Fact]
    public void ValidateContentQuality_AverageParagraphLengthBelow50_ReturnsFalse()
    {
        // Many short fragments (avg < 50 chars) but enough total words
        var paragraphs = Enumerable.Range(1, 30)
            .Select(i => $"Short fragment number {i} here.")
            .ToList();

        ReadableContentExtractor.ValidateContentQuality(paragraphs)
            .Should().BeFalse("average paragraph length is below 50 characters");
    }

    [Fact]
    public void ValidateContentQuality_RepeatedFirstWord_ReturnsFalse()
    {
        // >50% of paragraphs start with the same word (template boilerplate)
        var paragraphs = new List<string>
        {
            "Item: This is the first entry in the catalog with description of the product and its features for sale today.",
            "Item: This is the second entry in the catalog with description of another product and its features for sale.",
            "Item: This is the third entry in the catalog with description of yet another product and its features sale.",
            "Item: This is the fourth entry in the catalog with description of a different product and features for sale.",
            "Different starting word here in this paragraph with enough content to be long enough for the threshold test."
        };

        ReadableContentExtractor.ValidateContentQuality(paragraphs)
            .Should().BeFalse("more than 50% of paragraphs start with the same word");
    }

    [Fact]
    public void ValidateContentQuality_HighNonAlphabeticRatio_ReturnsFalse()
    {
        // JavaScript-like content with lots of symbols: { } ( ) ; = > < etc.
        var paragraphs = new List<string>
        {
            "var x = function() { return obj.val > 0 ? arr[idx++] : null; }; // comment here for length padding aaaa bbbb cccc",
            "if (typeof window !== 'undefined') { document.getElementById('app').innerHTML = '<div>' + data + '</div>'; } cccc",
            "const cfg = { key: 'val', num: 123, fn: () => { console.log(JSON.stringify({ a: 1, b: [2,3] })); } }; dddd eeee",
            "export default class App { constructor(props) { super(props); this.state = { items: [], loaded: false }; } }; ffff"
        };

        ReadableContentExtractor.ValidateContentQuality(paragraphs)
            .Should().BeFalse("more than 30% of characters are non-alphabetic (code content)");
    }

    [Fact]
    public void ValidateContentQuality_ValidArticleContent_ReturnsTrue()
    {
        var paragraphs = new List<string>
        {
            "A discovery of a new species of deep-sea fish has excited marine biologists around the world. Researchers found the creature at a depth of more than three thousand meters in the Pacific Ocean.",
            "Scientists say the fish has unique bioluminescent properties that distinguish it from all known species. Its ability to produce light in multiple wavelengths suggests a complex evolutionary history.",
            "Plans are underway to conduct further studies to understand the ecological role of this newly discovered organism. Funding has been secured for a follow-up expedition next year to explore.",
            "Marine conservation groups have welcomed the discovery, noting that it highlights how much remains unknown about ocean ecosystems. They argue this underscores the need for increased protection of habitats.",
            "Results were published in the journal Nature this week and have already generated significant interest from the scientific community worldwide. Peer review confirmed the novel classification."
        };

        ReadableContentExtractor.ValidateContentQuality(paragraphs)
            .Should().BeTrue("paragraphs are coherent article content with good quality metrics");
    }

    [Fact]
    public void ValidateContentQuality_ExactlyHalfRepeatedFirstWord_ReturnsTrue()
    {
        // Exactly 50% (not more than 50%) should pass - boundary test
        // 2 out of 4 start with "The" = exactly 50%, and total > 100 words
        var paragraphs = new List<string>
        {
            "The first paragraph of this article discusses the main topic with plenty of detail and supporting evidence for the argument being made by the researchers in question who studied the phenomenon.",
            "The second paragraph continues the discussion with additional context and background information about the subject matter at hand and its wider implications for society at large today.",
            "However a different perspective emerges when considering the historical context and the various factors that contributed to this situation over time and across borders around the globe.",
            "Meanwhile other experts have weighed in with their own analysis and interpretations of the available data and research findings published recently in academic journals and conferences."
        };

        ReadableContentExtractor.ValidateContentQuality(paragraphs)
            .Should().BeTrue("exactly 50% repeated first word is at the boundary and should pass");
    }

    #endregion

    #region Integration tests: Level 2.5 and Level 3 quality gating

    [Fact]
    public async Task ExtractAsync_Level25_RejectsCommentSectionParagraphs()
    {
        // Arrange - Page where the largest paragraph block is a comment section
        // with short repetitive comments, not real article content
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        // Build HTML with no content-area selectors matching, so extraction falls to Level 2.5
        // The largest <p> block is in a div with no boilerplate class but contains garbage
        var garbageParagraphs = string.Join("\n", Enumerable.Range(1, 8).Select(i =>
            $"<p>Reply: user{i} says something short here.</p>"));

        var html = $@"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div class='wrapper'>
                    <div class='thread'>
                        {garbageParagraphs}
                    </div>
                </div>
            </body></html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        // Assert - quality gate should reject the comment-like paragraphs
        result.Should().BeNull("garbage comment-section paragraphs should fail quality validation");
    }

    [Fact]
    public void ValidateContentQuality_RejectsRepeatedTemplateText()
    {
        // Simulates what Level 3 (ExtractParagraphsAlternative) would produce from
        // repeated template/catalog text nodes. The quality gate should reject this.
        var paragraphs = Enumerable.Range(1, 8).Select(i =>
            $"Product: Item number {i} is available in our catalog with specifications including weight, dimensions, color options, material composition, and warranty period for all customers to review before purchase.").ToList();

        ReadableContentExtractor.ValidateContentQuality(paragraphs)
            .Should().BeFalse("repeated template text where >50% start with the same word should be rejected");
    }

    [Fact]
    public async Task ExtractAsync_Level25_AcceptsValidArticleBlock()
    {
        // Arrange - Page where the largest paragraph block is genuine article content
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var articleParagraphs = string.Join("\n", Enumerable.Range(1, 5).Select(i =>
            $"<p>This is article paragraph {i} with substantial text content that discusses an important topic. The research findings suggest significant implications for the field of study and warrant further investigation by experts.</p>"));

        var html = $@"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div class='wrapper'>
                    <div class='story-area'>
                        {articleParagraphs}
                    </div>
                    <div class='other'>
                        <p>Short aside.</p>
                    </div>
                </div>
            </body></html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        // Assert
        result.Should().NotBeNull("valid article paragraph block should pass quality validation");
        result!.Paragraphs.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task ExtractAsync_Level3_RejectsGarbageJsTextNodes()
    {
        // Arrange - Page that falls through L1, L2, L2.5 and hits L3.
        // L1 fails: no <p>/<blockquote>/<li> with >50 chars text.
        // L2 fails: no <div>/<section> elements with direct text >50 chars.
        // L2.5a fails: text density scoring yields no candidate with >=3 substantial blocks.
        // L2.5b fails: no <p> elements, so FindLargestParagraphBlock returns empty.
        // L3 (ExtractParagraphsAlternative): finds text nodes >100 chars in <details> elements,
        // but the content is JS-like garbage with >30% non-alphabetic chars, failing quality gate.
        //
        // Uses <details> to avoid L2's search of <div>/<section> elements.
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        // Each details element has >100 chars of JS-like content with normal spacing (enough words
        // to pass the 100-word check) but dense symbols that fail the 70% alphabetic ratio check.
        var jsGarbageDetails = new[]
        {
            @"<details>var x = fn() { return [1, 2, 3].map(i =&gt; i * 2 + 1); }; var y = { k1: 'v1', k2: [4, 5, 6], k3: fn(a, b) { return a &gt; b ? a : b; } };</details>",
            @"<details>if (x !== null &amp;&amp; y &gt;= 10) { z = arr.reduce((a, b) =&gt; a + b, 0); } else { z = x ? [1, 2, 3] : []; w = { a: 1, b: 2, c: { d: 3, e: [4, 5] } }; }</details>",
            @"<details>const { a, b, ...rest } = obj; let q = [1, 2, ...a, ...b]; r = { ...rest, x: 10, y: 20 }; s = q.filter(v =&gt; v &gt; 5).map(v =&gt; v * 2);</details>",
            @"<details>for (let i = 0; i &lt; arr.length; i++) { if (arr[i] % 2 === 0) { res.push(arr[i] * 3 + 1); } else { res.push(arr[i] / 2 - 1); } cnt++; }</details>",
            @"<details>switch (x) { case 1: y = [2, 3]; break; case 2: y = { a: 4, b: 5 }; break; default: y = fn(x) =&gt; x * x + x / 2 - 1; z = !y ? 0 : 1; }</details>",
            @"<details>obj = { fn: (x, y) =&gt; { return x ** 2 + y ** 2; }, g: [1, 2, 3].join(','), h: ""k=v&amp;a=b&amp;c=d"", i: typeof x !== 'undefined' ? x : null };</details>"
        };

        var html = $@"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <details class='wrapper'>
                    <details class='js-bundle'>
                        {string.Join("\n", jsGarbageDetails)}
                    </details>
                </details>
            </body></html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        // Assert - L3 quality gate should reject JS garbage (non-alphabetic ratio > 30%)
        result.Should().BeNull(
            "Level 3 extraction of JS text nodes should fail the alphabetic ratio quality check");
    }

    [Fact]
    public async Task ExtractAsync_Level2_AcceptsValidProseInSections()
    {
        // Arrange - Page with valid prose in <section> elements (no <p> tags).
        // Level 2 now searches both <div> and <section> elements, so this content
        // is picked up directly. Each section has an inline <span> child with prose.
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var proseSections = new[]
        {
            "<section><span>Scientists at the marine research station announced a remarkable discovery this week. A previously unknown species of deep-sea jellyfish was found thriving near hydrothermal vents at extreme depths.</span></section>",
            "<section><span>The creature displays an unusual bioluminescent pattern that researchers believe serves as both a defense mechanism and a method of communication with others of its kind in the darkness below.</span></section>",
            "<section><span>Funding for the expedition came from an international coalition of universities and conservation groups. Researchers spent nearly three months aboard a specially equipped vessel in the southern Pacific Ocean.</span></section>",
            "<section><span>Marine biologists emphasized that the finding highlights vast gaps in our understanding of ocean ecosystems. Entire communities of organisms may exist in regions that remain completely unexplored today.</span></section>",
            "<section><span>Publication of the formal species description is expected later this year in a leading peer-reviewed journal. Several follow-up expeditions are already being planned for the next research season ahead.</span></section>",
            "<section><span>Conservation advocates have called for expanded protections around deep-sea hydrothermal vent systems. They argue that mining and industrial activities could devastate fragile habitats before they are even cataloged.</span></section>"
        };

        var html = $@"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div class='wrapper'>
                    <div class='text-block'>
                        {string.Join("\n", proseSections)}
                    </div>
                </div>
            </body></html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        // Assert - Level 2 should extract valid prose from <section> elements
        result.Should().NotBeNull(
            "Level 2 extraction of valid prose in <section> elements should succeed");
        result!.Paragraphs.Count.Should().BeGreaterThanOrEqualTo(3,
            "enough prose content should produce multiple paragraphs");
    }

    #endregion

    #region Paywall detection tests

    [Fact]
    public async Task ExtractAsync_PaywalledNytPage_SetsIsPaywalled()
    {
        // Arrange - NYT-style paywall page: short article with 2 paragraphs (below threshold)
        // plus a gateway div and "Subscribe to The Times" prompt
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <article>
                    <h1>Breaking News Story</h1>
                    <p>The government announced a new policy today that will affect millions of citizens across the country. Officials say the changes are designed to improve public services.</p>
                    <p>Critics have raised concerns about the cost of implementation and the timeline for the proposed changes to take effect across all regions.</p>
                </article>
                <div class='gateway expanded-dock'>
                    <p>Subscribe to The Times to continue reading this article.</p>
                    <button>Subscribe Now</button>
                </div>
            </body></html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://www.nytimes.com/2024/01/15/article.html");

        // Assert
        result.Should().NotBeNull("the page has extractable content");
        result!.IsPaywalled.Should().BeTrue("paywall gateway element and truncated content should flag as paywalled");
    }

    [Fact]
    public async Task ExtractAsync_FreeArticle_IsPaywalledIsFalse()
    {
        // Arrange - Full article with no paywall indicators
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <article>
                    <h1>A Fascinating Discovery in Marine Biology</h1>
                    <p>Scientists at a leading research university have announced a breakthrough in marine biology that could reshape our understanding of deep-sea ecosystems and the organisms that inhabit them.</p>
                    <p>The research team spent over two years collecting samples from hydrothermal vents located thousands of meters below the surface of the Pacific Ocean near the coast of Chile.</p>
                    <p>Their findings reveal a complex web of symbiotic relationships between bacteria and larger organisms that was previously unknown to science and challenges existing theories about deep-sea life.</p>
                    <p>Funding for the expedition was provided by an international consortium of research institutions and government agencies committed to advancing our knowledge of ocean biodiversity.</p>
                    <p>The lead researcher expressed optimism that the discoveries would lead to new applications in biotechnology and medicine, noting that deep-sea organisms often produce unique chemical compounds.</p>
                </article>
            </body></html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/free-article");

        // Assert
        result.Should().NotBeNull("the page has full article content");
        result!.IsPaywalled.Should().BeFalse("no paywall indicators are present");
        result.Paragraphs.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task ExtractAsync_GenericPaywall_SetsIsPaywalled()
    {
        // Arrange - Generic paywall page with "Already a subscriber? Log in" and short content
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <article>
                    <h1>Exclusive Report on Economic Trends</h1>
                    <p>Economic indicators suggest a significant shift in consumer spending patterns across major metropolitan areas in the United States.</p>
                </article>
                <div class='paywall-barrier regwall' data-testid='paywall-prompt'>
                    <h2>This article is for subscribers</h2>
                    <p>Already a subscriber? Log in to continue reading.</p>
                    <p>Create a free account to read this and other premium content.</p>
                </div>
            </body></html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/premium-article");

        // Assert
        result.Should().NotBeNull("the page has some extractable content");
        result!.IsPaywalled.Should().BeTrue("paywall div with subscriber text and truncated content should flag as paywalled");
    }

    [Fact]
    public async Task ExtractAsync_PaywallElementWithFullContent_IsPaywalled()
    {
        // Arrange - Page has a gateway paywall element with full preview content.
        // Paywall HTML elements are strong indicators — sites like NYT show preview
        // content before the gate, so we always trust these elements.
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <article>
                    <h1>Full Article Despite Paywall Element</h1>
                    <p>This is the first paragraph of a complete article that has plenty of content for readers to enjoy. The topic covers recent advances in renewable energy technology and their potential impact on global markets.</p>
                    <p>The second paragraph continues with detailed analysis of solar panel efficiency improvements and cost reductions that have made solar energy competitive with fossil fuels in many regions of the world today.</p>
                    <p>In the third section we examine wind energy developments, including offshore wind farms that are being built at unprecedented scale in the North Sea and along the eastern seaboard of the United States.</p>
                    <p>Battery storage technology has also seen remarkable progress, with new lithium-ion alternatives promising longer lifespans, faster charging times, and significantly reduced environmental impact during manufacturing and disposal.</p>
                </article>
                <div class='gateway' style='display:none'>
                    <p>Subscribe for unlimited access</p>
                </div>
            </body></html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/full-article");

        // Assert
        result.Should().NotBeNull();
        result!.IsPaywalled.Should().BeTrue("gateway HTML element is a strong paywall indicator regardless of content amount");
    }

    [Fact]
    public void DetectPaywall_MembersOnlyText_WithTruncatedContent_ReturnsTrue()
    {
        // Direct unit test of DetectPaywall with "members only" text pattern
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(@"<html><body>
            <p>Preview of the article content here.</p>
            <div>This content is for members only. Please subscribe to continue.</div>
        </body></html>");

        var paragraphs = new List<string>
        {
            "Preview of the article content here."
        };

        ReadableContentExtractor.DetectPaywall(doc, paragraphs)
            .Should().BeTrue("'members only' text pattern with truncated content should be detected");
    }

    [Fact]
    public void DetectPaywall_StartYourFreeTrial_WithTruncatedContent_ReturnsTrue()
    {
        // Direct unit test of DetectPaywall with "start your free trial" text pattern
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(@"<html><body>
            <p>First paragraph of a premium article with limited preview.</p>
            <div>Start your free trial today to read the full story.</div>
        </body></html>");

        var paragraphs = new List<string>
        {
            "First paragraph of a premium article with limited preview."
        };

        ReadableContentExtractor.DetectPaywall(doc, paragraphs)
            .Should().BeTrue("'start your free trial' text pattern with truncated content should be detected");
    }

    [Fact]
    public void DetectPaywall_RegwallElement_WithTruncatedContent_ReturnsTrue()
    {
        // Direct unit test of DetectPaywall with regwall CSS class
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(@"<html><body>
            <article>
                <p>Brief introduction to the article topic.</p>
            </article>
            <div class='regwall'>
                <p>Register to read more.</p>
            </div>
        </body></html>");

        var paragraphs = new List<string>
        {
            "Brief introduction to the article topic."
        };

        ReadableContentExtractor.DetectPaywall(doc, paragraphs)
            .Should().BeTrue("regwall CSS class with truncated content should be detected");
    }

    [Fact]
    public void DetectPaywall_DataTestIdPaywall_WithTruncatedContent_ReturnsTrue()
    {
        // Direct unit test of DetectPaywall with data-testid="paywall"
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(@"<html><body>
            <article>
                <p>A short preview of the article is shown here.</p>
            </article>
            <div data-testid='paywall-modal'>
                <p>Subscribe to continue reading.</p>
            </div>
        </body></html>");

        var paragraphs = new List<string>
        {
            "A short preview of the article is shown here."
        };

        ReadableContentExtractor.DetectPaywall(doc, paragraphs)
            .Should().BeTrue("data-testid containing 'paywall' with truncated content should be detected");
    }

    [Fact]
    public void DetectPaywall_NoIndicators_ReturnsFalse()
    {
        // Direct unit test: no paywall indicators should return false
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(@"<html><body>
            <article>
                <p>A normal article with no paywall at all.</p>
            </article>
        </body></html>");

        var paragraphs = new List<string>
        {
            "A normal article with no paywall at all."
        };

        ReadableContentExtractor.DetectPaywall(doc, paragraphs)
            .Should().BeFalse("no paywall indicators should not flag as paywalled");
    }

    #endregion

    #region NYT-style content extraction

    [Fact]
    public async Task ExtractAsync_NytStyleStoryBodyCompanionColumn_ExtractsParagraphs()
    {
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <main>
                    <div class='StoryBodyCompanionColumn'>
                        <div class='css-53u6y8'>
                            <p class='css-at9mc1'>The recent surge in oil prices has sent shockwaves through global markets, affecting everything from gasoline prices at the pump to the cost of heating homes during winter months.</p>
                            <p class='css-at9mc1'>Analysts say the price increase is driven by a combination of geopolitical tensions in the Middle East and production cuts by OPEC member nations that have restricted supply significantly.</p>
                            <p class='css-at9mc1'>The impact on consumers has been immediate, with many households reporting that their monthly energy bills have increased by as much as thirty percent compared to the same period last year.</p>
                            <p class='css-at9mc1'>Energy experts warn that prices could continue to rise unless there is a diplomatic resolution to the ongoing conflicts or a significant increase in production from non-OPEC countries.</p>
                        </div>
                    </div>
                </main>
            </body></html>";

        var result = await extractor.ExtractAsync(html, "https://www.nytimes.com/2024/01/15/business/oil-prices.html");

        result.Should().NotBeNull("NYT StoryBodyCompanionColumn should be recognized as content area");
        result!.Paragraphs.Should().HaveCountGreaterOrEqualTo(3);
        result.Paragraphs[0].Should().Contain("oil prices");
    }

    [Fact]
    public async Task ExtractAsync_DataTestIdArticleBody_ExtractsParagraphs()
    {
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div data-testid='article-body'>
                    <p>The new legislation aims to address longstanding concerns about data privacy in the digital age, requiring companies to obtain explicit consent before collecting personal information.</p>
                    <p>Privacy advocates have praised the bill as a significant step forward, noting that it aligns with similar regulations already in place in the European Union under the General Data Protection Regulation.</p>
                    <p>Tech industry representatives have expressed concerns about the compliance costs, arguing that smaller companies may struggle to implement the required changes within the proposed timeline.</p>
                    <p>Congressional leaders from both parties have expressed support for the bill, suggesting it could receive bipartisan approval when it comes to a floor vote later this month.</p>
                </div>
            </body></html>";

        var result = await extractor.ExtractAsync(html, "https://www.nytimes.com/2024/01/15/technology/data-privacy.html");

        result.Should().NotBeNull("data-testid='article-body' should be recognized as content area");
        result!.Paragraphs.Should().HaveCountGreaterOrEqualTo(3);
    }

    [Fact]
    public void IsArticlePage_NytStoryBodyCompanionColumn_ReturnsTrue()
    {
        var html = @"<html><head></head>
            <body>
                <div class='StoryBodyCompanionColumn'>
                    <p>Article content here with sufficient text.</p>
                    <p>Second paragraph with more meaningful content for the reader.</p>
                    <p>Third paragraph ensures we have enough content to detect this as an article page.</p>
                </div>
            </body></html>";

        ReadableContentExtractor.IsArticlePage(html)
            .Should().BeTrue("page with StoryBodyCompanionColumn should be recognized as article");
    }

    #endregion

    #region JS-heavy page extraction (text density scoring)

    [Fact]
    public async Task ExtractAsync_ContentInSectionsNotDivs_ExtractsViaLevel2()
    {
        // Page where article content is in <section> elements with <span> text,
        // not in <div> or <p> tags. Before the fix, Level 2 only searched <div> elements
        // and would miss <section>-based content entirely.
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div id='root'>
                    <div class='article-zone' data-content-region='body'>
                        <section class='block'>
                            <span class='graf'>The Arctic is experiencing unprecedented changes as rising global temperatures
                            accelerate the melting of permafrost and sea ice, fundamentally altering ecosystems that have
                            remained stable for millennia.</span>
                        </section>
                        <section class='block'>
                            <span class='graf'>Research teams from universities across Scandinavia have documented a dramatic
                            shift in wildlife migration patterns, with species previously confined to temperate regions
                            now being observed hundreds of kilometers north of their historical range.</span>
                        </section>
                        <section class='block'>
                            <span class='graf'>The loss of sea ice is particularly concerning for marine mammals such as
                            polar bears and walruses that depend on ice platforms for hunting and resting throughout
                            the long Arctic winter season.</span>
                        </section>
                        <section class='block'>
                            <span class='graf'>Indigenous communities in northern Canada and Siberia report that traditional
                            knowledge about seasonal patterns is becoming less reliable as weather becomes more
                            unpredictable and permafrost thaw destabilizes infrastructure.</span>
                        </section>
                    </div>
                </div>
            </body></html>";

        var result = await extractor.ExtractAsync(html, "https://example.com/arctic-article");

        result.Should().NotBeNull("content in <section> elements should be extracted via Level 2");
        result!.Paragraphs.Count.Should().BeGreaterThanOrEqualTo(3);
        result.Paragraphs[0].Should().Contain("Arctic");
    }

    [Fact]
    public async Task ExtractAsync_TextDensityScoring_FindsBestContainer()
    {
        // Page with NO content area selectors matching, NO <p> tags,
        // and content spread across <section> elements within a high-density container.
        // A low-density noise container has short divs.
        // Text density scoring should pick the content container over the noise.
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div id='app'>
                    <div class='noise-widget'>
                        <div class='item'><a href='/'>Home</a> <a href='/about'>About</a> <a href='/contact'>Contact Us Today</a></div>
                        <div class='item'><a href='/news'>News</a> <a href='/sports'>Sports</a> <a href='/tech'>Technology</a></div>
                    </div>
                    <div class='custom-article-wrapper'>
                        <section class='text-chunk'>
                            <span>Scientists at a leading marine research laboratory announced a remarkable breakthrough
                            in understanding deep ocean ecosystems this week. The discovery could reshape conservation
                            strategies for vulnerable marine habitats around the world.</span>
                        </section>
                        <section class='text-chunk'>
                            <span>The research team spent nearly three years collecting samples from hydrothermal vents
                            located thousands of meters below the surface of the eastern Pacific Ocean, using remotely
                            operated vehicles equipped with specialized collection instruments.</span>
                        </section>
                        <section class='text-chunk'>
                            <span>Their findings reveal a complex web of symbiotic relationships between chemosynthetic
                            bacteria and larger organisms that was previously unknown to science. These relationships
                            challenge existing theoretical models of deep-sea ecology significantly.</span>
                        </section>
                        <section class='text-chunk'>
                            <span>Funding for the expedition was provided by an international consortium of research
                            institutions and government agencies committed to advancing knowledge of ocean biodiversity
                            and protecting fragile marine ecosystems for future generations.</span>
                        </section>
                    </div>
                </div>
            </body></html>";

        var result = await extractor.ExtractAsync(html, "https://example.com/ocean-article");

        result.Should().NotBeNull("text density scoring should find the article container");
        result!.Paragraphs.Count.Should().BeGreaterThanOrEqualTo(3);
        result.Paragraphs[0].Should().Contain("marine");
    }

    [Fact]
    public async Task ExtractAsync_WordPressGutenberg_ExtractsParagraphs()
    {
        // WordPress Gutenberg page with wp-block-post-content wrapper
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div class='site-content'>
                    <div class='wp-block-post-content'>
                        <p>The city council voted unanimously to approve a comprehensive infrastructure modernization plan that will affect transportation, utilities, and public spaces across the metropolitan area over the next decade.</p>
                        <p>The plan includes significant upgrades to the aging water system, which engineers say has reached the end of its useful life after more than sixty years of continuous service to residents and businesses.</p>
                        <p>Transportation improvements will focus on expanding public transit options, including new bus rapid transit corridors and dedicated bicycle lanes connecting residential neighborhoods to commercial districts.</p>
                        <p>Funding for the estimated four billion dollar initiative will come from a combination of federal grants, municipal bonds, and a modest increase in property tax assessments phased in over five years.</p>
                    </div>
                    <div class='wp-block-latest-posts'>
                        <p>Latest: Weather forecast for the weekend</p>
                        <p>Latest: Sports scores from last night</p>
                    </div>
                </div>
            </body></html>";

        var result = await extractor.ExtractAsync(html, "https://example.com/city-plan");

        result.Should().NotBeNull("wp-block-post-content should be recognized as content area");
        result!.Paragraphs.Count.Should().BeGreaterThanOrEqualTo(3);
        result.Paragraphs[0].Should().Contain("city council");
        // Should not include the "Latest:" noise from wp-block-latest-posts
        result.Paragraphs.Should().NotContain(p => p.Contains("Latest:"));
    }

    [Fact]
    public async Task ExtractAsync_SubstackAvailableContent_ExtractsParagraphs()
    {
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div class='post'>
                    <div class='available-content'>
                        <p>The debate over artificial intelligence regulation has intensified in recent weeks as lawmakers from both parties introduced competing proposals to govern the rapidly evolving technology sector.</p>
                        <p>Proponents of stricter regulation argue that without clear guidelines, AI systems could be deployed in ways that harm consumers, perpetuate bias, and undermine democratic institutions and processes.</p>
                        <p>Technology industry leaders have pushed back, contending that overly prescriptive rules could stifle innovation and put domestic companies at a competitive disadvantage relative to international rivals.</p>
                        <p>Independent researchers have called for a balanced approach that promotes transparency and accountability while preserving the flexibility needed for continued advancement in the field.</p>
                    </div>
                    <div class='paywall'>
                        <p>Subscribe to continue reading this post.</p>
                    </div>
                </div>
            </body></html>";

        var result = await extractor.ExtractAsync(html, "https://example.substack.com/p/ai-regulation");

        result.Should().NotBeNull("Substack available-content should be recognized as content area");
        result!.Paragraphs.Count.Should().BeGreaterThanOrEqualTo(3);
        result.Paragraphs[0].Should().Contain("artificial intelligence");
    }

    [Fact]
    public async Task ExtractAsync_GhostCms_ExtractsParagraphs()
    {
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div class='gh-content gh-canvas'>
                    <p>A comprehensive study of renewable energy adoption rates across thirty countries reveals striking differences in the pace of transition away from fossil fuels, highlighting the complex interplay of policy, economics, and public opinion.</p>
                    <p>Countries with strong government incentives and established manufacturing capabilities for solar panels and wind turbines have seen the fastest adoption rates over the past five years, outpacing initial projections.</p>
                    <p>The study also found that public acceptance of renewable energy infrastructure varies significantly by region, with rural communities often expressing more concerns about visual impact and land use changes than urban populations.</p>
                    <p>Researchers recommend a multi-pronged approach that combines financial incentives with community engagement and workforce development programs to accelerate the transition while maintaining public support.</p>
                </div>
            </body></html>";

        var result = await extractor.ExtractAsync(html, "https://example.ghost.io/renewable-energy");

        result.Should().NotBeNull("Ghost gh-content should be recognized as content area");
        result!.Paragraphs.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task ExtractAsync_DataContentRegion_ExtractsParagraphs()
    {
        // Page using data-content-region attribute (common in modern SPAs)
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div id='__next'>
                    <div class='custom-wrapper' data-content-region='body'>
                        <p>Astronomers using the James Webb Space Telescope have captured unprecedented images of a distant galaxy cluster that formed within the first billion years after the Big Bang, providing new insights into early cosmic evolution.</p>
                        <p>The observations reveal structures that current models of galaxy formation did not predict, suggesting that the processes driving the assembly of stars and galaxies in the early universe were more efficient than previously thought.</p>
                        <p>The international team of researchers published their findings in a special edition of the journal Nature, accompanied by detailed spectroscopic analyses that confirm the extreme age of the observed structures.</p>
                        <p>Follow-up observations are planned for the next observation cycle to determine whether similar structures exist in other regions of the early universe, which could have profound implications for cosmological theory.</p>
                    </div>
                </div>
            </body></html>";

        var result = await extractor.ExtractAsync(html, "https://example.com/space-discovery");

        result.Should().NotBeNull("data-content-region='body' should be recognized as content area");
        result!.Paragraphs.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void IsArticlePage_WordPressGutenberg_ReturnsTrue()
    {
        var html = @"<html><body>
            <div class='wp-block-post-content'>
                <p>Article content with enough text to be substantial.</p>
                <p>More article content for testing the detection.</p>
                <p>Third paragraph ensures we pass the threshold check.</p>
            </div></body></html>";

        ReadableContentExtractor.IsArticlePage(html)
            .Should().BeTrue("page with wp-block-post-content should be recognized as article");
    }

    [Fact]
    public void IsArticlePage_GhostCms_ReturnsTrue()
    {
        var html = @"<html><body>
            <div class='gh-content'>
                <p>Article content here.</p>
            </div></body></html>";

        ReadableContentExtractor.IsArticlePage(html)
            .Should().BeTrue("page with gh-content should be recognized as article");
    }

    [Fact]
    public void IsArticlePage_DataContentRegion_ReturnsTrue()
    {
        var html = @"<html><body>
            <div data-content-region='body'>
                <p>Article content here.</p>
            </div></body></html>";

        ReadableContentExtractor.IsArticlePage(html)
            .Should().BeTrue("page with data-content-region='body' should be recognized as article");
    }

    [Fact]
    public void HasExtractableContent_TextDensityFindsBestContainer_ReturnsTrue()
    {
        // Page with no standard selectors, content in sections within a dense container.
        // Each paragraph needs enough words so total exceeds 100-word quality gate threshold.
        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div id='app'>
                    <div class='custom-article-zone'>
                        <section class='chunk'>
                            <span>Scientists at a leading marine research laboratory announced a remarkable breakthrough in understanding deep ocean ecosystems this week. The discovery of previously unknown symbiotic relationships between chemosynthetic bacteria and larger organisms could reshape conservation strategies for vulnerable marine habitats around the world.</span>
                        </section>
                        <section class='chunk'>
                            <span>The research team spent nearly three years collecting samples from hydrothermal vents located thousands of meters below the surface of the eastern Pacific Ocean, using remotely operated vehicles equipped with specialized collection instruments designed to preserve delicate biological specimens during ascent.</span>
                        </section>
                        <section class='chunk'>
                            <span>Their findings reveal a complex web of ecological dependencies that was previously unknown to science and challenge existing theoretical models of deep-sea ecology significantly. Funding for the expedition was provided by an international consortium of research institutions and government agencies committed to advancing knowledge of ocean biodiversity.</span>
                        </section>
                    </div>
                </div>
            </body></html>";

        ReadableContentExtractor.HasExtractableContent(html)
            .Should().BeTrue("text density scoring should find content in section-based containers");
    }

    #endregion

    #region Boilerplate removal — false positive prevention

    [Fact]
    public async Task ExtractAsync_ClassContainingAdSubstring_NotRemovedAsBoilerplate()
    {
        // "heading", "loading", "padding" all contain "ad" as a substring.
        // The old boilerplate removal used contains(@class, 'ad') which matched these.
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <article>
                    <div class='heading-wrapper'>
                        <h1>Test Article</h1>
                    </div>
                    <div class='reading-body loading-complete'>
                        <p>The first paragraph of this article discusses important topics that readers need to understand in the context of global economic developments.</p>
                        <p>The second paragraph continues with analysis of the situation, providing detailed information about how various factors contribute to the overall picture.</p>
                        <p>The third paragraph wraps up with conclusions and forward-looking statements about what may happen in the coming months and years ahead.</p>
                        <p>A fourth paragraph adds additional context and supporting evidence from multiple authoritative sources and expert opinions gathered over time.</p>
                    </div>
                </article>
            </body></html>";

        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        result.Should().NotBeNull("classes containing 'ad' as a substring should not be treated as ad boilerplate");
        result!.Paragraphs.Should().HaveCountGreaterOrEqualTo(3);
    }

    [Fact]
    public async Task ExtractAsync_NytMultiColumnLayout_AggregatesAcrossColumns()
    {
        // NYT splits articles across multiple StoryBodyCompanionColumn divs.
        // Each column may have 1-2 paragraphs, but together they form the full article.
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <main>
                    <div class='StoryBodyCompanionColumn'>
                        <div class='css-53u6y8'>
                            <p>The recent developments in international trade policy have raised concerns among economists about potential impacts on global supply chains and manufacturing sectors.</p>
                            <p>Trade experts note that the new tariff structure could lead to significant shifts in how companies approach their sourcing strategies and production planning.</p>
                        </div>
                    </div>
                    <figure><img src='photo.jpg' /></figure>
                    <div class='StoryBodyCompanionColumn'>
                        <div class='css-53u6y8'>
                            <p>The domestic impact is already being felt in several key industries, with automotive manufacturers reporting increased costs that could eventually be passed on to consumers.</p>
                            <p>Meanwhile, agricultural exporters are expressing frustration about retaliatory measures that have reduced their access to previously lucrative overseas markets.</p>
                        </div>
                    </div>
                    <div class='StoryBodyCompanionColumn'>
                        <div class='css-53u6y8'>
                            <p>Economists project that the cumulative effect of these trade tensions could reduce gross domestic product growth by up to half a percentage point over the next fiscal year.</p>
                        </div>
                    </div>
                </main>
            </body></html>";

        var result = await extractor.ExtractAsync(html, "https://www.nytimes.com/2024/03/15/business/trade-policy.html");

        result.Should().NotBeNull("paragraphs split across multiple StoryBodyCompanionColumn divs should be aggregated");
        result!.Paragraphs.Should().HaveCountGreaterOrEqualTo(4,
            "all paragraphs from all columns should be combined");
    }

    [Fact]
    public async Task ExtractAsync_ArticleHeaderPreserved_WhenInsideArticleTag()
    {
        // Article-internal <header> contains title and lead paragraph.
        // Only the site-level header should be removed as boilerplate.
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <header><nav><a href='/'>Home</a></nav></header>
                <article>
                    <header>
                        <h1>Article Title</h1>
                        <p class='summary'>This is the lead paragraph that provides a summary of the article content and sets up the key themes explored below.</p>
                    </header>
                    <section name='articleBody'>
                        <p>The main body of the article begins here with detailed analysis of the situation and its broader implications for all stakeholders involved.</p>
                        <p>Further paragraphs continue the analysis with additional evidence and expert commentary that supports the main thesis of the article.</p>
                        <p>The concluding section brings together the various threads discussed above and offers perspective on what readers should watch for going forward.</p>
                    </section>
                </article>
            </body></html>";

        var result = await extractor.ExtractAsync(html, "https://example.com/news/article");

        result.Should().NotBeNull("article content should be extracted even when article has internal <header>");
        result!.Paragraphs.Should().HaveCountGreaterOrEqualTo(3);
    }

    #endregion
}
