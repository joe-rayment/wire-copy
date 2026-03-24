// Educational and personal use only.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Podcast;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Domain.ValueObjects.Podcast;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Publishes podcast episodes and RSS feeds to cloud storage.
/// </summary>
internal sealed class PodcastPublisher : IPodcastPublisher
{
    private const string FeedIndexPath = "podcasts/feed-index.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ICloudStorageClient _storage;
    private readonly IPodcastFeedGenerator _feedGenerator;
    private readonly ILogger<PodcastPublisher> _logger;

    public PodcastPublisher(
        ICloudStorageClient storage,
        IPodcastFeedGenerator feedGenerator,
        ILogger<PodcastPublisher> logger)
    {
        _storage = storage;
        _feedGenerator = feedGenerator;
        _logger = logger;
    }

    public async Task<FeedPublishResult> PublishFeedAsync(
        PodcastMetadata podcast,
        IReadOnlyList<EpisodeSource> episodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(podcast);
        ArgumentNullException.ThrowIfNull(episodes);

        try
        {
            _logger.LogInformation(
                "Publishing podcast '{Title}' with {Count} episodes",
                podcast.Title,
                episodes.Count);

            // Step 1: Get or create feed UUID
            var feedUuid = await GetOrCreateFeedUuidAsync(podcast.Title, cancellationToken);
            var feedBasePath = $"podcasts/{feedUuid}";

            // Step 2: Load existing episodes from manifest to preserve them
            var existingEpisodes = await LoadExistingEpisodesAsync(feedBasePath, cancellationToken);

            // Step 3: Upload new episodes (using deterministic IDs to enable skip-if-exists)
            var newEpisodeMetadata = new List<EpisodeMetadata>();
            var episodesUploaded = 0;

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var episodeUuid = DeriveEpisodeId(episode.Title, episode.SourceUrl);
                var objectPath = $"{feedBasePath}/episodes/{episodeUuid}.m4b";

                if (await _storage.ExistsAsync(objectPath, cancellationToken))
                {
                    _logger.LogDebug("Episode already uploaded, skipping: {Title}", episode.Title);
                }
                else
                {
                    if (!File.Exists(episode.LocalAudioFilePath))
                    {
                        _logger.LogWarning("Audio file not found: {Path}", episode.LocalAudioFilePath);
                        continue;
                    }

                    await _storage.UploadAsync(
                        episode.LocalAudioFilePath,
                        objectPath,
                        "audio/x-m4b",
                        cancellationToken);

                    _logger.LogInformation("Uploaded episode: {Title}", episode.Title);
                    episodesUploaded++;
                }

                var fileInfo = new FileInfo(episode.LocalAudioFilePath);

                newEpisodeMetadata.Add(new EpisodeMetadata
                {
                    Id = episodeUuid,
                    Title = episode.Title,
                    Description = episode.Description,
                    PublishedAtUtc = DateTime.UtcNow,
                    AudioUrl = _storage.GetPublicUrl(objectPath),
                    AudioSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
                    Duration = episode.Duration,
                    AudioMimeType = "audio/x-m4b",
                    Chapters = episode.Chapters,
                    SourceUrl = episode.SourceUrl,
                });
            }

            // Step 4: Merge new episodes with existing ones (new episodes replace duplicates by ID)
            var mergedEpisodes = MergeEpisodes(existingEpisodes, newEpisodeMetadata);

            // Step 5: Generate feed XML with all episodes
            var feedObjectPath = $"{feedBasePath}/feed.xml";
            var feedUrl = _storage.GetPublicUrl(feedObjectPath);
            var enrichedPodcast = podcast with { FeedUrl = feedUrl };

            var feedXml = await _feedGenerator.GenerateFeedXmlAsync(
                enrichedPodcast,
                mergedEpisodes,
                cancellationToken);

            // Step 6: Upload feed.xml
            await _storage.UploadStringAsync(
                feedXml,
                feedObjectPath,
                "application/rss+xml",
                cancellationToken);

            _logger.LogInformation("Published feed: {FeedUrl}", feedUrl);

            // Step 7: Update manifest and feed index
            await UpdateManifestAsync(feedBasePath, podcast, mergedEpisodes, cancellationToken);
            await UpdateFeedIndexAsync(podcast.Title, feedUuid, feedUrl, cancellationToken);

            return FeedPublishResult.Successful(feedUrl, episodesUploaded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish podcast feed: {Message}", ex.Message);
            return FeedPublishResult.Failure($"Publish failed: {ex.Message}");
        }
    }

