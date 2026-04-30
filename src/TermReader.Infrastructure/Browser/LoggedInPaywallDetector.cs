// Licensed under the MIT License. See LICENSE in the repository root.

using HtmlAgilityPack;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Pure-function detector that decides whether a page on a paywalled domain
/// looks like the user is logged in and the markup is a full article (not a
/// gate). Used by <see cref="AutoCookieRefresher"/> to decide when to opportunistically
/// import cookies from the foreground browser session.
///
/// <para>
/// Detection is intentionally conservative. We accept false negatives (we miss
/// some logged-in pages) in exchange for very few false positives (we never
/// overwrite real cookies with anonymous ones). The contract is:
/// </para>
///
/// <list type="bullet">
///   <item>Site-specific signal: an account / profile link is present in the markup.</item>
///   <item>No paywall gate: <see cref="ReadableContentExtractor.HasPaywallElements"/> returns false.</item>
///   <item>Substantial body: visible text contains more than <see cref="MinWordCount"/> words.</item>
/// </list>
///
/// <para>
/// All three must hold for <see cref="LooksLoggedIn"/> to return true.
/// </para>
/// </summary>
internal static class LoggedInPaywallDetector
{
    /// <summary>
    /// Minimum visible-text word count required before we treat the page as a
    /// full (non-gated) article. NYT preview pages typically clock in well below
    /// this; full articles are 800+ words on average.
    /// </summary>
    public const int MinWordCount = 500;

    private static readonly string[] AccountLinkXPaths =
    {
        // NYT account dropdown / profile link.
        "//a[contains(@href, '/account/')]",
        "//a[contains(@href, 'myaccount.nytimes.com')]",

        // Generic profile / account / logout markers used across paywalled sites.
        "//a[contains(@href, '/account')]",
        "//a[contains(@href, '/profile')]",
        "//a[contains(@href, '/logout')]",
        "//a[contains(@href, '/sign-out')]",
        "//a[contains(@href, '/signout')]",

        // WSJ / WaPo: data-test or aria-label markers exposed when signed in.
        "//*[@data-test='signed-in-user']",
        "//*[@aria-label='Account menu']",
    };

    /// <summary>
    /// Returns true when the HTML looks like the user is logged in on a
    /// paywalled site. Returns false on any of: missing markup, paywall gate
    /// present, missing account link, or thin body content.
    /// </summary>
    public static bool LooksLoggedIn(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        HtmlDocument doc;
        try
        {
            doc = new HtmlDocument();
            doc.LoadHtml(html);
        }
        catch
        {
            return false;
        }

        // Generic gate signal — if present, definitely not logged-in for this article.
        if (ReadableContentExtractor.HasPaywallElements(html))
        {
            return false;
        }

        // Site-specific account link signal.
        var hasAccountLink = AccountLinkXPaths.Any(
            xpath => doc.DocumentNode.SelectSingleNode(xpath) != null);
        if (!hasAccountLink)
        {
            return false;
        }

        // Generic content-volume signal.
        var visibleText = doc.DocumentNode.InnerText ?? string.Empty;
        var wordCount = CountWords(visibleText);
        return wordCount > MinWordCount;
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var count = 0;
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }
}
