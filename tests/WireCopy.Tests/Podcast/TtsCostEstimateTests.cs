// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class TtsCostEstimateTests
{
    private readonly OpenAiTtsService _service;

    public TtsCostEstimateTests()
    {
        var config = Options.Create(new OpenAiTtsConfiguration());
        _service = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);
    }

    [Fact]
    public void EstimateCost_CorrectCharacterCount()
    {
        var text = "Hello, world!";
        var estimate = _service.EstimateCost(text);

        estimate.CharacterCount.Should().Be(text.Length);
    }

    [Fact]
    public void EstimateCost_ShortText_SingleChunk()
    {
        var text = "Short text.";
        var estimate = _service.EstimateCost(text);

        estimate.ChunkCount.Should().Be(1);
    }

    [Fact]
    public void EstimateCost_CorrectUsdCalculation()
    {
        // $15 per 1M characters
        var text = new string('a', 1000);
        var estimate = _service.EstimateCost(text);

        // 1000 * 15 / 1_000_000 = 0.015
        estimate.EstimatedCostUsd.Should().Be(0.015m);
    }

    [Fact]
    public void EstimateCost_CorrectDurationEstimate()
    {
        // 150 WPM * 5 chars/word = 750 chars/min
        var text = new string('a', 750);
        var estimate = _service.EstimateCost(text);

        estimate.EstimatedDurationMinutes.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void EstimateCost_SummaryFormat()
    {
        var text = "Some sample text for cost estimation.";
        var estimate = _service.EstimateCost(text);

        estimate.Summary.Should().Contain("chars");
        estimate.Summary.Should().Contain("chunks");
        estimate.Summary.Should().Contain("min");
        estimate.Summary.Should().Contain("$");
    }

    [Fact]
    public void EstimateCost_LongText_MultipleChunks()
    {
        var text = string.Join("\n\n", Enumerable.Repeat("This is a paragraph with enough content.", 200));
        var estimate = _service.EstimateCost(text);

        estimate.ChunkCount.Should().BeGreaterThan(1);
    }
}
