// Licensed under the MIT License. See LICENSE in the repository root.

using FFMpegCore;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Finds an ffmpeg + ffprobe toolchain when the process PATH cannot (workspace-ja7bc.2, ported
/// from 383525f4). A macOS <c>.app</c> launched from Finder/Spotlight inherits only a minimal
/// PATH (<c>/usr/bin:/bin:/usr/sbin:/sbin</c>), NOT the user's shell PATH, so a Homebrew ffmpeg at
/// <c>/opt/homebrew/bin</c> is invisible to a bare <c>Process.Start</c> of "ffmpeg". This probes
/// the well-known Homebrew/usr locations explicitly.
/// <para>
/// Resolution order: the <see cref="OverrideEnv"/> directory; then PATH, where the first
/// directory that holds an ffmpeg is authoritative (a complete one is used by absolute path, an
/// ffprobe-less one means "leave it to the OS", which keeps PATH-based stand-ins working);
/// then the well-known install directories. A directory is picked only when it holds BOTH ffmpeg
/// and ffprobe, and Playwright's screencast-only ffmpeg (no ffprobe, no aac/mp4 encoders) is
/// never picked. When nothing is found the bare names are returned, so the existing
/// "FFmpeg is not installed" handling still fires.
/// </para>
/// Detect-only: installing ffmpeg is the user's (or the launcher's) job.
/// </summary>
internal static class FfmpegLocator
{
    /// <summary>Explicit override: a directory containing ffmpeg+ffprobe.</summary>
    public const string OverrideEnv = "WIRECOPY_FFMPEG_DIR";

    private static readonly object Gate = new();
    private static string? _pinnedDir;

    /// <summary>Absolute path to ffmpeg when a complete toolchain was located, else the bare
    /// name "ffmpeg" (resolved through PATH at exec time). Re-evaluated on every call so a
    /// changed PATH is honoured.</summary>
    public static string FfmpegPath => BinaryPath("ffmpeg");

    /// <summary>Absolute path to ffprobe when a complete toolchain was located, else the bare
    /// name "ffprobe".</summary>
    public static string FfprobePath => BinaryPath("ffprobe");

    /// <summary>
    /// Test hook: replaces the well-known install directories (an empty list disables the
    /// fallback so a PATH-only test is not rescued by a real ffmpeg on the dev machine).
    /// </summary>
    internal static IReadOnlyList<string>? WellKnownDirsOverride { get; set; }

    /// <summary>
    /// Points FFMpegCore's <c>BinaryFolder</c> at the located toolchain, but only when it lies
    /// outside PATH: when PATH already resolves ffmpeg, FFMpegCore finds the same binary itself
    /// and pinning would freeze a PATH that can still change. Safe to call repeatedly. Returns
    /// the directory FFMpegCore is pinned to, or null when it is left to PATH.
    /// </summary>
    public static string? Configure()
    {
        lock (Gate)
        {
            var dir = Resolve();
            var pin = dir is not null && !PathDirectories(Environment.GetEnvironmentVariable("PATH")).Contains(dir)
                ? dir
                : null;

            if (pin != _pinnedDir)
            {
                _pinnedDir = pin;
                GlobalFFOptions.Configure(o => o.BinaryFolder = pin ?? string.Empty);
            }

            return pin;
        }
    }

    /// <summary>
    /// Pure resolution (no global side effects) so it is unit-testable: explicit override first,
    /// then PATH (the first non-screencast directory holding an ffmpeg decides: usable when it
    /// also holds ffprobe, otherwise null so the OS keeps resolving the bare name), then the
    /// well-known install dirs. Null means "no usable directory".
    /// </summary>
    internal static string? ResolveDirectory(string? overrideDir, string? pathVar, IEnumerable<string> wellKnown)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir) && IsCompleteToolchain(overrideDir))
        {
            return overrideDir;
        }

        var pathDir = PathDirectories(pathVar)
            .FirstOrDefault(dir => !IsScreencastOnly(dir) && File.Exists(Path.Combine(dir, ExeName("ffmpeg"))));
        if (pathDir is not null)
        {
            return IsCompleteToolchain(pathDir) ? pathDir : null;
        }

        return wellKnown.FirstOrDefault(IsCompleteToolchain);
    }

    /// <summary>A directory is usable only if it contains BOTH ffmpeg AND ffprobe. This single
    /// rule rejects the screencast-only bundled ffmpeg, which ships no ffprobe.</summary>
    internal static bool IsCompleteToolchain(string dir) =>
        File.Exists(Path.Combine(dir, ExeName("ffmpeg")))
        && File.Exists(Path.Combine(dir, ExeName("ffprobe")));

    private static string? Resolve() => ResolveDirectory(
        Environment.GetEnvironmentVariable(OverrideEnv),
        Environment.GetEnvironmentVariable("PATH"),
        WellKnownDirsOverride ?? WellKnownDirs());

    private static string BinaryPath(string name)
    {
        var dir = Resolve();
        return dir is null ? name : Path.Combine(dir, ExeName(name));
    }

    private static IEnumerable<string> PathDirectories(string? pathVar) =>
        (pathVar ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(dir => !string.IsNullOrWhiteSpace(dir));

    // Belt-and-braces: never select Playwright's bundled screencast ffmpeg even if a stray ffprobe
    // ever appeared beside it.
    private static bool IsScreencastOnly(string dir)
    {
        var norm = dir.Replace('\\', '/');
        return norm.Contains("ms-playwright") || norm.Contains(".playwright");
    }

    private static IEnumerable<string> WellKnownDirs()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/bin";   // Apple-silicon Homebrew
            yield return "/usr/local/bin";       // Intel Homebrew
        }

        yield return "/usr/bin";
        yield return "/usr/local/bin";
    }

    private static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
}
