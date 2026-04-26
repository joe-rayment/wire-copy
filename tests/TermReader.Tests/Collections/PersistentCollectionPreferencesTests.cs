// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using TermReader.Application.Interfaces;
using TermReader.Persistence;
using Xunit;

namespace TermReader.Tests.Collections;

[Trait("Category", "Unit")]
public class PersistentCollectionPreferencesTests
{
    private readonly IUserSettingsStore _settingsStore;
    private readonly PersistentCollectionPreferences _sut;

    public PersistentCollectionPreferencesTests()
    {
        _settingsStore = Substitute.For<IUserSettingsStore>();
        _sut = new PersistentCollectionPreferences(_settingsStore);
    }

    [Fact]
    public void Get_WhenNoValueStored_ReturnsNull()
    {
        _settingsStore.Get("LastUsedCollectionId").Returns((string?)null);

        var result = _sut.LastUsedCollectionId;

        result.Should().BeNull();
    }

    [Fact]
    public void Get_WhenValidGuidStored_ReturnsParsedGuid()
    {
        var expected = Guid.NewGuid();
        _settingsStore.Get("LastUsedCollectionId").Returns(expected.ToString());

        var result = _sut.LastUsedCollectionId;

        result.Should().Be(expected);
    }

    [Fact]
    public void Get_WhenInvalidValueStored_ReturnsNull()
    {
        _settingsStore.Get("LastUsedCollectionId").Returns("not-a-guid");

        var result = _sut.LastUsedCollectionId;

        result.Should().BeNull();
    }

    [Fact]
    public void Set_WithGuid_PersistsToStore()
    {
        var id = Guid.NewGuid();

        _sut.LastUsedCollectionId = id;

        _settingsStore.Received(1).Set("LastUsedCollectionId", id.ToString());
    }

    [Fact]
    public void Set_WithNull_RemovesFromStore()
    {
        _sut.LastUsedCollectionId = null;

        _settingsStore.Received(1).Remove("LastUsedCollectionId");
    }

    [Fact]
    public void Get_CachesValueAfterFirstRead()
    {
        var expected = Guid.NewGuid();
        _settingsStore.Get("LastUsedCollectionId").Returns(expected.ToString());

        // Read twice
        _ = _sut.LastUsedCollectionId;
        var result = _sut.LastUsedCollectionId;

        result.Should().Be(expected);
        _settingsStore.Received(1).Get("LastUsedCollectionId"); // Only one disk read
    }

    [Fact]
    public void Set_ThenGet_ReturnsCachedValueWithoutDiskRead()
    {
        var id = Guid.NewGuid();

        _sut.LastUsedCollectionId = id;
        var result = _sut.LastUsedCollectionId;

        result.Should().Be(id);
        _settingsStore.DidNotReceive().Get(Arg.Any<string>()); // No disk read needed
    }

    [Fact]
    public void Constructor_ThrowsOnNullSettingsStore()
    {
        var act = () => new PersistentCollectionPreferences(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
