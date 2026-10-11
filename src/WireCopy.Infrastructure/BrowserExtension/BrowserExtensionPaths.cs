// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>
/// The shared contract between the browser extension's native-messaging host
/// (WireCopy.API in host mode) and the app's install flow (workspace-m7aml):
/// the host name the extension calls, the extension ID the browser lets through,
/// and where each piece lives under <c>{LocalAppData}/WireCopy/browser-extension/</c>.
/// </summary>
public static class BrowserExtensionPaths
{
    /// <summary>Native-messaging host name; must match <c>HOST</c> in extension/background.js.</summary>
    public const string HostName = "com.wirecopy.reader";

    /// <summary>
    /// The extension's ID, pinned by the <c>key</c> field in extension/manifest.json so an
    /// unpacked install gets the same ID on every machine.
    /// </summary>
    public const string ExtensionId = "cmidfakmpcnmaohapmcoigenlcjlahim";

    /// <summary>The only origin the browser lets launch the host (manifest <c>allowed_origins</c>).</summary>
    public const string AllowedOrigin = "chrome-extension://" + ExtensionId + "/";

    /// <summary>Name of the exported unpacked-extension folder the user loads into the browser.</summary>
    public const string ExtensionFolderName = "WireCopy-Extension";

    /// <summary>
    /// Root folder for everything the extension link needs:
    /// <c>{LocalAppData}/WireCopy/browser-extension</c>.
    /// </summary>
    public static string Root(string? localAppData = null) =>
        Path.Combine(
            localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WireCopy",
            "browser-extension");

    /// <summary>The exported unpacked extension the user drags onto the browser's extensions page.</summary>
    public static string ExtensionDir(string? localAppData = null) =>
        Path.Combine(Root(localAppData), ExtensionFolderName);

    /// <summary>The script the host manifest points at; it starts the app in host mode.</summary>
    public static string LauncherPath(string? localAppData = null) =>
        Path.Combine(
            Root(localAppData),
            OperatingSystem.IsWindows() ? "wirecopy-native-host.cmd" : "wirecopy-native-host");

    /// <summary>The host manifest; copied into each browser's NativeMessagingHosts folder.</summary>
    public static string ManifestPath(string? localAppData = null) =>
        Path.Combine(Root(localAppData), HostName + ".json");

    /// <summary>Per-browser record of the last time the extension reached the host.</summary>
    public static string ConnectionsPath(string? localAppData = null) =>
        Path.Combine(Root(localAppData), "connections.json");
}
