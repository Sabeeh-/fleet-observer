using System.Collections.Concurrent;

namespace Api.Fleet;

/// <summary>Decides how a simulated worker misbehaves right now.</summary>
public interface IFaultSource
{
    FaultMode Get(string workerId);
}

/// <summary>Testing environment: faults change only when a test sets them.</summary>
public sealed class ScriptedFaultSource : IFaultSource
{
    private readonly ConcurrentDictionary<string, FaultMode> _faults = new();

    public FaultMode Get(string workerId) => _faults.GetValueOrDefault(workerId, FaultMode.None);
    public void Set(string workerId, FaultMode mode) => _faults[workerId] = mode;
    public void Clear() => _faults.Clear();
}

/// <summary>
/// Demo chaos: one fault episode somewhere in the fleet every 15–20 s, lasting 2–6 s.
/// Seeded, so a run can be reproduced.
/// </summary>
public sealed class RandomFaultSource(TimeProvider time, FleetOptions options) : IFaultSource
{
    private static readonly FaultMode[] Modes = [FaultMode.Slow, FaultMode.WrongAnswer, FaultMode.Unresponsive];

    private readonly Lock _gate = new();
    private readonly Random _rng = new(options.Seed);
    private DateTimeOffset? _nextEpisodeAt;
    private (string WorkerId, FaultMode Mode, DateTimeOffset Until)? _episode;

    public FaultMode Get(string workerId)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            _nextEpisodeAt ??= now + Gap();

            if (_episode is { } ended && now >= ended.Until)
            {
                _episode = null;
                _nextEpisodeAt = now + Gap();
            }

            if (_episode is null && now >= _nextEpisodeAt)
            {
                var target = FleetState.WorkerIds[_rng.Next(FleetState.WorkerIds.Count)];
                _episode = (target, Modes[_rng.Next(Modes.Length)], now + TimeSpan.FromSeconds(2 + _rng.NextDouble() * 4));
            }

            return _episode is { } e && e.WorkerId == workerId ? e.Mode : FaultMode.None;
        }
    }

    private TimeSpan Gap() => TimeSpan.FromSeconds(15 + _rng.NextDouble() * 5);
}
