// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using FluentAssertions;
using WireCopy.Infrastructure.BrowserExtension;
using Xunit;

namespace WireCopy.Tests.BrowserExtension;

[Trait("Category", "Unit")]
public sealed class NativeHostRegistrarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wc-nhr-" + Guid.NewGuid().ToString("N"));

    public NativeHostRegistrarTests()
    {
        Directory.CreateDirectory(_root);
    }

    private string Home => Path.Combine(_root, "home");

    private string ConfigRoot => Path.Combine(Home, ".config");

    private string LocalAppData => Path.Combine(Home, ".local", "share");

    private string BinDir => Path.Combine(_root, "bin");

    private string SourceDir => Path.Combine(_root, "shipped-extension");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Guards:   The browser manifest is written only for browsers that are really installed, with the
    //           host name, launcher path, "stdio" type and the one allowed extension origin.
    // Fails if: The registrar writes a manifest for a browser that is not installed (chromium here),
    //           or leaves out / mangles name, path, type or allowed_origins.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Register_WritesManifestOnlyForDetectedBrowser
    [Fact]
    public void Register_WritesManifestOnlyForDetectedBrowser()
    {
        Directory.CreateDirectory(Path.Combine(ConfigRoot, "vivaldi"));

        var result = NativeHostRegistrar.Register(LinuxEnv(), new NativeHostCommand("/opt/wirecopy/WireCopy.API", null), SourceDir);

        result.Single(r => r.Id == "vivaldi").Detected.Should().BeTrue();
        result.Single(r => r.Id == "chromium").Detected.Should().BeFalse();
        result.Single(r => r.Id == "chromium").ManifestPaths.Should().BeEmpty();

        var expected = Path.Combine(ConfigRoot, "vivaldi", "NativeMessagingHosts", "com.wirecopy.reader.json");
        result.Single(r => r.Id == "vivaldi").ManifestPaths.Should().Equal(expected);
        Directory.Exists(Path.Combine(ConfigRoot, "chromium")).Should().BeFalse();

        using var doc = JsonDocument.Parse(File.ReadAllText(expected));
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be(BrowserExtensionPaths.HostName);
        root.GetProperty("path").GetString().Should().Be(BrowserExtensionPaths.LauncherPath(LocalAppData));
        root.GetProperty("type").GetString().Should().Be("stdio");
        root.GetProperty("allowed_origins").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(BrowserExtensionPaths.AllowedOrigin);
    }

    // Guards:   When the app runs under the dotnet muxer, the launcher sets DOTNET_ROOT and runs
    //           "dotnet exec <dll> native-messaging-host", is executable (0755), and single-quotes
    //           paths even when they contain an apostrophe.
    // Fails if: A path with a ' is not escaped as '\'' (the script would break), the launcher is not
    //           executable, or DOTNET_ROOT is missing.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Register_DotnetMode_WritesQuotedExecutableLauncher
    [Fact]
    public void Register_DotnetMode_WritesQuotedExecutableLauncher()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var command = new NativeHostCommand("/usr/share/o'brien/dotnet", "/apps/wire copy/WireCopy.API.dll");

        NativeHostRegistrar.Register(LinuxEnv(), command, SourceDir);

        var launcher = BrowserExtensionPaths.LauncherPath(LocalAppData);
        File.ReadAllText(launcher).Should().Be(
            "#!/bin/sh\n" +
            "# Wire Copy browser-extension host — written by the app; do not edit.\n" +
            "DOTNET_ROOT='/usr/share/o'\\''brien'\n" +
            "export DOTNET_ROOT\n" +
            "exec '/usr/share/o'\\''brien/dotnet' exec '/apps/wire copy/WireCopy.API.dll' native-messaging-host \"$@\"\n");
        File.GetUnixFileMode(launcher).Should().Be((UnixFileMode)Convert.ToInt32("755", 8));
    }

    // Guards:   A published app (its own executable) gets a launcher that execs the apphost directly
    //           with "native-messaging-host", no dotnet involved, still executable.
    // Fails if: The apphost path is unquoted/mis-quoted (an apostrophe in it), or DOTNET_ROOT lines leak
    //           into apphost mode.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Register_ApphostMode_WritesDirectExecLauncher
    [Fact]
    public void Register_ApphostMode_WritesDirectExecLauncher()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        NativeHostRegistrar.Register(LinuxEnv(), new NativeHostCommand("/home/jo'e/WireCopy.API", null), SourceDir);

        var launcher = BrowserExtensionPaths.LauncherPath(LocalAppData);
        File.ReadAllText(launcher).Should().Be(
            "#!/bin/sh\n" +
            "# Wire Copy browser-extension host — written by the app; do not edit.\n" +
            "exec '/home/jo'\\''e/WireCopy.API' native-messaging-host \"$@\"\n");
        File.GetUnixFileMode(launcher).Should().Be((UnixFileMode)Convert.ToInt32("755", 8));
    }

    // Guards:   Running the registrar a second time changes nothing on disk (no needless rewrites of the
    //           launcher, manifests or mirrored extension files).
    // Fails if: Any output is rewritten when its content is unchanged (its modified time moves).
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Register_SecondRun_WritesNothing
    [Fact]
    public void Register_SecondRun_WritesNothing()
    {
        Directory.CreateDirectory(Path.Combine(ConfigRoot, "vivaldi"));
        Directory.CreateDirectory(Path.Combine(SourceDir, "icons"));
        File.WriteAllText(Path.Combine(SourceDir, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(SourceDir, "icons", "16.png"), "png");
        var command = new NativeHostCommand("/opt/wirecopy/WireCopy.API", null);

        NativeHostRegistrar.Register(LinuxEnv(), command, SourceDir);

        var outputs = new[]
        {
            BrowserExtensionPaths.LauncherPath(LocalAppData),
            BrowserExtensionPaths.ManifestPath(LocalAppData),
            Path.Combine(ConfigRoot, "vivaldi", "NativeMessagingHosts", "com.wirecopy.reader.json"),
            Path.Combine(BrowserExtensionPaths.ExtensionDir(LocalAppData), "manifest.json"),
            Path.Combine(BrowserExtensionPaths.ExtensionDir(LocalAppData), "icons", "16.png"),
        };
        var old = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        foreach (var file in outputs)
        {
            File.Exists(file).Should().BeTrue(file);
            File.SetLastWriteTimeUtc(file, old);
        }

        NativeHostRegistrar.Register(LinuxEnv(), command, SourceDir);

        foreach (var file in outputs)
        {
            File.GetLastWriteTimeUtc(file).Should().Be(old, file);
        }
    }

    // Guards:   A browser that is installed but never launched (no profile folder yet, only an executable
    //           on PATH) still gets the manifest, in its primary config folder, created on demand.
    // Fails if: Executable-only browsers are skipped, or the primary folder is not created.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Register_ExecutableOnlyBrowser_GetsPrimaryDirCreated
    [Fact]
    public void Register_ExecutableOnlyBrowser_GetsPrimaryDirCreated()
    {
        Directory.CreateDirectory(BinDir);
        var chromiumExe = Path.Combine(BinDir, "chromium-browser");
        File.WriteAllText(chromiumExe, "#!/bin/sh\n");

        var result = NativeHostRegistrar.Register(LinuxEnv(), new NativeHostCommand("/opt/wirecopy/WireCopy.API", null), SourceDir);

        var chromium = result.Single(r => r.Id == "chromium");
        chromium.Detected.Should().BeTrue();
        chromium.LaunchExecutable.Should().Be(chromiumExe);
        var expected = Path.Combine(ConfigRoot, "chromium", "NativeMessagingHosts", "com.wirecopy.reader.json");
        chromium.ManifestPaths.Should().Equal(expected);
        File.Exists(expected).Should().BeTrue();
        result.Where(r => r.Id != "chromium").Should().OnlyContain(r => !r.Detected);
    }

    // Guards:   The mirrored extension folder tracks what the app ships: changed files are updated, files
    //           the app no longer ships are deleted (with their emptied folders), and the browser-owned
    //           top-level _metadata folder survives.
    // Fails if: A stale file or empty folder lingers, a changed file keeps old content, or _metadata is deleted.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Register_Mirror_DeletesStaleFilesButKeepsMetadata
    [Fact]
    public void Register_Mirror_DeletesStaleFilesButKeepsMetadata()
    {
        var command = new NativeHostCommand("/opt/wirecopy/WireCopy.API", null);
        var extDir = BrowserExtensionPaths.ExtensionDir(LocalAppData);
        Directory.CreateDirectory(SourceDir);
        File.WriteAllText(Path.Combine(SourceDir, "background.js"), "v1");
        NativeHostRegistrar.Register(LinuxEnv(), command, SourceDir);

        Directory.CreateDirectory(Path.Combine(extDir, "old", "deep"));
        File.WriteAllText(Path.Combine(extDir, "old", "deep", "stale.js"), "x");
        File.WriteAllText(Path.Combine(extDir, "stale.txt"), "x");
        Directory.CreateDirectory(Path.Combine(extDir, "_metadata"));
        File.WriteAllText(Path.Combine(extDir, "_metadata", "verified_contents.json"), "{}");
        File.WriteAllText(Path.Combine(SourceDir, "background.js"), "version-two");

        NativeHostRegistrar.Register(LinuxEnv(), command, SourceDir);

        File.ReadAllText(Path.Combine(extDir, "background.js")).Should().Be("version-two");
        File.Exists(Path.Combine(extDir, "stale.txt")).Should().BeFalse();
        Directory.Exists(Path.Combine(extDir, "old")).Should().BeFalse();
        File.Exists(Path.Combine(extDir, "_metadata", "verified_contents.json")).Should().BeTrue();
    }

    // Guards:   A missing shipped-extension folder (e.g. a dev build) is skipped quietly; the launcher and
    //           manifest are still written so the host can be found.
    // Fails if: Register throws when the source folder is absent, or creates an empty extension folder.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Register_MissingSourceExtension_IsSkippedQuietly
    [Fact]
    public void Register_MissingSourceExtension_IsSkippedQuietly()
    {
        var act = () => NativeHostRegistrar.Register(LinuxEnv(), new NativeHostCommand("/opt/wirecopy/WireCopy.API", null), SourceDir);

        act.Should().NotThrow();
        Directory.Exists(BrowserExtensionPaths.ExtensionDir(LocalAppData)).Should().BeFalse();
        File.Exists(BrowserExtensionPaths.ManifestPath(LocalAppData)).Should().BeTrue();
    }

    // Guards:   On Windows the host is registered through the registry (one HKCU key per detected browser,
    //           pointing at the manifest) and the launcher is a CRLF .cmd; no files go into browser folders.
    // Fails if: The registry callback is skipped or given the wrong key/path, or the .cmd uses LF line endings.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter Register_Windows_UsesRegistryAndCrlfCmdLauncher
    [Fact]
    public void Register_Windows_UsesRegistryAndCrlfCmdLauncher()
    {
        Directory.CreateDirectory(Path.Combine(LocalAppData, "Google", "Chrome"));
        var writes = new List<(string Key, string Manifest)>();
        var env = LinuxEnv() with
        {
            Platform = NativePlatform.Windows,
            ConfigRoot = LocalAppData,
            RegistryWrite = (key, manifest) => writes.Add((key, manifest)),
        };

        var result = NativeHostRegistrar.Register(env, new NativeHostCommand("C:\\Apps\\Wire Copy\\WireCopy.API.exe", null), SourceDir);

        var manifestPath = Path.GetFullPath(BrowserExtensionPaths.ManifestPath(LocalAppData));
        writes.Should().Equal((@"Software\Google\Chrome\NativeMessagingHosts\com.wirecopy.reader", manifestPath));
        result.Single(r => r.Id == "chrome").ManifestPaths.Should().Equal(manifestPath);
        File.ReadAllText(BrowserExtensionPaths.LauncherPath(LocalAppData)).Should().Be(
            "@echo off\r\n\"C:\\Apps\\Wire Copy\\WireCopy.API.exe\" native-messaging-host %*\r\n");
    }

    /// <summary>
    /// A Linux environment rooted in the temp folder. FileExists is false for anything outside it, so
    /// real browsers installed on this machine (/opt/..., /snap/...) never leak into a test.
    /// </summary>
    private NativeHostEnvironment LinuxEnv() =>
        new(
            NativePlatform.Linux,
            Home,
            ConfigRoot,
            LocalAppData,
            new[] { BinDir },
            path => path.StartsWith(_root, StringComparison.Ordinal) && File.Exists(path));
}
