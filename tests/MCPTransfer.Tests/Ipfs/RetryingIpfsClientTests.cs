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

        public int PinCallCount;
        public int FetchCallCount;

        public void QueueSuccess(string cid)
            => _pinScript.Enqueue(new Outcome { PinResult = cid });

        public void QueueSuccess(byte[] bytes)
            => _fetchScript.Enqueue(new Outcome { FetchResult = bytes });

        public void QueueFailure(Exception ex)
        {
            _pinScript.Enqueue(new Outcome { Failure = ex });
            _fetchScript.Enqueue(new Outcome { Failure = ex });
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

        private sealed class Outcome
        {
            public Exception? Failure;
            public string? PinResult;
            public byte[]? FetchResult;
        }
    }
}
