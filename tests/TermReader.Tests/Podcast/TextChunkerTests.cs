// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using TermReader.Infrastructure.Podcast;
using Xunit;

namespace TermReader.Tests.Podcast;

[Trait("Category", "Unit")]
public class TextChunkerTests
{
    [Fact]
    public void ChunkText_ShortText_ReturnsSingleChunk()
    {
        var result = TextChunker.ChunkText("Hello world", 4096);

        result.Should().HaveCount(1);
        result[0].Should().Be("Hello world");
    }

    [Fact]
    public void ChunkText_EmptyText_ReturnsEmptyList()
    {
        var result = TextChunker.ChunkText(string.Empty);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ChunkText_NullText_ReturnsEmptyList()
    {
        var result = TextChunker.ChunkText(null!);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ChunkText_ExactLimitText_ReturnsSingleChunk()
    {
        var text = new string('a', 100);
        var result = TextChunker.ChunkText(text, 100);

        result.Should().HaveCount(1);
        result[0].Should().Be(text);
    }

    [Fact]
    public void ChunkText_SplitsAtParagraphBoundary()
    {
        var paragraph1 = new string('a', 50);
        var paragraph2 = new string('b', 50);
        var text = $"{paragraph1}\n\n{paragraph2}";

        var result = TextChunker.ChunkText(text, 60);

        result.Should().HaveCount(2);
        result[0].Should().Contain("a");
        result[1].Should().Contain("b");
    }

    [Fact]
    public void ChunkText_LongParagraph_SplitsAtSentenceBoundary()
    {
        var sentence1 = new string('a', 40) + ". ";
        var sentence2 = new string('b', 40) + ". ";
        var text = sentence1 + sentence2;

        var result = TextChunker.ChunkText(text, 50);

        result.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void ChunkText_LongSentence_SplitsAtWordBoundary()
    {
        var words = string.Join(" ", Enumerable.Repeat("word", 50));
        var result = TextChunker.ChunkText(words, 30);

        result.Should().HaveCountGreaterThan(1);
        result.Should().AllSatisfy(chunk => chunk.Length.Should().BeLessThanOrEqualTo(30));
    }

    [Fact]
    public void ChunkText_VeryLongWord_HardSplitsAtMaxSize()
    {
        var longWord = new string('x', 200);
        var result = TextChunker.ChunkText(longWord, 50);

        result.Should().HaveCountGreaterThan(1);
        result.Should().AllSatisfy(chunk => chunk.Length.Should().BeLessThanOrEqualTo(50));
    }

    [Fact]
    public void ChunkText_AllChunksRespectMaxSize()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 20).Select(i =>
            string.Join(". ", Enumerable.Range(0, 5).Select(j => $"Sentence {i}-{j} with some content")) + "."));

        var maxSize = 100;
        var result = TextChunker.ChunkText(text, maxSize);

        result.Should().AllSatisfy(chunk => chunk.Length.Should().BeLessThanOrEqualTo(maxSize));
    }

    [Fact]
    public void ChunkText_MaxChunkSizeLessThanOne_ThrowsArgumentOutOfRange()
    {
        var act = () => TextChunker.ChunkText("text", 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ChunkText_ConcatenationRecoversOriginalContent()
    {
        var text = "First paragraph with content.\n\nSecond paragraph with more content.\n\nThird paragraph.";
        var result = TextChunker.ChunkText(text, 50);

        var concatenated = string.Join(string.Empty, result);
        concatenated.Should().Contain("First paragraph");
        concatenated.Should().Contain("Second paragraph");
        concatenated.Should().Contain("Third paragraph");
    }
}
