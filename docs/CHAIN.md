# MCPTransfer — On-chain layer

> Reference for the three smart contracts and their C# bindings.
> Top-level overview in the [README](../README.md); crypto details in
> [docs/CRYPTO.md](CRYPTO.md).

## Sommaire

- [Vue d'ensemble](#vue-densemble)
- [Les trois contrats](#les-trois-contrats)
  - [FileRegistry](#fileregistry)
  - [KeyRegistry](#keyregistry)
  - [AgentDirectory](#agentdirectory)
- [Flux end-to-end on-chain](#flux-end-to-end-on-chain)
- [Configuration C#](#configuration-c)
- [Déploiement](#déploiement)
- [Coûts gas estimés](#coûts-gas-estimés)
- [Limites v1](#limites-v1)

---

## Vue d'ensemble

Trois contrats minimaux, **immuables** une fois déployés (pas d'admin, pas
d'upgrade, pas de fee).

```
┌────────────────────────────────────────────────────────────────────┐
│  FileRegistry      │ events FileSent(from,to,cid,hash,ts)          │
│                    │ ↑ inbox push notification                     │
├────────────────────┼───────────────────────────────────────────────┤
│  KeyRegistry       │ mapping address → ML-KEM-768 pubkey           │
│                    │ ↑ recipient pubkey lookup avant HybridKem      │
├────────────────────┼───────────────────────────────────────────────┤
│  AgentDirectory    │ mapping handle ↔ address (FCFS)                │
│                    │ ↑ "envoie à alice-ai" sans copier-coller addr │
└────────────────────┴───────────────────────────────────────────────┘
```

Target deployment : **Polygon Amoy** (chainId 80002). Local dev :
**Anvil** (chainId 31337).

---

## Les trois contrats

### FileRegistry

[`contracts/src/FileRegistry.sol`](../contracts/src/FileRegistry.sol)

**Rôle** : tableau d'affichage on-chain. Quand Alice envoie un fichier à Bob,
elle émet un event qui dit "j'ai déposé tel manifest CID pour toi". Bob
indexe sa boîte de réception en filtrant les events `FileSent` où
`indexed to == bob.address`.

**Surface** : 1 fonction, 1 event.

```solidity
event FileSent(
    address indexed from,
    address indexed to,
    string cid,
    bytes32 contentHash,
    uint64 timestamp
);

function send(address to, string calldata cid, bytes32 contentHash) external;
```

**Invariants enforced** :
- `to != address(0)`
- `bytes(cid).length > 0`
- `contentHash != bytes32(0)`
- `msg.sender` est l'expéditeur déclaré — impossible de spoofer

**Storage** : zéro. Tout dans les events. Coût d'émission ≈ 30k gas.

### KeyRegistry

[`contracts/src/KeyRegistry.sol`](../contracts/src/KeyRegistry.sol)

**Rôle** : chaque agent publie sa clé publique ML-KEM-768 (FIPS 203) sur
laquelle les expéditeurs vont encapsuler. Self-service strict — seul
`msg.sender` peut écrire son entrée.

**Surface** :

```solidity
mapping(address => bytes) private _mlkemPubkey;
uint256 public constant ML_KEM_768_PUBKEY_LENGTH = 1184;
uint64  public constant KEY_VERSION = 1;

event KeyPublished(address indexed who, bytes mlkemPubkey, uint64 version);

function publish(bytes calldata mlkemPubkey) external;  // must be 1184 bytes
function get(address who) external view returns (bytes memory);
```

**Coût** : ~190k gas la première fois (storage allocation), ~50k pour
overwrite. Free sur Amoy.

### AgentDirectory

[`contracts/src/AgentDirectory.sol`](../contracts/src/AgentDirectory.sol)

**Rôle** : annuaire `alice-ai` → `0xabc…`. First-come-first-served,
permanent, non-transférable en v1.

**Format de handle** : `[a-z0-9-]{3,32}` sans hyphen en début ou fin.
Validation on-chain caractère par caractère.

**Surface** :

```solidity
mapping(string => address) public handleToAddress;
mapping(address => string) public addressToHandle;

event HandleClaimed(string indexed handleHash, address indexed owner, string handle);

function claim(string calldata handle) external;
```

**Invariants** :
- Un address ne peut claim qu'**un seul** handle (forever)
- Un handle ne peut être claim qu'**une seule** fois (forever)
- Le handle doit passer `[a-z0-9-]{3,32}` + pas d'hyphen en bord

**Note événement** : `string indexed` produit un topic = `keccak256(handle)`.
Le handle lisible reste dans les données non-indexées.

---

## Flux end-to-end on-chain

Scénario complet : Alice envoie un fichier à `alice-bob.ai`.

```
1. Alice : `mcptx keygen` → identity locale
2. Alice : tx KeyRegistry.publish(alice.mlkem_pk)
3. Alice : tx AgentDirectory.claim("alice")          (1 fois pour la vie)

   ┌────────────────────────────────────────────────────────────┐
   │ Bob fait la même chose de son côté (publish + claim "bob") │
   └────────────────────────────────────────────────────────────┘

4. Alice veut envoyer à bob :
   a. dir.ResolveAsync("bob") → bobAddress
   b. keyRegistry.GetAsync(bobAddress) → bob.mlkem_pk
   c. HybridKem.Encapsulate(bobAddress, bob.mlkem_pk) → derived key
   d. ChunkedAead encrypts → IPFS uploads → SignedManifest
   e. fileRegistry.SendAsync(bobAddress, manifestCid, contentHash, alice.sk)
      → event FileSent émis

5. Bob :
   a. fileRegistry.WatchInboxAsync(bobAddress, fromBlock, …)
      → yields FileSentEvent (CID + hash)
   b. ipfs.FetchAsync(manifestCid) → SignedManifest bytes
   c. Vérif hash et signature
   d. HybridKem.Decapsulate avec bob.identity
   e. ChunkedAead decrypts → fichier reconstitué
```

---

## Configuration C#

```csharp
var config = new ChainConfig
{
    RpcUrl                = "https://rpc-amoy.polygon.technology",
    ChainId               = ChainConfig.AmoyChainId,         // 80002
    FileRegistryAddress   = EthereumAddress.FromHex("0x..."),
    KeyRegistryAddress    = EthereumAddress.FromHex("0x..."),
    AgentDirectoryAddress = EthereumAddress.FromHex("0x..."),
};

var chain = new EthereumChainClient(config);

// Pour les view calls : pas de signer requis
var pk = await chain.KeyRegistry.GetAsync(bobAddress);
var addr = await chain.AgentDirectory.ResolveAsync("bob");

// Pour les state-changing calls : le Secp256k1KeyPair signe la tx
var txHash = await chain.FileRegistry.SendAsync(
    bobAddress, manifestCid, contentHash, alice.Secp256k1);
```

`Web3` est construit en interne :
- **read-only** (sans signer) : une instance partagée par client
- **signing** : une nouvelle instance par appel state-changing, à partir
  du keypair fourni

---

## Déploiement

### Local (Anvil)

```sh
# Terminal 1
anvil

# Terminal 2
cd contracts
forge script script/Deploy.s.sol \
    --rpc-url anvil \
    --private-key 0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80 \
    --broadcast
```

Le script log les 3 adresses ; copier-coller dans `ChainConfig`.

### Tests d'intégration (Anvil)

La suite contient des tests d'intégration *live* (`tests/.../Integration/`) qui
démarrent eux-mêmes un Anvil éphémère, déploient les trois contrats via
`Deploy.s.sol`, puis exercent le cycle complet : publication de clés,
revendication de handle, `FileSent` + inbox, et un round-trip d'enveloppe
chiffrée de bout en bout (chiffrement → IPFS fichier → annonce on-chain →
réception avec corroboration du content-hash on-chain).

Ils sont **désactivés par défaut** (ils exigent une toolchain Foundry locale et
ne doivent pas tourner dans la CI partagée). Pour les lancer :

```powershell
$env:MCPTX_RUN_ANVIL_TESTS = '1'          # active la suite (sinon : no-op)
$env:MCPTX_FOUNDRY_BIN = 'E:\foundry'     # optionnel : dossier de anvil/forge
                                          # (sinon résolu via PATH ou ~/.foundry/bin)
dotnet test --filter "FullyQualifiedName~Integration"
```

Variables reconnues par la fixture :
- `MCPTX_RUN_ANVIL_TESTS` — `1`/`true` pour activer ; sinon chaque test sort tôt.
- `MCPTX_FOUNDRY_BIN` — dossier contenant `anvil`/`forge` (sinon PATH puis
  `~/.foundry/bin`).
- `MCPTX_ANVIL_PORT` — port TCP d'Anvil (défaut `8559`).

Si Foundry est introuvable alors même que le gate est actif, la fixture reste
désactivée (les tests no-op) plutôt que de faire échouer le run.

### Polygon Amoy

Pré-requis :
- Wallet avec POL (faucet : <https://faucet.polygon.technology/>)
- RPC endpoint (Alchemy / Infura / public)
- Polygonscan API key (pour `--verify`)

```sh
export POLYGON_AMOY_RPC_URL="https://..."
export POLYGONSCAN_API_KEY="..."
export AMOY_DEPLOYER_PK="0x..."

cd contracts
forge script script/Deploy.s.sol \
    --rpc-url amoy \
    --private-key $AMOY_DEPLOYER_PK \
    --broadcast \
    --verify
```

Les adresses déployées apparaissent dans :
- stdout du script
- `contracts/broadcast/Deploy.s.sol/80002/run-latest.json`

---

## Coûts gas estimés

Estimés analytiquement (à confirmer avec `forge test --gas-report`) :

| Opération | Gas approx | Coût Amoy (1 gwei) | Coût Amoy (10 gwei) |
|-----------|-----------:|-------------------:|--------------------:|
| `FileRegistry.send`         | ~30 000 | 0.00003 POL | 0.0003 POL |
| `KeyRegistry.publish` (1re) | ~190 000 | 0.00019 POL | 0.0019 POL |
| `KeyRegistry.publish` (rotate) | ~50 000 | 0.00005 POL | 0.0005 POL |
| `AgentDirectory.claim`      | ~110 000 | 0.00011 POL | 0.0011 POL |
| Déploiement total (3 contrats) | ~1 500 000 | 0.0015 POL | 0.015 POL |

À 1 POL ≈ 0.5 USD, un agent paie typiquement < 0.001 USD par envoi.

---

## Limites v1

| Limite | Mitigation v2 |
|--------|---------------|
| Handles non-transférables, non-révocables | Ajouter `transfer(string handle, address newOwner)` |
| Pas de filtrage on-chain par sender (alice peut spammer bob) | Bob filtre client-side ; ajouter `IBlocklist` côté lecteur |
| Pas de fee → pas de coût pour spam | Soit fee on-chain (mais voir [README — pas de fee](../README.md)) ; soit rate-limiting off-chain |
| ML-KEM pubkey en clair on-chain → social graph visible | Hashage avant publication + reveal off-chain ; rebrouille en v2 |
| Pas de batch operations | `multicall(bytes[] calls)` ou batched function |
| Pas de pagination sur les events `FileSent` | Indexeur off-chain (Goldsky, The Graph, Subsquid) |
