// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

// Guards:   a missing or non-executable Patchright Node driver is restored at startup (from the NuGet cache),
//           so browser launches do not die with "Driver not found" and a blank web pane.
// Fails if: Ensure stops copying the driver when .playwright is absent, or stops re-adding the +x bit
//           (e.g. the cache lookup, the copy, or EnsureExecutable is removed or broken).
// Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter FullyQualifiedName~PlaywrightDriverBootstrapper
//
// workspace-ltre: a missing/non-executable Patchright Node driver made every browser launch die with
// "Driver not found" and a blank web pane. The startup self-heal restores the driver from the NuGet
// cache and re-asserts its executable bit.
[Trait("Category", "Unit")]
public sealed class PlaywrightDriverBootstrapperTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("wc-pw-driver").FullName;

    private static string? FindNode(string baseDir)
    {
        var nodeRoot = Path.Combine(baseDir, ".playwright", "node");
        return Directory.Exists(nodeRoot)
            ? Directory.EnumerateFiles(nodeRoot, OperatingSystem.IsWindows() ? "node.exe" : "node", SearchOption.AllDirectories)
                .FirstOrDefault()
            : null;
    }

    [Fact]
    public void Ensure_RestoresDriverFromNugetCache_WhenMissing()
    {
        // _tmp has no .playwright — the missing-driver case. The Patchright NuGet cache (a build
        // dependency, always restored) is the source.
        var status = PlaywrightDriverBootstrapper.Ensure(_tmp, NullLogger.Instance);

        status.Should().Be(DriverStatus.Healed);
        var node = FindNode(_tmp);
        node.Should().NotBeNull("the driver node must be restored");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(node!).Should().HaveFlag(UnixFileMode.UserExecute, "the driver must be runnable");
        }
    }

    [Fact]
    public void Ensure_ReassertsExecutableBit_WhenDriverPresentButNotExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file mode only.
        }

        PlaywrightDriverBootstrapper.Ensure(_tmp, NullLogger.Instance).Should().Be(DriverStatus.Healed);
        var node = FindNode(_tmp)!;
        File.SetUnixFileMode(node, UnixFileMode.UserRead | UnixFileMode.UserWrite); // strip +x (the bad-bit case)

        var status = PlaywrightDriverBootstrapper.Ensure(_tmp, NullLogger.Instance);

        status.Should().Be(DriverStatus.Present);
        File.GetUnixFileMode(node).Should().HaveFlag(UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }
}
