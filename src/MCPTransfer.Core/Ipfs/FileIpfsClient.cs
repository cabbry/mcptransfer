using System.Security.Cryptography;

namespace MCPTransfer.Core.Ipfs;

/// <summary>
/// Disk-backed content-addressed store: each pinned blob is written to a
/// file named after its SHA-256 hex digest inside a shared directory. Two
/// separate processes that point at the same directory share the store,
/// which makes a real cross-process <c>send</c> → <c>receive</c> round-trip
/// possible WITHOUT a network IPFS provider (Pinata).
/// </summary>
/// <remarks>
/// <para>
/// CIDs are 64-char lowercase hex (the SHA-256 of the content). They are
/// deliberately NOT real IPFS CIDs — this client is a local stand-in for
/// development and integration tests, not a network backend.
/// </para>
/// <para>
/// Pins are atomic (write to a unique <c>.tmp</c>, then rename). Fetch
/// validates the CID against the 64-hex shape before touching the
/// filesystem, closing path-traversal (a CID like <c>../../etc/passwd</c>
/// is rejected, never used to build a path).
/// </para>
/// </remarks>
public sealed class FileIpfsClient : IIpfsClient
{
    private readonly string _directory;

    public FileIpfsClient(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public async Task<string> PinAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cid = ComputeCid(data.Span);
        var finalPath = Path.Combine(_directory, cid);

        // Content-addressed + idempotent: identical content -> identical CID,
        // so an existing file is already the correct bytes.
        if (File.Exists(finalPath))
            return cid;

        var tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, data.ToArray(), cancellationToken).ConfigureAwait(false);
            // Content-addressed: an existing blob already holds identical bytes,
            // so NEVER overwrite. A plain (non-overwriting) move means a
            // concurrent pin of the same content simply loses the race and
            // throws IOException, which we treat as success. This avoids the
            // delete-then-move churn of overwrite:true that races on Windows.
            File.Move(tempPath, finalPath);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            // A concurrent (or prior) pin of identical content already created
            // the final blob — that's the idempotent success case.
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            // If our temp survived (e.g. the move lost the race), clean it up.
            if (File.Exists(tempPath))
                TryDelete(tempPath);
        }
        return cid;
    }

    public async Task<byte[]> FetchAsync(string cid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(cid);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsValidCid(cid))
        {
            // Reject anything that isn't our 64-hex shape BEFORE building a
            // path — this is the path-traversal guard.
            throw new ArgumentException(
                $"Invalid file-store CID '{cid}' (expected 64 lowercase hex chars).",
                nameof(cid));
        }

        var path = Path.Combine(_directory, cid);
        if (!File.Exists(path))
            throw new KeyNotFoundException($"CID '{cid}' not found in file store '{_directory}'.");

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public Task UnpinAsync(string cid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(cid);
        cancellationToken.ThrowIfCancellationRequested();
        // Same path-traversal guard as FetchAsync: reject anything that isn't
        // our 64-hex shape BEFORE building a path.
        if (!IsValidCid(cid))
            throw new ArgumentException(
                $"Invalid file-store CID '{cid}' (expected 64 lowercase hex chars).", nameof(cid));

        // Idempotent (absent file is a no-op) but NOT silent: a real failure to
        // delete an existing blob (locked / permission denied) propagates so the
        // gc layer records it rather than counting a failed unpin as success.
        var path = Path.Combine(_directory, cid);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>SHA-256 hex (64 lowercase chars) of the content.</summary>
    public static string ComputeCid(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static bool IsValidCid(string cid)
    {
        if (cid.Length != 64)
            return false;
        foreach (var c in cid)
        {
            var ok = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!ok) return false;
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
