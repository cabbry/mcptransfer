# MCPTransfer

**Trustless, permissionless, post-quantum-hybrid file transfer between AI agents.**

A POC for AI-to-AI file exchange with no central server, no accounts, no gatekeepers.

- **Data**: chunked, encrypted client-side (AES-256-GCM), stored on **IPFS**
- **Metadata**: published as events on an **EVM chain** (Polygon Amoy testnet)
- **Identity**: secp256k1 keypair = Ethereum address (no signup required)
- **Crypto**: **hybrid KEM** = secp256k1 ECDHE + **ML-KEM-768** (FIPS 203), combined via HKDF-SHA256 → AES-256-GCM data key. Resistant to "harvest now, decrypt later" attacks.

## Status

POC under construction.

| Phase | Status |
|-------|--------|
| 0 — Bootstrap + crypto spike | done |
| 1 — Envelope + chunking + IPFS (in-memory) + CLI | done |
| 2 — Smart contracts (FileRegistry, KeyRegistry) | pending |
| 3 — Pinata IPFS client + Amoy chain client | pending |

## Repository layout

```
MCPTransfer/
  MCPTransfer.slnx
  Directory.Build.props
  Directory.Packages.props          # central package versions
  src/
    MCPTransfer.Core/               # crypto, envelope, IPFS & chain clients
    MCPTransfer.Agent/              # CLI (mcptx)
  tests/
    MCPTransfer.Tests/              # xunit
  contracts/                        # Solidity / Foundry (to come in Phase 2)
```

## Requirements

- .NET 10 SDK (`dotnet --version` >= 10.0)
- (Phase 2+) Foundry, a Pinata API key, an Amoy RPC endpoint

## Build & test

```powershell
dotnet restore MCPTransfer.slnx
dotnet build   MCPTransfer.slnx
dotnet test    MCPTransfer.slnx
```

## CLI quickstart (`mcptx`)

```powershell
# Generate a fresh hybrid identity at ~/.mcptx/identity.json
dotnet run --project src/MCPTransfer.Agent -- keygen

# Show the local agent's address and public keys
dotnet run --project src/MCPTransfer.Agent -- whoami
```

Identity file format (plaintext JSON, see [src/MCPTransfer.Core/Storage/AgentIdentityFile.cs](src/MCPTransfer.Core/Storage/AgentIdentityFile.cs)):

```json
{
  "mlkem_private_key": "base64 (2400 bytes encoded)",
  "secp256k1_private_key": "0x... (32 bytes hex)",
  "version": 1
}
```

> POC limitation: private keys are stored unencrypted. Production deployments should wrap the file in a passphrase-derived encryption or hand off to an OS keyring / TPM-backed key store.

## Cryptographic construction (planned)

> Détails complets : [docs/CRYPTO.md](docs/CRYPTO.md)


```
                Sender                                  Recipient
                   |                                        |
   eph_sk, eph_pk = secp256k1.gen()                         |
   ss1 = ECDH(eph_sk, recipient.secp256k1_pk)               |
   (kem_ct, ss2) = ML-KEM-768.Encaps(recipient.mlkem_pk)    |
   K = HKDF-SHA256(ss1 || ss2 || "MCPTx-v1-hybrid")         |
   ciphertext = AES-256-GCM(K, chunk)        per-chunk      |
                                                            |
                                            ss1 = ECDH(my.sk, eph_pk)
                                            ss2 = ML-KEM.Decaps(my.mlkem_sk, kem_ct)
                                            K  = HKDF(...)
                                            decrypt chunks
```

Each chunk uses a unique nonce `nonce_prefix(8 random) || chunk_idx(4 BE)`.
A signed JSON **manifest** lists every chunk's CID and tag; only the
manifest's CID goes on-chain.

## Known limits (POC scope)

- Metadata graph (who sends to whom, when) is public on-chain — confidentiality
  of *content* only, not of social graph. Mixing/relayers are out of scope.
- Pinata free tier: 1 GB total storage. Hard cap on POC volume.
- AES-GCM cap: ~64 GiB per (key, nonce-prefix) pair. Files larger than that
  are rejected.
- PQC signatures (ML-DSA-65) deferred to v2 — v1 signs the manifest with
  classical ECDSA secp256k1.
- Identity = Ethereum address (secp256k1). The ML-KEM public key is bound
  via an on-chain `KeyRegistry` event signed by the same address, not by
  cryptographic derivation.

## License

TBD.
