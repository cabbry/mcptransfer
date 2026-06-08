# `mcptx` CLI reference

> Full command reference. See [README](../README.md) for a tutorial-style
> walkthrough, [docs/CHAIN.md](CHAIN.md) for the on-chain layer, and
> [docs/CRYPTO.md](CRYPTO.md) for the crypto construction.

## Global flags

Most commands accept these overrides:

| Flag | Default | Notes |
|------|---------|-------|
| `--config PATH` | `~/.mcptx/config.json` | Path to the config file |
| `--identity PATH` | `~/.mcptx/identity.json` | Path to the agent identity file |

## Environment variables

Override individual config fields at runtime:

| Variable | Maps to |
|----------|---------|
| `MCPTX_RPC_URL` | `chain.rpc_url` |
| `MCPTX_CHAIN_ID` | `chain.chain_id` |
| `MCPTX_FILE_REGISTRY` | `chain.file_registry_address` |
| `MCPTX_KEY_REGISTRY` | `chain.key_registry_address` |
| `MCPTX_AGENT_DIRECTORY` | `chain.agent_directory_address` |
| `MCPTX_IPFS_KIND` | `ipfs.kind` (`pinata` or `memory`) |
| `MCPTX_GATEWAY_URL` | `ipfs.gateway_url` |
| `PINATA_JWT` | `ipfs.pinata_jwt` |

Unset variables leave the config file value untouched.

---

## Commands

### `mcptx keygen [--out PATH] [--force]`

Generate a new hybrid identity (secp256k1 + ML-KEM-768) and write it to
disk. Refuses to overwrite an existing identity without `--force`.

```sh
mcptx keygen
# Identity written to ~/.mcptx/identity.json
# Address: 0xabc...
```

### `mcptx whoami [--in PATH] [--full]`

Print the local agent's address and public keys. ML-KEM public key is
truncated by default (`--full` prints the entire 1184-byte base64).

### `mcptx config init [--profile anvil-local|amoy] [--pinata-jwt JWT] [--out PATH] [--force]`

Bootstrap `~/.mcptx/config.json` from a canned profile.

| Profile | Use case |
|---------|----------|
| `anvil-local` (default) | Local Foundry anvil + deterministic deploy addresses |
| `amoy` | Polygon Amoy testnet RPC, empty contract addresses to fill manually |

### `mcptx config show [--config PATH]`

Print the effective configuration (file overlaid with env-var overrides).

### `mcptx register-key`

Publish the local secp256k1 + ML-KEM-768 public keys to the on-chain
`KeyRegistry`. Both keys are required: secp256k1 for ECDH on the sender
side, ML-KEM-768 for PQC encapsulation. The call is signed by the agent's
own secp256k1 private key.

```sh
mcptx register-key
# Publishing keys for 0xabc...
#   secp256k1 sha256:dead... (33 bytes)
#   mlkem     sha256:beef... (1184 bytes)
#   tx hash: 0x...
#   ✓ round-trip verified
```

### `mcptx claim <handle>`

Claim a handle on the `AgentDirectory`. Format: `[a-z0-9-]{3,32}`, no
leading or trailing hyphen. First-come-first-served, permanent.

### `mcptx resolve <handle>`

Single-value lookup: handle → 0x address.

### `mcptx whois <handle|0xaddress>`

Aggregate lookup: address ↔ handle plus both pubkey fingerprints. Useful
to inspect whether a peer has completed registration.

### `mcptx send <file> --to <handle|0xaddress> [--mime TYPE]`

End-to-end send:

1. Resolve `--to` (handle or address).
2. Fetch recipient pubkeys from `KeyRegistry`; verify the secp256k1
   pubkey derives to the declared address.
3. Encrypt `<file>` via `EnvelopeWriter` (HybridKem → chunked AES-GCM →
   signed manifest), upload each chunk + the manifest to IPFS via the
   configured backend.
4. Emit a `FileSent` event on `FileRegistry` with the manifest CID and
   its keccak-256 content hash.

```sh
mcptx send report.pdf --to alice-ai --mime application/pdf
# Sending 'report.pdf' to alice-ai (0xabc...)
#   manifest CID: bafy...
#   chunks: 1
#   total size: 12345 bytes
#   tx hash: 0x...
# ✓ sent
```

### `mcptx inbox [--since BLOCK]`

List recent `FileSent` events addressed to this agent.

```sh
mcptx inbox
# Inbox for 0xabc...
#   scanning blocks 100000 .. 110000 (latest)
#   #    block      timestamp            from                                         cid
#   ---- ---------- -------------------- -------------------------------------------- ------------
#   0    109987     2026-05-26 10:21:33  0xdead...                                    bafy...
```

### `mcptx receive <cid> --out PATH`

Fetch the signed manifest at `<cid>` from IPFS, verify its signature
and per-chunk AEAD tags, atomically decrypt the plaintext to `<PATH>`
(via `.tmp` + rename — failure leaves no partial file).

---

## End-to-end flow (Alice → Bob, anvil profile)

```sh
# In two separate shells (Alice + Bob)

# Both:
mcptx keygen
mcptx config init --profile anvil-local

# Both publish their key material on chain
mcptx register-key

# Bob claims a handle so Alice doesn't have to type her recipient's hex
mcptx claim bob

# Alice sends to Bob's handle
mcptx send report.pdf --to bob --mime application/pdf

# Bob lists inbox and decrypts
mcptx inbox
mcptx receive bafy... --out received.pdf
```

The IPFS layer requires either a real Pinata JWT (`mcptx config init
--pinata-jwt $PINATA_JWT`) or sender + receiver running in the same
process (memory mode — only for tests). Anvil running locally provides
the chain layer.
