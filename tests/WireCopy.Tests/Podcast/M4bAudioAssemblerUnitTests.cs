// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Audio;
using WireCopy.Domain.ValueObjects.Audio;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Unit tests for M4bAudioAssembler validation and error paths.
/// These complement the always-skipped integration tests that require FFmpeg.
/// </summary>
[Trait("Category", "Unit")]
public class M4bAudioAssemblerUnitTests : IDisposable
{
    private readonly M4bAudioAssembler _sut;
    private readonly string _tempDir;

    public M4bAudioAssemblerUnitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"m4b-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = Options.Create(new PodcastConfiguration
        {
            TempDirectory = _tempDir,
        });
        _sut = new M4bAudioAssembler(config, NullLogger<M4bAudioAssembler>.Instance);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best-effort cleanup */ }
    }

    private static AudioMetadata CreateMetadata() => new()
    {
        Title = "Test Podcast Episode",
    };

    [Fact]
    public async Task AssembleAsync_NullRequest_ThrowsArgumentNull()
    {
        var act = () => _sut.AssembleAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AssembleAsync_EmptySegments_ReturnsFailure()
    {
        var request = new AssemblyRequest
        {
            Segments = new List<ArticleAudioSegment>(),
            Metadata = CreateMetadata(),
            OutputPath = Path.Combine(_tempDir, "output.m4b"),
        };

        var result = await _sut.AssembleAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No audio segments");
    }

    [Fact]
    public async Task AssembleAsync_MissingSegmentFile_ReturnsFailure()
    {
        var request = new AssemblyRequest
        {
            Segments = new List<ArticleAudioSegment>
            {
                new()
                {
                    Title = "Chapter 1",
                    AudioFilePath = Path.Combine(_tempDir, "nonexistent.aac"),
                    Duration = TimeSpan.FromMinutes(5),
                },
            },
            Metadata = CreateMetadata(),
            OutputPath = Path.Combine(_tempDir, "output.m4b"),
        };

        var result = await _sut.AssembleAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Audio file not found");
        result.ErrorMessage.Should().Contain("nonexistent.aac");
    }

    [Fact]
    public async Task AssembleAsync_SecondSegmentMissing_ReportsCorrectFile()
    {
        // First segment exists, second doesn't
        var existingFile = Path.Combine(_tempDir, "chapter1.aac");
        await File.WriteAllBytesAsync(existingFile, new byte[] { 0xFF, 0xF1, 0x00 });

        var request = new AssemblyRequest
        {
            Segments = new List<ArticleAudioSegment>
            {
                new()
                {
                    Title = "Chapter 1",
                    AudioFilePath = existingFile,
                    Duration = TimeSpan.FromMinutes(5),
                },
                new()
                {
                    Title = "Chapter 2",
                    AudioFilePath = Path.Combine(_tempDir, "missing.aac"),
                    Duration = TimeSpan.FromMinutes(5),
                },
            },
            Metadata = CreateMetadata(),
            OutputPath = Path.Combine(_tempDir, "output.m4b"),
        };

        var result = await _sut.AssembleAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("missing.aac",
            "error should identify the specific missing file, not just 'file not found'");
    }
}
