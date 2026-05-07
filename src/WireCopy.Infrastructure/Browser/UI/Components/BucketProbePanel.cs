// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
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
    /// account first.
    /// </summary>
    public static int RenderPrerequisiteGate(ThemePalette palette, int startRow)
    {
        var row = startRow;
        WriteAt(2, row++, $"{palette.PrimaryText.AnsiFg}Service account first.{Reset}");
        WriteAt(2, row++, $"{palette.SecondaryText.AnsiFg}We need the service account key to verify your bucket and (if needed) create it.{Reset}");
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

    private static void DrawSpinner(ThemePalette palette, string label, int row, string frame)
    {
        ClearLine(row);
        WriteAt(2, row, $"{palette.GetAccentFg().AnsiFg}{frame}{Reset} {palette.PrimaryText.AnsiFg}{label}{Reset}");
    }

    private static void WriteAt(int col, int row, string text)
    {
        try
        {
            Console.SetCursorPosition(Math.Max(0, col), Math.Max(0, row));
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
            Console.SetCursorPosition(0, Math.Max(0, row));
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        var width = Math.Max(20, Console.WindowWidth - 1);
        Console.Write(new string(' ', width));
        try
        {
            Console.SetCursorPosition(0, Math.Max(0, row));
        }
        catch (ArgumentOutOfRangeException)
        {
            // ignore
        }
    }
}
