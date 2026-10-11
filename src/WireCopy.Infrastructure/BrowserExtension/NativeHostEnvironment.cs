// Licensed under the MIT License. See LICENSE in the repository root.

using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>The operating systems the native-messaging host registers itself on.</summary>
public enum NativePlatform
{
    /// <summary>Linux (and other Unix-likes that follow the XDG layout).</summary>
    Linux,

    /// <summary>macOS.</summary>
    MacOS,

    /// <summary>Windows (browsers find the host through a registry key).</summary>
    Windows,
}

/// <summary>
/// Everything <see cref="NativeHostRegistrar"/> needs to know about the machine, as plain
/// values, so tests can aim it at a temp folder instead of the real home directory.
/// </summary>
/// <param name="Platform">Which OS layout to register for.</param>
/// <param name="Home">The user's home directory.</param>
/// <param name="ConfigRoot">
/// Where browsers keep their profile folders: <c>$XDG_CONFIG_HOME</c> (else <c>~/.config</c>) on Linux,
/// <c>~/Library/Application Support</c> on macOS, <c>%LOCALAPPDATA%</c> on Windows.
/// </param>
/// <param name="LocalAppData">The base passed to <see cref="BrowserExtensionPaths"/>.</param>
/// <param name="PathDirectories">The directories on <c>PATH</c>, searched for browser executables.</param>
/// <param name="FileExists">Whether a file exists; injectable so tests do not see real browsers.</param>
/// <param name="RegistryWrite">
/// Windows only: sets the default value of a <c>HKCU</c> subkey (subkey path, manifest path); null elsewhere.
/// </param>
public sealed record NativeHostEnvironment(
    NativePlatform Platform,
    string Home,
    string ConfigRoot,
    string LocalAppData,
    IReadOnlyList<string> PathDirectories,
    Func<string, bool> FileExists,
    Action<string, string>? RegistryWrite = null)
{
    /// <summary>Builds the environment of the running process (the real machine).</summary>
    public static NativeHostEnvironment FromCurrentProcess()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (OperatingSystem.IsWindows())
        {
            return new NativeHostEnvironment(
                NativePlatform.Windows, home, localAppData, localAppData, pathDirs, File.Exists, WriteRegistryDefault);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new NativeHostEnvironment(
                NativePlatform.MacOS,
                home,
                Path.Combine(home, "Library", "Application Support"),
                localAppData,
                pathDirs,
                File.Exists);
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configRoot = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".config") : xdg;
        return new NativeHostEnvironment(NativePlatform.Linux, home, configRoot, localAppData, pathDirs, File.Exists);
    }

    private static void WriteRegistryDefault(string subKey, string manifestPath)
    {
        if (OperatingSystem.IsWindows())
        {
            SetDefaultValue(subKey, manifestPath);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetDefaultValue(string subKey, string manifestPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        if (!string.Equals(key.GetValue(null) as string, manifestPath, StringComparison.Ordinal))
        {
            key.SetValue(null, manifestPath);
        }
    }
}
