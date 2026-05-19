namespace MCPTransfer.Core.Ipfs;

/// <summary>
/// Decorator that transparently retries Pin / Fetch operations on the
/// wrapped <see cref="IIpfsClient"/> using a configurable
/// <see cref="RetryPolicy"/>. The decorator is orthogonal to the
/// envelope-level drain pattern: it masks transient failures (network
/// glitches, 429 rate limits, 5xx) so the orchestrator only ever sees
/// truly permanent failures.
/// </summary>
public sealed class RetryingIpfsClient : IIpfsClient
{
    private readonly IIpfsClient _inner;
    private readonly RetryPolicy _policy;

    public RetryingIpfsClient(IIpfsClient inner, RetryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _policy = policy ?? RetryPolicy.Default;

        if (_policy.MaxAttempts < 1)
            throw new ArgumentException(
                $"RetryPolicy.MaxAttempts must be at least 1 (got {_policy.MaxAttempts}).",
                nameof(policy));
        if (_policy.JitterFraction is < 0.0 or > 1.0)
            throw new ArgumentException(
                $"RetryPolicy.JitterFraction must be in [0, 1] (got {_policy.JitterFraction}).",
                nameof(policy));
    }

    public Task<string> PinAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => RetryAsync(token => _inner.PinAsync(data, token), cancellationToken);

    public Task<byte[]> FetchAsync(string cid, CancellationToken cancellationToken = default)
        => RetryAsync(token => _inner.FetchAsync(cid, token), cancellationToken);

    private async Task<T> RetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        var delay = _policy.InitialDelay;

        while (true)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                attempt < _policy.MaxAttempts - 1
                && !cancellationToken.IsCancellationRequested
                && _policy.ShouldRetry(ex))
            {
                attempt++;
                await Task.Delay(ApplyJitter(delay, _policy.JitterFraction), cancellationToken).ConfigureAwait(false);

                var nextMs = delay.TotalMilliseconds * _policy.BackoffMultiplier;
                if (nextMs > _policy.MaxDelay.TotalMilliseconds)
                    nextMs = _policy.MaxDelay.TotalMilliseconds;
                delay = TimeSpan.FromMilliseconds(nextMs);
            }
        }
    }

    /// <summary>
    /// Returns <paramref name="delay"/> scaled by a uniformly random factor
    /// in <c>[1 − jitter, 1 + jitter]</c>. With <paramref name="jitter"/> = 0
    /// the delay is returned unchanged (deterministic mode for tests).
    /// </summary>
    internal static TimeSpan ApplyJitter(TimeSpan delay, double jitter)
    {
        if (jitter <= 0.0)
            return delay;

        // Uniform in [-jitter, +jitter], scaled around 1.
        var multiplier = 1.0 + (Random.Shared.NextDouble() * 2.0 - 1.0) * jitter;
        var jitteredMs = delay.TotalMilliseconds * multiplier;
        if (jitteredMs < 0)
            jitteredMs = 0;
        return TimeSpan.FromMilliseconds(jitteredMs);
    }
}
