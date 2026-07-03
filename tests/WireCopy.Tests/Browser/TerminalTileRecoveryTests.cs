// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-9k27.6: crash-safe terminal tile recovery — the record round-trips,
/// startup recovery drives the matched-window restore script (which targets the
/// window still sitting on the recorded tile, never <c>window 1</c>, and no-ops
/// when the user rearranged it), and the record is always cleared afterwards.
/// </summary>
[Trait("Category", "Unit")]
public class TerminalTileRecoveryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tile-recovery-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void Record_RoundTrips_ThroughDisk()
    {
        TerminalTileRecovery.Record(
            "com.mitchellh.ghostty",
            new TerminalTiling.WindowRect(10, 20, 1600, 900),
            new TerminalTiling.WindowRect(0, 25, 1000, 875),
            NullLogger.Instance,
            _path);

        var read = TerminalTileRecovery.TryRead(_path);
        read.Should().NotBeNull();
        read!.BundleId.Should().Be("com.mitchellh.ghostty");
        read.PreW.Should().Be(1600);
        read.TileW.Should().Be(1000);

        TerminalTileRecovery.Clear(NullLogger.Instance, _path);
        TerminalTileRecovery.TryRead(_path).Should().BeNull();
    }

    [Fact]
    public async Task RecoverAsync_DrivesTheMatchedWindowRestore_WithRecordedTileAndPreDockBounds()
    {
        TerminalTileRecovery.Record(
            "com.mitchellh.ghostty",
            new TerminalTiling.WindowRect(10, 20, 1600, 900),
            new TerminalTiling.WindowRect(0, 25, 1000, 875),
            NullLogger.Instance,
            _path);

        var scripts = new List<string>();
        Task<(int, string, string)?> FakeOsascript(string script)
        {
            scripts.Add(script);
            return Task.FromResult<(int, string, string)?>((0, TerminalTiling.RestoreResultRestored, ""));
        }

        await TerminalTileRecovery.RecoverAsync(FakeOsascript, NullLogger.Instance, _path);

        // workspace-9k27.6 (minor): ONE script that matches the tiled window itself —
        // never `window 1`, which in a multi-window terminal may be a different window.
        scripts.Should().HaveCount(1, "the matched-window script probes and restores in one pass");
        scripts[0].Should().NotContain("window 1", "restore must target the tiled window, not the frontmost");
        scripts[0].Should().Contain("com.mitchellh.ghostty");
        scripts[0].Should().Contain("{10, 20}").And.Contain("{1600, 900}", "restore = the PRE-DOCK bounds");
        scripts[0].Should().Contain("(1000)").And.Contain("(875)", "the match is against the recorded TILE");
        TerminalTileRecovery.TryRead(_path).Should().BeNull("the record is cleared after recovery");
    }

    [Fact]
    public async Task RecoverAsync_ClearsTheRecord_WhenNoWindowMatchesTheTile()
    {
        TerminalTileRecovery.Record(
            "com.mitchellh.ghostty",
            new TerminalTiling.WindowRect(10, 20, 1600, 900),
            new TerminalTiling.WindowRect(0, 25, 1000, 875),
            NullLogger.Instance,
            _path);

        var scripts = new List<string>();
        Task<(int, string, string)?> FakeOsascript(string script)
        {
            scripts.Add(script);

            // The user moved/resized/closed the tiled window → the script no-ops.
            return Task.FromResult<(int, string, string)?>((0, TerminalTiling.RestoreResultNoMatch, ""));
        }

        await TerminalTileRecovery.RecoverAsync(FakeOsascript, NullLogger.Instance, _path);

        scripts.Should().HaveCount(1);
        TerminalTileRecovery.TryRead(_path).Should().BeNull("the stale record is still discarded");
    }

    [Fact]
    public async Task RecoverAsync_ClearsTheRecord_WhenOsascriptFails()
    {
        TerminalTileRecovery.Record(
            "com.mitchellh.ghostty",
            new TerminalTiling.WindowRect(10, 20, 1600, 900),
            new TerminalTiling.WindowRect(0, 25, 1000, 875),
            NullLogger.Instance,
            _path);

        static Task<(int, string, string)?> FakeOsascript(string script) =>
            Task.FromResult<(int, string, string)?>((1, "", "not authorized"));

        await TerminalTileRecovery.RecoverAsync(FakeOsascript, NullLogger.Instance, _path);

        TerminalTileRecovery.TryRead(_path).Should().BeNull(
            "a failed recovery must not retry forever on every startup");
    }
}
