using System.Security.Cryptography;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

namespace MCPTransfer.Tests;

/// <summary>
/// Phase 0 spike: validates that BouncyCastle 2.6.2 gives us a working
/// ML-KEM-768 KEM and secp256k1 ECDH, and that we can combine both shared
/// secrets via HKDF-SHA256 into a single AES-256 key (the hybrid PQC pattern).
/// </summary>
public class CryptoSpikeTests
{
    [Fact]
    public void MlKem768_RoundTrip_YieldsMatchingSharedSecret()
    {
        var rng = new SecureRandom();

        var kpg = new MLKemKeyPairGenerator();
        kpg.Init(new MLKemKeyGenerationParameters(rng, MLKemParameters.ml_kem_768));
        var kp = kpg.GenerateKeyPair();

        var pub = (MLKemPublicKeyParameters)kp.Public;
        var priv = (MLKemPrivateKeyParameters)kp.Private;

        var pubBytes = pub.GetEncoded();
        Assert.Equal(1184, pubBytes.Length);

        var recipientPub = MLKemPublicKeyParameters.FromEncoding(
            MLKemParameters.ml_kem_768, pubBytes);

        var enc = new MLKemEncapsulator(MLKemParameters.ml_kem_768);
        enc.Init(recipientPub);

        var ct = new byte[enc.EncapsulationLength];
        var ssSender = new byte[enc.SecretLength];
        enc.Encapsulate(ct, 0, ct.Length, ssSender, 0, ssSender.Length);

        Assert.Equal(1088, ct.Length);
        Assert.Equal(32, ssSender.Length);

        var dec = new MLKemDecapsulator(MLKemParameters.ml_kem_768);
        dec.Init(priv);

        var ssRecipient = new byte[dec.SecretLength];
        dec.Decapsulate(ct, 0, ct.Length, ssRecipient, 0, ssRecipient.Length);

        Assert.Equal(ssSender, ssRecipient);
    }

    [Fact]
    public void Secp256k1_Ecdh_BothPartiesDeriveSameSecret()
    {
        var (alicePriv, alicePub) = GenerateSecp256k1();
        var (bobPriv, bobPub) = GenerateSecp256k1();

        var aliceShared = Ecdh(alicePriv, bobPub);
        var bobShared = Ecdh(bobPriv, alicePub);

        Assert.Equal(32, aliceShared.Length);
        Assert.Equal(aliceShared, bobShared);
    }

    [Fact]
    public void HybridKem_CombinesEcdhAndMlKemIntoAes256Key()
    {
        var rng = new SecureRandom();

        // Recipient long-term keys
        var (recipientEcSk, recipientEcPk) = GenerateSecp256k1();
        var mlKpg = new MLKemKeyPairGenerator();
        mlKpg.Init(new MLKemKeyGenerationParameters(rng, MLKemParameters.ml_kem_768));
        var recipientMlKp = mlKpg.GenerateKeyPair();
        var recipientMlPk = (MLKemPublicKeyParameters)recipientMlKp.Public;
        var recipientMlSk = (MLKemPrivateKeyParameters)recipientMlKp.Private;

        // Sender side
        var (ephSk, ephPk) = GenerateSecp256k1();
        var ss1Sender = Ecdh(ephSk, recipientEcPk);

        var enc = new MLKemEncapsulator(MLKemParameters.ml_kem_768);
        enc.Init(recipientMlPk);
        var kemCt = new byte[enc.EncapsulationLength];
        var ss2Sender = new byte[enc.SecretLength];
        enc.Encapsulate(kemCt, 0, kemCt.Length, ss2Sender, 0, ss2Sender.Length);

        var senderKey = HkdfCombine(ss1Sender, ss2Sender, info: "MCPTx-v1-hybrid");

        // Recipient side
        var ss1Recipient = Ecdh(recipientEcSk, ephPk);

        var dec = new MLKemDecapsulator(MLKemParameters.ml_kem_768);
        dec.Init(recipientMlSk);
        var ss2Recipient = new byte[dec.SecretLength];
        dec.Decapsulate(kemCt, 0, kemCt.Length, ss2Recipient, 0, ss2Recipient.Length);

        var recipientKey = HkdfCombine(ss1Recipient, ss2Recipient, info: "MCPTx-v1-hybrid");

        Assert.Equal(32, senderKey.Length);
        Assert.Equal(senderKey, recipientKey);
    }

    // --- helpers ---

    private static (BigInteger sk, byte[] pkCompressed) GenerateSecp256k1()
    {
        var x9 = SecNamedCurves.GetByName("secp256k1");
        var domain = new ECDomainParameters(x9.Curve, x9.G, x9.N, x9.H);
        var rng = new SecureRandom();

        var gen = new ECKeyPairGenerator("ECDH");
        gen.Init(new ECKeyGenerationParameters(domain, rng));
        var kp = gen.GenerateKeyPair();

        var sk = ((ECPrivateKeyParameters)kp.Private).D;
        var pk = ((ECPublicKeyParameters)kp.Public).Q.GetEncoded(compressed: true);
        return (sk, pk);
    }

    private static byte[] Ecdh(BigInteger mySk, byte[] peerPkBytes)
    {
        var x9 = SecNamedCurves.GetByName("secp256k1");
        var domain = new ECDomainParameters(x9.Curve, x9.G, x9.N, x9.H);
        var myPriv = new ECPrivateKeyParameters(mySk, domain);
        var peerPub = new ECPublicKeyParameters(domain.Curve.DecodePoint(peerPkBytes), domain);

        var agree = new ECDHBasicAgreement();
        agree.Init(myPriv);
        var z = agree.CalculateAgreement(peerPub);
        return BigIntegers.AsUnsignedByteArray(32, z);
    }

    private static byte[] HkdfCombine(byte[] ss1, byte[] ss2, string info)
    {
        var ikm = new byte[ss1.Length + ss2.Length];
        Buffer.BlockCopy(ss1, 0, ikm, 0, ss1.Length);
        Buffer.BlockCopy(ss2, 0, ikm, ss1.Length, ss2.Length);

        return HKDF.DeriveKey(
            hashAlgorithmName: HashAlgorithmName.SHA256,
            ikm: ikm,
            outputLength: 32,
            salt: null,
            info: System.Text.Encoding.UTF8.GetBytes(info));
    }
}
