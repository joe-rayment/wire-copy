// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.Themes;
using Xunit;

namespace TermReader.Tests.Browser;

[Collection("ThemeProvider")]
[Trait("Category", "Unit")]
public class ThemeProviderTests : IDisposable
{
    private static readonly string PersistencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TermReader",
        "theme.txt");

    public ThemeProviderTests()
    {
        DeleteThemeFile();
    }

    public void Dispose()
    {
        DeleteThemeFile();
    }

    #region DefaultTheme

    [Fact]
    public void NewProvider_WithNoPersistedFile_DefaultsToPhosphor()
    {
        var sut = new ThemeProvider();

        sut.CurrentTheme.Should().Be(ThemeName.Phosphor);
    }

    #endregion

    #region CycleTheme

    [Fact]
    public void CycleTheme_FromPhosphor_ReturnsAmber()
    {
        var sut = new ThemeProvider();

        var result = sut.CycleTheme();

        result.Should().Be(ThemeName.Amber);
        sut.CurrentTheme.Should().Be(ThemeName.Amber);
    }

    [Fact]
    public void CycleTheme_FromAmber_ReturnsDracula()
    {
        var sut = new ThemeProvider();
        sut.SetTheme(ThemeName.Amber);

        var result = sut.CycleTheme();

        result.Should().Be(ThemeName.Dracula);
        sut.CurrentTheme.Should().Be(ThemeName.Dracula);
    }

    [Fact]
    public void CycleTheme_FromDracula_ReturnsLight()
    {
        var sut = new ThemeProvider();
        sut.SetTheme(ThemeName.Dracula);

        var result = sut.CycleTheme();

        result.Should().Be(ThemeName.Light);
        sut.CurrentTheme.Should().Be(ThemeName.Light);
    }

    [Fact]
    public void CycleTheme_FromLight_WrapsToPhosphor()
    {
        var sut = new ThemeProvider();
        sut.SetTheme(ThemeName.Light);

        var result = sut.CycleTheme();

        result.Should().Be(ThemeName.Phosphor);
        sut.CurrentTheme.Should().Be(ThemeName.Phosphor);
    }

    [Fact]
    public void CycleTheme_FullCycle_ReturnsToPhosphor()
    {
        var sut = new ThemeProvider();

        sut.CycleTheme(); // Amber
        sut.CycleTheme(); // Dracula
        sut.CycleTheme(); // Light
        var result = sut.CycleTheme(); // Phosphor

        result.Should().Be(ThemeName.Phosphor);
    }

    #endregion

    #region SetTheme

    [Theory]
    [InlineData(ThemeName.Phosphor)]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void SetTheme_UpdatesCurrentTheme(ThemeName theme)
    {
        var sut = new ThemeProvider();

        sut.SetTheme(theme);

        sut.CurrentTheme.Should().Be(theme);
    }

    #endregion

    #region Persistence

    [Theory]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void Persistence_RoundTrip_RestoresTheme(ThemeName theme)
    {
        var first = new ThemeProvider();
        first.SetTheme(theme);

        var second = new ThemeProvider();

        second.CurrentTheme.Should().Be(theme);
    }

    [Fact]
    public void Persistence_CorruptFile_DefaultsToPhosphor()
    {
        var directory = Path.GetDirectoryName(PersistencePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(PersistencePath, "not-a-valid-theme");

        var sut = new ThemeProvider();

        sut.CurrentTheme.Should().Be(ThemeName.Phosphor);
    }

    [Fact]
    public void Persistence_EmptyFile_DefaultsToPhosphor()
    {
        var directory = Path.GetDirectoryName(PersistencePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(PersistencePath, string.Empty);

        var sut = new ThemeProvider();

        sut.CurrentTheme.Should().Be(ThemeName.Phosphor);
    }

    #endregion

    private static void DeleteThemeFile()
    {
        if (File.Exists(PersistencePath))
        {
            File.Delete(PersistencePath);
        }
    }
}

[CollectionDefinition("ThemeProvider", DisableParallelization = true)]
[Trait("Category", "Unit")]
public class ThemeProviderCollection : ICollectionFixture<ThemeProviderCollection>
{
}
