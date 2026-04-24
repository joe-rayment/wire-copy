// Educational and personal use only.

namespace TermReader.Domain.Entities.Browser;

/// <summary>
/// Represents clean, readable article content extracted from a web page.
/// Used for the "Reader View" mode.
/// </summary>
public class ReadableContent
{
    /// <summary>
    /// Article title.
    /// </summary>
    public string Title { get; private set; }

    /// <summary>
    /// Author name (if available).
    /// </summary>
    public string? Author { get; private set; }

    /// <summary>
    /// Publication date (if available).
    /// </summary>
    public DateTime? PublishedDate { get; private set; }

    /// <summary>
    /// Clean text content with formatting removed.
    /// </summary>
    public string CleanedText { get; private set; }

    /// <summary>
    /// Content split into paragraphs for better rendering.
    /// </summary>
    public IReadOnlyList<string> Paragraphs { get; private set; }

    /// <summary>
    /// Word count of the content.
    /// </summary>
    public int WordCount { get; private set; }

    /// <summary>
    /// Estimated reading time in minutes (assuming 200 words/minute).
    /// </summary>
    public int EstimatedReadingMinutes { get; private set; }

    /// <summary>
    /// Whether paywall indicators were detected and content appears truncated.
    /// </summary>
    public bool IsPaywalled { get; private set; }

    private ReadableContent(
        string title,
        string cleanedText,
        List<string> paragraphs,
        string? author = null,
        DateTime? publishedDate = null,
        bool isPaywalled = false)
    {
        Title = title;
        CleanedText = cleanedText;
        Paragraphs = paragraphs;
        Author = author;
        PublishedDate = publishedDate;
        IsPaywalled = isPaywalled;

        // Calculate word count
        WordCount = cleanedText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        // Estimate reading time (200 words per minute)
        EstimatedReadingMinutes = Math.Max(1, WordCount / 200);
    }

    /// <summary>
    /// Creates a ReadableContent instance.
    /// </summary>
    public static ReadableContent Create(
        string title,
        string cleanedText,
        List<string> paragraphs,
        string? author = null,
        DateTime? publishedDate = null,
        bool isPaywalled = false)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title cannot be empty", nameof(title));

        if (string.IsNullOrWhiteSpace(cleanedText))
            throw new ArgumentException("Content cannot be empty", nameof(cleanedText));

        if (paragraphs == null || paragraphs.Count == 0)
            throw new ArgumentException("Paragraphs cannot be empty", nameof(paragraphs));

        return new ReadableContent(title, cleanedText, paragraphs, author, publishedDate, isPaywalled);
    }

    /// <summary>
    /// Gets a preview of the content (first 200 characters).
    /// </summary>
    public string GetPreview(int maxLength = 200)
    {
        if (CleanedText.Length <= maxLength)
            return CleanedText;

        return CleanedText.Substring(0, maxLength) + "\u2026";
    }

    /// <summary>
    /// Gets formatted metadata string for display.
    /// Example: "By Jane Doe · Jan 22, 2024 · 5 min read"
    /// </summary>
    public string GetMetadataString()
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(Author))
            parts.Add($"By {Author}");

        if (PublishedDate.HasValue)
            parts.Add(PublishedDate.Value.ToString("MMM dd, yyyy"));

        parts.Add($"{EstimatedReadingMinutes} min read");

        return string.Join(" \u00b7 ", parts);
    }
}
