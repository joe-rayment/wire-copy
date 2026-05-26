// Licensed under the MIT License. See LICENSE in the repository root.

using ATL;
using FFMpegCore;
using FFMpegCore.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Audio;
using WireCopy.Application.Interfaces.Audio;
using WireCopy.Domain.ValueObjects.Audio;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Assembles multiple audio segments into a single M4B file with Nero chapter markers
/// and embedded metadata using FFMpegCore and ATL.NET.
/// </summary>
#pragma warning disable S101 // "M4b" is the audio file format name
internal sealed class M4bAudioAssembler : IAudioAssembler
#pragma warning restore S101
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
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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
        IProgress<AssemblyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Segments.Count == 0)
        {
            return AssemblyResult.Failure("No audio segments provided.");
        }

        // Validate that all segment files exist
        var missingSegment = request.Segments.FirstOrDefault(s => !File.Exists(s.AudioFilePath));
        if (missingSegment != null)
        {
            return AssemblyResult.Failure($"Audio file not found: {missingSegment.AudioFilePath}");
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
                    var analysis = await FFProbe.AnalyseAsync(segment.AudioFilePath, cancellationToken: cancellationToken).ConfigureAwait(false);
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

                // workspace-74zy: tick after each segment is probed and queued so
                // the orchestrator can show real "N of M segments" copy instead
                // of a frozen percent during the otherwise silent probe phase.
                progress?.Report(new AssemblyProgress
                {
                    TotalSegments = request.Segments.Count,
                    CompletedSegments = i + 1,
                    FfmpegPercent = 0,
                    Message = $"Probed {i + 1} of {request.Segments.Count} segments",
                });
            }

            // Phase C: Concatenate all segments into single M4B
            var outputDir = Path.GetDirectoryName(request.OutputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                try
                {
                    Directory.CreateDirectory(outputDir);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    return AssemblyResult.Failure(
                        $"Cannot create output directory '{outputDir}': {ex.Message}");
                }
            }

            _logger.LogInformation("Concatenating {Count} segments into M4B...", segmentPaths.Count);

            // workspace-74zy: FFMpegCore's NotifyOnProgress fires with a 0..100
            // percent while concat runs. Forward it as a smooth in-segment
            // signal so the orchestrator can drive a real progress bar during
            // the assembly phase instead of holding at "70%" for minutes.
            var concatArgs = FFMpegArguments
                .FromDemuxConcatInput(segmentPaths)
                .OutputToFile(request.OutputPath, overwrite: true, options => options
                    .WithAudioCodec(_config.AudioCodec)
                    .WithAudioBitrate(ParseBitrate(_config.AudioBitrate))
                    .WithCustomArgument($"-ac {_config.AudioChannels} -ar {_config.SampleRate} -movflags +faststart"));

            if (progress is not null)
            {
                var total = request.Segments.Count;
                concatArgs = concatArgs.NotifyOnProgress(
                    percent => progress.Report(new AssemblyProgress
                    {
                        TotalSegments = total,
                        CompletedSegments = total,
                        FfmpegPercent = percent,
                        Message = $"Concatenating ({percent:F0}%)",
                    }),
                    runningTime > TimeSpan.Zero ? runningTime : TimeSpan.FromHours(1));
            }

            await concatArgs.ProcessAsynchronously(throwOnError: true).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // Phase D: Embed chapters via an ffmpeg ffmetadata post-pass.
            // workspace-2g70: ATL.NET's track.Chapters.Add → track.Save() flow
            // writes chapter atoms but with a broken tref → chap reference
            // (audio track points only at the chapter PICTURE sub-track, not
            // the TEXT sub-track), so Apple Podcasts can't find the titles.
            // ffmpeg's ffmetadata input produces a correctly-linked Quicktime
            // chapter text track. We run a second ffmpeg pass that copies the
            // audio stream verbatim and merges in the chapter metadata.
            var ffmpegChaptersWritten = false;
            if (chapters.Count > 0)
            {
                try
                {
                    await InjectChaptersWithFfmpegAsync(
                        request.OutputPath,
                        chapters,
                        cancellationToken).ConfigureAwait(false);
                    ffmpegChaptersWritten = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "ffmpeg chapter injection failed; falling back to ATL.NET Nero chapters");
                }
            }

            // Phase E: Write non-chapter metadata (title/artist/comment + cover
            // art) via ATL.NET. When ffmpeg already embedded chapters, pass
            // chapters=null so we don't write a duplicate Nero atom. When the
            // ffmpeg pass failed, fall back to ATL.NET Nero chapters — some
            // players (foobar2000, AntennaPod) still pick those up.
            _logger.LogInformation("Writing metadata...");
            List<AudioChapterMarker>? chaptersForAtl = null;
            if (!ffmpegChaptersWritten && _config.UseNeroChapters)
            {
                chaptersForAtl = chapters;
            }

            WriteChaptersAndMetadata(
                request.OutputPath,
                chaptersForAtl,
                request.Metadata);

            // Get final file info
            var fileInfo = new FileInfo(request.OutputPath);

            // workspace-mie2: a 0-byte or missing output here means FFmpeg
            // silently produced an invalid file — fail loudly so the
            // publisher doesn't try to upload (and ultimately surface)
            // a dead episode.
            if (!fileInfo.Exists)
            {
                return AssemblyResult.Failure(
                    $"Assembly reported success but the output file is missing: {request.OutputPath}");
            }

            if (fileInfo.Length == 0)
            {
                return AssemblyResult.Failure(
                    $"Assembly reported success but the output file is zero bytes: {request.OutputPath}");
            }

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

        // Embed cover art if provided (with size limit and TOCTOU-safe read)
        if (!string.IsNullOrEmpty(metadata.CoverArtPath))
        {
            try
            {
                const long maxCoverArtBytes = 10 * 1024 * 1024; // 10 MB
                var coverInfo = new FileInfo(metadata.CoverArtPath);
                if (coverInfo.Exists && coverInfo.Length <= maxCoverArtBytes)
                {
                    var pictureData = File.ReadAllBytes(metadata.CoverArtPath);
                    track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(
                        pictureData,
                        PictureInfo.PIC_TYPE.Front,
                        ATL.AudioData.MetaDataIOFactory.TagType.NATIVE));
                }
            }
            catch (Exception)
            {
                // Cover art is optional — skip on any IO error
            }
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; the temp file will rot harmlessly otherwise.
        }
    }

    /// <summary>
    /// Builds an ffmpeg ffmetadata document containing one [CHAPTER] block
    /// per <see cref="AudioChapterMarker"/>. Titles are escaped per the
    /// ffmetadata grammar (backslash before <c>=</c>, <c>;</c>, <c>#</c>,
    /// backslash, and newline). Timebase is fixed at 1/1000 so the START
    /// and END values can be expressed as millisecond integers.
    /// </summary>
    private static string BuildFfmetadataChapters(IReadOnlyList<AudioChapterMarker> chapters)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(";FFMETADATA1\n");
        foreach (var c in chapters)
        {
            var startMs = (long)Math.Max(0, c.StartTime.TotalMilliseconds);
            var endMs = c.EndTime.HasValue
                ? (long)Math.Max(startMs + 1, c.EndTime.Value.TotalMilliseconds)
                : startMs + 1000;
            sb.Append("\n[CHAPTER]\n");
            sb.Append("TIMEBASE=1/1000\n");
            sb.Append("START=").Append(startMs).Append('\n');
            sb.Append("END=").Append(endMs).Append('\n');
            sb.Append("title=").Append(EscapeFfmetadata(c.Title)).Append('\n');
        }

        return sb.ToString();
    }

    private static string EscapeFfmetadata(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                case '=':
                case ';':
                case '#':
                    sb.Append('\\').Append(ch);
                    break;
                case '\n':
                    sb.Append("\\\n");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// workspace-2g70: write an ffmetadata chapters file next to the m4b and
    /// run an ffmpeg post-pass that maps the audio stream verbatim and
    /// merges the chapter metadata in. ffmpeg writes a Quicktime chapter
    /// text track with the audio track's <c>tref → chap</c> reference
    /// pointing at the text track — the structurally-correct layout that
    /// Apple Podcasts / Overcast / Pocket Casts all read.
    /// </summary>
    private async Task InjectChaptersWithFfmpegAsync(
        string outputPath,
        IReadOnlyList<AudioChapterMarker> chapters,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.GetDirectoryName(outputPath) ?? Path.GetTempPath();
        var metadataPath = Path.Combine(tempDir, $".chapters-{Guid.NewGuid():N}.ffm");
        var withChaptersPath = Path.Combine(tempDir, $".m4b-chap-{Guid.NewGuid():N}.m4a");

        try
        {
            await File.WriteAllTextAsync(
                metadataPath,
                BuildFfmetadataChapters(chapters),
                cancellationToken).ConfigureAwait(false);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(outputPath);
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(metadataPath);
            psi.ArgumentList.Add("-map_metadata");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:a");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
            psi.ArgumentList.Add(withChaptersPath);

            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("ffmpeg process failed to start");
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"ffmpeg chapter-injection pass failed (exit {process.ExitCode}): {stderr}");
            }

            // Atomic replace: overwrite the original m4b with the chaptered copy.
            File.Move(withChaptersPath, outputPath, overwrite: true);
            _logger.LogInformation("Embedded {Count} chapters via ffmpeg ffmetadata", chapters.Count);
        }
        finally
        {
            TryDelete(metadataPath);
            TryDelete(withChaptersPath);
        }
    }
}
