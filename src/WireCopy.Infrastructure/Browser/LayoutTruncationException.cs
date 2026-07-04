// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// workspace-i1lv: raised when an OpenAI chat completion stops because it hit
/// the output-token cap (<c>finish_reason=length</c>) or comes back empty. On a
/// reasoning model (gpt-5-mini) <c>max_completion_tokens</c> is shared by
/// reasoning AND visible tokens, so a too-small cap at a higher reasoning-effort
/// tier can burn the whole budget on reasoning and return NO parseable JSON. The
/// old code surfaced that as a bare <see cref="System.Text.Json.JsonException"/>
/// that the setup wizard mislabelled "check your connection". This typed signal
/// lets the analyzer RETRY with a larger cap (not more prompt text) and the UI
/// report the real cause.
/// </summary>
public sealed class LayoutTruncationException : Exception
{
    public LayoutTruncationException(int? outputTokenCap, int? reasoningTokenCount)
        : base(BuildMessage(outputTokenCap, reasoningTokenCount))
    {
        OutputTokenCap = outputTokenCap;
        ReasoningTokenCount = reasoningTokenCount;
    }

    /// <summary>The <c>max_completion_tokens</c> cap the truncated call ran under.</summary>
    public int? OutputTokenCap { get; }

    /// <summary>Reasoning tokens the model spent before running out of room, when the SDK reports it.</summary>
    public int? ReasoningTokenCount { get; }

    private static string BuildMessage(int? cap, int? reasoning)
    {
        var reasoningPart = reasoning is { } r ? $", {r} of them on reasoning" : string.Empty;
        return $"OpenAI completion truncated at the {cap?.ToString() ?? "default"} output-token cap{reasoningPart} "
             + "— no parseable content returned. Retry with a larger cap or a lower reasoning effort.";
    }
}
