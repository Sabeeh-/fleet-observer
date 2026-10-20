namespace Api.Fleet;

/// <summary>
/// Pure domain state for the fleet and its incidents. Not thread-safe by design:
/// only the observer loop calls it (ADR 0001, single writer). Every method takes
/// the current time explicitly so tests can drive it without a clock.
/// </summary>
public sealed class FleetState
{
    public const int Size = 5;
    public static readonly IReadOnlyList<string> WorkerIds =
        Enumerable.Range(1, Size).Select(i => $"worker-{i}").ToArray();

    private readonly FleetOptions _options;
    private readonly Action<LogEntry>? _onLog;
    private readonly Dictionary<string, Worker> _workers;
    private readonly List<Incident> _incidents = [];
    private long _logSeq;
    private int _incidentSeq;

    public FleetState(FleetOptions? options = null, Action<LogEntry>? onLog = null)
    {
        _options = options ?? new FleetOptions();
        _onLog = onLog;
        _workers = WorkerIds.ToDictionary(id => id, id => new Worker(id));
    }

    public bool Running { get; private set; }
    public IEnumerable<Worker> Workers => WorkerIds.Select(id => _workers[id]);
    public IReadOnlyList<Incident> Incidents => _incidents;
    public Worker Get(string id) => _workers[id];

    public bool Start(DateTimeOffset now)
    {
        if (Running) return false;
        Running = true;
        foreach (var w in Workers) BeginRun(w, now);
        return true;
    }

    public bool Stop(DateTimeOffset now)
    {
        if (!Running) return false;
        Running = false;
        foreach (var w in Workers)
        {
            w.Epoch++; // anything still in flight from the old run is now stale
            SetStatus(w, WorkerStatus.Stopped, now);
        }
        return true;
    }

    public (CommandResult Result, int Epoch) Restart(string workerId, DateTimeOffset now)
    {
        if (!_workers.TryGetValue(workerId, out var w)) return (CommandResult.NotFound, 0);
        if (!Running) return (CommandResult.Conflict, 0);
        Log(w, LogSeverity.Info, "Restart requested", now);
        BeginRun(w, now);
        return (CommandResult.Ok, w.Epoch);
    }

    public CommandResult Acknowledge(int incidentId, DateTimeOffset now)
    {
        var incident = _incidents.FirstOrDefault(i => i.Id == incidentId);
        if (incident is null) return CommandResult.NotFound;
        var w = _workers[incident.WorkerId];
        switch (incident.State)
        {
            case IncidentState.Open:
                incident.State = IncidentState.Acknowledged;
                incident.AcknowledgedAt = now;
                Log(w, LogSeverity.Info, $"Incident acknowledged: {Display(incident.Kind)}", now);
                break;
            case IncidentState.Recovered:
                incident.AcknowledgedAt = now;
                Resolve(w, incident, now);
                break;
            // Already acknowledged or resolved: acknowledging again changes nothing.
        }
        return CommandResult.Ok;
    }

    public void Accept(Report report, DateTimeOffset now)
    {
        if (!_workers.TryGetValue(report.WorkerId, out var w)) return;
        if (w.Status == WorkerStatus.Stopped || report.Epoch != w.Epoch) return; // stale run

        var (expectedCycle, expectedIndex) = w.LastIndex >= Primes.Count ? (w.Cycle + 1, 1) : (w.Cycle, w.LastIndex + 1);
        if (report.Cycle != expectedCycle || report.Index != expectedIndex)
        {
            Log(w, LogSeverity.Error,
                $"Out-of-order report cycle {report.Cycle} #{report.Index} (expected cycle {expectedCycle} #{expectedIndex}); ignored", now);
            return;
        }

        w.Cycle = report.Cycle;
        w.LastIndex = report.Index;
        w.LastValue = report.Value;
        w.LastLatencyMs = (now - report.StartedAt).TotalMilliseconds;
        w.LastReportAt = now;
        w.LastHeardAt = now;

        var expected = Primes.First17[report.Index - 1];
        var verdict = report.Value != expected ? Verdict.WrongAnswer
            : w.LastLatencyMs > _options.SlowMs ? Verdict.TooSlow
            : Verdict.Ok;

        switch (verdict)
        {
            case Verdict.WrongAnswer:
                w.OkStreak = 0;
                if (w.Status != WorkerStatus.WrongAnswer)
                {
                    Log(w, LogSeverity.Error, $"cycle {report.Cycle} #{report.Index} expected {expected} got {report.Value}", now);
                    SetStatus(w, WorkerStatus.WrongAnswer, now);
                }
                RaiseIncident(w, IncidentKind.WrongAnswer, now);
                break;

            case Verdict.TooSlow:
                w.OkStreak = 0;
                if (w.Status is WorkerStatus.Starting or WorkerStatus.Healthy) SetStatus(w, WorkerStatus.Slow, now);
                break;

            case Verdict.Ok:
                w.OkStreak++;
                var recovered = w.Status switch
                {
                    WorkerStatus.Starting or WorkerStatus.Slow => true,
                    WorkerStatus.WrongAnswer or WorkerStatus.Unresponsive => w.OkStreak >= _options.RecoverAfter,
                    _ => false,
                };
                if (recovered) SetStatus(w, WorkerStatus.Healthy, now);
                break;
        }
    }

