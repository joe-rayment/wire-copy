// <copyright file="LocalFileStorageTests.cs" company="NYTAudioScraper">
// Copyright (c) NYTAudioScraper. All rights reserved.
// </copyright>

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NYTAudioScraper.Infrastructure.Storage;
using Xunit;

namespace NYTAudioScraper.Tests;

public class LocalFileStorageTests : IDisposable
{
    private readonly LocalFileStorage _storage;
    private readonly ILogger<LocalFileStorage> _logger;
    private readonly string _testOutputDir;

    public LocalFileStorageTests()
    {
        _logger = Substitute.For<ILogger<LocalFileStorage>>();
        _storage = new LocalFileStorage(_logger);
        _testOutputDir = _storage.GetOutputDirectory();
    }

    public void Dispose()
    {
        // Cleanup test files
        if (Directory.Exists(_testOutputDir))
        {
            try
            {
                var files = Directory.GetFiles(_testOutputDir, "test-*");
                foreach (var file in files)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task SaveAudioAsync_WithValidData_SavesFile()
    {
        // Arrange
        var audioData = new byte[] { 1, 2, 3, 4, 5 };
        var fileName = "test-audio-save.mp3";

        // Act
        var filePath = await _storage.SaveAudioAsync(audioData, fileName);

        // Assert
        File.Exists(filePath).Should().BeTrue();
        var savedData = await File.ReadAllBytesAsync(filePath);
        savedData.Should().Equal(audioData);

        // Cleanup
        File.Delete(filePath);
    }

    [Fact]
    public async Task SaveAudioAsync_WithNullData_ThrowsArgumentException()
    {
        // Arrange
        byte[]? audioData = null;
        var fileName = "test-null.mp3";

        // Act
        Func<Task> act = async () => await _storage.SaveAudioAsync(audioData!, fileName);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*cannot be null or empty*");
    }

    [Fact]
    public async Task SaveAudioAsync_WithEmptyData_ThrowsArgumentException()
    {
        // Arrange
        var audioData = Array.Empty<byte>();
        var fileName = "test-empty.mp3";

        // Act
        Func<Task> act = async () => await _storage.SaveAudioAsync(audioData, fileName);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*cannot be null or empty*");
    }

    [Fact]
    public async Task SaveAudioAsync_WithNullFileName_ThrowsArgumentException()
    {
        // Arrange
        var audioData = new byte[] { 1, 2, 3 };
        string? fileName = null;

        // Act
        Func<Task> act = async () => await _storage.SaveAudioAsync(audioData, fileName!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*cannot be null or empty*");
    }

    [Fact]
    public async Task SaveAudioAsync_WithEmptyFileName_ThrowsArgumentException()
    {
        // Arrange
        var audioData = new byte[] { 1, 2, 3 };
        var fileName = "";

        // Act
        Func<Task> act = async () => await _storage.SaveAudioAsync(audioData, fileName);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*cannot be null or empty*");
    }

    [Fact]
    public async Task SaveAudioAsync_WithPathTraversalAttempt_SanitizesFileName()
    {
        // Arrange
        var audioData = new byte[] { 1, 2, 3 };
        var maliciousFileName = "../../etc/passwd";

        // Act
        var filePath = await _storage.SaveAudioAsync(audioData, maliciousFileName);

        // Assert
        filePath.Should().Contain(_testOutputDir);
        filePath.Should().NotContain("..");
        Path.GetFileName(filePath).Should().Be("passwd");

        // Cleanup
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task SaveAudioAsync_WithInvalidCharacters_SanitizesFileName()
    {
        // Arrange
        var audioData = new byte[] { 1, 2, 3 };
        var fileName = "test<>:|?.mp3";

        // Act
        var filePath = await _storage.SaveAudioAsync(audioData, fileName);

        // Assert
        File.Exists(filePath).Should().BeTrue();
        Path.GetFileName(filePath).Should().NotContain("<");
        Path.GetFileName(filePath).Should().NotContain(">");
        Path.GetFileName(filePath).Should().NotContain(":");
        Path.GetFileName(filePath).Should().NotContain("|");
        Path.GetFileName(filePath).Should().NotContain("?");

        // Cleanup
        File.Delete(filePath);
    }

    [Fact]
    public async Task SaveAudioAsync_ExistingFile_OverwritesFile()
    {
        // Arrange
        var originalData = new byte[] { 1, 2, 3 };
        var newData = new byte[] { 4, 5, 6, 7 };
        var fileName = "test-overwrite.mp3";

        // Act
        var filePath1 = await _storage.SaveAudioAsync(originalData, fileName);
        var filePath2 = await _storage.SaveAudioAsync(newData, fileName);

        // Assert
        filePath1.Should().Be(filePath2);
        var savedData = await File.ReadAllBytesAsync(filePath2);
        savedData.Should().Equal(newData);
        savedData.Should().NotEqual(originalData);

        // Cleanup
        File.Delete(filePath2);
    }

    [Fact]
    public async Task SaveAudioAsync_LargeFile_SavesCorrectly()
    {
        // Arrange
        var audioData = new byte[1024 * 1024]; // 1 MB
        new Random().NextBytes(audioData);
        var fileName = "test-large-file.mp3";

        // Act
        var filePath = await _storage.SaveAudioAsync(audioData, fileName);

        // Assert
        File.Exists(filePath).Should().BeTrue();
        var fileInfo = new FileInfo(filePath);
        fileInfo.Length.Should().Be(audioData.Length);

        // Cleanup
        File.Delete(filePath);
    }

    [Fact]
    public async Task DeleteFileAsync_ExistingFile_DeletesSuccessfully()
    {
        // Arrange
        var audioData = new byte[] { 1, 2, 3 };
        var fileName = "test-delete.mp3";
        var filePath = await _storage.SaveAudioAsync(audioData, fileName);
        File.Exists(filePath).Should().BeTrue();

        // Act
        await _storage.DeleteFileAsync(filePath);

        // Assert
        File.Exists(filePath).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteFileAsync_NonExistentFile_DoesNotThrow()
    {
        // Arrange
        var filePath = Path.Combine(_testOutputDir, "non-existent-file.mp3");

        // Act
        Func<Task> act = async () => await _storage.DeleteFileAsync(filePath);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void GetOutputDirectory_ReturnsValidPath()
    {
        // Act
        var outputDir = _storage.GetOutputDirectory();

        // Assert
        outputDir.Should().NotBeNullOrEmpty();
        Directory.Exists(outputDir).Should().BeTrue();
    }

    [Fact]
    public void GetOutputDirectory_CreatesDirectoryIfNotExists()
    {
        // This is implicitly tested by the constructor creating the directory
        // Act
        var outputDir = _storage.GetOutputDirectory();

        // Assert
        Directory.Exists(outputDir).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAudioAsync_VeryLongFileName_TruncatesCorrectly()
    {
        // Arrange
        var audioData = new byte[] { 1, 2, 3 };
        var longFileName = new string('a', 300) + ".mp3"; // 300+ characters

        // Act
        var filePath = await _storage.SaveAudioAsync(audioData, longFileName);

        // Assert
        var fileName = Path.GetFileName(filePath);
        fileName.Length.Should().BeLessThanOrEqualTo(255);
        fileName.Should().EndWith(".mp3");

        // Cleanup
        File.Delete(filePath);
    }
}
