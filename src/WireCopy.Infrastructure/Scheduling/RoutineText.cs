// Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Scheduling;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-wyvga.9 (S3): every user-visible Routines string. Never write these inline elsewhere.
/// </summary>
public static class RoutineText
{
    /// <summary>Launcher footer hint while choosing a site to add to a routine.</summary>
    public const string AddSiteFooter = "Enter: pick this site for the routine · Esc: back to the routine";

    /// <summary>Status shown when Enter lands on the Reading List / Routines cell while adding a site.</summary>
    public const string AddSiteNotASite = "That's not a site — pick a site tile or type a web address.";

    public const string HomeStub = "Routines home isn't built yet";
    public const string EditorStub = "Routine editor isn't built yet";
    public const string PickStub = "Pick mode isn't built yet";
    public const string CardRunning = "running…";
    public const string Busy = "Another podcast is generating — try again when it finishes";
    public const string NothingToCollect = "Nothing to collect yet";
    public const string WholeSectionAlready = "Whole section already included — Space on the header removes it";
    public const string StoryHint = "Space adds or removes this story · Esc done";
    public const string HeaderOnlyYet = "Space on the section header adds the whole section";
    public const string NoSection = "This story isn't in a section the routine can follow";
    public const string CannotPlace = "Can't place this story — the routine wouldn't find it in its section";
    public const string NameEmpty = "Name can't be empty";
    public const string NameTaken = "A routine with that name already exists";
    public const string NameEditHint = "type a name · Enter done · Esc keep";
    public const string PodcastEditHint = "type the podcast name · Enter done · Esc keep";
    public const string TimeEditHint = "type a time like 07:00 · Enter done · Esc keep";
    public const string PodcastEmpty = "Podcast name can't be empty";
    public const string TimeInvalid = "Enter a time like 07:00";
    public const string DefaultRoutineName = "New routine";
    public const string HomeFooter = "[Enter]:open  [n]:new  [r]:run now  [e]:last run  [Space]:on/off  [d]:delete  [Esc]:back";
    public const string FixedRunAgainFormat = "Saved. Press r to run {0} again.";

    /// <summary>Status while the fix row looks for audio-engine.</summary>
    public const string LookingForAudioEngine = "Looking for audio-engine…";

    /// <summary>Status while the native folder window for choosing the audio-engine folder is open.</summary>
    public const string ChooseAudioEngineFolder = "Choose the audio-engine folder in the window that just opened…";

    /// <summary>Status while the fix row starts audio-engine (a first start can take a minute or more).</summary>
    public const string StartingAudioEngine = "Starting audio-engine…";

    /// <summary>Fix result when the person closed the location prompt without choosing.</summary>
    public const string NothingChosen = "Nothing chosen — WireCopy still can't find audio-engine.";
    public const string RunNowRow = "Run now";
    public const string RunAgainRow = "Run again";
    public const string OpenLogRow = "Open the full app log";
    public const string DetailsRunning = "running…";
    public const string NeverRanYet = "This routine hasn't run yet.";
    public const string NoLogKept = "No log was kept for this run.";
    public const string SavedTo = "Saved to";
    public const string FeedLabel = "Feed";
    public const string WhatHappened = "What happened";
    public const string AtStep = "At step";
    public const string HowToFix = "How to fix";
    public const string SectionsHeading = "Sections";
    public const string LogHeading = "Log";
    public const string EarlierRunsHeading = "Earlier runs";
    public const string DetailsFooter = "Enter select · r run again · l full log · ↑/↓ move · PgUp/PgDn scroll · Esc back";
    public const string LastRunRow = "Last run";
    public const string LastRunAction = "details";
    public const string NeverRunValue = "never run";

    public const string EditorFooter = "Enter edit · x remove · r run now · Esc back";
    public const string NewRoutineTitle = "+ New routine";
    public const string NewRoutineHint = "Enter to create";
    public const string RoutinesTitle = "ROUTINES";
    public const string HomeTitle = "Routines";
    public const string HomeEyebrow = "ROUTINES";

