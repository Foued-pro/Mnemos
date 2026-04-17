# Mnemos
Claude Desktop has no memory API. So I reverse-engineered its storage.

Mnemos is a local MCP server that intercepts Claude Desktop's conversation data in real-time, vectorizes it with a local ONNX model, and exposes hybrid search back to Claude — without any API calls, cloud sync, or data leaving your machine.
## The problem

LLMs degrade as their context window fills up. Continuing a single conversation forever makes the model slower, more confused, and prone to hallucinations — a phenomenon sometimes called *context rot*.

Mnemos solves this with selective memory: instead of stuffing 200k tokens into a single prompt, Claude can query only the relevant fragments from past sessions on demand, keeping active contexts clean and lightweight.

---

## How it works

Mnemos watches Claude Desktop's local storage in real-time, decompresses and deserializes conversation data, vectorizes it with a local ONNX model, and indexes everything into a local SQLite database. An MCP server then exposes search tools back to Claude.

```
Claude Desktop (Chromium)
    ├─ Active session ──> CacheWatcher  (Zstd decompression + JSON parsing)
    └─ History ─────────> BlobWatcher  (Snappy decompression + V8 deserialization)
                               │
                               ▼
              EmbeddingEngine (ONNX MiniLM-L6-v2)
                               │
                               ▼
              SQLite  (FTS5 + vector BLOBs)
                               │
                               ▼
              MCP Server  <── JSON-RPC over stdio ──>  Claude
```

Search uses **Reciprocal Rank Fusion** to merge semantic results (cosine similarity) with keyword results (BM25 via FTS5), giving better recall than either approach alone.

---

## Features

- **Hybrid search** — semantic + keyword, merged with RRF
- **Local embeddings** — `MiniLM-L6-v2` via ONNX Runtime, CUDA-accelerated when available
- **Real-time indexing** — OS file watchers + semaphore debouncing, zero polling overhead
- **Thinking extraction** — Claude's internal reasoning blocks are indexed alongside regular turns
- **Fully offline** — nothing leaves your machine

---

## The reverse engineering

Claude Desktop exposes no history API. Getting to the conversation data required reverse-engineering several layers of Chromium's internal storage formats.

| Layer | Finding |
|---|---|
| HTTP cache | `28 B5 2F FD` — Zstandard magic bytes in `Cache/Cache_Data/f_*` |
| Compression | `FF 11 02` header — Snappy framed stream, identified via `idb_value_wrapping.cc` |
| Blink wrapper | 15-byte internal metadata prefix, successfully stripped |
| V8 serialization | Custom C# deserializer for SSV opcodes: `0x6F` objects, `0x49` ZigZag ints, `0x41` dense arrays |

Full writeup: [MNEMOS_REVERSE.md](./MNEMOS_REVERSE.md)

---

## Stack

- **C# / .NET 9**
- **SQLite** with FTS5 and trigram tokenizer
- **Microsoft.ML.OnnxRuntime**
- **ModelContextProtocol.Server** (official MCP C# SDK)

---
## Download

**[→ Download Mnemos.exe (v1.0.0)](https://github.com/Foued-pro/Mnemos/releases/latest)**

No .NET installation required — self-contained executable.

Add to `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "mnemos": {
      "command": "C:\\path\\to\\Mnemos.exe"
    }
  }
}
```

Restart Claude Desktop.

---

## Build from source

**Prerequisites:** Windows, .NET 9 SDK, Claude Desktop

```bash
git clone https://github.com/Foued-pro/mnemos
cd mnemos/Mnemos
dotnet publish -c Release -r win-x64 --self-contained true
```

## MCP tools

| Tool | Description |
|---|---|
| `search_hybrid(query, limit)` | Hybrid semantic + keyword search across all indexed conversations |
| `get_recent_messages(uuid, n)` | Fetch the last N messages from a specific thread |
| `get_stats()` | Database health, indexing progress, and memory usage |

---

## Roadmap

- macOS / Linux path support
- Auto-summarization of stale conversations
- Multi-instance support
- Web UI for browsing the database

---

## License

MIT
