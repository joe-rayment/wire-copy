// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-romy.10 — manual live probe: replays the wizard's two analyzer
/// rounds against the REAL OpenAI API using the memeorandum fixture, printing
/// the returned sections and their live coverage. Run manually when the
/// live gate reports a degenerate pattern on a site, to see exactly what the
/// model returned without burning full TUI cycles:
///   dotnet test --filter FullyQualifiedName~MemeorandumLiveProbe
/// Skipped by default: needs a real key + network.
/// </summary>
public class MemeorandumLiveProbeTests
{
    private readonly ITestOutputHelper _output;

    public MemeorandumLiveProbeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Skip = "live OpenAI probe; run manually via --filter MemeorandumLiveProbe with WIRECOPY_LIVE_PROBE=1")]
    public void Placeholder()
    {
    }

    [Fact]
    public async Task Probe_InferPattern_OnMemeorandumFixture()
    {
        if (Environment.GetEnvironmentVariable("WIRECOPY_LIVE_PROBE") != "1")
        {
            return; // opt-in only
        }

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WireCopy", "settings.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        var apiKey = doc.RootElement.GetProperty("Settings").GetProperty("OpenAiApiKey")
            .GetProperty("Value").GetString();
        apiKey.Should().NotBeNullOrEmpty();

        var store = Substitute.For<IUserSettingsStore>();
        store.Get("OpenAiApiKey").Returns(apiKey);

        var analyzer = new OpenAiHierarchyAnalyzer(
            Options.Create(new OpenAiHierarchyConfiguration()),
            Options.Create(new OpenAiTtsConfiguration()),
            store,
            NullLogger<OpenAiHierarchyAnalyzer>.Instance);

        var html = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "memeorandum-2026-06-12.html"));
        var extractor = new LinkExtractor(NullLogger<LinkExtractor>.Instance);
        var links = await extractor.ExtractLinksAsync(html, "https://www.memeorandum.com/");

        var proposal = await analyzer.ProposeSetupQuestionsAsync(null, links, "https://www.memeorandum.com/");
        _output.WriteLine($"questions={proposal.Questions.Count}");
        var inferred = await analyzer.InferPatternFromAnswersAsync(
            null, links, "https://www.memeorandum.com/", proposal, new List<SetupAnswer>());

        _output.WriteLine($"confidence={inferred.Confidence} confirmQ={inferred.ConfirmQuestion?.Prompt}");
        foreach (var s in inferred.Config.Sections)
        {
            var matches = links.Count(l => l.Type == LinkType.Content && !l.IsGroupHeader &&
                NavigationTreeBuilder.MatchesSection(l, s));
            _output.WriteLine($"section '{s.Name}' matches={matches}");
            _output.WriteLine($"   selectors: {string.Join(" | ", s.ParentSelectors)}");
            _output.WriteLine($"   urlPatterns: {string.Join(" | ", s.UrlPatterns)}");
        }

        var (covered, total) = SetupWizard.Coverage(inferred.Config, links);
        _output.WriteLine($"coverage {covered} of {total}; degenerate={SetupWizard.IsDegenerate(inferred.Config, links)}");
        if (SetupWizard.OrderingSanityFailure(inferred.Config, links) is { } sf)
        {
            _output.WriteLine($"sanity: {sf.Answer}");
        }
    }
}
