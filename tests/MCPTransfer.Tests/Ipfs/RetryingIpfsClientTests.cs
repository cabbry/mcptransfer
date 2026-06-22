using System.Collections.Concurrent;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Tests.Ipfs;

public class RetryingIpfsClientTests
{
    private static readonly RetryPolicy FastPolicy = new()
    {
        MaxAttempts = 4,
        InitialDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(4),
        BackoffMultiplier = 2.0,
        JitterFraction = 0, // deterministic timing for assertions
    };

    [Fact]
    public async Task Pin_Succeeds_OnFirstTry_NoRetry()
    {
        var inner = new ScriptedIpfsClient();
        inner.QueueSuccess("cid-A");
        var client = new RetryingIpfsClient(inner, FastPolicy);

        var cid = await client.PinAsync(new byte[10]);

        Assert.Equal("cid-A", cid);
        Assert.Equal(1, inner.PinCallCount);
    }

    [Fact]
    public async Task Pin_RetriesOnTransientFailure_ThenSucceeds()
    {
        var inner = new ScriptedIpfsClient();
        inner.QueueFailure(new TimeoutException("transient"));
        inner.QueueFailure(new TimeoutException("transient"));
        inner.QueueSuccess("cid-B");
        var client = new RetryingIpfsClient(inner, FastPolicy);

        var cid = await client.PinAsync(new byte[10]);

        Assert.Equal("cid-B", cid);
        Assert.Equal(3, inner.PinCallCount);
    }

    [Fact]
    public async Task Pin_DoesNotRetryWhenPolicyRejects()
    {
        var inner = new ScriptedIpfsClient();
        inner.QueueFailure(new ArgumentException("permanent: bad input"));
        var client = new RetryingIpfsClient(inner, FastPolicy);

        await Assert.ThrowsAsync<ArgumentException>(() => client.PinAsync(new byte[10]));

        Assert.Equal(1, inner.PinCallCount);
    }

    [Fact]
    public async Task Pin_ThrowsAfterMaxAttempts()
    {
        var inner = new ScriptedIpfsClient();
        inner.QueueFailure(new TimeoutException("attempt 1"));
        inner.QueueFailure(new TimeoutException("attempt 2"));
        inner.QueueFailure(new TimeoutException("attempt 3"));
        inner.QueueFailure(new TimeoutException("attempt 4"));
        var client = new RetryingIpfsClient(inner, FastPolicy); // MaxAttempts = 4

        await Assert.ThrowsAsync<TimeoutException>(() => client.PinAsync(new byte[10]));
        Assert.Equal(4, inner.PinCallCount);
    }

    [Fact]
    public async Task Unpin_RetriesOnTransientFailure_ThenSucceeds()
    {
        var inner = new ScriptedIpfsClient();
        inner.QueueFailure(new TimeoutException("transient"));
        inner.QueueUnpinSuccess();
        var client = new RetryingIpfsClient(inner, FastPolicy);

        await client.UnpinAsync("cid-Z");

        Assert.Equal(2, inner.UnpinCallCount);
    }

    [Fact]
    public async Task Fetch_RetriesOnTransientFailure()
    {
        var inner = new ScriptedIpfsClient();
        inner.QueueFailure(new TimeoutException("transient"));
        inner.QueueSuccess(new byte[] { 1, 2, 3 });
        var client = new RetryingIpfsClient(inner, FastPolicy);

        var bytes = await client.FetchAsync("cid-X");

        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        Assert.Equal(2, inner.FetchCallCount);
    }

