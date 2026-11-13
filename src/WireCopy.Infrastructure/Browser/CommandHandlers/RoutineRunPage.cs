// Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>Builds the launcher-tile screens for one routine run: running, finished, never ran (workspace-6ifpp.3).</summary>
internal static class RoutineRunPage
{
    public static TileScreen Running(ScheduleRecipe recipe, RoutineRunProgress progress, DateTimeOffset now)
    {
        var started = DateTime.SpecifyKind(progress.StartedAtUtc.UtcDateTime, DateTimeKind.Utc).ToLocalTime();
        var elapsed = RoutineText.Took(now.UtcDateTime - progress.StartedAtUtc.UtcDateTime);
        var stages = progress.Stages.ToList();
        var cells = stages.Select(StageCell).ToList();

        int selected;
        var running = stages.FindIndex(s => s.State == RoutineRunStageState.Running);
        if (running >= 0)
        {
            selected = running;
        }
        else
        {
            selected = Math.Max(0, stages.FindLastIndex(s => s.State == RoutineRunStageState.Done));
        }

        return new TileScreen
        {
            Screen = "run",
            Numbered = false,
            Eyebrow = $"Routines › {recipe.Name} › Run",
            Title = progress.CancelRequested ? "Stopping…" : "Running…",
            Tagline = $"Started {started.ToString("HH:mm", CultureInfo.InvariantCulture)} · {elapsed} so far",
            CountLabel = progress.StoriesTotal > 0 ? $"{progress.StoriesDone} of {progress.StoriesTotal} stories" : string.Empty,
            Cells = cells,
            Columns = 3,
            Selected = selected,
            AppLabel = "Run",
            Hints = new List<LauncherHint>
            {
                new() { Key = "x", Label = "cancel" },
                new() { Key = "Esc", Label = "leave it running" },
                new() { Key = "l", Label = "log" },
            },
        };
    }

    public static TileScreen Finished(
        ScheduleRecipe recipe, ScheduledRun run, IReadOnlyList<ScheduledRun> earlier, bool isLatest, DateTimeOffset now, int selected, string? hintRow = null)
    {
        var diagnosis = RoutineRunDiagnosis.Diagnose(run);
        var success = run.Status is ScheduledRunStatus.Completed or ScheduledRunStatus.PartialSuccess;
        var hasFeed = !string.IsNullOrWhiteSpace(run.TargetFeedUrl);
        var steps = RoutineRunDiagnosis.Steps(run);
        var fileName = FileLabel(run.TargetLocalPath);
        var again = new Tile { Id = "again", Title = "RUN AGAIN", Hint = "same sections, now" };

        var firstIsAgain = false;
        Tile first;
        if (success)
        {
            first = new Tile { Id = "play", Title = "PLAY EPISODE", Hint = Path.GetFileNameWithoutExtension(fileName) };
        }
        else if (diagnosis.Fix.Kind is RunFixKind.SetupRow or RunFixKind.OpenSite or RunFixKind.OpenEngineSettings)
        {
            first = new Tile { Id = "fix", Title = diagnosis.Fix.Label.ToUpperInvariant(), Hint = "the fix · Enter does it, then run again" };
        }
        else
        {
            first = again;
            firstIsAgain = true;
        }

        var log = new Tile { Id = "log", Title = "OPEN THE LOG", Hint = "what happened, line by line" };
        var showFile = new Tile { Id = "showFile", Title = "SHOW FILE", Hint = fileName };
        TileCell thisRun;
        if (success && hasFeed)
        {
            var copy = new Tile { Id = "copyFeed", Title = "COPY FEED URL", Hint = "paste it into your podcast app" };
            thisRun = new TileCell { Legend = "This run", Top = copy, Bottom = showFile };
        }
        else if (success)
        {
            thisRun = new TileCell { Legend = "This run", Top = again, Bottom = showFile };
        }
        else if (firstIsAgain)
        {
            thisRun = new TileCell { Legend = "This run", Top = log };
        }
        else
        {
            thisRun = new TileCell { Legend = "This run", Top = again, Bottom = log };
        }

        var sections = steps.Select((s, i) => new TileCell { Tile = SectionTile(s, i) }).ToList();
        var earlierTop = earlier.Count > 0 ? EarlierTile(earlier[0]) : null;
        var earlierBottom = earlier.Count > 1 ? EarlierTile(earlier[1]) : null;
        var earlierCell = earlierTop is null
            ? new TileCell()
            : new TileCell { Legend = "Earlier runs", Top = earlierTop, Bottom = earlierBottom };

        var cells = new List<TileCell>
        {
            new() { Tile = first },
            thisRun,
            sections.Count > 0 ? sections[0] : new TileCell(),
            earlierCell,
        };
        cells.AddRange(sections.Skip(1));

        string tagline;
        if (success)
        {
            tagline = $"{run.ItemCount} stories · took {Duration(run)}";
        }
        else
        {
            tagline = string.IsNullOrWhiteSpace(diagnosis.Short) ? diagnosis.What : diagnosis.Short;
        }

        return new TileScreen
        {
            Screen = "run",
            Eyebrow = $"Routines › {recipe.Name} › {(isLatest ? "Last run" : "Earlier run")}",
            Title = TitleFor(run),
            Tagline = tagline,
            CountLabel = $"{VerbFor(run)} {RoutineText.When(run.StartedAtUtc, now)}",
            HintRow = hintRow,
            Cells = cells,
            Columns = 2,
            Selected = selected,
            AppLabel = "Run",
            Hints = new List<LauncherHint>
            {
                new() { Key = "Enter", Label = "open" },
                new() { Key = "1-9", Label = "jump" },
                new() { Key = "r", Label = "run again" },
                new() { Key = "l", Label = "log" },
                new() { Key = "Esc", Label = "back" },
            },
        };
    }

