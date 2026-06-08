using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Crypto;

public class AgentIdentityTests
{
    [Fact]
    public void Generate_ProducesFullIdentity()
    {
        var identity = AgentIdentity.Generate();

        Assert.Equal(Secp256k1KeyPair.PublicKeyCompressedByteLength,
            identity.Secp256k1.PublicKeyCompressed.Length);
        Assert.Equal(MlKemPublicKey.PublicKeyByteLength,
            identity.MlKem.PublicKey.Bytes.Length);
        Assert.Equal(identity.Secp256k1.Address, identity.Address);
    }

    [Fact]
    public void Generate_ProducesDistinctIdentities()
    {
        var a = AgentIdentity.Generate();
        var b = AgentIdentity.Generate();

        Assert.NotEqual(a.Address, b.Address);
        Assert.False(a.MlKem.PublicKey.Bytes.SequenceEqual(b.MlKem.PublicKey.Bytes));
    }

    [Fact]
    public void FromKeys_PreservesProvidedKeys()
    {
        var ec = Secp256k1KeyPair.Generate();
        var kem = MlKemKeyPair.Generate();
        var dsa = MlDsaKeyPair.Generate();

        var identity = AgentIdentity.FromKeys(ec, kem, dsa);
        Assert.Same(ec, identity.Secp256k1);
        Assert.Same(kem, identity.MlKem);
        Assert.Same(dsa, identity.MlDsa);
        Assert.Equal(ec.Address, identity.Address);
    }

    [Fact]
    public void FromKeys_RejectsNulls()
    {
        var ec = Secp256k1KeyPair.Generate();
        var kem = MlKemKeyPair.Generate();
        var dsa = MlDsaKeyPair.Generate();
        Assert.Throws<ArgumentNullException>(() => AgentIdentity.FromKeys(null!, kem, dsa));
        Assert.Throws<ArgumentNullException>(() => AgentIdentity.FromKeys(ec, null!, dsa));
        Assert.Throws<ArgumentNullException>(() => AgentIdentity.FromKeys(ec, kem, null!));
    }

    [Fact]
    public void ToPublic_PreservesAddressAndKeys()
    {
        var identity = AgentIdentity.Generate();
        var pub = identity.ToPublic();

        Assert.Equal(identity.Address, pub.Address);
        Assert.True(identity.Secp256k1.PublicKeyCompressed.SequenceEqual(pub.Secp256k1PublicKeyCompressed.Span));
        Assert.True(identity.MlKem.PublicKey.Bytes.SequenceEqual(pub.MlKem.Bytes));
    }

    [Fact]
    public void PublicIdentity_DerivesAddressFromPublicKey()
    {
        var identity = AgentIdentity.Generate();
        var rebuilt = AgentPublicIdentity.FromBytes(
            identity.Secp256k1.PublicKeyCompressed,
            identity.MlKem.PublicKey.Bytes);

        Assert.Equal(identity.Address, rebuilt.Address);
    }

    [Fact]
    public void PublicIdentity_RejectsWrongSecp256k1Length()
    {
        var identity = AgentIdentity.Generate();
        var kem = identity.MlKem.PublicKey;
        Assert.Throws<ArgumentException>(() => new AgentPublicIdentity(new byte[32], kem));
        Assert.Throws<ArgumentException>(() => new AgentPublicIdentity(new byte[65], kem));
    }

    [Fact]
    public void PublicIdentity_StoredBytesAreIndependentOfCallerArray()
    {
        var identity = AgentIdentity.Generate();
        var providedBytes = identity.Secp256k1.PublicKeyCompressed.ToArray();
        var pub = new AgentPublicIdentity(providedBytes, identity.MlKem.PublicKey);

        // Mutating the caller-supplied array must not affect the stored copy.
        providedBytes[0] ^= 0xFF;
        Assert.NotEqual(providedBytes[0], pub.Secp256k1PublicKeyCompressed.Span[0]);
    }
}
