// <copyright file="ReadableContentTests.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using FluentAssertions;
using NYTAudioScraper.Domain.Entities.Browser;
using Xunit;

namespace NYTAudioScraper.Tests.Browser;

public class ReadableContentTests
{
    [Fact]
    public void Create_WithValidData_ReturnsContent()
    {
        // Arrange
        var title = "Test Article";
        var text = "This is the article content with multiple words for testing.";
        var paragraphs = new List<string> { "Paragraph 1", "Paragraph 2" };

        // Act
        var content = ReadableContent.Create(title, text, paragraphs);

        // Assert
        content.Should().NotBeNull();
        content.Title.Should().Be(title);
        content.CleanedText.Should().Be(text);
        content.Paragraphs.Should().HaveCount(2);
    }

    [Fact]
    public void Create_WithAuthorAndDate_SetsOptionalFields()
    {
        // Arrange
        var title = "Test Article";
        var text = "Content here.";
        var paragraphs = new List<string> { "Content here." };
        var author = "Jane Doe";
        var publishedDate = new DateTime(2024, 1, 22);

        // Act
        var content = ReadableContent.Create(title, text, paragraphs, author, publishedDate);

        // Assert
        content.Author.Should().Be(author);
        content.PublishedDate.Should().Be(publishedDate);
    }

    [Fact]
    public void Create_WithEmptyTitle_ThrowsArgumentException()
    {
        // Arrange
        var text = "Content";
        var paragraphs = new List<string> { "Content" };

        // Act & Assert
        var act = () => ReadableContent.Create("", text, paragraphs);
        act.Should().Throw<ArgumentException>().WithParameterName("title");
    }

    [Fact]
    public void Create_WithWhitespaceTitle_ThrowsArgumentException()
    {
        // Arrange
        var text = "Content";
        var paragraphs = new List<string> { "Content" };

        // Act & Assert
        var act = () => ReadableContent.Create("   ", text, paragraphs);
        act.Should().Throw<ArgumentException>().WithParameterName("title");
    }

    [Fact]
    public void Create_WithEmptyContent_ThrowsArgumentException()
    {
        // Arrange
        var paragraphs = new List<string> { "Paragraph" };

        // Act & Assert
        var act = () => ReadableContent.Create("Title", "", paragraphs);
        act.Should().Throw<ArgumentException>().WithParameterName("cleanedText");
    }

    [Fact]
    public void Create_WithEmptyParagraphs_ThrowsArgumentException()
    {
        // Act & Assert
        var act = () => ReadableContent.Create("Title", "Content", new List<string>());
        act.Should().Throw<ArgumentException>().WithParameterName("paragraphs");
    }

    [Fact]
    public void Create_WithNullParagraphs_ThrowsArgumentException()
    {
        // Act & Assert
        var act = () => ReadableContent.Create("Title", "Content", null!);
        act.Should().Throw<ArgumentException>().WithParameterName("paragraphs");
    }

    [Fact]
    public void WordCount_CalculatesCorrectly()
    {
        // Arrange
        var text = "One two three four five six seven eight nine ten";
        var paragraphs = new List<string> { text };

        // Act
        var content = ReadableContent.Create("Title", text, paragraphs);

        // Assert
        content.WordCount.Should().Be(10);
    }

    [Fact]
    public void WordCount_HandlesMultipleSpaces()
    {
        // Arrange
        var text = "One  two   three    four";
        var paragraphs = new List<string> { text };

        // Act
        var content = ReadableContent.Create("Title", text, paragraphs);

        // Assert
        content.WordCount.Should().Be(4);
    }

    [Fact]
    public void EstimatedReadingMinutes_CalculatesCorrectly()
    {
        // Arrange - 400 words should be 2 minutes at 200 wpm
        var words = string.Join(" ", Enumerable.Repeat("word", 400));
        var paragraphs = new List<string> { words };

        // Act
        var content = ReadableContent.Create("Title", words, paragraphs);

        // Assert
        content.EstimatedReadingMinutes.Should().Be(2);
    }

    [Fact]
    public void EstimatedReadingMinutes_MinimumIsOneMinute()
    {
        // Arrange - Very short content
        var text = "Short";
        var paragraphs = new List<string> { text };

        // Act
        var content = ReadableContent.Create("Title", text, paragraphs);

        // Assert
        content.EstimatedReadingMinutes.Should().Be(1);
    }

    [Fact]
    public void GetPreview_ReturnsTruncatedContent()
    {
        // Arrange
        var text = new string('x', 500);
        var paragraphs = new List<string> { text };
        var content = ReadableContent.Create("Title", text, paragraphs);

        // Act
        var preview = content.GetPreview(100);

        // Assert
        preview.Should().HaveLength(103); // 100 chars + "..."
        preview.Should().EndWith("...");
    }

    [Fact]
    public void GetPreview_ShortContent_ReturnsFullText()
    {
        // Arrange
        var text = "Short content";
        var paragraphs = new List<string> { text };
        var content = ReadableContent.Create("Title", text, paragraphs);

        // Act
        var preview = content.GetPreview(200);

        // Assert
        preview.Should().Be(text);
        preview.Should().NotEndWith("...");
    }

    [Fact]
    public void GetPreview_DefaultLength_Is200()
    {
        // Arrange
        var text = new string('x', 500);
        var paragraphs = new List<string> { text };
        var content = ReadableContent.Create("Title", text, paragraphs);

        // Act
        var preview = content.GetPreview();

        // Assert
        preview.Should().HaveLength(203); // 200 chars + "..."
    }

    [Fact]
    public void GetMetadataString_WithAllFields_FormatsCorrectly()
    {
        // Arrange
        var content = ReadableContent.Create(
            "Title",
            "Content here for testing the metadata string output.",
            new List<string> { "Content here for testing the metadata string output." },
            "Jane Doe",
            new DateTime(2024, 1, 22));

        // Act
        var metadata = content.GetMetadataString();

        // Assert
        metadata.Should().Contain("By Jane Doe");
        metadata.Should().Contain("Jan 22, 2024");
        metadata.Should().Contain("min read");
    }

    [Fact]
    public void GetMetadataString_WithoutAuthor_OmitsAuthor()
    {
        // Arrange
        var content = ReadableContent.Create(
            "Title",
            "Content",
            new List<string> { "Content" },
            null,
            new DateTime(2024, 1, 22));

        // Act
        var metadata = content.GetMetadataString();

        // Assert
        metadata.Should().NotContain("By");
        metadata.Should().Contain("Jan 22, 2024");
    }

    [Fact]
    public void GetMetadataString_WithoutDate_OmitsDate()
    {
        // Arrange
        var content = ReadableContent.Create(
            "Title",
            "Content",
            new List<string> { "Content" },
            "Jane Doe",
            null);

        // Act
        var metadata = content.GetMetadataString();

        // Assert
        metadata.Should().Contain("By Jane Doe");
        metadata.Should().NotContain("2024");
    }

    [Fact]
    public void GetMetadataString_MinimalContent_ShowsReadingTime()
    {
        // Arrange
        var content = ReadableContent.Create(
            "Title",
            "Content",
            new List<string> { "Content" });

        // Act
        var metadata = content.GetMetadataString();

        // Assert
        metadata.Should().Contain("min read");
    }
}
