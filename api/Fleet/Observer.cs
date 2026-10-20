using System.Threading.Channels;

namespace Api.Fleet;

/// <summary>
/// The observer loop: the only code that changes <see cref="FleetState"/> (ADR 0001).
/// Reports, clock ticks and operator commands all go through one channel and are handled
/// one at a time; after each message an immutable snapshot is published for readers.
/// </summary>
public sealed class Observer : BackgroundService, IReportSink
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly Channel<Message> _inbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, CancellationTokenSource> _runners = [];
    private readonly IFaultSource _faults;
    private readonly TimeProvider _time;
    private readonly FleetOptions _options;
    private readonly ILogger<Observer> _logger;
    private FleetState _state;
    private FleetSnapshot _snapshot;
    private long _version;
    private DateTimeOffset _heartbeatAt;
    private CancellationToken _stopping;

    public Observer(IFaultSource faults, TimeProvider time, FleetOptions options, ILogger<Observer> logger)
    {
        _faults = faults;
        _time = time;
        _options = options;
        _logger = logger;
        _state = NewState();
        _heartbeatAt = time.GetUtcNow();
        _snapshot = BuildSnapshot();
    }

    public FleetSnapshot Current => Volatile.Read(ref _snapshot);

    public ValueTask SubmitAsync(Report report, CancellationToken ct) => _inbox.Writer.WriteAsync(new ReportMessage(report), ct);

    public Task<CommandResult> StartFleetAsync() => Send(new StartFleet());
    public Task<CommandResult> StopFleetAsync() => Send(new StopFleet());
    public Task<CommandResult> RestartAsync(string workerId) => Send(new RestartWorker(workerId));
    public Task<CommandResult> AcknowledgeAsync(int incidentId) => Send(new Acknowledge(incidentId));
    public Task<CommandResult> ResetAsync() => Send(new Reset());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _ = TickAsync(stoppingToken);

        try
        {
            await foreach (var message in _inbox.Reader.ReadAllAsync(stoppingToken))
            {
                var now = _time.GetUtcNow();
                try
                {
                    Handle(message, now);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Observer failed to handle {Message}", message.GetType().Name);
                    (message as Command)?.Reply.TrySetException(ex);
                }
                Volatile.Write(ref _snapshot, BuildSnapshot());
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            StopAllRunners();
        }
    }

    private void Handle(Message message, DateTimeOffset now)
    {
        switch (message)
        {
            case ReportMessage m:
                _state.Accept(m.Report, now);
                break;

            case TickMessage:
                _heartbeatAt = now;
                _state.Tick(now);
                break;

            case StartFleet c:
                if (_state.Start(now))
                {
                    foreach (var w in _state.Workers) StartRunner(w.Id, w.Epoch);
                }
                c.Reply.TrySetResult(CommandResult.Ok);
                break;

            case StopFleet c:
                if (_state.Stop(now)) StopAllRunners();
                c.Reply.TrySetResult(CommandResult.Ok);
                break;

            case RestartWorker c:
                var (result, epoch) = _state.Restart(c.WorkerId, now);
                if (result == CommandResult.Ok) StartRunner(c.WorkerId, epoch);
                c.Reply.TrySetResult(result);
                break;

            case Acknowledge c:
                c.Reply.TrySetResult(_state.Acknowledge(c.IncidentId, now));
                break;

            case Reset c:
                StopAllRunners();
                _state = NewState();
                c.Reply.TrySetResult(CommandResult.Ok);
                break;
        }
    }

    private void StartRunner(string workerId, int epoch)
    {
        StopRunner(workerId);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        _runners[workerId] = cts;
        var workerNumber = FleetState.WorkerIds.ToList().IndexOf(workerId) + 1;
        var rng = new Random(_options.Seed + workerNumber * 1000 + epoch);
        _ = Task.Run(() => WorkerRunner.RunAsync(workerId, epoch, this, _faults, _time, _options, rng, cts.Token));
    }

    private void StopRunner(string workerId)
    {
        if (_runners.Remove(workerId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void StopAllRunners()
    {
        foreach (var id in _runners.Keys.ToArray()) StopRunner(id);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TickInterval, _time, ct);
                await _inbox.Writer.WriteAsync(new TickMessage(), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<CommandResult> Send(Command command)
    {
        await _inbox.Writer.WriteAsync(command);
        return await command.Reply.Task;
    }

    private FleetState NewState() => new(_options, entry =>
        _logger.Log(entry.Level switch
        {
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        }, "{WorkerId} {Message}", entry.WorkerId, entry.Message));

    private FleetSnapshot BuildSnapshot() =>
        new(++_version, _state.Running, _heartbeatAt, _heartbeatAt, _state.WorkerViews(), _state.ActiveIncidents());

    private abstract record Message;
    private sealed record ReportMessage(Report Report) : Message;
    private sealed record TickMessage : Message;

    private abstract record Command : Message
    {
        public TaskCompletionSource<CommandResult> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record StartFleet : Command;
    private sealed record StopFleet : Command;
    private sealed record RestartWorker(string WorkerId) : Command;
    private sealed record Acknowledge(int IncidentId) : Command;
    private sealed record Reset : Command;
}
