// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.Interfaces;

/// <summary>
/// Reconciles the on-disk bookmarks config file (the source of truth) with the
/// runtime DB mirror. Runs at every app start and after any in-app mutation
/// that needs to re-sync the file. Adds new shipped defaults additively, never
/// deletes user-owned bookmarks.
/// </summary>
public interface IBookmarkReconciler
{
    /// <summary>
    /// Reconciles config file -> DB:
    ///  - If the user's config file does not exist, create it from the DB
    ///    (existing-user upgrade) or from the shipped defaults (fresh install).
    ///  - For every entry in the config: ensure the DB has a bookmark with that
    ///    URL; if the name differs, update it; align sort order to file order.
    ///  - Bookmarks present in the DB but not in the config file are preserved
    ///    (they are user-owned customizations).
    ///  - New entries in the shipped defaults that are missing from the user
    ///    config are added to the user config file.
    /// </summary>
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
