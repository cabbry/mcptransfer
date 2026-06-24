using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCPTransfer.Core.Crypto;

/// <summary>
/// A shareable "contact card": the public material a sender needs to encrypt
/// to a recipient — the recipient's address, secp256k1 public key (for ECDH)
/// and ML-KEM-768 public key (for the KEM). It carries exactly what
/// <see cref="AgentPublicIdentity"/> needs, so a recipient can be sent to
/// WITHOUT having published on-chain (no gas, no IPFS pin): they export a card,
/// share it out-of-band, and the sender uses <c>send --to-pubkey</c>.
/// </summary>
/// <remarks>
/// Trust model: resolving via the on-chain <c>KeyRegistry</c> ties the ML-KEM
/// key to a keccak256 commitment; a card has no such commitment, so its
/// authenticity rests on the out-of-band channel it arrived through
/// (trust-on-first-use). The address↔secp256k1 binding is always re-checked by
/// the sender (<see cref="ToPublicIdentity"/> derives the address from the
/// key), so a malformed or mismatched card is rejected.
/// </remarks>
public sealed record ContactCard(string Address, byte[] Secp256k1Compressed, byte[] MlKemPublicKey)
{
    public const int CurrentVersion = 1;

    /// <summary>Serialize to indented JSON (secp256k1 as 0x-hex, ML-KEM as base64).</summary>
    public string ToJson()
    {
        var root = new JsonObject
        {
            ["version"] = CurrentVersion,
            ["address"] = Address,
            ["secp256k1"] = "0x" + Convert.ToHexString(Secp256k1Compressed).ToLowerInvariant(),
            ["mlkem"] = Convert.ToBase64String(MlKemPublicKey),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Parse a card from JSON. Throws <see cref="FormatException"/> on missing
    /// fields or undecodable key material (crypto-length validation happens in
    /// <see cref="ToPublicIdentity"/>).
    /// </summary>
    public static ContactCard FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"contact card is not valid JSON: {ex.Message}", ex);
        }
        if (root is null)
            throw new FormatException("contact card is empty.");

        var address = (root["address"]?.GetValue<string>())
            ?? throw new FormatException("contact card is missing 'address'.");
        var secpHex = (root["secp256k1"]?.GetValue<string>())
            ?? throw new FormatException("contact card is missing 'secp256k1'.");
        var mlkemB64 = (root["mlkem"]?.GetValue<string>())
            ?? throw new FormatException("contact card is missing 'mlkem'.");

        byte[] secp, mlkem;
        try
        {
            secp = Convert.FromHexString(secpHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? secpHex[2..] : secpHex);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"contact card 'secp256k1' is not valid hex: {ex.Message}", ex);
        }
        try
        {
            mlkem = Convert.FromBase64String(mlkemB64);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"contact card 'mlkem' is not valid base64: {ex.Message}", ex);
        }

        return new ContactCard(address, secp, mlkem);
    }

    /// <summary>
    /// Build the verified public identity. Throws <see cref="ArgumentException"/>
    /// if the key lengths are wrong; the caller must additionally check that the
    /// derived <see cref="AgentPublicIdentity.Address"/> matches
    /// <see cref="Address"/> (the address↔key binding).
    /// </summary>
    public AgentPublicIdentity ToPublicIdentity()
        => AgentPublicIdentity.FromBytes(Secp256k1Compressed, MlKemPublicKey);
}
