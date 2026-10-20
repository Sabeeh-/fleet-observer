using Api.Fleet;

namespace Api.Tests;

public class FleetStateTests
{
    private const string W = "worker-1";
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly FleetState _state = new();
    private DateTimeOffset _now = T0;
    private int _cycle = 1;
    private int _index;

    public FleetStateTests() => _state.Start(_now);

    private Worker Worker => _state.Get(W);
    private Incident Single => Assert.Single(_state.Incidents, i => i.WorkerId == W);
    private IEnumerable<string> LogMessages => Worker.Log.Select(e => e.Message);

    /// <summary>Sends the next report in sequence. Value defaults to the correct prime.</summary>
    private void Report(int latencyMs = 100, long? value = null, int? epoch = null)
    {
        if (++_index > Primes.Count) { _index = 1; _cycle++; }
        _now += TimeSpan.FromMilliseconds(latencyMs);
        var startedAt = _now - TimeSpan.FromMilliseconds(latencyMs);
        _state.Accept(new Report(W, epoch ?? Worker.Epoch, _cycle, _index, value ?? Primes.First17[_index - 1], startedAt), _now);
    }

    private void Wrong() => Report(value: Primes.First17[(_index % Primes.Count)] + 1);

    private void GoSilent(int ms = 3100)
    {
        _now += TimeSpan.FromMilliseconds(ms);
        _state.Tick(_now);
    }

    private void ResetSequence() { _cycle = 1; _index = 0; }

    [Fact]
    public void Fleet_has_five_workers_with_fixed_ids()
    {
        Assert.Equal(["worker-1", "worker-2", "worker-3", "worker-4", "worker-5"], _state.Workers.Select(w => w.Id));
        Assert.All(_state.Workers, w => Assert.Equal(WorkerStatus.Starting, w.Status));
    }

    [Fact]
    public void First_ok_report_makes_worker_healthy()
    {
        Report();
        Assert.Equal(WorkerStatus.Healthy, Worker.Status);
        Assert.Contains("Starting → Healthy", LogMessages);
    }

    [Fact]
    public void Cycle_wraps_from_17_to_1_and_increments()
    {
        for (var i = 0; i < Primes.Count; i++) Report();
        Assert.Equal((1, 17), (Worker.Cycle, Worker.LastIndex));

        Report();
        Assert.Equal((2, 1), (Worker.Cycle, Worker.LastIndex));
        Assert.Equal(WorkerStatus.Healthy, Worker.Status);
    }

    [Fact]
    public void Out_of_order_report_is_ignored_and_logged()
    {
        Report();
        _state.Accept(new Report(W, Worker.Epoch, 1, 5, 11, _now), _now);

        Assert.Equal(1, Worker.LastIndex);
        Assert.Contains(LogMessages, m => m.StartsWith("Out-of-order report"));
    }

    [Fact]
    public void Observer_checks_value_itself_and_opens_wrong_answer_incident()
    {
        Report(); Report(); Report(); Report();
        Report(value: 12); // #5 should be 11

        Assert.Equal(WorkerStatus.WrongAnswer, Worker.Status);
        Assert.Contains("cycle 1 #5 expected 11 got 12", LogMessages);
        Assert.Contains("Healthy → Wrong answer", LogMessages);
        Assert.Equal((IncidentKind.WrongAnswer, IncidentState.Open), (Single.Kind, Single.State));
    }

    [Fact]
    public void A_prime_at_the_wrong_index_is_still_a_wrong_answer()
    {
        Report(value: 3); // #1 should be 2; 3 is prime but not the expected one
        Assert.Equal(WorkerStatus.WrongAnswer, Worker.Status);
    }

    [Fact]
    public void Repeated_wrong_answers_keep_one_incident_and_do_not_flood_the_log()
    {
        Report();
        Wrong(); Wrong(); Wrong();

        Assert.Single(_state.Incidents);
        Assert.Single(LogMessages, m => m.Contains("expected"));
    }

    [Fact]
    public void Slow_report_is_a_warning_without_an_incident()
    {
        Report();
        Report(latencyMs: 700);

        Assert.Equal(WorkerStatus.Slow, Worker.Status);
        Assert.Empty(_state.Incidents);
        Assert.Contains(Worker.Log, e => e.Message == "Healthy → Slow" && e.Level == LogSeverity.Warning);

        Report();
        Assert.Equal(WorkerStatus.Healthy, Worker.Status);
    }

    [Fact]
    public void Exactly_500ms_is_not_slow()
    {
        Report(latencyMs: 500);
        Assert.Equal(WorkerStatus.Healthy, Worker.Status);
    }

    [Fact]
    public void Silence_over_three_seconds_opens_unresponsive_incident()
    {
        Report();
        GoSilent(2900);
        Assert.Equal(WorkerStatus.Healthy, Worker.Status);

        GoSilent(200);
        Assert.Equal(WorkerStatus.Unresponsive, Worker.Status);
        Assert.Equal((IncidentKind.Unresponsive, IncidentState.Open), (Single.Kind, Single.State));
    }

