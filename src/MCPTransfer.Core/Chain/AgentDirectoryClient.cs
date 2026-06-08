using MCPTransfer.Core.Chain.Abi;
using MCPTransfer.Core.Crypto;
using Nethereum.Web3;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Nethereum-backed implementation of <see cref="IAgentDirectoryClient"/>.
/// </summary>
public sealed class AgentDirectoryClient : IAgentDirectoryClient
{
    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";

    private readonly ChainConfig _config;
    private readonly Web3 _readOnlyWeb3;

    public AgentDirectoryClient(ChainConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _readOnlyWeb3 = Web3Factory.CreateReadOnly(config);
    }

    /// <inheritdoc />
    public async Task<string> ClaimAsync(
        string handle,
        Secp256k1KeyPair self,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(handle);
        ArgumentNullException.ThrowIfNull(self);

        var web3 = Web3Factory.CreateSigning(self, _config);
        var handler = web3.Eth.GetContractTransactionHandler<AgentDirectoryAbi.ClaimFunction>();
        var fn = new AgentDirectoryAbi.ClaimFunction { Handle = handle };

        var receipt = await handler
            .SendRequestAndWaitForReceiptAsync(_config.AgentDirectoryAddress.LowerHex, fn, cancellationToken)
            .ConfigureAwait(false);
        return receipt.TransactionHash;
    }

    /// <inheritdoc />
    public async Task<EthereumAddress?> ResolveAsync(
        string handle,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(handle);
        cancellationToken.ThrowIfCancellationRequested();

        var handler = _readOnlyWeb3.Eth.GetContractQueryHandler<AgentDirectoryAbi.HandleToAddressFunction>();
        var fn = new AgentDirectoryAbi.HandleToAddressFunction { Handle = handle };

        var raw = await handler
            .QueryAsync<string>(_config.AgentDirectoryAddress.LowerHex, fn)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw) || string.Equals(raw, ZeroAddress, StringComparison.OrdinalIgnoreCase))
            return null;
        return EthereumAddress.FromHex(raw);
    }

    /// <inheritdoc />
    public async Task<string?> ReverseResolveAsync(
        EthereumAddress address,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        cancellationToken.ThrowIfCancellationRequested();

        var handler = _readOnlyWeb3.Eth.GetContractQueryHandler<AgentDirectoryAbi.AddressToHandleFunction>();
        var fn = new AgentDirectoryAbi.AddressToHandleFunction { Address = address.LowerHex };

        var raw = await handler
            .QueryAsync<string>(_config.AgentDirectoryAddress.LowerHex, fn)
            .ConfigureAwait(false);
        return string.IsNullOrEmpty(raw) ? null : raw;
    }
}
