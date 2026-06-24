# MCPTransfer

**Trustless, permissionless, post-quantum-hybrid file transfer between AI agents.**

A POC for AI-to-AI file exchange with no central server, no accounts, no gatekeepers.

> 📄 **White paper** (chain- and storage-agnostic protocol description, for
> reviewers and foundations): [docs/WHITEPAPER.md](docs/WHITEPAPER.md) ·
> [PDF (EN)](docs/MCPTransfer-WhitePaper-v1.0.pdf) ·
> [version française](docs/WHITEPAPER.fr.md) ·
> [PDF (FR)](docs/MCPTransfer-WhitePaper-v1.0.fr.pdf) — regenerate with
> `python docs/tools/make_whitepaper_pdf.py [en|fr]`.

- **Data**: chunked, encrypted client-side (AES-256-GCM), stored on **IPFS**
- **Metadata**: published as events on an **EVM chain** (Polygon Amoy testnet)
- **Identity**: secp256k1 keypair = Ethereum address (no signup required)
- **Crypto**: **hybrid KEM** = secp256k1 ECDHE + **ML-KEM-768** (FIPS 203), combined via HKDF-SHA256 → AES-256-GCM data key. Resistant to "harvest now, decrypt later" attacks. **Hybrid signatures** = ECDSA secp256k1 + **ML-DSA-65** (FIPS 204) co-sign every manifest.

## Status

POC **feature-complete and verified live**: the four contracts are deployed on
Polygon Amoy (addresses pre-filled in the `amoy` config profile — see
[docs/CHAIN.md](docs/CHAIN.md)) and the full encrypted round-trip has been
validated over real Pinata IPFS — an 18 MB multi-chunk transfer received
byte-identical, with the on-chain content-hash corroboration confirmed.

| Phase | Status |
|-------|--------|
| 0 — Bootstrap + crypto spike | done |
| 1 — Envelope + chunking + IPFS (in-memory + Pinata + file) + CLI | done |
| 2 — Smart contracts (FileRegistry, KeyRegistry, AgentDirectory) + C# chain client | done |
| 3 — CLI send/inbox/receive on-chain + config layer | done (anvil-verified, Amoy-pending) |
| 4 — MCP server (`mcptx mcp-serve`, 8 tools over stdio) | done |
| 5 — Hybrid signatures (ECDSA secp256k1 + ML-DSA-65) | done |
| Hardening — input/parse robustness, MCP path confinement + signing lock | done |
| Tooling — `mcptx version`, on-chain receive corroboration, CI, gated anvil integration tests | done |
| Contracts v2 — transferable handles, Blocklist (anti-spam), KeyRegistry hash commitment | done |
| Hardening 2 — encrypted identity at rest (Argon2id + AES-GCM), best-effort key zeroization | done |
| **Live** — contracts deployed on Polygon Amoy, full round-trip over real Pinata IPFS | **done** |

**Verified:** 289 .NET tests pass (the 4 gated integration tests no-op unless
enabled) + 33 Solidity tests under `forge`; the full envelope round-trip is
byte-identical against a live anvil (single- and multi-chunk, dual-signed,
on-chain content-hash corroborated).

## Repository layout

```
MCPTransfer/
  MCPTransfer.slnx
  Directory.Build.props
  Directory.Packages.props          # central package versions
  bitbucket-pipelines.yml           # CI (mirrored in .github/workflows/ci.yml)
  src/
    MCPTransfer.Core/               # crypto, envelope, IPFS & chain clients, config
    MCPTransfer.Agent/              # CLI (mcptx) + MCP server (Mcp/)
  tests/
    MCPTransfer.Tests/              # xUnit (+ gated live tests under Integration/)
  contracts/                        # Solidity / Foundry — see contracts/README.md
  docs/                             # CRYPTO.md, CHAIN.md, CLI.md, MCP.md (French)
```

## Requirements

- **.NET 10 SDK** (`dotnet --version` >= 10.0) — required for everything below.
- **Foundry** (anvil/forge/cast) — only for the local on-chain walkthrough and
  the gated integration tests. Install: <https://getfoundry.sh>.
- A **Pinata JWT** and an **Amoy RPC endpoint** — only for the production path.

---

## Quickstart

### 1. Zero-setup: build, test, see your identity (no chain, no secrets)

```powershell
dotnet restore MCPTransfer.slnx
dotnet build   MCPTransfer.slnx
dotnet test    MCPTransfer.slnx      # 402 tests pass (4 anvil tests no-op without the gate)

# one guided command: identity + config + Claude Desktop snippet, zero accounts
dotnet run --project src/MCPTransfer.Agent -- setup --local
dotnet run --project src/MCPTransfer.Agent -- doctor        # checklist of what's ready / what to fix
dotnet run --project src/MCPTransfer.Agent -- whoami        # your address + pubkeys
```

