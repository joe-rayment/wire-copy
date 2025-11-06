using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace NYTAudioScraper.Infrastructure.Health;

/// <summary>
/// Health check to verify FFmpeg is installed and accessible
/// </summary>
public class FFmpegHealthCheck : IHealthCheck
{
    private readonly ILogger<FFmpegHealthCheck> _logger;

    public FFmpegHealthCheck(ILogger<FFmpegHealthCheck> logger)
    {
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                var version = output.Split('\n')[0].Replace("ffmpeg version ", "");
                return HealthCheckResult.Healthy(
                    "FFmpeg is available",
                    new Dictionary<string, object>
                    {
                        ["version"] = version
                    });
            }

            return HealthCheckResult.Unhealthy("FFmpeg returned non-zero exit code");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFmpeg health check failed");
            return HealthCheckResult.Unhealthy(
                "FFmpeg is not installed or not in PATH. Install with: brew install ffmpeg (macOS) or apt-get install ffmpeg (Linux)",
                ex);
        }
    }
}
