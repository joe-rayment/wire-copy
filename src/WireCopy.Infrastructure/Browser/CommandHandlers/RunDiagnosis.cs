// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

internal sealed record RunDiagnosis(string Headline, string Short, string What, string? Step, string? HowToFix, RunFix Fix, bool NeedsAttention);
