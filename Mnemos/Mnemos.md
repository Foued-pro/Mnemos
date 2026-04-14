# Mnemos — Documentation Technique du Projet

> Memory layer persistant pour Claude Desktop sur Windows

---

## 1. Contexte et Problème

### Le problème fondamental

Claude Desktop n'a aucune mémoire entre les sessions. Chaque nouvelle conversation repart de zéro. Si tu as passé 3h à déboguer un problème la semaine dernière, Claude l'ignore totalement aujourd'hui.

### L'objectif de Mnemos

Capturer automatiquement chaque échange avec Claude Desktop, les stocker localement, et les réinjecter intelligemment via le protocole MCP quand c'est pertinent. Zéro action manuelle. 100% local. 100% privé.

```
Tu tapes un prompt
→ Mnemos capture l'échange en arrière-plan
→ Tu demandes "c'était quoi le code d'hier ?"
→ Claude appelle Mnemos via MCP
→ Mnemos retrouve le contexte dans SQLite
→ Claude te répond avec la vraie information
```

---

## 2. Choix du Langage : C# / .NET 9

### Pourquoi pas les autres ?

**Go** — Pas de SDK MCP officiel. Il faudrait implémenter le protocole JSON-RPC à la main, ce qui représente une semaine de travail pour quelque chose qui existe déjà en C#.

**Python** — SDK MCP officiel disponible mais empreinte mémoire élevée (~150-200MB avec les libs chargées) pour un process qui tourne H24 en background. Démarrage lent (~200ms). Pas adapté pour un service système Windows.

**TypeScript/Bun** — SDK MCP officiel, bonne option. Mais C# est supérieur sur Windows pour l'intégration système, la gestion des services natifs, et l'accès aux APIs Windows (FileSystemWatcher, FileShare).

**Rust** — Overkill. Pas de SDK MCP officiel. Gain marginal pour ce use case.

**Java/Kotlin** — JVM overhead absurde pour un process background. ~300MB de RAM au démarrage.

**C#** — Le meilleur choix pour ce projet spécifiquement parce que :

- SDK MCP officiel v1.0, maintenu conjointement par Microsoft et Anthropic, sorti en mars 2026
- `FileShare.ReadWrite` : lit le LevelDB pendant que Claude écrit dedans, sans copie shadow et sans lock
- `FileSystemWatcher` : intégration native Windows pour détecter les changements en temps réel
- `Microsoft.Data.Sqlite` : SQLite natif, performant, zéro overhead
- `BackgroundService` + `UseWindowsService()` : service Windows natif en 3 lignes
- Single binary via `dotnet publish` avec Native AOT : ~10-20MB de RAM en idle
- Démarrage < 50ms

### Pourquoi .NET 9 et pas .NET 10 ?

.NET 10 est en preview au moment du développement (SDK 10.0.201 non stable). .NET 9 est la version LTS stable avec support jusqu'en mai 2026.

### IDE : JetBrains Rider

Rider est utilisé car il offre une meilleure expérience C# que VS Code sur Windows, avec un debugger natif, une indexation plus rapide et un support complet de .NET 9. Installé et configuré sur la machine.

---

## 3. Comment les Données sont Capturées

### Investigation du LevelDB (résultat de nos tests)

Claude Desktop stocke ses données dans IndexedDB via Chromium :

```
%APPDATA%\Claude\IndexedDB\https_claude.ai_0.indexeddb.leveldb\
├── 000103.log        ← WAL actif (Write-Ahead Log)
├── 000104.ldb        ← données compactées
├── MANIFEST-000001
├── CURRENT
└── LOCK              ← verrou Chromium (ne jamais copier)
```

**Ce qu'on a découvert par test direct :**

Le LevelDB stocke les données sous deux formats :

**Format 1 — Drafts TipTap (confirmé)**
Chaque frappe au clavier est enregistrée en temps réel sous la clé `p5store:chat-draft:<uuid>` :

```json
{
  "state": {
    "tipTapEditorState": {
      "type": "doc",
      "content": [{
        "type": "paragraph",
        "content": [{"type": "text", "text": "je use que claude desktop je parlais de lire les fichier logs ou quoi"}]
      }]
    }
  },
  "version": 1,
  "updatedAt": 1775945026558
}
```

Le WAL se met à jour toutes les ~2 secondes pendant la frappe — confirmé en live.

**Format 2 — Cache React Query (confirmé)**
Les réponses de Claude et le contenu des conversations récentes sont stockés dans le cache React Query sous la clé `react-query-cache`. C'est du JSON TipTap enrichi avec du markdown rendu.

