// Educational and personal use only.

using System.Text;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Splits text into chunks suitable for TTS API requests, respecting logical boundaries.
/// </summary>
internal static class TextChunker
{
    /// <summary>
    /// Splits text into chunks of at most <paramref name="maxChunkSize"/> characters.
    /// Splits at paragraph boundaries first, then sentence boundaries, then word boundaries,
    /// then hard-splits as a last resort. No empty chunks, no content loss.
    /// </summary>
    public static IReadOnlyList<string> ChunkText(string text, int maxChunkSize = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChunkSize, 1);

        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (text.Length <= maxChunkSize)
        {
            return [text];
        }

        var chunks = new List<string>();
        var paragraphs = SplitParagraphs(text);

        var currentChunk = new StringBuilder(maxChunkSize);

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length == 0)
            {
                continue;
            }

            // If adding this paragraph (with separator) fits, append it
            if (currentChunk.Length + paragraph.Length + (currentChunk.Length > 0 ? 2 : 0) <= maxChunkSize)
            {
                if (currentChunk.Length > 0)
                {
                    currentChunk.Append("\n\n");
                }

                currentChunk.Append(paragraph);
                continue;
            }

            // Flush current chunk if non-empty
            if (currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString());
                currentChunk.Clear();
            }

            // If the paragraph itself fits in a single chunk, start a new chunk with it
            if (paragraph.Length <= maxChunkSize)
            {
                currentChunk.Append(paragraph);
                continue;
            }

            // Paragraph is too large - split by sentences
            SplitLargeBlock(paragraph, maxChunkSize, chunks);
        }

        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString());
        }

        return chunks;
    }

    private static List<string> SplitParagraphs(string text)
    {
        return text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.None)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
    }

    private static void SplitLargeBlock(string block, int maxChunkSize, List<string> chunks)
    {
        var sentences = SplitSentences(block);
        var currentChunk = new StringBuilder(maxChunkSize);

        foreach (var sentence in sentences)
        {
            if (sentence.Length == 0)
            {
                continue;
            }

            if (currentChunk.Length + sentence.Length + (currentChunk.Length > 0 ? 1 : 0) <= maxChunkSize)
            {
                if (currentChunk.Length > 0)
                {
                    currentChunk.Append(' ');
                }

                currentChunk.Append(sentence);
                continue;
            }

            if (currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString());
                currentChunk.Clear();
            }

            if (sentence.Length <= maxChunkSize)
            {
                currentChunk.Append(sentence);
                continue;
            }

            // Sentence is too large - split by words
            SplitByWords(sentence, maxChunkSize, chunks);
        }

        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString());
        }
    }

    private static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            current.Append(text[i]);

            // Sentence ends at '.', '!', or '?' followed by whitespace or end of text
            if (text[i] is '.' or '!' or '?' &&
                (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                sentences.Add(current.ToString().Trim());
                current.Clear();

                // Skip trailing whitespace
                while (i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
                {
                    i++;
                }
            }
        }

        if (current.Length > 0)
        {
            var remaining = current.ToString().Trim();
            if (remaining.Length > 0)
            {
                sentences.Add(remaining);
            }
        }

        return sentences;
    }

    private static void SplitByWords(string text, int maxChunkSize, List<string> chunks)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentChunk = new StringBuilder(maxChunkSize);

        foreach (var word in words)
        {
            if (currentChunk.Length + word.Length + (currentChunk.Length > 0 ? 1 : 0) <= maxChunkSize)
            {
                if (currentChunk.Length > 0)
                {
                    currentChunk.Append(' ');
                }

                currentChunk.Append(word);
                continue;
            }

            if (currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString());
                currentChunk.Clear();
            }

            if (word.Length <= maxChunkSize)
            {
                currentChunk.Append(word);
                continue;
            }

            // Word is too large - hard-split
            HardSplit(word, maxChunkSize, chunks);
        }

        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString());
        }
    }

    private static void HardSplit(string text, int maxChunkSize, List<string> chunks)
    {
        for (var i = 0; i < text.Length; i += maxChunkSize)
        {
            var length = Math.Min(maxChunkSize, text.Length - i);
            chunks.Add(text.Substring(i, length));
        }
    }
}
