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
- [Références](#références)

---

## Vue d'ensemble

Cinq couches indépendantes, chacune avec un rôle précis :

```
┌─────────────────────────────────────────────────────────────┐
│  IDENTITÉ        │  secp256k1 keypair                       │
│                  │  → adresse Ethereum (= identifiant)      │
├──────────────────┼──────────────────────────────────────────┤
│  KEM HYBRIDE     │  ECDHE secp256k1  ⊕  ML-KEM-768          │
│                  │  ↓                                       │
│                  │  HKDF-SHA256(ss1 ‖ ss2 ‖ info)           │
│                  │  ↓                                       │
│                  │  clé symétrique 256-bit K                │
├──────────────────┼──────────────────────────────────────────┤
│  CHIFFREMENT     │  AES-256-GCM par chunk (16 MiB)          │
│                  │  nonce = 8B random ‖ 4B chunk_idx        │
├──────────────────┼──────────────────────────────────────────┤
│  SIGNATURE       │  ECDSA secp256k1 sur Keccak-256(manifest)│
├──────────────────┼──────────────────────────────────────────┤
│  HASHES          │  Keccak-256 (cohérence EVM)              │
└─────────────────────────────────────────────────────────────┘
```

**Suite identifier officielle** : `Hybrid-secp256k1+MLKEM768-AES256GCM`.
Versionnée dans chaque manifest pour permettre la migration future
(notamment vers une signature hybride avec ML-DSA-65).

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
- Capacité : 2³² chunks × 16 MiB = **64 GiB par envoi** avant de devoir
  tourner la clé. Au-delà → on rejette le fichier

### Pourquoi pas un nonce purement aléatoire 12 bytes

NIST SP 800-38D dit : **max ~2³² messages aléatoires** avec la même
clé avant proba de collision non-négligeable (paradoxe des anniversaires
sur 96 bits). Notre construction est plus stricte et déterministe —
donc plus sûre.

---

## Couche 4 — Signature du manifest : ECDSA secp256k1

### Quoi signer

Le **manifest entier** (JSON canonicalisé) qui contient :

- Identités sender/recipient
- Liste des CIDs des chunks + leurs tags GCM
- Suite cryptographique utilisée (`Hybrid-secp256k1+MLKEM768-AES256GCM`)
- Toutes les valeurs publiques (`eph_pk`, `kem_ct`, `nonce_prefix`)

**Ce que ça protège** : un tiers ne peut pas republier le CID d'un envoi
en se faisant passer pour sender. Et le recipient peut prouver à un
tiers (auditeur, juge) qui a envoyé quoi.

### Pourquoi ECDSA secp256k1 et pas Schnorr/EdDSA

EdDSA (Ed25519) serait *techniquement supérieur* :

- Pas de malléabilité (ECDSA en a)
- Déterministe (pas besoin d'aléa pendant la signature → moins de risque
  d'implémentation foireuse)
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

### Note sur les signatures post-quantum

C'est la principale limite consciente.

**v2** ajoutera **ML-DSA-65** (FIPS 204, ex-Dilithium) en signature
hybride : signer deux fois (ECDSA + ML-DSA), valider les deux.
Coût : +3.3 KB par manifest, négligeable.

On garde l'ECDSA seul pour v1 parce que :

1. La confidentialité ("harvest now, decrypt later") est la menace
   **urgente** — un attaquant peut stocker aujourd'hui. Les signatures
   forgées dans 20 ans pour réécrire l'historique, c'est un problème
   mais moins prioritaire.
2. Le manifest a un champ `suite` versionné → migration v2 facile.

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
