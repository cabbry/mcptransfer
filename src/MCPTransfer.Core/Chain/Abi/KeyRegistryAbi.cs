using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace MCPTransfer.Core.Chain.Abi;

/// <summary>
/// Nethereum function / event ABI mirrors for <c>KeyRegistry</c>.
/// </summary>
internal static class KeyRegistryAbi
{
    [Function("publish")]
    public sealed class PublishFunction : FunctionMessage
    {
        [Parameter("bytes", "mlkemPubkey", 1)]
        public byte[] MlkemPubkey { get; set; } = Array.Empty<byte>();
    }

    [Function("get", "bytes")]
    public sealed class GetFunction : FunctionMessage
    {
        [Parameter("address", "who", 1)]
        public string Who { get; set; } = string.Empty;
    }

    [Event("KeyPublished")]
    public sealed class KeyPublishedEventDto : IEventDTO
    {
        [Parameter("address", "who", 1, true)]
        public string Who { get; set; } = string.Empty;

        [Parameter("bytes", "mlkemPubkey", 2, false)]
        public byte[] MlkemPubkey { get; set; } = Array.Empty<byte>();

        [Parameter("uint64", "version", 3, false)]
        public ulong Version { get; set; }
    }
}
