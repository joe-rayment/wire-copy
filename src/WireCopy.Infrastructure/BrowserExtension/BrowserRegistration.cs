// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>What <see cref="NativeHostRegistrar"/> found and did for one browser.</summary>
/// <param name="Id">Browser id: chrome, chromium, vivaldi, brave, edge, or arc.</param>
/// <param name="DisplayName">Human name, e.g. "Google Chrome".</param>
/// <param name="Detected">True when the browser's profile folder or executable exists on this machine.</param>
/// <param name="ManifestPaths">
/// Where the host manifest was placed for this browser (the registry-referenced manifest on Windows);
/// empty when the browser was not detected.
/// </param>
/// <param name="LaunchExecutable">The browser executable found, used to open its extensions page; null if none.</param>
public sealed record BrowserRegistration(
    string Id,
    string DisplayName,
    bool Detected,
    IReadOnlyList<string> ManifestPaths,
    string? LaunchExecutable);
