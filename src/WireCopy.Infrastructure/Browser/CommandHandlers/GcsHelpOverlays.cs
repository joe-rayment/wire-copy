// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Beginner-first help overlays for the GCS setup flow (workspace-ur5h).
///
/// <para>
/// Both the GCS service-account row and the bucket row carry a
/// <see cref="UI.Components.FormFieldConfig.OnExtraKey"/> hook that renders
/// a plain-language overlay when the user presses <c>?</c>. The previous
/// implementation only had a bucket overlay (workspace-dlq5); the user
/// reported the SA-key flow assumed knowledge a beginner doesn't have, so
/// this file adds the SA-key overlay and width-wraps both so nothing
/// truncates at narrow terminals.
/// </para>
/// </summary>
internal static class GcsHelpOverlays
{
    /// <summary>
    /// Number of rows the bucket help overlay occupies. Used by callers to
    /// size the wipe region after dismissal.
    /// </summary>
    public const int BucketOverlayRows = 20;

    /// <summary>
    /// Number of rows the service-account help overlay occupies.
    /// </summary>
    public const int ServiceAccountOverlayRows = 22;

    private const string Reset = "\x1b[0m";

    /// <summary>
    /// Renders the verbose plain-language overlay for the GCS service
    /// account JSON field. Targets a beginner who has never opened Google
    /// Cloud Console — explains what GCS is, what a service account key is,
    /// where to get one, and which role to grant.
    /// </summary>
    public static void RenderServiceAccountHelp(ThemePalette palette, int startRow)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var fieldWidth = Math.Min(GcsCopy.MaxAbsoluteWidth, Math.Max(40, UI.OverlayViewport.Width) - 6);
        var maxCopy = GcsCopy.MaxCopyWidth(fieldWidth);

        ClearOverlay(startRow, ServiceAccountOverlayRows);

        var lines = new List<string>
        {
            $"{palette.HeaderTitleFg.AnsiFg}Google Cloud service account — what is this?{Reset}",
            string.Empty,
            $"{palette.PrimaryText.AnsiFg}Plain-English version{Reset}",
        };
        lines.AddRange(WrapBody(
            palette,
            "WireCopy uploads your podcast audio + RSS feed to Google Cloud Storage. " +
            "A service-account key is a small JSON file Google gives you that lets " +
            "WireCopy authenticate without a password.",
            maxCopy));

        lines.Add(string.Empty);
        lines.Add($"{palette.PrimaryText.AnsiFg}Don't have a Google Cloud account?{Reset}");
        lines.AddRange(WrapBody(
            palette,
            "Sign up at cloud.google.com (free tier covers podcast hosting). " +
            "Then create a project — any name will do.",
            maxCopy));

        lines.Add(string.Empty);
        lines.Add($"{palette.PrimaryText.AnsiFg}How to get the JSON file{Reset}");
        lines.AddRange(WrapBody(
            palette,
            "1. console.cloud.google.com -> IAM & Admin -> Service accounts.",
            maxCopy));
        lines.AddRange(WrapBody(
            palette,
            "2. Create one named wirecopy-podcast.",
            maxCopy));
        lines.AddRange(WrapBody(
            palette,
            "3. Grant role: Storage Object Admin.",
            maxCopy));
        lines.AddRange(WrapBody(
            palette,
            "4. Open it -> Keys -> Add Key -> Create new key -> JSON.",
            maxCopy));

        lines.Add(string.Empty);
        lines.Add($"{palette.GetDimFg().AnsiFg}{GcsCopy.FitOrShorten("Press any key to close this guide.", "Any key closes.", maxCopy)}{Reset}");

