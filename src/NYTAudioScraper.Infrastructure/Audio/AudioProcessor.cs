// Educational and personal use only.

using FFMpegCore;
using FFMpegCore.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.ValueObjects;
using NYTAudioScraper.Infrastructure.Configuration;

namespace NYTAudioScraper.Infrastructure.Audio;

public class AudioProcessor : IAudioProcessor
{
    private readonly AudioConfiguration _config;
    private readonly ILogger<AudioProcessor> _logger;

    public AudioProcessor(
        IOptions<AudioConfiguration> config,
        ILogger<AudioProcessor> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public async Task<string> CreateAudiobookAsync(
        IEnumerable<string> inputFiles,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var files = inputFiles.ToList();
            _logger.LogInformation("Creating audiobook from {Count} audio files to {OutputPath}", files.Count, outputPath);

            if (files.Count == 0)
            {
                throw new ArgumentException("No audio files provided", nameof(inputFiles));
            }

            // Create concat file list for FFmpeg
            var concatFilePath = Path.Combine(Path.GetTempPath(), $"concat_{Guid.NewGuid()}.txt");
            var concatLines = files.Select(f => $"file '{f.Replace("'", @"'\''")}'");
            await File.WriteAllLinesAsync(concatFilePath, concatLines, cancellationToken);

            try
            {
                // Use FFmpeg to concatenate and convert to M4B
                await FFMpegArguments
                    .FromFileInput(concatFilePath, false, options => options
                        .WithCustomArgument("-f concat")
                        .WithCustomArgument("-safe 0"))
                    .OutputToFile(outputPath, true, options => options
                        .WithAudioCodec(AudioCodec.Aac)
                        .WithAudioBitrate(_config.BitRate)
                        .WithAudioSamplingRate(_config.SampleRate)
                        .WithCustomArgument($"-ac {_config.Channels}")
                        .WithCustomArgument("-f mp4")
                        .WithCustomArgument("-movflags +faststart")
                        .WithCustomArgument("-filter:a loudnorm=I=-16:TP=-1.5:LRA=11"))
                    .ProcessAsynchronously();

                _logger.LogInformation("Successfully created audiobook: {OutputPath}", outputPath);
                return outputPath;
            }
            finally
            {
                if (File.Exists(concatFilePath))
                {
                    File.Delete(concatFilePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating audiobook");
            throw;
        }
    }

    public async Task<AudioMetadata> GetMetadataAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Getting metadata for {FilePath}", filePath);

            var mediaInfo = await FFProbe.AnalyseAsync(filePath, cancellationToken: cancellationToken);
            var fileInfo = new FileInfo(filePath);

            var durationMs = (int)mediaInfo.Duration.TotalMilliseconds;
            var bitRate = (int)(mediaInfo.PrimaryAudioStream?.BitRate ?? 0);
            var sampleRate = mediaInfo.PrimaryAudioStream?.SampleRateHz ?? 0;
            var channels = mediaInfo.PrimaryAudioStream?.Channels ?? 0;
            var codec = mediaInfo.PrimaryAudioStream?.CodecName ?? "unknown";
            var fileSizeBytes = fileInfo.Length;

            var metadata = new AudioMetadata(
                Codec: codec,
                BitRate: bitRate,
                SampleRate: sampleRate,
                Channels: channels,
                DurationMs: durationMs,
                FileSizeBytes: fileSizeBytes);

            _logger.LogInformation(
                "Retrieved metadata: Duration={Duration}s, Codec={Codec}",
                metadata.DurationSeconds,
                metadata.Codec);

            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting metadata for {FilePath}", filePath);
            return AudioMetadata.Default;
        }
    }
}
