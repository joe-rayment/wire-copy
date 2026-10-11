// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>
/// Makes the browser extension's native-messaging host discoverable (workspace-m7aml): mirrors the
/// shipped extension to a stable folder, writes the launcher script and host manifest, and copies
/// the manifest to every installed Chromium-family browser. Idempotent: every write goes through
/// <c>WriteIfChanged</c>, so a second run touches nothing.
/// </summary>
public static class NativeHostRegistrar
{
    private const string MetadataDirName = "_metadata";
    private const string HostCommandArgs = "native-messaging-host";
    private const string ManifestDescription = "Wire Copy — save articles to your Reading List";
    private const string LauncherComment = "Wire Copy browser-extension host — written by the app; do not edit.";
    private const UnixFileMode LauncherMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const string GuideResourceName = "WireCopy.BrowserExtension.add-extension.html";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Mirrors the extension, writes the launcher and manifest, and registers the host with each browser found.
    /// </summary>
    /// <param name="env">The machine layout (real or a test fake).</param>
    /// <param name="command">How the launcher should start this app in host mode.</param>
    /// <param name="sourceExtensionDir">
    /// The shipped extension folder; defaults to <c>{AppContext.BaseDirectory}/browser-extension</c>.
    /// Skipped quietly when it does not exist.
    /// </param>
    /// <returns>One entry per known browser, detected or not.</returns>
    public static IReadOnlyList<BrowserRegistration> Register(
        NativeHostEnvironment env,
        NativeHostCommand command,
        string? sourceExtensionDir = null)
    {
        MirrorExtension(
            sourceExtensionDir ?? Path.Combine(AppContext.BaseDirectory, "browser-extension"),
            BrowserExtensionPaths.ExtensionDir(env.LocalAppData),
            env.Platform);

        WriteGuidePage(Path.GetFullPath(BrowserExtensionPaths.GuidePath(env.LocalAppData)));

        var launcherPath = Path.GetFullPath(BrowserExtensionPaths.LauncherPath(env.LocalAppData));
        WriteLauncher(launcherPath, command, env.Platform);

        var manifestPath = Path.GetFullPath(BrowserExtensionPaths.ManifestPath(env.LocalAppData));
        var manifestBytes = Utf8NoBom.GetBytes(BuildManifestJson(launcherPath));
        WriteIfChanged(manifestPath, manifestBytes);

        var results = new List<BrowserRegistration>();
        foreach (var browser in BuildBrowsers(env))
        {
            results.Add(RegisterBrowser(env, browser, manifestPath, manifestBytes));
        }

        return results;
    }

    private static BrowserRegistration RegisterBrowser(
        NativeHostEnvironment env,
        BrowserDefinition browser,
        string manifestPath,
        byte[] manifestBytes)
    {
        var existingDirs = browser.ConfigDirs.Where(Directory.Exists).ToList();
        var executable = FindExecutable(env, browser.Executables);
        var detected = existingDirs.Count > 0 || executable is not null;
        var manifestPaths = new List<string>();

        if (detected && env.Platform == NativePlatform.Windows)
        {
            if (browser.RegistryKey is not null && env.RegistryWrite is not null)
            {
                env.RegistryWrite(browser.RegistryKey + @"\NativeMessagingHosts\" + BrowserExtensionPaths.HostName, manifestPath);
                manifestPaths.Add(manifestPath);
            }
        }
        else if (detected)
        {
            // Browsers that already have a profile folder get the manifest there; an
            // executable-only browser (never launched) gets its primary folder created.
            var targets = existingDirs.Count > 0 ? existingDirs : new List<string> { browser.ConfigDirs[0] };
            foreach (var dir in targets)
            {
                var target = Path.Combine(dir, "NativeMessagingHosts", BrowserExtensionPaths.HostName + ".json");
                WriteIfChanged(target, manifestBytes);
                manifestPaths.Add(target);
            }
        }

        return new BrowserRegistration(browser.Id, browser.DisplayName, detected, manifestPaths, executable);
    }

    private static string? FindExecutable(NativeHostEnvironment env, IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate))
            {
                if (env.FileExists(candidate))
                {
                    return candidate;
                }

                continue;
            }

