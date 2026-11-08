// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>An editor row being edited in place (workspace-0mo7l.1). Draw fills <see cref="ScreenRow"/>/<see cref="InputCol"/> for the caller's text prompt.</summary>
internal sealed class RoutineEditView
{
    public RoutineEditView(RoutineRowKind kind, string prefix, string hint)
    {
        Kind = kind;
        Prefix = prefix;
        Hint = hint;
    }

    public RoutineRowKind Kind { get; }

    public string Prefix { get; }

    public string Hint { get; }

    public string? Error { get; set; }

    public int ScreenRow { get; set; }

    public int InputCol { get; set; }
}
