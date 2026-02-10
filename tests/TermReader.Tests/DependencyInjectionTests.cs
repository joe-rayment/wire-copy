// <copyright file="DependencyInjectionTests.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure;
using Xunit;

namespace TermReader.Tests;

public class DependencyInjectionTests
{
    private readonly IServiceProvider _serviceProvider;

    public DependencyInjectionTests()
    {
        var services = new ServiceCollection();
        var configuration = GetConfiguration();

        // Register services
        services.AddInfrastructure(configuration);
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveIScraperService()
    {
        // Act
        var service = _serviceProvider.GetService<IScraperService>();

        // Assert
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<IScraperService>();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveIAudioGenerator()
    {
        // Act
        var service = _serviceProvider.GetService<IAudioGenerator>();

        // Assert
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<IAudioGenerator>();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveIAudioProcessor()
    {
        // Act
        var service = _serviceProvider.GetService<IAudioProcessor>();

        // Assert
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<IAudioProcessor>();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveIChapterMarker()
    {
        // Act
        var service = _serviceProvider.GetService<IChapterMarker>();

        // Assert
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<IChapterMarker>();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveIFileStorage()
    {
        // Act
        var service = _serviceProvider.GetService<IFileStorage>();

        // Assert
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<IFileStorage>();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveAllServicesSuccessfully()
    {
        // Act
        var scraperService = _serviceProvider.GetRequiredService<IScraperService>();
        var audioGenerator = _serviceProvider.GetRequiredService<IAudioGenerator>();
        var audioProcessor = _serviceProvider.GetRequiredService<IAudioProcessor>();
        var chapterMarker = _serviceProvider.GetRequiredService<IChapterMarker>();
        var fileStorage = _serviceProvider.GetRequiredService<IFileStorage>();

        // Assert
        scraperService.Should().NotBeNull();
        audioGenerator.Should().NotBeNull();
        audioProcessor.Should().NotBeNull();
        chapterMarker.Should().NotBeNull();
        fileStorage.Should().NotBeNull();
    }

    private static IConfiguration GetConfiguration()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Auth:Email"] = "test@example.com",
            ["Auth:Password"] = "testpassword",
            ["Auth:BaseUrl"] = "https://www.nytimes.com",
            ["Auth:MaxArticles"] = "10",
            ["Auth:RateLimitDelayMs"] = "3000",
            ["ElevenLabs:ApiKey"] = "test-api-key",
            ["ElevenLabs:BaseUrl"] = "https://api.elevenlabs.io/v1",
            ["Audio:OutputFormat"] = "m4b",
            ["Audio:Codec"] = "aac",
            ["Audio:BitRate"] = "64000",
            ["Audio:SampleRate"] = "44100",
            ["Audio:Channels"] = "1",
            ["Audio:OutputDirectory"] = "output"
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();
    }
}
