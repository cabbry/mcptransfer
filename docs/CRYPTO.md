# MCPTransfer — Mécanisme cryptographique

> Document de référence sur la pile cryptographique du POC.
> Lecture détaillée — vue d'ensemble dans le [README](../README.md).

## Sommaire

- [Vue d'ensemble](#vue-densemble)
- [Couche 1 — Identité : secp256k1](#couche-1--identité--secp256k1)
- [Couche 2 — KEM hybride (cœur du système)](#couche-2--kem-hybride-cœur-du-système)
  - [Pourquoi hybride : "Harvest now, decrypt later"](#pourquoi-hybride--harvest-now-decrypt-later)
  - [Composant A — ECDHE secp256k1](#composant-a--ecdhe-secp256k1)
  - [Composant B — ML-KEM-768](#composant-b--ml-kem-768)
  - [Combinaison : HKDF-SHA256](#combinaison--hkdf-sha256)
- [Couche 3 — Chiffrement des chunks : AES-256-GCM](#couche-3--chiffrement-des-chunks--aes-256-gcm)
- [Couche 4 — Signature du manifest : ECDSA secp256k1](#couche-4--signature-du-manifest--ecdsa-secp256k1)
- [Couche 5 — Hashes : Keccak-256](#couche-5--hashes--keccak-256)
- [Threat model](#threat-model)
- [Flux complet bout-en-bout](#flux-complet-bout-en-bout)
- [Stockage des clés : chiffrement at-rest et zeroization](#stockage-des-clés--chiffrement-at-rest-et-zeroization)
- [Références](#références)

---

## Vue d'ensemble

Cinq couches indépendantes, chacune avec un rôle précis :

```
┌─────────────────────────────────────────────────────────────┐
│  IDENTITÉ        │  secp256k1 keypair                       │
│                  │  → adresse Ethereum (= identifiant)      │
├──────────────────┼──────────────────────────────────────────┤
│  KEM HYBRIDE     │  ECDHE secp256k1  ⊕  ML-KEM-768         │
│                  │  ↓                                       │
│                  │  HKDF-SHA256(ss1 ‖ ss2 ‖ info)           │
│                  │  ↓                                       │
│                  │  clé symétrique 256-bit K                │
├──────────────────┼──────────────────────────────────────────┤
│  CHIFFREMENT     │  AES-256-GCM par chunk (16 MiB)          │
│                  │  nonce = 8B random ‖ 4B chunk_idx        │
├──────────────────┼──────────────────────────────────────────┤
│  SIGNATURE       │  HYBRIDE : ECDSA secp256k1 + ML-DSA-65    │
│                  │  (les deux signatures vérifiées)         │
├──────────────────┼──────────────────────────────────────────┤
│  HASHES          │  Keccak-256 (cohérence EVM)              │
└─────────────────────────────────────────────────────────────┘
```

**Suite identifier officielle** : `Hybrid-secp256k1+MLKEM768-AES256GCM`.
Versionnée dans chaque manifest. Depuis la Phase 5, la signature est
elle aussi hybride (ECDSA + ML-DSA-65) — cf. Couche 4.

---

## Couche 1 — Identité : secp256k1

### Définition

Courbe elliptique sur un corps fini de 256 bits, équation
`y² = x³ + 7 (mod p)`. C'est la courbe de Bitcoin et Ethereum.

- **Clé privée** : un scalaire `sk ∈ [1, n-1]` (32 bytes)
- **Clé publique** : un point `pk = sk · G` sur la courbe
  (33 bytes compressé, 65 décompressé)
- **Adresse Ethereum** = `Keccak-256(pk_uncompressed[1:])[12:]` → 20 bytes

**Sécurité classique** : ~128 bits (problème du log discret elliptique).

### Pourquoi secp256k1 et pas Curve25519

Curve25519 est techniquement *supérieure* pour un nouveau design :
plus rapide, design plus propre, pas de malléabilité ECDSA.

Mais **on est lock-in EVM** :

- L'adresse on-chain est dérivée de secp256k1
- Le smart contract vérifie `msg.sender` via signature ECDSA secp256k1
- Utiliser une autre courbe = devoir maintenir **deux** identités
  (une on-chain, une crypto) → confusion, surface d'attaque, UX dégradée

Trade-off accepté : secp256k1 partout.

### Lib (.NET)

```csharp
using Org.BouncyCastle.Asn1.Sec;          // SecNamedCurves.GetByName("secp256k1")
using Org.BouncyCastle.Crypto.Parameters; // EC{Private,Public}KeyParameters
using Org.BouncyCastle.Crypto.Generators; // ECKeyPairGenerator
```

---

## Couche 2 — KEM hybride (cœur du système)

### Pourquoi hybride : "Harvest now, decrypt later"

Un adversaire **enregistre** aujourd'hui tout le trafic chiffré.
Dans 10–20 ans, quand un ordinateur quantique avec suffisamment de
qubits stables existera, l'algorithme de Shor casse secp256k1 →
il **déchiffre rétroactivement** tout ce qu'il a stocké.

Pour des fichiers archivés (contrats, données médicales, propriété
intellectuelle), c'est une menace **réelle aujourd'hui**, pas dans 20 ans.

**Solution standard** (NIST SP 800-227, IETF X-Wing, BSI TR-02102) :
**toujours hybrider**. Jamais PQC seul, jamais classique seul pour
les nouveaux designs sensibles à long terme.

Logique : la clé finale est sûre tant qu'**au moins une** des deux
primitives tient.

| Scénario | Classique cassé | PQC cassé | K compromise ? |
|----------|-----------------|-----------|----------------|
| Quantum break secp256k1     | ✅ | ❌ | **Non** — ML-KEM tient |
| ML-KEM faille découverte    | ❌ | ✅ | **Non** — ECDH tient |
| Les deux cassés             | ✅ | ✅ | Oui — game over (mais alors *toute* la crypto mondiale tombe) |

### Composant A — ECDHE secp256k1

ECDH **éphémère** = on génère une nouvelle paire `(eph_sk, eph_pk)`
**à chaque envoi**.

```
Sender                                  Recipient
eph_sk, eph_pk = secp256k1.gen()
ss1 = ECDH(eph_sk, recipient.pk)
                                        ss1 = ECDH(recipient.sk, eph_pk)
```

**Propriété clé : forward secrecy**. Si la clé long-terme du destinataire
fuite plus tard, les transferts passés restent protégés (l'éphémère
a été détruite). Sans éphémère, une fuite compromet *tout l'historique*.

Le shared secret `ss1` est la coordonnée X du point
`eph_sk · recipient.pk`, paddée à 32 bytes.

### Composant B — ML-KEM-768

**ML-KEM** = *Module Lattice-based Key Encapsulation Mechanism*,
**FIPS 203** (standardisé août 2024). Ex-CRYSTALS-Kyber.

#### Comment ça marche (vulgarisé)

Famille : **lattice-based**, problème *Module-LWE* (Learning With Errors
sur réseaux modulaires). Conjecturé **dur même pour un ordinateur
quantique**.

```
KeyGen() → (pk, sk)               // pk = 1184 B, sk = 2400 B
Encaps(pk) → (ct, ss)             // ct = 1088 B, ss = 32 B
Decaps(sk, ct) → ss               // même ss = 32 B
```

**KEM ≠ chiffrement** : on ne chiffre pas un message arbitraire,
on **encapsule** un secret aléatoire que les deux côtés vont partager.
C'est exactement ce qu'on veut pour dériver une clé symétrique.

#### Niveau choisi

| Paramètre set  | Sécurité ~              | pk      | ct      | Cas |
|----------------|-------------------------|---------|---------|-----|
| ML-KEM-512     | NIST Cat 1, ~AES-128    | 800 B   | 768 B   | Sous-dimensionné, marge faible |
| **ML-KEM-768** | **NIST Cat 3, ~AES-192**| **1184 B** | **1088 B** | **Notre choix** |
| ML-KEM-1024    | NIST Cat 5, ~AES-256    | 1568 B  | 1568 B  | Overkill, +33% taille |

**Pourquoi ML-KEM-768** : consensus actuel (X-Wing IETF, BSI, sondages
Cloudflare/Google), marge confortable, taille raisonnable. ML-KEM-1024
est gardé pour les cas paranoïa-classifié (renseignement militaire).

#### Niveau de sécurité formel : IND-CCA2

ML-KEM est **IND-CCA2 sécurisé** (indistinguabilité contre attaques à
chiffré choisi adaptatives). Concrètement : même si l'attaquant peut
soumettre des ciphertexts choisis au déchiffreur, il ne peut pas
extraire d'info. C'est le niveau de garantie le plus fort attendu
d'un KEM.

#### Lib (.NET)

BouncyCastle.Cryptography 2.6.2 expose ML-KEM directement dans le
namespace mainline (legacy `Kyber*` a été retiré de cette release) :

```csharp
using Org.BouncyCastle.Crypto.Kems;       // MLKemEncapsulator, MLKemDecapsulator
using Org.BouncyCastle.Crypto.Parameters; // MLKemParameters, MLKem{Public,Private}KeyParameters
using Org.BouncyCastle.Crypto.Generators; // MLKemKeyPairGenerator
```

### Combinaison : HKDF-SHA256

**Mauvaise idée** : `K = SHA256(ss1 ‖ ss2)`. Simple, mais pas de
séparation de domaine, pas de salt, pas de discipline.

**Bonne idée** : **HKDF** (RFC 5869) — HMAC-based Key Derivation Function.
Deux étapes :

```
1. Extract:  PRK = HMAC-SHA256(salt, ss1 ‖ ss2)
             → "lisse" l'entropie en un pseudo-random key
2. Expand:   K   = HMAC-SHA256(PRK, info ‖ counter)
             → étire à la taille voulue, avec séparation de domaine
```

#### Le paramètre `info` (séparation de domaine)

Sert à garantir que les clés dérivées pour des contextes différents
sont *cryptographiquement indépendantes*, même avec les mêmes entrées.

Chez nous : `info = "MCPTx-v1-hybrid"` (sera enrichi avec sender/recipient
/transcript en Phase 1 pour empêcher les cross-protocol attacks).

#### Pourquoi obligatoire (et pas SHA256 brut)

Si demain on dérive aussi une clé HMAC pour authentifier des metadata
hors AEAD, on veut `K_aes ≠ K_hmac` même dérivés du même `(ss1, ss2)`.
HKDF permet ça via des `info` différents. SHA256 brut ne le permet pas.

---

## Couche 3 — Chiffrement des chunks : AES-256-GCM

### AEAD : confidentialité + intégrité en une primitive

**AEAD** = *Authenticated Encryption with Associated Data*. Une seule
primitive donne **à la fois** :

- **Confidentialité** : on ne peut pas lire le clair sans la clé
- **Intégrité** : toute modification du ciphertext est détectée à la
  lecture

Le tag de 16 bytes (128 bits) en sortie de GCM est la "preuve"
d'intégrité. À la lecture, si le tag ne match pas → erreur, on rejette.

### Pourquoi AES-256-GCM (et pas ChaCha20-Poly1305 ou AES-GCM-SIV)

| Algo | Pour | Contre | Décision |
|------|------|--------|----------|
| **AES-256-GCM** | Built-in .NET, AES-NI hardware ubiquitaire, perfs énormes | Sensible au nonce reuse | ✅ **Choix** (nonce dérivé déterministe → reuse impossible) |
| ChaCha20-Poly1305 | Robuste sans AES-NI, plus simple | .NET 8+ uniquement en built-in, AES-NI plus rapide sur nos cibles | Pas nécessaire |
| AES-GCM-SIV | Misuse-resistant (nonce reuse safe) | Pas built-in .NET, perf inférieure | Overkill |

### Découpage systématique en chunks de 16 MiB

**Le fichier n'est jamais chiffré d'un seul tenant.** Il est toujours
découpé en blocs de **16 MiB** (constante `ChunkedAead.DefaultChunkSize`,
voir Phase 1.5 de l'implémentation), même pour un fichier de quelques
octets — il y a alors un unique chunk de petite taille.

Pourquoi un découpage systématique :

| Bénéfice | Détail |
|----------|--------|
| **Streaming** | Le chiffrement consomme l'entrée séquentiellement et yield chaque chunk. Pas de fichier entier chargé en RAM, indispensable pour les transferts multi-Go |
| **Upload parallèle** | Chaque chunk est un objet IPFS indépendant — on peut paralléliser les uploads (et les downloads à la réception) avec un degré de parallélisme configurable |
| **Reprise sur erreur** | Si un upload échoue à mi-parcours, on ne reprend que les chunks manquants au lieu de tout recommencer |
| **Compatibilité IPFS / Pinata** | Reste sous les limites par requête des gateways grand public (typiquement < 25 MiB) |
| **Authentification fine** | Chaque chunk porte son propre tag GCM — la corruption d'un seul octet est isolée et détectée localement |

Pourquoi **16 MiB** précisément (et pas 1 MiB ou 256 MiB) :

- En dessous de quelques MiB, l'overhead par chunk (nonce, tag, requête
  IPFS, entrée dans le manifest) devient relativement important
- Au-dessus de 64 MiB, la parallélisation devient moins utile et la
  consommation mémoire par worker explose
- 16 MiB est le sweet spot retenu par la plupart des systèmes
  similaires (sweet spot empirique, ajustable via la constante)

**Conséquence directe sur la cryptographie** : un index de chunk sur
4 bytes (cf. construction du nonce ci-dessous) suffit à adresser
2³² chunks de 16 MiB, soit **64 GiB par envoi**. Au-delà, on tournerait
la clé — pour le POC on rejette simplement les fichiers > 64 GiB.

### La règle d'or de GCM : **nonce unique par (key, nonce)**

Si on chiffre **deux fois** avec le même `(key, nonce)` :

- Le clair leak (XOR des deux ciphertexts → clair XOR clair)
- Le tag GCM devient forgeable

C'est une **catastrophe cryptographique**. On construit donc le nonce
pour qu'**il soit impossible** d'avoir une collision.

### Schéma de nonce retenu

```
nonce (12 bytes) = nonce_prefix (8 bytes random, par envoi)
                 ‖ chunk_idx    (4 bytes big-endian, 0..N-1)
```

- Le préfixe aléatoire de 8 bytes est tiré une seule fois par envoi
  → probabilité de collision entre deux envois = 2⁻⁶⁴ (négligeable)
- Le `chunk_idx` garantit l'unicité **à l'intérieur** d'un envoi
- Deux limites de capacité distinctes coexistent :
  - **Saturation de l'index de chunk** : sur 4 bytes signés (`int`), max
    2³¹ chunks. À 16 MiB chacun → 2³¹ × 2²⁴ = **2⁵⁵ bytes ≈ 32 PiB**.
    Jamais la contrainte effective.
  - **Sécurité AES-GCM** (NIST SP 800-38D) : recommandation de rotation
    de clé au-delà de **~64 GiB de plaintext par clé**. Comme chaque
    envoi a sa propre clé dérivée par HKDF, le budget est de 64 GiB par
    envoi (≈ 4 096 chunks de 16 MiB). C'est **la** contrainte qui s'applique.
- Au-delà de 64 GiB : `EnvelopeWriter.SendAsync` rejette explicitement
  (`InvalidOperationException`). Cap documenté, pas de rotation de clé
  intra-envoi en POC. Pour transférer plus, splitter en plusieurs envois.

### Pourquoi pas un nonce purement aléatoire 12 bytes

NIST SP 800-38D dit : **max ~2³² messages aléatoires** avec la même
clé avant proba de collision non-négligeable (paradoxe des anniversaires
sur 96 bits). Notre construction est plus stricte et déterministe —
donc plus sûre.

---

## Couche 4 — Signature du manifest : hybride ECDSA secp256k1 + ML-DSA-65

> **Mise à jour (Phase 5)** : la signature du manifest est désormais
> **hybride** — classique **ECDSA secp256k1** + post-quantique
> **ML-DSA-65** (FIPS 204, ex-Dilithium). Un manifest reste authentifiable
> même face à un adversaire quantique futur capable de casser ECDSA.

### Quoi signer

Le **manifest entier** (JSON canonicalisé) qui contient :

- Identités sender/recipient
- Liste des CIDs des chunks + leurs tags GCM
- Suite cryptographique utilisée (`Hybrid-secp256k1+MLKEM768-AES256GCM`)
- Toutes les valeurs publiques (`eph_pk`, `kem_ct`, `nonce_prefix`)

**Ce que ça protège** : un tiers ne peut pas republier le CID d'un envoi
en se faisant passer pour sender. Et le recipient peut prouver à un
tiers (auditeur, juge) qui a envoyé quoi.

### Construction hybride et binding

Le `SignedManifest` embarque **deux** pubkeys (secp256k1 + ML-DSA) et
**deux** signatures :

```
ecdsa_sig  = ECDSA-secp256k1.Sign( Keccak256( manifest_bytes ‖ mldsa_pubkey ) )
mldsa_sig  = ML-DSA-65.Sign( manifest_bytes )
```

Vérification (les trois doivent passer) :

1. `secp256k1_pubkey` dérive vers `manifest.sender` (ancre d'identité) ;
2. `ecdsa_sig` vérifie sur `Keccak256(manifest ‖ mldsa_pubkey)` — l'adresse
   **vouche pour la pubkey ML-DSA** (binding) ;
3. `mldsa_sig` vérifie sur `manifest_bytes` — authenticité **post-quantique**
   du contenu.

La pubkey ML-DSA n'est **pas** publiée on-chain : elle voyage dans le
manifest, et l'ECDSA ancrée à l'adresse la lie. Donc aucun changement de
contrat `KeyRegistry`.

### ⚠️ Plafond inhérent du binding

Tant que l'identité **est** une adresse Ethereum (ECDSA), le *lien*
clé-PQC ↔ identité reste **classiquement sécurisé** : pour substituer la
pubkey ML-DSA, il faut forger l'ECDSA (étape 2) — possible pour un
adversaire quantique, mais à ce stade tout le modèle d'identité ECDSA est
déjà tombé. **ML-DSA rend l'authenticité du *contenu* du manifest
post-quantique, étant donné un binding de confiance ; il ne rend pas le
lien clé→identité post-quantique.** Le faire exigerait une identité
PQC-native (hors EVM) — hors scope.

ML-KEM/ML-DSA tailles : pubkey ML-DSA-65 = 1952 B, signature = 3309 B
(≈ +5,3 KB par manifest, négligeable face aux chunks).

### Pourquoi ECDSA secp256k1 et pas Schnorr/EdDSA

EdDSA (Ed25519) serait *techniquement supérieur* :

- Pas de malléabilité (ECDSA en a)
- Déterministe (pas besoin d'aléa pendant la signature → moins de risque
  d'implémentation erronée)
- Plus rapide

Mais **on signe aussi des transactions on-chain** (l'event `FileSent`
du smart contract). EVM force ECDSA secp256k1. Avoir **une seule paire
de clés** pour les deux usages = plus simple, moins de surface d'attaque.

Trade-off accepté.

### Hash pour signer : Keccak-256

Pas SHA-256 — on prend **Keccak-256** (la variante pré-standardisation
utilisée par Ethereum). Ainsi :

- Le `content_hash` qu'on met on-chain peut être vérifié *par le
  contrat lui-même* si besoin un jour (Keccak est natif EVM via
  l'opcode `KECCAK256`)
- Cohérence : une seule famille de hash dans tout le système on-chain

(En interne, HKDF utilise SHA-256, mais c'est un détail de la KDF,
pas observable de l'extérieur.)

---

## Couche 5 — Hashes : Keccak-256

| Usage | Algo | Pourquoi |
|-------|------|----------|
| `content_hash` on-chain | Keccak-256 | Vérifiable on-chain via opcode `KECCAK256` |
| Hash signé par ECDSA | Keccak-256 | Cohérence avec l'écosystème Ethereum |
| KDF interne (HKDF) | SHA-256 | Standard RFC 5869, perf, support natif .NET |
| Tag d'intégrité par chunk | GHASH (interne à GCM) | Imposé par AES-GCM |

---

## Threat model

### On protège contre

| Menace | Mécanisme |
|--------|-----------|
| Lecture du contenu par IPFS, gateways, fournisseur stockage | AES-256-GCM, clé jamais transmise en clair |
| Modification silencieuse d'un chunk | Tag GCM par chunk |
| Réordering / suppression de chunks | Manifest signé liste l'ordre et le compte |
| Usurpation d'expéditeur | Signature ECDSA + adresse on-chain dérivée de la même clé |
| Compromission future de secp256k1 (quantum) | ML-KEM-768 tient → contenu reste secret |
| Compromission future de ML-KEM (faille découverte) | ECDH secp256k1 tient → contenu reste secret |
| Fuite long-terme de la clé du destinataire | Forward secrecy via ECDHE — les anciens envois restent secrets |
| Replay du même envoi par un tiers | Signature liée au manifest qui inclut timestamp/transcript |

### On ne protège **pas** contre

| Limite | Pourquoi (POC) |
|--------|---------------|
| Graphe social (qui envoie à qui, quand, combien) | Tx on-chain publiques. Résolution = mixing/relayers, **v2** |
| **Signatures** post-quantum | ECDSA secp256k1 reste classique. Un adversaire quantique futur peut forger des signatures → compromet l'audit trail ex-post. **v2 = hybride ML-DSA-65** |
| Compromission de la machine de l'agent | Clés privées en clair sur disque (POC). Solution = HSM / TPM / Secure Enclave |
| Disponibilité des chunks sur IPFS | Si plus personne ne pin, le contenu disparaît. Pas une question de crypto |
| Cohérence du KeyRegistry on-chain | On fait confiance au contrat. Auto-publication via `msg.sender` empêche l'usurpation mais pas l'erreur de saisie |
| **RPC menteur** (résolution de handle, lookup de clés, inbox) | On fait confiance à l'endpoint RPC. Détail complet ci-dessous — c'est la limite la plus importante du modèle on-chain |

### Frontières de confiance on-chain (Phase 2/3)

Le client CLI lit la chain via **un seul endpoint RPC**. Tout ce que le RPC
retourne est cru sur parole. Quatre conséquences à acter explicitement —
détectées au code review, non couvertes par les défenses cryptographiques :

#### 1. Un RPC menteur peut rediriger un envoi vers un attaquant

`mcptx send fichier --to alice-ai` résout le handle `alice-ai` en adresse
via **un seul `eth_call`** à `AgentDirectory`. Si le RPC (compromis, ou MITM
réseau) retourne `attackerAddr` pour ce handle **et** sert les clés de
l'attaquant, le contrôle client-side `recipientPublic.Address == recipient`
**passe** (la pubkey de l'attaquant dérive bien vers son adresse). Le fichier
est alors chiffré **pour l'attaquant**. Perte de confidentialité totale.

> **Mitigation opérateur** : utiliser un RPC de confiance ; ou épingler
> l'adresse du destinataire hors-bande (`--to 0xADDR` au lieu du handle) ;
> ou recouper la résolution via plusieurs RPC. **v2** : multi-RPC quorum,
> ou ancrage de l'annuaire via une preuve de Merkle vérifiable.

#### 2. La pubkey ML-KEM liée au destinataire — ✅ RÉSOLU (v2)

> **Statut : corrigé en code.** La pubkey ML-KEM du destinataire est
> désormais **liée dans le contexte HKDF** (`EnvelopeContext.BuildHkdfContext`
> bind `sender ‖ recipient ‖ recipient_mlkem_pubkey ‖ nonce_prefix`). Une clé
> ML-KEM substituée change donc **déterministiquement** la clé AES dérivée :
> ce qui n'était sûr que *par accident* de la construction hybride est
> maintenant une **garantie par design** (la dérivation échoue closed même si
> un futur refactor retirait la patte ECDH du KDF). ⚠️ Changement de
> dérivation → incompatible avec les enveloppes créées avant (OK en pré-release).

Contexte historique (avant le fix) : seule la clé secp256k1 était vérifiée
(elle dérive vers l'adresse) ; la clé ML-KEM, non dérivable d'une adresse,
n'était liée à rien — un RPC menteur pouvait la substituer. La sécurité tenait
au fait que la patte ECDH (nécessitant la clé privée secp256k1 réelle du
destinataire) faisait diverger la clé finale. Ce filet est désormais explicite.

Reste pour une v(n+1) : exiger une preuve de possession de la clé ML-KEM à la
publication (`KeyRegistry`), pour fermer aussi le canal "DoS par clé bidon".

#### 3. Le `content_hash` on-chain vérifié à la réception — ✅ RÉSOLU (v2)

> **Statut : corrigé en code.** `EnvelopeReader.Receive*Async` accepte un
> `expectedContentHash` optionnel : s'il est fourni, le `Keccak256` du manifest
> récupéré doit y correspondre (comparaison `FixedTimeEquals`) **avant** tout
> déchiffrement, sinon la réception est refusée. `mcptx inbox` affiche
> désormais le `content_hash` de chaque event `FileSent` ; `mcptx receive
> --expect-hash 0x…` (et le tool MCP `receive_file`, paramètre `expect_hash`)
> le vérifient. L'ancre on-chain n'est plus décorative : elle relie les octets
> livrés à l'enregistrement on-chain, ce qui défait un manifest substitué
> (mais validement signé par un tiers) servi au même CID par un backend non
> content-addressed.

Note : sans `--expect-hash`, `receive` procède sur la seule signature du
manifest (et l'avertit explicitement). Le check est opt-in mais le flux
naturel `inbox → receive` fournit le hash.

#### 4. `receive` présente l'expéditeur comme authentique sans recoupement

`mcptx receive <cid>` affiche `from: 0x…` avec un ✓ vert. La signature du
manifest prouve que *cette adresse a bien signé ce manifest* — mais **rien**
ne relie ce CID à un event `FileSent` on-chain, ni ne vérifie que
l'expéditeur est celui attendu par l'utilisateur. Un attaquant peut forger un
manifest valide signé par sa propre identité et fournir le CID hors-bande ;
l'utilisateur voit une adresse d'apparence autoritaire.

> **v2** : `receive` devrait recouper le CID avec l'event `FileSent` (qui
> nomme l'expéditeur via `msg.sender`, non spoofable), reverse-resolver
> l'adresse en handle, et avertir si l'expéditeur n'est pas vérifié.

### Note sur les signatures post-quantum — ✅ RÉSOLU (Phase 5)

> **Statut : corrigé en code.** Le manifest est désormais **co-signé**
> ECDSA secp256k1 **+ ML-DSA-65** (cf. Couche 4). Les deux signatures
> doivent vérifier. Le contenu du manifest est donc authentifié de façon
> post-quantique.

Historiquement on gardait l'ECDSA seul pour v1 car la confidentialité
("harvest now, decrypt later") était la menace **urgente** (déjà couverte
par le KEM hybride), tandis qu'une signature forgée nécessite l'ordinateur
quantique *au moment de la forge*. Phase 5 ferme néanmoins ce maillon.

**Plafond restant** (cf. Couche 4 → « Plafond inhérent du binding ») : le
*lien* clé-PQC ↔ identité reste ancré ECDSA tant que l'identité est une
adresse Ethereum. Lever ce plafond = identité PQC-native, hors EVM, hors
scope POC.

---

## Flux complet bout-en-bout

```
SENDER (Alice)                                       RECIPIENT (Bob)
═══════════════                                      ═══════════════════
file → split en chunks de 16 MiB

eph_sk, eph_pk = secp256k1.gen()                     [reçoit manifest]
ss1 = ECDH(eph_sk, bob.secp256k1_pk)                 ss1 = ECDH(bob.sk, eph_pk)

(kem_ct, ss2) = ML-KEM768.Encaps(bob.mlkem_pk)       ss2 = ML-KEM768.Decaps(bob.mlkem_sk, kem_ct)

K = HKDF-SHA256(ss1‖ss2, "MCPTx-v1-hybrid", 32B)     K = HKDF-SHA256(ss1‖ss2, "MCPTx-v1-hybrid", 32B)

nonce_prefix = random(8 bytes)
∀ chunk i:
  nonce_i = nonce_prefix ‖ BE(i, 4)
  (ct_i, tag_i) = AES-256-GCM.Encrypt(K, nonce_i, chunk_i)
  cid_i = IPFS.upload(ct_i)

manifest = { suite, sender, recipient, eph_pk,
             kem_ct, nonce_prefix,
             chunks=[{i, cid_i, tag_i}], ... }
sig = ECDSA-secp256k1.Sign(alice.sk, Keccak256(manifest))
cid_manifest = IPFS.upload(manifest ‖ sig)
                                                     [récupère cid_manifest depuis chain event]
contract.FileRegistry.send(bob, cid_manifest,        manifest, sig ← IPFS.download(cid_manifest)
                            Keccak256(manifest))     verify ECDSA(alice.pk, Keccak256(manifest), sig)

                                                     ∀ chunk i:
                                                       ct_i ← IPFS.download(cid_i)
                                                       nonce_i = nonce_prefix ‖ BE(i, 4)
                                                       chunk_i = AES-256-GCM.Decrypt(K, nonce_i, ct_i, tag_i)
                                                     concat → file ✓
```

---

## Stockage des clés : chiffrement at-rest et zeroization

### Fichier d'identité chiffré (v3, opt-in)

Par défaut `~/.mcptx/identity.json` est en clair (v2, mode `0600` sur POSIX).
Si la variable d'environnement **`MCPTX_PASSPHRASE`** est définie au moment
du `keygen`, le fichier est écrit chiffré (v3) ; la même variable est lue à
chaque chargement (CLI et serveur MCP).

Construction :

```
clé_AES   = Argon2id(passphrase, salt 16B aléatoire ; m=19456 KiB, t=2, p=1)   // baseline OWASP, RFC 9106 v1.3
enveloppe = AES-256-GCM(clé_AES, nonce 12B aléatoire,
                        plaintext = JSON v2,
                        aad = "mcptx-identity-v3|argon2id|salt|m|t|p")
```

Les coûts Argon2 utilisés sont stockés dans l'en-tête du fichier et **liés
dans l'AAD** : altérer le salt ou les coûts (pour affaiblir le KDF) fait
échouer l'authentification GCM exactement comme une mauvaise passphrase —
les deux cas sont indistinguables par design.

Limites : la passphrase transite par une variable d'environnement (visible
de l'utilisateur courant) ; un keyring OS / TPM reste la cible production.

### Zeroization (best-effort)

`Secp256k1KeyPair`, `MlKemKeyPair`, `MlDsaKeyPair` et `AgentIdentity`
implémentent `IDisposable` : `Dispose()` met à zéro les encodages de clés
privées mis en cache. Les buffers intermédiaires (clé AES dérivée, payload
v2 déchiffré, bytes du fichier, décodages base64, passphrase UTF-8, clé de
données HKDF côté enveloppe) sont zéroisés après usage.

**Honnêteté sur ce que ça NE couvre PAS** — en .NET managé :
- BouncyCastle conserve ses propres copies internes (paramètres ML-KEM /
  ML-DSA, scalaire `BigInteger` secp256k1) qui ne sont pas zéroisables ;
- le GC peut déplacer/copier les tableaux avant leur mise à zéro ;
- les `string` .NET (passphrase, hex JSON) sont immuables et non-zéroisables.

C'est donc une **réduction de fenêtre d'exposition**, pas une garantie. Le
serveur MCP (processus long-vivant) dispose l'identité à l'arrêt ; les
commandes CLI s'appuient sur la fin du processus. Une garantie forte
demanderait des buffers natifs épinglés ou un secret-store OS — hors scope
POC.

---

## Références

### Standards

- **FIPS 203** — Module-Lattice-Based Key-Encapsulation Mechanism Standard (ML-KEM), NIST, août 2024
- **FIPS 204** — Module-Lattice-Based Digital Signature Standard (ML-DSA), NIST, août 2024 *(v2)*
- **NIST SP 800-38D** — Galois/Counter Mode of Operation (GCM)
- **NIST SP 800-227** *(draft)* — Recommendations for KEMs and Hybrid Constructions
- **RFC 5869** — HMAC-based Extract-and-Expand Key Derivation Function (HKDF)
- **RFC 6979** — Deterministic Usage of DSA and ECDSA *(implementation hint)*
- **SEC 2** — Recommended Elliptic Curve Domain Parameters (secp256k1)

### Drafts / consensus PQC hybride

- **IETF X-Wing** — Hybrid KEM combining X25519 and ML-KEM-768
  *(modèle structurel de notre construction, adapté à secp256k1)*
- **BSI TR-02102-1** — Cryptographic Mechanisms (recommandations PQC hybride)
- **Cloudflare** *Post-quantum readiness* blog series
