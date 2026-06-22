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

## Les quatre contrats

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

### KeyRegistry (v2 : hash commitment)

[`contracts/src/KeyRegistry.sol`](../contracts/src/KeyRegistry.sol)

**Rôle** : chaque agent publie sa clé secp256k1 compressée **en clair**
(l'expéditeur en a besoin pour l'ECDH) plus un **engagement** sur sa clé
ML-KEM-768 : le hash keccak256 de la clé (1184 octets) et un pointeur
content-addressed (CID) où la clé complète se récupère. Self-service
strict — seul `msg.sender` peut écrire son entrée.

**Pourquoi un commitment plutôt que la clé complète on-chain (v1)** :
- le gas de `publish` chute d'un ordre de grandeur (~1.3M → ~150k) ;
- la chaîne ne porte plus de matériel de clé volumineux ;
- le canal de distribution devient **non-trusté** : le client de référence
  épingle la clé sur IPFS, mais n'importe quel canal convient — le lecteur
  DOIT vérifier `keccak256(cléRécupérée) == mlkemHash` avant d'encapsuler
  (c'est ce que fait `RecipientResolver`).

**Surface** :

```solidity
uint256 public constant SECP256K1_COMPRESSED_LENGTH = 33;
uint256 public constant MAX_CID_LENGTH = 128;
uint64  public constant KEY_VERSION = 2;

event KeysPublished(address indexed who, bytes secp256k1Pubkey,
                    bytes32 mlkemHash, string mlkemCid, uint64 version);

function publish(bytes calldata secp256k1Pubkey, bytes32 mlkemHash,
                 string calldata mlkemCid) external;
function getSecp256k1(address who) external view returns (bytes memory);
function getMlKem(address who) external view
    returns (bytes32 mlkemHash, string memory mlkemCid);
```

Côté C#, `KeyPublication.PublishAsync` fait le flux complet
(pin IPFS → hash → publish) ; `mcptx register-key` et l'outil MCP
`register_key` l'utilisent.

### AgentDirectory (v2 : handles transférables)

[`contracts/src/AgentDirectory.sol`](../contracts/src/AgentDirectory.sol)

**Rôle** : annuaire `alice-ai` → `0xabc…`. First-come-first-served ;
depuis la v2 le propriétaire peut **transférer** son handle (migration vers
une nouvelle keypair, par exemple).

**Format de handle** : `[a-z0-9-]{3,32}` sans hyphen en début ou fin.
Validation on-chain caractère par caractère.

**Surface** :

```solidity
mapping(string => address) public handleToAddress;
mapping(address => string) public addressToHandle;

event HandleClaimed(string indexed handleHash, address indexed owner, string handle);
event HandleTransferred(string indexed handleHash, address indexed from,
                        address indexed to, string handle);

function claim(string calldata handle) external;
function transfer(string calldata handle, address newOwner) external;
```

**Invariants** :
- Un address possède au plus **un** handle à la fois
- Un handle non-transféré reste lié à son owner ; seul l'owner peut `transfer`
- `transfer` exige que le nouveau owner n'ait pas déjà de handle ; l'ancien
  owner est libéré et peut re-claim un autre handle
- Le handle doit passer `[a-z0-9-]{3,32}` + pas d'hyphen en bord

**Note événement** : `string indexed` produit un topic = `keccak256(handle)`.
Le handle lisible reste dans les données non-indexées.

CLI : `mcptx transfer-handle <handle> --to 0x…` (volontairement absent de la
surface MCP — donner ce pouvoir à un host MCP serait excessif).

### Blocklist (v2 : anti-spam advisory)

[`contracts/src/Blocklist.sol`](../contracts/src/Blocklist.sol)

**Rôle** : `FileRegistry.send` est volontairement permissionless, donc
n'importe qui peut "spammer" la inbox de n'importe qui. La parade est au
moment de la **lecture** : chaque destinataire enregistre on-chain les
expéditeurs qu'il ignore, et les clients honnêtes filtrent ces events
(`InboxFilter` côté C# — un `eth_call isBlocked` par expéditeur distinct).

**État purement advisory** : rien n'empêche on-chain un expéditeur bloqué
d'émettre des events ; ils ne sont simplement plus affichés. Réversible.

```solidity
mapping(address => mapping(address => bool)) public isBlocked; // recipient => sender

event BlockSet(address indexed recipient, address indexed sender, bool blocked);

function setBlocked(address sender, bool blocked) external;
```

CLI : `mcptx block <handle|0x…>` / `mcptx unblock <handle|0x…>` ;
MCP : outils `block_sender` / `unblock_sender`. `mcptx inbox` et l'outil MCP
`inbox` affichent le nombre d'events masqués. Adresse optionnelle dans la
config (`blocklist_address` / `MCPTX_BLOCKLIST`) — sans elle, le filtrage est
désactivé et block/unblock indisponibles.

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

## Cycle de vie du stockage (gc / unpin)

La chaîne ne stocke qu'un **pointeur** (le CID du manifest) et un hash ; les
octets chiffrés vivent dans le plan de données (IPFS / Pinata). Sans
nettoyage, ces blobs resteraient épinglés **à vie** et l'expéditeur paierait
leur hébergement indéfiniment. `mcptx gc` rend le plan de données
**éphémère** : l'expéditeur — qui a épinglé les chunks + le manifest — les
dépingle une fois que le destinataire a eu le temps de récupérer.

```
Expéditeur                          Plan de données (IPFS/Pinata)
   │  send  ─ pin(chunks, manifest) ──────────►  [épinglés]
   │  FileRegistry.send(cid, hash)               (announce on-chain)
   │                                                   │
   │  ... le destinataire fait `receive` ...           │
   │                                                   ▼
   │  gc --older-than 30d / --cid <cid>  ── unpin ──►  [libérés]
```

**Points clés** (implémentation : [`StorageGc`](../src/MCPTransfer.Core/Chain/StorageGc.cs),
CLI : `mcptx gc`) :

- **Opération côté expéditeur.** Le gc utilise `FileRegistry.GetSentAsync`
  (events `FileSent` indexés `from == moi`) — le miroir de `GetInboxAsync`. On
  ne dépingle que ce qu'on a soi-même épinglé.
- **Pas une garantie de confidentialité.** Dépingler retire l'hébergement,
  pas le secret : une copie déjà aspirée par un nœud tiers reste protégée par
  l'enveloppe hybride post-quantique, jamais par la suppression. Le gc est une
  question de **coût/hygiène**, pas de sécurité.
- **CIDs uniques par envoi** (fraîche aléa par transfert : clé éphémère, ct
  KEM, nonce). Dépingler un transfert n'affecte jamais un autre.
- **La clé ML-KEM enregistrée n'est jamais touchée.** C'est de
  l'infrastructure permanente publiée via `KeyRegistry`, pas un transfert ;
  elle n'apparaît jamais comme CID de manifest/chunk, et le CID est en plus
  protégé nominativement.
- **Idempotent + best-effort.** Un re-run saute les CIDs déjà libérés ; un
  unpin en échec est rapporté sans interrompre le reste.

**Caveat RPC public.** Le mode `--older-than` repose sur `eth_getLogs`, capé
par les endpoints publics à une fenêtre trop étroite pour atteindre les vieux
transferts. Pour le gc par âge sur RPC public, utiliser un endpoint managé, ou
libérer transfert par transfert avec `--cid` (fiable partout). Détails CLI :
[docs/CLI.md](CLI.md#mcptx-gc---older-than-dur---cid-cid---dry-run---since-block).

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

**Déploiement canonique du POC (2026-06-10)** — pré-rempli par
`mcptx config init --profile amoy` :

| Contrat | Adresse Amoy |
|---------|--------------|
| FileRegistry | [`0x04d02596F41b620857603240d822309847A07261`](https://amoy.polygonscan.com/address/0x04d02596F41b620857603240d822309847A07261) |
| KeyRegistry | [`0x00e92639C38666b2FA0f9f3367cD6C6E746cB597`](https://amoy.polygonscan.com/address/0x00e92639C38666b2FA0f9f3367cD6C6E746cB597) |
| AgentDirectory | [`0x86fb0B991dBaA25Dc54b95F2f6a81742b0c0Ca67`](https://amoy.polygonscan.com/address/0x86fb0B991dBaA25Dc54b95F2f6a81742b0c0Ca67) |
| Blocklist | [`0x67df7EF83c6F5c87AD6DfD816437C76a11578CE7`](https://amoy.polygonscan.com/address/0x67df7EF83c6F5c87AD6DfD816437C76a11578CE7) |

Round-trip live validé le jour du déploiement : fichier de 18 Mo chiffré en
2 chunks épinglés sur Pinata, annoncé via `FileSent`, reçu et déchiffré
byte-identique par le destinataire (`alice-live` → `bob-live`).

⚠️ Le RPC public `rpc-amoy.polygon.technology` cape sévèrement
`eth_getLogs` ; le client retombe automatiquement sur une fenêtre étroite,
et `--since BLOCK` reste la valeur sûre pour l'historique. Un endpoint
Alchemy/Infura gratuit lève la contrainte.

Pour déployer votre propre instance — pré-requis :
- Wallet avec POL (faucet : <https://faucet.polygon.technology/> ou
  <https://faucets.chain.link/polygon-amoy>)
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

## Limites v1 → état v2

| Limite v1 | État |
|-----------|------|
| Handles non-transférables, non-révocables | ✅ **fait** — `AgentDirectory.transfer(handle, newOwner)` + `mcptx transfer-handle` |
| Pas de filtrage par sender (alice peut spammer bob) | ✅ **fait** — contrat `Blocklist` + filtrage client-side (`InboxFilter`, `mcptx block/unblock`) |
| ML-KEM pubkey en clair on-chain (1184 B, ~1.3M gas) | ✅ **fait** — KeyRegistry v2 : hash commitment + CID, clé distribuée off-chain et vérifiée à la lecture |
| Pas de fee → pas de coût pour spam | ⏳ assumé — pas de fee par design ([README — pas de fee](../README.md)) ; le Blocklist mitige côté lecture, rate-limiting off-chain en option pour l'infra hébergée |

Note honnêteté sur le commitment ML-KEM : la clé reste **publiquement
récupérable** (le client de référence l'épingle sur IPFS public). Le gain
principal est gas + empreinte chaîne ; le commitment permet aussi une
distribution privée hors-bande pour qui le souhaite — la chaîne ne voit que
le hash. Ce n'est PAS une protection du social graph (qui reste visible via
les events `FileSent`).
| Pas de batch operations | `multicall(bytes[] calls)` ou batched function |
| Pas de pagination sur les events `FileSent` | Indexeur off-chain (Goldsky, The Graph, Subsquid) |
