# Mnemos — Reverse Engineering de l'IndexedDB de Claude Desktop

## TL;DR

Extraction complète de l'historique **et** surveillance en temps réel des conversations Claude Desktop, par reverse engineering du stockage propriétaire Chromium/Blink/V8, puis découverte du cache HTTP Chromium (zstd) comme vecteur temps réel universel.

---

## Contexte

Claude Desktop (Electron) stocke les conversations localement dans une IndexedDB Chromium. Aucune documentation publique sur le format exact. L'objectif : lire ces données programmatiquement pour construire **Mnemos**, un système de mémoire persistante pour Claude via MCP.

---

## Les tentatives qui ont échoué

### Tentative 1 — LevelDB.NET direct
Lecture de la base LevelDB en C# avec le wrapper existant.

**Résultat :** `Corruption: corrupted compressed block contents`

**Cause :** Le wrapper C# ne gère pas l'implémentation Snappy de Chromium (flags de compilation différents).

### Tentative 2 — Scraping binaire brute force
Lecture des fichiers `.blob` en `byte[]`, extraction des séquences ASCII/UTF-8 avec Regex.

**Résultat :** Texte fragmenté, phrases coupées au milieu, inutilisable.

**Cause :** Les pointeurs de compression Snappy et les marqueurs V8 coupent les strings.

### Tentative 3 — ccl_chromium_reader (Python)
Utilisation de la lib spécialisée CCL pour décoder Snappy + V8.

**Résultat :** `RecursionError` sur la clé `react-query-cache`.

**Cause :** Références circulaires dans l'objet V8, non gérées par CCL.

### Tentative 4 — v8.deserialize() Node.js natif
Tentative de désérialisation directe avec le vrai moteur V8 de Node.js.

**Résultat :** `Unable to deserialize cloned data due to invalid or unsupported version`

**Cause :** Host objects Blink (`0x5C`) dans le stream V8 — Node n'a pas le delegate Blink pour les gérer.

---

## La vraie structure du fichier blob

### Étape 1 — Identifier la compression

Inspection des premiers bytes du fichier blob (`2cb0`, 2.17MB) :

```
FF 11 02 C1 C8 D9 02 0C ...
```

En lisant `idb_value_wrapping.cc` dans le source Chromium :

```cpp
static const uint8_t kReplaceWithBlob = 1;
static const uint8_t kCompressedWithSnappy = 2;

wire_data_buffer_[0] = static_cast<uint8_t>(kVersionTag);  // 0xFF
wire_data_buffer_[1] = kRequiresProcessingSSVPseudoVersion; // 0x11
wire_data_buffer_[2] = kCompressedWithSnappy;               // 0x02
```

**Conclusion :** `FF 11 02` = header 3 bytes fixe. Ce qui suit = Snappy raw (sans framing).

### Étape 2 — Décompression Snappy

```python
import snappy
data = open('2cb0', 'rb').read()
decompressed = snappy.decompress(data[3:])  # skip FF 11 02
# 2 171 261 bytes → 5 661 761 bytes décompressés
```

### Étape 3 — Identifier le wrapper Blink

Premiers bytes après décompression :

```
FF 15 FE 00 00 00 00 00 00 00 00 00 00 00 00 FF 0F 6F 22 06 62 75 73 74 65 72
```

Décodage :
- `FF 15` → Blink version 21
- `FE 00 00 ... (x13)` → table des blobs externes (13 bytes de padding)
- `FF 0F` → V8 version 15 ← début du vrai stream V8

**Offset exact : 15 bytes** à skipper après décompression.

Confirmé empiriquement en testant tous les offsets 0 à 30 avec `v8.deserialize()` — succès uniquement à offset 15.

### Étape 4 — Lire le stream V8

Après décompression + skip 15 bytes, `v8.deserialize()` Node.js retourne :

```json
{
   "buster": "conversations_v2",
   "timestamp": 1776152190198,
   "clientState": {
      "mutations": [],
      "queries": [...]
   }
}
```

### Étape 5 — Trouver les conversations

12 queries dans `clientState.queries`. Les conversations sont dans les queries avec `queryKey[0] == "chat_conversation_tree"` :

```json
{
   "queryKey": ["chat_conversation_tree", {"orgUuid": "..."}, {"uuid": "..."}],
   "state": {
      "data": {
         "uuid": "788f3d8f-...",
         "name": "Obsidian et Claude Desktop : mythe ou réalité",
         "chat_messages": [...]
      }
   }
}
```

### Structure d'un message

