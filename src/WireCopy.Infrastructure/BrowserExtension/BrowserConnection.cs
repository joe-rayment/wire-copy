// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>One browser's last contact with the native-messaging host.</summary>
/// <param name="LastSeenUtc">When the extension in that browser last reached the host.</param>
/// <param name="ExtensionVersion">The extension's manifest version, when it sent one.</param>
public sealed record BrowserConnection(DateTime LastSeenUtc, string? ExtensionVersion);
