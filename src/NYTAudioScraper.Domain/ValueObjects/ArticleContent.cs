namespace NYTAudioScraper.Domain.ValueObjects;

/// <summary>
/// Value object representing parsed article content
/// </summary>
public record ArticleContent(
    string RawHtml,
    string PlainText,
    int CharacterCount,
    int WordCount)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(PlainText);
    public decimal EstimatedAudioCost(decimal costPerCharacter = 0.0003m)
        => CharacterCount * costPerCharacter;
}
