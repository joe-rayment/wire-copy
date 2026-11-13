// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-6ifpp.3 — a plain pager over one routine run's own log lines. Every line is wrapped
/// to the terminal width (never cut), scrolling is by wrapped row, and it opens at the top.
/// Paints like <see cref="LogViewerCommandHandler"/>: cursor home, per-line clear, raw console.
/// </summary>
internal static class RoutineRunLogViewer
{
    private const string Reset = "\x1b[0m";

    public static async Task ShowAsync(
        CommandContext ctx, RenderOptions options, string title, IReadOnlyList<string> lines, CancellationToken ct)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var scroll = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                options = ctx.GetCurrentRenderOptions();
                var width = Math.Max(20, options.TerminalWidth);
                var body = Math.Max(1, options.TerminalHeight - 2);
                var rows = Wrap(lines, width);
                var maxScroll = Math.Max(0, rows.Count - body);
                scroll = Math.Clamp(scroll, 0, maxScroll);
                Render(rows, scroll, body, width, title, lines.Count, palette);

                var cmd = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
                var page = Math.Max(1, body - 1);
                switch (cmd.Type)
                {
                    case CommandType.GoBack or CommandType.Quit:
                        return;
                    case CommandType.MoveDown:
                        scroll++;
                        break;
                    case CommandType.MoveUp:
                        scroll--;
                        break;
                    case CommandType.PageDown:
                        scroll += page;
                        break;
                    case CommandType.PageUp:
                        scroll -= page;
                        break;
                    case CommandType.GoToTop:
                        scroll = 0;
                        break;
                    case CommandType.GoToBottom:
                        scroll = maxScroll;
                        break;
                    default:
                        if (char.ToLowerInvariant(cmd.RawKeyChar ?? '\0') == 'q')
                        {
                            return;
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

    private static List<string> Wrap(IReadOnlyList<string> lines, int width)
    {
        var rows = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Replace("\t", "    ", StringComparison.Ordinal);
            if (line.Length <= width)
            {
                rows.Add(line);
                continue;
            }

            rows.Add(line[..width]);
            var rest = line[width..];
            var chunk = Math.Max(1, width - 2);
            for (var i = 0; i < rest.Length; i += chunk)
            {
                rows.Add("  " + rest.Substring(i, Math.Min(chunk, rest.Length - i)));
            }
        }

        return rows;
    }

    private static void Render(List<string> rows, int scroll, int body, int width, string title, int lineCount, ThemePalette palette)
    {
        var sb = new StringBuilder();
        sb.Append("\x1b[H");
        var header = $" Run log · {title} · {lineCount} lines";
        sb.Append(palette.GetAccentFg().AnsiFg).Append("\x1b[1m").Append(Clip(header, width)).Append(Reset).Append("\x1b[K\n");
        for (var i = 0; i < body; i++)
        {
            var idx = scroll + i;
            if (idx < rows.Count)
            {
                sb.Append(rows[idx]);
            }

            sb.Append("\x1b[K\n");
        }

        const string Hints = " ↑/↓ scroll · PgUp/PgDn page · g/G top/bottom · Esc back";
        sb.Append(palette.GetDimFg().AnsiFg).Append(Clip(Hints, width)).Append(Reset).Append("\x1b[K");
        Console.Out.Write(sb.ToString());
        Console.Out.Flush();
    }

    private static string Clip(string s, int width) => s.Length > width ? s[..Math.Max(0, width - 1)] + "…" : s;
}
