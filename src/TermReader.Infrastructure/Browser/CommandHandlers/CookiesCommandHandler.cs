// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles the <c>:cookies</c> slash command and its subcommands
/// (<c>status</c>, <c>import</c>, <c>clear</c>).
///
/// <para>
/// Background: paywalled sites like NYT only get pre-loaded by the background
/// HTTP fetcher when <c>cookies.json</c> exists at
/// <c>{LocalAppData}/TermReader/cookies.json</c>. The credential-driven
/// AutoLoginService writes that file automatically, but a manual login via the
/// foreground browser does not. This command lets the user copy session
/// cookies from the live Playwright context into <c>cookies.json</c> so the
/// HTTP preloader can authenticate.
/// </para>
/// </summary>
internal static class CookiesCommandHandler
{
    public static async Task HandleCookiesCommand(
        CommandContext ctx, string? subcommand, RenderOptions options, CancellationToken ct)
    {
        var sub = (subcommand ?? "status").Trim().ToLowerInvariant();

        switch (sub)
        {
            case "status":
            case "":
                await HandleStatus(ctx, options, ct).ConfigureAwait(false);
                return;

            case "import":
            case "refresh":
                await HandleImport(ctx, options, ct).ConfigureAwait(false);
                return;

            case "clear":
                await HandleClear(ctx, options, ct).ConfigureAwait(false);
                return;

            default:
                ctx.NavigationService.SetStatusMessage(
                    $"Unknown :cookies subcommand '{sub}'. Use status | import | clear.");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
        }
    }