    private static readonly DayOfWeek[] MonToSun =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    };

    /// <summary>"Daily · 05:00", "Weekdays · 05:00", "Weekends · 05:00" or "Mon, Wed, Fri · 05:00".</summary>
    public static string Frequency(Cadence cadence)
    {
        ArgumentNullException.ThrowIfNull(cadence);
        var days = cadence.Days;
        string label;
        if (days.Count == 7)
        {
            label = "Daily";
        }
        else if (days.Count == 5 && MonToSun.Take(5).All(days.Contains))
        {
            label = "Weekdays";
        }
        else if (days.Count == 2 && days.Contains(DayOfWeek.Saturday) && days.Contains(DayOfWeek.Sunday))
        {
            label = "Weekends";
        }
        else
        {
            label = string.Join(", ", MonToSun.Where(days.Contains).Select(ShortDay));
        }

        return $"{label} · {Hm(cadence.LocalTime)}";
    }

    /// <summary>"next today 05:00" — uses <see cref="NextDueCalculator"/>.</summary>
    public static string NextRun(Cadence cadence, DateTimeOffset nowLocal)
    {
        var next = NextDueCalculator.Decide(cadence, RecipeRunState.Initial, nowLocal).NextAt;
        return next is { } at ? $"next {DayWord(at.DateTime, nowLocal.DateTime)}" : "next —";
    }

    /// <summary>"all" | "top 5" | "first story" | "#1, #3".</summary>
    public static string Take(RecipeStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.TakeMode switch
        {
            TakeMode.TopN => $"top {step.TakeCount ?? 1}",
            TakeMode.SingleTopStory => "first story",
            TakeMode.Slots => Slots(step.Positions),
            _ => "all",
        };
    }

    /// <summary>"○ not included" | "● all 9" | "● top 5" | "● first story" | "● #1, #3".</summary>
    public static string Chip(SectionPick? pick, int sectionCount)
    {
        if (pick is null)
        {
            return "○ not included";
        }

        return pick.TakeMode switch
        {
            TakeMode.TopN => $"● top {pick.TakeCount ?? 1}",
            TakeMode.SingleTopStory => "● first story",
            TakeMode.Slots => "● " + Slots(pick.Positions),
            _ => $"● all {sectionCount}",
        };
    }

    /// <summary>The default name for a freshly created routine: "New routine", then "New routine 2", "New routine 3", …</summary>
    public static string UniqueDefaultName(IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var n = 1;
        var name = DefaultRoutineName;
        while (taken.Contains(name))
        {
            n++;
            name = $"{DefaultRoutineName} {n}";
        }

        return name;
    }

    public static string SlotBadge(int position, string sectionName) => $"#{position} · {sectionName}";

    /// <summary>"nothing yet" | "1 section · 1 site" | "3 sections · 2 sites".</summary>
    public static string Contents(ScheduleRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var steps = recipe.Steps;
        if (steps.Count == 0)
        {
            return "nothing yet";
        }

        var sites = steps.Select(s => s.Domain).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return $"{steps.Count} {(steps.Count == 1 ? "section" : "sections")} · {sites} {(sites == 1 ? "site" : "sites")}";
    }

    /// <summary>
    /// "never run" | "last: ✓ today 05:00" | "last: ⚠ partial yesterday 05:00" | "last: ✗ failed Tue 05:00"
    /// | "last: skipped Tue 05:00". <paramref name="nowLocal"/> defaults to the local clock.
    /// </summary>
    public static string LastResult(ScheduleRecipe recipe, DateTimeOffset? nowLocal = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var state = recipe.RunState;
        if (state.LastStatus == RunStatus.Never)
        {
            return "never run";
        }

        var now = (nowLocal ?? DateTimeOffset.Now).DateTime;
        var key = state.LastRunOccurrenceKey ?? string.Empty;
        var hasSlot = DateTime.TryParseExact(
            key, "yyyy-MM-dd@HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var slot);
        if (!hasSlot && key.StartsWith("run-now@", StringComparison.Ordinal))
        {
            // Manual runs carry a UTC "run-now@yyyy-MM-ddTHH:mm:ss#seq" key; show it in local time.
            var stamp = key["run-now@".Length..];
            var hash = stamp.IndexOf('#');
            if (hash >= 0)
            {
                stamp = stamp[..hash];
            }

            if (DateTime.TryParseExact(stamp, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
            {
                slot = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
                hasSlot = true;
            }
        }

        var suffix = hasSlot ? " " + DayWord(slot, now) : string.Empty;
        return state.LastStatus switch
        {
            RunStatus.Success => $"last: ✓{suffix}",
            RunStatus.PartialSuccess => $"last: ⚠ partial{suffix}",
            RunStatus.Failed => $"last: ✗ failed{suffix}",
            RunStatus.Skipped => $"last: skipped{suffix}",
            _ => "never run",
        };
    }

    /// <summary>"none yet — Enter to create" | "1 routine · next Morning brief Tue 05:00" | "3 routines · all off".</summary>
    public static string LauncherHint(IReadOnlyList<ScheduleRecipe> recipes, DateTimeOffset nowLocal)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        if (recipes.Count == 0)
        {
            return "none yet — Enter to create";
        }

        var count = $"{recipes.Count} {(recipes.Count == 1 ? "routine" : "routines")}";
        (ScheduleRecipe Recipe, DateTimeOffset At)? best = null;
        foreach (var r in recipes.Where(r => r.Enabled))
        {
            if (NextDueCalculator.Decide(r.Cadence, RecipeRunState.Initial, nowLocal).NextAt is { } at
                && (best is null || at < best.Value.At))
            {
                best = (r, at);
            }
        }

        return best is { } b
            ? $"{count} · next {b.Recipe.Name} {DayWord(b.At.DateTime, nowLocal.DateTime)}"
            : $"{count} · all off";
    }

    public static string PickFooter(int sectionsIncluded, int slotsIncluded)
    {
        const string keys = "Space add/remove · Esc done";
        if (sectionsIncluded == 0 && slotsIncluded == 0)
        {
            return $"{keys} — nothing from this site yet";
        }

        var parts = new List<string>();
        if (sectionsIncluded > 0)
        {
            parts.Add($"{sectionsIncluded} {(sectionsIncluded == 1 ? "section" : "sections")}");
        }

        if (slotsIncluded > 0)
        {
            parts.Add($"{slotsIncluded} {(slotsIncluded == 1 ? "story" : "stories")}");
        }

        return $"{keys} — {string.Join(", ", parts)} from this site";
    }

    public static string AddedToRoutine(string name, int sectionCount) =>
        $"Added to {name} — {sectionCount} {(sectionCount == 1 ? "section" : "sections")} from this site";

    public static string Running(string name) => $"Running {name}…";

    public static string ThemeDeferred(string themeName) => $"Theme: {themeName} (applies when you leave Routines)";

    public static string DeleteConfirm(string name) => $"Delete {name}? y / n";

    public static string RemoveConfirm(string label) => $"Remove {label}? y / n";

    public static string Eyebrow(string name) => $"ROUTINE · {name}";

    public static string FixedRunAgain(string name) => string.Format(CultureInfo.InvariantCulture, FixedRunAgainFormat, name);

    public static string NarrationReady(string name) => $"Narration is ready. Press r to run {name} again.";

    /// <summary>Fix result once audio-engine answers; <paramref name="startedFrom"/> may be null when unknown.</summary>
    public static string AudioEngineRunning(string name, string? startedFrom)
        => $"audio-engine is running{(string.IsNullOrWhiteSpace(startedFrom) ? string.Empty : $" (started from {startedFrom})")}. Press r to run {name} again.";

    /// <summary>Fix result when audio-engine now answers but narration still needs something else.</summary>
    /// <param name="startedFrom">Where it was started from; null or blank when unknown.</param>
    /// <param name="stillNeeded">The remaining problem, as the preflight worded it.</param>
    /// <returns>The status line.</returns>
    public static string AudioEngineRunningButNotReady(string? startedFrom, string stillNeeded)
        => $"audio-engine is running now{(string.IsNullOrWhiteSpace(startedFrom) ? string.Empty : $" (started from {startedFrom})")}. Still needed: {stillNeeded}";

    /// <summary>Fourth line on a Routines-home card whose newest run needs attention (⚠ for a partial success, ✗ otherwise).</summary>
    public static string CardAlert(string shortReason, bool partial) => $"{(partial ? "⚠" : "✗")} {shortReason} — e for details";

    /// <summary>Where the badge points: the launcher says where to go, the Routines home says what to press.</summary>
    public static string BadgeSuffix(bool onRoutinesHome, bool several)
    {
        if (!onRoutinesHome)
        {
            return " — open :routines";
        }

        return several ? " — press e on each" : " — press e on it";
    }

    public static string BadgeFailed(string name, string shortReason, string suffix) => $"⚠ {name} failed: {shortReason}{suffix}";

    public static string BadgeFailedOnHome(string name, string suffix) => $"⚠ {name} failed{suffix}";

    public static string BadgeRecovered(string name, string suffix) => $"⚠ {name}: a routine run used a re-derived section{suffix}";

    public static string BadgeDegraded(string name, string suffix) => $"⚠ {name}: routine run published with a degraded source{suffix}";

    public static string BadgeSeveral(int count, string suffix) => $"⚠ {count} routines need attention{suffix}";

    /// <summary>Editor "Last run" row value: glyph + status word + when, plus the short reason when it needs attention.</summary>
    public static string LastRunValue(ScheduledRun? run, string? shortReason, DateTimeOffset nowLocal)
    {
        if (run is null)
        {
            return "never run";
        }

        var word = run.Status switch
        {
            ScheduledRunStatus.Completed => "✓",
            ScheduledRunStatus.PartialSuccess => "⚠ partial",
            ScheduledRunStatus.Failed or ScheduledRunStatus.Interrupted => "✗ failed",
            ScheduledRunStatus.Skipped => "skipped",
            _ => "running",
        };
        var text = $"{word} {When(run.StartedAtUtc, nowLocal)}";
        return string.IsNullOrEmpty(shortReason) ? text : $"{text} — {shortReason}";
    }

    public static string DetailsEyebrow(string name) => $"ROUTINE · {name} · LAST RUN";

    /// <summary>Local "today 19:16" for a UTC start time.</summary>
    public static string When(DateTime startedAtUtc, DateTimeOffset nowLocal)
    {
        var local = DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc).ToLocalTime();
        return DayWord(local, nowLocal.DateTime);
    }

    public static string Took(TimeSpan d) =>
        d.TotalSeconds < 60 ? $"{Math.Max(0, (int)d.TotalSeconds)}s" : $"{(int)d.TotalMinutes}m {d.Seconds:00}s";

    private static string Slots(IEnumerable<int> positions) => string.Join(", ", positions.Select(p => $"#{p}"));

    private static string Hm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string ShortDay(DayOfWeek d) => d.ToString()[..3];

    /// <summary>S3 day-word rule, always followed by " HH:mm" (local 24h).</summary>
    private static string DayWord(DateTime target, DateTime now)
    {
        var diff = (target.Date - now.Date).Days;
        string word;
        if (diff == 0)
        {
            word = "today";
        }
        else if (diff == 1)
        {
            word = "tomorrow";
        }
        else if (diff == -1)
        {
            word = "yesterday";
        }
        else if (Math.Abs(diff) <= 6)
        {
            word = ShortDay(target.DayOfWeek);
        }
        else
        {
            word = target.ToString("MMM d", CultureInfo.InvariantCulture);
        }

        return $"{word} {target.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }
}
