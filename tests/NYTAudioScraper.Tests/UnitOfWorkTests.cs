// <copyright file="UnitOfWorkTests.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;
using NYTAudioScraper.Infrastructure.Persistence;
using Xunit;

namespace NYTAudioScraper.Tests;

/// <summary>
/// Tests for IUnitOfWork interface and UnitOfWork implementation
/// </summary>
public class UnitOfWorkTests : IAsyncDisposable
{
    private readonly AppDbContext _context;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<UnitOfWork> _logger;

    public UnitOfWorkTests()
    {
        // Create in-memory database for testing
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(options);
        _logger = Substitute.For<ILogger<UnitOfWork>>();
        _unitOfWork = new UnitOfWork(_context, _logger);
    }

    [Fact]
    public async Task SaveChangesAsync_WithPendingChanges_PersistsToDatabase()
    {
        // Arrange
        var article = CreateTestArticle("article-1", "Test Article");
        _context.Articles.Add(article);

        // Act
        var result = await _unitOfWork.SaveChangesAsync();

        // Assert
        result.Should().Be(1);
        var savedArticle = await _context.Articles.FindAsync("article-1");
        savedArticle.Should().NotBeNull();
        savedArticle!.Title.Should().Be("Test Article");
    }

    [Fact]
    public async Task SaveChangesAsync_WithNoChanges_ReturnsZero()
    {
        // Act
        var result = await _unitOfWork.SaveChangesAsync();

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public async Task SaveChangesAsync_WithMultipleChanges_PersistsAll()
    {
        // Arrange
        var article1 = CreateTestArticle("article-1", "Article 1");
        var article2 = CreateTestArticle("article-2", "Article 2");
        _context.Articles.AddRange(article1, article2);

        // Act
        var result = await _unitOfWork.SaveChangesAsync();

        // Assert
        result.Should().Be(2);
        _context.Articles.Should().HaveCount(2);
    }

    [Fact]
    public async Task BeginTransactionAsync_StartsNewTransaction()
    {
        // Act
        await _unitOfWork.BeginTransactionAsync();

        // Assert
        _context.Database.CurrentTransaction.Should().NotBeNull();
    }

    [Fact]
    public async Task BeginTransactionAsync_WhenTransactionInProgress_ThrowsException()
    {
        // Arrange
        await _unitOfWork.BeginTransactionAsync();

        // Act
        Func<Task> act = async () => await _unitOfWork.BeginTransactionAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A transaction is already in progress");
    }

    [Fact]
    public async Task CommitTransactionAsync_PersistsChangesAndCommits()
    {
        // Arrange
        await _unitOfWork.BeginTransactionAsync();
        var article = CreateTestArticle("article-1", "Test Article");
        _context.Articles.Add(article);

        // Act
        await _unitOfWork.CommitTransactionAsync();

        // Assert
        var savedArticle = await _context.Articles.FindAsync("article-1");
        savedArticle.Should().NotBeNull();
        _context.Database.CurrentTransaction.Should().BeNull(); // Transaction disposed after commit
    }

    [Fact]
    public async Task CommitTransactionAsync_WithoutTransaction_ThrowsException()
    {
        // Act
        Func<Task> act = async () => await _unitOfWork.CommitTransactionAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No transaction in progress");
    }

    [Fact]
    public async Task RollbackTransactionAsync_DiscardsChanges()
    {
        // Arrange
        await _unitOfWork.BeginTransactionAsync();
        var article = CreateTestArticle("article-1", "Test Article");
        _context.Articles.Add(article);

        // Act
        await _unitOfWork.RollbackTransactionAsync();

        // Assert
        var savedArticle = await _context.Articles.FindAsync("article-1");
        savedArticle.Should().BeNull(); // Changes were rolled back
        _context.Database.CurrentTransaction.Should().BeNull(); // Transaction disposed after rollback
    }

    [Fact]
    public async Task RollbackTransactionAsync_WithoutTransaction_ThrowsException()
    {
        // Act
        Func<Task> act = async () => await _unitOfWork.RollbackTransactionAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No transaction in progress");
    }

    [Fact]
    public async Task CommitTransactionAsync_OnError_RollsBackAutomatically()
    {
        // Arrange
        await _unitOfWork.BeginTransactionAsync();

        // Create invalid entity to cause save error
        var article = new Article
        {
            Id = "article-1",
            Title = null!, // Required field, will cause error
            Content = "Test",
            Url = "https://example.com",
            Author = "Test",
            ScrapedDate = DateTime.UtcNow
        };
        _context.Articles.Add(article);

        // Act
        Func<Task> act = async () => await _unitOfWork.CommitTransactionAsync();

        // Assert
        await act.Should().ThrowAsync<Exception>();
        _context.Database.CurrentTransaction.Should().BeNull(); // Transaction rolled back and disposed
    }

    [Fact]
    public async Task MultipleOperations_WithinTransaction_AreAtomic()
    {
        // Arrange
        await _unitOfWork.BeginTransactionAsync();

        var article1 = CreateTestArticle("article-1", "Article 1");
        var article2 = CreateTestArticle("article-2", "Article 2");

        _context.Articles.Add(article1);
        await _unitOfWork.SaveChangesAsync(); // Save first article

        _context.Articles.Add(article2);
        // Don't save second article yet

        // Act - Rollback entire transaction
        await _unitOfWork.RollbackTransactionAsync();

        // Assert - Both articles should be rolled back
        _context.Articles.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_DisposesResources()
    {
        // Arrange
        var disposable = _unitOfWork as IDisposable;

        // Act
        disposable?.Dispose();

        // Assert - Should not throw
        disposable.Should().NotBeNull();
    }

    [Fact]
    public async Task DisposeAsync_DisposesResourcesAsynchronously()
    {
        // Arrange
        await _unitOfWork.BeginTransactionAsync();
        var article = CreateTestArticle("article-1", "Test Article");
        _context.Articles.Add(article);

        // Act - DisposeAsync should properly clean up transaction
        await _unitOfWork.DisposeAsync();

        // Assert - Should not throw and transaction should be cleaned up
        _context.Database.CurrentTransaction.Should().BeNull();
    }

    [Fact]
    public void HasActiveTransaction_ReturnsFalse_WhenNoTransaction()
    {
        // Assert
        _unitOfWork.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task HasActiveTransaction_ReturnsTrue_WhenTransactionActive()
    {
        // Act
        await _unitOfWork.BeginTransactionAsync();

        // Assert
        _unitOfWork.HasActiveTransaction.Should().BeTrue();
    }

    [Fact]
    public async Task HasActiveTransaction_ReturnsFalse_AfterCommit()
    {
        // Arrange
        await _unitOfWork.BeginTransactionAsync();
        var article = CreateTestArticle("article-1", "Test Article");
        _context.Articles.Add(article);

        // Act
        await _unitOfWork.CommitTransactionAsync();

        // Assert
        _unitOfWork.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task HasActiveTransaction_ReturnsFalse_AfterRollback()
    {
        // Arrange
        await _unitOfWork.BeginTransactionAsync();

        // Act
        await _unitOfWork.RollbackTransactionAsync();

        // Assert
        _unitOfWork.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task SaveChangesAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var article = CreateTestArticle("article-1", "Test Article");
        _context.Articles.Add(article);

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act
        Func<Task> act = async () => await _unitOfWork.SaveChangesAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static Article CreateTestArticle(string id, string title)
    {
        return new Article
        {
            Id = id,
            Title = title,
            Content = "Test content",
            Url = $"https://example.com/{id}",
            Author = "Test Author",
            ScrapedDate = DateTime.UtcNow
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_unitOfWork != null)
        {
            await _unitOfWork.DisposeAsync();
        }
        _context?.Dispose();
    }
}
