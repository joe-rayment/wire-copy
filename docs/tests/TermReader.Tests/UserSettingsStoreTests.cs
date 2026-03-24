// Educational and personal use only.

using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests;

[Trait("Category", "Unit")]
public class UserSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDataProtectionProvider _dataProtection;

    public UserSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"settings-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _dataProtection = DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(_tempDir, "keys")));
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
            // Best-effort cleanup.
        }
    }

    private UserSettingsStore CreateStore() =>
        new(_dataProtection, NullLogger<UserSettingsStore>.Instance);

    [Fact]
    public void Get_MissingKey_ReturnsNull()
    {
        var store = CreateStore();

        store.Get("nonexistent").Should().BeNull();
    }

    [Fact]
    public void Set_PlainValue_RoundTrips()
    {
        var store = CreateStore();

        store.Set("bucket", "my-test-bucket");

        store.Get("bucket").Should().Be("my-test-bucket");
    }

    [Fact]
    public void Set_EncryptedValue_RoundTrips()
    {
        var store = CreateStore();

        store.Set("apikey", "sk-secret-123", encrypt: true);

        store.Get("apikey").Should().Be("sk-secret-123");
    }

    [Fact]
    public void Set_OverwritesExistingValue()
    {
        var store = CreateStore();

        store.Set("key", "old");
        store.Set("key", "new");

        store.Get("key").Should().Be("new");
    }

    [Fact]
    public void Remove_DeletesEntry()
    {
        var store = CreateStore();

        store.Set("key", "value");
        store.Remove("key");

        store.Get("key").Should().BeNull();
    }

    [Fact]
    public void Remove_NonexistentKey_DoesNotThrow()
    {
        var store = CreateStore();

        var act = () => store.Remove("nonexistent");

        act.Should().NotThrow();
    }

    [Fact]
    public void MultipleKeys_IndependentStorage()
    {
        var store = CreateStore();

        store.Set("a", "value-a");
        store.Set("b", "value-b", encrypt: true);
        store.Set("c", "value-c");

        store.Get("a").Should().Be("value-a");
        store.Get("b").Should().Be("value-b");
        store.Get("c").Should().Be("value-c");
    }
}
