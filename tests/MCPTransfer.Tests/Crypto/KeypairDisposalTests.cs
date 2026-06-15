using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Crypto;

/// <summary>
/// Review finding #5: Dispose() zeroes the cached private-key bytes, but the
/// lazy getter must NOT silently regenerate them afterwards. After disposal,
/// private-key access (and the operations that use it) throw
/// ObjectDisposedException; public material stays readable.
/// </summary>
public class KeypairDisposalTests
{
    [Fact]
    public void Secp256k1_AfterDispose_PrivateAccessThrows_PublicStillWorks()
    {
        var kp = Secp256k1KeyPair.Generate();
        var address = kp.Address; // cache the public side before disposal
        kp.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = kp.PrivateKey.Length; });
        Assert.Throws<ObjectDisposedException>(() => kp.SignEcdsa(new byte[32]));
        Assert.Throws<ObjectDisposedException>(() => kp.Ecdh(new byte[33]));
        // Public key / address remain usable.
        Assert.Equal(address.LowerHex, kp.Address.LowerHex);
        _ = kp.PublicKeyCompressed.Length;
    }

    [Fact]
    public void MlKem_AfterDispose_PrivateAndDecapsulateThrow()
    {
        var kp = MlKemKeyPair.Generate();
        var pub = kp.PublicKey.Bytes.ToArray(); // public side cached pre-dispose
        kp.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = kp.PrivateKeyEncoded.Length; });
        Assert.Throws<ObjectDisposedException>(() => kp.Decapsulate(new byte[MlKemPublicKey.CiphertextByteLength]));
        Assert.NotEmpty(pub);
    }

    [Fact]
    public void MlDsa_AfterDispose_PrivateAndSignThrow_PublicStillWorks()
    {
        var kp = MlDsaKeyPair.Generate();
        kp.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = kp.PrivateKeyEncoded.Length; });
        Assert.Throws<ObjectDisposedException>(() => kp.Sign(new byte[] { 1, 2, 3 }));
        _ = kp.PublicKeyEncoded.Length; // public key remains readable
    }

    [Fact]
    public void AgentIdentity_Dispose_DisposesAllThreeSubkeys()
    {
        var id = AgentIdentity.Generate();
        id.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = id.Secp256k1.PrivateKey.Length; });
        Assert.Throws<ObjectDisposedException>(() => { _ = id.MlKem.PrivateKeyEncoded.Length; });
        Assert.Throws<ObjectDisposedException>(() => { _ = id.MlDsa.PrivateKeyEncoded.Length; });
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var kp = Secp256k1KeyPair.Generate();
        _ = kp.PrivateKey.Length; // populate the cache
        kp.Dispose();
        kp.Dispose(); // must not throw
    }
}
