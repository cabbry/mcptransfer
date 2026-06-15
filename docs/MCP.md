# MCPTransfer — MCP server (`mcptx mcp-serve`)

> How an AI host (Claude Desktop, Cursor, an agent SDK) drives MCPTransfer
> as a set of MCP tools. CLI reference: [docs/CLI.md](CLI.md). Crypto +
> on-chain layers: [docs/CRYPTO.md](CRYPTO.md), [docs/CHAIN.md](CHAIN.md).

## What it is

`mcptx mcp-serve` runs a [Model Context Protocol](https://modelcontextprotocol.io)
server over **stdio**, exposing MCPTransfer's capabilities as tools. An AI
asked to *"send this report to alice-ai"* calls the `send_file` tool; the
server resolves the handle on chain, encrypts the file end-to-end, pins it
to IPFS, and announces it on chain — all without the AI ever touching keys
or contracts directly.

Built on the official `ModelContextProtocol` C# SDK (1.4.0).

## Setup

1. Generate an identity and a config once (see [docs/CLI.md](CLI.md)):

   ```sh
   mcptx keygen
   mcptx config init --profile anvil-local --ipfs-dir /shared/ipfs
   ```

2. Register the host to launch the server. **Claude Desktop**
   (`claude_desktop_config.json`):

   ```json
   {
     "mcpServers": {
       "mcptransfer": {
         "command": "dotnet",
         "args": [
           "E:/Projects/MCPTransfer/src/MCPTransfer.Agent/bin/Debug/net10.0/mcptx.dll",
           "mcp-serve"
         ],
         "env": {
           "PINATA_JWT": "eyJ...optional, for the pinata backend..."
         }
       }
     }
   }
   ```

   Or, with a published single-file `mcptx` executable:

   ```json
   {
     "mcpServers": {
       "mcptransfer": { "command": "mcptx", "args": ["mcp-serve"] }
     }
   }
   ```

   Pass `--identity` / `--config` in `args` to override the default
   `~/.mcptx/*` paths.

3. Restart the host. The `mcptransfer` tools appear in the tool list.

## Tools

All tools return JSON. Errors are surfaced as MCP tool errors (the server
catches the exception and sets `isError`).

| Tool | Kind | Description |
|------|------|-------------|
| `whoami` | read | This agent's address + key fingerprints |
| `resolve` | read | Handle → address (or unclaimed) |
| `whois` | read | Handle/address → address, reverse handle, key registration (secp fingerprint + ML-KEM hash/CID) |
| `inbox` | read | FileSent events addressed to this agent (optional `since_block`); senders on the blocklist are filtered out (`blocked_hidden` count) |
| `register_key` | **signed** | Pin the ML-KEM-768 pubkey to IPFS, then publish secp256k1 + keccak256 commitment + CID on chain |
| `claim` | **signed** | Claim a handle (first-come-first-served) |
| `block_sender` | **signed** | Block a sender (handle or 0x) on this agent's on-chain blocklist |
| `unblock_sender` | **signed** | Reverse a block |
| `send_file` | **signed** | Encrypt + pin a local file for a recipient, announce on chain |
| `receive_file` | read | Fetch a CID, verify, decrypt to a local path (atomic); auto-corroborates against the on-chain FileSent |

(Handle **transfer** is deliberately CLI-only — `mcptx transfer-handle` —
giving an MCP host the power to permanently hand the agent's name to an
arbitrary address would be an excessive authority grant.)

The optional tool parameters (`mime`, `expect_hash`, `since_block`) are
genuinely optional in the tool schema (they carry a `null` default), so a host
may omit them. For a two-machine, both-ends-Claude walkthrough see
[docs/MCP-DEMO.md](MCP-DEMO.md).

`send_file(path, to, mime?)` and `receive_file(cid, outPath)` operate on
**paths on the server's filesystem** — the natural model for a local MCP
server that shares a filesystem with its host.

### ⚠️ Filesystem access & the `MCPTX_MCP_ROOT` sandbox

By default `send_file` can **read any file** the server process can access,
and `receive_file` can **write (overwrite) any path** it can write. A
compromised or prompt-injected host could therefore exfiltrate secrets
(`send_file` an arbitrary file to an attacker handle) or clobber files
(`receive_file` over `~/.mcptx/identity.json` or an autostart script).

**Set `MCPTX_MCP_ROOT` to a directory to confine both tools to it.** When
set, every `path`/`out_path` must resolve inside that root (traversal,
absolute escapes, and sibling-prefix tricks are rejected); otherwise the
tool refuses with an "outside the configured MCP workspace root" error. When
unset, the server prints a startup warning to stderr. **Always set a root
when exposing the server to an untrusted host.**

```json
"env": { "MCPTX_MCP_ROOT": "/home/agent/mcptx-workspace" }
```

## Trust model

⚠️ **The server holds the agent's private key and signs transactions on the
host's request.** When the AI calls `send_file` / `claim` / `register_key`,
the server signs a real transaction and spends gas with the local identity's
key. This is the intended model — the AI delegates signing authority to the
server — but it means:

- Run `mcp-serve` only with an identity whose key you are willing to let the
  connected host spend gas with.
- For testnet (anvil / Amoy) this is low-stakes; for any value-bearing chain
  it is a real authority grant.
- The signed-action tools (`register_key`, `claim`, `send_file`) are
  explicitly described as gas-spending in their tool descriptions so the host
  can surface that to the user.

The on-chain trust boundaries documented in
[docs/CRYPTO.md → Frontières de confiance on-chain](CRYPTO.md) apply equally
here (a lying RPC can redirect a `send_file`; the recipient's secp256k1 key
is verified to derive to its address, and since registry v2 the ML-KEM key
fetched from IPFS is verified against its on-chain keccak256 commitment).

## Lifecycle & concurrency notes

The server registers identity, config, the chain client, and the IPFS client
as DI singletons. The chain client's read-only Web3 instances and the Pinata
HttpClient are therefore created once for the server's lifetime and disposed
on shutdown — unlike the one-shot CLI, a long-lived server must not churn
them per call.

**Concurrent state-changing tools.** All of `register_key` / `claim` /
`block_sender` / `unblock_sender` / `send_file` sign transactions from the
same EOA and rely on auto-nonce. If a host called two of them concurrently,
both would fetch the same pending nonce and one tx would be rejected. The
server serializes the transaction-submission step behind a single signing
lock, so concurrent calls queue rather than collide. (Read-only tools —
`whoami`/`resolve`/`whois`/`inbox` — run freely in parallel.)

**Encrypted identity.** If the identity file is encrypted (v3), set
`MCPTX_PASSPHRASE` in the server's `env` block; the key material is
decrypted once at startup. On shutdown the server zeroes the agent's cached
private-key material (best-effort — see docs/CRYPTO.md, Zeroization).

**Known limitations (accepted for the POC).** Cancellation tokens are passed
to the tools but the underlying Nethereum RPC calls are not yet abortable
mid-flight (a hung RPC runs to its HTTP timeout). Key zeroization is
best-effort only (BouncyCastle internals and GC copies are not coverable in
managed .NET). The `inbox` tool caps the `eth_getLogs` span at
50 000 blocks (most public RPCs reject wider ranges); page through history
with `since_block` for older events.