    public async Task<FeedPublishResult> BootstrapFeedAsync(
        PodcastMetadata podcast,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(podcast);

        try
        {
            // Check for existing feed first to avoid clobbering
            var existingUrl = await GetExistingFeedUrlAsync(podcast.Title, cancellationToken);
            if (existingUrl != null)
            {
                _logger.LogInformation("Existing feed found for '{Title}': {Url}", podcast.Title, existingUrl);
                return FeedPublishResult.Successful(existingUrl, 0);
            }

            _logger.LogInformation("Bootstrapping empty feed for '{Title}'", podcast.Title);

            // Get or create feed UUID
            var feedUuid = await GetOrCreateFeedUuidAsync(podcast.Title, cancellationToken);
            var feedBasePath = $"podcasts/{feedUuid}";

            // Compute feed URL so we can set it on the metadata for atom:link
            var feedObjectPath = $"{feedBasePath}/feed.xml";
            var feedUrl = _storage.GetPublicUrl(feedObjectPath);

            // Enrich metadata with feed URL for atom:link rel=self
            var enrichedPodcast = podcast with { FeedUrl = feedUrl };

            // Generate empty feed
            var feedXml = await _feedGenerator.GenerateFeedXmlAsync(
                enrichedPodcast,
                Array.Empty<EpisodeMetadata>(),
                cancellationToken);

            // Upload feed
            await _storage.UploadStringAsync(
                feedXml,
                feedObjectPath,
                "application/rss+xml",
                cancellationToken);

            // Update manifest and feed index
            await UpdateManifestAsync(feedBasePath, podcast, [], cancellationToken);
            await UpdateFeedIndexAsync(podcast.Title, feedUuid, feedUrl, cancellationToken);

            _logger.LogInformation("Bootstrapped empty feed at {FeedUrl}", feedUrl);
            return FeedPublishResult.Successful(feedUrl, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to bootstrap feed: {Message}", ex.Message);
            return FeedPublishResult.Failure($"Feed bootstrap failed: {ex.Message}");
        }
    }

    public async Task<string?> GetExistingFeedUrlAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);

        var index = await LoadFeedIndexAsync(cancellationToken);
        if (index.TryGetValue(title, out var entry))
        {
            return entry.FeedUrl;
        }

        return null;
    }

    private static IReadOnlyList<EpisodeMetadata> MergeEpisodes(
        IReadOnlyList<EpisodeMetadata> existing,
        IReadOnlyList<EpisodeMetadata> incoming)
    {
        var merged = new Dictionary<string, EpisodeMetadata>(StringComparer.Ordinal);

        foreach (var episode in existing)
        {
            merged[episode.Id] = episode;
        }

        foreach (var episode in incoming)
        {
            merged[episode.Id] = episode;
        }

        return merged.Values.OrderBy(e => e.PublishedAtUtc).ToList();
    }

    private static string DeriveEpisodeId(string title, string? sourceUrl)
    {
        var input = $"{title}|{sourceUrl ?? string.Empty}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private async Task<string> GetOrCreateFeedUuidAsync(string title, CancellationToken cancellationToken)
    {
        var index = await LoadFeedIndexAsync(cancellationToken);
        if (index.TryGetValue(title, out var entry))
        {
            _logger.LogDebug("Found existing feed UUID for '{Title}': {Uuid}", title, entry.Uuid);
            return entry.Uuid;
        }

        var newUuid = Guid.NewGuid().ToString("N");
        _logger.LogInformation("Created new feed UUID for '{Title}': {Uuid}", title, newUuid);
        return newUuid;
    }

    private async Task<Dictionary<string, FeedIndexEntry>> LoadFeedIndexAsync(CancellationToken cancellationToken)
    {
        var json = await _storage.DownloadStringAsync(FeedIndexPath, cancellationToken);
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, FeedIndexEntry>();
        }

        return JsonSerializer.Deserialize<Dictionary<string, FeedIndexEntry>>(json, JsonOptions) ?? [];
    }

    private async Task UpdateFeedIndexAsync(
        string title,
        string uuid,
        string feedUrl,
        CancellationToken cancellationToken)
    {
        var index = await LoadFeedIndexAsync(cancellationToken);
        index[title] = new FeedIndexEntry { Uuid = uuid, FeedUrl = feedUrl };

        var json = JsonSerializer.Serialize(index, JsonOptions);
        await _storage.UploadStringAsync(json, FeedIndexPath, "application/json", cancellationToken);
    }

    private async Task<IReadOnlyList<EpisodeMetadata>> LoadExistingEpisodesAsync(
        string feedBasePath,
        CancellationToken cancellationToken)
    {
        var json = await _storage.DownloadStringAsync($"{feedBasePath}/manifest.json", cancellationToken);
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ManifestData>(json, JsonOptions);
            return manifest?.Episodes ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse existing manifest, starting fresh");
            return [];
        }
    }

    private async Task UpdateManifestAsync(
        string feedBasePath,
        PodcastMetadata podcast,
        IReadOnlyList<EpisodeMetadata> episodes,
        CancellationToken cancellationToken)
    {
        var manifest = new ManifestData
        {
            Title = podcast.Title,
            Author = podcast.Author,
            Description = podcast.Description,
            EpisodeCount = episodes.Count,
            UpdatedAtUtc = DateTime.UtcNow,
            Episodes = episodes.ToList(),
        };

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        await _storage.UploadStringAsync(
            json,
            $"{feedBasePath}/manifest.json",
            "application/json",
            cancellationToken);
    }

    private sealed record FeedIndexEntry
    {
        public required string Uuid { get; init; }

        public required string FeedUrl { get; init; }
    }

    private sealed record ManifestData
    {
        public string? Title { get; init; }

        public string? Author { get; init; }

        public string? Description { get; init; }

        public int EpisodeCount { get; init; }

        public DateTime UpdatedAtUtc { get; init; }

        public List<EpisodeMetadata>? Episodes { get; init; }
    }
}
