# MCPTransfer

**Trustless, permissionless, post-quantum-hybrid file transfer between AI agents.**

A POC for AI-to-AI file exchange with no central server, no accounts, no gatekeepers.

- **Data**: chunked, encrypted client-side (AES-256-GCM), stored on **IPFS**
- **Metadata**: published as events on an **EVM chain** (Polygon Amoy testnet)
- **Identity**: secp256k1 keypair = Ethereum address (no signup required)
- **Crypto**: **hybrid KEM** = secp256k1 ECDHE + **ML-KEM-768** (FIPS 203), combined via HKDF-SHA256 → AES-256-GCM data key. Resistant to "harvest now, decrypt later" attacks. **Hybrid signatures** = ECDSA secp256k1 + **ML-DSA-65** (FIPS 204) co-sign every manifest.

## Status

POC under construction.

| Phase | Status |
|-------|--------|
| 0 — Bootstrap + crypto spike | done |
| 1 — Envelope + chunking + IPFS (in-memory + Pinata + file) + CLI | done |
| 2 — Smart contracts (FileRegistry, KeyRegistry, AgentDirectory) + C# chain client | done |
| 3 — CLI send/inbox/receive on-chain + config layer | done (anvil-tested, Amoy-pending) |
| 4 — MCP server (`mcptx mcp-serve`, 8 tools over stdio) | done |

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
  contracts/                        # Solidity / Foundry — Phase 2 (see contracts/README.md)
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

Full command reference: [docs/CLI.md](docs/CLI.md).

End-to-end flow (Alice → Bob, anvil local profile):

```powershell
# In both shells (sender + recipient):
dotnet run --project src/MCPTransfer.Agent -- keygen
dotnet run --project src/MCPTransfer.Agent -- config init --profile anvil-local --pinata-jwt $env:PINATA_JWT

# Publish ML-KEM + secp256k1 pubkeys on-chain (required before either can receive)
dotnet run --project src/MCPTransfer.Agent -- register-key

# Bob claims a handle so Alice can address him by name
dotnet run --project src/MCPTransfer.Agent -- claim bob

# Alice sends a file
dotnet run --project src/MCPTransfer.Agent -- send report.pdf --to bob --mime application/pdf

# Bob lists his inbox and decrypts
dotnet run --project src/MCPTransfer.Agent -- inbox
dotnet run --project src/MCPTransfer.Agent -- receive bafy... --out received.pdf
```

**IPFS backend** (precedence: `--ipfs-dir` > `--pinata-jwt` > memory):

- `--ipfs-dir DIR` — a shared-folder **file store**. Two `mcptx` processes
  pointing at the same directory exchange files with **no network provider**.
  This is the recommended way to run a real local `send`→`receive` round-trip
  (verified end-to-end against anvil, single- and multi-chunk).
- `--pinata-jwt JWT` (or `PINATA_JWT` env) — real IPFS network pinning.
- memory — in-process only, test-only (pins vanish on exit).

Identity file format (plaintext JSON, see [src/MCPTransfer.Core/Storage/AgentIdentityFile.cs](src/MCPTransfer.Core/Storage/AgentIdentityFile.cs)):

```json
{
  "mlkem_private_key": "base64 (2400 bytes encoded)",
  "secp256k1_private_key": "0x... (32 bytes hex)",
  "version": 1
}
```

> POC limitation: private keys are stored unencrypted. Production deployments should wrap the file in a passphrase-derived encryption or hand off to an OS keyring / TPM-backed key store.

## Pinata IPFS client

For real network IPFS pinning (vs the in-memory client used by tests):

1. Sign up at [https://app.pinata.cloud](https://app.pinata.cloud) (free tier: 1 GB)
2. Generate a JWT in **API Keys → New Key**
3. Compose with the retry decorator (Phase 1.9):

```csharp
var jwt = Environment.GetEnvironmentVariable("PINATA_JWT")
    ?? throw new InvalidOperationException("PINATA_JWT not set");

using var pinata = new PinataIpfsClient(jwt);
var ipfs = new RetryingIpfsClient(pinata, RetryPolicy.Default);

var writer = new EnvelopeWriter(ipfs);
var reader = new EnvelopeReader(ipfs);
```

The `RetryingIpfsClient` wrapping absorbs transient 429 / 5xx / network errors with exponential backoff + jitter. Auth failures (401/403) and not-found (404) are surfaced immediately as permanent.

To run the live integration test against your Pinata account:

```powershell
$env:PINATA_JWT = "eyJ..."
dotnet test --filter IntegrationTest_RealPinata
```

Without the env var, the integration test is skipped silently.

## MCP server

`mcptx mcp-serve` runs a [Model Context Protocol](https://modelcontextprotocol.io)
server over stdio, exposing MCPTransfer as 8 tools (`whoami`, `resolve`,
`whois`, `inbox`, `register_key`, `claim`, `send_file`, `receive_file`) so an
AI host can drive the whole protocol — *"send this report to alice-ai"* →
on-chain handle resolution + hybrid-PQC encryption + IPFS pin + chain event.

```json
{
  "mcpServers": {
    "mcptransfer": {
      "command": "dotnet",
      "args": ["…/mcptx.dll", "mcp-serve"]
    }
  }
}
```

> The server holds the agent's key and signs transactions on the host's
> request — a deliberate authority grant. Full reference + trust model:
> [docs/MCP.md](docs/MCP.md).

## On-chain layer

Three immutable contracts on Polygon Amoy (or Anvil locally):

- **FileRegistry** — emits `FileSent(from, to, cid, contentHash, ts)`
- **KeyRegistry** — `mapping(address => bytes)` for **both** secp256k1 compressed and ML-KEM-768 public keys
- **AgentDirectory** — first-come-first-served `handle ↔ address` registry (`alice-ai` style)

C# wrappers in `MCPTransfer.Core.Chain` (Nethereum-backed):

```csharp
var chain = new EthereumChainClient(new ChainConfig
{
    RpcUrl                = "https://rpc-amoy.polygon.technology",
    ChainId               = ChainConfig.AmoyChainId,
    FileRegistryAddress   = EthereumAddress.FromHex("0x..."),
    KeyRegistryAddress    = EthereumAddress.FromHex("0x..."),
    AgentDirectoryAddress = EthereumAddress.FromHex("0x..."),
});

// Resolve a handle and look up the recipient's PQC pubkey
var bob = await chain.AgentDirectory.ResolveAsync("bob");
var bobMlKem = await chain.KeyRegistry.GetAsync(bob!);

// Announce a transfer
var tx = await chain.FileRegistry.SendAsync(
    bob!, manifestCid, contentHash, alice.Secp256k1);
```

> Détails complets : [docs/CHAIN.md](docs/CHAIN.md). Contracts source : [contracts/](contracts/).

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
- Manifests are co-signed ECDSA secp256k1 + **ML-DSA-65** (post-quantum).
  However, because identity = an ECDSA Ethereum address, the *binding* of
  any PQC key to the identity remains classically secured — ML-DSA makes
  manifest-content authenticity post-quantum given a trusted binding, not
  the key→identity link. A PQC-native identity (off-EVM) is out of scope.
- Identity = Ethereum address (secp256k1). The ML-KEM public key is published
  in the on-chain `KeyRegistry` (self-published via `msg.sender`); the ML-DSA
  public key travels inside each signed manifest, bound by the ECDSA signature.

## License

TBD.
