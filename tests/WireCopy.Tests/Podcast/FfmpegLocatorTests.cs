// Licensed under the MIT License. See LICENSE in the repository root.

using FFMpegCore;
using FluentAssertions;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-ja7bc.2 (ported from workspace-koos): FfmpegLocator must select a directory only
/// when it holds BOTH ffmpeg and ffprobe, so the screencast-only bundled ffmpeg (no ffprobe) is
/// never chosen, and must honour the explicit override + PATH ordering. Filesystem-only, no real
/// ffmpeg required. PATH and FFMpegCore's global options are process-global, hence the serialized
/// <see cref="ProcessGlobalStateCollection"/>.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
[Trait("Category", "Unit")]
public sealed class FfmpegLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wc-ffmpeg-" + Guid.NewGuid().ToString("N"));
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
    private readonly string? _originalOverride = Environment.GetEnvironmentVariable(FfmpegLocator.OverrideEnv);
    private readonly string _originalBinaryFolder = GlobalFFOptions.Current.BinaryFolder;

    public FfmpegLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        Environment.SetEnvironmentVariable(FfmpegLocator.OverrideEnv, _originalOverride);
        FfmpegLocator.WellKnownDirsOverride = null;
        GlobalFFOptions.Configure(o => o.BinaryFolder = _originalBinaryFolder);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    // Guards:   a directory counts as an ffmpeg toolchain only if it holds BOTH ffmpeg and ffprobe.
    // Fails if: IsCompleteToolchain accepts an ffmpeg-only directory (e.g. the screencast ffmpeg).
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~IsCompleteToolchain_RequiresBothBinaries"
    [Fact]
    public void IsCompleteToolchain_RequiresBothBinaries()
    {
        var both = MakeDir("both", "ffmpeg", "ffprobe");
        var ffmpegOnly = MakeDir("ffmpeg-only", "ffmpeg");
        var ffprobeOnly = MakeDir("ffprobe-only", "ffprobe");
        var empty = MakeDir("empty");

        FfmpegLocator.IsCompleteToolchain(both).Should().BeTrue();
        FfmpegLocator.IsCompleteToolchain(ffmpegOnly).Should().BeFalse("a screencast-only ffmpeg with no ffprobe must be rejected");
        FfmpegLocator.IsCompleteToolchain(ffprobeOnly).Should().BeFalse();
        FfmpegLocator.IsCompleteToolchain(empty).Should().BeFalse();
    }

    // Guards:   WIRECOPY_FFMPEG_DIR, when it points at a complete toolchain, wins over everything.
    // Fails if: the override is ignored or PATH/well-known dirs are consulted before it.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ResolveDirectory_PrefersExplicitOverride_WhenComplete"
    [Fact]
    public void ResolveDirectory_PrefersExplicitOverride_WhenComplete()
    {
        var over = MakeDir("override", "ffmpeg", "ffprobe");
        var onPath = MakeDir("on-path", "ffmpeg", "ffprobe");

        FfmpegLocator.ResolveDirectory(over, onPath, Array.Empty<string>())
            .Should().Be(over);
    }

    // Guards:   an override missing ffprobe does not win; resolution falls through to PATH.
    // Fails if: an incomplete override directory is returned (ffprobe-less ffmpeg would be used).
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ResolveDirectory_IgnoresOverride_WhenIncomplete"
    [Fact]
    public void ResolveDirectory_IgnoresOverride_WhenIncomplete()
    {
        var incompleteOverride = MakeDir("override-incomplete", "ffmpeg");
        var onPath = MakeDir("on-path", "ffmpeg", "ffprobe");

        FfmpegLocator.ResolveDirectory(incompleteOverride, onPath, Array.Empty<string>())
            .Should().Be(onPath, "an override missing ffprobe must not win; fall through to a complete PATH entry");
    }

    // Guards:   Playwright's bundled screencast ffmpeg directory is never selected from PATH.
    // Fails if: the ms-playwright/.playwright exclusion is removed from the PATH scan.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ResolveDirectory_SkipsScreencastBundledFfmpeg_OnPath"
    [Fact]
    public void ResolveDirectory_SkipsScreencastBundledFfmpeg_OnPath()
    {
        // A ".playwright"/"ms-playwright" dir that even somehow has both binaries must be skipped.
        var screencast = MakeDir(Path.Combine("ms-playwright", "ffmpeg-1011"), "ffmpeg", "ffprobe");
        var real = MakeDir("real", "ffmpeg", "ffprobe");
        var pathVar = string.Join(Path.PathSeparator, screencast, real);

        FfmpegLocator.ResolveDirectory(overrideDir: null, pathVar, Array.Empty<string>())
            .Should().Be(real);
    }

    // Guards:   the Finder-launch case: a minimal PATH with no ffmpeg falls back to well-known dirs.
    // Fails if: the well-known fallback is removed or skipped when PATH has no ffmpeg at all.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ResolveDirectory_FallsBackToWellKnown_WhenPathHasNoToolchain"
    [Fact]
    public void ResolveDirectory_FallsBackToWellKnown_WhenPathHasNoToolchain()
    {
        var wellKnown = MakeDir("wellknown", "ffmpeg", "ffprobe");
        var pathNoTools = MakeDir("path-no-tools");

        FfmpegLocator.ResolveDirectory(overrideDir: null, pathNoTools, new[] { "/nope/does/not/exist", wellKnown })
            .Should().Be(wellKnown);
    }

    // Guards:   when only an incomplete ffmpeg exists anywhere, the result is null (bare name, so
    //           the existing "FFmpeg is not installed" handling fires).
    // Fails if: an ffmpeg-only directory is returned as if it were a usable toolchain.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ResolveDirectory_ReturnsNull_WhenNothingComplete"
    [Fact]
    public void ResolveDirectory_ReturnsNull_WhenNothingComplete()
    {
        var ffmpegOnly = MakeDir("only-ffmpeg", "ffmpeg");

        FfmpegLocator.ResolveDirectory(null, ffmpegOnly, new[] { "/also/missing" })
            .Should().BeNull();
    }

    // Guards:   an ffprobe-less ffmpeg first on PATH (a PATH-based stand-in) stays authoritative.
    // Fails if: resolution skips past it to a later PATH dir or a well-known dir, which would
    //           bypass a fake/pinned ffmpeg that other tests and users deliberately put first.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ResolveDirectory_FfprobeLessFfmpegFirstOnPath_ReturnsNull"
    [Fact]
    public void ResolveDirectory_FfprobeLessFfmpegFirstOnPath_ReturnsNull()
    {
        var standIn = MakeDir("stand-in", "ffmpeg");
        var laterPath = MakeDir("later-path", "ffmpeg", "ffprobe");
        var wellKnown = MakeDir("wellknown", "ffmpeg", "ffprobe");
        var pathVar = string.Join(Path.PathSeparator, standIn, laterPath);

        FfmpegLocator.ResolveDirectory(overrideDir: null, pathVar, new[] { wellKnown })
            .Should().BeNull("the first PATH ffmpeg is authoritative even without ffprobe, so the OS keeps resolving the bare name");
    }

    // Guards:   Configure() leaves FFMpegCore on PATH when the resolved dir is already on PATH,
    //           and pins BinaryFolder only for a toolchain found outside PATH.
    // Fails if: Configure() pins a directory that is on PATH (freezing a PATH that can change),
    //           or fails to pin one that is not (FFMpegCore then cannot find ffmpeg).
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~Configure_PinsBinaryFolder_OnlyWhenResolvedDirIsOffPath"
    [Fact]
    public void Configure_PinsBinaryFolder_OnlyWhenResolvedDirIsOffPath()
    {
        Environment.SetEnvironmentVariable(FfmpegLocator.OverrideEnv, null);
        var onPath = MakeDir("on-path", "ffmpeg", "ffprobe");
        var offPath = MakeDir("off-path", "ffmpeg", "ffprobe");
        var emptyPathDir = MakeDir("empty-path");

        // Toolchain on PATH: nothing is pinned, FFMpegCore keeps resolving through PATH.
        Environment.SetEnvironmentVariable("PATH", onPath);
        FfmpegLocator.WellKnownDirsOverride = Array.Empty<string>();
        FfmpegLocator.Configure().Should().BeNull();
        GlobalFFOptions.Current.BinaryFolder.Should().BeEmpty();

        // PATH has no ffmpeg but a well-known dir does: that dir is pinned.
        Environment.SetEnvironmentVariable("PATH", emptyPathDir);
        FfmpegLocator.WellKnownDirsOverride = new[] { offPath };
        FfmpegLocator.Configure().Should().Be(offPath);
        GlobalFFOptions.Current.BinaryFolder.Should().Be(offPath);
        FfmpegLocator.FfmpegPath.Should().Be(Path.Combine(offPath, "ffmpeg"));

        // Back to a PATH toolchain: the pin is released again.
        Environment.SetEnvironmentVariable("PATH", onPath);
        FfmpegLocator.Configure().Should().BeNull();
        GlobalFFOptions.Current.BinaryFolder.Should().BeEmpty();
    }

    private string MakeDir(string name, params string[] binaries)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var bin in binaries)
        {
            var exe = OperatingSystem.IsWindows() ? bin + ".exe" : bin;
            File.WriteAllText(Path.Combine(dir, exe), "#!/bin/sh\n");
        }

        return dir;
    }
}
