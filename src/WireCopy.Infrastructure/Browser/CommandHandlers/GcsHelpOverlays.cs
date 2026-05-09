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

        var fieldWidth = Math.Min(GcsCopy.MaxAbsoluteWidth, Math.Max(40, Console.WindowWidth) - 6);
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
    /// Renders the verbose plain-language overlay for the GCS bucket name
    /// field. Mirrors the SA-key overlay structure so both feel like
    /// siblings.
    /// </summary>
    public static void RenderBucketHelp(ThemePalette palette, int startRow)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var fieldWidth = Math.Min(GcsCopy.MaxAbsoluteWidth, Math.Max(40, Console.WindowWidth) - 6);
        var maxCopy = GcsCopy.MaxCopyWidth(fieldWidth);

        ClearOverlay(startRow, BucketOverlayRows);

        var lines = new List<string>
        {
            $"{palette.HeaderTitleFg.AnsiFg}Google Cloud Storage bucket — what is this?{Reset}",
            string.Empty,
            $"{palette.PrimaryText.AnsiFg}Plain-English version{Reset}",
        };
        lines.AddRange(WrapBody(
            palette,
            "A bucket is a folder in Google Cloud Storage that holds your podcast " +
            "audio files and RSS feed. The bucket name is part of the public URL.",
            maxCopy));

        lines.Add(string.Empty);
        lines.Add($"{palette.PrimaryText.AnsiFg}Naming rules{Reset}");
        lines.AddRange(WrapBody(
            palette,
            "Lowercase letters, numbers, hyphens. 3-63 characters. Globally unique.",
            maxCopy));

        lines.Add(string.Empty);
        lines.Add($"{palette.PrimaryText.AnsiFg}Don't have one yet?{Reset}");
        lines.AddRange(WrapBody(
            palette,
            "Type the name you want and press Enter. We'll create it in your project.",
            maxCopy));

        lines.Add(string.Empty);
        lines.Add($"{palette.PrimaryText.AnsiFg}Where to find an existing one{Reset}");
        lines.AddRange(WrapBody(
            palette,
            "console.cloud.google.com -> Cloud Storage -> Buckets. Copy the value " +
            "in the Name column (not the URL). gs:// prefix is fine — we'll strip it.",
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
        var width = Math.Max(20, Console.WindowWidth - 1);
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
                Console.SetCursorPosition(0, r);
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
                Console.SetCursorPosition(2, startRow + i);
            }
            catch (ArgumentOutOfRangeException)
            {
                break;
            }

            Console.Write(lines[i]);
        }
    }
}
