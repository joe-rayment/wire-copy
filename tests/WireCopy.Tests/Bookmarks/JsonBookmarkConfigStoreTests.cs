// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Infrastructure.Bookmarks;
using WireCopy.Persistence;
using Xunit;

namespace WireCopy.Tests.Bookmarks;

[Trait("Category", "Unit")]
public sealed class JsonBookmarkConfigStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;
    private readonly JsonBookmarkConfigStore _sut;
    private bool _disposed;

    public JsonBookmarkConfigStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wirecopy-bookmark-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "bookmarks.json");
        _sut = new JsonBookmarkConfigStore(
            Substitute.For<ILogger<JsonBookmarkConfigStore>>(),
            _configPath,
            typeof(AppDbContext).Assembly);
    }

    [Fact]
    public async Task LoadShippedDefaults_ContainsAllExpectedSites()
    {
        var defaults = await _sut.LoadShippedDefaultsAsync();

        defaults.Version.Should().Be(JsonBookmarkConfigStore.CurrentSchemaVersion);
        var urls = defaults.Bookmarks.Select(b => b.Url).ToList();
        urls.Should().Contain(new[]
        {
            "http://127.0.0.1:8642/",
            "http://127.0.0.1:8642/world.html",
            "http://127.0.0.1:8642/science.html",
            "http://127.0.0.1:8642/disaster.html",
            "http://127.0.0.1:8642/arts.html",
            "http://127.0.0.1:8642/news/the-titanic-strikes-an-iceberg.html",
        });
    }

    [Fact]
    public async Task LoadShippedDefaults_PreservesArrayOrder()
    {
        var defaults = await _sut.LoadShippedDefaultsAsync();

        // The array order IS the sort order.
        defaults.Bookmarks[0].Name.Should().Be("The Daily Gazette");
        defaults.Bookmarks[^1].Name.Should().Be("Read: Fighting the Flames with Dynamite");
    }

    [Fact]
    public async Task LoadUserConfig_FileMissing_ReturnsNull()
    {
        File.Exists(_configPath).Should().BeFalse();

        var result = await _sut.LoadUserConfigAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip_PreservesEntriesAndOrder()
    {
        var bookmarks = new List<Bookmark>
        {
            Bookmark.Create("First", "https://first.example", 0),
            Bookmark.Create("Second", "https://second.example", 1),
            Bookmark.Create("Third", "https://third.example", 2),
        };

        await _sut.SaveUserConfigAsync(bookmarks);

        File.Exists(_configPath).Should().BeTrue();
        var loaded = await _sut.LoadUserConfigAsync();
        loaded.Should().NotBeNull();
        loaded!.Bookmarks.Select(b => b.Url).Should().Equal(
            "https://first.example",
            "https://second.example",
            "https://third.example");
    }

    [Fact]
    public async Task SaveUserConfig_AtomicWrite_NoStaleTempFileLeftBehind()
    {
        var bookmarks = new List<Bookmark>
        {
            Bookmark.Create("X", "https://x.example", 0),
        };

        await _sut.SaveUserConfigAsync(bookmarks);

        File.Exists(_configPath).Should().BeTrue();
        File.Exists(_configPath + ".tmp").Should().BeFalse(
            "the temp file is renamed onto the final path so nothing should be left behind");
    }

    [Fact]
    public async Task SaveUserConfig_OverwritesExistingFile_AtomicReplacement()
    {
        var first = new List<Bookmark> { Bookmark.Create("Old", "https://old.example", 0) };
        await _sut.SaveUserConfigAsync(first);

        var second = new List<Bookmark> { Bookmark.Create("New", "https://new.example", 0) };
        await _sut.SaveUserConfigAsync(second);

        var loaded = await _sut.LoadUserConfigAsync();
        loaded!.Bookmarks.Should().ContainSingle();
        loaded.Bookmarks[0].Url.Should().Be("https://new.example");
    }

    [Fact]
    public async Task SaveUserConfig_CreatesParentDirectoryIfMissing()
    {
        var nested = Path.Combine(_tempDir, "a", "b", "c", "bookmarks.json");
        var nestedStore = new JsonBookmarkConfigStore(
            Substitute.For<ILogger<JsonBookmarkConfigStore>>(),
            nested,
            typeof(AppDbContext).Assembly);

        await nestedStore.SaveUserConfigAsync(
            new BookmarkConfigFile(
                JsonBookmarkConfigStore.CurrentSchemaVersion,
                new List<BookmarkConfigEntry> { new("X", "https://x.example") }));

        File.Exists(nested).Should().BeTrue();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }

        _disposed = true;
    }
}
