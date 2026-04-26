// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Configuration.Validation;
using Xunit;

namespace TermReader.Tests;

[Trait("Category", "Unit")]
public class AnthropicConfigurationTests
{
    private readonly AnthropicConfigurationValidator _validator = new();

    [Fact]
    public void SectionName_IsAnthropic()
    {
        AnthropicConfiguration.SectionName.Should().Be("Anthropic");
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var config = new AnthropicConfiguration();

        config.ApiKey.Should().BeNull();
        config.Model.Should().Be("claude-haiku-4-5-20251001");
        config.MaxTokens.Should().Be(4096);
        config.MaxBudgetUsd.Should().Be(0.10m);
    }

    [Fact]
    public void Validate_DefaultConfig_Succeeds()
    {
        var config = new AnthropicConfiguration();

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_NullApiKey_Succeeds()
    {
        var config = new AnthropicConfiguration { ApiKey = null };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyModel_Fails()
    {
        var config = new AnthropicConfiguration { Model = "" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Model");
    }

    [Fact]
    public void Validate_WhitespaceModel_Fails()
    {
        var config = new AnthropicConfiguration { Model = "   " };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Model");
    }

    [Fact]
    public void Validate_ZeroMaxTokens_Fails()
    {
        var config = new AnthropicConfiguration { MaxTokens = 0 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxTokens");
    }

    [Fact]
    public void Validate_NegativeMaxTokens_Fails()
    {
        var config = new AnthropicConfiguration { MaxTokens = -1 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxTokens");
    }

    [Fact]
    public void Validate_ZeroMaxBudget_Fails()
    {
        var config = new AnthropicConfiguration { MaxBudgetUsd = 0m };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxBudgetUsd");
    }

    [Fact]
    public void Validate_NegativeMaxBudget_Fails()
    {
        var config = new AnthropicConfiguration { MaxBudgetUsd = -0.5m };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxBudgetUsd");
    }

    [Fact]
    public void Validate_CustomValidConfig_Succeeds()
    {
        var config = new AnthropicConfiguration
        {
            ApiKey = "sk-ant-test-key",
            Model = "claude-sonnet-4-20250514",
            MaxTokens = 8192,
            MaxBudgetUsd = 1.00m
        };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }
}