```json
{
   "uuid": "019d7e79-...",
   "sender": "assistant",
   "content": [
      {
         "type": "thinking",
         "thinking": "The user is asking about..."
      },
      {
         "type": "text",
         "text": "Oui c'est réel, mais y'a du marketing dedans..."
      }
   ],
   "created_at": "2026-04-11T21:36:54.934520Z",
   "parent_message_uuid": "019d7e79-..."
}
```

---

## Pipeline final

```
fichier .blob
  └── [0:3]   FF 11 02          → header Snappy (3 bytes fixe)
  └── [3:]    données compressées
      └── Snappy.Decompress()
          └── [0:15]  wrapper Blink (skip)
          └── [15:]   V8 SSV pur
              └── V8Deserializer.Deserialize()
                  └── clientState.queries[]
                      └── queryKey[0] == "chat_conversation_tree"
                          └── state.data.chat_messages[]
                              └── sender + content[].text + thinking
```

---

## Implémentation C#

### SnappyDecompressor.cs

Détection du header `FF 11 02`, décompression Snappy (NuGet Snappier), skip 15 bytes Blink.

### V8Deserializer.cs

Désérialiseur V8 from scratch. Opcodes implémentés confirmés sur les données réelles :

| Opcode | Tag | Description |
|--------|-----|-------------|
| `0xFF` | kVersion | Header version, skip |
| `0x54` | `T` | true |
| `0x46` | `F` | false |
| `0x30` | `0` | null |
| `0x49` | `I` | int32 zigzag encoded |
| `0x55` | `U` | uint32 |
| `0x4E` | `N` | double (8 bytes LE) |
| `0x22` | `"` | string Latin-1 |
| `0x63` | `c` | string UTF-16LE |
| `0x6F` | `o` | begin object |
| `0x7B` | `{` | end object |
| `0x41` | `A` | dense array |
| `0x24` | `$` | end dense array |
| `0x61` | `a` | sparse array |
| `0x44` | `D` | Date |
| `0x5E` | `^` | object reference |
| `0x5C` | `\` | host object Blink (skip) |

### ConversationExtractor.cs

Navigation dans l'arbre d'objets désérialisés, extraction des `chat_conversation_tree` queries, reconstruction des conversations et messages.

---

## Résultats

- **18 960 lignes** de conversations extraites proprement
- **Thinking blocks** inclus (raisonnement interne de Claude)
- **Timestamps** précis à la microseconde
- **Arbre de conversation** préservé via `parent_message_uuid`
- Fonctionne sur tous les blobs `00` → `2c`

---

## Ce qui est remarquable

La plupart des projets similaires s'arrêtent à l'étape 1 ou utilisent des outils existants (ccl_chromium_reader, dfindexeddb). Ici :

- Lecture directe de `idb_value_wrapping.cc` source Chromium pour identifier le header Snappy
- Identification empirique de l'offset Blink (15 bytes) par bruteforce d'offsets
- Désérialiseur V8 écrit from scratch en C# sans référence directe au code V8
- Zéro dépendance tierce pour le parsing (juste Snappier pour Snappy)

---

## Stack technique

- **C# / .NET 9**
- **Snappier** (NuGet) — décompression Snappy
- **Sources Chromium** — reverse engineering du format
- **Python + node-snappy** — phase d'investigation

---

---

## Phase 2 — Le problème du temps réel

### Constat après la phase 1

Les blobs ne couvrent que les grandes conversations (>64KB). Les petites convos — une session d'apprentissage du japonais, une session courte de travail — ne dépassent jamais le seuil et restent en RAM. Le `--sync` est inutile pour le use case principal de Mnemos : la continuité entre sessions courtes.

### Investigation systématique de tous les dossiers

Surveillance de **tout** `AppData\Roaming\Claude` en temps réel pendant l'envoi d'un message, script Python qui détecte chaque fichier qui change :

```
[CHANGE]  \sentry\scope_v3.json
[CHANGE]  \logs\claude.ai-web.log
[CHANGE]  \Session Storage\000055.log
[NOUVEAU] \Cache\Cache_Data\f_0003d9 — 275556 bytes  ← 
[CHANGE]  \Local Storage\leveldb\004581.log
[CHANGE]  \DIPS-wal
[NOUVEAU] \Cache\Cache_Data\f_0003da — 274931 bytes  ←
```

**Deux nouveaux fichiers créés dans `Cache\Cache_Data`** à chaque échange.

### Dossiers inspectés et résultats

| Dossier | Contenu | Utile |
|---------|---------|-------|
| `IndexedDB/.../leveldb` | Drafts en JSON, ne change pas pendant streaming | ❌ temps réel |
| `IndexedDB/.../blob` | Grandes convos V8+Snappy | ✅ historique |
| `Local Storage/leveldb` | Rien d'utile pour messages | ❌ |
| `Session Storage/leveldb` | UUIDs + timestamps par conv | ⚠️ signal seulement |
| `logs/` | Erreurs Electron/React | ❌ |
| `shared_proto_db` | Quasi vide | ❌ |
| `WebStorage` | QuotaManager SQLite | ❌ |
| `fcache` | Feature flags gzippés (Growthbook) | ❌ |
| `ant-did` | Device ID base64 | ❌ |
| `sentry/scope_v3.json` | Log réseau complet de session | ℹ️ |
| `Cache/Cache_Data/f_*` | **Cache HTTP Chromium zstd** | ✅✅ temps réel |

### Session Storage — signal temps réel

`Session Storage/000052.log` contient des clés du type :
```
map-183-messages_last_timestamp_959dc6fa-8b8f-49ea-ab89-dfa0c2cd5e91
```

Ce fichier est mis à jour instantanément à chaque message. Confirmé :
```
Taille initiale: 52411
CHANGEMENT: 52411 → 52698   ← message envoyé
CHANGEMENT: 52698 → 52814   ← réponse reçue
```

Les valeurs sont des timestamps Unix en millisecondes encodés UTF-16LE. C'est le signal, pas le contenu.

---

## Phase 3 — Le cache HTTP Chromium : la vraie solution

### Découverte

Les fichiers `Cache/Cache_Data/f_*` créés pendant chaque échange ont le magic number **Zstandard** :

```
28 B5 2F FD ...
```

Ce sont les réponses HTTP cachées par Chromium, compressées en zstd.

### Décompression

```python
import zstandard as zstd