    [Fact]
    public async Task Fetch_DoesNotRetryKeyNotFound()
    {
        var inner = new ScriptedIpfsClient();
        inner.QueueFailure(new KeyNotFoundException("cid not pinned"));
        var client = new RetryingIpfsClient(inner, FastPolicy);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.FetchAsync("cid-missing"));
        Assert.Equal(1, inner.FetchCallCount);
    }

    [Fact]
    public async Task Cancellation_StopsRetryDelay()
    {
        var inner = new ScriptedIpfsClient();
        // Queue many failures so a non-cancelled call would retry several times.
        for (var i = 0; i < 10; i++)
            inner.QueueFailure(new TimeoutException("transient"));

        var policy = new RetryPolicy
        {
            MaxAttempts = 10,
            InitialDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromSeconds(60),
            BackoffMultiplier = 2.0,
        };
        var client = new RetryingIpfsClient(inner, policy);

        using var cts = new CancellationTokenSource();
        var task = client.PinAsync(new byte[10], cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<TaskCanceledException>(() => task);
    }

    [Fact]
    public void Constructor_RejectsZeroMaxAttempts()
    {
        var inner = new ScriptedIpfsClient();
        var policy = new RetryPolicy { MaxAttempts = 0 };
        Assert.Throws<ArgumentException>(() => new RetryingIpfsClient(inner, policy));
    }

    [Fact]
    public void Dispose_ForwardsToInnerDisposableClient()
    {
        var inner = new DisposableIpfsClient();
        var client = new RetryingIpfsClient(inner, FastPolicy);

        Assert.False(inner.Disposed);
        client.Dispose();
        Assert.True(inner.Disposed);
    }

    [Fact]
    public void Dispose_NoThrow_WhenInnerNotDisposable()
    {
        // ScriptedIpfsClient is not IDisposable — Dispose must be a no-op, not throw.
        var client = new RetryingIpfsClient(new ScriptedIpfsClient(), FastPolicy);
        client.Dispose();
    }

    private sealed class DisposableIpfsClient : IIpfsClient, IDisposable
    {
        public bool Disposed { get; private set; }
        public Task<string> PinAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
            => Task.FromResult("cid");
        public Task<byte[]> FetchAsync(string cid, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<byte>());
        public Task UnpinAsync(string cid, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Constructor_RejectsOutOfRangeJitterFraction()
    {
        var inner = new ScriptedIpfsClient();
        Assert.Throws<ArgumentException>(
            () => new RetryingIpfsClient(inner, new RetryPolicy { JitterFraction = -0.1 }));
        Assert.Throws<ArgumentException>(
            () => new RetryingIpfsClient(inner, new RetryPolicy { JitterFraction = 1.1 }));
    }

    [Fact]
    public void ApplyJitter_WithZeroFraction_ReturnsInputUnchanged()
    {
        var d = TimeSpan.FromSeconds(2);
        Assert.Equal(d, RetryingIpfsClient.ApplyJitter(d, 0.0));
    }

    [Fact]
    public void ApplyJitter_WithFraction_StaysWithinBounds()
    {
        var d = TimeSpan.FromSeconds(2);
        const double jitter = 0.2;
        // Run many samples; every one must lie in [d * (1 - jitter), d * (1 + jitter)].
        for (var i = 0; i < 200; i++)
        {
            var jittered = RetryingIpfsClient.ApplyJitter(d, jitter);
            Assert.InRange(
                jittered.TotalMilliseconds,
                d.TotalMilliseconds * (1 - jitter) - 0.001,
                d.TotalMilliseconds * (1 + jitter) + 0.001);
        }
    }

    [Fact]
    public void ApplyJitter_WithFraction_DistributesAcrossRange()
    {
        // Ensure the multiplier is actually random, not stuck at a single value.
        var d = TimeSpan.FromSeconds(2);
        var samples = new HashSet<double>();
        for (var i = 0; i < 50; i++)
        {
            samples.Add(Math.Round(RetryingIpfsClient.ApplyJitter(d, 0.5).TotalMilliseconds, 1));
        }
        Assert.True(samples.Count > 10,
            $"Expected jitter to spread samples; got only {samples.Count} distinct values.");
    }

    [Fact]
    public async Task DefaultPolicy_IsApplied_WhenNullPassed()
    {
        var inner = new ScriptedIpfsClient();
        inner.QueueSuccess("cid-default");
        var client = new RetryingIpfsClient(inner); // no policy -> Default

        var cid = await client.PinAsync(new byte[1]);
        Assert.Equal("cid-default", cid);
    }

    // --- helper: an IIpfsClient driven by a script of outcomes ---

    private sealed class ScriptedIpfsClient : IIpfsClient
    {
        private readonly ConcurrentQueue<Outcome> _pinScript = new();
        private readonly ConcurrentQueue<Outcome> _fetchScript = new();
        private readonly ConcurrentQueue<Outcome> _unpinScript = new();

        public int PinCallCount;
        public int FetchCallCount;
        public int UnpinCallCount;

        public void QueueSuccess(string cid)
            => _pinScript.Enqueue(new Outcome { PinResult = cid });

        public void QueueSuccess(byte[] bytes)
            => _fetchScript.Enqueue(new Outcome { FetchResult = bytes });

        public void QueueUnpinSuccess()
            => _unpinScript.Enqueue(new Outcome());

        public void QueueFailure(Exception ex)
        {
            _pinScript.Enqueue(new Outcome { Failure = ex });
            _fetchScript.Enqueue(new Outcome { Failure = ex });
            _unpinScript.Enqueue(new Outcome { Failure = ex });
        }

        public Task<string> PinAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PinCallCount);
            if (!_pinScript.TryDequeue(out var outcome))
                throw new InvalidOperationException("PinAsync called with no script entry.");
            if (outcome.Failure is not null)
                return Task.FromException<string>(outcome.Failure);
            return Task.FromResult(outcome.PinResult!);
        }

        public Task<byte[]> FetchAsync(string cid, CancellationToken ct = default)
        {
            Interlocked.Increment(ref FetchCallCount);
            if (!_fetchScript.TryDequeue(out var outcome))
                throw new InvalidOperationException("FetchAsync called with no script entry.");
            if (outcome.Failure is not null)
                return Task.FromException<byte[]>(outcome.Failure);
            return Task.FromResult(outcome.FetchResult!);
        }

        public Task UnpinAsync(string cid, CancellationToken ct = default)
        {
            Interlocked.Increment(ref UnpinCallCount);
            if (!_unpinScript.TryDequeue(out var outcome))
                throw new InvalidOperationException("UnpinAsync called with no script entry.");
            return outcome.Failure is not null ? Task.FromException(outcome.Failure) : Task.CompletedTask;
        }

        private sealed class Outcome
        {
            public Exception? Failure;
            public string? PinResult;
            public byte[]? FetchResult;
        }
    }
}
