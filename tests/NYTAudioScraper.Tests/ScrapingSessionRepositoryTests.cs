using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NYTAudioScraper.Domain.Entities;
using NYTAudioScraper.Infrastructure.Persistence;
using NYTAudioScraper.Infrastructure.Persistence.Repositories;

namespace NYTAudioScraper.Tests;

public class ScrapingSessionRepositoryTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly ScrapingSessionRepository _repository;

    public ScrapingSessionRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);
        _repository = new ScrapingSessionRepository(_context);
    }

    [Fact]
    public async Task GetSessionsByDateRangeAsync_WithinRange_ReturnsSessions()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var sessions = new[]
        {
            new ScrapingSession
            {
                Id = "session-1",
                StartedAt = now,
                Articles = new List<Article>(),
                Status = ScrapingStatus.Completed
            },
            new ScrapingSession
            {
                Id = "session-2",
                StartedAt = now.AddDays(-5),
                Articles = new List<Article>(),
                Status = ScrapingStatus.Completed
            },
            new ScrapingSession
            {
                Id = "session-3",
                StartedAt = now.AddDays(-10),
                Articles = new List<Article>(),
                Status = ScrapingStatus.Completed
            }
        };

        foreach (var session in sessions)
        {
            await _repository.AddAsync(session);
        }
        await _repository.SaveChangesAsync();

        // Act
        var result = await _repository.GetSessionsByDateRangeAsync(now.AddDays(-7), now.AddDays(1));

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(s => s.Id == "session-1");
        result.Should().Contain(s => s.Id == "session-2");
    }

    [Fact]
    public async Task GetSessionsByStatusAsync_WithMatchingStatus_ReturnsSessions()
    {
        // Arrange
        var sessions = new[]
        {
            new ScrapingSession
            {
                Id = "session-1",
                StartedAt = DateTime.UtcNow,
                Articles = new List<Article>(),
                Status = ScrapingStatus.Completed
            },
            new ScrapingSession
            {
                Id = "session-2",
                StartedAt = DateTime.UtcNow,
                Articles = new List<Article>(),
                Status = ScrapingStatus.InProgress
            },
            new ScrapingSession
            {
                Id = "session-3",
                StartedAt = DateTime.UtcNow,
                Articles = new List<Article>(),
                Status = ScrapingStatus.Completed
            }
        };

        foreach (var session in sessions)
        {
            await _repository.AddAsync(session);
        }
        await _repository.SaveChangesAsync();

        // Act
        var result = await _repository.GetSessionsByStatusAsync(ScrapingStatus.Completed);

        // Assert
        result.Should().HaveCount(2);
        result.Should().AllSatisfy(s => s.Status.Should().Be(ScrapingStatus.Completed));
    }

    [Fact]
    public async Task GetLastIncompleteSessionAsync_WithIncompleteSessions_ReturnsLatest()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var sessions = new[]
        {
            new ScrapingSession
            {
                Id = "session-1",
                StartedAt = now.AddDays(-2),
                Articles = new List<Article>(),
                Status = ScrapingStatus.InProgress
            },
            new ScrapingSession
            {
                Id = "session-2",
                StartedAt = now.AddDays(-1),
                Articles = new List<Article>(),
                Status = ScrapingStatus.PartiallyCompleted
            },
            new ScrapingSession
            {
                Id = "session-3",
                StartedAt = now,
                Articles = new List<Article>(),
                Status = ScrapingStatus.Completed
            }
        };

        foreach (var session in sessions)
        {
            await _repository.AddAsync(session);
        }
        await _repository.SaveChangesAsync();

        // Act
        var result = await _repository.GetLastIncompleteSessionAsync();

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("session-2"); // Most recent incomplete
    }

    [Fact]
    public async Task GetLastIncompleteSessionAsync_WithNoIncompleteSessions_ReturnsNull()
    {
        // Arrange
        var session = new ScrapingSession
        {
            Id = "session-1",
            StartedAt = DateTime.UtcNow,
            Articles = new List<Article>(),
            Status = ScrapingStatus.Completed
        };
        await _repository.AddAsync(session);
        await _repository.SaveChangesAsync();

        // Act
        var result = await _repository.GetLastIncompleteSessionAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetTotalCostAsync_SumsAllCompletedSessions()
    {
        // Arrange
        var sessions = new[]
        {
            new ScrapingSession
            {
                Id = "session-1",
                StartedAt = DateTime.UtcNow,
                Articles = new List<Article>(),
                Status = ScrapingStatus.Completed,
                EstimatedCost = 10.50m
            },
            new ScrapingSession
            {
                Id = "session-2",
                StartedAt = DateTime.UtcNow,
                Articles = new List<Article>(),
                Status = ScrapingStatus.PartiallyCompleted,
                EstimatedCost = 5.25m
            },
            new ScrapingSession
            {
                Id = "session-3",
                StartedAt = DateTime.UtcNow,
                Articles = new List<Article>(),
                Status = ScrapingStatus.Failed,
                EstimatedCost = 2.00m
            }
        };

        foreach (var session in sessions)
        {
            await _repository.AddAsync(session);
        }
        await _repository.SaveChangesAsync();

        // Act
        var result = await _repository.GetTotalCostAsync();

        // Assert
        result.Should().Be(15.75m); // Only Completed and PartiallyCompleted
    }

    [Fact]
    public async Task GetTotalCostByDateRangeAsync_WithinRange_SumsCosts()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var sessions = new[]
        {
            new ScrapingSession
            {
                Id = "session-1",
                StartedAt = now,
                Articles = new List<Article>(),
                Status = ScrapingStatus.Completed,
                EstimatedCost = 10.00m
            },
            new ScrapingSession
            {
                Id = "session-2",
                StartedAt = now.AddDays(-5),
                Articles = new List<Article>(),
                Status = ScrapingStatus.Completed,
                EstimatedCost = 5.00m
            },
            new ScrapingSession
            {
                Id = "session-3",
                StartedAt = now.AddDays(-10),
                Articles = new List<Article>(),
                Status = ScrapingStatus.Completed,
                EstimatedCost = 3.00m
            }
        };

        foreach (var session in sessions)
        {
            await _repository.AddAsync(session);
        }
        await _repository.SaveChangesAsync();

        // Act
        var result = await _repository.GetTotalCostByDateRangeAsync(now.AddDays(-7), now.AddDays(1));

        // Assert
        result.Should().Be(15.00m); // Only first two sessions
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }
}
