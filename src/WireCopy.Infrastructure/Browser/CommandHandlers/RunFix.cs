// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

internal sealed record RunFix(RunFixKind Kind, string Label, SettingsCommandHandler.SetupRow? Row = null, string? SiteUrl = null);
