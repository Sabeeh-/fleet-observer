namespace Api.Fleet;

public enum WorkerStatus { Stopped, Starting, Healthy, Slow, WrongAnswer, Unresponsive }

public enum FaultMode { None, Slow, WrongAnswer, Unresponsive }

public enum IncidentKind { WrongAnswer, Unresponsive }

public enum IncidentState { Open, Acknowledged, Recovered, Resolved }

public enum LogSeverity { Info, Warning, Error }

public enum Verdict { Ok, TooSlow, WrongAnswer }

public enum CommandResult { Ok, NotFound, Conflict }

/// <summary>What a worker claims. The observer checks it; it never trusts it.</summary>
public sealed record Report(string WorkerId, int Epoch, int Cycle, int Index, long Value, DateTimeOffset StartedAt);

public sealed record LogEntry(long Seq, DateTimeOffset At, string WorkerId, LogSeverity Level, string Message);

public sealed class FleetOptions
{
    public int SlowMs { get; set; } = 500;
    public int UnresponsiveMs { get; set; } = 3000;
    public int RecoverAfter { get; set; } = 3;
    public int LogCapacity { get; set; } = 10;
    public int MinDelayMs { get; set; } = 50;
    public int MaxDelayMs { get; set; } = 400;
    public int SlowMinDelayMs { get; set; } = 600;
    public int SlowMaxDelayMs { get; set; } = 900;
    public int Seed { get; set; } = 17;
}

public sealed class Worker(string id)
{
    public string Id { get; } = id;
    public WorkerStatus Status { get; internal set; } = WorkerStatus.Stopped;
    public int Epoch { get; internal set; }
    public int Cycle { get; internal set; } = 1;
    public int LastIndex { get; internal set; }
    public long? LastValue { get; internal set; }
    public double? LastLatencyMs { get; internal set; }
    public DateTimeOffset? LastReportAt { get; internal set; }
    public DateTimeOffset LastHeardAt { get; internal set; }
    public int OkStreak { get; internal set; }
    internal Queue<LogEntry> LogBuffer { get; } = new();
    public IReadOnlyCollection<LogEntry> Log => LogBuffer;
}

public sealed class Incident(int id, string workerId, IncidentKind kind, DateTimeOffset openedAt)
{
    public int Id { get; } = id;
    public string WorkerId { get; } = workerId;
    public IncidentKind Kind { get; } = kind;
    public IncidentState State { get; internal set; } = IncidentState.Open;
    public DateTimeOffset OpenedAt { get; } = openedAt;
    public DateTimeOffset? AcknowledgedAt { get; internal set; }
    public DateTimeOffset? RecoveredAt { get; internal set; }
    public DateTimeOffset? ResolvedAt { get; internal set; }
}

public sealed record WorkerView(
    string Id, WorkerStatus Status, int Epoch, int Cycle, int LastIndex, long? LastValue,
    double? LastLatencyMs, DateTimeOffset? LastReportAt, IReadOnlyList<LogEntry> Log);

public sealed record IncidentView(int Id, string WorkerId, IncidentKind Kind, IncidentState State, DateTimeOffset OpenedAt);

public sealed record FleetSnapshot(
    long Version, bool Running, DateTimeOffset HeartbeatAt, DateTimeOffset ServerNow,
    IReadOnlyList<WorkerView> Workers, IReadOnlyList<IncidentView> Incidents);
