using System.Runtime.CompilerServices;
using MCPTransfer.Core.Chain.Abi;
using MCPTransfer.Core.Crypto;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Nethereum-backed implementation of <see cref="IFileRegistryClient"/>.
/// </summary>
public sealed class FileRegistryClient : IFileRegistryClient
{
    private readonly ChainConfig _config;
    private readonly Web3 _readOnlyWeb3;

    public FileRegistryClient(ChainConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _readOnlyWeb3 = Web3Factory.CreateReadOnly(config);
    }

    /// <inheritdoc />
    public async Task<string> SendAsync(
        EthereumAddress recipient,
        string cid,
        byte[] contentHash,
        Secp256k1KeyPair sender,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentException.ThrowIfNullOrEmpty(cid);
        ArgumentNullException.ThrowIfNull(contentHash);
        ArgumentNullException.ThrowIfNull(sender);
        if (contentHash.Length != Hashes.Keccak256ByteLength)
        {
            throw new ArgumentException(
                $"contentHash must be exactly {Hashes.Keccak256ByteLength} bytes (got {contentHash.Length}).",
                nameof(contentHash));
        }

        var web3 = Web3Factory.CreateSigning(sender, _config);
        var handler = web3.Eth.GetContractTransactionHandler<FileRegistryAbi.SendFunction>();

        var fn = new FileRegistryAbi.SendFunction
        {
            To = recipient.LowerHex,
            Cid = cid,
            ContentHash = contentHash,
        };

        var receipt = await handler
            .SendRequestAndWaitForReceiptAsync(_config.FileRegistryAddress.LowerHex, fn, cancellationToken)
            .ConfigureAwait(false);
        return receipt.TransactionHash;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<FileSentEvent> WatchInboxAsync(
        EthereumAddress me,
        ulong fromBlock,
        TimeSpan pollInterval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(me);
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "Poll interval must be positive.");

        var cursor = fromBlock;
        while (!cancellationToken.IsCancellationRequested)
        {
            var latest = await GetLatestBlockNumberAsync(cancellationToken).ConfigureAwait(false);
            if (cursor <= latest)
            {
                var batch = await GetInboxAsync(me, cursor, latest, cancellationToken).ConfigureAwait(false);
                foreach (var ev in batch)
                    yield return ev;
                cursor = latest + 1;
            }

            try
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileSentEvent>> GetInboxAsync(
        EthereumAddress me,
        ulong fromBlock,
        ulong toBlock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(me);
        if (toBlock < fromBlock)
            throw new ArgumentOutOfRangeException(nameof(toBlock), "toBlock must be >= fromBlock.");

        var eventHandler = _readOnlyWeb3.Eth.GetEvent<FileRegistryAbi.FileSentEventDto>(
            _config.FileRegistryAddress.LowerHex);

        // Filter on topic-2 (indexed `to` = me); leave topic-1 (`from`) unfiltered.
        // Nethereum's CreateFilterInput expects topic values wrapped in object[]
        // (so a single value uses `new object[] { value }`).
        var filter = eventHandler.CreateFilterInput(
            filterTopic1: (object[]?)null,
            filterTopic2: new object[] { me.LowerHex },
            fromBlock: new BlockParameter(new HexBigInteger(fromBlock)),
            toBlock: new BlockParameter(new HexBigInteger(toBlock)));

        var logs = await eventHandler.GetAllChangesAsync(filter).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return logs.Select(Map).ToList();
    }

    /// <inheritdoc />
    public async Task<ulong> GetLatestBlockNumberAsync(CancellationToken cancellationToken = default)
    {
        var block = await _readOnlyWeb3.Eth.Blocks.GetBlockNumber.SendRequestAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return (ulong)block.Value;
    }

    /// <inheritdoc />
    public async Task<FileSentEvent?> FindByCidAsync(
        EthereumAddress me,
        string cid,
        ulong fromBlock,
        ulong toBlock,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(cid);
        var events = await GetInboxAsync(me, fromBlock, toBlock, cancellationToken).ConfigureAwait(false);
        // Most recent announcement of this CID to this recipient.
        return events
            .Where(e => string.Equals(e.Cid, cid, StringComparison.Ordinal))
            .OrderByDescending(e => e.BlockNumber)
            .ThenByDescending(e => e.LogIndex)
            .FirstOrDefault();
    }

    private static FileSentEvent Map(EventLog<FileRegistryAbi.FileSentEventDto> log)
    {
        return new FileSentEvent(
            From: EthereumAddress.FromHex(log.Event.From),
            To: EthereumAddress.FromHex(log.Event.To),
            Cid: log.Event.Cid,
            ContentHash: log.Event.ContentHash,
            Timestamp: DateTimeOffset.FromUnixTimeSeconds((long)log.Event.Timestamp),
            TransactionHash: log.Log.TransactionHash,
            BlockNumber: (ulong)log.Log.BlockNumber.Value,
            LogIndex: (uint)log.Log.LogIndex.Value);
    }
}
