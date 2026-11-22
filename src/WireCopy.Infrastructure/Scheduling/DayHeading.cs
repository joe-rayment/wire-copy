// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-98qqi.1 — a paper prints the same section under a different heading on some days:
/// the New York Times has "Business Day" on weekdays and "Sunday Business" on Sundays (the
/// routine on the Mac lost the whole section on Sun 22 Nov 2026). Two headings name the same
/// section when they are the same words once the words that only say which day it is are set
/// aside.
/// </summary>
internal static class DayHeading
{
    /// <summary>Words that say when a section is printed, not what it is about.</summary>
    private static readonly HashSet<string> DayWords = new(StringComparer.Ordinal)
    {
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "day", "daily", "weekday", "weekend", "today", "todays", "the",
    };

    /// <summary>
    /// The words of a heading that name the section, lower-cased, sorted and joined by a space:
    /// "Business Day", "Sunday Business" and "SundayBusiness" all give "business". Empty when the
    /// heading has nothing but day words ("Sunday", "The Daily").
    /// </summary>
    /// <param name="heading">A section heading as the page or the saved layout spells it.</param>
    /// <returns>The key two headings of one section share.</returns>
    internal static string Key(string? heading)
    {
        if (string.IsNullOrWhiteSpace(heading))
        {
            return string.Empty;
        }

        var words = new List<string>();
        var word = new StringBuilder();
        var previous = '\0';
        foreach (var c in heading)
        {
            if (c is '\'' or '’')
            {
                continue; // "Today's" is one word
            }

            // A word ends at anything that is not a letter or digit, and where a capital follows
            // a small letter: "SundayBusiness" is two words.
            var letter = char.IsLetterOrDigit(c);
            if (!letter || (char.IsUpper(c) && char.IsLower(previous)))
            {
                Flush(word, words);
            }

            if (letter)
            {
                word.Append(char.ToLowerInvariant(c));
            }

            previous = c;
        }

        Flush(word, words);
        words.Sort(StringComparer.Ordinal);
        return string.Join(' ', words);
    }

    private static void Flush(StringBuilder word, List<string> words)
    {
        if (word.Length > 0 && !DayWords.Contains(word.ToString()))
        {
            words.Add(word.ToString());
        }

        word.Clear();
    }
}
