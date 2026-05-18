using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Crypto;

public class Secp256k1KeyPairTests
{
    // Default Anvil/Hardhat account #0 — public, widely-documented test key.
    private const string AnvilAccount0PrivKey =
        "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
    private const string AnvilAccount0Address =
        "0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266";

    [Fact]
    public void Generate_ProducesValidSizes()
    {
        var kp = Secp256k1KeyPair.Generate();

        Assert.Equal(Secp256k1KeyPair.PrivateKeyByteLength, kp.PrivateKey.Length);
        Assert.Equal(Secp256k1KeyPair.PublicKeyCompressedByteLength, kp.PublicKeyCompressed.Length);
        Assert.Equal(Secp256k1KeyPair.PublicKeyUncompressedByteLength, kp.PublicKeyUncompressed.Length);

        // Compressed encoding starts with 0x02 or 0x03.
        Assert.True(kp.PublicKeyCompressed[0] is 0x02 or 0x03);
        // Uncompressed encoding starts with 0x04.
        Assert.Equal(0x04, kp.PublicKeyUncompressed[0]);
    }

    [Fact]
    public void Generate_ProducesDistinctKeys()
    {
        var a = Secp256k1KeyPair.Generate();
        var b = Secp256k1KeyPair.Generate();
        Assert.False(a.PrivateKey.SequenceEqual(b.PrivateKey));
        Assert.NotEqual(a.Address, b.Address);
    }

    [Fact]
    public void FromPrivateKeyHex_DerivesKnownEthereumAddress()
    {
        var kp = Secp256k1KeyPair.FromPrivateKeyHex(AnvilAccount0PrivKey);
        Assert.Equal(AnvilAccount0Address, kp.Address.ChecksumHex);
    }

    [Fact]
    public void FromPrivateKey_RoundTripsThroughBytes()
    {
        var original = Secp256k1KeyPair.Generate();
        var roundTripped = Secp256k1KeyPair.FromPrivateKey(original.PrivateKey);

        Assert.True(original.PrivateKey.SequenceEqual(roundTripped.PrivateKey));
        Assert.True(original.PublicKeyCompressed.SequenceEqual(roundTripped.PublicKeyCompressed));
        Assert.Equal(original.Address, roundTripped.Address);
    }

    [Fact]
    public void FromPrivateKey_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => Secp256k1KeyPair.FromPrivateKey(new byte[31]));
        Assert.Throws<ArgumentException>(() => Secp256k1KeyPair.FromPrivateKey(new byte[33]));
    }

    [Fact]
    public void FromPrivateKey_RejectsZeroScalar()
    {
        Assert.Throws<ArgumentException>(() => Secp256k1KeyPair.FromPrivateKey(new byte[32]));
    }

    [Fact]
    public void Ecdh_BothDirectionsProduceMatchingSharedSecret()
    {
        var alice = Secp256k1KeyPair.Generate();
        var bob = Secp256k1KeyPair.Generate();

        var fromAlice = alice.Ecdh(bob.PublicKeyCompressed);
        var fromBob = bob.Ecdh(alice.PublicKeyCompressed);

        Assert.Equal(Secp256k1KeyPair.SharedSecretByteLength, fromAlice.Length);
        Assert.Equal(fromAlice, fromBob);
    }

    [Fact]
    public void Ecdh_AcceptsBothCompressedAndUncompressedPeerKey()
    {
        var alice = Secp256k1KeyPair.Generate();
        var bob = Secp256k1KeyPair.Generate();

        var viaCompressed = alice.Ecdh(bob.PublicKeyCompressed);
        var viaUncompressed = alice.Ecdh(bob.PublicKeyUncompressed);
        Assert.Equal(viaCompressed, viaUncompressed);
    }

    [Fact]
    public void Ecdh_RejectsMalformedPeerKey()
    {
        var alice = Secp256k1KeyPair.Generate();
        Assert.Throws<ArgumentException>(() => alice.Ecdh(new byte[32])); // wrong length
    }

    [Fact]
    public void AddressFromPublicKey_MatchesKeyPairAddress()
    {
        var kp = Secp256k1KeyPair.Generate();
        var fromCompressed = Secp256k1KeyPair.AddressFromPublicKey(kp.PublicKeyCompressed);
        var fromUncompressed = Secp256k1KeyPair.AddressFromPublicKey(kp.PublicKeyUncompressed);

        Assert.Equal(kp.Address, fromCompressed);
        Assert.Equal(kp.Address, fromUncompressed);
    }
}
