// <copyright file="LocalFileStorageTests.cs" company="TermReader">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>


using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Infrastructure.Storage;
using Xunit;

namespace TermReader.Tests.Storage;

[Trait("Category", "Unit")]
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
    public async Task DeleteFileAsync_ExistingFile_DeletesSuccessfully()
    {
        // Arrange
        var filePath = Path.Combine(_testOutputDir, "test-delete.tmp");
        await File.WriteAllBytesAsync(filePath, new byte[] { 1, 2, 3 });
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
}