    [Fact]
    public void Recovery_needs_three_consecutive_ok_reports()
    {
        Report();
        Wrong();
        Report(); Report();
        Wrong(); // streak broken
        Report(); Report();
        Assert.Equal(WorkerStatus.WrongAnswer, Worker.Status);

        Report();
        Assert.Equal(WorkerStatus.Healthy, Worker.Status);
    }

    [Fact]
    public void Recovered_incident_stays_active_until_acknowledged()
    {
        Report();
        GoSilent();
        Report(); Report(); Report();

        Assert.Equal(WorkerStatus.Healthy, Worker.Status);
        Assert.Equal(IncidentState.Recovered, Single.State);
        Assert.Single(_state.ActiveIncidents(), i => i.WorkerId == W);

        Assert.Equal(CommandResult.Ok, _state.Acknowledge(Single.Id, _now));
        Assert.Equal(IncidentState.Resolved, Single.State);
        Assert.DoesNotContain(_state.ActiveIncidents(), i => i.WorkerId == W);

        var messages = LogMessages.ToList();
        var order = new[] { "Healthy → Unresponsive", "Unresponsive → Healthy", "Incident resolved: Unresponsive" }
            .Select(m => messages.IndexOf(m)).ToList();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.Order(), order);
    }

    [Fact]
    public void Acknowledging_an_unhealthy_worker_does_not_resolve()
    {
        Report();
        Wrong();
        _state.Acknowledge(Single.Id, _now);
        Assert.Equal(IncidentState.Acknowledged, Single.State);

        Wrong(); Report(); Report();
        Assert.Equal(IncidentState.Acknowledged, Single.State);

        Report();
        Assert.Equal(IncidentState.Resolved, Single.State);
        Assert.Equal("Incident resolved: Wrong answer", LogMessages.Last());
    }

    [Fact]
    public void Same_fault_recurring_before_ack_reopens_the_incident()
    {
        Report();
        Wrong();
        Report(); Report(); Report();
        Assert.Equal(IncidentState.Recovered, Single.State);

        Wrong();
        Assert.Equal(IncidentState.Open, Single.State);
        Assert.Contains("Incident reopened: Wrong answer", LogMessages);
    }

    [Fact]
    public void Acknowledge_is_idempotent_and_unknown_incident_is_not_found()
    {
        Report();
        Wrong();
        _state.Acknowledge(Single.Id, _now);
        var logCount = Worker.Log.Count;

        Assert.Equal(CommandResult.Ok, _state.Acknowledge(Single.Id, _now));
        Assert.Equal(logCount, Worker.Log.Count);
        Assert.Equal(CommandResult.NotFound, _state.Acknowledge(999, _now));
    }

    [Fact]
    public void Restart_keeps_id_drops_stale_reports_and_does_not_resolve_incident()
    {
        Report();
        Wrong();
        var oldEpoch = Worker.Epoch;

        var (result, epoch) = _state.Restart(W, _now);
        ResetSequence();

        Assert.Equal(CommandResult.Ok, result);
        Assert.Equal(W, Worker.Id);
        Assert.Equal(WorkerStatus.Starting, Worker.Status);
        Assert.Equal(oldEpoch + 1, epoch);
        Assert.NotEqual(IncidentState.Resolved, Single.State);

        Report(epoch: oldEpoch); // late report from the old run
        Assert.Equal(WorkerStatus.Starting, Worker.Status);
        Assert.Equal(0, Worker.LastIndex);

        ResetSequence();
        Report();
        Assert.Equal(WorkerStatus.Healthy, Worker.Status);
        Assert.Equal(IncidentState.Recovered, Single.State);
    }

    [Fact]
    public void Restart_while_stopped_is_a_conflict()
    {
        _state.Stop(_now);
        Assert.Equal(CommandResult.Conflict, _state.Restart(W, _now).Result);
        Assert.Equal(CommandResult.NotFound, new FleetState().Restart("worker-9", _now).Result);
    }

    [Fact]
    public void Stopped_fleet_never_goes_unresponsive_and_ignores_reports()
    {
        Report();
        _state.Stop(_now);
        var logCount = Worker.Log.Count;

        GoSilent(10_000);
        Report();

        Assert.All(_state.Workers, w => Assert.Equal(WorkerStatus.Stopped, w.Status));
        Assert.Equal(logCount, Worker.Log.Count);
        Assert.Empty(_state.Incidents);
    }

    [Fact]
    public void Log_keeps_last_ten_with_increasing_seq_and_only_own_worker()
    {
        Report();
        for (var i = 0; i < 8; i++) { Report(latencyMs: 700); Report(); } // 16 status changes

        Assert.Equal(10, Worker.Log.Count);
        var seqs = Worker.Log.Select(e => e.Seq).ToList();
        Assert.Equal(seqs.Order(), seqs);
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
        Assert.All(Worker.Log, e => Assert.Equal(W, e.WorkerId));
        Assert.All(_state.Get("worker-2").Log, e => Assert.Equal("worker-2", e.WorkerId));
    }
}