            foreach (var dir in env.PathDirectories)
            {
                var full = Path.Combine(dir, candidate);
                if (env.FileExists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the "Add Wire Copy" guide page (the embedded <c>add-extension.html</c>) beside the exported
    /// extension; rewritten whenever it differs, so an app update refreshes it.
    /// </summary>
    private static void WriteGuidePage(string guidePath)
    {
        using var stream = typeof(NativeHostRegistrar).Assembly.GetManifestResourceStream(GuideResourceName)
            ?? throw new InvalidOperationException("Missing embedded resource " + GuideResourceName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        WriteIfChanged(guidePath, buffer.ToArray());
    }

    private static string BuildManifestJson(string launcherPath)
    {
        var manifest = new
        {
            name = BrowserExtensionPaths.HostName,
            description = ManifestDescription,
            path = launcherPath,
            type = "stdio",
            allowed_origins = new[] { BrowserExtensionPaths.AllowedOrigin },
        };

        return JsonSerializer.Serialize(manifest, ManifestJsonOptions) + "\n";
    }

    private static void WriteLauncher(string launcherPath, NativeHostCommand command, NativePlatform platform)
    {
        if (platform == NativePlatform.Windows)
        {
            var invocation = command.EntryDll is null
                ? $"\"{command.Executable}\" {HostCommandArgs} %*"
                : $"\"{command.Executable}\" exec \"{command.EntryDll}\" {HostCommandArgs} %*";
            WriteIfChanged(launcherPath, Utf8NoBom.GetBytes("@echo off\r\n" + invocation + "\r\n"));
            return;
        }

        var sb = new StringBuilder();
        sb.Append("#!/bin/sh\n");
        sb.Append("# ").Append(LauncherComment).Append('\n');
        if (command.EntryDll is null)
        {
            sb.Append("exec ").Append(ShellQuote(command.Executable)).Append(' ').Append(HostCommandArgs).Append(" \"$@\"\n");
        }
        else
        {
            var dotnetRoot = Path.GetDirectoryName(command.Executable) ?? string.Empty;
            sb.Append("DOTNET_ROOT=").Append(ShellQuote(dotnetRoot)).Append('\n');
            sb.Append("export DOTNET_ROOT\n");
            sb.Append("exec ").Append(ShellQuote(command.Executable))
                .Append(" exec ").Append(ShellQuote(command.EntryDll))
                .Append(' ').Append(HostCommandArgs).Append(" \"$@\"\n");
        }

        WriteIfChanged(launcherPath, Utf8NoBom.GetBytes(sb.ToString()), LauncherMode);
    }

    /// <summary>Single-quotes a value for /bin/sh, turning each embedded <c>'</c> into <c>'\''</c>.</summary>
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Makes <paramref name="destDir"/> match <paramref name="sourceDir"/>: copies new/changed files,
    /// removes files and empty folders the app no longer ships, and leaves a top-level <c>_metadata</c>
    /// folder alone (the browser writes it into an unpacked extension).
    /// </summary>
    private static void MirrorExtension(string sourceDir, string destDir, NativePlatform platform)
    {
        if (!Directory.Exists(sourceDir))
        {
            return;
        }

        var comparer = platform == NativePlatform.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var shipped = new HashSet<string>(comparer);

        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            shipped.Add(relative);
            WriteIfChanged(Path.Combine(destDir, relative), File.ReadAllBytes(file));
        }

        var staleFiles = Directory.EnumerateFiles(destDir, "*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var relative = Path.GetRelativePath(destDir, f);
                return !IsInMetadata(relative) && !shipped.Contains(relative);
            })
            .ToList();
        foreach (var file in staleFiles)
        {
            File.Delete(file);
        }

        var candidateDirs = Directory.EnumerateDirectories(destDir, "*", SearchOption.AllDirectories)
            .Where(d => !IsInMetadata(Path.GetRelativePath(destDir, d)))
            .OrderByDescending(d => d.Length)
            .ToList();
#pragma warning disable S3267 // Emptiness must be re-checked after each delete (deepest first), so it cannot be a pre-filter.
        foreach (var dir in candidateDirs)
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
#pragma warning restore S3267
    }

    private static bool IsInMetadata(string relativePath)
    {
        var first = relativePath.Split(Path.DirectorySeparatorChar, 2)[0];
        return string.Equals(first, MetadataDirName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> only when it differs from what is
    /// there (UTF-8 bytes as given; temp file then move).
    /// </summary>
    private static void WriteIfChanged(string path, byte[] content, UnixFileMode? unixMode = null)
    {
        var unchanged = false;
        var info = new FileInfo(path);
        if (info.Exists && info.Length == content.Length)
        {
            unchanged = File.ReadAllBytes(path).AsSpan().SequenceEqual(content);
        }

        if (unchanged)
        {
            if (unixMode is { } wanted && !OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != wanted)
            {
                File.SetUnixFileMode(path, wanted);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(temp, content);
        if (unixMode is { } mode && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, mode);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static IReadOnlyList<BrowserDefinition> BuildBrowsers(NativeHostEnvironment env)
    {
        var cfg = env.ConfigRoot;
        switch (env.Platform)
        {
            case NativePlatform.MacOS:
                return new[]
                {
                    MacBrowser(env, "chrome", "Google Chrome", new[] { Path.Combine(cfg, "Google", "Chrome") }, "Google Chrome"),
                    MacBrowser(env, "chromium", "Chromium", new[] { Path.Combine(cfg, "Chromium") }, "Chromium"),
                    MacBrowser(env, "vivaldi", "Vivaldi", new[] { Path.Combine(cfg, "Vivaldi") }, "Vivaldi"),
                    MacBrowser(env, "brave", "Brave", new[] { Path.Combine(cfg, "BraveSoftware", "Brave-Browser") }, "Brave Browser"),
                    MacBrowser(env, "edge", "Microsoft Edge", new[] { Path.Combine(cfg, "Microsoft Edge") }, "Microsoft Edge"),
                    MacBrowser(env, "arc", "Arc", new[] { Path.Combine(cfg, "Arc", "User Data") }, "Arc"),
                };

            case NativePlatform.Windows:
                return new[]
                {
                    WindowsBrowser(env, "chrome", "Google Chrome", @"Software\Google\Chrome", new[] { "Google", "Chrome" }, @"Google\Chrome\Application\chrome.exe"),
                    WindowsBrowser(env, "chromium", "Chromium", @"Software\Chromium", new[] { "Chromium" }, @"Chromium\Application\chrome.exe"),
                    WindowsBrowser(env, "vivaldi", "Vivaldi", @"Software\Vivaldi", new[] { "Vivaldi" }, @"Vivaldi\Application\vivaldi.exe"),
                    WindowsBrowser(env, "brave", "Brave", @"Software\BraveSoftware\Brave-Browser", new[] { "BraveSoftware", "Brave-Browser" }, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                    WindowsBrowser(env, "edge", "Microsoft Edge", @"Software\Microsoft\Edge", new[] { "Microsoft", "Edge" }, @"Microsoft\Edge\Application\msedge.exe"),
                };

            default:
                return new[]
                {
                    new BrowserDefinition(
                        "chrome",
                        "Google Chrome",
                        new[] { Path.Combine(cfg, "google-chrome"), Path.Combine(cfg, "google-chrome-beta"), Path.Combine(cfg, "google-chrome-unstable") },
                        new[] { "google-chrome", "google-chrome-stable", "/opt/google/chrome/chrome" }),
                    new BrowserDefinition(
                        "chromium",
                        "Chromium",
                        new[] { Path.Combine(cfg, "chromium") },
                        new[] { "chromium", "chromium-browser", "/snap/bin/chromium" }),
                    new BrowserDefinition(
                        "vivaldi",
                        "Vivaldi",
                        new[] { Path.Combine(cfg, "vivaldi"), Path.Combine(cfg, "vivaldi-snapshot") },
                        new[] { "vivaldi", "vivaldi-stable", "/opt/vivaldi/vivaldi" }),
                    new BrowserDefinition(
                        "brave",
                        "Brave",
                        new[] { Path.Combine(cfg, "BraveSoftware", "Brave-Browser") },
                        new[] { "brave-browser", "/opt/brave.com/brave/brave" }),
                    new BrowserDefinition(
                        "edge",
                        "Microsoft Edge",
                        new[] { Path.Combine(cfg, "microsoft-edge") },
                        new[] { "microsoft-edge", "microsoft-edge-stable", "/opt/microsoft/msedge/msedge" }),
                };
        }
    }

    private static BrowserDefinition MacBrowser(
        NativeHostEnvironment env,
        string id,
        string displayName,
        string[] configDirs,
        string appName)
    {
        var binary = Path.Combine(appName + ".app", "Contents", "MacOS", appName);
        return new BrowserDefinition(
            id,
            displayName,
            configDirs,
            new[] { Path.Combine("/Applications", binary), Path.Combine(env.Home, "Applications", binary) });
    }

    private static BrowserDefinition WindowsBrowser(
        NativeHostEnvironment env,
        string id,
        string displayName,
        string registryKey,
        string[] vendorSegments,
        string relativeExecutable)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            env.LocalAppData,
        };

        return new BrowserDefinition(
            id,
            displayName,
            new[] { Path.Combine(new[] { env.ConfigRoot }.Concat(vendorSegments).ToArray()) },
            roots.Where(r => !string.IsNullOrEmpty(r)).Select(r => Path.Combine(r, relativeExecutable)).ToArray(),
            registryKey);
    }

    /// <summary>One browser's profile folders and executables for the current platform.</summary>
    private sealed record BrowserDefinition(
        string Id,
        string DisplayName,
        IReadOnlyList<string> ConfigDirs,
        IReadOnlyList<string> Executables,
        string? RegistryKey = null);
}
