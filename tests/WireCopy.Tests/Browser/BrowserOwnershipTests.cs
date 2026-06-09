// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Infrastructure.Browser.Cache;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-mya7: the share-the-browser decision logic and the interrupted-work
/// checkpoint. The live pause/resume loop is exercised by the one-browser e2e.
/// </summary>
public class BrowserOwnershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsUserActive_RecentInput_True()
    {
        BrowserOwnershipArbiter.IsUserActive(Now.AddSeconds(-3), Now, TimeSpan.FromSeconds(10))
            .Should().BeTrue();
    }

    [Fact]
    public void IsUserActive_OldInput_False()
    {
        BrowserOwnershipArbiter.IsUserActive(Now.AddSeconds(-30), Now, TimeSpan.FromSeconds(10))
            .Should().BeFalse();
    }

    [Fact]
    public void IsUserActive_NeverAnyInput_False()
    {
        BrowserOwnershipArbiter.IsUserActive(null, Now, TimeSpan.FromSeconds(10)).Should().BeFalse();
    }

    [Fact]
    public void CanResume_RequiresTheLongerQuietPeriod()
    {
        // 15s quiet: past the 10s active window but NOT past the 25s resume bar —
        // the user may just be reading; don't fight them yet.
        BrowserOwnershipArbiter.CanResume(Now.AddSeconds(-15), Now, TimeSpan.FromSeconds(25))
            .Should().BeFalse();
        BrowserOwnershipArbiter.CanResume(Now.AddSeconds(-26), Now, TimeSpan.FromSeconds(25))
            .Should().BeTrue();
        BrowserOwnershipArbiter.CanResume(null, Now, TimeSpan.FromSeconds(25)).Should().BeTrue();
    }

    [Fact]
    public void Checkpoint_RoundTrips_AndDeletes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wc-checkpoint-{Guid.NewGuid():N}.json");
        try
        {
            PreloadCheckpoint.Save(path, "https://news.example.com/",
                ["https://news.example.com/a", "https://news.example.com/b"], NullLogger.Instance);

            var loaded = PreloadCheckpoint.Load(path, NullLogger.Instance);
            loaded.Should().NotBeNull();
            loaded!.PageUrl.Should().Be("https://news.example.com/");
            loaded.RemainingUrls.Should().Equal(
                "https://news.example.com/a", "https://news.example.com/b");

            PreloadCheckpoint.Delete(path, NullLogger.Instance);
            File.Exists(path).Should().BeFalse();
            PreloadCheckpoint.Load(path, NullLogger.Instance).Should().BeNull();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Checkpoint_EmptyRemaining_LoadsAsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wc-checkpoint-{Guid.NewGuid():N}.json");
        try
        {
            PreloadCheckpoint.Save(path, "https://x/", [], NullLogger.Instance);
            PreloadCheckpoint.Load(path, NullLogger.Instance).Should().BeNull(
                "an empty checkpoint carries no work and must self-clean");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
