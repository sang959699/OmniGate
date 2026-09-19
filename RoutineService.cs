using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

// ------------------ ROUTINES ------------------
// Routines are persisted definitions made up of device actions and explicit
// delays.  A run is deliberately detached from the HTTP request so an iOS
// Shortcut can trigger a long sequence without holding a connection open.
public interface IRoutineService
{
    IReadOnlyList<RoutineDto> List();
    RoutineDto? Get(string idOrSlug);
    RoutineDto Create(RoutineUpsertRequest request);
    RoutineDto? Update(string idOrSlug, RoutineUpsertRequest request);
    bool Delete(string idOrSlug);
    RoutineRunDto Start(string idOrSlug);
    RoutineRunDto? GetRun(string runId);
    void Stop();
}

public sealed class RoutineService : IRoutineService
{
    private const int MaxRoutines = 100;
    private const int MaxSteps = 50;
    private const int MaxNameLength = 80;
    private const int MaxSlugLength = 80;
    private const int MaxDelaySeconds = 3600;
    private const int MaxRetainedRuns = 100;

    private readonly IConfiguration _config;
    private readonly ILogger<RoutineService> _logger;
    private readonly ISwitchBotService _switchBot;
    private readonly ITapoService _tapo;
    private readonly IWakeOnLanService _wol;
    private readonly string _routineFile;
    private readonly object _fileLock = new();
    private readonly List<RoutineDefinition> _routines = new();
    private readonly ConcurrentDictionary<string, RoutineRunRecord> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _activeRoutineRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public RoutineService(
        IConfiguration config,
        ILogger<RoutineService> logger,
        ISwitchBotService switchBot,
        ITapoService tapo,
        IWakeOnLanService wol)
    {
        _config = config;
        _logger = logger;
        _switchBot = switchBot;
        _tapo = tapo;
        _wol = wol;
        _routineFile = LocalStatePath.Resolve(_config, "Storage:RoutinesFile", "routines.json");
        LocalStatePath.MigrateLegacyIfMissing(_routineFile, "routines.json");
        Load();
    }

