// Educational and personal use only.

using ATL;
using FFMpegCore;
using FFMpegCore.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Audio;
using TermReader.Application.Interfaces.Audio;
using TermReader.Domain.ValueObjects.Audio;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Assembles multiple audio segments into a single M4B file with Nero chapter markers
/// and embedded metadata using FFMpegCore and ATL.NET.
/// </summary>
internal sealed class M4bAudioAssembler : IAudioAssembler
{
    private readonly PodcastConfiguration _config;
    private readonly ILogger<M4bAudioAssembler> _logger;

    public M4bAudioAssembler(IOptions<PodcastConfiguration> config, ILogger<M4bAudioAssembler> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public async Task<bool> ValidatePrerequisitesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process != null)
            {
                await process.WaitForExitAsync(cancellationToken);
                return process.ExitCode == 0;
            }
        }
        catch
        {
            // FFmpeg is not installed
        }

        _logger.LogError("FFmpeg is not installed or not found in PATH");
        return false;
    }

    public async Task<AssemblyResult> AssembleAsync(
        AssemblyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Segments.Count == 0)
        {
            return AssemblyResult.Failure("No audio segments provided.");
        }

        // Validate that all segment files exist
        foreach (var segment in request.Segments)
        {
            if (!File.Exists(segment.AudioFilePath))
            {
                return AssemblyResult.Failure($"Audio file not found: {segment.AudioFilePath}");
            }
        }

        using var tempManager = new TempFileManager(_config.TempDirectory, _logger);

        try
        {
            _logger.LogInformation(
                "Starting M4B assembly: {SegmentCount} segments -> {OutputPath}",
                request.Segments.Count,
                request.OutputPath);

            // Phase A & B: Probe durations and compute chapter timestamps
            var chapters = new List<AudioChapterMarker>();
            var segmentPaths = new List<string>();
            var runningTime = TimeSpan.Zero;

            for (var i = 0; i < request.Segments.Count; i++)
            {
                var segment = request.Segments[i];

                // Use provided duration or probe it
                var duration = segment.Duration;
                if (duration == TimeSpan.Zero)
                {
                    var analysis = await FFProbe.AnalyseAsync(segment.AudioFilePath, cancellationToken: cancellationToken);
                    duration = analysis.Duration;
                }

                chapters.Add(new AudioChapterMarker
                {
                    Title = segment.Title,
                    StartTime = runningTime,
                    EndTime = runningTime + duration,
                    SourceUrl = segment.SourceUrl,
                });

                segmentPaths.Add(segment.AudioFilePath);
                runningTime += duration;

                _logger.LogDebug(
                    "Segment {Index}/{Total}: '{Title}' ({Duration:mm\\:ss})",
                    i + 1,
                    request.Segments.Count,
                    segment.Title,
                    duration);
            }

            // Phase C: Concatenate all segments into single M4B
            var outputDir = Path.GetDirectoryName(request.OutputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            _logger.LogInformation("Concatenating {Count} segments into M4B...", segmentPaths.Count);

            await FFMpegArguments
                .FromDemuxConcatInput(segmentPaths)
                .OutputToFile(request.OutputPath, overwrite: true, options => options
                    .WithAudioCodec(_config.AudioCodec)
                    .WithAudioBitrate(ParseBitrate(_config.AudioBitrate))
                    .WithCustomArgument($"-ac {_config.AudioChannels} -ar {_config.SampleRate} -movflags +faststart"))
                .ProcessAsynchronously(throwOnError: true);

            cancellationToken.ThrowIfCancellationRequested();

            // Phases D+E: Write Nero chapter markers and file metadata via ATL.NET
            _logger.LogInformation("Writing chapters and metadata...");
            WriteChaptersAndMetadata(
                request.OutputPath,
                _config.UseNeroChapters ? chapters : null,
                request.Metadata);

            // Get final file info
            var fileInfo = new FileInfo(request.OutputPath);

            _logger.LogInformation(
                "M4B assembly complete: {Duration:hh\\:mm\\:ss}, {Size} bytes, {Chapters} chapters",
                runningTime,
                fileInfo.Length,
                chapters.Count);

            // Phase F: Cleanup temp files
            if (request.CleanupTemporaryFiles)
            {
                tempManager.Cleanup();
            }

            return AssemblyResult.Successful(
                request.OutputPath,
                runningTime,
                fileInfo.Length,
                chapters);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "M4B assembly failed: {Message}", ex.Message);
            return AssemblyResult.Failure($"Assembly failed: {ex.Message}");
        }
    }

    private static void WriteChaptersAndMetadata(
        string filePath,
        List<AudioChapterMarker>? chapters,
        AudioMetadata metadata)
    {
        var track = new Track(filePath);

        // Write chapters
        if (chapters is { Count: > 0 })
        {
            track.Chapters.Clear();

            foreach (var chapter in chapters)
            {
                track.Chapters.Add(new ChapterInfo
                {
                    StartTime = (uint)chapter.StartTime.TotalMilliseconds,
                    EndTime = chapter.EndTime.HasValue
                        ? (uint)chapter.EndTime.Value.TotalMilliseconds
                        : 0u,
                    Title = chapter.Title,
                });
            }
        }

        // Write metadata
        track.Title = metadata.Title;

        if (!string.IsNullOrEmpty(metadata.Author))
        {
            track.Artist = metadata.Author;
        }

        if (!string.IsNullOrEmpty(metadata.Genre))
        {
            track.Genre = metadata.Genre;
        }

        if (!string.IsNullOrEmpty(metadata.Description))
        {
            track.Comment = metadata.Description;
        }

        if (metadata.PublishedDate.HasValue)
        {
            track.Year = metadata.PublishedDate.Value.Year;
        }

        // Embed cover art if provided
        if (!string.IsNullOrEmpty(metadata.CoverArtPath) && File.Exists(metadata.CoverArtPath))
        {
            var pictureData = File.ReadAllBytes(metadata.CoverArtPath);

            track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(
                pictureData,
                PictureInfo.PIC_TYPE.Front,
                ATL.AudioData.MetaDataIOFactory.TagType.NATIVE));
        }

        if (!track.Save())
        {
            throw new InvalidOperationException("Failed to save chapter markers and metadata to the M4B file.");
        }
    }

    private static int ParseBitrate(string bitrate)
    {
        // Parse "64k" -> 64, "128k" -> 128
        var numStr = bitrate.TrimEnd('k', 'K');
        return int.TryParse(numStr, out var value) ? value : 64;
    }

}
