using System.Security.Cryptography;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Crypto;

public class Secp256k1SigningTests
{
    private static byte[] RandomHash() => RandomNumberGenerator.GetBytes(Hashes.Keccak256ByteLength);

    [Fact]
    public void SignAndVerify_RoundTrip()
    {
        var signer = Secp256k1KeyPair.Generate();
        var hash = RandomHash();

        var signature = signer.SignEcdsa(hash);

        Assert.Equal(Secp256k1KeyPair.SignatureByteLength, signature.Length);
        Assert.True(Secp256k1KeyPair.VerifyEcdsa(signer.PublicKeyCompressed, hash, signature));
        Assert.True(Secp256k1KeyPair.VerifyEcdsa(signer.PublicKeyUncompressed, hash, signature));
    }

    [Fact]
    public void Verify_FailsWithWrongPublicKey()
    {
        var alice = Secp256k1KeyPair.Generate();
        var bob = Secp256k1KeyPair.Generate();
        var hash = RandomHash();

        var aliceSig = alice.SignEcdsa(hash);
        Assert.False(Secp256k1KeyPair.VerifyEcdsa(bob.PublicKeyCompressed, hash, aliceSig));
    }

    [Fact]
    public void Verify_FailsOnTamperedHash()
    {
        var signer = Secp256k1KeyPair.Generate();
        var hash = RandomHash();
        var signature = signer.SignEcdsa(hash);

        hash[0] ^= 0x01;
        Assert.False(Secp256k1KeyPair.VerifyEcdsa(signer.PublicKeyCompressed, hash, signature));
    }

    [Fact]
    public void Verify_FailsOnTamperedSignature()
    {
        var signer = Secp256k1KeyPair.Generate();
        var hash = RandomHash();
        var signature = signer.SignEcdsa(hash);

        signature[0] ^= 0x01;
        Assert.False(Secp256k1KeyPair.VerifyEcdsa(signer.PublicKeyCompressed, hash, signature));
    }

    [Fact]
    public void Sign_IsDeterministic_PerRfc6979()
    {
        var signer = Secp256k1KeyPair.Generate();
        var hash = RandomHash();
        var first = signer.SignEcdsa(hash);
        var second = signer.SignEcdsa(hash);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Sign_DifferentHashes_ProduceDifferentSignatures()
    {
        var signer = Secp256k1KeyPair.Generate();
        var first = signer.SignEcdsa(RandomHash());
        var second = signer.SignEcdsa(RandomHash());
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Sign_ProducesLowSSignature()
    {
        var signer = Secp256k1KeyPair.Generate();
        for (var i = 0; i < 32; i++)
        {
            var sig = signer.SignEcdsa(RandomHash());
            // The high bit of s is set only when s >= n/2; n/2 has its top bit unset for secp256k1,
            // so low-s normalization keeps signature[32] < 0x80 in practice.
            Assert.True(sig[32] < 0x80, $"Signature {i}: s top byte should be < 0x80 (low-s), got 0x{sig[32]:X2}.");
        }
    }

    [Fact]
    public void Sign_RejectsWrongHashLength()
    {
        var signer = Secp256k1KeyPair.Generate();
        Assert.Throws<ArgumentException>(() => signer.SignEcdsa(new byte[31]));
        Assert.Throws<ArgumentException>(() => signer.SignEcdsa(new byte[33]));
    }

    [Fact]
    public void Verify_ReturnsFalseOnStructuralProblems()
    {
        var signer = Secp256k1KeyPair.Generate();
        var validHash = RandomHash();
        var validSig = signer.SignEcdsa(validHash);

        Assert.False(Secp256k1KeyPair.VerifyEcdsa(signer.PublicKeyCompressed, new byte[31], validSig));
        Assert.False(Secp256k1KeyPair.VerifyEcdsa(signer.PublicKeyCompressed, validHash, new byte[Secp256k1KeyPair.SignatureByteLength - 1]));
        Assert.False(Secp256k1KeyPair.VerifyEcdsa(new byte[32], validHash, validSig));
    }

    [Fact]
    public void Verify_RejectsHighSSignature()
    {
        var signer = Secp256k1KeyPair.Generate();
        var hash = RandomHash();
        var sig = signer.SignEcdsa(hash);

        // Flip s into its high counterpart: s' = n - s, which is mathematically
        // equivalent but lies above n/2. The recovery byte v at sig[64] also
        // flips with this transform, but we don't consult it during verify so
        // any byte is acceptable here.
        var nBytes = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141");
        var sBytes = sig.AsSpan(32, 32).ToArray();

        var nMinusS = new byte[32];
        var borrow = 0;
        for (var i = 31; i >= 0; i--)
        {
            var diff = nBytes[i] - sBytes[i] - borrow;
            if (diff < 0) { diff += 256; borrow = 1; } else borrow = 0;
            nMinusS[i] = (byte)diff;
        }

        var malleable = new byte[Secp256k1KeyPair.SignatureByteLength];
        sig.AsSpan(0, 32).CopyTo(malleable.AsSpan(0, 32));
        nMinusS.CopyTo(malleable, 32);
        malleable[64] = sig[64]; // keep a valid-shape v byte; not consulted by verify

        Assert.False(Secp256k1KeyPair.VerifyEcdsa(signer.PublicKeyCompressed, hash, malleable));
    }

    [Fact]
    public void Recover_RoundTrip_YieldsSignersPublicKey()
    {
        var signer = Secp256k1KeyPair.Generate();
        var hash = RandomHash();
        var sig = signer.SignEcdsa(hash);

        var recoveredPk = Secp256k1KeyPair.Recover(hash, sig);

        Assert.Equal(Secp256k1KeyPair.PublicKeyCompressedByteLength, recoveredPk.Length);
        Assert.True(signer.PublicKeyCompressed.SequenceEqual(recoveredPk));
    }

    [Fact]
    public void Recover_RejectsBadSignatureLength()
    {
        Assert.Throws<ArgumentException>(
            () => Secp256k1KeyPair.Recover(new byte[32], new byte[Secp256k1KeyPair.SignatureByteLength - 1]));
    }
}
