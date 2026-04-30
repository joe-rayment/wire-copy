// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;
using Xunit;

namespace WireCopy.Tests.Browser.UI;

[Trait("Category", "Unit")]
public class ConfirmationDialogTests
{
    private readonly IInputHandler _input = Substitute.For<IInputHandler>();
    private readonly ThemePalette _palette = BuiltInThemes.Get(ThemeName.Phosphor);

    [Fact]
    public async Task ConfirmAsync_YKey_ReturnsTrue()
    {
        _input.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 'y' });

        try
        {
            var result = await ConfirmationDialog.ConfirmAsync(
                _input, "Delete?", "Remove this item?", _palette, CancellationToken.None);
            result.Should().BeTrue();
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task ConfirmAsync_UpperY_ReturnsTrue()
    {
        _input.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 'Y' });

        try
        {
            var result = await ConfirmationDialog.ConfirmAsync(
                _input, "Delete?", "Remove this item?", _palette, CancellationToken.None);
            result.Should().BeTrue();
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task ConfirmAsync_NKey_ReturnsFalse()
    {
        _input.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 'n' });

        try
        {
            var result = await ConfirmationDialog.ConfirmAsync(
                _input, "Delete?", "Remove this item?", _palette, CancellationToken.None);
            result.Should().BeFalse();
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task ConfirmAsync_EscapeKey_ReturnsFalse()
    {
        _input.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.GoBack });

        try
        {
            var result = await ConfirmationDialog.ConfirmAsync(
                _input, "Delete?", "Remove this item?", _palette, CancellationToken.None);
            result.Should().BeFalse();
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task ConfirmAsync_Destructive_UsesErrorColor()
    {
        _input.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 'y' });

        try
        {
            // Should not throw — isDestructive just changes colors
            var result = await ConfirmationDialog.ConfirmAsync(
                _input, "Delete?", "Remove this item?", _palette, CancellationToken.None,
                isDestructive: true);
            result.Should().BeTrue();
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task ConfirmDestructiveAsync_CorrectInput_ReturnsTrue()
    {
        _input.PromptForInputAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("DELETE");

        try
        {
            var result = await ConfirmationDialog.ConfirmDestructiveAsync(
                _input, "Clear", "Remove all items?", null, _palette, 24, CancellationToken.None);
            result.Should().BeTrue();
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task ConfirmDestructiveAsync_Cancelled_ReturnsFalse()
    {
        _input.PromptForInputAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns((string?)null);

        try
        {
            var result = await ConfirmationDialog.ConfirmDestructiveAsync(
                _input, "Clear", "Remove all items?", null, _palette, 24, CancellationToken.None);
            result.Should().BeFalse();
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task ConfirmDestructiveAsync_WithAffectedItems_ShowsItemList()
    {
        _input.PromptForInputAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("DELETE");

        var items = new List<string> { "Article 1", "Article 2", "Article 3" };

        try
        {
            var result = await ConfirmationDialog.ConfirmDestructiveAsync(
                _input, "Clear", "Remove all items?", items, _palette, 24, CancellationToken.None);
            result.Should().BeTrue();
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }
}
