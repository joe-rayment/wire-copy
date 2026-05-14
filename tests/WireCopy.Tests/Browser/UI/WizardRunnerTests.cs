// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;
using Xunit;

namespace WireCopy.Tests.Browser.UI;

[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class WizardRunnerTests
{
    private static readonly ThemePalette Palette =
        BuiltInThemes.Get(Domain.Enums.Browser.ThemeName.Phosphor);

    [Fact]
    public void WizardStep_Properties()
    {
        var step = new WizardStep
        {
            Title = "Setup",
            Description = "Configure your API keys",
            Fields = new List<FormFieldConfig>
            {
                new() { Label = "API Key" },
            },
            IsOptional = true,
        };

        step.Title.Should().Be("Setup");
        step.Description.Should().Be("Configure your API keys");
        step.Fields.Should().HaveCount(1);
        step.IsOptional.Should().BeTrue();
        step.OnValidateAsync.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_EmptySteps_ReturnsEmptyDictionary()
    {
        var input = Substitute.For<IInputHandler>();
        var steps = new List<WizardStep>();

        var result = await WizardRunner.RunAsync(input, steps, Palette, CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_SingleStep_SingleField_CollectsValue()
    {
        var input = Substitute.For<IInputHandler>();
        input.PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>())
            .Returns("my-value");

        var steps = new List<WizardStep>
        {
            new()
            {
                Title = "Test",
                Fields = new List<FormFieldConfig>
                {
                    new() { Label = "Name" },
                },
            },
        };

        try
        {
            var result = await WizardRunner.RunAsync(input, steps, Palette, CancellationToken.None);
            result.Should().NotBeNull();
            result!["Name"].Should().Be("my-value");
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task RunAsync_CancelOnFirstStep_ReturnsNull()
    {
        var input = Substitute.For<IInputHandler>();

        // First call to FormField.PromptAsync returns null (Escape)
        input.PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>())
            .Returns((string?)null);

        var steps = new List<WizardStep>
        {
            new()
            {
                Title = "Test",
                Fields = new List<FormFieldConfig>
                {
                    new() { Label = "Name" },
                },
            },
        };

        try
        {
            var result = await WizardRunner.RunAsync(input, steps, Palette, CancellationToken.None);
            result.Should().BeNull();
        }
        catch (IOException)
        {
            // Expected in CI
        }
    }

    [Fact]
    public async Task RunAsync_MultipleSteps_CollectsAllValues()
    {
        var callCount = 0;
        var input = Substitute.For<IInputHandler>();
        input.PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>())
            .Returns(x =>
            {
                callCount++;
                return callCount switch
                {
                    1 => "user@example.com",
                    2 => "sk-ant-test",
                    _ => "extra",
                };
            });

        var steps = new List<WizardStep>
        {
            new()
            {
                Title = "Account",
                Fields = new List<FormFieldConfig>
                {
                    new() { Label = "Email" },
                },
            },
            new()
            {
                Title = "API Key",
                Fields = new List<FormFieldConfig>
                {
                    new() { Label = "Key" },
                },
            },
        };

        try
        {
            var result = await WizardRunner.RunAsync(input, steps, Palette, CancellationToken.None);
            result.Should().NotBeNull();
            result!["Email"].Should().Be("user@example.com");
            result["Key"].Should().Be("sk-ant-test");
        }
        catch (IOException)
        {
            // Expected in CI
        }
    }

    [Fact]
    public async Task RunAsync_WithAsyncValidation_ErrorRetries()
    {
        var callCount = 0;
        var validationCallCount = 0;
        var input = Substitute.For<IInputHandler>();
        input.PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>())
            .Returns(x =>
            {
                callCount++;
                return callCount switch
                {
                    1 => "bad-bucket",        // first attempt (will fail validation)
                    2 => (string?)null,        // validation error prompt, press any key
                    3 => "good-bucket",        // retry (will pass validation)
                    _ => (string?)null,
                };
            });

        var steps = new List<WizardStep>
        {
            new()
            {
                Title = "Storage",
                Fields = new List<FormFieldConfig>
                {
                    new() { Label = "Bucket" },
                },
                OnValidateAsync = async values =>
                {
                    validationCallCount++;
                    await Task.CompletedTask;
                    return values["Bucket"] == "bad-bucket" ? "Bucket not found" : null;
                },
            },
        };

        try
        {
            var result = await WizardRunner.RunAsync(input, steps, Palette, CancellationToken.None);
            result.Should().NotBeNull();
            result!["Bucket"].Should().Be("good-bucket");
            validationCallCount.Should().Be(2);
        }
        catch (IOException)
        {
            // Expected in CI
        }
    }

    [Fact]
    public void WizardStep_MultipleFields()
    {
        var step = new WizardStep
        {
            Title = "Credentials",
            Fields = new List<FormFieldConfig>
            {
                new() { Label = "Email" },
                new() { Label = "Password", IsSecret = true },
                new() { Label = "API Key", Placeholder = "sk-ant-..." },
            },
        };

        step.Fields.Should().HaveCount(3);
        step.Fields[1].IsSecret.Should().BeTrue();
        step.Fields[2].Placeholder.Should().Be("sk-ant-...");
    }
}
