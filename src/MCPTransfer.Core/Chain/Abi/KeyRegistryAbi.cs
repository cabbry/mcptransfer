using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace MCPTransfer.Core.Chain.Abi;

/// <summary>
/// Nethereum function / event ABI mirrors for <c>KeyRegistry</c> (v2:
/// secp256k1 in clear + ML-KEM hash commitment and CID pointer).
/// </summary>
internal static class KeyRegistryAbi
{
    [Function("publish")]
    public sealed class PublishFunction : FunctionMessage
    {
        [Parameter("bytes", "secp256k1Pubkey", 1)]
        public byte[] Secp256k1Pubkey { get; set; } = Array.Empty<byte>();

        [Parameter("bytes32", "mlkemHash", 2)]
        public byte[] MlkemHash { get; set; } = Array.Empty<byte>();

        [Parameter("string", "mlkemCid", 3)]
        public string MlkemCid { get; set; } = string.Empty;
    }

    [Function("getSecp256k1", "bytes")]
    public sealed class GetSecp256k1Function : FunctionMessage
    {
        [Parameter("address", "who", 1)]
        public string Who { get; set; } = string.Empty;
    }

    [Function("getMlKem", typeof(GetMlKemOutputDto))]
    public sealed class GetMlKemFunction : FunctionMessage
    {
        [Parameter("address", "who", 1)]
        public string Who { get; set; } = string.Empty;
    }

    [FunctionOutput]
    public sealed class GetMlKemOutputDto : IFunctionOutputDTO
    {
        [Parameter("bytes32", "mlkemHash", 1)]
        public byte[] MlkemHash { get; set; } = Array.Empty<byte>();

        [Parameter("string", "mlkemCid", 2)]
        public string MlkemCid { get; set; } = string.Empty;
    }

    [Event("KeysPublished")]
    public sealed class KeysPublishedEventDto : IEventDTO
    {
        [Parameter("address", "who", 1, true)]
        public string Who { get; set; } = string.Empty;

        [Parameter("bytes", "secp256k1Pubkey", 2, false)]
        public byte[] Secp256k1Pubkey { get; set; } = Array.Empty<byte>();

        [Parameter("bytes32", "mlkemHash", 3, false)]
        public byte[] MlkemHash { get; set; } = Array.Empty<byte>();

        [Parameter("string", "mlkemCid", 4, false)]
        public string MlkemCid { get; set; } = string.Empty;

        [Parameter("uint64", "version", 5, false)]
        public ulong Version { get; set; }
    }
}
