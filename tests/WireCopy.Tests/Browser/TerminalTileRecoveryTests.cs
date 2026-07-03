// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-9k27.6: crash-safe terminal tile recovery — the record round-trips,
/// the restore decision respects user rearrangement, and startup recovery drives
/// the set-bounds script only when the live window still matches the tile.
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

    private static TerminalTileRecovery.TileRecord MakeRecord() => new(
        "com.mitchellh.ghostty",
        PreX: 10, PreY: 20, PreW: 1600, PreH: 900,
        TileX: 0, TileY: 25, TileW: 1000, TileH: 875);

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
    public void ShouldRestore_TrueWhenWindowStillOnTile_FalseWhenUserMovedIt()
    {
        var record = MakeRecord();

        TerminalTileRecovery.ShouldRestore(new TerminalTiling.WindowRect(0, 25, 1000, 875), record)
            .Should().BeTrue("the window is exactly where we tiled it");
        TerminalTileRecovery.ShouldRestore(new TerminalTiling.WindowRect(3, 27, 995, 872), record)
            .Should().BeTrue("within tolerance — WMs nudge by a few px");
        TerminalTileRecovery.ShouldRestore(new TerminalTiling.WindowRect(400, 300, 800, 600), record)
            .Should().BeFalse("the user moved/resized the window — their layout wins");
    }

    [Fact]
    public async Task RecoverAsync_RestoresPreDockBounds_WhenWindowStillMatchesTile()
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
            // First call = get bounds → report the window still ON the tile.
            return Task.FromResult<(int, string, string)?>(
                script.Contains("get position") ? (0, "0, 25, 1000, 875", "") : (0, "", ""));
        }

        await TerminalTileRecovery.RecoverAsync(FakeOsascript, NullLogger.Instance, _path);

        scripts.Should().HaveCount(2, "a get-bounds probe then a set-bounds restore");
        scripts[1].Should().Contain("{10, 20}").And.Contain("{1600, 900}", "restore = the PRE-DOCK bounds");
        TerminalTileRecovery.TryRead(_path).Should().BeNull("the record is cleared after recovery");
    }

    [Fact]
    public async Task RecoverAsync_LeavesWindowAlone_WhenUserRearrangedIt()
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
            return Task.FromResult<(int, string, string)?>((0, "500, 300, 900, 700", ""));
        }

        await TerminalTileRecovery.RecoverAsync(FakeOsascript, NullLogger.Instance, _path);

        scripts.Should().HaveCount(1, "only the probe — no set-bounds when the user moved the window");
        TerminalTileRecovery.TryRead(_path).Should().BeNull("the stale record is still discarded");
    }
}
