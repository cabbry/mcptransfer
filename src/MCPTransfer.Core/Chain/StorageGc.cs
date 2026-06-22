using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Envelope;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Storage garbage-collection: unpins the IPFS content of transfers this agent
/// has SENT, so the data plane stays an ephemeral mailbox rather than an
/// archive (white paper §8). The sender pinned the chunks + manifest, so the
/// sender releases them once recipients have had time to fetch.
/// </summary>
/// <remarks>
/// Confidentiality of any copies that persist on third-party nodes does NOT
/// depend on this deletion — it rests on the hybrid-PQC envelope; gc is about
/// not paying to host transfers forever. The chunk and manifest CIDs are
/// unique per transfer (fresh per-send randomness), so unpinning one transfer
/// never affects another. A registered agent's ML-KEM key blob is standing
/// infrastructure (published via <c>KeyRegistry</c>, not a transfer) and must
/// never be collected — pass it as a protected CID.
/// </remarks>
public static class StorageGc
{
    /// <summary>Default in-flight IPFS operations (matches the envelope I/O layer).</summary>
    public const int DefaultMaxParallelism = 4;

    /// <summary>One transfer's CIDs to release. <see cref="Recipient"/> is null
    /// when the manifest could not be resolved and the caller did not supply it
    /// (the by-CID path).</summary>
    public sealed record Target(
        string ManifestCid,
        DateTimeOffset SentAt,
        EthereumAddress? Recipient,
        IReadOnlyList<string> ChunkCids,
        bool ManifestResolved);

    /// <summary>Outcome of an unpin run.</summary>
    public sealed record Result(int TransfersProcessed, int CidsUnpinned, int CidsSkipped, IReadOnlyList<string> Errors);

    /// <summary>
    /// Plan from sent transfers whose on-chain announcement is older than
    /// <paramref name="cutoff"/>. The block range [<paramref name="fromBlock"/>,
    /// <paramref name="toBlock"/>] is scanned by paging forward in
    /// <paramref name="windowSize"/>-block windows (so deep history is reached
    /// on an RPC that accepts that span; a provider that caps <c>eth_getLogs</c>
    /// below it surfaces as a thrown exception the caller can report). Manifests
    /// are resolved to their chunk CIDs with bounded parallelism (best-effort: a
    /// manifest that no longer fetches or fails signature verification yields
    /// just its own CID, so a re-run still releases the manifest pin).
    /// </summary>
    public static async Task<IReadOnlyList<Target>> PlanByAgeAsync(
        IFileRegistryClient fileRegistry,
        IIpfsClient ipfs,
        EthereumAddress me,
        DateTimeOffset cutoff,
        ulong fromBlock,
        ulong toBlock,
        ulong windowSize = InboxWindow.MaxSpan,
        int maxParallelism = DefaultMaxParallelism,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileRegistry);
        ArgumentNullException.ThrowIfNull(ipfs);
        ArgumentNullException.ThrowIfNull(me);

        var sent = await fileRegistry
            .GetSentPagedAsync(me, fromBlock, toBlock, windowSize, cancellationToken).ConfigureAwait(false);

