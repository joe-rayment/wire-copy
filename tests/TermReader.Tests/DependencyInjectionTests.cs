// <copyright file="DependencyInjectionTests.cs" company="TermReader">
// Educational and personal use only.
// </copyright>

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Infrastructure.Bookmarks;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Collections;
using TermReader.Persistence;
using Xunit;

namespace TermReader.Tests;

public class DependencyInjectionTests
{
    private readonly IServiceProvider _serviceProvider;

    public DependencyInjectionTests()
    {
        var services = new ServiceCollection();
        var configuration = GetConfiguration();

        // Register configuration as a service (required by AddTerminalBrowser)
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();

        // Register terminal browser services (modular DI, matching Program.cs)
        services.AddTerminalBrowser();
        services.AddPersistence();
        services.AddCollections();
        services.AddBookmarks();

        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveIBrowserService()
    {
        var service = _serviceProvider.GetService<IBrowserService>();
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<IBrowserService>();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveIPageLoader()
    {
        var service = _serviceProvider.GetService<IPageLoader>();
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<IPageLoader>();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveIPageRenderer()
    {
        var service = _serviceProvider.GetService<IPageRenderer>();
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<IPageRenderer>();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveIFileStorage()
    {
        var service = _serviceProvider.GetService<IFileStorage>();
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<IFileStorage>();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveINavigationService()
    {
        var service = _serviceProvider.GetService<INavigationService>();
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<INavigationService>();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveBrowserSession()
    {
        var service = _serviceProvider.GetService<IBrowserSession>();
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<IBrowserSession>();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveAllBrowserServices()
    {
        var browserService = _serviceProvider.GetRequiredService<IBrowserService>();
        var pageLoader = _serviceProvider.GetRequiredService<IPageLoader>();
        var pageRenderer = _serviceProvider.GetRequiredService<IPageRenderer>();
        var navigationService = _serviceProvider.GetRequiredService<INavigationService>();
        var fileStorage = _serviceProvider.GetRequiredService<IFileStorage>();

        browserService.Should().NotBeNull();
        pageLoader.Should().NotBeNull();
        pageRenderer.Should().NotBeNull();
        navigationService.Should().NotBeNull();
        fileStorage.Should().NotBeNull();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveScopedICollectionService()
    {
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetService<ICollectionService>();
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<ICollectionService>();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveScopedICollectionRepository()
    {
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetService<ICollectionRepository>();
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<ICollectionRepository>();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveICollectionExporters()
    {
        var exporters = _serviceProvider.GetServices<ICollectionExporter>().ToList();
        exporters.Should().HaveCountGreaterOrEqualTo(2);
        exporters.Select(e => e.Format).Should().Contain("urls");
        exporters.Select(e => e.Format).Should().Contain("opml");
    }

    [Fact]
    public void ServiceProvider_ShouldResolveILinkExtractor()
    {
        var service = _serviceProvider.GetService<ILinkExtractor>();
        service.Should().NotBeNull();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveINavigationTreeBuilder()
    {
        var service = _serviceProvider.GetService<INavigationTreeBuilder>();
        service.Should().NotBeNull();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveIReadableContentExtractor()
    {
        var service = _serviceProvider.GetService<IReadableContentExtractor>();
        service.Should().NotBeNull();
    }

    [Fact]
    public void ServiceProvider_ShouldResolveIInputHandler()
    {
        var service = _serviceProvider.GetService<IInputHandler>();
        service.Should().NotBeNull();
    }

    private static IConfiguration GetConfiguration()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Auth:BaseUrl"] = "https://www.nytimes.com",
            ["Auth:MaxArticles"] = "10",
            ["Auth:RateLimitDelayMs"] = "3000",
            ["Browser:BrowserType"] = "Chrome",
            ["Browser:Headless"] = "true",
            ["Browser:ImplicitWaitSeconds"] = "10",
            ["Browser:PageLoadTimeoutSeconds"] = "30",
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();
    }
}