**Ce qui n'est PAS stocké localement :**
L'historique complet des conversations vit sur les serveurs Anthropic. Le LevelDB ne contient que le cache session courante + les drafts. C'est pourquoi l'historique est accessible depuis n'importe quel appareil connecté.

### Stratégie de lecture : FileShare.ReadWrite

La différence critique par rapport à Python : en C#, on lit le fichier `.log` directement pendant que Chromium y écrit, sans copie shadow :

```csharp
using var stream = new FileStream(
    path,
    FileMode.Open,
    FileAccess.Read,
    FileShare.ReadWrite  // Chromium garde son accès exclusif en écriture, on lit en parallèle
);
```

En Python avec `shutil.copytree`, Windows retourne une erreur sur le fichier `LOCK`. La solution de contournement (copie sélective) fonctionne mais est moins propre.

---

## 4. Architecture du Projet

### Structure des projets

```
Mnemos.sln
├── Mnemos.Core          ← types fondamentaux, interfaces, logique pure
├── Mnemos.Watcher       ← FileSystemWatcher + parser LevelDB
├── Mnemos.Storage       ← SQLite + FTS5
└── Mnemos.Service       ← entry point, MCP server + background service
```

### Mnemos.Core

Contient les types de base et les interfaces. Aucune dépendance externe.

```csharp
public record Turn(
    string Id,               // SHA256(ConversationId + Role + Content)
    string ConversationId,
    string Role,             // "human" | "assistant"
    string Content,
    DateTime Timestamp
);

public interface IMemoryStore
{
    Task SaveTurnsAsync(IEnumerable<Turn> turns);
    Task<IEnumerable<Turn>> SearchAsync(string query, int limit = 10);
    Task<IEnumerable<Turn>> GetByDateAsync(DateTime date);
}

public interface ILevelDbParser
{
    IEnumerable<Turn> Parse(byte[] rawData);
}
```

### Mnemos.Watcher

Deux responsabilités séparées.

**LevelDbWatcher** — surveille le dossier IndexedDB :

```csharp
public class LevelDbWatcher : BackgroundService
{
    // FileSystemWatcher sur %APPDATA%\Claude\IndexedDB
    // Debounce 500ms (Chromium écrit en rafale sur chaque frappe)
    // Filtre sur *.log et *.ldb uniquement
    // À chaque changement → lit via FileShare.ReadWrite → passe au parser
}
```

**LevelDbParser** — extrait les turns du binaire :

```csharp
public class LevelDbParser : ILevelDbParser
{
    // Lit le fichier avec FileShare.ReadWrite (pas de copie shadow)
    // Extrait les blobs JSON TipTap via regex sur le binaire brut
    // Parse le format p5store:chat-draft: pour les prompts
    // Parse le format react-query-cache pour les réponses Claude
    // Déduplique via SHA256(content) → évite les doublons sur update fréquent
}
```

### Mnemos.Storage

```csharp
// Schema SQLite
CREATE TABLE turns (
    id              TEXT PRIMARY KEY,   -- SHA256 du contenu
    conversation_id TEXT NOT NULL,
    role            TEXT NOT NULL,      -- 'human' ou 'assistant'
    content         TEXT NOT NULL,
    timestamp       INTEGER NOT NULL    -- Unix timestamp ms
);

// Index FTS5 pour la recherche full-text
CREATE VIRTUAL TABLE turns_fts USING fts5(
    content,
    content=turns,
    tokenize='unicode61'               -- gère les accents français
);

// Triggers pour synchroniser turns → turns_fts automatiquement
CREATE TRIGGER turns_ai AFTER INSERT ON turns BEGIN
    INSERT INTO turns_fts(rowid, content) VALUES (new.rowid, new.content);
END;
```

### Mnemos.Service

```csharp
Host.CreateDefaultBuilder()
    .UseWindowsService()              // service Windows natif
    .ConfigureServices(services => {
        services.AddHostedService<LevelDbWatcher>();
        services.AddSingleton<IMemoryStore, MemoryStore>();
        services.AddSingleton<ILevelDbParser, LevelDbParser>();
        services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTool<SearchMemoriesTool>()
            .WithTool<GetByDateTool>();
    });
```

---

## 5. Les Deux Tools MCP Exposés

### search_memories

Interroge SQLite FTS5 et retourne les N résultats les plus pertinents.

```csharp
[McpServerTool, Description("Search past conversations by keyword or topic")]
public async Task<string> SearchMemories(
    [Description("Keywords to search for")] string query,
    [Description("Max results")] int limit = 10)
{
    var results = await _store.SearchAsync(query, limit);
    return JsonSerializer.Serialize(results);
}
```