    /// <summary>Detects silence. The observer never pings workers; it only measures time since they were last heard.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (!Running) return;
        foreach (var w in Workers)
        {
            if (w.Status is WorkerStatus.Stopped or WorkerStatus.Unresponsive) continue;
            if ((now - w.LastHeardAt).TotalMilliseconds <= _options.UnresponsiveMs) continue;
            w.OkStreak = 0;
            SetStatus(w, WorkerStatus.Unresponsive, now);
            RaiseIncident(w, IncidentKind.Unresponsive, now);
        }
    }

    public IReadOnlyList<WorkerView> WorkerViews() =>
        Workers.Select(w => new WorkerView(
            w.Id, w.Status, w.Epoch, w.Cycle, w.LastIndex, w.LastValue, w.LastLatencyMs, w.LastReportAt,
            w.LogBuffer.ToArray())).ToArray();

    public IReadOnlyList<IncidentView> ActiveIncidents() =>
        _incidents.Where(i => i.State != IncidentState.Resolved)
            .Select(i => new IncidentView(i.Id, i.WorkerId, i.Kind, i.State, i.OpenedAt)).ToArray();

    private void BeginRun(Worker w, DateTimeOffset now)
    {
        w.Epoch++;
        w.Cycle = 1;
        w.LastIndex = 0;
        w.LastValue = null;
        w.LastLatencyMs = null;
        w.OkStreak = 0;
        w.LastHeardAt = now;
        SetStatus(w, WorkerStatus.Starting, now);
    }

    private void SetStatus(Worker w, WorkerStatus next, DateTimeOffset now)
    {
        if (w.Status == next) return;
        var level = next switch
        {
            WorkerStatus.WrongAnswer or WorkerStatus.Unresponsive => LogSeverity.Error,
            WorkerStatus.Slow => LogSeverity.Warning,
            _ => LogSeverity.Info,
        };
        Log(w, level, $"{Display(w.Status)} → {Display(next)}", now);
        w.Status = next;
        if (next == WorkerStatus.Healthy) RecoverIncidents(w, now);
    }

    private void RaiseIncident(Worker w, IncidentKind kind, DateTimeOffset now)
    {
        var existing = _incidents.FirstOrDefault(i => i.WorkerId == w.Id && i.Kind == kind && i.State != IncidentState.Resolved);
        if (existing is null)
        {
            _incidents.Add(new Incident(++_incidentSeq, w.Id, kind, now));
            Log(w, LogSeverity.Error, $"Incident opened: {Display(kind)}", now);
        }
        else if (existing.State == IncidentState.Recovered)
        {
            existing.State = IncidentState.Open;
            existing.RecoveredAt = null;
            Log(w, LogSeverity.Error, $"Incident reopened: {Display(kind)}", now);
        }
    }

    private void RecoverIncidents(Worker w, DateTimeOffset now)
    {
        foreach (var incident in _incidents.Where(i => i.WorkerId == w.Id && i.State != IncidentState.Resolved).ToArray())
        {
            incident.RecoveredAt = now;
            if (incident.State == IncidentState.Acknowledged)
            {
                Resolve(w, incident, now);
            }
            else if (incident.State == IncidentState.Open)
            {
                incident.State = IncidentState.Recovered;
                Log(w, LogSeverity.Warning, $"Incident recovered — awaiting acknowledgement: {Display(incident.Kind)}", now);
            }
        }
    }

    // Only reachable with both an acknowledgement and a Healthy worker (SPEC invariant 4).
    private void Resolve(Worker w, Incident incident, DateTimeOffset now)
    {
        incident.State = IncidentState.Resolved;
        incident.ResolvedAt = now;
        Log(w, LogSeverity.Info, $"Incident resolved: {Display(incident.Kind)}", now);
    }

    private void Log(Worker w, LogSeverity level, string message, DateTimeOffset now)
    {
        var entry = new LogEntry(++_logSeq, now, w.Id, level, message);
        w.LogBuffer.Enqueue(entry);
        while (w.LogBuffer.Count > _options.LogCapacity) w.LogBuffer.Dequeue();
        _onLog?.Invoke(entry);
    }

    public static string Display(WorkerStatus status) => status switch
    {
        WorkerStatus.WrongAnswer => "Wrong answer",
        _ => status.ToString(),
    };

    public static string Display(IncidentKind kind) => kind switch
    {
        IncidentKind.WrongAnswer => "Wrong answer",
        _ => kind.ToString(),
    };
}
