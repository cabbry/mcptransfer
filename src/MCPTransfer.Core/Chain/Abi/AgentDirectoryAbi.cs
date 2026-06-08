using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace MCPTransfer.Core.Chain.Abi;

/// <summary>
/// Nethereum function / event ABI mirrors for <c>AgentDirectory</c>.
/// </summary>
internal static class AgentDirectoryAbi
{
    [Function("claim")]
    public sealed class ClaimFunction : FunctionMessage
    {
        [Parameter("string", "handle", 1)]
        public string Handle { get; set; } = string.Empty;
    }

    /// <summary>
    /// Auto-generated public getter for <c>mapping(string => address) handleToAddress</c>.
    /// Returns <see cref="EthereumAddress.ByteLength"/>-byte address; zero address for unclaimed.
    /// </summary>
    [Function("handleToAddress", "address")]
    public sealed class HandleToAddressFunction : FunctionMessage
    {
        [Parameter("string", "", 1)]
        public string Handle { get; set; } = string.Empty;
    }

    /// <summary>
    /// Auto-generated public getter for <c>mapping(address => string) addressToHandle</c>.
    /// Returns empty string for addresses that never claimed.
    /// </summary>
    [Function("addressToHandle", "string")]
    public sealed class AddressToHandleFunction : FunctionMessage
    {
        [Parameter("address", "", 1)]
        public string Address { get; set; } = string.Empty;
    }

    [Event("HandleClaimed")]
    public sealed class HandleClaimedEventDto : IEventDTO
    {
        // The indexed `string` parameter gets keccak256'd into the topic on
        // emission; on the .NET side Nethereum surfaces the topic as the raw
        // 32-byte hash, NOT the original string.
        [Parameter("string", "handleHash", 1, true)]
        public byte[] HandleHash { get; set; } = Array.Empty<byte>();

        [Parameter("address", "owner", 2, true)]
        public string Owner { get; set; } = string.Empty;

        [Parameter("string", "handle", 3, false)]
        public string Handle { get; set; } = string.Empty;
    }
}
