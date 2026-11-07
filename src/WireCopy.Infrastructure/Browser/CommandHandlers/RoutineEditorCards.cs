// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Small full-screen choice cards for the routine editor (workspace-wyvga.5): a single-choice
/// list and a multi-select day list. Painted directly like the editor itself; Esc returns null.
/// </summary>
internal static class RoutineEditorCards
{
    private const string Reset = "\u001b[0m";

    public static async Task<int?> PickAsync(
        CommandContext ctx, RenderOptions options, ThemePalette palette, string title, string hint, IReadOnlyList<string> labels, CancellationToken ct)
    {
        var cursor = 0;
        while (!ct.IsCancellationRequested)
        {
            Draw(options, palette, title, hint, labels, cursor);
            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            switch (command.Type)
            {
                case CommandType.GoBack or CommandType.Quit:
                    return null;
                case CommandType.MoveDown or CommandType.ExpandNode:
                    cursor = (cursor + 1) % labels.Count;
                    break;
                case CommandType.MoveUp or CommandType.CollapseNode:
                    cursor = (cursor - 1 + labels.Count) % labels.Count;
                    break;
                case CommandType.TerminalResized:
                    options = ctx.GetCurrentRenderOptions();
                    break;
                case CommandType.ActivateLink:
                    return cursor;
            }
        }

        return null;
    }

    public static async Task<List<DayOfWeek>?> PickDaysAsync(
        CommandContext ctx, RenderOptions options, ThemePalette palette, IReadOnlySet<DayOfWeek>? initial, CancellationToken ct)
    {
        var all = Enum.GetValues<DayOfWeek>();
        var selected = new HashSet<DayOfWeek>(initial ?? new HashSet<DayOfWeek>());
        var cursor = 0;
        while (!ct.IsCancellationRequested)
        {
            var labels = all.Select(d => $"[{(selected.Contains(d) ? "x" : " ")}] {d}").ToList();
            labels.Add("✓ Done");
            Draw(options, palette, "Pick days", "↑/↓ move · space toggle · Enter on Done · Esc back", labels, cursor);
            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            switch (command.Type)
            {
                case CommandType.GoBack or CommandType.Quit:
                    return null;
                case CommandType.MoveDown or CommandType.ExpandNode:
                    cursor = (cursor + 1) % labels.Count;
                    break;
                case CommandType.MoveUp or CommandType.CollapseNode:
                    cursor = (cursor - 1 + labels.Count) % labels.Count;
                    break;
                case CommandType.TerminalResized:
                    options = ctx.GetCurrentRenderOptions();
                    break;
                case CommandType.ActivateLink:
                    if (cursor == all.Length)
                    {
                        return selected.ToList();
                    }

                    Toggle(selected, all[cursor]);
                    break;
            }

            if (command.RawKeyChar == ' ' && cursor < all.Length)
            {
                Toggle(selected, all[cursor]);
            }
        }

        return null;
    }

    private static void Toggle(HashSet<DayOfWeek> set, DayOfWeek day)
    {
        if (!set.Add(day))
        {
            set.Remove(day);
        }
    }

    private static void Draw(RenderOptions options, ThemePalette palette, string title, string hint, IReadOnlyList<string> labels, int cursor)
    {
        var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
        var width = Math.Max(20, options.TerminalWidth - 6);
        helpers.Clear();
        helpers.WriteLine();
        helpers.WriteLine($"  {palette.GetRoutineAccentFg().AnsiFg}{RenderHelpers.TruncateText(title, width)}{Reset}");
        helpers.WriteLine();
        var window = Math.Max(1, options.TerminalHeight - 8);
        var first = Math.Clamp(cursor - window + 1, 0, Math.Max(0, labels.Count - window));
        for (var i = first; i < Math.Min(labels.Count, first + window); i++)
        {
            var text = RenderHelpers.TruncateText(labels[i], width - 2).PadRight(Math.Max(4, width - 2));
            helpers.WriteLine(i == cursor
                ? $"  {palette.GetMutedFg().AnsiFg}▌{Reset} {palette.SelectedItemBg.AnsiBg}{palette.SelectedItemFg.AnsiFg}{text}{Reset}"
                : $"    {palette.PrimaryText.AnsiFg}{text.TrimEnd()}{Reset}");
        }

        helpers.WriteLine();
        helpers.WriteLine($"  {palette.GetDimFg().AnsiFg}{RenderHelpers.TruncateText(hint, width)}{Reset}");
        helpers.ClearRemainingLines();
    }
}
