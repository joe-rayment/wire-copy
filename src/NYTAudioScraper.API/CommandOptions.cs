using CommandLine;

namespace NYTAudioScraper.API;

public class CommandOptions
{
    [Option('u', "url", Required = false, HelpText = "Specific NYT article URL to process")]
    public string? ArticleUrl { get; set; }

    [Option('s', "section", Required = false, HelpText = "NYT section to scrape (e.g., technology, politics)")]
    public string? Section { get; set; }

    [Option('c', "count", Required = false, Default = 100, HelpText = "Number of articles to process (default: 100, collects all from specified sections)")]
    public int ArticleCount { get; set; }

    [Option('v', "voice", Required = false, HelpText = "ElevenLabs voice ID to use")]
    public string? VoiceId { get; set; }

    [Option('b', "budget", Required = false, HelpText = "Maximum budget in dollars (default: 5.0)")]
    public decimal Budget { get; set; } = 5.0m;

    [Option('o', "output", Required = false, HelpText = "Output directory path")]
    public string? OutputPath { get; set; }

    [Option("skip-login", Required = false, Default = false, HelpText = "Skip NYT login (only scrape public articles)")]
    public bool SkipLogin { get; set; }

    [Option("test", Required = false, Default = false, HelpText = "Run with test/mock data")]
    public bool TestMode { get; set; }
}
