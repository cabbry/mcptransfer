using MCPTransfer.Core.Chain;

namespace MCPTransfer.Agent.Commands;

/// <summary>
/// <c>mcptx gc</c> — release (unpin) the IPFS content of transfers this agent
/// has sent, so the data plane stays an ephemeral mailbox rather than an
/// archive. Two modes: by age (<c>--older-than</c>, pages the chain for this
/// agent's <c>FileSent</c> events) or by explicit manifest CID (<c>--cid</c>,
/// no chain scan — reliable on any RPC). The agent's own registered ML-KEM key
/// blob is always protected.
/// </summary>
internal static class GcCommand
{
    /// <summary>Largest practical age window (years). Beyond this the
    /// <c>UtcNow - duration</c> cutoff would underflow and the value is almost
    /// certainly a mistake.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(36_500); // ~100 years

    public const string Usage =
        "  mcptx gc [--older-than DUR] [--cid CID]... [--dry-run] [--since BLOCK] [--identity PATH] [--config PATH]\n"
      + "      Unpin the IPFS content of transfers YOU sent, so files don't live\n"
      + "      forever. DUR is like 30d / 12h / 90m / 3600s (must be > 0). --cid\n"
      + "      targets a known manifest CID directly (repeatable; works on any RPC).\n"
      + "      --since sets the first block of the age scan (default 0). --dry-run\n"
      + "      shows what would be released without unpinning. Run --dry-run first.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var olderThanRaw = Common.GetFlagValue(args, "--older-than");
        var cids = Common.GetFlagValues(args, "--cid");
        var dryRun = Common.HasFlag(args, "--dry-run");

        if (olderThanRaw is null && cids.Count == 0)
            return Common.Fail("specify --older-than DUR (e.g. 30d) and/or one or more --cid CID.");

        TimeSpan? olderThan = null;
        if (olderThanRaw is not null)
        {
            if (!TryParseDuration(olderThanRaw, out var dur, out var durError))
                return Common.Fail(durError!);
            olderThan = dur;
        }

        var identity = await Common.TryLoadIdentityAsync(args, ct).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;
        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        var ipfs = Common.TryBuildIpfsClient(config, out var ipfsError);
        if (ipfs is null) return Common.Fail(ipfsError!);

