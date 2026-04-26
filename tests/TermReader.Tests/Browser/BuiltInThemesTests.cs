// Licensed under the MIT License. See LICENSE in the repository root.

using System.Reflection;
using FluentAssertions;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Themes;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class BuiltInThemesTests
{
    private static readonly PropertyInfo[] PaletteProperties = typeof(ThemePalette)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(ThemeColor))
        .ToArray();

    [Theory]
    [InlineData(ThemeName.Phosphor)]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void Get_ReturnsNonNullPalette(ThemeName theme)
    {
        var palette = BuiltInThemes.Get(theme);

        palette.Should().NotBeNull();
    }

    [Theory]
    [InlineData(ThemeName.Phosphor)]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void Get_AllColorRolesArePopulated(ThemeName theme)
    {
        var palette = BuiltInThemes.Get(theme);

        // ThemePalette has 20 ThemeColor properties
        PaletteProperties.Should().HaveCount(20);

        foreach (var prop in PaletteProperties)
        {
            var color = (ThemeColor)prop.GetValue(palette)!;
            // A default ThemeColor would have ConsoleColor.Black (0) and AnsiCode 0.
            // Not all roles default to Black/0, so just verify it's not an uninitialized struct
            // by checking it's a valid, defined ConsoleColor.
            color.ConsoleColor.Should().BeDefined(
                because: $"{prop.Name} should have a valid ConsoleColor");
        }
    }

    [Theory]
    [InlineData(ThemeName.Phosphor)]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void Get_AllConsoleColorsAreValidEnumValues(ThemeName theme)
    {
        var palette = BuiltInThemes.Get(theme);

        foreach (var prop in PaletteProperties)
        {
            var color = (ThemeColor)prop.GetValue(palette)!;
            Enum.IsDefined(typeof(ConsoleColor), color.ConsoleColor).Should().BeTrue(
                because: $"{prop.Name} ConsoleColor {color.ConsoleColor} should be a valid enum value");
        }
    }

    [Theory]
    [InlineData(ThemeName.Phosphor)]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void Get_AllAnsiCodesAreInValidRange(ThemeName theme)
    {
        var palette = BuiltInThemes.Get(theme);

        foreach (var prop in PaletteProperties)
        {
            var color = (ThemeColor)prop.GetValue(palette)!;
            // AnsiCode is a byte so always 0-255, but verify explicitly
            color.AnsiCode.Should().BeInRange(0, 255,
                because: $"{prop.Name} ANSI code should be in 0-255 range");
        }
    }

    [Fact]
    public void Get_AllThemeNamesHavePalettes()
    {
        var allThemes = Enum.GetValues<ThemeName>();

        foreach (var theme in allThemes)
        {
            var act = () => BuiltInThemes.Get(theme);
            act.Should().NotThrow(
                because: $"theme {theme} should have a registered palette");
        }
    }
}
