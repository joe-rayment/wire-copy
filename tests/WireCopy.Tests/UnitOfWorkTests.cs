// <copyright file="UnitOfWorkTests.cs" company="Wire Copy">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>


using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Persistence;
using Xunit;

namespace WireCopy.Tests;

/// <summary>
/// Tests for IUnitOfWork interface and UnitOfWork implementation
/// </summary>
[Trait("Category", "Unit")]
public class UnitOfWorkTests : TestDatabaseFixture, IAsyncDisposable
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<UnitOfWork> _logger;

    public UnitOfWorkTests()
    {
        _logger = Substitute.For<ILogger<UnitOfWork>>();
        _unitOfWork = new UnitOfWork(DbContext, _logger);
    }

    [Fact]
    public async Task SaveChangesAsync_WithPendingChanges_PersistsToDatabase()
    {
        // Arrange
        var collection = Collection.Create("Test Collection");
        DbContext.Collections.Add(collection);

        // Act
        var result = await _unitOfWork.SaveChangesAsync();

        // Assert
        result.Should().Be(1);
        var saved = await DbContext.Collections.FindAsync(collection.Id);
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Test Collection");
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
        var collection1 = Collection.Create("Collection 1");
        var collection2 = Collection.Create("Collection 2");
        DbContext.Collections.AddRange(collection1, collection2);

        // Act
        var result = await _unitOfWork.SaveChangesAsync();

        // Assert
        result.Should().Be(2);
        DbContext.Collections.Should().HaveCount(2);
    }

    [Fact]
    public async Task BeginTransactionAsync_StartsNewTransaction()
    {
        // Act
        await _unitOfWork.BeginTransactionAsync();

        // Assert
        DbContext.Database.CurrentTransaction.Should().NotBeNull();
        DbContext.Database.CurrentTransaction!.TransactionId.Should().NotBeEmpty(
            "transaction should have been assigned a valid ID");
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
        var collection = Collection.Create("Test Collection");
        DbContext.Collections.Add(collection);

        // Act
        await _unitOfWork.CommitTransactionAsync();

        // Assert
        var saved = await DbContext.Collections.FindAsync(collection.Id);
        saved.Should().NotBeNull();
        DbContext.Database.CurrentTransaction.Should().BeNull(); // Transaction disposed after commit
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
        var collection = Collection.Create("Test Collection");
        DbContext.Collections.Add(collection);
        await _unitOfWork.SaveChangesAsync();

        // Act
        await _unitOfWork.RollbackTransactionAsync();

        // Assert - Use a fresh context to verify data was actually rolled back in the database
        await using var verifyContext = CreateDbContext();
        var saved = await verifyContext.Collections.FindAsync(collection.Id);
        saved.Should().BeNull(); // Changes were rolled back
        DbContext.Database.CurrentTransaction.Should().BeNull(); // Transaction disposed after rollback
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
    public async Task MultipleOperations_WithinTransaction_AreAtomic()
    {
        // Arrange
        await _unitOfWork.BeginTransactionAsync();

        var collection1 = Collection.Create("Collection 1");
        var collection2 = Collection.Create("Collection 2");

        DbContext.Collections.Add(collection1);
        await _unitOfWork.SaveChangesAsync(); // Save first collection

        DbContext.Collections.Add(collection2);
        // Don't save second collection yet

        // Act - Rollback entire transaction
        await _unitOfWork.RollbackTransactionAsync();

        // Assert - Both collections should be rolled back (use fresh context to verify database state)
        await using var verifyContext = CreateDbContext();
        verifyContext.Collections.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_DisposesResources()
    {
        // Arrange
        var disposable = _unitOfWork as IDisposable;

        // Act
        disposable?.Dispose();

        // Assert - Dispose should not throw, and UnitOfWork should implement IDisposable
        disposable.Should().NotBeNull();

        // After disposal, starting a new transaction should fail or the context should be unusable
        DbContext.Database.CurrentTransaction.Should().BeNull(
            "disposal should clean up any active transaction");
    }

    [Fact]
    public async Task DisposeAsync_DisposesResourcesAsynchronously()
    {
        // Arrange
        await _unitOfWork.BeginTransactionAsync();
        var collection = Collection.Create("Test Collection");
        DbContext.Collections.Add(collection);

        // Act - DisposeAsync should properly clean up transaction
        await _unitOfWork.DisposeAsync();

        // Assert - Should not throw and transaction should be cleaned up
        DbContext.Database.CurrentTransaction.Should().BeNull();
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
        var collection = Collection.Create("Test Collection");
        DbContext.Collections.Add(collection);

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
        var collection = Collection.Create("Test Collection");
        DbContext.Collections.Add(collection);

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act
        Func<Task> act = async () => await _unitOfWork.SaveChangesAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    public async ValueTask DisposeAsync()
    {
        if (_unitOfWork != null)
        {
            await _unitOfWork.DisposeAsync();
        }

        Dispose();
    }
}
