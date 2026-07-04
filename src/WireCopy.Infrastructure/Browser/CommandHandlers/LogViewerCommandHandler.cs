// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-v3pz: the in-app log viewer (<c>:logs</c>). Shows the recent entries
/// from <see cref="ILogBuffer"/> newest-at-bottom, scrollable and level-filterable,
/// so the user can pull up a problem's details without leaving the app — and get
/// them OUT via <c>c</c> (OSC 52 clipboard) or <c>e</c> (export to a file). A
/// self-contained interactive handler in the Settings/Schedule mould: it draws
/// over the frame and restores the previous screen on exit.
/// </summary>
internal static class LogViewerCommandHandler
{
    private const string Reset = "\x1b[0m";

    public static async Task HandleAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var buffer = scope.ServiceProvider.GetService<ILogBuffer>();
        if (buffer is null)
        {
            ctx.NavigationService.SetStatusMessage("Log viewer unavailable (no buffer)", StatusSeverity.Error);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var minLevel = LogSeverity.Trace;
        var search = string.Empty;
        var scroll = 0;
        var followTail = true;
        string? flash = "j/k scroll · Ctrl+D/U page · g/G ends · f level · / search · c copy · e export · Esc back";

        List<LogRecord> Filtered() => buffer.Snapshot()
            .Where(r => r.Level >= minLevel)
            .Where(r => search.Length == 0
                || r.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (r.SourceContext?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                options = ctx.GetCurrentRenderOptions();
                var body = Math.Max(1, options.TerminalHeight - 2); // header + status
                var rows = Filtered();
                var maxScroll = Math.Max(0, rows.Count - body);
                if (followTail)
                {
                    scroll = maxScroll;
                }

                scroll = Math.Clamp(scroll, 0, maxScroll);
                Render(rows, scroll, body, options, palette, minLevel, search, buffer.Capacity, followTail, flash);
                flash = null;

                var cmd = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
                var half = Math.Max(1, body / 2);
                switch (cmd.Type)
                {
                    case CommandType.GoBack or CommandType.Quit:
                        return;
                    case CommandType.MoveDown:
                        scroll++;
                        followTail = scroll >= maxScroll;
                        break;
                    case CommandType.MoveUp:
                        scroll--;
                        followTail = false;
                        break;
                    case CommandType.PageDown:
                        scroll += half;
                        followTail = scroll >= maxScroll;
                        break;
                    case CommandType.PageUp:
                        scroll -= half;
                        followTail = false;
                        break;
                    case CommandType.GoToTop:
                        scroll = 0;
                        followTail = false;
                        break;
                    case CommandType.GoToBottom:
                        followTail = true;
                        break;
                    default:
                        switch (char.ToLowerInvariant(cmd.RawKeyChar ?? '\0'))
                        {
                            case 'q':
                                return;
                            case 'f':
                                minLevel = NextLevel(minLevel);
                                followTail = true;
                                break;
                            case '/':
                                search = (await PromptAsync(ctx, options, "Search logs", search, ct).ConfigureAwait(false) ?? search).Trim();
                                followTail = true;
                                break;
                            case 'c':
                                flash = CopyToClipboard(rows);
                                break;
                            case 'e':
                                flash = ExportToFile(rows, minLevel, search, ctx);
                                break;
                        }

                        break;
                }
            }
        }
        finally
        {
            Console.Write("\x1b[2J\x1b[H");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
    }

    private static string CopyToClipboard(List<LogRecord> rows)
    {
        if (rows.Count == 0)
        {
            return "Nothing to copy";
        }

        var text = string.Join("\n", rows.Select(PlainLine));
        return Osc52Clipboard.Copy(text)
            ? $"Copied {rows.Count} line(s) to the clipboard"
            : $"Too many lines for the clipboard — press e to export the {rows.Count} instead";
    }

    private static string ExportToFile(List<LogRecord> rows, LogSeverity level, string search, CommandContext ctx)
    {
        try
        {
            Directory.CreateDirectory("logs");
            var name = $"logs/wirecopy-export-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            var sb = new StringBuilder();
            sb.AppendLine($"# WireCopy log export — {rows.Count} line(s), level>={level}" +
                (search.Length > 0 ? $", search=\"{search}\"" : string.Empty));
            foreach (var r in rows)
            {
                sb.AppendLine(PlainLine(r));
            }

            File.WriteAllText(name, sb.ToString());
            return $"Exported {rows.Count} line(s) → {Path.GetFullPath(name)}";
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Log export failed");
            return "Export failed — see the log file";
        }
    }

    private static string PlainLine(LogRecord r)
    {
        var line = $"{r.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss} {Abbrev(r.Level)} {r.Message}";
        return r.Exception is { Length: > 0 } ? line + "\n    " + r.Exception.Replace("\n", "\n    ", StringComparison.Ordinal) : line;
    }

    private static void Render(
        List<LogRecord> rows,
        int scroll,
        int body,
        RenderOptions options,
        ThemePalette palette,
        LogSeverity minLevel,
        string search,
        int capacity,
        bool followTail,
        string? flash)
    {
        var width = Math.Max(20, options.TerminalWidth);
        var sb = new StringBuilder();
        sb.Append("\x1b[H"); // cursor home (no full clear → no flicker; each line clears itself)

        var filterNote = minLevel == LogSeverity.Trace ? "all" : $"≥{minLevel}";
        var searchNote = search.Length > 0 ? $" · /{search}" : string.Empty;
        var header = $" Logs · {rows.Count}/{capacity} · {filterNote}{searchNote}{(followTail ? " · tail" : string.Empty)}";
        sb.Append(palette.GetAccentFg().AnsiFg).Append("\x1b[1m").Append(Fit(header, width)).Append(Reset).Append("\x1b[K\n");

        for (var i = 0; i < body; i++)
        {
            var idx = scroll + i;
            if (idx < rows.Count)
            {
                var r = rows[idx];
                sb.Append(ColorFor(r.Level, palette)).Append(Fit(LineFor(r), width)).Append(Reset);
            }

            sb.Append("\x1b[K\n");
        }

        var status = flash ?? " j/k scroll · f level · / search · c copy · e export · Esc back";
        sb.Append(palette.GetDimFg().AnsiFg).Append(Fit(status, width)).Append(Reset).Append("\x1b[K");
        Console.Out.Write(sb.ToString());
        Console.Out.Flush();
    }

    private static string LineFor(LogRecord r)
    {
        var ctx = string.IsNullOrEmpty(r.SourceContext) ? string.Empty : $"[{Short(r.SourceContext!)}] ";
        var msg = r.Message + (r.Exception is { Length: > 0 } ? " ⟪+exception⟫" : string.Empty);
        return $"{r.Timestamp.ToLocalTime():HH:mm:ss} {Abbrev(r.Level)} {ctx}{msg}";
    }

    private static string Short(string sourceContext)
    {
        var dot = sourceContext.LastIndexOf('.');
        return dot >= 0 && dot < sourceContext.Length - 1 ? sourceContext[(dot + 1)..] : sourceContext;
    }

    private static string Abbrev(LogSeverity level) => level switch
    {
        LogSeverity.Trace => "TRC",
        LogSeverity.Debug => "DBG",
        LogSeverity.Information => "INF",
        LogSeverity.Warning => "WRN",
        LogSeverity.Error => "ERR",
        LogSeverity.Critical => "CRT",
        _ => "INF",
    };

    private static string ColorFor(LogSeverity level, ThemePalette palette) => level switch
    {
        LogSeverity.Error or LogSeverity.Critical => palette.ErrorFg.AnsiFg,
        LogSeverity.Warning => palette.GetWarningFg().AnsiFg,
        LogSeverity.Information => palette.PrimaryText.AnsiFg,
        _ => palette.GetDimFg().AnsiFg,
    };

    private static LogSeverity NextLevel(LogSeverity level) => level switch
    {
        LogSeverity.Trace => LogSeverity.Debug,
        LogSeverity.Debug => LogSeverity.Information,
        LogSeverity.Information => LogSeverity.Warning,
        LogSeverity.Warning => LogSeverity.Error,
        LogSeverity.Error => LogSeverity.Critical,
        _ => LogSeverity.Trace,
    };

    private static string Fit(string s, int width)
    {
        if (s.Length == width)
        {
            return s;
        }

        return s.Length > width ? s[..Math.Max(0, width - 1)] + "…" : s.PadRight(width);
    }

    private static async Task<string?> PromptAsync(CommandContext ctx, RenderOptions options, string label, string initial, CancellationToken ct)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var field = new UI.Components.FormFieldConfig
        {
            Label = label,
            Subtitle = "Filter to matching lines · empty clears · Esc keeps current",
            Placeholder = initial.Length > 0 ? initial : "type to filter…",
        };
        var fieldWidth = Math.Min(80, Math.Max(30, options.TerminalWidth - 6));
        var startRow = Math.Max(0, options.TerminalHeight - UI.Components.FormField.HeightFor(field) - 1);
        return await UI.Components.FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct).ConfigureAwait(false);
    }
}