    public static TileScreen NeverRan(ScheduleRecipe recipe)
    {
        return new TileScreen
        {
            Screen = "run",
            Eyebrow = $"Routines › {recipe.Name} › Last run",
            Title = "Not run yet",
            Tagline = "Run it now to hear it",
            Cells = new List<TileCell>
            {
                new() { Tile = new Tile { Id = "again", Title = "RUN NOW", Hint = "collect, narrate and publish" } },
            },
            Columns = 2,
            Selected = 0,
            AppLabel = "Run",
            Hints = new List<LauncherHint>
            {
                new() { Key = "Enter", Label = "run now" },
                new() { Key = "r", Label = "run now" },
                new() { Key = "Esc", Label = "back" },
            },
        };
    }

    private static TileCell StageCell(RoutineRunStage stage)
    {
        var hint = stage.State switch
        {
            RoutineRunStageState.Done => $"✓ {stage.Hint}",
            RoutineRunStageState.Failed => $"✗ {stage.Hint}",
            RoutineRunStageState.Running => stage.Hint ?? string.Empty,
            _ => string.IsNullOrWhiteSpace(stage.Hint) ? "waiting" : $"waiting · {stage.Hint}",
        };
        return new TileCell
        {
            Tile = new Tile
            {
                Id = $"stage:{stage.Key}",
                Title = stage.Title,
                Hint = hint,
                Detail = stage.Detail,
                Disabled = stage.State == RoutineRunStageState.Waiting,
            },
        };
    }

    private static string TitleFor(ScheduledRun run)
    {
        var feed = !string.IsNullOrWhiteSpace(run.TargetFeedUrl);
        return run.Status switch
        {
            ScheduledRunStatus.Completed => feed ? "Published" : "Saved on this computer",
            ScheduledRunStatus.PartialSuccess => "Published, with gaps",
            ScheduledRunStatus.Failed => run.ErrorClass switch
            {
                "NarrationNotReady" or "GenerationFailed" => "Couldn’t narrate",
                "NoContentResolved" => "Nothing to read",
                _ => "Failed",
            },
            ScheduledRunStatus.Interrupted => run.ErrorClass == "Cancelled" ? "Stopped" : "Interrupted",
            ScheduledRunStatus.Skipped => run.ErrorClass == "MissedPastGrace" ? "Missed" : "Nothing to collect",
            _ => "Failed",
        };
    }

    private static string VerbFor(ScheduledRun run)
    {
        var feed = !string.IsNullOrWhiteSpace(run.TargetFeedUrl);
        return run.Status switch
        {
            ScheduledRunStatus.Completed => feed ? "published" : "saved",
            ScheduledRunStatus.PartialSuccess => "published",
            ScheduledRunStatus.Interrupted => "stopped",
            ScheduledRunStatus.Skipped => run.ErrorClass == "MissedPastGrace" ? "missed" : "skipped",
            _ => "failed",
        };
    }

    private static string Duration(ScheduledRun run) =>
        run.FinishedAtUtc is { } end ? RoutineText.Took(end - run.StartedAtUtc) : "unknown time";

    private static string FileLabel(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);

    private static Tile EarlierTile(ScheduledRun run)
    {
        var local = DateTime.SpecifyKind(run.StartedAtUtc, DateTimeKind.Utc).ToLocalTime();
        var when = local.ToString("ddd HH:mm", CultureInfo.InvariantCulture).ToUpperInvariant();
        string hint;
        if (run.Status is ScheduledRunStatus.Completed or ScheduledRunStatus.PartialSuccess)
        {
            var glyph = run.Status == ScheduledRunStatus.Completed ? "✓" : "⚠";
            hint = $"{glyph} {VerbFor(run)} · {run.ItemCount} stories · {Duration(run)}";
        }
        else
        {
            var d = RoutineRunDiagnosis.Diagnose(run);
            var reason = string.IsNullOrWhiteSpace(d.Short) ? d.What : d.Short;
            var verb = VerbFor(run);
            hint = verb == "failed" ? $"✗ {verb} · {reason}" : $"{verb} · {reason}";
        }

        return new Tile { Id = $"run:{run.Id}", Title = when, Hint = hint };
    }

    private static Tile SectionTile(RunStepOutcome step, int index)
    {
        var ok = step.ItemsContributed > 0 && step.Status is "Resolved" or "Recovered";
        var glyph = "✗";
        if (ok)
        {
            glyph = step.Status == "Recovered" ? "⚠" : "✓";
        }

        var host = Uri.TryCreate(step.SourceUrl, UriKind.Absolute, out var u) ? u.Host : step.SourceUrl;
        var stories = step.ItemsContributed == 1 ? "story" : "stories";
        return new Tile
        {
            Id = $"step:{index}",
            Title = step.SectionName.ToUpperInvariant(),
            Hint = $"{glyph} {step.ItemsContributed} {stories} · {host}",
            Detail = string.IsNullOrWhiteSpace(step.Diagnostic) ? null : step.Diagnostic,
        };
    }
}
