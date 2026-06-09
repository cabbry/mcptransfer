using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace MCPTransfer.Core.Chain.Abi;

/// <summary>
/// Nethereum function / event ABI mirrors for <c>Blocklist</c>.
/// </summary>
internal static class BlocklistAbi
{
    [Function("setBlocked")]
    public sealed class SetBlockedFunction : FunctionMessage
    {
        [Parameter("address", "sender", 1)]
        public string Sender { get; set; } = string.Empty;

        [Parameter("bool", "blocked", 2)]
        public bool Blocked { get; set; }
    }

    /// <summary>
    /// Auto-generated public getter for
    /// <c>mapping(address => mapping(address => bool)) isBlocked</c>.
    /// </summary>
    [Function("isBlocked", "bool")]
    public sealed class IsBlockedFunction : FunctionMessage
    {
        [Parameter("address", "", 1)]
        public string Recipient { get; set; } = string.Empty;

        [Parameter("address", "", 2)]
        public string Sender { get; set; } = string.Empty;
    }

    [Event("BlockSet")]
    public sealed class BlockSetEventDto : IEventDTO
    {
        [Parameter("address", "recipient", 1, true)]
        public string Recipient { get; set; } = string.Empty;

        [Parameter("address", "sender", 2, true)]
        public string Sender { get; set; } = string.Empty;

        [Parameter("bool", "blocked", 3, false)]
        public bool Blocked { get; set; }
    }
}
