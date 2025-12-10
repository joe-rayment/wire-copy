// <copyright file="ArticleRepositoryTests.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NYTAudioScraper.Domain.Entities;
using NYTAudioScraper.Infrastructure.Persistence;
using NYTAudioScraper.Infrastructure.Persistence.Repositories;

namespace NYTAudioScraper.Tests;

public class ArticleRepositoryTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly ArticleRepository _repository;

    public ArticleRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);
        _repository = new ArticleRepository(_context);
    }

    [Fact]
    public async Task GetByUrlAsync_WithExistingUrl_ReturnsArticle()
    {
        // Arrange
        var article = new Article
        {
            Id = "test-1",
            Title = "Test Article",
            Url = "https://nytimes.com/test",
            Content = "Test content",
            PublishedDate = DateTime.UtcNow,
            ScrapedDate = DateTime.UtcNow
        };
        await _repository.AddAsync(article);
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetByUrlAsync("https://nytimes.com/test");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("test-1");
    }

    [Fact]
    public async Task GetByUrlAsync_WithNonExistentUrl_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByUrlAsync("https://nytimes.com/nonexistent");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetBySectionAsync_WithMatchingSection_ReturnsArticles()
    {
        // Arrange
        var articles = new[]
        {
            new Article
            {
                Id = "test-1",
                Title = "Business Article 1",
                Url = "https://nytimes.com/business/1",
                Content = "Content 1",
                Section = "Business",
                PublishedDate = DateTime.UtcNow,
                ScrapedDate = DateTime.UtcNow
            },
            new Article
            {
                Id = "test-2",
                Title = "Business Article 2",
                Url = "https://nytimes.com/business/2",
                Content = "Content 2",
                Section = "Business",
                PublishedDate = DateTime.UtcNow.AddDays(-1),
                ScrapedDate = DateTime.UtcNow
            },
            new Article
            {
                Id = "test-3",
                Title = "Sports Article",
                Url = "https://nytimes.com/sports/1",
                Content = "Content 3",
                Section = "Sports",
                PublishedDate = DateTime.UtcNow,
                ScrapedDate = DateTime.UtcNow
            }
        };

        foreach (var article in articles)
        {
            await _repository.AddAsync(article);
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetBySectionAsync("Business");

        // Assert
        result.Should().HaveCount(2);
        result.Should().AllSatisfy(a => a.Section.Should().Be("Business"));
    }

    [Fact]
    public async Task GetByPublishedDateRangeAsync_WithinRange_ReturnsArticles()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var articles = new[]
        {
            new Article
            {
                Id = "test-1",
                Title = "Recent Article",
                Url = "https://nytimes.com/recent",
                Content = "Content",
                PublishedDate = now,
                ScrapedDate = now
            },
            new Article
            {
                Id = "test-2",
                Title = "Old Article",
                Url = "https://nytimes.com/old",
                Content = "Content",
                PublishedDate = now.AddDays(-10),
                ScrapedDate = now
            }
        };

        foreach (var article in articles)
        {
            await _repository.AddAsync(article);
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetByPublishedDateRangeAsync(now.AddDays(-2), now.AddDays(1));

        // Assert
        result.Should().HaveCount(1);
        result.First().Id.Should().Be("test-1");
    }

    [Fact]
    public async Task ExistsByUrlAsync_WithExistingUrl_ReturnsTrue()
    {
        // Arrange
        var article = new Article
        {
            Id = "test-1",
            Title = "Test Article",
            Url = "https://nytimes.com/test",
            Content = "Content",
            PublishedDate = DateTime.UtcNow,
            ScrapedDate = DateTime.UtcNow
        };
        await _repository.AddAsync(article);
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.ExistsByUrlAsync("https://nytimes.com/test");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsByUrlAsync_WithNonExistentUrl_ReturnsFalse()
    {
        // Act
        var result = await _repository.ExistsByUrlAsync("https://nytimes.com/nonexistent");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetRecentlyScrapedAsync_ReturnsLatestArticles()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var articles = Enumerable.Range(1, 5).Select(i => new Article
        {
            Id = $"test-{i}",
            Title = $"Article {i}",
            Url = $"https://nytimes.com/article-{i}",
            Content = "Content",
            PublishedDate = now,
            ScrapedDate = now.AddMinutes(-i)
        }).ToArray();

        foreach (var article in articles)
        {
            await _repository.AddAsync(article);
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetRecentlyScrapedAsync(3);

        // Assert
        result.Should().HaveCount(3);
        result.First().Id.Should().Be("test-1"); // Most recent
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }
}
