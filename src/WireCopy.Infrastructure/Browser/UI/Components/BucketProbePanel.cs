// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Components;

/// <summary>
/// Reusable rendering helpers for the GCS bucket Setup row's probe / create
/// flow (workspace-dwgl). Centralises the spinner row and the three result
/// panels (Verified / Not Found / Access Denied) so the SettingsCommandHandler
/// stays focused on orchestration.
/// </summary>
internal static class BucketProbePanel
{
    private const string Reset = "\x1b[0m";

    private const int SpinnerIntervalMs = 80;

    /// <summary>
    /// Spinner frames matched to <c>PodcastCommandHandler.SpinnerFrames</c>
    /// so the visual cadence is consistent across the app.
    /// </summary>
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    /// <summary>
    /// Runs <paramref name="work"/> while ticking a single-line spinner row.
    /// Returns the result of the work task; cancellation propagates from
    /// <paramref name="ct"/>.
    /// </summary>
    public static async Task<T> RunWithSpinnerAsync<T>(
        ThemePalette palette,
        string label,
        int row,
        Func<CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(work);

        var workTask = work(ct);
        var frame = 0;

        while (!workTask.IsCompleted)
        {
            DrawSpinner(palette, label, row, SpinnerFrames[frame % SpinnerFrames.Length]);
            try
            {
                await Task.WhenAny(workTask, Task.Delay(SpinnerIntervalMs, ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            frame++;
        }

        // Clear the spinner row once work resolves.
        ClearLine(row);
        return await workTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Render the success ("Verified") panel. Returns the row after the
    /// rendered block so callers can place subsequent prompts.
    /// </summary>
    public static int RenderSuccess(
        ThemePalette palette,
        int startRow,
        string bucketName,
        string projectId,
        string location)
    {
        var row = startRow;
        WriteAt(2, row++, $"{palette.GetSuccessFg().AnsiFg}✓ Verified.{Reset}");
        WriteAt(4, row++, $"{palette.PrimaryText.AnsiFg}Bucket:   {bucketName}{Reset}");
        WriteAt(4, row++, $"{palette.PrimaryText.AnsiFg}Project:  {projectId}{Reset}");
        WriteAt(4, row++, $"{palette.PrimaryText.AnsiFg}Location: {location}{Reset}");
        row++;
        WriteAt(
            4,
            row++,
            $"{palette.GetAccentFg().AnsiFg}[Done]{Reset}   {palette.GetDimFg().AnsiFg}← Enter or Esc{Reset}");
        return row;
    }

    /// <summary>
    /// Render the "bucket not found" panel offering create / edit / cancel.
    /// </summary>
    public static int RenderNotFound(
        ThemePalette palette,
        int startRow,
        string bucketName,
        string projectId)
    {
        var row = startRow;
        WriteAt(2, row++, $"{palette.ErrorFg.AnsiFg}✗ Bucket \"{bucketName}\" doesn't exist yet.{Reset}");
        WriteAt(4, row++, $"{palette.PrimaryText.AnsiFg}We can create it in project {projectId}.{Reset}");
        row++;
        WriteAt(
            4,
            row++,
            $"{palette.GetAccentFg().AnsiFg}[C]{Reset} {palette.PrimaryText.AnsiFg}Create it for us{Reset} · " +
            $"{palette.GetAccentFg().AnsiFg}[E]{Reset} {palette.PrimaryText.AnsiFg}Edit name{Reset} · " +
            $"{palette.GetAccentFg().AnsiFg}[Esc]{Reset} {palette.PrimaryText.AnsiFg}Cancel{Reset}");
        return row;
    }

    /// <summary>
    /// Render the create-confirm panel.
    /// </summary>
    public static int RenderCreateConfirm(
        ThemePalette palette,
        int startRow,
        string bucketName,
        string projectId,
        string location)
    {
        var row = startRow;
        WriteAt(2, row++, $"{palette.PrimaryText.AnsiFg}Create bucket?{Reset}");
        WriteAt(4, row++, $"{palette.PrimaryText.AnsiFg}Name:      {bucketName}{Reset}");
        WriteAt(4, row++, $"{palette.PrimaryText.AnsiFg}Project:   {projectId}{Reset}");
        WriteAt(4, row++, $"{palette.PrimaryText.AnsiFg}Region:    {location}{Reset}");
        WriteAt(4, row++, $"{palette.PrimaryText.AnsiFg}Class:     Standard{Reset}");
        WriteAt(4, row++, $"{palette.PrimaryText.AnsiFg}Access:    Uniform bucket-level (UBLA on){Reset}");
        row++;
        WriteAt(
            4,
            row++,
            $"{palette.GetAccentFg().AnsiFg}[Y]{Reset} {palette.PrimaryText.AnsiFg}Create{Reset} · " +
            $"{palette.GetAccentFg().AnsiFg}[N]{Reset} {palette.PrimaryText.AnsiFg}Cancel{Reset}");
        return row;
    }

    /// <summary>
    /// Render the access-denied panel with the masked SA email.
    /// </summary>
    public static int RenderAccessDenied(
        ThemePalette palette,
        int startRow,
        string bucketName,
        string serviceAccountEmail)
    {
        var row = startRow;
        WriteAt(2, row++, $"{palette.ErrorFg.AnsiFg}✗ Found \"{bucketName}\", but the service account can't access it.{Reset}");
        row++;
        WriteAt(4, row++, $"{palette.PrimaryText.AnsiFg}Missing role: Storage Object Admin (or Object Creator + Viewer){Reset}");
        WriteAt(4, row++, $"{palette.PrimaryText.AnsiFg}Service account: {serviceAccountEmail}{Reset}");
        row++;
        WriteAt(4, row++, $"{palette.SecondaryText.AnsiFg}Fix in GCP: IAM → Grant access → paste the email above → Storage Object Admin.{Reset}");
        row++;
        WriteAt(
            4,
            row++,
            $"{palette.GetAccentFg().AnsiFg}[R]{Reset} {palette.PrimaryText.AnsiFg}Retry{Reset} · " +
            $"{palette.GetAccentFg().AnsiFg}[E]{Reset} {palette.PrimaryText.AnsiFg}Edit name{Reset} · " +
            $"{palette.GetAccentFg().AnsiFg}[Esc]{Reset} {palette.PrimaryText.AnsiFg}Cancel{Reset}");
        return row;
    }

    /// <summary>
    /// Render a generic error panel (used for Timeout / NetworkError /
    /// CredentialsInvalid / BucketCreationFailed). When
    /// <paramref name="allowRetry"/> is <c>false</c> the <c>[R]</c> action is
    /// suppressed — used by the create-failure branch where retrying the same
    /// name will fail identically (e.g. the global-uniqueness conflict).
    /// </summary>
    public static int RenderGenericError(
        ThemePalette palette,
        int startRow,
        string message,
        string? interpretation = null,
        bool allowRetry = true)
    {
        var row = startRow;
        WriteAt(2, row++, $"{palette.ErrorFg.AnsiFg}✗ {message}{Reset}");
        if (!string.IsNullOrWhiteSpace(interpretation))
        {
            WriteAt(4, row++, $"{palette.SecondaryText.AnsiFg}{interpretation}{Reset}");
        }

        row++;
        var actions = allowRetry
            ? $"{palette.GetAccentFg().AnsiFg}[R]{Reset} {palette.PrimaryText.AnsiFg}Retry{Reset} · " +
              $"{palette.GetAccentFg().AnsiFg}[E]{Reset} {palette.PrimaryText.AnsiFg}Edit name{Reset} · " +
              $"{palette.GetAccentFg().AnsiFg}[Esc]{Reset} {palette.PrimaryText.AnsiFg}Cancel{Reset}"
            : $"{palette.GetAccentFg().AnsiFg}[E]{Reset} {palette.PrimaryText.AnsiFg}Edit name{Reset} · " +
              $"{palette.GetAccentFg().AnsiFg}[Esc]{Reset} {palette.PrimaryText.AnsiFg}Cancel{Reset}";
        WriteAt(4, row++, actions);
        return row;
    }

    /// <summary>
    /// Render the prerequisite-gate panel asking the user to set the service
    /// account first. workspace-ur5h: wraps to fit narrow terminals so the
    /// "we need the service account key" sentence doesn't run off the edge
    /// at width 80.
    /// </summary>
    public static int RenderPrerequisiteGate(ThemePalette palette, int startRow)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var row = startRow;
        var width = Math.Min(80, Math.Max(40, OverlayViewport.Width)) - 6;

        WriteAt(2, row++, $"{palette.PrimaryText.AnsiFg}Service account first.{Reset}");

        const string body = "Set the service account key before the bucket — we need it to verify (and, if asked, create) the bucket.";
        foreach (var line in WrapToWidth(body, width))
        {
            WriteAt(2, row++, $"{palette.SecondaryText.AnsiFg}{line}{Reset}");
        }

        row++;
        WriteAt(
            2,
            row++,
            $"{palette.GetAccentFg().AnsiFg}[A]{Reset} {palette.PrimaryText.AnsiFg}Set service account{Reset} · " +
            $"{palette.GetAccentFg().AnsiFg}[Esc]{Reset} {palette.PrimaryText.AnsiFg}Back{Reset}");
        return row;
    }

    /// <summary>
    /// Wait for one of a small set of single-character keys (case-insensitive)
    /// or Esc / GoBack / Quit. Returns the matched lowercase key, or
    /// <c>null</c> when the user backed out.
    /// </summary>
    public static async Task<char?> WaitForChoiceAsync(
        IInputHandler input,
        IReadOnlyCollection<char> validKeys,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(validKeys);

        while (!ct.IsCancellationRequested)
        {
            var command = await input.WaitForInputAsync(ct).ConfigureAwait(false);

            if (command.Type == CommandType.TerminalResized)
            {
                continue;
            }

            if (command.Type is CommandType.GoBack or CommandType.Quit)
            {
                return null;
            }

            if (command.Type == CommandType.ActivateLink)
            {
                // Enter; some panels treat Enter as the same as the only
                // affirmative key. Surface as a pseudo-character so callers
                // can branch.
                return '\n';
            }

            if (command.RawKeyChar is char raw)
            {
                var ch = char.ToLowerInvariant(raw);
                if (validKeys.Contains(ch))
                {
                    return ch;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Render the four-line live status panel for the credential-verify
    /// probe (workspace-cgnt). One line per step (Auth, Upload, Download,
    /// Delete) with a spinner / check / cross prefix and an inline timing
    /// suffix once the step completes. Subsequent calls overwrite in place
    /// so the same four rows update through the run.
    /// </summary>
    public static int RenderVerifyStatus(
        ThemePalette palette,
        int startRow,
        GcsVerifyStep currentStep,
        IReadOnlyList<(GcsVerifyStep Step, bool? Done, TimeSpan? Elapsed, string? Note)> rows)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(rows);

        var labels = new (GcsVerifyStep Step, string Label)[]
        {
            (GcsVerifyStep.Auth, "Authenticate"),
            (GcsVerifyStep.Upload, "Upload sentinel object"),
            (GcsVerifyStep.Download, "Download and compare"),
            (GcsVerifyStep.Delete, "Delete sentinel object"),
        };

        var row = startRow;
        for (var i = 0; i < labels.Length; i++)
        {
            var step = labels[i].Step;
            var label = labels[i].Label;
            var meta = FindRow(rows, step);
            ClearLine(row);
            string prefix;
            string textColor;
            if (meta is null)
            {
                if (step == currentStep)
                {
                    prefix = $"{palette.GetAccentFg().AnsiFg}…{Reset}";
                    textColor = palette.PrimaryText.AnsiFg;
                }
                else
                {
                    prefix = $"{palette.GetDimFg().AnsiFg}·{Reset}";
                    textColor = palette.GetDimFg().AnsiFg;
                }
            }
            else if (meta.Value.Done == true)
            {
                prefix = $"{palette.GetSuccessFg().AnsiFg}✓{Reset}";
                textColor = palette.PrimaryText.AnsiFg;
            }
            else if (meta.Value.Done == false)
            {
                prefix = $"{palette.ErrorFg.AnsiFg}✗{Reset}";
                textColor = palette.ErrorFg.AnsiFg;
            }
            else
            {
                prefix = $"{palette.GetAccentFg().AnsiFg}…{Reset}";
                textColor = palette.PrimaryText.AnsiFg;
            }

            var timing = meta?.Elapsed is TimeSpan ts ? $" {palette.GetDimFg().AnsiFg}({ts.TotalMilliseconds:0} ms){Reset}" : string.Empty;
            var note = !string.IsNullOrEmpty(meta?.Note) ? $" {palette.SecondaryText.AnsiFg}— {meta.Value.Note}{Reset}" : string.Empty;
            WriteAt(2, row, $"  {prefix} {textColor}{label}{Reset}{timing}{note}");
            row++;
        }

        return row;
    }

    private static (GcsVerifyStep Step, bool? Done, TimeSpan? Elapsed, string? Note)? FindRow(
        IReadOnlyList<(GcsVerifyStep Step, bool? Done, TimeSpan? Elapsed, string? Note)> rows,
        GcsVerifyStep step)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Step == step)
            {
                return rows[i];
            }
        }

        return null;
    }

    private static IEnumerable<string> WrapToWidth(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text) || maxLen <= 0)
        {
            yield return text;
            yield break;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            var prospective = current.Length == 0 ? word.Length : current.Length + 1 + word.Length;
            if (prospective > maxLen)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                if (word.Length > maxLen)
                {
                    var w = word;
                    while (w.Length > maxLen)
                    {
                        yield return w[..maxLen];
                        w = w[maxLen..];
                    }

                    if (w.Length > 0)
                    {
                        current.Append(w);
                    }
                }
                else
                {
                    current.Append(word);
                }
            }
            else
            {
                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(word);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static void DrawSpinner(ThemePalette palette, string label, int row, string frame)
    {
        ClearLine(row);
        WriteAt(2, row, $"{palette.GetAccentFg().AnsiFg}{frame}{Reset} {palette.PrimaryText.AnsiFg}{label}{Reset}");
    }

    private static void WriteAt(int col, int row, string text)
    {
        try
        {
            // workspace-s621: all panel drawing funnels through here — shift into
            // the dock-aware viewport so the panel is never under a docked browser.
            Console.SetCursorPosition(OverlayViewport.Left + Math.Max(0, col), Math.Max(0, row));
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        Console.Write(text);
    }

    private static void ClearLine(int row)
    {
        try
        {
            Console.SetCursorPosition(OverlayViewport.Left, Math.Max(0, row));
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        var width = Math.Max(20, OverlayViewport.Width - 1);
        Console.Write(new string(' ', width));
        try
        {
            Console.SetCursorPosition(OverlayViewport.Left, Math.Max(0, row));
        }
        catch (ArgumentOutOfRangeException)
        {
            // ignore
        }
    }
}
