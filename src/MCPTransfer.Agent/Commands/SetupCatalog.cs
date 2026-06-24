using MCPTransfer.Core.Configuration;

namespace MCPTransfer.Agent.Commands;

/// <summary>
/// Single source of truth for the chains and decentralized-storage backends
/// the onboarding flow (<c>mcptx setup</c>) and diagnostics (<c>mcptx doctor</c>)
/// know about. Adding a new chain or storage backend = one entry here — the
/// menus, prompts, and config composition pick it up automatically. This is
/// what keeps onboarding extensible as more chains / storage layers land.
/// </summary>
internal static class SetupCatalog
{
    /// <summary>A selectable chain profile (RPC + contract addresses).</summary>
    /// <param name="Name">Stable id used on the CLI (e.g. <c>amoy</c>).</param>
    /// <param name="Summary">One-line human description for the menu.</param>
    /// <param name="BuildChain">Produces the chain section (addresses + RPC).</param>
    /// <param name="NeedsGas">True if state-changing ops cost real (test)gas.</param>
    /// <param name="FaucetHint">Where to get gas, when <paramref name="NeedsGas"/>.</param>
    internal sealed record ChainOption(
        string Name, string Summary, Func<ChainConfigSection> BuildChain, bool NeedsGas, string? FaucetHint);

    internal static readonly IReadOnlyList<ChainOption> Chains = new[]
    {
        new ChainOption(
            "anvil-local",
            "Local Foundry anvil — no accounts, no gas, no faucet (best for a first try)",
            () => DefaultProfiles.AnvilLocal().Chain,
            NeedsGas: false,
            FaucetHint: null),
        new ChainOption(
            "amoy",
            "Polygon Amoy testnet — needs test POL from a faucet",
            () => DefaultProfiles.Amoy().Chain,
            NeedsGas: true,
            FaucetHint: "https://faucet.polygon.technology/ (Amoy POL)"),
    };

    /// <summary>A selectable decentralized-storage backend.</summary>
    /// <param name="Kind">The <see cref="IpfsConfigSection.Kind"/> value.</param>
    /// <param name="Summary">One-line human description for the menu.</param>
    /// <param name="NeedsCredential">True if a token/JWT is required.</param>
    /// <param name="NeedsDirectory">True if a local directory is required.</param>
    internal sealed record StorageOption(
        string Kind, string Summary, bool NeedsCredential, bool NeedsDirectory);

    internal static readonly IReadOnlyList<StorageOption> Storages = new[]
    {
        new StorageOption(
            IpfsConfigSection.KindFile,
            "Shared folder — no accounts, works cross-process locally (great for demos)",
            NeedsCredential: false, NeedsDirectory: true),
        new StorageOption(
            IpfsConfigSection.KindPinata,
            "Pinata (IPFS pinning service) — needs a free JWT",
            NeedsCredential: true, NeedsDirectory: false),
        new StorageOption(
            IpfsConfigSection.KindMemory,
            "In-process only — demo/tests, content lost on exit",
            NeedsCredential: false, NeedsDirectory: false),
    };

    internal static ChainOption? FindChain(string name)
        => Chains.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    internal static ChainOption? FindChainByChainId(long chainId)
        => Chains.FirstOrDefault(c => c.BuildChain().ChainId == chainId);

    internal static StorageOption? FindStorage(string kind)
        => Storages.FirstOrDefault(s => string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>Build the IPFS/storage section for a chosen backend.</summary>
    internal static IpfsConfigSection BuildStorage(string kind, string? jwt, string? directory) => kind switch
    {
        IpfsConfigSection.KindFile => new IpfsConfigSection { Kind = IpfsConfigSection.KindFile, Directory = directory },
        IpfsConfigSection.KindPinata => new IpfsConfigSection { Kind = IpfsConfigSection.KindPinata, PinataJwt = jwt },
        IpfsConfigSection.KindMemory => new IpfsConfigSection { Kind = IpfsConfigSection.KindMemory },
        _ => throw new ArgumentException($"unknown storage backend '{kind}'.", nameof(kind)),
    };

    /// <summary>Compose a full config from a chain profile + a storage section.</summary>
    internal static MCPTransferConfig Compose(ChainOption chain, IpfsConfigSection storage)
        => new() { Chain = chain.BuildChain(), Ipfs = storage };
}
