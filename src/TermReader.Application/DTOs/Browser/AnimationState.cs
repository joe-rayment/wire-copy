// Educational and personal use only.

namespace TermReader.Application.DTOs.Browser;

/// <summary>
/// Tracks animation playback state for the timer-tick mechanism.
/// When an animation is active, WaitForInputAsync races keyboard input
/// against a periodic timer to allow frame updates between keypresses.
/// </summary>
public class AnimationState
{
    /// <summary>
    /// Whether an animation is currently playing.
    /// When true, WaitForInputAsync uses Task.WhenAny to race input against a timer tick.
    /// When false, input handling is purely blocking (zero behavior change).
    /// </summary>
    public bool HasActiveAnimation { get; set; }

    /// <summary>
    /// When the current animation began (UTC).
    /// </summary>
    public DateTime AnimationStarted { get; set; }

    /// <summary>
    /// Current frame index within the active animation.
    /// Reset to 0 when a new animation starts.
    /// </summary>
    public int AnimationFrameIndex { get; set; }

    /// <summary>
    /// Starts a new animation, resetting frame state.
    /// </summary>
    public void Start()
    {
        HasActiveAnimation = true;
        AnimationStarted = DateTime.UtcNow;
        AnimationFrameIndex = 0;
    }

    /// <summary>
    /// Stops the current animation and resets state.
    /// </summary>
    public void Stop()
    {
        HasActiveAnimation = false;
        AnimationFrameIndex = 0;
    }

    /// <summary>
    /// Advances to the next frame. Returns the new frame index.
    /// </summary>
    public int AdvanceFrame()
    {
        return ++AnimationFrameIndex;
    }
}