        var aged = sent.Where(e => e.Timestamp < cutoff).ToList();
        return await ResolveManyAsync(
            ipfs, aged.Select(e => (e.Cid, e.Timestamp, (EthereumAddress?)e.To)).ToList(),
            maxParallelism, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Plan from an explicit set of manifest CIDs (no chain scan) — for
    /// releasing transfers the caller already knows are delivered. Resolved
    /// with bounded parallelism.
    /// </summary>
    public static async Task<IReadOnlyList<Target>> PlanByCidsAsync(
        IIpfsClient ipfs,
        IEnumerable<string> manifestCids,
        int maxParallelism = DefaultMaxParallelism,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ipfs);
        ArgumentNullException.ThrowIfNull(manifestCids);

        var items = manifestCids
            .Distinct(StringComparer.Ordinal)
            .Select(cid => (cid, default(DateTimeOffset), (EthereumAddress?)null))
            .ToList();
        return await ResolveManyAsync(ipfs, items, maxParallelism, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Unpin every chunk CID and manifest CID in <paramref name="targets"/>,
    /// skipping any CID in <paramref name="protectedCids"/> (e.g. the agent's
    /// own registered ML-KEM key). CIDs are released with bounded parallelism;
    /// each unpin is independent and idempotent, so ordering does not matter.
    /// Best-effort: a failed unpin is recorded and the run continues.
    /// </summary>
    public static async Task<Result> UnpinAsync(
        IIpfsClient ipfs,
        IReadOnlyList<Target> targets,
        IReadOnlySet<string>? protectedCids = null,
        int maxParallelism = DefaultMaxParallelism,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ipfs);
        ArgumentNullException.ThrowIfNull(targets);
        protectedCids ??= new HashSet<string>(StringComparer.Ordinal);

        var skipped = 0;
        var toRelease = new List<string>();
        foreach (var target in targets)
        {
            foreach (var cid in target.ChunkCids.Append(target.ManifestCid))
            {
                if (protectedCids.Contains(cid)) { skipped++; continue; }
                toRelease.Add(cid);
            }
        }
        // De-dup so a CID shared by two targets (or a manifest that is also a
        // chunk) is released — and counted — once.
        var distinct = toRelease.Distinct(StringComparer.Ordinal).ToList();

        var outcomes = await RunBoundedAsync(distinct, maxParallelism, async (cid, token) =>
        {
            try
            {
                await ipfs.UnpinAsync(cid, token).ConfigureAwait(false);
                return (ok: true, error: (string?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (ok: false, error: $"{cid}: {ex.GetType().Name} — {ex.Message}");
            }
        }, cancellationToken).ConfigureAwait(false);

        var unpinned = outcomes.Count(o => o.ok);
        var errors = outcomes.Where(o => !o.ok).Select(o => o.error!).ToList();
        return new Result(targets.Count, unpinned, skipped, errors);
    }

    private static async Task<IReadOnlyList<Target>> ResolveManyAsync(
        IIpfsClient ipfs,
        IReadOnlyList<(string Cid, DateTimeOffset SentAt, EthereumAddress? Recipient)> items,
        int maxParallelism,
        CancellationToken ct)
        => await RunBoundedAsync(items, maxParallelism,
                (item, token) => ResolveAsync(ipfs, item.Cid, item.SentAt, item.Recipient, token), ct)
            .ConfigureAwait(false);

    private static async Task<Target> ResolveAsync(
        IIpfsClient ipfs, string manifestCid, DateTimeOffset sentAt, EthereumAddress? recipient, CancellationToken ct)
    {
        try
        {
            var bytes = await ipfs.FetchAsync(manifestCid, ct).ConfigureAwait(false);
            var signed = SignedManifest.FromJsonBytes(bytes);
            // Only trust the chunk list of a manifest whose signature verifies —
            // a tampering gateway returning altered bytes (different chunk CIDs)
            // would otherwise drive which of our pins get released.
            if (!signed.VerifySignature())
                return new Target(manifestCid, sentAt, recipient, Array.Empty<string>(), ManifestResolved: false);

            var chunks = signed.Manifest.Chunks.Select(c => c.Cid).ToList();
            return new Target(manifestCid, sentAt, recipient ?? signed.Manifest.Recipient, chunks, ManifestResolved: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Manifest gone or unparseable — still release the manifest pin.
            return new Target(manifestCid, sentAt, recipient, Array.Empty<string>(), ManifestResolved: false);
        }
    }

    /// <summary>
    /// Run <paramref name="body"/> over <paramref name="items"/> with at most
    /// <paramref name="maxParallelism"/> in flight, preserving input order in
    /// the result (mirrors the envelope I/O layer's semaphore pattern).
    /// </summary>
    private static async Task<TResult[]> RunBoundedAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        int maxParallelism,
        Func<TItem, CancellationToken, Task<TResult>> body,
        CancellationToken ct)
    {
        if (maxParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(maxParallelism), "Max parallelism must be at least 1.");
        if (items.Count == 0)
            return Array.Empty<TResult>();

        using var semaphore = new SemaphoreSlim(maxParallelism, maxParallelism);
        var tasks = new Task<TResult>[items.Count];
        for (var i = 0; i < items.Count; i++)
            tasks[i] = RunOneAsync(items[i]);
        return await Task.WhenAll(tasks).ConfigureAwait(false);

        async Task<TResult> RunOneAsync(TItem item)
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try { return await body(item, ct).ConfigureAwait(false); }
            finally { semaphore.Release(); }
        }
    }
}
