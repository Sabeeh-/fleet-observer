namespace Api.Fleet;

public static class Primes
{
    public const int Count = 17;

    /// <summary>The observer's reference table. Kept separate from <see cref="Nth"/> so verdicts never depend on worker code.</summary>
    public static readonly IReadOnlyList<long> First17 = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59];

    /// <summary>What a worker runs: generates the n-th prime (1-based) by trial division.</summary>
    public static long Nth(int n)
    {
        var found = 0;
        for (long candidate = 2; ; candidate++)
        {
            if (IsPrime(candidate) && ++found == n) return candidate;
        }
    }

    private static bool IsPrime(long n)
    {
        if (n < 2) return false;
        for (long d = 2; d * d <= n; d++)
        {
            if (n % d == 0) return false;
        }
        return true;
    }
}
