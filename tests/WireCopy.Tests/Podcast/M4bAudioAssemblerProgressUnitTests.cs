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
/// Unit-level coverage for the per-segment progress tick in
/// <see cref="M4bAudioAssembler.AssembleAsync"/> (workspace-74zy QA follow-up).
/// Runs without FFmpeg: every segment is given a non-zero <c>Duration</c> so
/// the probe phase is skipped, then the assembler calls into FFMpegCore for
/// the concat (which fails fast with a missing-binary error if FFmpeg is
/// absent). The progress ticks for the per-segment phase MUST already have
/// fired by the time the concat attempt happens, so we observe them
/// regardless of FFmpeg's presence.
/// </summary>
[Trait("Category", "Unit")]
public class M4bAudioAssemblerProgressUnitTests : IDisposable
{
    private readonly string _tempDir;
    private readonly M4bAudioAssembler _assembler;

    public M4bAudioAssemblerProgressUnitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"m4b-progress-unit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = Options.Create(new PodcastConfiguration { TempDirectory = _tempDir });
        _assembler = new M4bAudioAssembler(config, NullLogger<M4bAudioAssembler>.Instance);
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
            // best-effort
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AssembleAsync_TicksPerSegmentProgress_EvenWhenConcatFails()
    {
        // Three segments with pre-set Duration so the probe phase is skipped.
        // Write minimal placeholder files so the existence check passes.
        var segments = new List<ArticleAudioSegment>();
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(_tempDir, $"seg-{i}.aac");
            await File.WriteAllBytesAsync(path, [0xFF, 0xF1]);
            segments.Add(new ArticleAudioSegment
            {
                Title = $"Chapter {i + 1}",
                AudioFilePath = path,
                Duration = TimeSpan.FromSeconds(1),
                SourceUrl = $"https://example.com/{i}",
            });
        }

        var request = new AssemblyRequest
        {
            Segments = segments,
            OutputPath = Path.Combine(_tempDir, "out.m4b"),
            Metadata = new AudioMetadata
            {
                Title = "Test",
                Author = "A",
                Description = "d",
                Genre = "Podcast",
            },
            CleanupTemporaryFiles = false,
        };

        var events = new List<AssemblyProgress>();
        var sink = new Progress<AssemblyProgress>(p => events.Add(p));

        try
        {
            await _assembler.AssembleAsync(request, sink);
        }
        catch
        {
            // FFmpeg concat may fail on the 2-byte placeholder audio. We don't
            // care — the per-segment tick fires BEFORE concat, so the assertion
            // below is meaningful either way.
        }

        // After Progress<T>'s async Post, give the captured callbacks a moment
        // to drain into the list. Two yields is sufficient since the callback
        // is a simple list-add.
        await Task.Yield();
        await Task.Yield();

        events.Should().NotBeEmpty("the per-segment probe loop must tick the progress sink even when concat ultimately fails");
        events.Select(e => e.CompletedSegments).Max().Should().Be(3,
            "the final per-segment tick reports CompletedSegments == TotalSegments");
        events.Where(e => e.FfmpegPercent == 0).Select(e => e.TotalSegments).Should().AllBeEquivalentTo(3);
    }
}
