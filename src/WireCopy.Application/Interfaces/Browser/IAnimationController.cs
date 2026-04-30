// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// Controls animation state for the input handler's timer-tick mechanism.
/// When an animation is active, the input handler races keyboard input against
/// a periodic timer, returning AnimationTick commands between keypresses.
/// </summary>
public interface IAnimationController
{
    /// <summary>
    /// Gets the current animation state.
    /// </summary>
    AnimationState AnimationState { get; }

    /// <summary>
    /// Gets or sets the timer interval in milliseconds for animation ticks.
    /// Default is 60ms (~16fps). Only used when HasActiveAnimation is true.
    /// </summary>
    int AnimationIntervalMs { get; set; }

    /// <summary>
    /// Starts an animation, enabling the timer-tick mechanism.
    /// </summary>
    void StartAnimation();

    /// <summary>
    /// Stops the current animation, disabling the timer-tick mechanism.
    /// The input handler returns to pure keyboard-blocking behavior.
    /// </summary>
    void StopAnimation();
}
