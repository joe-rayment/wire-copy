// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Audio;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// End-to-end integration tests for the podcast pipeline.
/// These tests require external dependencies (OpenAI API key, FFmpeg, GCS credentials)
/// as well as the full browser stack for content extraction.
/// All tests are unconditionally skipped for CI; run manually with credentials configured.
/// </summary>
[Trait("Category", "Integration")]
public class PodcastPipelineIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public PodcastPipelineIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"podcast-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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
            // Best-effort cleanup
        }
    }

    [Fact(Skip = "Requires OpenAI API key, FFmpeg, GCS credentials, and browser stack")]
    public void PodcastServices_CanBeResolved_WithFullConfiguration()
    {
        var services = BuildServiceProvider();

        var tts = services.GetService<ITtsService>();
        var assembler = services.GetService<IAudioAssembler>();
        var publisher = services.GetService<IPodcastPublisher>();
        var orchestrator = services.GetService<IPodcastOrchestrator>();

        tts.Should().NotBeNull();
        assembler.Should().NotBeNull();
        publisher.Should().NotBeNull();
        orchestrator.Should().NotBeNull();
    }

    [Fact(Skip = "Requires OpenAI API key, FFmpeg, GCS credentials, and browser stack")]
    public void TtsService_WithApiKey_IsConfigured()
    {
        var services = BuildServiceProvider();
        var tts = services.GetRequiredService<ITtsService>();

        // Only passes when OPENAI_API_KEY or config key is set
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
        {
            tts.IsConfigured.Should().BeTrue();
        }
    }

    [Fact(Skip = "Requires OpenAI API key, FFmpeg, GCS credentials, and browser stack")]
    public async Task AudioAssembler_ValidatePrerequisites_ReturnsTrueWhenFfmpegAvailable()
    {
        var services = BuildServiceProvider();
        var assembler = services.GetRequiredService<IAudioAssembler>();

        var result = await assembler.ValidatePrerequisitesAsync();

        // Only passes when FFmpeg is on PATH
        if (IsFfmpegAvailable())
        {
            result.Should().BeTrue();
        }
    }

    [Fact(Skip = "Requires OpenAI API key, FFmpeg, GCS credentials, and browser stack")]
    public async Task Orchestrator_EmptyCollection_ReturnsNoArticlesError()
    {
        var services = BuildServiceProvider();
        var orchestrator = services.GetRequiredService<IPodcastOrchestrator>();

        var collection = Domain.Entities.Collections.Collection.Create("Empty Test");
        var progressUpdates = new List<PodcastProgress>();
        var progress = new Progress<PodcastProgress>(p => progressUpdates.Add(p));

        var result = await orchestrator.GeneratePodcastAsync(collection, progress);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No readable articles");
    }

    [Fact(Skip = "Requires OpenAI API key, FFmpeg, GCS credentials, and browser stack")]
    public async Task Orchestrator_WithoutApiKey_PreflightCheckFails()
    {
        var services = BuildServiceProviderWithoutApiKey();
        var orchestrator = services.GetRequiredService<IPodcastOrchestrator>();

        var collection = Domain.Entities.Collections.Collection.Create("Test");
        collection.AddItem("https://example.com", "Example");

        var result = await orchestrator.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("TTS service is not configured");
    }

    private IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAiTts:ApiKey"] = Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
                ["Gcs:BucketName"] = Environment.GetEnvironmentVariable("GCS_TEST_BUCKET"),
                ["Podcast:TempDirectory"] = _tempDir,
                ["Podcast:Title"] = "Integration Test Podcast",
                ["Podcast:Author"] = "Test Runner",
                ["Auth:BaseUrl"] = "https://www.example.com",
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddTerminalBrowser();
        services.AddPodcast();

        return services.BuildServiceProvider();
    }

    private IServiceProvider BuildServiceProviderWithoutApiKey()
    {
        var services = new ServiceCollection();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Podcast:TempDirectory"] = _tempDir,
                ["Podcast:Title"] = "Test",
                ["Podcast:Author"] = "Test",
                ["Auth:BaseUrl"] = "https://www.example.com",
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddTerminalBrowser();
        services.AddPodcast();

        return services.BuildServiceProvider();
    }

    private static bool IsFfmpegAvailable()
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
                process.WaitForExit(5000);
                return process.ExitCode == 0;
            }
        }
        catch
        {
            // Not available
        }

        return false;
    }
}
