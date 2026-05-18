using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Crypto;

public class MlKemKeyPairTests
{
    [Fact]
    public void Generate_ProducesExpectedSizes()
    {
        var kp = MlKemKeyPair.Generate();

        Assert.Equal(MlKemPublicKey.PublicKeyByteLength, kp.PublicKey.Bytes.Length);
        Assert.Equal(MlKemKeyPair.PrivateKeyEncodedByteLength, kp.PrivateKeyEncoded.Length);
    }

    [Fact]
    public void Generate_ProducesDistinctKeys()
    {
        var a = MlKemKeyPair.Generate();
        var b = MlKemKeyPair.Generate();
        Assert.False(a.PublicKey.Bytes.SequenceEqual(b.PublicKey.Bytes));
    }

    [Fact]
    public void EncapsDecaps_YieldsMatchingSharedSecret()
    {
        var kp = MlKemKeyPair.Generate();
        var encapsulation = kp.PublicKey.Encapsulate();

        Assert.Equal(MlKemPublicKey.CiphertextByteLength, encapsulation.Ciphertext.Length);
        Assert.Equal(MlKemPublicKey.SharedSecretByteLength, encapsulation.SharedSecret.Length);

        var recovered = kp.Decapsulate(encapsulation.Ciphertext);
        Assert.Equal(encapsulation.SharedSecret, recovered);
    }

    [Fact]
    public void PublicKey_RoundTripsThroughRawBytes()
    {
        var kp = MlKemKeyPair.Generate();
        var raw = kp.PublicKey.Bytes.ToArray();

        var rebuilt = new MlKemPublicKey(raw);
        Assert.True(kp.PublicKey.Bytes.SequenceEqual(rebuilt.Bytes));

        // And it still works for the encaps/decaps cycle.
        var encapsulation = rebuilt.Encapsulate();
        var ss = kp.Decapsulate(encapsulation.Ciphertext);
        Assert.Equal(encapsulation.SharedSecret, ss);
    }

    [Fact]
    public void PrivateKey_RoundTripsThroughEncoding()
    {
        var original = MlKemKeyPair.Generate();
        var encoded = original.PrivateKeyEncoded.ToArray();

        var restored = MlKemKeyPair.FromEncodedPrivateKey(encoded);
        Assert.True(original.PrivateKeyEncoded.SequenceEqual(restored.PrivateKeyEncoded));
        Assert.True(original.PublicKey.Bytes.SequenceEqual(restored.PublicKey.Bytes));

        // Cross-decapsulation: ct from original.PublicKey decapsulates with restored.
        var encapsulation = original.PublicKey.Encapsulate();
        var ss = restored.Decapsulate(encapsulation.Ciphertext);
        Assert.Equal(encapsulation.SharedSecret, ss);
    }

    [Fact]
    public void PublicKeyConstructor_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => new MlKemPublicKey(new byte[1183]));
        Assert.Throws<ArgumentException>(() => new MlKemPublicKey(new byte[1185]));
    }

    [Fact]
    public void FromEncodedPrivateKey_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => MlKemKeyPair.FromEncodedPrivateKey(new byte[2399]));
        Assert.Throws<ArgumentException>(() => MlKemKeyPair.FromEncodedPrivateKey(new byte[2401]));
    }

    [Fact]
    public void Decapsulate_RejectsWrongCiphertextLength()
    {
        var kp = MlKemKeyPair.Generate();
        Assert.Throws<ArgumentException>(() => kp.Decapsulate(new byte[1087]));
        Assert.Throws<ArgumentException>(() => kp.Decapsulate(new byte[1089]));
    }
}
