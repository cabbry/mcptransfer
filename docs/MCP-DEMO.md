# Cross-machine MCP showcase

Two AI agents on two different machines exchanging a file, each driving its own
`mcptx mcp-serve` server as MCP tools — coordinating ONLY through the chain
(announcements) and IPFS (encrypted data), with no direct connection between
the machines.

This is the protocol's thesis made concrete: an MCP host (Claude Desktop,
Claude Code, Cursor, any agent framework) calls `send_file` / `inbox` /
`receive_file` in natural language; handle resolution, hybrid-PQC encryption,
IPFS pinning, the on-chain `FileSent`, and the corroborated decrypt all happen
behind the tool boundary.

> **Validated 2026-06-15** (PC side, driven over the raw MCP/JSON-RPC protocol):
> the PC's `mcptx mcp-serve` listed its 10 tools, ran `whoami`, and
> `receive_file`'d a file the Mac had sent — decrypted byte-identical
> (`tools/mcp-vitrine.ps1` is the repro). The Mac→PC data path was earlier
> proven end-to-end (docs/CHAIN.md). The only gap to a fully bidirectional,
> both-ends-Claude run is testnet POL for the sending agent's gas.

## Prerequisites (each machine)

1. Build: `dotnet build MCPTransfer.slnx -c Release`.
2. Config: `mcptx config init --profile amoy --pinata-jwt <JWT>` (the Amoy
   contract addresses are baked into the profile).
3. Identity: `mcptx keygen` (optionally with `MCPTX_PASSPHRASE` set to encrypt
   it at rest).
4. **The RECEIVING agent must be registered**: `mcptx register-key` (publishes
   its ML-KEM commitment so a sender can encrypt to it) — this is the only
   step that needs gas on the recipient side. The SENDING agent needs gas for
   the one `FileSent` transaction. Fund from any Amoy faucet without a
   mainnet-asset gate (e.g. the Google Cloud Web3 faucet).

## Register the MCP server with each Claude

Drop a `.mcp.json` in the project root on each machine (or use
`claude mcp add`), pointing at that machine's identity:

```json
{
  "mcpServers": {
    "mcptransfer": {
      "command": "dotnet",
      "args": [
        "<abs>/src/MCPTransfer.Agent/bin/Release/net10.0/mcptx.dll",
        "mcp-serve",
        "--identity", "<abs>/.mcptx/identity.json"
      ],
      "env": {
        "PINATA_JWT": "eyJ...",
        "MCPTX_MCP_ROOT": "<abs>/mcptx-workspace"
      }
    }
  }
}
```

`MCPTX_MCP_ROOT` confines `send_file`/`receive_file` to that directory — always
set it when exposing the server to a host (see docs/MCP.md, trust model). If
the identity is encrypted, add `"MCPTX_PASSPHRASE": "..."` to `env`.

Restart/reconnect Claude so it picks up the server; the ten `mcptransfer` tools
then appear.

## Run it (natural language)

On the **sender** machine, to the local Claude:

> Use the mcptransfer tools to send `report.pdf` to `bob-ai`.

→ Claude calls `send_file(path="…/mcptx-workspace/report.pdf", to="bob-ai")`,
which returns the manifest CID.

On the **recipient** machine, to its Claude:

> Check my mcptransfer inbox and decrypt the newest file into `received.pdf`.

→ Claude calls `inbox`, then `receive_file(cid=…, outPath="…/received.pdf")`.
The result reports the sender, whether it was corroborated against the on-chain
`FileSent`, and the metadata; the bytes are byte-identical to the original.

## Notes

- **No direct link** between the machines — only Amoy + IPFS. Neither needs an
  inbound port or the other's IP.
- Handle **transfer** is deliberately CLI-only (not an MCP tool) — too much
  authority for a prompt-injectable host.
- On the public Amoy RPC, `inbox`/corroboration may degrade to a narrow scan or
  signature-only verification (the integrity guarantee still holds via the
  manifest signatures). A managed RPC (Alchemy/Infura) removes this.
