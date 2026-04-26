// <copyright file="CommandOptionsTests.cs" company="TermReader">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>

using FluentAssertions;
using TermReader.API;
using Xunit;

namespace TermReader.Tests;

[Trait("Category", "Unit")]
public class CommandOptionsTests
{
    [Fact]
    public void BrowseOptions_DefaultUrl_ShouldBeNull()
    {
        var options = new BrowseOptions();
        options.Url.Should().BeNull();
    }

    [Fact]
    public void BrowseOptions_Validate_ValidUrl_ShouldNotReturnError()
    {
        var options = new BrowseOptions
        {
            Url = "https://news.ycombinator.com"
        };

        var errors = options.Validate();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void BrowseOptions_Validate_InvalidUrl_ShouldReturnError()
    {
        var options = new BrowseOptions
        {
            Url = "not-a-valid-url"
        };

        var errors = options.Validate();
        errors.Should().ContainSingle()
            .Which.Should().Contain("Invalid URL");
    }

    [Fact]
    public void BrowseOptions_Validate_NoUrl_ShouldNotReturnError()
    {
        var options = new BrowseOptions();

        var errors = options.Validate();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void BrowseOptions_Validate_HttpsUrl_ShouldNotReturnError()
    {
        var options = new BrowseOptions
        {
            Url = "https://example.com"
        };

        var errors = options.Validate();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void BrowseOptions_Validate_HttpUrl_ShouldNotReturnError()
    {
        var options = new BrowseOptions
        {
            Url = "http://example.com"
        };

        var errors = options.Validate();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void BrowseOptions_Validate_FtpUrl_ShouldReturnError()
    {
        var options = new BrowseOptions
        {
            Url = "ftp://example.com"
        };

        var errors = options.Validate();
        errors.Should().ContainSingle()
            .Which.Should().Contain("Invalid URL");
    }
}
