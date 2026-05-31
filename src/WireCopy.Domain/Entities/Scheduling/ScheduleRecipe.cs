// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Scheduling;

namespace WireCopy.Domain.Entities.Scheduling;

/// <summary>
/// workspace-frpl.2 — a recurring "recipe": an ordered set of section/story
/// sources across one or more sites that, on a <see cref="Cadence"/>, assemble
/// into one podcast episode (B7) and publish it. Mirrors the
/// <see cref="WireCopy.Domain.Entities.Collections.Collection"/> aggregate style:
/// private setters, static <see cref="Create"/>, explicit mutation methods.
/// </summary>
public sealed class ScheduleRecipe
{
    private readonly List<RecipeStep> _steps;

    private ScheduleRecipe(
        Guid id,
        string name,
        bool enabled,
        Cadence cadence,
        List<RecipeStep> steps,
        string outputCollectionName,
        RecipeRunState runState,
        int version)
    {
        Id = id;
        Name = name;
        Enabled = enabled;
        Cadence = cadence;
        _steps = steps;
        OutputCollectionName = outputCollectionName;
        RunState = runState;
        Version = version;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public bool Enabled { get; private set; }

    public Cadence Cadence { get; private set; }

    public IReadOnlyList<RecipeStep> Steps => _steps.AsReadOnly();

    public string OutputCollectionName { get; private set; }

    public RecipeRunState RunState { get; private set; }

    public int Version { get; private set; }

    public bool HasRequiredStep => _steps.Any(s => s.Required);

    public static ScheduleRecipe Create(
        string name,
        Cadence cadence,
        IEnumerable<RecipeStep> steps,
        string? outputCollectionName = null,
        bool enabled = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Recipe name cannot be empty", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(cadence);
        var stepList = (steps ?? throw new ArgumentNullException(nameof(steps))).ToList();
        if (stepList.Count == 0)
        {
            throw new ArgumentException("A recipe must have at least one step", nameof(steps));
        }

        if (!stepList.Any(s => s.Required))
        {
            throw new ArgumentException(
                "A recipe must have at least one Required step so the run has a content guarantee", nameof(steps));
        }

        return new ScheduleRecipe(
            Guid.NewGuid(),
            name.Trim(),
            enabled,
            cadence,
            stepList,
            string.IsNullOrWhiteSpace(outputCollectionName) ? name.Trim() : outputCollectionName.Trim(),
            RecipeRunState.Initial,
            version: 1);
    }

    /// <summary>Rehydrates a persisted recipe (used by the store, B4) without re-validating identity.</summary>
    public static ScheduleRecipe Rehydrate(
        Guid id,
        string name,
        bool enabled,
        Cadence cadence,
        IEnumerable<RecipeStep> steps,
        string outputCollectionName,
        RecipeRunState runState,
        int version)
    {
        return new ScheduleRecipe(
            id, name, enabled, cadence,
            (steps ?? throw new ArgumentNullException(nameof(steps))).ToList(),
            outputCollectionName, runState ?? RecipeRunState.Initial, version);
    }

    public void Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Recipe name cannot be empty", nameof(newName));
        }

        Name = newName.Trim();
    }

    public void Enable() => Enabled = true;

    public void Disable() => Enabled = false;

    public void SetCadence(Cadence cadence) => Cadence = cadence ?? throw new ArgumentNullException(nameof(cadence));

    public void AddStep(RecipeStep step) => _steps.Add(step ?? throw new ArgumentNullException(nameof(step)));

    public void RemoveStep(int index)
    {
        EnsureIndex(index);
        _steps.RemoveAt(index);
    }

    public void MoveStepUp(int index)
    {
        EnsureIndex(index);
        if (index == 0)
        {
            return;
        }

        (_steps[index - 1], _steps[index]) = (_steps[index], _steps[index - 1]);
    }

    public void MoveStepDown(int index)
    {
        EnsureIndex(index);
        if (index == _steps.Count - 1)
        {
            return;
        }

        (_steps[index + 1], _steps[index]) = (_steps[index], _steps[index + 1]);
    }

    /// <summary>Records the outcome of an occurrence (the convenience cache; EF row is authoritative).</summary>
    public void RecordRun(DateOnly localDate, string occurrenceKey, RunStatus status)
    {
        RunState = RunState with
        {
            LastRunLocalDate = localDate,
            LastRunOccurrenceKey = occurrenceKey,
            LastStatus = status,
            AcknowledgedAtUtc = null,
        };
    }

    public void Acknowledge(DateTimeOffset whenUtc) => RunState = RunState with { AcknowledgedAtUtc = whenUtc };

    private void EnsureIndex(int index)
    {
        if (index < 0 || index >= _steps.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
