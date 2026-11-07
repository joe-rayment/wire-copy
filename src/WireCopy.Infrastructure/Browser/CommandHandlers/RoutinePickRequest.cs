// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>workspace-wyvga.9: what the pick mode is asked to open.</summary>
public sealed record RoutinePickRequest(Guid RoutineId, string SiteUrl, string? PreIncludeSection);
