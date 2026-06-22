using MCPTransfer.Core.Chain;

namespace MCPTransfer.Agent.Commands;

/// <summary>
/// <c>mcptx gc</c> — release (unpin) the IPFS content of transfers this agent
/// has sent, so the data plane stays an ephemeral mailbox rather than an
/// archive. Two modes: by age (<c>--older-than</c>, scans the chain for this
/// agent's <c>FileSent</c> events) or by explicit manifest CID (<c>--cid</c>,
/// no chain scan — reliable on any RPC). The agent's own registered ML-KEM key
/// blob is always protected.
/// </summary>
internal static class GcCommand
{
    public const string Usage =
        "  mcptx gc [--older-than DUR] [--cid CID]... [--dry-run] [--since BLOCK] [--identity PATH] [--config PATH]\n"
      + "      Unpin the IPFS content of transfers YOU sent, so files don't live\n"
      + "      forever. DUR is like 30d / 12h / 90m / 3600s. --cid targets a known\n"
      + "      manifest CID directly (repeatable; works on any RPC). --dry-run shows\n"
      + "      what would be released without unpinning. Run --dry-run first.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var olderThanRaw = Common.GetFlagValue(args, "--older-than");
        var cids = CollectFlagValues(args, "--cid");
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
                var byCid = await StorageGc.PlanByCidsAsync(ipfs, cids, ct).ConfigureAwait(false);
                targets.AddRange(byCid);
            }

            if (olderThan is not null)
            {
                var cutoff = DateTimeOffset.UtcNow - olderThan.Value;
                ulong latest;
                ulong fromBlock, toBlock;
                try
                {
                    latest = await chain.FileRegistry.GetLatestBlockNumberAsync(ct).ConfigureAwait(false);
                    var sinceFlag = Common.GetFlagValue(args, "--since");
                    ulong? since = sinceFlag is not null ? ParseBlock(sinceFlag) : null;
                    (fromBlock, toBlock) = InboxWindow.Compute(latest, since);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    return Common.Fail(ex.Message);
                }
                catch (Exception ex)
                {
                    return Common.Fail($"unable to read chain head: {ex.Message}");
                }

                Console.WriteLine($"Scanning sent transfers in blocks {fromBlock}..{toBlock} "
                    + $"older than {Describe(olderThan.Value)} (before {cutoff:yyyy-MM-dd HH:mm:ss}Z)");
                try
                {
                    var byAge = await StorageGc
                        .PlanByAgeAsync(chain.FileRegistry, ipfs, identity.Address, cutoff, fromBlock, toBlock, ct)
                        .ConfigureAwait(false);
                    targets.AddRange(byAge);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Public RPCs cap eth_getLogs far below the window old
                    // transfers live in; the age scan can't reach them. The
                    // --cid path stays reliable everywhere.
                    Console.Error.WriteLine(
                        $"  note: the RPC rejected the sent-events scan ({ex.Message}). "
                        + "Old transfers need a managed RPC, or release them by --cid.");
                    if (cids.Count == 0) return Common.ExitError;
                }
            }

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

            var result = await StorageGc.UnpinAsync(ipfs, targets, protectedCids, ct).ConfigureAwait(false);

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

    private static List<string> CollectFlagValues(string[] args, string flag)
    {
        var values = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (!string.Equals(args[i], flag, StringComparison.Ordinal))
                continue;
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value after {flag}.");
            values.Add(args[++i]);
        }
        return values;
    }

    /// <summary>Parse a coarse duration: an integer followed by a single unit
    /// d(ays)/h(ours)/m(inutes)/s(econds), e.g. "30d", "12h", "90m".</summary>
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
        if (!long.TryParse(numberPart, out var n) || n < 0)
        {
            error = $"--older-than: invalid number in '{raw}' (expected a non-negative integer before the unit).";
            return false;
        }
        duration = unit switch
        {
            'd' => TimeSpan.FromDays(n),
            'h' => TimeSpan.FromHours(n),
            'm' => TimeSpan.FromMinutes(n),
            's' => TimeSpan.FromSeconds(n),
            _ => TimeSpan.MinValue,
        };
        if (duration == TimeSpan.MinValue)
        {
            error = $"--older-than: unknown unit '{raw[^1]}' (use d, h, m, or s).";
            return false;
        }
        return true;
    }

    private static string Describe(TimeSpan d)
        => d.TotalDays >= 1 ? $"{d.TotalDays:0.##}d"
         : d.TotalHours >= 1 ? $"{d.TotalHours:0.##}h"
         : d.TotalMinutes >= 1 ? $"{d.TotalMinutes:0.##}m"
         : $"{d.TotalSeconds:0.##}s";

    private static ulong ParseBlock(string raw)
    {
        if (!ulong.TryParse(raw, out var v))
            throw new ArgumentException($"--since: invalid block number '{raw}' (expected a non-negative integer).");
        return v;
    }
}
