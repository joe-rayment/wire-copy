// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class OutputFolderPurgerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly OutputFolderPurger _sut;

    public OutputFolderPurgerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"output-purger-test-{Guid.NewGuid():N}");
        _sut = new OutputFolderPurger(NullLogger<OutputFolderPurger>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task PurgeOldFilesAsync_MissingFolder_ReturnsZeroAndDoesNotThrow()
    {
        var bogusPath = Path.Combine(_tempDir, "does", "not", "exist");

        var deleted = await _sut.PurgeOldFilesAsync(bogusPath, TimeSpan.FromHours(36));

        deleted.Should().Be(0);
        Directory.Exists(bogusPath).Should().BeFalse();
    }

    [Fact]
    public async Task PurgeOldFilesAsync_NullOrWhitespaceFolder_ReturnsZero()
    {
        (await _sut.PurgeOldFilesAsync(string.Empty, TimeSpan.FromHours(36))).Should().Be(0);
        (await _sut.PurgeOldFilesAsync("   ", TimeSpan.FromHours(36))).Should().Be(0);
    }

    [Fact]
    public async Task PurgeOldFilesAsync_EmptyFolder_ReturnsZero()
    {
        Directory.CreateDirectory(_tempDir);

        var deleted = await _sut.PurgeOldFilesAsync(_tempDir, TimeSpan.FromHours(36));

        deleted.Should().Be(0);
        Directory.Exists(_tempDir).Should().BeTrue();
    }

    [Fact]
    public async Task PurgeOldFilesAsync_NoOldFiles_KeepsAllFiles()
    {
        Directory.CreateDirectory(_tempDir);
        var fresh1 = CreateFileWithAge("fresh1.m4b", TimeSpan.FromHours(1));
        var fresh2 = CreateFileWithAge("fresh2.mp3", TimeSpan.FromHours(10));

        var deleted = await _sut.PurgeOldFilesAsync(_tempDir, TimeSpan.FromHours(36));

        deleted.Should().Be(0);
        File.Exists(fresh1).Should().BeTrue();
        File.Exists(fresh2).Should().BeTrue();
    }

    [Fact]
    public async Task PurgeOldFilesAsync_MixedOldAndNew_DeletesOnlyOldM4bAndMp3()
    {
        Directory.CreateDirectory(_tempDir);
        var oldM4b = CreateFileWithAge("old.m4b", TimeSpan.FromHours(48));
        var oldMp3 = CreateFileWithAge("old.mp3", TimeSpan.FromHours(72));
        var freshM4b = CreateFileWithAge("fresh.m4b", TimeSpan.FromHours(1));

        var deleted = await _sut.PurgeOldFilesAsync(_tempDir, TimeSpan.FromHours(36));

        deleted.Should().Be(2);
        File.Exists(oldM4b).Should().BeFalse();
        File.Exists(oldMp3).Should().BeFalse();
        File.Exists(freshM4b).Should().BeTrue();
    }

    [Fact]
    public async Task PurgeOldFilesAsync_DoesNotDeleteOtherExtensions()
    {
        Directory.CreateDirectory(_tempDir);
        // Audio outputs and the feed manifest go; everything else stays.
        var oldM4b = CreateFileWithAge("old.m4b", TimeSpan.FromHours(72));
        var oldM4a = CreateFileWithAge("old.m4a", TimeSpan.FromHours(72));
        var oldFeedXml = CreateFileWithAge("feed.xml", TimeSpan.FromHours(72));
        var oldTxt = CreateFileWithAge("old.txt", TimeSpan.FromHours(72));
        var oldJson = CreateFileWithAge("index.json", TimeSpan.FromHours(72));
        var oldWav = CreateFileWithAge("old.wav", TimeSpan.FromHours(72));

        var deleted = await _sut.PurgeOldFilesAsync(_tempDir, TimeSpan.FromHours(36));

        deleted.Should().Be(3);
        File.Exists(oldM4b).Should().BeFalse();
        File.Exists(oldM4a).Should().BeFalse();
        File.Exists(oldFeedXml).Should().BeFalse();
        File.Exists(oldTxt).Should().BeTrue();
        File.Exists(oldJson).Should().BeTrue();
        File.Exists(oldWav).Should().BeTrue();
    }

    [Fact]
    public async Task PurgeOldFilesAsync_CaseInsensitiveExtensionMatch()
    {
        Directory.CreateDirectory(_tempDir);
        var m4bUpper = CreateFileWithAge("upper.M4B", TimeSpan.FromHours(48));
        var mp3Mixed = CreateFileWithAge("mixed.Mp3", TimeSpan.FromHours(48));

        var deleted = await _sut.PurgeOldFilesAsync(_tempDir, TimeSpan.FromHours(36));

        deleted.Should().Be(2);
        File.Exists(m4bUpper).Should().BeFalse();
        File.Exists(mp3Mixed).Should().BeFalse();
    }

    [Fact]
    public async Task PurgeOldFilesAsync_RespectsBoundaryAtTtl()
    {
        Directory.CreateDirectory(_tempDir);
        // Right at the boundary (within a few seconds) should NOT be purged.
        var boundary = CreateFileWithAge("boundary.m4b", TimeSpan.FromHours(36) - TimeSpan.FromMinutes(1));
        // Just past boundary SHOULD be purged.
        var past = CreateFileWithAge("past.m4b", TimeSpan.FromHours(36) + TimeSpan.FromMinutes(1));

        var deleted = await _sut.PurgeOldFilesAsync(_tempDir, TimeSpan.FromHours(36));

        deleted.Should().Be(1);
        File.Exists(boundary).Should().BeTrue();
        File.Exists(past).Should().BeFalse();
    }

    [Fact]
    public async Task PurgeOldFilesAsync_OnlyTopLevelFiles_NotRecursive()
    {
        // The purger is intentionally non-recursive: nested cache dirs (like
        // the ArticleContentCache) should never be touched.
        Directory.CreateDirectory(_tempDir);
        var nestedDir = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(nestedDir);
        var nestedOldM4b = Path.Combine(nestedDir, "deep.m4b");
        File.WriteAllBytes(nestedOldM4b, new byte[] { 1, 2, 3 });
        File.SetLastWriteTimeUtc(nestedOldM4b, DateTime.UtcNow - TimeSpan.FromHours(72));

        var topLevelOldM4b = CreateFileWithAge("top.m4b", TimeSpan.FromHours(72));

        var deleted = await _sut.PurgeOldFilesAsync(_tempDir, TimeSpan.FromHours(36));

        deleted.Should().Be(1);
        File.Exists(topLevelOldM4b).Should().BeFalse();
        File.Exists(nestedOldM4b).Should().BeTrue();
    }

    [Fact]
    public async Task PurgeOldFilesAsync_UnreadableFolder_DoesNotThrow()
    {
        // Path that's a file rather than a directory: Directory.Exists returns
        // false, so the purger should treat it like "missing" — no exception.
        var fileAsFolder = Path.Combine(Path.GetTempPath(), $"not-a-dir-{Guid.NewGuid():N}.txt");
        File.WriteAllText(fileAsFolder, "not a folder");

        try
        {
            var deleted = await _sut.PurgeOldFilesAsync(fileAsFolder, TimeSpan.FromHours(36));
            deleted.Should().Be(0);
        }
        finally
        {
            File.Delete(fileAsFolder);
        }
    }

    [Fact]
    public async Task PurgeOldFilesAsync_LongTtl_KeepsEverything()
    {
        Directory.CreateDirectory(_tempDir);
        var ancient = CreateFileWithAge("ancient.m4b", TimeSpan.FromDays(365));

        var deleted = await _sut.PurgeOldFilesAsync(_tempDir, TimeSpan.FromDays(366 * 10));

        deleted.Should().Be(0);
        File.Exists(ancient).Should().BeTrue();
    }

    [Fact]
    public async Task PurgeOldFilesAsync_ZeroTtl_DeletesAllPurgeableFiles()
    {
        // TTL of 0 means "anything older than now" — i.e. everything that exists.
        Directory.CreateDirectory(_tempDir);
        var m4b = CreateFileWithAge("a.m4b", TimeSpan.FromMinutes(5));
        var txt = CreateFileWithAge("a.txt", TimeSpan.FromMinutes(5));

        var deleted = await _sut.PurgeOldFilesAsync(_tempDir, TimeSpan.Zero);

        deleted.Should().Be(1);
        File.Exists(m4b).Should().BeFalse();
        File.Exists(txt).Should().BeTrue();
    }

    private string CreateFileWithAge(string name, TimeSpan age)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[] { 0xff });
        var stamp = DateTime.UtcNow - age;
        File.SetLastWriteTimeUtc(path, stamp);
        return path;
    }
}
