using MCPTransfer.Core.Chain.Abi;
using MCPTransfer.Core.Crypto;
using Nethereum.Web3;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Nethereum-backed implementation of <see cref="IBlocklistClient"/>.
/// </summary>
public sealed class BlocklistClient : IBlocklistClient
{
    private readonly ChainConfig _config;
    private readonly EthereumAddress _contractAddress;
    private readonly Web3 _readOnlyWeb3;

    public BlocklistClient(ChainConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _contractAddress = config.BlocklistAddress
            ?? throw new ArgumentException(
                "ChainConfig.BlocklistAddress is not set; the Blocklist contract is unconfigured.",
                nameof(config));
        _readOnlyWeb3 = Web3Factory.CreateReadOnly(config);
    }

    /// <inheritdoc />
    public async Task<string> SetBlockedAsync(
        EthereumAddress sender,
        bool blocked,
        Secp256k1KeyPair self,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(self);
        if (sender == self.Address)
            throw new ArgumentException("Cannot block your own address.", nameof(sender));

        var web3 = Web3Factory.CreateSigning(self, _config);
        var handler = web3.Eth.GetContractTransactionHandler<BlocklistAbi.SetBlockedFunction>();
        var fn = new BlocklistAbi.SetBlockedFunction { Sender = sender.LowerHex, Blocked = blocked };

        var receipt = await handler
            .SendRequestAndWaitForReceiptAsync(_contractAddress.LowerHex, fn, cancellationToken)
            .ConfigureAwait(false);
        return receipt.TransactionHash;
    }

    /// <inheritdoc />
    public async Task<bool> IsBlockedAsync(
        EthereumAddress recipient,
        EthereumAddress sender,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(sender);
        cancellationToken.ThrowIfCancellationRequested();

        var handler = _readOnlyWeb3.Eth.GetContractQueryHandler<BlocklistAbi.IsBlockedFunction>();
        var fn = new BlocklistAbi.IsBlockedFunction
        {
            Recipient = recipient.LowerHex,
            Sender = sender.LowerHex,
        };
        return await handler
            .QueryAsync<bool>(_contractAddress.LowerHex, fn)
            .ConfigureAwait(false);
    }
}
