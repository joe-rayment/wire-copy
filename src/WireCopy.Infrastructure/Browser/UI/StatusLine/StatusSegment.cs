// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI.StatusLine;

/// <summary>One run of styled text inside a status item variant.</summary>
internal readonly record struct StatusSegment(string Text, StatusStyle Style);
