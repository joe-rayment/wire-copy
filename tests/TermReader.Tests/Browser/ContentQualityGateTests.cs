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
        // L2 fails: no <div> elements at all, so no divs with direct text.
        // L2.5 fails: no <p> elements, so FindLargestParagraphBlock returns empty.
        // L3 (ExtractParagraphsAlternative): finds text nodes >100 chars in <section> elements,
        // but the content is JS-like garbage with >30% non-alphabetic chars, failing quality gate.
        //
        // Uses <section> instead of <div>/<span> to avoid L2's GetDirectTextContent path
        // (L2 searches ".//div" and includes inline elements like <span> as direct text).
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        // Each section has >100 chars of JS-like content with normal spacing (enough words to
        // pass the 100-word check) but dense symbols that fail the 70% alphabetic ratio check.
        // Combined: ~230 words, ~0.33 alphabetic ratio (well below 0.70 threshold).
        var jsGarbageSections = new[]
        {
            @"<section>var x = fn() { return [1, 2, 3].map(i =&gt; i * 2 + 1); }; var y = { k1: 'v1', k2: [4, 5, 6], k3: fn(a, b) { return a &gt; b ? a : b; } };</section>",
            @"<section>if (x !== null &amp;&amp; y &gt;= 10) { z = arr.reduce((a, b) =&gt; a + b, 0); } else { z = x ? [1, 2, 3] : []; w = { a: 1, b: 2, c: { d: 3, e: [4, 5] } }; }</section>",
            @"<section>const { a, b, ...rest } = obj; let q = [1, 2, ...a, ...b]; r = { ...rest, x: 10, y: 20 }; s = q.filter(v =&gt; v &gt; 5).map(v =&gt; v * 2);</section>",
            @"<section>for (let i = 0; i &lt; arr.length; i++) { if (arr[i] % 2 === 0) { res.push(arr[i] * 3 + 1); } else { res.push(arr[i] / 2 - 1); } cnt++; }</section>",
            @"<section>switch (x) { case 1: y = [2, 3]; break; case 2: y = { a: 4, b: 5 }; break; default: y = fn(x) =&gt; x * x + x / 2 - 1; z = !y ? 0 : 1; }</section>",
            @"<section>obj = { fn: (x, y) =&gt; { return x ** 2 + y ** 2; }, g: [1, 2, 3].join(','), h: ""k=v&amp;a=b&amp;c=d"", i: typeof x !== 'undefined' ? x : null };</section>"
        };

        var html = $@"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <section class='wrapper'>
                    <section class='js-bundle'>
                        {string.Join("\n", jsGarbageSections)}
                    </section>
                </section>
            </body></html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        // Assert - L3 quality gate should reject JS garbage (non-alphabetic ratio > 30%)
        result.Should().BeNull(
            "Level 3 extraction of JS text nodes should fail the alphabetic ratio quality check");
    }

    [Fact]
    public async Task ExtractAsync_Level3_AcceptsValidTextNodesWhenUpperLevelsFail()
    {
        // Arrange - Same structure that forces L3, but with genuine prose as text nodes.
        // L1 fails: no <p>/<blockquote>/<li> with >50 chars text.
        // L2 fails: no <div> elements at all.
        // L2.5 fails: no <p> elements, so FindLargestParagraphBlock returns empty.
        // L3 picks up text nodes inside <section> elements and they pass all quality checks.
        //
        // Uses <section> wrappers (not <div>) to bypass L2 entirely, same as rejection test.
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        // Real prose as direct text inside <section> elements, each >100 chars.
        // Good alphabetic ratio, varied first words, sufficient average paragraph length.
        var proseSections = new[]
        {
            "<section>Scientists at the marine research station announced a remarkable discovery this week. A previously unknown species of deep-sea jellyfish was found thriving near hydrothermal vents at extreme depths.</section>",
            "<section>The creature displays an unusual bioluminescent pattern that researchers believe serves as both a defense mechanism and a method of communication with others of its kind in the darkness below.</section>",
            "<section>Funding for the expedition came from an international coalition of universities and conservation groups. Researchers spent nearly three months aboard a specially equipped vessel in the southern Pacific Ocean.</section>",
            "<section>Marine biologists emphasized that the finding highlights vast gaps in our understanding of ocean ecosystems. Entire communities of organisms may exist in regions that remain completely unexplored today.</section>",
            "<section>Publication of the formal species description is expected later this year in a leading peer-reviewed journal. Several follow-up expeditions are already being planned for the next research season ahead.</section>",
            "<section>Conservation advocates have called for expanded protections around deep-sea hydrothermal vent systems. They argue that mining and industrial activities could devastate fragile habitats before they are even cataloged.</section>"
        };

        var html = $@"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <section class='wrapper'>
                    <section class='text-block'>
                        {string.Join("\n", proseSections)}
                    </section>
                </section>
            </body></html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        // Assert - L3 should extract valid prose and quality gate should accept it
        result.Should().NotBeNull(
            "Level 3 extraction of valid prose text nodes should pass quality validation");
        result!.Paragraphs.Count.Should().BeGreaterThanOrEqualTo(3,
            "enough prose content should produce multiple paragraphs");
    }

    #endregion
}
