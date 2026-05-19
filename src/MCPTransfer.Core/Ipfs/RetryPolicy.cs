namespace MCPTransfer.Core.Ipfs;

/// <summary>
/// Retry behaviour applied by <see cref="RetryingIpfsClient"/> around each
/// Pin / Fetch call. Exponential backoff capped at <see cref="MaxDelay"/>.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>Total number of attempts including the first one. Must be ≥ 1.</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>Delay before the second attempt. Doubles (or multiplies by
    /// <see cref="BackoffMultiplier"/>) on each subsequent failure.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Cap on the delay between attempts.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Multiplicative growth factor applied to the delay after each failure.</summary>
    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>
    /// Random spread applied to each computed delay, as a fraction in
    /// <c>[0, 1]</c>. The actual sleep is uniformly sampled from
    /// <c>delay × [1 − jitter, 1 + jitter]</c>. Defends against the
    /// thundering-herd pattern where several parallel chunks retry in
    /// lockstep and re-saturate the same gateway. Default: <c>0.2</c> (±20%).
    /// Set to <c>0</c> for deterministic timing (tests).
    /// </summary>
    public double JitterFraction { get; init; } = 0.2;

    /// <summary>
    /// Predicate that decides whether a given exception is worth retrying.
    /// Defaults to <see cref="DefaultShouldRetry"/>, which skips obvious
    /// non-transient errors (cancellation, programming errors, not-found).
    /// </summary>
    public Func<Exception, bool> ShouldRetry { get; init; } = DefaultShouldRetry;

    /// <summary>Default retry predicate: anything that is not clearly permanent.</summary>
    public static bool DefaultShouldRetry(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is not OperationCanceledException
            && exception is not ArgumentException
            && exception is not KeyNotFoundException
            && exception is not ObjectDisposedException;
    }

    public static RetryPolicy Default { get; } = new();
}
