// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Wraps a chat completion's text payload alongside the total token count
/// reported by the provider, so callers can apply per-month budget caps.
/// </summary>
internal readonly record struct ChatCompletionResult(string Text, int TotalTokens);
