// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Scheduling;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-frpl.5 — JSON-backed <see cref="IScheduleStore"/> mirroring
/// HierarchyConfigStore's idiom (one file, lock, atomic temp+move, empty on
/// corrupt) but with <see cref="JsonStringEnumConverter"/> so enums persist as
/// NAMES — reordering a TakeMode/DayOfWeek member never remaps existing files.
/// Recipes are serialized through explicit persisted DTOs because the domain
/// aggregate uses private setters + interface-typed members.
/// </summary>
internal sealed class ScheduleStore : IScheduleStore
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly ILogger<ScheduleStore>? _logger;
    private readonly object _lock = new();

    public ScheduleStore(string? storageDirectory = null, ILogger<ScheduleStore>? logger = null)
    {
        var dir = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WireCopy");
        _filePath = Path.Combine(dir, "schedules.json");
        _logger = logger;
    }

    public Task<IReadOnlyList<ScheduleRecipe>> GetAllAsync()
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ScheduleRecipe>>(
                Load().Select(ToDomain).ToList());
        }
    }

    public Task<ScheduleRecipe?> GetAsync(Guid id)
    {
        lock (_lock)
        {
            var p = Load().FirstOrDefault(r => r.Id == id);
            return Task.FromResult(p is null ? null : ToDomain(p));
        }
    }

    public Task SaveAsync(ScheduleRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        lock (_lock)
        {
            var all = Load();
            all.RemoveAll(r => r.Id == recipe.Id);
            all.Add(ToPersisted(recipe));
            Write(all);
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid id)
    {
        lock (_lock)
        {
            var all = Load();
            var removed = all.RemoveAll(r => r.Id == id) > 0;
            if (removed)
            {
                Write(all);
            }

            return Task.FromResult(removed);
        }
    }

    public Task UpdateRunStateAsync(Guid id, RecipeRunState runState)
    {
        ArgumentNullException.ThrowIfNull(runState);
        lock (_lock)
        {
            // Read FRESH under the lock so a definition edit that landed before
            // this call is preserved — we touch only the run-state fields.
            var all = Load();
            var target = all.FirstOrDefault(r => r.Id == id);
            if (target is not null)
            {
                target.LastRunLocalDate = runState.LastRunLocalDate?.ToString("yyyy-MM-dd");
                target.LastRunOccurrenceKey = runState.LastRunOccurrenceKey;
                target.LastStatus = runState.LastStatus;
                target.AcknowledgedAtUtc = runState.AcknowledgedAtUtc;
                Write(all);
            }
        }

        return Task.CompletedTask;
    }

    private static PersistedRecipe ToPersisted(ScheduleRecipe r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Enabled = r.Enabled,
        Days = r.Cadence.Days.ToList(),
        LocalTime = r.Cadence.LocalTime.ToString("HH:mm"),
        GraceMinutes = r.Cadence.GraceWindow?.TotalMinutes,
        OutputCollectionName = r.OutputCollectionName,
        Version = r.Version,
        LastRunLocalDate = r.RunState.LastRunLocalDate?.ToString("yyyy-MM-dd"),
        LastRunOccurrenceKey = r.RunState.LastRunOccurrenceKey,
        LastStatus = r.RunState.LastStatus,
        AcknowledgedAtUtc = r.RunState.AcknowledgedAtUtc,
        Steps = r.Steps.Select(s => new PersistedStep
        {
            BookmarkId = s.BookmarkId,
            SourceUrl = s.SourceUrl,
            Domain = s.Domain,
            ConfigUrlPattern = s.ConfigUrlPattern,
            SectionName = s.SectionName,
            Scope = s.Scope,
            SortOrderFallback = s.SortOrderFallback,
            HeadingAliases = s.HeadingAliases.ToList(),
            TakeMode = s.TakeMode,
            TakeCount = s.TakeCount,
            Required = s.Required,
        }).ToList(),
    };

    private static ScheduleRecipe ToDomain(PersistedRecipe p)
    {
        var cadence = new Cadence
        {
            Days = p.Days.ToHashSet(),
            LocalTime = TimeOnly.ParseExact(p.LocalTime, "HH:mm"),
            GraceWindow = p.GraceMinutes is { } m ? TimeSpan.FromMinutes(m) : null,
        };

        var steps = p.Steps.Select(s => new RecipeStep
        {
            BookmarkId = s.BookmarkId,
            SourceUrl = s.SourceUrl,
            Domain = s.Domain,
            ConfigUrlPattern = s.ConfigUrlPattern,
            SectionName = s.SectionName,
            Scope = s.Scope,
            SortOrderFallback = s.SortOrderFallback,
            HeadingAliases = s.HeadingAliases,
            TakeMode = s.TakeMode,
            TakeCount = s.TakeCount,
            Required = s.Required,
        });

        var runState = new RecipeRunState
        {
            LastRunLocalDate = p.LastRunLocalDate is null ? null : DateOnly.ParseExact(p.LastRunLocalDate, "yyyy-MM-dd"),
            LastRunOccurrenceKey = p.LastRunOccurrenceKey,
            LastStatus = p.LastStatus,
            AcknowledgedAtUtc = p.AcknowledgedAtUtc,
        };

        return ScheduleRecipe.Rehydrate(p.Id, p.Name, p.Enabled, cadence, steps, p.OutputCollectionName, runState, p.Version);
    }

    private List<PersistedRecipe> Load()
    {
        if (!File.Exists(_filePath))
        {
            return new List<PersistedRecipe>();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var file = JsonSerializer.Deserialize<ScheduleFile>(json, JsonOptions);
            return file?.Recipes ?? new List<PersistedRecipe>();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Corrupt schedules.json — starting empty");
            return new List<PersistedRecipe>();
        }
    }

    private void Write(List<PersistedRecipe> recipes)
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new ScheduleFile { Version = CurrentVersion, Recipes = recipes }, JsonOptions);
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _filePath, overwrite: true);
    }

    private sealed class ScheduleFile
    {
        public int Version { get; set; } = CurrentVersion;

        public List<PersistedRecipe> Recipes { get; set; } = new();
    }

    private sealed class PersistedRecipe
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool Enabled { get; set; }

        public List<DayOfWeek> Days { get; set; } = new();

        public string LocalTime { get; set; } = "07:00";

        public double? GraceMinutes { get; set; }

        public List<PersistedStep> Steps { get; set; } = new();

        public string OutputCollectionName { get; set; } = string.Empty;

        public int Version { get; set; } = 1;

        public string? LastRunLocalDate { get; set; }

        public string? LastRunOccurrenceKey { get; set; }

        public RunStatus LastStatus { get; set; } = RunStatus.Never;

        public DateTimeOffset? AcknowledgedAtUtc { get; set; }
    }

    private sealed class PersistedStep
    {
        public Guid? BookmarkId { get; set; }

        public string SourceUrl { get; set; } = string.Empty;

        public string Domain { get; set; } = string.Empty;

        public string ConfigUrlPattern { get; set; } = string.Empty;

        public string SectionName { get; set; } = string.Empty;

        // workspace-42q8.2: absent in pre-existing files → PinnedSection, so old
        // recipes keep their meaning (serialized as the enum NAME like every enum here).
        public StepScope Scope { get; set; } = StepScope.PinnedSection;

        public int SortOrderFallback { get; set; }

        public List<string> HeadingAliases { get; set; } = new();

        public TakeMode TakeMode { get; set; } = TakeMode.WholeSection;

        public int? TakeCount { get; set; }

        public bool Required { get; set; }
    }
}
