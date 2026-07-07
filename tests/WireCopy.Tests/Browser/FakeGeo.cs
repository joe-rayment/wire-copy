// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Browser;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Fake geometry controller. Returns a queue of display reads (to simulate a phantom-then-
/// real transition), enforces a Chromium-style minimum width on every SetWindow, and records
/// the final applied rect.
/// </summary>
internal sealed class FakeGeo : IDockWindowGeometry
{
    private readonly Queue<SidecarGeometry.DisplayInfo?> _displays;
    private readonly int _minWidthClamp;
    private readonly Action<int, int>? _onMove;
    private readonly Func<TerminalTiling.WindowRect, TerminalTiling.WindowRect>? _setTransform;
    private SidecarGeometry.DisplayInfo? _lastDisplay;

    public FakeGeo(
        IEnumerable<SidecarGeometry.DisplayInfo?> displays,
        int minWidthClamp,
        Action<int, int>? onMove = null,
        Func<TerminalTiling.WindowRect, TerminalTiling.WindowRect>? setTransform = null)
    {
        _displays = new Queue<SidecarGeometry.DisplayInfo?>(displays);
        _minWidthClamp = minWidthClamp;
        _onMove = onMove;
        _setTransform = setTransform;
    }

    public TerminalTiling.WindowRect Current { get; set; } = new(-32000, -32000, 800, 600);

    /// <summary>CDP windowState the fake reports (workspace-v7g7 park transitions).</summary>
    public string? WindowState { get; set; } = "normal";

    public int SetCount { get; private set; }

    /// <summary>True while the window is in the truly-hidden state (workspace-ynn9).</summary>
    public bool Hidden { get; private set; } = true;

    /// <summary>Ordered log of geometry calls, so tests can assert un-hide-before-place.</summary>
    public List<string> Calls { get; } = [];

    public Task NormalizeAsync()
    {
        Calls.Add("Normalize");
        Hidden = false;
        WindowState = "normal";
        return Task.CompletedTask;
    }

    public Task IconifyAsync()
    {
        Calls.Add("Iconify");
        Hidden = true;
        WindowState = "minimized";
        return Task.CompletedTask;
    }

    public Task<string?> ReadWindowStateAsync() => Task.FromResult(WindowState);

    public Task HideAsync()
    {
        Calls.Add("Hide");
        Hidden = true;
        // Model the real hide: the window leaves the visible work area (off-screen move) and,
        // on non-macOS, is also iconified — either way it is no longer on-screen.
        Current = Current with { X = -32000, Y = -32000 };
        WindowState = "minimized";
        return Task.CompletedTask;
    }

    public Task MoveAsync(int left, int top)
    {
        Calls.Add("Move");
        _onMove?.Invoke(left, top);
        Current = Current with { X = left, Y = top };
        return Task.CompletedTask;
    }

    public Task SettleAsync() => Task.CompletedTask;

    public Task<SidecarGeometry.DisplayInfo?> ReadDisplayAsync()
    {
        // Once the queue is drained, keep returning the last value (steady state).
        if (_displays.Count > 0)
        {
            _lastDisplay = _displays.Dequeue();
        }

        return Task.FromResult(_lastDisplay);
    }

    public Task<TerminalTiling.WindowRect?> ReadWindowAsync() =>
        Task.FromResult<TerminalTiling.WindowRect?>(Current);

    public Task SetWindowAsync(TerminalTiling.WindowRect rect)
    {
        SetCount++;
        Calls.Add("Set");

        // Chromium refuses to make the window narrower than its platform minimum.
        var clampedWidth = System.Math.Max(rect.Width, _minWidthClamp);
        var applied = rect with { Width = clampedWidth };

        // Optional OS-behavior hook (workspace-v7g7): model a WM/OS that clamps or offsets
        // the applied position (macOS pulling a window inside the visible frame).
        if (_setTransform != null)
        {
            applied = _setTransform(applied);
        }

        Current = applied;
        return Task.CompletedTask;
    }
}
