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
| `MCPTX_BLOCKLIST` | `chain.blocklist_address` (optional; unset = no inbox filtering) |
| `MCPTX_IPFS_KIND` | `ipfs.kind` (`pinata`, `file`, or `memory`) |
| `MCPTX_IPFS_DIR` | `ipfs.directory` (shared folder for the `file` backend) |
| `MCPTX_GATEWAY_URL` | `ipfs.gateway_url` |
| `PINATA_JWT` | `ipfs.pinata_jwt` |
| `MCPTX_PASSPHRASE` | not a config field — passphrase for the encrypted (v3) identity file: `keygen` encrypts when set, every load decrypts with it |

Unset variables leave the config file value untouched.

---

## Commands

### `mcptx keygen [--out PATH] [--force]`

Generate a new hybrid identity (secp256k1 + ML-KEM-768 + ML-DSA-65) and
write it to disk. Refuses to overwrite an existing identity without
`--force`. If `MCPTX_PASSPHRASE` is set, the file is encrypted at rest
(Argon2id + AES-256-GCM, format v3 — see docs/CRYPTO.md); otherwise it is
plaintext JSON (v2).

```sh
mcptx keygen
# Identity written to ~/.mcptx/identity.json
#   at rest : PLAINTEXT (set MCPTX_PASSPHRASE before keygen to encrypt)
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

Publish the local keys to the on-chain `KeyRegistry` (v2 flow): the
secp256k1 pubkey is stored in clear (senders need it for ECDH); the
ML-KEM-768 pubkey is first pinned to the configured IPFS backend, then its
keccak256 hash + CID are published as an on-chain commitment. Senders fetch
the key by CID and verify it against the hash before encapsulating.

```sh
mcptx register-key
# Publishing keys for 0xabc...
#   secp256k1 sha256:dead...    (33 bytes, stored on-chain)
#   mlkem     sha256:beef...  (1184 bytes, pinned via file)
#   mlkem cid : ...
#   mlkem hash: 0x...
#   tx hash   : 0x...
#   ✓ round-trip verified (on-chain entry matches)
```

### `mcptx claim <handle>`

Claim a handle on the `AgentDirectory`. Format: `[a-z0-9-]{3,32}`, no
leading or trailing hyphen. First-come-first-served; transferable by the
owner via `transfer-handle`.

### `mcptx transfer-handle <handle> --to <0xaddress>`

Transfer YOUR handle to a new owner address (e.g. after migrating to a
fresh keypair). Pre-flight checks ownership for a clean error; the new
owner must not already have a handle; you are freed and may claim a new
one. **Warning:** after the transfer the new owner fully controls the
handle. (Deliberately not exposed as an MCP tool.)

### `mcptx block <handle|0xaddress>` / `mcptx unblock <handle|0xaddress>`

Add/remove a sender on your on-chain `Blocklist`. Blocked senders'
`FileSent` events are hidden from `mcptx inbox` (advisory: enforcement is
client-side at read time; reversible). Requires `blocklist_address` in the
config (the `anvil-local` profile pre-fills it) or `MCPTX_BLOCKLIST`.

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

### `mcptx gc [--older-than DUR] [--cid CID]... [--dry-run] [--since BLOCK]`

Release (unpin) the IPFS content of transfers **you sent**, so the data plane
stays an ephemeral mailbox rather than a permanent archive — "files don't live
forever". The sender pinned the chunks + manifest, so the sender runs `gc` to
let them expire once recipients have had time to fetch. This unpins; it is not
a confidentiality control — copies that already escaped to third-party nodes
stay protected by the hybrid-PQC envelope, not by deletion.

Two ways to choose what to release (combinable):

| Mode | Selects |
|------|---------|
| `--older-than DUR` | Your `FileSent` transfers announced more than `DUR` ago. `DUR` is a **positive** integer + unit: `30d`, `12h`, `90m`, `3600s` (0 is rejected — it would select every transfer, including in-flight ones). Pages the chain for events indexed `from = you`. |
| `--cid CID` | A specific manifest CID you already know is delivered. Repeatable. No chain scan — **reliable on any RPC**. |

`--dry-run` prints exactly what would be released without unpinning anything —
**run it first**. `--since BLOCK` sets the **first** block of the age scan
(default `0`); the scan then pages forward to the chain head, so old transfers
are reached. Raise `--since` to skip ancient history you don't need to scan.

Your own registered ML-KEM key blob (published via `KeyRegistry`) is always
excluded: `gc` only ever targets `FileSent` manifest + chunk CIDs, which never
include it, and the key CID is additionally protected by name.

```sh
# Preview what would be released for transfers older than 30 days
mcptx gc --older-than 30d --dry-run

# Actually release them
mcptx gc --older-than 30d

# Release one known transfer by its manifest CID (works on public RPC)
mcptx gc --cid bafy...
```

> **Public-RPC caveat.** `--older-than` pages history in wide `eth_getLogs`
> windows. Public endpoints (e.g. Amoy's) cap that span to a few dozen blocks —
> far too small to page millions of blocks of history in any reasonable number
> of calls — so the age scan can't run there. On a **managed** RPC
> (Alchemy/Infura) the wide window is accepted and the scan reaches old
> transfers normally. For age-based gc on a public RPC, use a managed endpoint,
> or release transfers individually with `--cid` (always reliable). The command
> says so and exits non-zero if the scan is capped and no `--cid` was given.

Unpinning is idempotent and best-effort: a re-run skips already-released CIDs,
and a failed unpin is reported (non-zero exit) without aborting the rest.

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

The IPFS layer can be: a **shared-folder file store** (`mcptx config init
--ipfs-dir /shared/path` — both agents point at the same directory, works
fully cross-process with no network), a real **Pinata JWT** (`--pinata-jwt
$PINATA_JWT`), or **memory** (in-process, tests only). Anvil running locally
provides the chain layer.

This exact flow is verified end-to-end (single-chunk and multi-chunk 40 MiB,
byte-identical round-trip) using the `--ipfs-dir` file backend against a
local anvil.
