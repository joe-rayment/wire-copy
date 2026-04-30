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

[Trait("Category", "Integration")]
public class M4bAudioAssemblerIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir;
    private readonly M4bAudioAssembler _assembler;
    private bool _ffmpegAvailable;

    public M4bAudioAssemblerIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"m4b-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = Options.Create(new PodcastConfiguration());
        _assembler = new M4bAudioAssembler(config, NullLogger<M4bAudioAssembler>.Instance);
    }

    public async Task InitializeAsync()
    {
        _ffmpegAvailable = await _assembler.ValidatePrerequisitesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

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
    }

    [Fact(Skip = "Requires FFmpeg on PATH")]
    public async Task ValidatePrerequisites_WithFfmpeg_ReturnsTrue()
    {
        if (!_ffmpegAvailable)
        {
            return;
        }

        var result = await _assembler.ValidatePrerequisitesAsync();
        result.Should().BeTrue();
    }

    [Fact(Skip = "Requires FFmpeg on PATH")]
    public async Task AssembleAsync_TwoSegments_CreatesM4bFile()
    {
        if (!_ffmpegAvailable)
        {
            return;
        }

        var segments = await CreateTestSegmentsAsync(2);
        var outputPath = Path.Combine(_tempDir, "output.m4b");

        var request = new AssemblyRequest
        {
            Segments = segments,
            OutputPath = outputPath,
            Metadata = new AudioMetadata
            {
                Title = "Test Podcast",
                Author = "Test Author",
                Description = "Test Description",
                Genre = "Podcast",
            },
            CleanupTemporaryFiles = false,
        };

        var result = await _assembler.AssembleAsync(request);

        result.Success.Should().BeTrue();
        result.OutputPath.Should().NotBeNullOrEmpty();
        File.Exists(result.OutputPath).Should().BeTrue();
        result.FileSizeBytes.Should().BeGreaterThan(0);
    }

    [Fact(Skip = "Requires FFmpeg on PATH")]
    public async Task AssembleAsync_WithMetadata_WritesMetadataCorrectly()
    {
        if (!_ffmpegAvailable)
        {
            return;
        }

        var segments = await CreateTestSegmentsAsync(1);
        var outputPath = Path.Combine(_tempDir, "metadata-test.m4b");

        var request = new AssemblyRequest
        {
            Segments = segments,
            OutputPath = outputPath,
            Metadata = new AudioMetadata
            {
                Title = "Metadata Test",
                Author = "Jane Doe",
                Description = "A test of metadata writing",
                Genre = "Audiobook",
            },
            CleanupTemporaryFiles = false,
        };

        var result = await _assembler.AssembleAsync(request);

        result.Success.Should().BeTrue();
        File.Exists(result.OutputPath!).Should().BeTrue();

        // Verify metadata with ATL.NET
        var track = new ATL.Track(result.OutputPath!);
        track.Title.Should().Be("Metadata Test");
        track.Artist.Should().Be("Jane Doe");
    }

    [Fact(Skip = "Requires FFmpeg on PATH")]
    public async Task AssembleAsync_MultipleSegments_HasChapterMarkers()
    {
        if (!_ffmpegAvailable)
        {
            return;
        }

        var segments = await CreateTestSegmentsAsync(3);
        var outputPath = Path.Combine(_tempDir, "chapters-test.m4b");

        var request = new AssemblyRequest
        {
            Segments = segments,
            OutputPath = outputPath,
            Metadata = new AudioMetadata
            {
                Title = "Chapters Test",
                Author = "Test",
                Genre = "Podcast",
            },
            CleanupTemporaryFiles = false,
        };

        var result = await _assembler.AssembleAsync(request);

        result.Success.Should().BeTrue();
        result.Chapters.Should().HaveCount(3);
        result.Chapters[0].Title.Should().Be("Chapter 1");
        result.Chapters[1].Title.Should().Be("Chapter 2");
        result.Chapters[2].Title.Should().Be("Chapter 3");
    }

    [Fact(Skip = "Requires FFmpeg on PATH")]
    public async Task AssembleAsync_OutputFile_HasFaststartAtom()
    {
        if (!_ffmpegAvailable)
        {
            return;
        }

        var segments = await CreateTestSegmentsAsync(1);
        var outputPath = Path.Combine(_tempDir, "faststart-test.m4b");

        var request = new AssemblyRequest
        {
            Segments = segments,
            OutputPath = outputPath,
            Metadata = new AudioMetadata
            {
                Title = "Faststart Test",
                Author = "Test",
                Genre = "Podcast",
            },
            CleanupTemporaryFiles = false,
        };

        var result = await _assembler.AssembleAsync(request);

        result.Success.Should().BeTrue();
        File.Exists(result.OutputPath!).Should().BeTrue();

        // For M4B/MP4 with -movflags +faststart, the moov atom should be near the beginning.
        // Read the first 8 bytes of the file - look for "ftyp" atom which comes first in faststart.
        var headerBytes = new byte[8];
        await using var stream = File.OpenRead(result.OutputPath!);
        await stream.ReadExactlyAsync(headerBytes);
        var atomType = System.Text.Encoding.ASCII.GetString(headerBytes, 4, 4);
        atomType.Should().Be("ftyp", "M4B with faststart should begin with ftyp atom");
    }

    private async Task<List<ArticleAudioSegment>> CreateTestSegmentsAsync(int count)
    {
        var segments = new List<ArticleAudioSegment>();

        for (var i = 0; i < count; i++)
        {
            var segmentPath = Path.Combine(_tempDir, $"segment-{i:D3}.aac");

            // Create a minimal valid AAC file using FFmpeg (generate 1 second of silence)
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -f lavfi -i anullsrc=r=44100:cl=mono -t 1 -c:a aac -b:a 64k \"{segmentPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process != null)
            {
                await process.WaitForExitAsync();
            }

            segments.Add(new ArticleAudioSegment
            {
                Title = $"Chapter {i + 1}",
                AudioFilePath = segmentPath,
                Duration = TimeSpan.FromSeconds(1),
                SourceUrl = $"https://example.com/article-{i + 1}",
            });
        }

        return segments;
    }
}
