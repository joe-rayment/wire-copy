// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;

namespace TermReader.Infrastructure.Browser.UI;

/// <summary>
/// Controls animation state for the terminal input handler's timer-tick mechanism.
/// Thread-safe: animation state can be started/stopped from the main loop
/// while the input handler checks it from its WaitForInputCoreAsync method.
/// </summary>
public class TerminalAnimationController : IAnimationController
{
    private const int DefaultAnimationIntervalMs = 60;
    private readonly AnimationState _animationState = new();
    private volatile int _animationIntervalMs = DefaultAnimationIntervalMs;

    /// <inheritdoc />
    public AnimationState AnimationState => _animationState;

    /// <inheritdoc />
    public int AnimationIntervalMs
    {
        get => _animationIntervalMs;
        set => _animationIntervalMs = Math.Max(16, value); // Floor at ~60fps
    }

    /// <inheritdoc />
    public void StartAnimation()
    {
        _animationState.Start();
    }

    /// <inheritdoc />
    public void StopAnimation()
    {
        _animationState.Stop();
    }
}
