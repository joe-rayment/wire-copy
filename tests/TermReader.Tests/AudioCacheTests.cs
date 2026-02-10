// <copyright file="AudioCacheTests.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Infrastructure.Caching;

namespace TermReader.Tests;

public class AudioCacheTests : IDisposable
{
    private readonly string _cacheDirectory;
    private readonly ILogger<AudioCache> _logger;
    private readonly AudioCache _cache;

    public AudioCacheTests()
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _logger = Substitute.For<ILogger<AudioCache>>();
        _cache = new AudioCache(_cacheDirectory, _logger);
    }

    [Fact]
    public async Task GetAsync_WithNonExistentContent_ReturnsNull()
    {
        // Act
        var result = await _cache.GetAsync("test content", "voice-123");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAndGetAsync_StoresAndRetrievesAudio()
    {
        // Arrange
        var content = "This is test content for audio generation";
        var voiceId = "voice-123";
        var audioData = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        await _cache.SetAsync(content, voiceId, audioData);
        var retrieved = await _cache.GetAsync(content, voiceId);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved.Should().BeEquivalentTo(audioData);
    }

    [Fact]
    public async Task GetAsync_WithDifferentVoiceId_ReturnsNull()
    {
        // Arrange
        var content = "test content";
        var audioData = new byte[] { 1, 2, 3 };
        await _cache.SetAsync(content, "voice-1", audioData);

        // Act - Query with different voice ID
        var result = await _cache.GetAsync(content, "voice-2");

        // Assert
        result.Should().BeNull(); // Different voice ID should be a cache miss
    }

    [Fact]
    public async Task GetAsync_WithDifferentContent_ReturnsNull()
    {
        // Arrange
        var voiceId = "voice-123";
        var audioData = new byte[] { 1, 2, 3 };
        await _cache.SetAsync("content 1", voiceId, audioData);

        // Act - Query with different content
        var result = await _cache.GetAsync("content 2", voiceId);

        // Assert
        result.Should().BeNull(); // Different content should be a cache miss
    }

    [Fact]
    public async Task GetCacheSizeAsync_ReturnsCorrectSize()
    {
        // Arrange
        await _cache.SetAsync("content 1", "voice-1", new byte[100]);
        await _cache.SetAsync("content 2", "voice-1", new byte[200]);
        await _cache.SetAsync("content 3", "voice-1", new byte[300]);

        // Act
        var size = await _cache.GetCacheSizeAsync();

        // Assert
        size.Should().Be(600); // 100 + 200 + 300
    }

    [Fact]
    public async Task CleanupAsync_RemovesOldFiles()
    {
        // Arrange
        var content1 = "old content";
        var content2 = "new content";
        var audioData = new byte[] { 1, 2, 3 };

        await _cache.SetAsync(content1, "voice-1", audioData);
        await _cache.SetAsync(content2, "voice-1", audioData);

        // Make the first file appear old by modifying its timestamp
        var files = Directory.GetFiles(_cacheDirectory, "*.mp3");
        File.SetLastWriteTimeUtc(files[0], DateTime.UtcNow.AddDays(-35));

        // Act
        await _cache.CleanupAsync(maxAgeDays: 30);

        // Assert
        var remainingFiles = Directory.GetFiles(_cacheDirectory, "*.mp3");
        remainingFiles.Should().HaveCount(1); // Only the new file should remain
    }

    [Fact]
    public async Task SetAsync_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var newCacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var newCache = new AudioCache(newCacheDir, _logger);

        // Act
        await newCache.SetAsync("test", "voice-1", new byte[] { 1, 2, 3 });

        // Assert
        Directory.Exists(newCacheDir).Should().BeTrue();

        // Cleanup
        Directory.Delete(newCacheDir, recursive: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }
}