Exemple d'usage : "c'était quoi le bug de l'IndexedDB hier ?" → Claude appelle `search_memories("IndexedDB bug")` → SQLite FTS5 retourne les turns pertinents.

### get_context_by_date

Récupère toutes les conversations d'une date précise.

```csharp
[McpServerTool, Description("Get all conversations from a specific date")]
public async Task<string> GetContextByDate(
    [Description("Date in YYYY-MM-DD format")] string date)
{
    var dt = DateTime.Parse(date);
    var results = await _store.GetByDateAsync(dt);
    return JsonSerializer.Serialize(results);
}
```

---

## 6. Optimisation de la Recherche

### Pourquoi SQLite FTS5 et pas un vector store

À l'échelle réelle de ce projet :

```
Usage intensif H24 = ~100 turns/jour (estimation haute)
1 turn = ~1KB de texte moyen
1 an = ~36MB de texte brut
```

SQLite FTS5 sur 36MB retourne des résultats en < 5ms. Les bases vectorielles (Pinecone, Weaviate, Chroma) sont conçues pour des millions de documents. C'est de l'overkill pur.

### Algorithme FTS5 : BM25

FTS5 utilise BM25 (Best Match 25) par défaut — le même algorithme que les moteurs de recherche modernes. Il prend en compte la fréquence du terme dans le document, la fréquence inverse dans le corpus (IDF), et la longueur du document.

```sql
-- Recherche avec score de pertinence
SELECT turns.*, bm25(turns_fts) as score
FROM turns_fts
JOIN turns ON turns.rowid = turns_fts.rowid
WHERE turns_fts MATCH 'krino pipeline'
ORDER BY score
LIMIT 10;
```

### Optimisations supplémentaires

**Index sur timestamp** pour les requêtes par date :
```sql
CREATE INDEX idx_turns_timestamp ON turns(timestamp DESC);
CREATE INDEX idx_turns_conversation ON turns(conversation_id, timestamp);
```

**Tokenizer unicode61** : gère correctement les caractères français (accents, apostrophes) contrairement au tokenizer ASCII par défaut.

**Déduplication par hash** : chaque turn a un ID = SHA256(conversation_id + role + content). Si le même contenu est parsé deux fois (redémarrage, rechargement), l'INSERT est ignoré via `INSERT OR IGNORE`.

**Debounce du watcher** : Chromium écrit dans le WAL à chaque frappe clavier. Sans debounce, le parser serait appelé 50 fois par message. Le debounce à 500ms attend la fin de la frappe avant de traiter.

---

## 7. Flux de Données Complet

```
[1] Tu tapes un message dans Claude Desktop
        ↓
[2] Chromium écrit dans 000103.log (WAL) en temps réel
        ↓
[3] FileSystemWatcher détecte le changement (event Changed)
        ↓
[4] Debounce 500ms → LevelDbParser lit le fichier via FileShare.ReadWrite
        ↓
[5] Parser extrait le JSON TipTap → Turn { role: "human", content: "..." }
        ↓
[6] Claude génère sa réponse → Chromium met à jour react-query-cache
        ↓
[7] FileSystemWatcher détecte à nouveau → Parser extrait Turn { role: "assistant", content: "..." }
        ↓
[8] MemoryStore.SaveTurnsAsync() → INSERT OR IGNORE dans SQLite + FTS5 sync automatique
        ↓
[9] Plus tard : "c'était quoi le code de Mnemos.Watcher ?"
        ↓
[10] Claude appelle search_memories("Mnemos Watcher code")
        ↓
[11] SQLite FTS5 BM25 → TOP 10 turns pertinents
        ↓
[12] Claude répond avec le vrai contexte
```

---

## 8. Configuration Claude Desktop

Dans `claude_desktop_config.json` (`%APPDATA%\Claude\claude_desktop_config.json`) :

```json
{
  "mcpServers": {
    "mnemos": {
      "command": "C:\\Users\\Subaru\\RiderProjects\\Mnemos\\Mnemos.Service\\bin\\Release\\net9.0\\Mnemos.Service.exe",
      "args": []
    }
  }
}
```

Dans les Instructions système de Claude Desktop (Settings → Instructions) :

```
Tu as accès aux outils Mnemos qui te donnent accès à l'historique complet de nos conversations passées.
- Quand on fait référence à quelque chose de passé ("hier", "la semaine dernière", "le projet X"), utilise search_memories.
- Quand on demande ce qui a été fait à une date précise, utilise get_context_by_date.
- Pour les questions techniques sur des projets en cours, commence toujours par search_memories avec le nom du projet.
```

---

## 9. Limites Connues