    private static async Task HandleStatus(
        CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var cookieManager = scope.ServiceProvider.GetRequiredService<ICookieManager>();
            var browserConfig = scope.ServiceProvider.GetRequiredService<IOptions<BrowserConfiguration>>().Value;

            var info = await cookieManager.GetCookieInfoAsync().ConfigureAwait(false);

            if (info == null || !info.Exists)
            {
                ctx.NavigationService.SetStatusMessage(
                    "No cookies stored. Log into a paywalled site in the foreground browser, then run :cookies import.");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            var ageText = info.CreatedAt.HasValue
                ? FormatAge(DateTime.UtcNow - info.CreatedAt.Value)
                : "unknown age";
            string expiryText;
            if (!info.ExpiresAt.HasValue)
            {
                expiryText = "no expiry";
            }
            else if (info.IsExpired)
            {
                expiryText = $"EXPIRED {FormatAge(DateTime.UtcNow - info.ExpiresAt.Value)} ago";
            }
            else
            {
                expiryText = $"expires in {FormatAge(info.ExpiresAt.Value - DateTime.UtcNow)}";
            }

            // Per-domain breakdown when we can decrypt.
            var perDomain = string.Empty;
            try
            {
                var cookies = await cookieManager.LoadCookiesAsync().ConfigureAwait(false);
                var domains = browserConfig.PaywalledDomains;
                if (domains.Length > 0)
                {
                    var counts = domains
                        .Select(d => new
                        {
                            Domain = d,
                            Count = cookies.Count(c =>
                                c.Domain.TrimStart('.').Equals(d, StringComparison.OrdinalIgnoreCase) ||
                                c.Domain.TrimStart('.').EndsWith("." + d, StringComparison.OrdinalIgnoreCase) ||
                                d.EndsWith("." + c.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase)),
                        })
                        .ToList();

                    perDomain = " | " + string.Join(
                        ", ",
                        counts.Select(c => $"{c.Domain}: {c.Count}"));
                }
            }
            catch (Exception ex)
            {
                ctx.Logger.LogDebug(ex, "Per-domain cookie tally failed");
            }

            var msg = $"Cookies: {info.CookieCount ?? 0} total, age {ageText}, {expiryText}{perDomain}";
            ctx.NavigationService.SetStatusMessage(msg);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to read cookie status");
            ctx.NavigationService.SetStatusMessage($"Cookie status failed: {ex.Message}");
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static async Task HandleImport(
        CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var browserSession = scope.ServiceProvider.GetRequiredService<IBrowserSession>();
            var cookieManager = scope.ServiceProvider.GetRequiredService<ICookieManager>();
            var httpRefresher = scope.ServiceProvider.GetRequiredService<IHttpCookieRefresher>();
            var browserConfig = scope.ServiceProvider.GetRequiredService<IOptions<BrowserConfiguration>>().Value;

            if (!browserSession.HasBrowserContext)
            {
                ctx.NavigationService.SetStatusMessage(
                    "Browser session not active. Open a paywalled site (e.g. nytimes.com) and log in first.");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            var domains = browserConfig.PaywalledDomains;
            if (domains.Length == 0)
            {
                ctx.NavigationService.SetStatusMessage(
                    "No paywalled domains configured (Browser:PaywalledDomains).");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            var aggregated = new Dictionary<(string Name, string Domain, string Path), StoredCookie>();
            var domainsWithCookies = new List<string>();

            foreach (var domain in domains)
            {
                ct.ThrowIfCancellationRequested();
                var url = $"https://{domain}/";
                IReadOnlyList<StoredCookie> domainCookies;
                try
                {
                    domainCookies = await browserSession.GetCookiesForUrlAsync(url).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex, "Failed to export cookies from browser for {Domain}", domain);
                    continue;
                }

                if (domainCookies.Count > 0)
                {
                    domainsWithCookies.Add(domain);
                }

                foreach (var c in domainCookies)
                {
                    aggregated[(c.Name, c.Domain, c.Path)] = c;
                }
            }

            if (aggregated.Count == 0)
            {
                ctx.NavigationService.SetStatusMessage(
                    "No cookies found in browser session for paywalled domains. Log in via the foreground browser first.");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            var toSave = aggregated.Values.ToList();
            await cookieManager.SaveCookiesAsync(toSave, ct).ConfigureAwait(false);

            // Refresh the HTTP CookieContainer so the background preloader picks
            // up new cookies without an app restart.
            try
            {
                await httpRefresher.RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Cookie save succeeded but HTTP refresher failed");
            }

            // Sync the captured cookies into the headless preload context so
            // pre-fetches authenticate against paywalled domains immediately —
            // no app restart required. If the preload context hasn't launched
            // yet the call is a no-op (the cookies will be loaded from
            // cookies.json the first time the preload context spins up).
            try
            {
                await browserSession.SyncCookiesToPreloadContextAsync(toSave).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Cookie save succeeded but preload-context sync failed");
            }

            var authCount = CountLikelyAuthCookies(toSave);
            var domainList = string.Join(", ", domainsWithCookies);
            var msg = $"Imported {toSave.Count} cookies ({authCount} auth) from browser session for {domainsWithCookies.Count} domain(s): {domainList}";
            ctx.NavigationService.SetStatusMessage(msg);
            ctx.Logger.LogInformation(
                "Imported {Count} cookies ({Auth} auth) from browser session for domains: {Domains}",
                toSave.Count,
                authCount,
                domainList);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to import cookies");
            ctx.NavigationService.SetStatusMessage($"Cookie import failed: {ex.Message}");
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static async Task HandleClear(
        CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var cookieManager = scope.ServiceProvider.GetRequiredService<ICookieManager>();
            var httpRefresher = scope.ServiceProvider.GetRequiredService<IHttpCookieRefresher>();

            var cleared = await cookieManager.ClearCookiesAsync().ConfigureAwait(false);

            // The HTTP CookieContainer keeps its in-memory cookies even after
            // the file is deleted. We don't expose a "clear" hook on it; the
            // user can restart the app to fully purge. Surface this honestly.
            ctx.NavigationService.SetStatusMessage(
                cleared
                    ? "Cookies cleared. Restart app to purge in-memory HTTP cookies."
                    : "No cookies file to clear.");

            // Best-effort refresh — re-loading from a now-empty store is a
            // no-op but keeps the call idempotent.
            try
            {
                await httpRefresher.RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogDebug(ex, "Refresh after clear failed (non-fatal)");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to clear cookies");
            ctx.NavigationService.SetStatusMessage($"Cookie clear failed: {ex.Message}");
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Heuristic count of cookies that look like authentication / session
    /// tokens. Used purely for the status message — never gates behavior.
    /// </summary>
    private static int CountLikelyAuthCookies(IReadOnlyList<StoredCookie> cookies)
    {
        return cookies.Count(c =>
        {
            var n = c.Name.ToLowerInvariant();
            return n.Contains("session") ||
                   n.Contains("auth") ||
                   n.Contains("token") ||
                   n.Contains("login") ||
                   n.Contains("nyt-s") ||
                   n.Contains("nyt-a") ||
                   n.StartsWith("sid", StringComparison.Ordinal);
        });
    }

    private static string FormatAge(TimeSpan span)
    {
        if (span.TotalSeconds < 0)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h";
        }

        if (span.TotalMinutes >= 1)
        {
            return $"{(int)span.TotalMinutes}m";
        }

        return $"{(int)span.TotalSeconds}s";
    }
}