        try
        {
            var chain = Common.BuildChainClient(config);

            // Defence-in-depth: never collect this agent's own registered ML-KEM
            // key blob (it's standing infra published via KeyRegistry, not a
            // transfer). It never appears as a FileSent manifest/chunk CID, but
            // guarding is free.
            var protectedCids = await ResolveProtectedCidsAsync(chain, identity.Address, ct).ConfigureAwait(false);

            var targets = new List<StorageGc.Target>();

            if (cids.Count > 0)
            {
                var byCid = await StorageGc.PlanByCidsAsync(ipfs, cids, cancellationToken: ct).ConfigureAwait(false);
                foreach (var t in byCid.Where(t => !t.ManifestResolved))
                {
                    // A named CID we couldn't fetch/verify is almost always a typo
                    // or an already-released transfer — say so rather than silently
                    // "releasing" a manifest pin that isn't there.
                    Console.Error.WriteLine(
                        $"  warning: --cid {t.ManifestCid} could not be fetched/verified "
                        + "(typo, already released, or gateway issue) — only its manifest pin will be released.");
                }
                targets.AddRange(byCid);
            }

            if (olderThan is not null)
            {
                var cutoff = DateTimeOffset.UtcNow - olderThan.Value;
                ulong latest, startBlock;
                try
                {
                    latest = await chain.FileRegistry.GetLatestBlockNumberAsync(ct).ConfigureAwait(false);
                    var sinceFlag = Common.GetFlagValue(args, "--since");
                    startBlock = sinceFlag is not null ? Common.ParseBlock(sinceFlag) : 0UL;
                    if (startBlock > latest)
                        throw new InvalidOperationException($"--since ({startBlock}) is past the chain head ({latest}).");
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    return Common.Fail(ex.Message);
                }
                catch (Exception ex)
                {
                    return Common.Fail($"unable to read chain head: {ex.Message}");
                }

                Console.WriteLine($"Scanning sent transfers in blocks {startBlock}..{latest} "
                    + $"older than {Describe(olderThan.Value)} (before {cutoff:yyyy-MM-dd HH:mm:ss}Z)");
                try
                {
                    var byAge = await StorageGc
                        .PlanByAgeAsync(chain.FileRegistry, ipfs, identity.Address, cutoff, startBlock, latest, cancellationToken: ct)
                        .ConfigureAwait(false);
                    targets.AddRange(byAge);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The age scan pages history in wide windows; public RPCs cap
                    // eth_getLogs far below that, so the scan can't run there. The
                    // --cid path stays reliable everywhere.
                    Console.Error.WriteLine(
                        $"  note: the sent-events scan failed ({ex.Message}). Public RPCs cap eth_getLogs "
                        + "below the span needed to page history — use a managed RPC, or release transfers by --cid.");
                    if (cids.Count == 0) return Common.ExitError;
                }
            }

            // De-dup across modes: a manifest reachable by both --cid and the age
            // scan must be planned (and counted) once.
            targets = targets
                .GroupBy(t => t.ManifestCid, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            if (targets.Count == 0)
            {
                Console.WriteLine("Nothing to release.");
                return Common.ExitSuccess;
            }

            PrintPlan(targets, protectedCids);

            if (dryRun)
            {
                Console.WriteLine();
                Console.WriteLine("[dry-run] No content unpinned. Re-run without --dry-run to release.");
                return Common.ExitSuccess;
            }

            var result = await StorageGc.UnpinAsync(ipfs, targets, protectedCids, cancellationToken: ct).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"Released {result.CidsUnpinned} CID(s) across {result.TransfersProcessed} transfer(s)"
                + (result.CidsSkipped > 0 ? $", skipped {result.CidsSkipped} protected." : "."));
            if (result.Errors.Count > 0)
            {
                Console.Error.WriteLine($"  {result.Errors.Count} unpin(s) failed (re-run to retry):");
                foreach (var e in result.Errors.Take(10))
                    Console.Error.WriteLine($"    - {e}");
                return Common.ExitError;
            }
            return Common.ExitSuccess;
        }
        finally
        {
            (ipfs as IDisposable)?.Dispose();
        }
    }

    /// <summary>Fetch the agent's own registered ML-KEM CID into a protected
    /// set; tolerates an unregistered agent or an RPC hiccup (the guard is
    /// belt-and-braces, not load-bearing).</summary>
    private static async Task<IReadOnlySet<string>> ResolveProtectedCidsAsync(
        EthereumChainClient chain, Core.Crypto.EthereumAddress me, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var entry = await chain.KeyRegistry.GetAsync(me, ct).ConfigureAwait(false);
            if (entry.IsRegistered && !string.IsNullOrEmpty(entry.MlKemCid))
                set.Add(entry.MlKemCid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"  note: could not read your registered ML-KEM key to protect it ({ex.Message}); "
                + "proceeding — gc only targets FileSent CIDs, which never include it.");
        }
        return set;
    }

    private static void PrintPlan(IReadOnlyList<StorageGc.Target> targets, IReadOnlySet<string> protectedCids)
    {
        var totalCids = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            var chunkCount = t.ChunkCids.Count;
            totalCids += chunkCount + 1; // chunks + manifest
            Console.WriteLine($"  [{i}] manifest {t.ManifestCid}");
            if (t.SentAt != default)
                Console.WriteLine($"      sent     : {t.SentAt:yyyy-MM-dd HH:mm:ss}Z  to {t.Recipient?.ToString() ?? "?"}");
            Console.WriteLine(t.ManifestResolved
                ? $"      chunks   : {chunkCount}"
                : "      chunks   : (manifest unresolved — releasing manifest pin only)");
        }
        Console.WriteLine();
        var protectedNote = protectedCids.Count > 0 ? $" ({protectedCids.Count} protected CID excluded)" : "";
        Console.WriteLine($"  {targets.Count} transfer(s), up to {totalCids} CID(s) to release{protectedNote}.");
    }

    /// <summary>Parse a coarse duration: a POSITIVE integer followed by a single
    /// unit d(ays)/h(ours)/m(inutes)/s(econds), e.g. "30d", "12h", "90m".
    /// Rejects 0 (would select every transfer, including in-flight ones) and
    /// values so large the cutoff would overflow.</summary>
    internal static bool TryParseDuration(string raw, out TimeSpan duration, out string? error)
    {
        duration = default;
        error = null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 2)
        {
            error = $"--older-than: invalid duration '{raw}' (expected e.g. 30d, 12h, 90m, 3600s).";
            return false;
        }
        var unit = char.ToLowerInvariant(raw[^1]);
        var numberPart = raw[..^1];
        if (!long.TryParse(numberPart, out var n) || n <= 0)
        {
            error = $"--older-than: invalid number in '{raw}' (expected a positive integer before the unit).";
            return false;
        }

        TimeSpan parsed;
        try
        {
            parsed = unit switch
            {
                'd' => TimeSpan.FromDays(n),
                'h' => TimeSpan.FromHours(n),
                'm' => TimeSpan.FromMinutes(n),
                's' => TimeSpan.FromSeconds(n),
                _ => TimeSpan.MinValue,
            };
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            error = $"--older-than: duration '{raw}' is too large (max {MaxAge.TotalDays:0}d).";
            return false;
        }

        if (parsed == TimeSpan.MinValue)
        {
            error = $"--older-than: unknown unit '{raw[^1]}' (use d, h, m, or s).";
            return false;
        }
        if (parsed > MaxAge)
        {
            error = $"--older-than: duration '{raw}' is too large (max {MaxAge.TotalDays:0}d).";
            return false;
        }

        duration = parsed;
        return true;
    }

    private static string Describe(TimeSpan d)
        => d.TotalDays >= 1 ? $"{d.TotalDays:0.##}d"
         : d.TotalHours >= 1 ? $"{d.TotalHours:0.##}h"
         : d.TotalMinutes >= 1 ? $"{d.TotalMinutes:0.##}m"
         : $"{d.TotalSeconds:0.##}s";
}
