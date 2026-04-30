// <copyright file="LegacyAppDataMigratorTests.cs" company="Wire Copy">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Configuration;

[Trait("Category", "Unit")]
public class LegacyAppDataMigratorTests : IDisposable
{
    private readonly string _root;

    public LegacyAppDataMigratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"migrator-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MigrateIfNeeded_RenamesLegacyDir_WhenOnlyLegacyExists()
    {
        var legacy = Path.Combine(_root, "TermReader");
        var current = Path.Combine(_root, "WireCopy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "termreader.db"), "fake-db-content");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

        LegacyAppDataMigrator.MigrateIfNeeded(NullLogger.Instance, _root);

        Directory.Exists(legacy).Should().BeFalse("legacy dir should have been moved");
        Directory.Exists(current).Should().BeTrue("current dir should now exist");
        File.Exists(Path.Combine(current, "termreader.db")).Should().BeTrue();
        File.Exists(Path.Combine(current, "settings.json")).Should().BeTrue();
    }

    [Fact]
    public void MigrateIfNeeded_NoOp_WhenNeitherExists()
    {
        // Sanity: empty root should be left alone (no exception, nothing created).
        LegacyAppDataMigrator.MigrateIfNeeded(NullLogger.Instance, _root);

        Directory.Exists(Path.Combine(_root, "TermReader")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "WireCopy")).Should().BeFalse();
    }

    [Fact]
    public void MigrateIfNeeded_NoOp_WhenOnlyCurrentExists()
    {
        var current = Path.Combine(_root, "WireCopy");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "wirecopy.db"), "fresh-install");

        LegacyAppDataMigrator.MigrateIfNeeded(NullLogger.Instance, _root);

        Directory.Exists(current).Should().BeTrue();
        File.ReadAllText(Path.Combine(current, "wirecopy.db")).Should().Be("fresh-install");
        Directory.Exists(Path.Combine(_root, "TermReader")).Should().BeFalse();
    }

    [Fact]
    public void MigrateIfNeeded_RefusesToOverwrite_WhenBothDirsExist()
    {
        var legacy = Path.Combine(_root, "TermReader");
        var current = Path.Combine(_root, "WireCopy");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(legacy, "old.db"), "old");
        File.WriteAllText(Path.Combine(current, "new.db"), "new");

        LegacyAppDataMigrator.MigrateIfNeeded(NullLogger.Instance, _root);

        // Both directories must still exist with their original contents intact.
        Directory.Exists(legacy).Should().BeTrue();
        Directory.Exists(current).Should().BeTrue();
        File.ReadAllText(Path.Combine(legacy, "old.db")).Should().Be("old");
        File.ReadAllText(Path.Combine(current, "new.db")).Should().Be("new");
    }
}
