// <copyright file="InterfaceIntegrationTests.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Application.Interfaces;
using TermReader.Domain.Entities;
using TermReader.Infrastructure.Audio;
using Xunit;

namespace TermReader.Tests;

/// <summary>
/// Integration tests demonstrating proper usage of Phase 1 interfaces together
/// Shows how interfaces enable loose coupling and easy testing
/// </summary>
public class InterfaceIntegrationTests
{
    [Fact]
    public async Task AudioGeneration_WithBudgetTracking_WorksTogether()
    {
        // Arrange - Create mocks using interfaces
        var mockAudioGenerator = Substitute.For<IAudioGenerator>();
        var mockBudgetService = Substitute.For<IBudgetService>();
        var mockRateLimiter = Substitute.For<IRateLimiter>();

        // Setup mock behaviors
        mockBudgetService.CanAfford(Arg.Any<decimal>()).Returns(true);
        mockBudgetService.MaxBudget.Returns(10.0m);
        mockBudgetService.RemainingBudget.Returns(8.0m);

        mockAudioGenerator.EstimateCost(Arg.Any<string>()).Returns(0.50m);
        mockAudioGenerator.GenerateAudioAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3, 4 });

        mockRateLimiter.ExecuteAsync(Arg.Any<Func<Task<byte[]>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Func<Task<byte[]>>>()());

        // Create parallel generator using interfaces
        var logger = Substitute.For<ILogger<ParallelAudioGenerator>>();
        var parallelGenerator = new ParallelAudioGenerator(mockAudioGenerator, mockRateLimiter, logger);

        var article = new Article
        {
            Id = "article-1",
            Title = "Test",
            Content = "Test content",
            Url = "https://example.com",
            Author = "Test",
            ScrapedDate = DateTime.UtcNow
        };

        // Act - Check budget, then generate audio
        var estimatedCost = mockAudioGenerator.EstimateCost(article.Content);
        var canAfford = mockBudgetService.CanAfford(estimatedCost);
        canAfford.Should().BeTrue();

        var result = await parallelGenerator.GenerateAudioForArticlesAsync(
            new[] { article },
            "voice-id");

        mockBudgetService.RecordExpense(estimatedCost);

        // Assert
        result.AllSuccessful.Should().BeTrue();
        mockBudgetService.Received(1).RecordExpense(estimatedCost);
        await mockRateLimiter.Received(1).ExecuteAsync(Arg.Any<Func<Task<byte[]>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void InterfaceAbstraction_EnablesEasyMocking()
    {
        // Demonstrate that all Phase 1 interfaces can be easily mocked

        // Budget tracking
        var budgetService = Substitute.For<IBudgetService>();
        budgetService.CanAfford(Arg.Any<decimal>()).Returns(true);

        // Article parsing
        var articleParser = Substitute.For<IArticleParser>();
        articleParser.ParseArticle(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new Article
            {
                Id = "1",
                Title = "Mocked Article",
                Content = "Mocked content",
                Url = "https://example.com",
                Author = "Mock",
                ScrapedDate = DateTime.UtcNow
            });

        // Rate limiting
        var rateLimiter = Substitute.For<IRateLimiter>();
        rateLimiter.AvailableSlots.Returns(3);

        // Audio generation
        var audioGenerator = Substitute.For<IAudioGenerator>();
        audioGenerator.EstimateCost(Arg.Any<string>()).Returns(0.30m);

        // Parallel audio generation
        var parallelGenerator = Substitute.For<IParallelAudioGenerator>();
        parallelGenerator.EstimateTotalCost(Arg.Any<IEnumerable<Article>>()).Returns(1.50m);

        // All interfaces successfully mocked!
        budgetService.Should().NotBeNull();
        articleParser.Should().NotBeNull();
        rateLimiter.Should().NotBeNull();
        audioGenerator.Should().NotBeNull();
        parallelGenerator.Should().NotBeNull();
    }

    [Fact]
    public void BudgetService_AsInterface_SupportsDecoratorPattern()
    {
        // Demonstrate how interfaces enable decorator pattern

        var innerBudgetService = Substitute.For<IBudgetService>();
        innerBudgetService.MaxBudget.Returns(100m);
        innerBudgetService.TotalSpent.Returns(50m);
        innerBudgetService.RemainingBudget.Returns(50m);
        innerBudgetService.CanAfford(Arg.Any<decimal>()).Returns(true);

        // Could create logging decorator
        var loggingDecorator = new LoggingBudgetServiceDecorator(innerBudgetService);

        // Could create caching decorator
        var cachingDecorator = new CachingBudgetServiceDecorator(innerBudgetService);

        // Decorators implement same interface
        loggingDecorator.Should().BeAssignableTo<IBudgetService>();
        cachingDecorator.Should().BeAssignableTo<IBudgetService>();
    }

    [Fact]
    public async Task RateLimiter_AsInterface_SupportsTestingWithoutDelay()
    {
        // Demonstrate testing rate-limited operations without actual delays

        var mockRateLimiter = Substitute.For<IRateLimiter>();
        var operationCount = 0;

        // Setup to execute immediately without rate limiting delays
        mockRateLimiter.ExecuteAsync(Arg.Any<Func<Task<int>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Func<Task<int>>>()());

        // Simulate multiple operations
        for (int i = 0; i < 5; i++)
        {
            var result = await mockRateLimiter.ExecuteAsync(
                () =>
                {
                    operationCount++;
                    return Task.FromResult(operationCount);
                });
        }

        // Operations executed immediately without delays
        operationCount.Should().Be(5);
        await mockRateLimiter.Received(5).ExecuteAsync(Arg.Any<Func<Task<int>>>(), Arg.Any<CancellationToken>());
    }

    // Example decorator implementations to demonstrate pattern

    private class LoggingBudgetServiceDecorator : IBudgetService
    {
        private readonly IBudgetService _inner;

        public LoggingBudgetServiceDecorator(IBudgetService inner)
        {
            _inner = inner;
        }

        public decimal MaxBudget
        {
            get => _inner.MaxBudget;
            set => _inner.MaxBudget = value;
        }

        public decimal TotalSpent => _inner.TotalSpent;
        public decimal RemainingBudget => _inner.RemainingBudget;

        public bool CanAfford(decimal estimatedCost)
        {
            Console.WriteLine($"[LOG] Checking if can afford: {estimatedCost:C}");
            return _inner.CanAfford(estimatedCost);
        }

        public void RecordExpense(decimal amount)
        {
            Console.WriteLine($"[LOG] Recording expense: {amount:C}");
            _inner.RecordExpense(amount);
        }

        public void Reset()
        {
            Console.WriteLine("[LOG] Resetting budget");
            _inner.Reset();
        }

        public BudgetSummary GetSummary()
        {
            Console.WriteLine("[LOG] Getting budget summary");
            return _inner.GetSummary();
        }
    }

    private class CachingBudgetServiceDecorator : IBudgetService
    {
        private readonly IBudgetService _inner;
        private BudgetSummary? _cachedSummary;

        public CachingBudgetServiceDecorator(IBudgetService inner)
        {
            _inner = inner;
        }

        public decimal MaxBudget
        {
            get => _inner.MaxBudget;
            set
            {
                _inner.MaxBudget = value;
                _cachedSummary = null; // Invalidate cache
            }
        }

        public decimal TotalSpent => _inner.TotalSpent;
        public decimal RemainingBudget => _inner.RemainingBudget;

        public bool CanAfford(decimal estimatedCost) => _inner.CanAfford(estimatedCost);

        public void RecordExpense(decimal amount)
        {
            _inner.RecordExpense(amount);
            _cachedSummary = null; // Invalidate cache
        }

        public void Reset()
        {
            _inner.Reset();
            _cachedSummary = null; // Invalidate cache
        }

        public BudgetSummary GetSummary()
        {
            if (_cachedSummary == null)
            {
                _cachedSummary = _inner.GetSummary();
            }
            return _cachedSummary;
        }
    }
}
