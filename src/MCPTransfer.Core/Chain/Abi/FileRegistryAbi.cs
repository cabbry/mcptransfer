using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace MCPTransfer.Core.Chain.Abi;

/// <summary>
/// Nethereum function / event ABI mirrors for <c>FileRegistry</c>.
/// Internal — the public surface is <see cref="IFileRegistryClient"/>.
/// </summary>
internal static class FileRegistryAbi
{
    [Function("send")]
    public sealed class SendFunction : FunctionMessage
    {
        [Parameter("address", "to", 1)]
        public string To { get; set; } = string.Empty;

        [Parameter("string", "cid", 2)]
        public string Cid { get; set; } = string.Empty;

        [Parameter("bytes32", "contentHash", 3)]
        public byte[] ContentHash { get; set; } = Array.Empty<byte>();
    }

    [Event("FileSent")]
    public sealed class FileSentEventDto : IEventDTO
    {
        [Parameter("address", "from", 1, true)]
        public string From { get; set; } = string.Empty;

        [Parameter("address", "to", 2, true)]
        public string To { get; set; } = string.Empty;

        [Parameter("string", "cid", 3, false)]
        public string Cid { get; set; } = string.Empty;

        [Parameter("bytes32", "contentHash", 4, false)]
        public byte[] ContentHash { get; set; } = Array.Empty<byte>();

        [Parameter("uint64", "timestamp", 5, false)]
        public ulong Timestamp { get; set; }
    }
}