`setup --local` picks the local anvil profile + a file store (no Pinata, no
gas, no faucet), generates your identity, writes the config, and prints the
exact Claude Desktop connector snippet (add `--write-claude-config` to merge it
in for you). `doctor` then tells you, line by line, what's wired up and what to
fix. Run `setup` with no flags for the interactive version (choose chain +
storage), or `setup --chain amoy --storage pinata --pinata-jwt …` for testnet.

That already exercises the whole crypto stack (the test suite encrypts,
chunks, signs, and round-trips real envelopes). For the end-to-end *on-chain*
flow you have two options below.

### 2. The on-chain round-trip, automated (recommended proof)

The gated integration tests spin up their own anvil, deploy the contracts, and
run the entire pipeline — handle resolution → hybrid-PQC encryption → IPFS →
on-chain `FileSent` → receive with content-hash corroboration → byte-identical
decrypt. They import anvil's pre-funded dev keys, so there's nothing to fund.

```powershell
$env:MCPTX_RUN_ANVIL_TESTS = '1'          # off by default
$env:MCPTX_FOUNDRY_BIN = 'E:\foundry'     # optional; else PATH / ~/.foundry/bin
dotnet test --filter "FullyQualifiedName~Integration"
```

See [docs/CHAIN.md](docs/CHAIN.md) → *Tests d'intégration (Anvil)*.

### 3. The on-chain round-trip, by hand (Alice → Bob, local anvil)

This drives the real CLI. Two agents share one local chain + one file-store
IPFS directory, each with its own identity file.

```powershell
# --- terminal 1: a throwaway chain + deployed contracts ---
anvil
# (terminal 2)
cd contracts
forge script script/Deploy.s.sol:Deploy --rpc-url http://127.0.0.1:8545 `
    --private-key 0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80 `
    --broadcast
# Deploys to the deterministic addresses the `anvil-local` profile already knows.

# --- shared config + a shared IPFS folder ---
$ipfs = "$env:TEMP\mcptx-demo"
dotnet run --project src/MCPTransfer.Agent -- config init --profile anvil-local --ipfs-dir $ipfs

# --- two identities ---
dotnet run --project src/MCPTransfer.Agent -- keygen --out alice.json
dotnet run --project src/MCPTransfer.Agent -- keygen --out bob.json
# note each printed Address; fund both so they can pay gas on anvil:
cast rpc anvil_setBalance <ALICE_ADDR> 0xDE0B6B3A7640000 --rpc-url http://127.0.0.1:8545
cast rpc anvil_setBalance <BOB_ADDR>   0xDE0B6B3A7640000 --rpc-url http://127.0.0.1:8545

# --- Bob makes himself reachable: publish keys + claim a handle ---
dotnet run --project src/MCPTransfer.Agent -- register-key --identity bob.json
dotnet run --project src/MCPTransfer.Agent -- claim bob    --identity bob.json

# --- Alice sends a file to "bob" ---
dotnet run --project src/MCPTransfer.Agent -- send report.pdf --to bob --mime application/pdf --identity alice.json
#   -> prints the manifest CID + content hash + tx hash

# --- Bob lists his inbox and decrypts (auto-corroborated against the chain) ---
dotnet run --project src/MCPTransfer.Agent -- inbox   --identity bob.json
dotnet run --project src/MCPTransfer.Agent -- receive <manifest-cid> --out received.pdf --identity bob.json
```

> Every command takes `--config PATH` (default `~/.mcptx/config.json`) and
> `--identity PATH` (default `~/.mcptx/identity.json`). `receive` corroborates
> the CID against the on-chain `FileSent` event by default; override with
> `--expect-hash 0x…`, widen the scan with `--since BLOCK`, or skip the chain
> lookup with `--no-verify-onchain`.

Full command reference: [docs/CLI.md](docs/CLI.md).

**IPFS backends** (precedence: `--ipfs-dir` > `--pinata-jwt` > memory):

- `--ipfs-dir DIR` — a shared-folder **file store**. Two `mcptx` processes
  pointing at the same directory exchange files with **no network provider**.
  Recommended for a real local round-trip.
- `--pinata-jwt JWT` (or `PINATA_JWT` env) — real IPFS network pinning.
- memory — in-process only, test-only (pins vanish on exit).

### Identity file

Plaintext JSON at `~/.mcptx/identity.json` (see
[AgentIdentityFile.cs](src/MCPTransfer.Core/Storage/AgentIdentityFile.cs)):

```json
{
  "mldsa_private_key": "base64 (ML-DSA-65 signing key)",
  "mlkem_private_key": "base64 (ML-KEM-768 KEM key)",
  "secp256k1_private_key": "0x… (32 bytes hex)",
  "version": 2
}
```

> **Encryption at rest (opt-in):** set `MCPTX_PASSPHRASE` before `keygen` and
> the file is written encrypted (v3: Argon2id → AES-256-GCM, KDF header bound
> as AAD); the same variable decrypts it on every load (CLI + MCP server).
> Without it the file is plaintext (`0600` on POSIX). An OS keyring / TPM
> remains the production target. v1 identity files (pre-ML-DSA) are not
> loadable — regenerate with `mcptx keygen --force`.

---

## MCP server

