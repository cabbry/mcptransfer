using MCPTransfer.Core.Chain.Abi;
using MCPTransfer.Core.Crypto;
using Nethereum.Web3;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Nethereum-backed implementation of <see cref="IKeyRegistryClient"/>.
/// </summary>
public sealed class KeyRegistryClient : IKeyRegistryClient
{
    /// <summary>Mirror of <c>KeyRegistry.ML_KEM_768_PUBKEY_LENGTH</c>.</summary>
    public const int MlKem768PubkeyLength = 1184;

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
        ReadOnlyMemory<byte> mlkemPubkey,
        Secp256k1KeyPair self,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(self);
        if (mlkemPubkey.Length != MlKem768PubkeyLength)
        {
            throw new ArgumentException(
                $"mlkemPubkey must be exactly {MlKem768PubkeyLength} bytes (got {mlkemPubkey.Length}).",
                nameof(mlkemPubkey));
        }

        var web3 = Web3Factory.CreateSigning(self, _config);
        var handler = web3.Eth.GetContractTransactionHandler<KeyRegistryAbi.PublishFunction>();

        var fn = new KeyRegistryAbi.PublishFunction
        {
            MlkemPubkey = mlkemPubkey.ToArray(),
        };

        var receipt = await handler
            .SendRequestAndWaitForReceiptAsync(_config.KeyRegistryAddress.LowerHex, fn, cancellationToken)
            .ConfigureAwait(false);
        return receipt.TransactionHash;
    }

    /// <inheritdoc />
    public async Task<byte[]> GetAsync(
        EthereumAddress who,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(who);
        cancellationToken.ThrowIfCancellationRequested();

        var handler = _readOnlyWeb3.Eth.GetContractQueryHandler<KeyRegistryAbi.GetFunction>();
        var fn = new KeyRegistryAbi.GetFunction { Who = who.LowerHex };

        var result = await handler
            .QueryAsync<byte[]>(_config.KeyRegistryAddress.LowerHex, fn)
            .ConfigureAwait(false);
        return result ?? Array.Empty<byte>();
    }
}
