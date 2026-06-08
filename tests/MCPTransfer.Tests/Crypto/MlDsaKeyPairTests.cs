using System.Security.Cryptography;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Crypto;

public class MlDsaKeyPairTests
{
    [Fact]
    public void Generate_ProducesExpectedPublicKeySize()
    {
        var kp = MlDsaKeyPair.Generate();
        Assert.Equal(MlDsaKeyPair.PublicKeyByteLength, kp.PublicKeyEncoded.Length);
    }

    [Fact]
    public void Generate_ProducesDistinctKeys()
    {
        var a = MlDsaKeyPair.Generate();
        var b = MlDsaKeyPair.Generate();
        Assert.False(a.PublicKeyEncoded.SequenceEqual(b.PublicKeyEncoded));
    }

    [Fact]
    public void SignVerify_RoundTrips()
    {
        var kp = MlDsaKeyPair.Generate();
        var msg = RandomNumberGenerator.GetBytes(256);

        var sig = kp.Sign(msg);

        Assert.Equal(MlDsaKeyPair.SignatureByteLength, sig.Length);
        Assert.True(MlDsaKeyPair.Verify(kp.PublicKeyEncoded, msg, sig));
    }

    [Fact]
    public void Verify_FailsOnTamperedMessage()
    {
        var kp = MlDsaKeyPair.Generate();
        var msg = RandomNumberGenerator.GetBytes(128);
        var sig = kp.Sign(msg);

        msg[0] ^= 0x01;
        Assert.False(MlDsaKeyPair.Verify(kp.PublicKeyEncoded, msg, sig));
    }

    [Fact]
    public void Verify_FailsOnTamperedSignature()
    {
        var kp = MlDsaKeyPair.Generate();
        var msg = RandomNumberGenerator.GetBytes(128);
        var sig = kp.Sign(msg);

        sig[0] ^= 0x01;
        Assert.False(MlDsaKeyPair.Verify(kp.PublicKeyEncoded, msg, sig));
    }

    [Fact]
    public void Verify_FailsWithWrongPublicKey()
    {
        var signer = MlDsaKeyPair.Generate();
        var other = MlDsaKeyPair.Generate();
        var msg = RandomNumberGenerator.GetBytes(64);
        var sig = signer.Sign(msg);

        Assert.False(MlDsaKeyPair.Verify(other.PublicKeyEncoded, msg, sig));
    }

    [Fact]
    public void Verify_ReturnsFalseOnWrongLengths()
    {
        var kp = MlDsaKeyPair.Generate();
        var msg = RandomNumberGenerator.GetBytes(64);
        var sig = kp.Sign(msg);

        Assert.False(MlDsaKeyPair.Verify(new byte[10], msg, sig));        // bad pubkey len
        Assert.False(MlDsaKeyPair.Verify(kp.PublicKeyEncoded, msg, new byte[10])); // bad sig len
    }

    [Fact]
    public void PrivateKey_RoundTripsThroughEncoding()
    {
        var original = MlDsaKeyPair.Generate();
        var encoded = original.PrivateKeyEncoded.ToArray();

        var restored = MlDsaKeyPair.FromEncodedPrivateKey(encoded);

        // Public key recovered from the restored private key matches.
        Assert.True(original.PublicKeyEncoded.SequenceEqual(restored.PublicKeyEncoded));

        // A signature from the restored key verifies under the original pubkey.
        var msg = RandomNumberGenerator.GetBytes(100);
        var sig = restored.Sign(msg);
        Assert.True(MlDsaKeyPair.Verify(original.PublicKeyEncoded, msg, sig));
    }

    [Fact]
    public void PublicKey_RoundTripsThroughVerify()
    {
        var kp = MlDsaKeyPair.Generate();
        var rawPub = kp.PublicKeyEncoded.ToArray();
        var msg = RandomNumberGenerator.GetBytes(50);
        var sig = kp.Sign(msg);

        // Verify using the externally-captured raw public-key bytes.
        Assert.True(MlDsaKeyPair.Verify(rawPub, msg, sig));
    }
}