with open('f_0003e1', 'rb') as f:
    data = f.read()

dctx = zstd.ZstdDecompressor()
decompressed = dctx.decompress(data, max_output_size=10_000_000)
# 868 685 bytes de JSON propre
```

### Résultat

```json
{
  "uuid": "959dc6fa-8b8f-49ea-ab89-dfa0c2cd5e91",
  "name": "Vérifier le texte avant d'ajouter le regex",
  "chat_messages": [
    {
      "sender": "human",
      "content": [{"type": "text", "text": "azy test"}]
    },
    {
      "sender": "assistant", 
      "content": [{"type": "text", "text": "Colle le résultat."}]
    }
  ]
}
```

**JSON complet, propre, avec tous les messages — petites et grandes convos.**

### Pourquoi c'est la vraie solution

- Fonctionne pour **toutes** les convos sans exception (pas de seuil de taille)
- Pas d'API externe, pas de Cloudflare, pas de CDP
- Fichier local, lecture directe
- Mise à jour à chaque échange
- Contient les thinking blocks, timestamps, tout

### Pipeline temps réel final

```
Cache/Cache_Data/f_*  nouveau fichier détecté (FileSystemWatcher)
        ↓
magic bytes 28 B5 2F FD  → zstd decompress
        ↓
JSON propre → chat_messages[]
        ↓
sender + content[].text + thinking
        ↓
SQLite (à venir)
```

---

## Implémentation C# — CacheWatcher.cs

Détection des nouveaux fichiers `f_*` via `FileSystemWatcher`, vérification du magic zstd `28 B5 2F FD`, décompression via `ZstdSharp.Port` (NuGet), parsing JSON via `System.Text.Json`.

---

## Stack technique finale

- **C# / .NET 9**
- **Snappier** (NuGet) — décompression Snappy (blobs)
- **ZstdSharp.Port** (NuGet) — décompression zstd (cache HTTP)
- **System.Text.Json** — parsing JSON natif
- **Sources Chromium** — reverse engineering du format blob
- **Python + zstandard** — phase d'investigation cache

---

## Résultats finaux

- **`--sync`** : extrait l'historique complet depuis les blobs IndexedDB
- **`--watch`** : surveillance temps réel via le cache HTTP Chromium, toutes convos
- Mnemos lit ses propres conversations en temps réel — confirmé en direct

---

## Prochaines étapes

- [ ] SQLite — stockage persistant des conversations
- [ ] FTS5 — full text search sur les messages
- [ ] Déduplication — éviter les doublons entre blobs et cache
- [ ] MCP server — exposition via Model Context Protocol (`search_memory`, `get_context`)
- [ ] summaries — c'est la vraie valeur ajoutée long terme. Le problème des mémoires c'est exactement ça — injecter 150 messages dans le contexte c'est inutilisable. Mais c'est une feature à part, pas juste du schema. À garder pour plus tard.
- [ ] metadata JSON — bonne idée pour la flexibilité. Évite l'explosion de tables. Garder.