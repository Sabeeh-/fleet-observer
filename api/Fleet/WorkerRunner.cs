namespace Api.Fleet;

/// <summary>Where workers send reports. Today an in-memory channel; the seam for a network transport later (ADR 0001).</summary>
public interface IReportSink
{
    ValueTask SubmitAsync(Report report, CancellationToken ct);
}

/// <summary>
/// The simulated observed process: produces primes 1..17 in a loop with a random delay,
/// applying whatever fault the fault source says. It only talks one way, to the sink.
/// </summary>
public static class WorkerRunner
{
    public static async Task RunAsync(
        string workerId, int epoch, IReportSink sink, IFaultSource faults, TimeProvider time,
        FleetOptions options, Random rng, CancellationToken ct)
    {
        var cycle = 1;
        var index = 1;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (faults.Get(workerId) == FaultMode.Unresponsive)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), time, ct);
                }

                var startedAt = time.GetUtcNow();
                var delayMs = faults.Get(workerId) == FaultMode.Slow
                    ? rng.Next(options.SlowMinDelayMs, options.SlowMaxDelayMs + 1)
                    : rng.Next(options.MinDelayMs, options.MaxDelayMs + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), time, ct);

                var value = Primes.Nth(index);
                if (faults.Get(workerId) == FaultMode.WrongAnswer) value += 1;

                await sink.SubmitAsync(new Report(workerId, epoch, cycle, index, value, startedAt), ct);

                if (++index > Primes.Count)
                {
                    index = 1;
                    cycle++;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped or restarted by the observer.
        }
    }
}
