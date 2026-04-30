// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WireCopy.Persistence;

namespace WireCopy.Tests;

/// <summary>
/// Base class for tests that need an in-memory SQLite database with AppDbContext.
/// Creates a shared SQLite connection and provides methods to create fresh DbContext instances.
/// </summary>
public abstract class TestDatabaseFixture : IDisposable
{
    private readonly SqliteConnection _connection;

    protected AppDbContext DbContext { get; }

    protected TestDatabaseFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        DbContext = new AppDbContext(options);
        DbContext.Database.EnsureCreated();
    }

    /// <summary>
    /// Creates a new AppDbContext instance sharing the same underlying SQLite connection.
    /// Useful for tests that need to verify data with a separate context.
    /// </summary>
    protected AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new AppDbContext(options);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            DbContext?.Dispose();
            _connection?.Dispose();
        }
    }
}
