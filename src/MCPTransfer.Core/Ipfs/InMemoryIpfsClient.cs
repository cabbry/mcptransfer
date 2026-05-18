using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MCPTransfer.Core.Ipfs;

/// <summary>
/// In-process content-addressed store used by tests and local round-trips.
/// CIDs are SHA-256 hex strings prefixed with <c>mem:</c> to make them
/// easy to recognise as not being real IPFS CIDs.
/// </summary>
public sealed class InMemoryIpfsClient : IIpfsClient
{
    private const string CidPrefix = "mem:";

    private readonly ConcurrentDictionary<string, byte[]> _store = new(StringComparer.Ordinal);

    public int Count => _store.Count;

    public bool Contains(string cid) => _store.ContainsKey(cid);

    public Task<string> PinAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = data.ToArray();
        var cid = ComputeCid(bytes);
        _store[cid] = bytes;
        return Task.FromResult(cid);
    }

    public Task<byte[]> FetchAsync(
        string cid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(cid);
        if (_store.TryGetValue(cid, out var bytes))
        {
            // Defensive copy so callers cannot mutate the stored content.
            return Task.FromResult((byte[])bytes.Clone());
        }
        throw new KeyNotFoundException($"CID '{cid}' not found in {nameof(InMemoryIpfsClient)}.");
    }

    public static string ComputeCid(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return CidPrefix + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
