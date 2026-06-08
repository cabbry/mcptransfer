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
        [Parameter("bytes", "secp256k1Pubkey", 1)]
        public byte[] Secp256k1Pubkey { get; set; } = Array.Empty<byte>();

        [Parameter("bytes", "mlkemPubkey", 2)]
        public byte[] MlkemPubkey { get; set; } = Array.Empty<byte>();
    }

    [Function("getSecp256k1", "bytes")]
    public sealed class GetSecp256k1Function : FunctionMessage
    {
        [Parameter("address", "who", 1)]
        public string Who { get; set; } = string.Empty;
    }

    [Function("getMlKem", "bytes")]
    public sealed class GetMlKemFunction : FunctionMessage
    {
        [Parameter("address", "who", 1)]
        public string Who { get; set; } = string.Empty;
    }

    [Event("KeysPublished")]
    public sealed class KeysPublishedEventDto : IEventDTO
    {
        [Parameter("address", "who", 1, true)]
        public string Who { get; set; } = string.Empty;

        [Parameter("bytes", "secp256k1Pubkey", 2, false)]
        public byte[] Secp256k1Pubkey { get; set; } = Array.Empty<byte>();

        [Parameter("bytes", "mlkemPubkey", 3, false)]
        public byte[] MlkemPubkey { get; set; } = Array.Empty<byte>();

        [Parameter("uint64", "version", 4, false)]
        public ulong Version { get; set; }
    }
}
