using MCPTransfer.Core.Chain.Abi;
using MCPTransfer.Core.Crypto;
using Nethereum.Web3;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Nethereum-backed implementation of <see cref="IKeyRegistryClient"/>.
/// </summary>
public sealed class KeyRegistryClient : IKeyRegistryClient
{
    /// <summary>Mirror of <c>KeyRegistry.SECP256K1_COMPRESSED_LENGTH</c>.</summary>
    public const int Secp256k1CompressedLength = 33;

    /// <summary>Mirror of <c>KeyRegistry.ML_KEM_768_PUBKEY_LENGTH</c> (the
    /// off-chain key the on-chain hash commits to).</summary>
    public const int MlKem768PubkeyLength = 1184;

    /// <summary>Mirror of <c>KeyRegistry.MAX_CID_LENGTH</c>.</summary>
    public const int MaxCidLength = 128;

    private readonly ChainConfig _config;
    private readonly Web3 _readOnlyWeb3;

    public KeyRegistryClient(ChainConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _readOnlyWeb3 = Web3Factory.CreateReadOnly(config);
    }

    /// <inheritdoc />
    public async Task<string> PublishAsync(
        ReadOnlyMemory<byte> secp256k1Pubkey,
        ReadOnlyMemory<byte> mlkemHash,
        string mlkemCid,
        Secp256k1KeyPair self,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrEmpty(mlkemCid);
        if (secp256k1Pubkey.Length != Secp256k1CompressedLength)
        {
            throw new ArgumentException(
                $"secp256k1Pubkey must be exactly {Secp256k1CompressedLength} bytes (got {secp256k1Pubkey.Length}).",
                nameof(secp256k1Pubkey));
        }
        if (mlkemHash.Length != Hashes.Keccak256ByteLength)
        {
            throw new ArgumentException(
                $"mlkemHash must be exactly {Hashes.Keccak256ByteLength} bytes (got {mlkemHash.Length}).",
                nameof(mlkemHash));
        }
        if (!mlkemHash.Span.ContainsAnyExcept((byte)0))
        {
            throw new ArgumentException(
                "mlkemHash must not be all-zero (the contract rejects a zero commitment).",
                nameof(mlkemHash));
        }
        if (mlkemCid.Length > MaxCidLength)
        {
            throw new ArgumentException(
                $"mlkemCid must be at most {MaxCidLength} bytes (got {mlkemCid.Length}).",
                nameof(mlkemCid));
        }

        var web3 = Web3Factory.CreateSigning(self, _config);
        var handler = web3.Eth.GetContractTransactionHandler<KeyRegistryAbi.PublishFunction>();

        var fn = new KeyRegistryAbi.PublishFunction
        {
            Secp256k1Pubkey = secp256k1Pubkey.ToArray(),
            MlkemHash = mlkemHash.ToArray(),
            MlkemCid = mlkemCid,
        };

        var receipt = await handler
            .SendRequestAndWaitForReceiptAsync(_config.KeyRegistryAddress.LowerHex, fn, cancellationToken)
            .ConfigureAwait(false);
        return receipt.TransactionHash;
    }

    /// <inheritdoc />
    public async Task<AgentPublicKeys> GetAsync(
        EthereumAddress who,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(who);
        cancellationToken.ThrowIfCancellationRequested();

        var ecHandler = _readOnlyWeb3.Eth.GetContractQueryHandler<KeyRegistryAbi.GetSecp256k1Function>();
        var mlHandler = _readOnlyWeb3.Eth.GetContractQueryHandler<KeyRegistryAbi.GetMlKemFunction>();

        var ec = await ecHandler.QueryAsync<byte[]>(
            _config.KeyRegistryAddress.LowerHex,
            new KeyRegistryAbi.GetSecp256k1Function { Who = who.LowerHex }).ConfigureAwait(false);
        var ml = await mlHandler.QueryDeserializingToObjectAsync<KeyRegistryAbi.GetMlKemOutputDto>(
            new KeyRegistryAbi.GetMlKemFunction { Who = who.LowerHex },
            _config.KeyRegistryAddress.LowerHex).ConfigureAwait(false);

        return new AgentPublicKeys(
            ec ?? Array.Empty<byte>(),
            ml?.MlkemHash ?? Array.Empty<byte>(),
            ml?.MlkemCid ?? string.Empty);
    }
}
