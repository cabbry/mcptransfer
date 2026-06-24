using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Crypto;

public class ContactCardTests
{
    private static ContactCard CardFor(AgentIdentity id) => new(
        id.Address.ChecksumHex,
        id.Secp256k1.PublicKeyCompressed.ToArray(),
        id.MlKem.PublicKey.Bytes.ToArray());

    [Fact]
    public void RoundTrip_PreservesFields_AndDerivesTheSameAddress()
    {
        var id = AgentIdentity.Generate();
        var card = CardFor(id);

        var parsed = ContactCard.FromJson(card.ToJson());

        Assert.Equal(card.Address, parsed.Address);
        Assert.Equal(card.Secp256k1Compressed, parsed.Secp256k1Compressed);
        Assert.Equal(card.MlKemPublicKey, parsed.MlKemPublicKey);

        // The whole point: a sender can rebuild the recipient's public identity.
        Assert.Equal(id.Address, parsed.ToPublicIdentity().Address);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]                                                     // missing fields
    [InlineData("""{"address":"0xabc","mlkem":"AAAA"}""")]                 // missing secp256k1
    [InlineData("""{"address":"0xabc","secp256k1":"zzzz","mlkem":"AAAA"}""")] // bad hex
    [InlineData("""{"address":"0xabc","secp256k1":"0x02","mlkem":"!!!!"}""")] // bad base64
    public void FromJson_RejectsMalformedCards(string json)
        => Assert.Throws<FormatException>(() => ContactCard.FromJson(json));

    [Fact]
    public void ToPublicIdentity_WrongKeyLength_Throws()
    {
        var id = AgentIdentity.Generate();
        var bad = new ContactCard(id.Address.ChecksumHex, new byte[10], id.MlKem.PublicKey.Bytes.ToArray());
        Assert.Throws<ArgumentException>(() => bad.ToPublicIdentity());
    }

    [Fact]
    public void SpoofedAddress_DerivesToADifferentAddress_SoTheBindingCheckCatchesIt()
    {
        var real = AgentIdentity.Generate();
        var attacker = AgentIdentity.Generate();

        // A card that claims the attacker's address but carries the real agent's keys.
        var spoof = new ContactCard(
            attacker.Address.ChecksumHex,
            real.Secp256k1.PublicKeyCompressed.ToArray(),
            real.MlKem.PublicKey.Bytes.ToArray());

        Assert.NotEqual(EthereumAddress.FromHex(spoof.Address), spoof.ToPublicIdentity().Address);
    }
}