`mcptx mcp-serve` runs a [Model Context Protocol](https://modelcontextprotocol.io)
server over stdio, exposing MCPTransfer as 10 tools (`whoami`, `resolve`,
`whois`, `inbox`, `register_key`, `claim`, `block_sender`, `unblock_sender`,
`send_file`, `receive_file`) so an AI host can drive the whole protocol —
*"send this report to alice-ai"* → on-chain handle resolution + hybrid-PQC
encryption + IPFS pin + chain event. `receive_file` auto-corroborates the CID
against the on-chain record; `inbox` filters senders on the agent's on-chain
blocklist.

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
> request — a deliberate authority grant. Set `MCPTX_MCP_ROOT` to confine file
> reads/writes to a directory. Full reference + trust model:
> [docs/MCP.md](docs/MCP.md).

## On-chain layer

Four immutable contracts on Polygon Amoy (or Anvil locally):

- **FileRegistry** — emits `FileSent(from, to, cid, contentHash, ts)`
- **KeyRegistry** — secp256k1 pubkey in clear + a **keccak256 commitment** to
  the ML-KEM-768 pubkey (full key pinned to IPFS, verified against the hash
  by every sender — the distribution channel is untrusted)
- **AgentDirectory** — first-come-first-served `handle ↔ address` registry
  (`alice-ai` style); handles are transferable by their owner
- **Blocklist** — per-recipient sender blocklist, enforced client-side at
  inbox-read time (anti-spam, reversible)

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
var bobKeys = await chain.KeyRegistry.GetAsync(bob!);

// Announce a transfer
var tx = await chain.FileRegistry.SendAsync(
    bob!, manifestCid, contentHash, alice.Secp256k1);
```

> Détails complets : [docs/CHAIN.md](docs/CHAIN.md). Contracts source : [contracts/](contracts/).

## Cryptographic construction

> Détails complets : [docs/CRYPTO.md](docs/CRYPTO.md)

```
                Sender                                  Recipient
                   |                                        |
   eph_sk, eph_pk = secp256k1.gen()                         |
   ss1 = ECDH(eph_sk, recipient.secp256k1_pk)               |
   (kem_ct, ss2) = ML-KEM-768.Encaps(recipient.mlkem_pk)    |
   K = HKDF-SHA256(ss1 || ss2 || context)                   |
   ciphertext = AES-256-GCM(K, chunk)        per-chunk      |
                                                            |
                                            ss1 = ECDH(my.sk, eph_pk)
                                            ss2 = ML-KEM.Decaps(my.mlkem_sk, kem_ct)
                                            K  = HKDF(...)
                                            decrypt chunks
```

Each chunk uses a unique nonce `nonce_prefix(8 random) || chunk_idx(4 BE)`.
A hybrid-signed JSON **manifest** (ECDSA secp256k1 + ML-DSA-65) lists every
chunk's CID and tag; only the manifest's CID and content hash go on-chain.

## What's left

The protocol and reference implementation are done. Remaining work either
needs your own accounts or belongs to a productization phase (v2):

- ~~Needs your credentials~~ — **done**: contracts live on Amoy, round-trip
  validated over Pinata. Optional residue: `forge verify` the contract source
  on Polygonscan (needs an API key), and use an Alchemy/Infura RPC instead of
  the public one (which caps `eth_getLogs` so hard the client falls back to a
  ~45-block scan window).
- **Productization** — a hosted relay/gateway and an agent-discovery story
  (so AI agents find and adopt the service). The v1-contract follow-ups
  (transferable handles, blocklist, ML-KEM hash commitment) are **done** —
  see [docs/CHAIN.md](docs/CHAIN.md) → *Limites v1 → état v2*.
- **Hardening beyond POC scope** — OS keyring / TPM for the identity key
  (current: opt-in Argon2id+AES-GCM file encryption); strong zeroization
  (current: best-effort, see [docs/CRYPTO.md](docs/CRYPTO.md) → *Zeroization*);
  mid-flight cancellation of Nethereum RPC calls (a
  [documented](docs/MCP.md) Nethereum API limitation today).

## Known limits (POC scope)

- Metadata graph (who sends to whom, when) is public on-chain — confidentiality
  of *content* only, not of the social graph. Mixing/relayers are out of scope.
- Pinata free tier: 1 GB total storage. Hard cap on POC volume.
- AES-GCM cap: ~64 GiB per (key, nonce-prefix) pair. Larger files are rejected.
- Manifests are co-signed ECDSA secp256k1 + **ML-DSA-65** (post-quantum).
  However, because identity = an ECDSA Ethereum address, the *binding* of any
  PQC key to the identity remains classically secured — ML-DSA makes
  manifest-content authenticity post-quantum given a trusted binding, not the
  key→identity link. A PQC-native identity (off-EVM) is out of scope.
- Identity = Ethereum address (secp256k1). The ML-KEM public key is published
  in the on-chain `KeyRegistry` (self-published via `msg.sender`); the ML-DSA
  public key travels inside each signed manifest, bound by the ECDSA signature.

## License

TBD.
