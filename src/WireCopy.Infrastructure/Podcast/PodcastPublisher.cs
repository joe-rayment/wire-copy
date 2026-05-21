// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.ValueObjects.Podcast;

namespace WireCopy.Infrastructure.Podcast;

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

    /// <summary>
    /// workspace-i2vl: IAM propagation backoff after MakeBucketPublic succeeds.
    /// GCS public-edge IAM is eventually consistent — typical propagation lag
    /// is a few seconds. Three attempts at 1s / 3s / 5s catches the common
    /// case; 9s total wall-time matches the user's "just generated a podcast"
    /// attention budget. If the bucket is still 403 after the third attempt
    /// we fall through to the gsutil-hint path (something else is wrong).
    /// </summary>
    private static readonly TimeSpan[] PropagationBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
    ];

    private readonly ICloudStorageClient _storage;
    private readonly IPodcastFeedGenerator _feedGenerator;
    private readonly ILogger<PodcastPublisher> _logger;
    private readonly IFeedReachabilityProbe _reachability;
    private readonly Func<TimeSpan, CancellationToken, Task> _propagationDelay;

    public PodcastPublisher(
        ICloudStorageClient storage,
        IPodcastFeedGenerator feedGenerator,
        ILogger<PodcastPublisher> logger,
        IFeedReachabilityProbe? reachability = null,
        Func<TimeSpan, CancellationToken, Task>? propagationDelay = null)
    {
        _storage = storage;
        _feedGenerator = feedGenerator;
        _logger = logger;
        _reachability = reachability ?? new HttpFeedReachabilityProbe();

        // Tests pass a no-op delay so the IAM-propagation backoff doesn't add
        // 9s of wall time to every BucketNotPublic case.
        _propagationDelay = propagationDelay ?? Task.Delay;
    }

    public async Task<FeedPublishResult> PublishFeedAsync(
        PodcastMetadata podcast,
        IReadOnlyList<EpisodeSource> episodes,
        IProgress<PublishProgress>? progress = null,
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
            var feedUuid = await GetOrCreateFeedUuidAsync(podcast.Title, cancellationToken).ConfigureAwait(false);
            var feedBasePath = $"podcasts/{feedUuid}";

            // Step 2: Load existing episodes from manifest to preserve them
            var existingEpisodes = await LoadExistingEpisodesAsync(feedBasePath, cancellationToken).ConfigureAwait(false);

            // Step 3: Upload new episodes (using deterministic IDs to enable skip-if-exists)
            var newEpisodeMetadata = new List<EpisodeMetadata>();
            var episodesUploaded = 0;
            var skipped = new List<SkippedEpisodeDetail>();

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var episodeUuid = DeriveEpisodeId(episode.Title, episode.SourceUrl);
                var objectPath = $"{feedBasePath}/episodes/{episodeUuid}.m4a";

                // workspace-z2om: the deterministic episode id is title+sourceUrl.
                // For Reading List runs both can stay constant while the M4B
                // *content* changes (e.g. a second article gets added). Skipping
                // the upload because the path already exists then leaves the
                // feed.xml + manifest advertising new size/duration/chapters
                // pointing at the OLD audio bytes. So: only skip when the
                // remote size matches the local size. Mismatch → re-upload.
                var existingSize = await _storage.GetObjectSizeAsync(objectPath, cancellationToken).ConfigureAwait(false);
                var localSize = File.Exists(episode.LocalAudioFilePath)
                    ? new FileInfo(episode.LocalAudioFilePath).Length
                    : -1L;
                var alreadyUploaded = existingSize.HasValue && localSize >= 0 && existingSize.Value == localSize;

                if (alreadyUploaded)
                {
                    _logger.LogDebug(
                        "Episode already uploaded with matching size, skipping: {Title} ({Size} bytes)",
                        episode.Title,
                        localSize);
                }
                else
                {
                    if (existingSize.HasValue && localSize >= 0 && existingSize.Value != localSize)
                    {
                        _logger.LogInformation(
                            "Remote audio size {RemoteSize} differs from local {LocalSize} for episode '{Title}'; re-uploading",
                            existingSize.Value,
                            localSize,
                            episode.Title);
                    }

                    if (!File.Exists(episode.LocalAudioFilePath))
                    {
                        // workspace-mie2: promote to error and capture the
                        // missing-file detail so the result screen can name
                        // what didn't ship. Also probe the parent directory
                        // so the log line gives future devs a fighting chance
                        // at triaging a path mismatch.
                        var siblings = ProbeSiblingFiles(episode.LocalAudioFilePath);
                        _logger.LogError(
                            "Audio file not found: {Path}. Directory contents: {Siblings}",
                            episode.LocalAudioFilePath,
                            siblings);
                        skipped.Add(new SkippedEpisodeDetail
                        {
                            Title = episode.Title,
                            MissingPath = episode.LocalAudioFilePath,
                            Reason = "Audio file missing on disk at the path the assembler reported.",
                        });
                        continue;
                    }

                    // workspace-74zy: feed byte-level upload progress back through
                    // the publisher's IProgress so the orchestrator can show a
                    // moving bar during the multi-MB upload instead of going
                    // silent. UploadedBytesTotal stays pinned at the local file
                    // size so the consumer can render a real percent.
                    var localFileSize = new FileInfo(episode.LocalAudioFilePath).Length;
                    IProgress<long>? bytesProgress = null;
                    if (progress is not null)
                    {
                        var publisherProgress = progress;
                        var currentTotal = episodes.Count;
                        var currentUploaded = episodesUploaded;
                        var currentTitle = episode.Title;
                        bytesProgress = new SyncProgress<long>(bytesSent => publisherProgress.Report(new PublishProgress
                        {
                            TotalEpisodes = currentTotal,
                            UploadedEpisodes = currentUploaded,
                            UploadedBytes = bytesSent,
                            UploadedBytesTotal = localFileSize,
                            Message = $"Uploading '{currentTitle}'",
                        }));
                    }

                    await _storage.UploadAsync(
                        episode.LocalAudioFilePath,
                        objectPath,
                        "audio/x-m4a",
                        bytesProgress,
                        cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation("Uploaded episode: {Title}", episode.Title);
                    episodesUploaded++;

                    // Coarse per-episode tick — fires once on success so consumers
                    // can update "N of M episodes uploaded" copy even when the
                    // storage client surfaces no byte-level signal.
                    progress?.Report(new PublishProgress
                    {
                        TotalEpisodes = episodes.Count,
                        UploadedEpisodes = episodesUploaded,
                        UploadedBytes = localFileSize,
                        UploadedBytesTotal = localFileSize,
                        Message = $"Uploaded '{episode.Title}'",
                    });
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
                    AudioMimeType = "audio/x-m4a",
                    Chapters = episode.Chapters,
                    SourceUrl = episode.SourceUrl,
                });
            }

            // workspace-mie2: if every requested episode was skipped because
            // the local audio file was missing, fail loudly instead of
            // overwriting feed.xml with stale-or-empty metadata. The user
            // would otherwise see "Podcast Ready!" pointing at a feed that
            // contains nothing new.
            if (skipped.Count > 0 && skipped.Count == episodes.Count)
            {
                _logger.LogError(
                    "Aborting publish: all {Count} requested episodes were missing their local audio files — assembler likely failed silently or temp cleanup ran early",
                    skipped.Count);
                return FeedPublishResult.Failure(
                    $"All {skipped.Count} episode(s) skipped — audio files missing on disk",
                    FeedPublishFailureClass.NoAudioFiles,
                    skipped);
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
                cancellationToken).ConfigureAwait(false);

            // Step 6: Upload feed.xml
            await _storage.UploadStringAsync(
                feedXml,
                feedObjectPath,
                "application/rss+xml",
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Published feed: {FeedUrl}", feedUrl);

            // Step 7: Update manifest and feed index
            await UpdateManifestAsync(feedBasePath, podcast, mergedEpisodes, cancellationToken).ConfigureAwait(false);
            await UpdateFeedIndexAsync(podcast.Title, feedUuid, feedUrl, cancellationToken).ConfigureAwait(false);

            // workspace-nb6b: anonymous HTTP GET of feed.xml so we catch any
            // remaining "bytes-on-disk-don't-match-what-the-internet-sees"
            // class of bug BEFORE we tell the user "Podcast Ready!". Same
            // probe Apple Podcasts and Overcast run. Three checks:
            // (1) HTTP 200, (2) Content-Type is RSS/XML, (3) body parses as
            // XML. A failure flips the result to a typed FeedPublishFailureClass
            // and the result screen renders specific remediation.
            var probe = await _reachability.CheckAsync(feedUrl, cancellationToken).ConfigureAwait(false);

            // workspace-p1px: when the bucket is private (probe routed
            // BucketNotPublic), try to flip allUsers:objectViewer in-process
            // before bothering the user. If the SA has setIamPolicy, this
            // saves the user a trip to Cloud Console; if not, we still
            // surface the gsutil one-liner via the remediation copy below.
            if (probe.FailureClass == FeedPublishFailureClass.BucketNotPublic
                && !string.IsNullOrWhiteSpace(_storage.BucketName))
            {
                _logger.LogInformation(
                    "Reachability probe returned 403 on {Bucket}; attempting auto-remediation",
                    _storage.BucketName);
                var remediation = await _storage
                    .MakeBucketPublicAsync(_storage.BucketName, cancellationToken)
                    .ConfigureAwait(false);

                if (remediation.Status is MakeBucketPublicStatus.Success
                    or MakeBucketPublicStatus.AlreadyPublic)
                {
                    _logger.LogInformation(
                        "Auto-remediation result on {Bucket}: {Status}. Re-probing with IAM-propagation backoff.",
                        _storage.BucketName,
                        remediation.Status);

                    // workspace-i2vl: re-probe with backoff so a Storage Admin
                    // user whose bucket was previously private gets the silent
                    // success path, not "gsutil hint + press r" — the IAM write
                    // has happened but the public edge can lag a few seconds.
                    for (var attempt = 0; attempt < PropagationBackoff.Length; attempt++)
                    {
                        await _propagationDelay(PropagationBackoff[attempt], cancellationToken)
                            .ConfigureAwait(false);
                        probe = await _reachability.CheckAsync(feedUrl, cancellationToken)
                            .ConfigureAwait(false);
                        if (probe.FailureClass == FeedPublishFailureClass.None)
                        {
                            _logger.LogInformation(
                                "Public read confirmed on {Bucket} after attempt {Attempt}",
                                _storage.BucketName,
                                attempt + 1);
                            break;
                        }
                    }

                    if (probe.FailureClass != FeedPublishFailureClass.None)
                    {
                        _logger.LogWarning(
                            "IAM propagation backoff exhausted ({Attempts} attempts) on {Bucket}; surfacing gsutil hint",
                            PropagationBackoff.Length,
                            _storage.BucketName);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Auto-remediation could not make {Bucket} public ({Status}): {Message}",
                        _storage.BucketName,
                        remediation.Status,
                        remediation.ErrorMessage ?? "(no message)");
                }
            }

            if (probe.FailureClass != FeedPublishFailureClass.None)
            {
                _logger.LogError(
                    "Post-publish reachability probe failed at {FailureClass}: {Diagnostic}",
                    probe.FailureClass,
                    probe.Diagnostic);
                return FeedPublishResult.Failure(probe.Diagnostic, probe.FailureClass, skipped);
            }

            if (skipped.Count > 0)
            {
                _logger.LogWarning(
                    "Publish completed with {SkipCount} skipped episode(s); {UploadCount} uploaded",
                    skipped.Count,
                    episodesUploaded);
                return FeedPublishResult.Partial(feedUrl, episodesUploaded, skipped);
            }

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
            var existingUrl = await GetExistingFeedUrlAsync(podcast.Title, cancellationToken).ConfigureAwait(false);
            if (existingUrl != null)
            {
                _logger.LogInformation("Existing feed found for '{Title}': {Url}", podcast.Title, existingUrl);
                return FeedPublishResult.Successful(existingUrl, 0);
            }

            _logger.LogInformation("Bootstrapping empty feed for '{Title}'", podcast.Title);

            // Get or create feed UUID
            var feedUuid = await GetOrCreateFeedUuidAsync(podcast.Title, cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);

            // Upload feed
            await _storage.UploadStringAsync(
                feedXml,
                feedObjectPath,
                "application/rss+xml",
                cancellationToken).ConfigureAwait(false);

            // Update manifest and feed index
            await UpdateManifestAsync(feedBasePath, podcast, [], cancellationToken).ConfigureAwait(false);
            await UpdateFeedIndexAsync(podcast.Title, feedUuid, feedUrl, cancellationToken).ConfigureAwait(false);

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

        var index = await LoadFeedIndexAsync(cancellationToken).ConfigureAwait(false);
        if (index.TryGetValue(title, out var entry))
        {
            return entry.FeedUrl;
        }

        return null;
    }

    public async Task<string> ResolveFeedUrlAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);

        var existing = await GetExistingFeedUrlAsync(title, cancellationToken).ConfigureAwait(false);
        if (existing != null)
        {
            return existing;
        }

        // No feed has been published yet — derive the URL the next publish
        // WILL use. workspace-zh3u: matches GetOrCreateFeedUuidAsync exactly so
        // the progress-screen footer shows the same URL the publisher
        // eventually writes to.
        var feedUuid = await GetOrCreateFeedUuidAsync(title, cancellationToken).ConfigureAwait(false);
        return _storage.GetPublicUrl($"podcasts/{feedUuid}/feed.xml");
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

    /// <summary>
    /// Returns a short summary of the audio files alongside the missing audio
    /// path so the error log gives a fighting chance at triaging a path
    /// mismatch (workspace-mie2). Looks for both .m4a (current) and .m4b
    /// (legacy) so stale outputs from older builds are still surfaced.
    /// </summary>
    private static string ProbeSiblingFiles(string missingPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(missingPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return "(parent directory does not exist)";
            }

            var siblings = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(p => p.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(".m4b", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return siblings.Length == 0
                ? "(no .m4a/.m4b files in parent directory)"
                : string.Join(", ", siblings.Select(Path.GetFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"(directory probe failed: {ex.Message})";
        }
    }

    private async Task<string> GetOrCreateFeedUuidAsync(string title, CancellationToken cancellationToken)
    {
        var index = await LoadFeedIndexAsync(cancellationToken).ConfigureAwait(false);
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
        var json = await _storage.DownloadStringAsync(FeedIndexPath, cancellationToken).ConfigureAwait(false);
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
        var index = await LoadFeedIndexAsync(cancellationToken).ConfigureAwait(false);
        index[title] = new FeedIndexEntry { Uuid = uuid, FeedUrl = feedUrl };

        var json = JsonSerializer.Serialize(index, JsonOptions);
        await _storage.UploadStringAsync(json, FeedIndexPath, "application/json", cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<EpisodeMetadata>> LoadExistingEpisodesAsync(
        string feedBasePath,
        CancellationToken cancellationToken)
    {
        var json = await _storage.DownloadStringAsync($"{feedBasePath}/manifest.json", cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
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
