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
    /// <paramref name="cutoff"/>. Each manifest is resolved to its chunk CIDs
    /// (best-effort: a manifest that no longer fetches yields just its own CID,
    /// so a re-run still releases the manifest pin).
    /// </summary>
    public static async Task<IReadOnlyList<Target>> PlanByAgeAsync(
        IFileRegistryClient fileRegistry,
        IIpfsClient ipfs,
        EthereumAddress me,
        DateTimeOffset cutoff,
        ulong fromBlock,
        ulong latestBlock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileRegistry);
        ArgumentNullException.ThrowIfNull(ipfs);
        ArgumentNullException.ThrowIfNull(me);

        var sent = await fileRegistry
            .GetSentAsync(me, fromBlock, latestBlock, cancellationToken).ConfigureAwait(false);

        var targets = new List<Target>();
        foreach (var ev in sent.Where(e => e.Timestamp < cutoff))
            targets.Add(await ResolveAsync(ipfs, ev.Cid, ev.Timestamp, ev.To, cancellationToken).ConfigureAwait(false));
        return targets;
    }

    /// <summary>
    /// Plan from an explicit set of manifest CIDs (no chain scan) — for
    /// releasing transfers the caller already knows are delivered.
    /// </summary>
    public static async Task<IReadOnlyList<Target>> PlanByCidsAsync(
        IIpfsClient ipfs,
        IEnumerable<string> manifestCids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ipfs);
        ArgumentNullException.ThrowIfNull(manifestCids);

        var targets = new List<Target>();
        foreach (var cid in manifestCids.Distinct(StringComparer.Ordinal))
            targets.Add(await ResolveAsync(ipfs, cid, sentAt: default, recipient: null, cancellationToken).ConfigureAwait(false));
        return targets;
    }

    /// <summary>
    /// Unpin every chunk CID and manifest CID in <paramref name="targets"/>,
    /// skipping any CID in <paramref name="protectedCids"/> (e.g. the agent's
    /// own registered ML-KEM key). Best-effort: a failed unpin is recorded and
    /// the run continues.
    /// </summary>
    public static async Task<Result> UnpinAsync(
        IIpfsClient ipfs,
        IReadOnlyList<Target> targets,
        IReadOnlySet<string>? protectedCids = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ipfs);
        ArgumentNullException.ThrowIfNull(targets);
        protectedCids ??= new HashSet<string>(StringComparer.Ordinal);

        var unpinned = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var target in targets)
        {
            // Chunks first, then the manifest — so a re-run after a mid-way
            // failure still reaches the manifest.
            foreach (var cid in target.ChunkCids.Append(target.ManifestCid))
            {
                if (protectedCids.Contains(cid)) { skipped++; continue; }
                try
                {
                    await ipfs.UnpinAsync(cid, cancellationToken).ConfigureAwait(false);
                    unpinned++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{cid}: {ex.GetType().Name} — {ex.Message}");
                }
            }
        }
        return new Result(targets.Count, unpinned, skipped, errors);
    }

    private static async Task<Target> ResolveAsync(
        IIpfsClient ipfs, string manifestCid, DateTimeOffset sentAt, EthereumAddress? recipient, CancellationToken ct)
    {
        try
        {
            var bytes = await ipfs.FetchAsync(manifestCid, ct).ConfigureAwait(false);
            var signed = SignedManifest.FromJsonBytes(bytes);
            var chunks = signed.Manifest.Chunks.Select(c => c.Cid).ToList();
            return new Target(manifestCid, sentAt, recipient ?? signed.Manifest.Recipient, chunks, ManifestResolved: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Manifest gone or unparseable — still release the manifest pin.
            return new Target(manifestCid, sentAt, recipient,
                Array.Empty<string>(), ManifestResolved: false);
        }
    }
}
