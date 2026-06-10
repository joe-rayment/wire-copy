// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.Entities.Bookmarks;

namespace WireCopy.Infrastructure.Bookmarks;

/// <summary>
/// Reconciles the bookmarks.json config file (source of truth) against the DB
/// mirror. Replaces the legacy <c>SeedDefaultsAsync</c> bootstrap path.
/// </summary>
public sealed class BookmarkReconciler : IBookmarkReconciler
{
    private readonly IBookmarkConfigStore _configStore;
    private readonly IBookmarkRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<BookmarkReconciler> _logger;

    public BookmarkReconciler(
        IBookmarkConfigStore configStore,
        IBookmarkRepository repository,
        IUnitOfWork unitOfWork,
        ILogger<BookmarkReconciler> logger)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        // Step 1: ensure the user config file exists. Existing-user upgrades
        // (DB has bookmarks but no file) export the DB to the file FIRST so we
        // never reconcile against an empty config.
        var existingDbBookmarks = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var configExisted = _configStore.UserConfigExists();

        if (!configExisted)
        {
            if (existingDbBookmarks.Count > 0)
            {
                _logger.LogInformation(
                    "No bookmarks.json found but DB has {Count} bookmarks; exporting DB to {Path} (upgrade migration).",
                    existingDbBookmarks.Count,
                    _configStore.UserConfigPath);
                await _configStore.SaveUserConfigAsync(existingDbBookmarks, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation(
                    "No bookmarks.json found and DB is empty; copying shipped defaults to {Path}.",
                    _configStore.UserConfigPath);
                var shipped = await _configStore.LoadShippedDefaultsAsync(cancellationToken).ConfigureAwait(false);
                await _configStore.SaveUserConfigAsync(shipped, cancellationToken).ConfigureAwait(false);
            }
        }

        // Step 2: load the (now-guaranteed-present) user config. Shipped
        // defaults SEED a missing config (step 1) and are never merged into an
        // existing one (workspace-kt19.3): the user's file is theirs — changing
        // the shipped set (e.g. to the demo bookmarks) must not inject entries
        // into configs that already exist.
        var mergedConfig = await _configStore.LoadUserConfigAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"User config at '{_configStore.UserConfigPath}' could not be loaded after first-run setup.");

        // Step 3: align the DB to mergedConfig.
        // - For each entry: ensure DB has a row with that URL (add if missing).
        // - If name differs, rename in DB.
        // - Sort order in DB matches array index in mergedConfig.
        // - Bookmarks present in DB but not in mergedConfig are LEFT ALONE
        //   (these are user customizations added via the app; they live below
        //   the configured ones in sort order).
        var dbByUrl = (await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(b => b.Url, b => b, StringComparer.OrdinalIgnoreCase);

        var configUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirty = false;

        for (var i = 0; i < mergedConfig.Bookmarks.Count; i++)
        {
            var entry = mergedConfig.Bookmarks[i];
            configUrls.Add(entry.Url);

            if (dbByUrl.TryGetValue(entry.Url, out var existing))
            {
                if (!string.Equals(existing.Name, entry.Name, StringComparison.Ordinal))
                {
                    existing.Rename(entry.Name);
                    await _repository.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
                    dirty = true;
                }

                if (existing.SortOrder != i)
                {
                    existing.SetSortOrder(i);
                    await _repository.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
                    dirty = true;
                }
            }
            else
            {
                var newBookmark = Bookmark.Create(entry.Name, entry.Url, i);
                await _repository.AddAsync(newBookmark, cancellationToken).ConfigureAwait(false);
                dirty = true;
            }
        }

        // Custom user-added bookmarks (in DB, not in config): re-number them so
        // they sit immediately after the configured ones, preserving their
        // relative order. We never delete them.
        var customs = dbByUrl.Values
            .Where(b => !configUrls.Contains(b.Url))
            .OrderBy(b => b.SortOrder)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var nextSortOrder = mergedConfig.Bookmarks.Count;
        foreach (var custom in customs)
        {
            if (custom.SortOrder != nextSortOrder)
            {
                custom.SetSortOrder(nextSortOrder);
                await _repository.UpdateAsync(custom, cancellationToken).ConfigureAwait(false);
                dirty = true;
            }

            nextSortOrder++;
        }

        if (dirty)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
