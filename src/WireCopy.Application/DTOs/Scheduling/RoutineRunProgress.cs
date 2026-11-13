// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Scheduling;

/// <summary>State of one stage of an in-flight routine run.</summary>
public enum RoutineRunStageState
{
    Waiting,
    Running,
    Done,
    Failed,
}

/// <summary>One stage of an in-flight routine run (narration, a step, narrate, publish).</summary>
public sealed record RoutineRunStage
{
    /// <summary>"narration", "step:0", "step:1", …, "narrate" or "publish".</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    public RoutineRunStageState State { get; init; }

    public string? Hint { get; init; }

    public string? Detail { get; init; }
}

/// <summary>Live progress of one in-flight routine run, shown by any screen.</summary>
public sealed record RoutineRunProgress
{
    public required Guid RecipeId { get; init; }

    public required Guid RunId { get; init; }

    public required string RoutineName { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required IReadOnlyList<RoutineRunStage> Stages { get; init; }

    public int StoriesDone { get; init; }

    public int StoriesTotal { get; init; }

    public bool CancelRequested { get; init; }
}
