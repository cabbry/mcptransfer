using System.Text;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Crypto;

public class HybridKemTests
{
    [Fact]
    public void EncapsulateDecapsulate_YieldsMatchingDerivedKey()
    {
        var recipient = AgentIdentity.Generate();
        var encapsulation = HybridKem.Encapsulate(recipient.ToPublic());

        var recoveredKey = HybridKem.Decapsulate(
            recipient,
            encapsulation.EphemeralSecp256k1PublicKey.Span,
            encapsulation.KemCiphertext.Span);

        Assert.Equal(HybridKem.DerivedKeyByteLength, encapsulation.DerivedKey.Length);
        Assert.Equal(encapsulation.DerivedKey.ToArray(), recoveredKey);
    }

    [Fact]
    public void Encapsulation_HasExpectedSizes()
    {
        var recipient = AgentIdentity.Generate();
        var encapsulation = HybridKem.Encapsulate(recipient.ToPublic());

        Assert.Equal(Secp256k1KeyPair.PublicKeyCompressedByteLength,
            encapsulation.EphemeralSecp256k1PublicKey.Length);
        Assert.Equal(MlKemPublicKey.CiphertextByteLength,
            encapsulation.KemCiphertext.Length);
        Assert.Equal(HybridKem.DerivedKeyByteLength,
            encapsulation.DerivedKey.Length);
    }

    [Fact]
    public void TwoEncapsulationsToSameRecipient_ProduceDifferentMaterial()
    {
        var recipient = AgentIdentity.Generate().ToPublic();

        var first = HybridKem.Encapsulate(recipient);
        var second = HybridKem.Encapsulate(recipient);

        Assert.False(first.EphemeralSecp256k1PublicKey.Span.SequenceEqual(second.EphemeralSecp256k1PublicKey.Span));
        Assert.False(first.KemCiphertext.Span.SequenceEqual(second.KemCiphertext.Span));
        Assert.False(first.DerivedKey.Span.SequenceEqual(second.DerivedKey.Span));
    }

    [Fact]
    public void WrongRecipient_ProducesDifferentKey()
    {
        var intendedRecipient = AgentIdentity.Generate();
        var attacker = AgentIdentity.Generate();
        var encapsulation = HybridKem.Encapsulate(intendedRecipient.ToPublic());

        // The attacker may have intercepted the ephemeral pubkey and the kem ciphertext,
        // but they cannot recover the same key without the intended recipient's private keys.
        // For ML-KEM, decapsulating a ciphertext that wasn't encapsulated against your key
        // yields an implicit-reject value (still 32 bytes, but unrelated).
        var attackerKey = HybridKem.Decapsulate(
            attacker,
            encapsulation.EphemeralSecp256k1PublicKey.Span,
            encapsulation.KemCiphertext.Span);

        Assert.NotEqual(encapsulation.DerivedKey.ToArray(), attackerKey);
    }

    [Fact]
    public void AdditionalContext_ChangesDerivedKey()
    {
        var recipient = AgentIdentity.Generate();
        var encapsulation = HybridKem.Encapsulate(
            recipient.ToPublic(),
            additionalContext: Encoding.UTF8.GetBytes("context-A"));

        var keyWithContextA = HybridKem.Decapsulate(
            recipient,
            encapsulation.EphemeralSecp256k1PublicKey.Span,
            encapsulation.KemCiphertext.Span,
            additionalContext: Encoding.UTF8.GetBytes("context-A"));

        var keyWithContextB = HybridKem.Decapsulate(
            recipient,
            encapsulation.EphemeralSecp256k1PublicKey.Span,
            encapsulation.KemCiphertext.Span,
            additionalContext: Encoding.UTF8.GetBytes("context-B"));

        Assert.Equal(encapsulation.DerivedKey.ToArray(), keyWithContextA);
        Assert.NotEqual(encapsulation.DerivedKey.ToArray(), keyWithContextB);
    }

    [Fact]
    public void Encapsulate_RejectsNullRecipient()
    {
        Assert.Throws<ArgumentNullException>(() =>
            HybridKem.Encapsulate(null!));
    }

    [Fact]
    public void Decapsulate_RejectsNullRecipient()
    {
        var dummyPk = new byte[Secp256k1KeyPair.PublicKeyCompressedByteLength];
        var dummyCt = new byte[MlKemPublicKey.CiphertextByteLength];
        Assert.Throws<ArgumentNullException>(() =>
            HybridKem.Decapsulate(null!, dummyPk, dummyCt));
    }
}
