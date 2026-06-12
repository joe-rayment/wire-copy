// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Browser.UI.Renderers;

namespace WireCopy.Tests;

/// <summary>
/// Shared console-output capture for renderer tests. Callers must be in
/// <see cref="ConsoleSerialCollection"/> — Console.SetOut is process-global.
/// </summary>
internal static class ConsoleCapture
{
    /// <summary>Captures everything <paramref name="action"/> writes to Console.Out.</summary>
    public static string Render(Action action)
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);
            action();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    /// <summary>
    /// Captures a render that goes through a fresh <see cref="RenderHelpers"/>
    /// (cleared first), the common shape for full-screen renderers.
    /// </summary>
    public static string Render(Action<RenderHelpers> action, int terminalHeight = 30)
    {
        return Render(() =>
        {
            var helpers = new RenderHelpers { TerminalHeight = terminalHeight };
            helpers.Clear();
            action(helpers);
        });
    }
}
