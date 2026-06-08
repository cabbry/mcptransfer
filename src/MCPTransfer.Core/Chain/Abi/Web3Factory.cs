using MCPTransfer.Core.Crypto;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace MCPTransfer.Core.Chain.Abi;

/// <summary>
/// Shared helpers for building Nethereum <see cref="Web3"/> instances from
/// our domain types. Keeps the chain client implementations focused on the
/// contract semantics rather than the wiring.
/// </summary>
internal static class Web3Factory
{
    /// <summary>
    /// Build a <see cref="Web3"/> bound to <paramref name="signer"/> for
    /// state-changing calls (Send / Publish / Claim).
    /// </summary>
    public static Web3 CreateSigning(Secp256k1KeyPair signer, ChainConfig config)
    {
        var privateKeyHex = Convert.ToHexString(signer.PrivateKey).ToLowerInvariant();
        var account = new Account(privateKeyHex, config.ChainId);
        return new Web3(account, config.RpcUrl);
    }

    /// <summary>
    /// Build a <see cref="Web3"/> with no signer, for read-only calls
    /// (eth_call / eth_getLogs).
    /// </summary>
    public static Web3 CreateReadOnly(ChainConfig config) => new(config.RpcUrl);
}