        WriteLines(startRow, lines, ServiceAccountOverlayRows);
    }

    /// <summary>
    /// Renders the verbose plain-language overlay for the GCS bucket field.
    /// workspace-spue: this prompt asks for the full PUBLIC URL the feed
    /// will live at, not a bare bucket name — the overlay must explain
    /// both the URL form and the bucket-name-naming rules underneath it.
    /// </summary>
    public static void RenderBucketHelp(ThemePalette palette, int startRow)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var fieldWidth = Math.Min(GcsCopy.MaxAbsoluteWidth, Math.Max(40, UI.OverlayViewport.Width) - 6);
        var maxCopy = GcsCopy.MaxCopyWidth(fieldWidth);

        ClearOverlay(startRow, BucketOverlayRows);

        var lines = new List<string>
        {
            $"{palette.HeaderTitleFg.AnsiFg}Public feed URL — what is this?{Reset}",
            string.Empty,
            $"{palette.PrimaryText.AnsiFg}Plain-English version{Reset}",
        };
        lines.AddRange(WrapBody(
            palette,
            "This is the address where your podcast RSS feed will live publicly. " +
            "Anyone (Apple Podcasts, Overcast, you) subscribes by pasting this URL.",
            maxCopy));

        lines.Add(string.Empty);
        lines.Add($"{palette.PrimaryText.AnsiFg}Accepted forms{Reset}");
        lines.AddRange(WrapBody(
            palette,
            "https://storage.googleapis.com/<bucket>/feed.xml  (recommended)",
            maxCopy));
        lines.AddRange(WrapBody(
            palette,
            "https://<bucket>.storage.googleapis.com/feed.xml",
            maxCopy));
        lines.AddRange(WrapBody(
            palette,
            "gs://<bucket>/feed.xml   ·   <bucket>   (also accepted)",
            maxCopy));

        lines.Add(string.Empty);
        lines.Add($"{palette.PrimaryText.AnsiFg}Bucket naming rules{Reset}");
        lines.AddRange(WrapBody(
            palette,
            "Lowercase letters, numbers, hyphens, underscores, dots. 3-63 chars. " +
            "Must start and end with a letter or number. Globally unique.",
            maxCopy));

        lines.Add(string.Empty);
        lines.Add($"{palette.PrimaryText.AnsiFg}Don't have a bucket yet?{Reset}");
        lines.AddRange(WrapBody(
            palette,
            "Accept the suggested URL with Enter — we'll create the bucket in " +
            "your project. Or pick your own name inside the URL form above.",
            maxCopy));

        lines.Add(string.Empty);
        lines.Add($"{palette.GetDimFg().AnsiFg}{GcsCopy.FitOrShorten("Press any key to close this guide.", "Any key closes.", maxCopy)}{Reset}");

        WriteLines(startRow, lines, BucketOverlayRows);
    }

    /// <summary>
    /// Wipes the rows used by an overlay so the live screen is restored
    /// when the user dismisses the modal. Wipes BOTH the requested region
    /// and the slid-up region computed by <see cref="WriteLines"/>: at narrow
    /// terminals the overlay slides up to keep its footer visible, so
    /// dismissal must wipe whichever region was actually painted.
    /// </summary>
    public static void ClearOverlay(int startRow, int rowCount)
    {
        var width = Math.Max(20, UI.OverlayViewport.Width - 1);
        var available = Math.Max(1, Console.WindowHeight - 1);
        var actualStart = Math.Max(1, Math.Min(startRow, available - rowCount));
        var topRow = Math.Min(startRow, actualStart);
        var bottomRow = Math.Max(startRow + rowCount, actualStart + rowCount);

        for (var r = topRow; r < bottomRow; r++)
        {
            if (r < 0 || r >= Console.WindowHeight)
            {
                continue;
            }

            try
            {
                Console.SetCursorPosition(UI.OverlayViewport.Left, r);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            Console.Write(new string(' ', width));
        }
    }

    private static IEnumerable<string> WrapBody(ThemePalette palette, string text, int maxCopy)
    {
        foreach (var line in GcsCopy.WrapToWidth(text, maxCopy))
        {
            yield return $"{palette.SecondaryText.AnsiFg}  {line}{Reset}";
        }
    }

    private static void WriteLines(int startRow, IReadOnlyList<string> lines, int budget)
    {
        // Slide the overlay up so the entire block (including the "press any
        // key" footer) fits inside the terminal — at width 80 / height 35
        // the helpRow lands too close to the bottom and the footer line was
        // overwriting itself onto the line above.
        var totalRows = Math.Min(lines.Count, budget);
        var available = Math.Max(1, Console.WindowHeight - 1);
        if (startRow + totalRows > available)
        {
            startRow = Math.Max(1, available - totalRows);
        }

        var max = Math.Min(totalRows, Math.Max(1, Console.WindowHeight - startRow - 1));
        for (var i = 0; i < max; i++)
        {
            try
            {
                Console.SetCursorPosition(UI.OverlayViewport.Left + 2, startRow + i);
            }
            catch (ArgumentOutOfRangeException)
            {
                break;
            }

            Console.Write(lines[i]);
        }
    }
}