**Cache React Query limité dans le temps** : Les réponses de Claude dans le LevelDB ne sont disponibles que pour les conversations récentes (cache de session). L'historique ancien vit sur les serveurs Anthropic et n'est pas accessible localement. Mnemos ne peut donc capturer que ce qui passe par le cache local.

**Format binaire V8** : Certaines valeurs du LevelDB sont sérialisées en V8 Serialization Format (format binaire JavaScript). Le parser actuel cible les blobs JSON lisibles. Si Anthropic change le format de stockage, le parser doit être mis à jour.

**Mises à jour Claude Desktop** : Le nom des fichiers `.log` change à chaque compactage LevelDB (ex: `000099.log` → `000103.log`). Le FileSystemWatcher surveille le dossier entier, donc ce changement est transparent.

---

## 10. Roadmap

**Phase 1 — MVP (en cours)**
- Parser LevelDB pour les drafts TipTap
- Storage SQLite FTS5
- MCP server avec search_memories et get_context_by_date
- Service Windows natif

**Phase 2 — Enrichissement**
- Parser react-query-cache pour les réponses Claude
- Tool `summarize_project(name)` : résumé d'un projet sur la période
- Tool `get_recent_context(hours)` : dernières N heures de conversation

**Phase 3 — Intelligence**
- Semantic search hybride : FTS5 + embeddings locaux (modèle léger via ONNX Runtime)
- Détection automatique de projets par clustering de conversations
- Morning digest : résumé automatique des tâches ouvertes

---

*Documentation générée le 12 avril 2026 — Projet Mnemos v0.1*

Les vrais dangers par priorité
1. Rotation LevelDB — le plus urgent à gérer dans le code
   csharp// Le .log actif peut disparaître pendant qu'on le lit
   // Solution : Polly retry + re-scan du dossier sur FileNotFoundException

services.AddResiliencePipeline("leveldb", builder => {
builder.AddRetry(new() {
ShouldHandle = new PredicateBuilder().Handle<IOException>(),
MaxRetryAttempts = 3,
Delay = TimeSpan.FromMilliseconds(200)
});
});
2. Fragilité du parser — signal d'alarme automatique
   csharp// Si 0 patterns trouvés pendant 24h → log dans Event Viewer Windows
   if (_lastSuccessfulParse < DateTime.Now.AddHours(-24))
   EventLog.WriteEntry("Mnemos", "Parser silent > 24h", EventLogEntryType.Warning);
3. SQLCipher — à faire dès le début, pas après
   Beaucoup plus simple à intégrer au départ qu'à migrer une base existante. Le package NuGet c'est SQLitePCLRaw.bundle_sqlcipher.

Ce que Gemini n'a pas mentionné mais qui va piquer
Le react-query-cache est un cache — il se vide. Les réponses de Claude qui datent de plus de X sessions seront purgées avant que Mnemos les capture. La vraie solution long terme reste le MCP save_transcript en complément du watcher.

Problème 1 — Code (le plus critique)
Si tu me donnes un fichier de 300 lignes et qu'on le modifie 10 fois, stocker 10 snapshots complets c'est 3000 lignes pour le même fichier. La solution c'est les diffs Myers :
v1 → stocké en entier (verbatim)
v2 → stocké comme diff vs v1 (seulement les lignes changées)
v3 → stocké comme diff vs v2
...

Reconstruction : rejouer les diffs dans l'ordre
tree-sitter permet d'aller encore plus loin — au lieu de differ le fichier entier, on diffère fonction par fonction. Si tu modifies une fonction sur 10, seule celle-là est mise à jour en base.

Problème 2 — Contenu conversationnel ("j'ai fait tel exo hier")
Là le verbatim est du gaspillage pur. La solution c'est la classification avant stockage :
Turn reçu
→ Router détecte le type

Type CODE      → verbatim + diff versioning
Type TECHNIQUE → verbatim chunké (512 tokens max)
Type FACTUEL   → compression sémantique en key-value
"completed: exercice X | date: hier | résultat: ok"
Type CASUAL    → vitality basse + taille courte → TTL court

Problème 3 — Compression SQLite
SQLite supporte zstd via extension. Le texte se compresse à ~30% de sa taille originale :
36MB brut → ~11MB compressé
Ou plus simple : compresser la colonne content en zlib avant INSERT, décompresser au SELECT. Transparent pour FTS5.

Problème 4 — Déduplication sémantique
Si tu me donnes le même code deux fois dans deux convs différentes, on stocke deux fois. Solution : hash SHA256 exact pour les doublons parfaits, et cosine similarity > 0.95 pour les quasi-doublons.
