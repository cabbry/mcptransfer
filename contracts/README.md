# MCPTransfer — Solidity contracts

Three minimal contracts that back the MCPTransfer protocol. Target chain:
**Polygon Amoy** testnet (chainId 80002). Local devnet: **Anvil** (chainId 31337).

| Contract | Purpose |
|----------|---------|
| `FileRegistry.sol` | Emits `FileSent(from, to, cid, contentHash, timestamp)` so recipients can index their inbox without polling IPFS |
| `KeyRegistry.sol` | `mapping(address => bytes)` for each agent's ML-KEM-768 public key. Senders look it up before encapsulating |
| `AgentDirectory.sol` | First-come-first-served `handle ↔ address` registry. Permanent, non-transferable in v1 |

No upgradeability, no admin role, no fees. The 3 contracts are immutable once
deployed.

## Setup (one-time, after installing Foundry)

forge-std is committed as a git submodule. After cloning:

```sh
git submodule update --init --recursive
```

If you prefer fetching forge-std fresh:

```sh
forge install foundry-rs/forge-std
```

## Commands

```sh
forge build                                          # compile
forge test -vv                                       # run tests with traces
forge test --gas-report                              # with gas usage
anvil                                                # local devnet @ http://127.0.0.1:8545
forge script script/Deploy.s.sol --rpc-url anvil \
    --private-key 0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80 \
    --broadcast                                      # deploy to local anvil

# Amoy deployment (after env vars are set)
forge script script/Deploy.s.sol --rpc-url amoy \
    --private-key $AMOY_DEPLOYER_PK --broadcast --verify
```

Required env vars for Amoy:

- `POLYGON_AMOY_RPC_URL` — any Amoy RPC endpoint (Alchemy, Infura, public RPC)
- `POLYGONSCAN_API_KEY` — for `--verify`
- `AMOY_DEPLOYER_PK` — deployer private key (must hold POL from a faucet)

## Layout

```
contracts/
├── foundry.toml
├── remappings.txt           # forge-std/ -> lib/forge-std/src/
├── src/
│   ├── FileRegistry.sol
│   ├── KeyRegistry.sol
│   └── AgentDirectory.sol
├── test/
│   ├── FileRegistry.t.sol
│   ├── KeyRegistry.t.sol
│   └── AgentDirectory.t.sol
└── script/
    └── Deploy.s.sol
```
