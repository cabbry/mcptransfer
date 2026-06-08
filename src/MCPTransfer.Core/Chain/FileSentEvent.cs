using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Public-facing projection of a <c>FileSent</c> event scraped from chain logs.
/// Wraps the raw Nethereum DTO with our domain types (EthereumAddress, bytes).
/// </summary>
public sealed record FileSentEvent(
    EthereumAddress From,
    EthereumAddress To,
    string Cid,
    byte[] ContentHash,
    DateTimeOffset Timestamp,
    string TransactionHash,
    ulong BlockNumber,
    uint LogIndex);
