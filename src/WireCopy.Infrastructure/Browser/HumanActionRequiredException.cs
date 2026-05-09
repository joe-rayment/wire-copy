// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Thrown by the page-load pipeline when a human-in-the-loop action (CAPTCHA,
/// login, cookie consent, 2FA, paywall, region block) is the reason the load
/// could not complete. Carries the typed <see cref="HumanActionRequired"/>
/// signal so consumers (orchestrator → renderer) can produce variant-aware
/// copy instead of falling back to "Something went wrong".
///
/// <para>
/// MVP scope (workspace-0b9s): we keep the throw-based propagation rather
/// than refactoring <see cref="PageLoadPipeline"/> to a result-returning shape.
/// A follow-up task converts this to a non-throwing pipeline.
/// </para>
/// </summary>
public sealed class HumanActionRequiredException : InvalidOperationException
{
    public HumanActionRequiredException(HumanActionRequired requiredAction, string? message = null)
        : base(message ?? $"Human action required: {requiredAction.Variant} on {requiredAction.Domain}")
    {
        RequiredAction = requiredAction;
    }

    public HumanActionRequired RequiredAction { get; }
}