    public IReadOnlyList<RoutineDto> List()
    {
        lock (_fileLock)
        {
            return _routines
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToDto)
                .ToList();
        }
    }

    public RoutineDto? Get(string idOrSlug)
    {
        lock (_fileLock)
        {
            var routine = FindLocked(idOrSlug);
            return routine == null ? null : ToDto(routine);
        }
    }

    public RoutineDto Create(RoutineUpsertRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var routine = BuildDefinition(request, existing: null, now);

        lock (_fileLock)
        {
            if (_routines.Count >= MaxRoutines)
            {
                throw new RoutineValidationException($"A maximum of {MaxRoutines} routines can be saved.");
            }

            // Names are human-friendly; automatically suffix duplicate slugs so
            // each saved routine gets a usable Shortcut URL without another
            // field in the UI.
            routine.Slug = MakeUniqueSlugLocked(routine.Slug, existingId: null);
            _routines.Add(routine);
            SaveLocked();
            return ToDto(routine);
        }
    }

    public RoutineDto? Update(string idOrSlug, RoutineUpsertRequest request)
    {
        lock (_fileLock)
        {
            var existing = FindLocked(idOrSlug);
            if (existing == null) return null;

            var updated = BuildDefinition(request, existing, DateTimeOffset.UtcNow);
            EnsureUniqueSlugLocked(updated.Slug, existing.Id);

            var index = _routines.FindIndex(r => r.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase));
            _routines[index] = updated;
            SaveLocked();
            return ToDto(updated);
        }
    }

    public bool Delete(string idOrSlug)
    {
        lock (_fileLock)
        {
            var existing = FindLocked(idOrSlug);
            if (existing == null) return false;
            if (_activeRoutineRuns.ContainsKey(existing.Id))
            {
                throw new RoutineConflictException("This routine is currently running and cannot be deleted.");
            }

            _routines.RemoveAll(r => r.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase));
            SaveLocked();
            return true;
        }
    }

    public RoutineRunDto Start(string idOrSlug)
    {
        RoutineDefinition routine;
        lock (_fileLock)
        {
            routine = FindLocked(idOrSlug)?.Clone()
                ?? throw new RoutineNotFoundException($"Routine '{idOrSlug}' was not found.");
        }

        if (routine.PreventDuplicateRuns && !_activeRoutineRuns.TryAdd(routine.Id, 0))
        {
            throw new RoutineConflictException("This routine is already running.");
        }

        var run = new RoutineRunRecord(routine);
        _runs[run.RunId] = run;
        PruneRuns();

        _ = Task.Run(() => ExecuteRunAsync(run, routine, _shutdown.Token));
        return Snapshot(run);
    }

    public RoutineRunDto? GetRun(string runId)
    {
        return _runs.TryGetValue(runId, out var run) ? Snapshot(run) : null;
    }

    public void Stop()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }
    }

    private async Task ExecuteRunAsync(RoutineRunRecord run, RoutineDefinition routine, CancellationToken cancellationToken)
    {
        bool hadFailures = false;
        lock (run.SyncRoot)
        {
            run.Status = "Running";
            run.StartedAt = DateTimeOffset.UtcNow;
        }

        try
        {
            for (int index = 0; index < routine.Steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = routine.Steps[index];
                var stepResult = GetStepResult(run, index);

                lock (run.SyncRoot)
                {
                    run.CurrentStep = index + 1;
                    stepResult.Status = "Running";
                    stepResult.StartedAt = DateTimeOffset.UtcNow;
                }

                try
                {
                    string message = await ExecuteStepAsync(step, cancellationToken);
                    lock (run.SyncRoot)
                    {
                        stepResult.Status = "Succeeded";
                        stepResult.Message = message;
                        stepResult.CompletedAt = DateTimeOffset.UtcNow;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lock (run.SyncRoot)
                    {
                        stepResult.Status = "Cancelled";
                        stepResult.Message = "Run cancelled during this step.";
                        stepResult.CompletedAt = DateTimeOffset.UtcNow;
                    }
                    throw;
                }
                catch (Exception ex)
                {
                    hadFailures = true;
                    _logger.LogWarning(ex, "[Routine] Step {StepNumber} failed in routine {RoutineId}.", index + 1, routine.Id);
                    lock (run.SyncRoot)
                    {
                        stepResult.Status = "Failed";
                        stepResult.Message = ex.Message;
                        stepResult.CompletedAt = DateTimeOffset.UtcNow;
                    }

                    if (routine.StopOnError)
                    {
                        lock (run.SyncRoot)
                        {
                            run.Status = "Failed";
                            run.Error = ex.Message;
                            run.CompletedAt = DateTimeOffset.UtcNow;
                        }
                        return;
                    }
                }
            }

            lock (run.SyncRoot)
            {
                run.Status = hadFailures ? "CompletedWithErrors" : "Completed";
                run.CompletedAt = DateTimeOffset.UtcNow;
                run.CurrentStep = routine.Steps.Count;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (run.SyncRoot)
            {
                run.Status = "Cancelled";
                run.Error = "The routine was cancelled because OmniGate is shutting down.";
                run.CompletedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Routine] Unexpected error while running routine {RoutineId}.", routine.Id);
            lock (run.SyncRoot)
            {
                run.Status = "Failed";
                run.Error = ex.Message;
                run.CompletedAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _activeRoutineRuns.TryRemove(routine.Id, out _);
        }
    }

    private async Task<string> ExecuteStepAsync(RoutineStep step, CancellationToken cancellationToken)
    {
        switch (step.Type)
        {
            case "delay":
                await Task.Delay(TimeSpan.FromSeconds(step.Seconds!.Value), cancellationToken);
                return $"Waited {step.Seconds.Value.ToString(CultureInfo.InvariantCulture)} second(s).";

            case "switchbot":
                _switchBot.QueueCommand(step.Action == "on"
                    ? new byte[] { 0x57, 0x01, 0x01 }
                    : new byte[] { 0x57, 0x01, 0x02 });
                return $"SwitchBot {(step.Action ?? "unknown").ToUpperInvariant()} command queued.";

            case "wol":
                await _wol.WakeAsync(step.MacAddress, step.BroadcastIp, step.Port);
                return "Wake on LAN magic packet sent.";

            case "tapo":
                if (!ulong.TryParse(step.NodeId, NumberStyles.None, CultureInfo.InvariantCulture, out var nodeId))
                {
                    throw new InvalidOperationException("The Tapo node ID is invalid.");
                }

                ushort endpointId = step.EndpointId!.Value;
                bool success = await _tapo.ControlOutletAsync(
                    nodeId,
                    endpointId,
                    turnOn: step.Action == "on",
                    toggle: false,
                    cancellationToken);

                if (!success)
                {
                    throw new InvalidOperationException($"Tapo outlet {endpointId} did not acknowledge the command.");
                }

                return $"Tapo outlet {endpointId} turned {(step.Action ?? "unknown").ToUpperInvariant()}.";

            default:
                throw new InvalidOperationException($"Unsupported routine step type '{step.Type}'.");
        }
    }

    private RoutineDefinition BuildDefinition(RoutineUpsertRequest request, RoutineDefinition? existing, DateTimeOffset now)
    {
        if (request == null) throw new RoutineValidationException("A routine definition is required.");

        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) throw new RoutineValidationException("Routine name is required.");
        if (name.Length > MaxNameLength) throw new RoutineValidationException($"Routine name must be {MaxNameLength} characters or fewer.");

        string routineId = existing?.Id ?? Guid.NewGuid().ToString("N");
        string slug = string.IsNullOrWhiteSpace(request.Slug)
            ? existing?.Slug ?? Slugify(name)
            : Slugify(request.Slug!);
        if (slug.Length == 0) slug = $"routine-{routineId[..8]}";
        if (slug.Length > MaxSlugLength) throw new RoutineValidationException($"Routine API name must be {MaxSlugLength} characters or fewer.");

        var requestSteps = request.Steps ?? new List<RoutineStepRequest>();
        if (requestSteps.Count == 0) throw new RoutineValidationException("Add at least one action or time gap.");
        if (requestSteps.Count > MaxSteps) throw new RoutineValidationException($"A routine can contain at most {MaxSteps} steps.");

        var steps = requestSteps.Select(NormalizeStep).ToList();
        return new RoutineDefinition
        {
            Id = routineId,
            Name = name,
            Slug = slug,
            StopOnError = request.StopOnError ?? existing?.StopOnError ?? true,
            PreventDuplicateRuns = request.PreventDuplicateRuns ?? existing?.PreventDuplicateRuns ?? true,
            Steps = steps,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        };
    }

    private static RoutineStep NormalizeStep(RoutineStepRequest? request)
    {
        if (request == null) throw new RoutineValidationException("Routine steps cannot be empty.");
        string type = request.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        string action = request.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        var step = new RoutineStep
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = type,
            Action = action,
            NodeId = request.NodeId?.Trim(),
            EndpointId = request.EndpointId,
            Seconds = request.Seconds,
            MacAddress = request.MacAddress?.Trim(),
            BroadcastIp = request.BroadcastIp?.Trim(),
            Port = request.Port
        };

        switch (type)
        {
            case "delay":
                if (request.Seconds is null or < 1 or > MaxDelaySeconds)
                    throw new RoutineValidationException($"Time gaps must be between 1 and {MaxDelaySeconds} seconds.");
                step.Action = null;
                break;

            case "switchbot":
                if (action is not ("on" or "off"))
                    throw new RoutineValidationException("SwitchBot actions must be 'on' or 'off'.");
                step.NodeId = null;
                step.EndpointId = null;
                step.Seconds = null;
                break;

            case "wol":
                if (action != "wake")
                    throw new RoutineValidationException("Wake on LAN actions must be 'wake'.");
                step.NodeId = null;
                step.EndpointId = null;
                step.Seconds = null;
                if (step.Port is <= 0 or > 65535)
                    throw new RoutineValidationException("Wake on LAN port must be between 1 and 65535.");
                break;

            case "tapo":
                if (action is not ("on" or "off"))
                    throw new RoutineValidationException("Tapo actions must be 'on' or 'off'.");
                if (!ulong.TryParse(step.NodeId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    throw new RoutineValidationException("A valid Tapo node ID is required.");
                if (step.EndpointId is null or 0)
                    throw new RoutineValidationException("A valid Tapo outlet endpoint is required.");
                step.Seconds = null;
                break;

            default:
                throw new RoutineValidationException("Each step must be a Tapo action, SwitchBot action, Wake on LAN action, or time gap.");
        }

        return step;
    }

    private void Load()
    {
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(_routineFile)) return;
                var loaded = JsonSerializer.Deserialize<List<RoutineDefinition>>(File.ReadAllText(_routineFile), _jsonOptions);
                if (loaded == null) return;

                foreach (var routine in loaded.Take(MaxRoutines))
                {
                    try
                    {
                        routine.Id = string.IsNullOrWhiteSpace(routine.Id) ? Guid.NewGuid().ToString("N") : routine.Id;
                        routine.Name = routine.Name?.Trim() ?? string.Empty;
                        routine.Slug = Slugify(string.IsNullOrWhiteSpace(routine.Slug) ? routine.Name : routine.Slug);
                        routine.Steps ??= new List<RoutineStep>();
                        foreach (var step in routine.Steps)
                        {
                            step.Id = string.IsNullOrWhiteSpace(step.Id) ? Guid.NewGuid().ToString("N") : step.Id;
                            step.Type = step.Type?.Trim().ToLowerInvariant() ?? string.Empty;
                            step.Action = string.IsNullOrWhiteSpace(step.Action) ? null : step.Action.Trim().ToLowerInvariant();
                        }

                        if (routine.Name.Length == 0 || routine.Slug.Length == 0 || routine.Steps.Count == 0)
                            continue;
                        if (_routines.Any(r => r.Slug.Equals(routine.Slug, StringComparison.OrdinalIgnoreCase)))
                            routine.Slug = MakeUniqueSlugLocked(routine.Slug, routine.Id);
                        _routines.Add(routine);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Routine] Skipping an invalid saved routine.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Routine] Failed to load {RoutineFile}.", _routineFile);
            }
        }
    }

    private void SaveLocked()
    {
        try
        {
            string json = JsonSerializer.Serialize(_routines, _jsonOptions);
            File.WriteAllText(_routineFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Routine] Failed to save {RoutineFile}.", _routineFile);
            throw new InvalidOperationException("The routine could not be saved.", ex);
        }
    }

    private RoutineDefinition? FindLocked(string idOrSlug)
    {
        if (string.IsNullOrWhiteSpace(idOrSlug)) return null;
        return _routines.FirstOrDefault(r =>
            r.Id.Equals(idOrSlug, StringComparison.OrdinalIgnoreCase) ||
            r.Slug.Equals(idOrSlug, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureUniqueSlugLocked(string slug, string? existingId)
    {
        if (_routines.Any(r => r.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase) &&
            !r.Id.Equals(existingId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new RoutineConflictException($"The API name '{slug}' is already in use.");
        }
    }

    private string MakeUniqueSlugLocked(string baseSlug, string? existingId)
    {
        string candidate = baseSlug;
        int suffix = 2;
        while (_routines.Any(r => r.Slug.Equals(candidate, StringComparison.OrdinalIgnoreCase) &&
            !r.Id.Equals(existingId, StringComparison.OrdinalIgnoreCase)))
        {
            string suffixText = $"-{suffix++}";
            candidate = baseSlug[..Math.Min(baseSlug.Length, MaxSlugLength - suffixText.Length)] + suffixText;
        }
        return candidate;
    }

    private static string Slugify(string value)
    {
        string slug = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length <= MaxSlugLength ? slug : slug[..MaxSlugLength].Trim('-');
    }

    private void PruneRuns()
    {
        if (_runs.Count <= MaxRetainedRuns) return;
        foreach (var stale in _runs.Values
            .Where(r => IsTerminal(r.Status))
            .OrderBy(r => r.CompletedAt ?? r.StartedAt)
            .Take(Math.Max(1, _runs.Count - MaxRetainedRuns)))
        {
            _runs.TryRemove(stale.RunId, out _);
        }
    }

    private static bool IsTerminal(string status) => status is "Completed" or "CompletedWithErrors" or "Failed" or "Cancelled";

    private static RoutineRunStepRecord GetStepResult(RoutineRunRecord run, int index)
    {
        lock (run.SyncRoot)
        {
            while (run.Steps.Count <= index) run.Steps.Add(new RoutineRunStepRecord());
            return run.Steps[index];
        }
    }

    private static RoutineDto ToDto(RoutineDefinition routine)
    {
        return new RoutineDto
        {
            Id = routine.Id,
            Name = routine.Name,
            Slug = routine.Slug,
            StopOnError = routine.StopOnError,
            PreventDuplicateRuns = routine.PreventDuplicateRuns,
            CreatedAt = routine.CreatedAt,
            UpdatedAt = routine.UpdatedAt,
            EstimatedDelaySeconds = routine.Steps.Where(s => s.Type == "delay").Sum(s => s.Seconds ?? 0),
            ApiPath = $"/api/routines/{routine.Slug}/run",
            Steps = routine.Steps.Select(s => new RoutineStepDto
            {
                Id = s.Id,
                Type = s.Type,
                Action = s.Action,
                NodeId = s.NodeId,
                EndpointId = s.EndpointId,
                Seconds = s.Seconds,
                MacAddress = s.MacAddress,
                BroadcastIp = s.BroadcastIp,
                Port = s.Port
            }).ToList()
        };
    }

    private static RoutineRunDto Snapshot(RoutineRunRecord run)
    {
        lock (run.SyncRoot)
        {
            return new RoutineRunDto
            {
                RunId = run.RunId,
                RoutineId = run.RoutineId,
                RoutineName = run.RoutineName,
                Status = run.Status,
                CurrentStep = run.CurrentStep,
                TotalSteps = run.TotalSteps,
                StartedAt = run.StartedAt,
                CompletedAt = run.CompletedAt,
                Error = run.Error,
                Steps = run.Steps.Select(s => new RoutineRunStepDto
                {
                    Status = s.Status,
                    Message = s.Message,
                    StartedAt = s.StartedAt,
                    CompletedAt = s.CompletedAt
                }).ToList()
            };
        }
    }

    private sealed class RoutineDefinition
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public bool StopOnError { get; set; } = true;
        public bool PreventDuplicateRuns { get; set; } = true;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public List<RoutineStep> Steps { get; set; } = new();

        public RoutineDefinition Clone() => new()
        {
            Id = Id,
            Name = Name,
            Slug = Slug,
            StopOnError = StopOnError,
            PreventDuplicateRuns = PreventDuplicateRuns,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Steps = Steps.Select(s => s.Clone()).ToList()
        };
    }

    private sealed class RoutineStep
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Type { get; set; } = string.Empty;
        public string? Action { get; set; }
        public string? NodeId { get; set; }
        public ushort? EndpointId { get; set; }
        public int? Seconds { get; set; }
        public string? MacAddress { get; set; }
        public string? BroadcastIp { get; set; }
        public int? Port { get; set; }

        public RoutineStep Clone() => (RoutineStep)MemberwiseClone();
    }

    private sealed class RoutineRunRecord
    {
        public RoutineRunRecord(RoutineDefinition routine)
        {
            RunId = Guid.NewGuid().ToString("N");
            RoutineId = routine.Id;
            RoutineName = routine.Name;
            TotalSteps = routine.Steps.Count;
            Status = "Queued";
        }

        public readonly object SyncRoot = new();
        public string RunId { get; }
        public string RoutineId { get; }
        public string RoutineName { get; }
        public int TotalSteps { get; }
        public string Status { get; set; }
        public int CurrentStep { get; set; }
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAt { get; set; }
        public string? Error { get; set; }
        public List<RoutineRunStepRecord> Steps { get; } = new();
    }

    private sealed class RoutineRunStepRecord
    {
        public string Status { get; set; } = "Queued";
        public string? Message { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
    }
}

public sealed class RoutineUpsertRequest
{
    public string? Name { get; set; }
    public string? Slug { get; set; }
    public bool? StopOnError { get; set; }
    public bool? PreventDuplicateRuns { get; set; }
    public List<RoutineStepRequest>? Steps { get; set; }
}

public sealed class RoutineStepRequest
{
    public string? Type { get; set; }
    public string? Action { get; set; }
    public string? NodeId { get; set; }
    public ushort? EndpointId { get; set; }
    public int? Seconds { get; set; }
    public string? MacAddress { get; set; }
    public string? BroadcastIp { get; set; }
    public int? Port { get; set; }
}

public sealed class RoutineDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool StopOnError { get; set; }
    public bool PreventDuplicateRuns { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int EstimatedDelaySeconds { get; set; }
    public string ApiPath { get; set; } = string.Empty;
    public List<RoutineStepDto> Steps { get; set; } = new();
}

public sealed class RoutineStepDto
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Action { get; set; }
    public string? NodeId { get; set; }
    public ushort? EndpointId { get; set; }
    public int? Seconds { get; set; }
    public string? MacAddress { get; set; }
    public string? BroadcastIp { get; set; }
    public int? Port { get; set; }
}

public sealed class RoutineRunDto
{
    public string RunId { get; set; } = string.Empty;
    public string RoutineId { get; set; } = string.Empty;
    public string RoutineName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public List<RoutineRunStepDto> Steps { get; set; } = new();
}

public sealed class RoutineRunStepDto
{
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class RoutineValidationException : Exception
{
    public RoutineValidationException(string message) : base(message) { }
}

public sealed class RoutineNotFoundException : Exception
{
    public RoutineNotFoundException(string message) : base(message) { }
}

public sealed class RoutineConflictException : Exception
{
    public RoutineConflictException(string message) : base(message) { }
}
