# Mnemos

**Local persistent memory for Claude Desktop — 100% private, zero API calls.**

Mnemos intercepts your Claude Desktop conversations in real time, indexes them in a local SQLite database with full-text search, and exposes a MCP server so Claude can search its own memory across sessions.

> "Where were we on the krino project?" — Claude can now answer that.

---

## How it works

Claude Desktop stores conversations in a Chromium HTTP cache compressed with **Zstandard**. Mnemos watches for new cache files, decompresses them, extracts the messages, and stores them locally.

```
Claude Desktop conversation
    ↓
Cache/Cache_Data/f_* (zstd compressed)
    ↓
Mnemos CacheWatcher detects new file
    ↓
zstd decompress → JSON with full chat_messages[]
    ↓
SQLite + FTS5 index (local, private)
    ↓
MCP server → Claude calls search_memory()
```

No API calls. No Anthropic servers. No cloud. Everything stays on your machine.

---

## Features

- **Real-time capture** — every conversation is indexed as you talk
- **Full-text search** — BM25 + time decay, finds relevant messages fast
- **Thinking blocks** — Claude's internal reasoning is stored too
- **MCP server** — Claude can search its own memory mid-conversation
- **Deduplication** — UUID-based, no duplicate messages between sources
- **Tiered memory** — recent messages (Tier 1) vs archived (Tier 3)
- **Code block indexing** — trigram search on code snippets

---

## The reverse engineering

This project required reverse engineering Chromium's proprietary storage format from scratch:

| Layer | Discovery |
|-------|-----------|
| Compression | `FF 11 02` header = Snappy (found in `idb_value_wrapping.cc` Chromium source) |
| Blink wrapper | 15 bytes to skip after decompression (found empirically) |
| V8 serialization | Custom deserializer for V8 SSV opcodes (`0x6F`, `0x41`, `0x22`...) |
| HTTP cache | `28 B5 2F FD` = Zstandard magic bytes in `Cache/Cache_Data/f_*` |

Full writeup: [MNEMOS_REVERSE.md](./MNEMOS_REVERSE.md)

---

## Installation

### Prerequisites

- Windows (Claude Desktop path is hardcoded for now)
- .NET 9 SDK
- Claude Desktop

### Build

```bash
git clone https://github.com/Foued-pro/mnemos
cd mnemos/Mnemos
dotnet publish -c Release -r win-x64 --self-contained true
```

### Configure Claude Desktop

Add to `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "mnemos": {
      "command": "C:\\path\\to\\publish\\Mnemos.exe",
      "args": ["--mcp"]
    }
  }
}
```

Restart Claude Desktop.

---

## Usage

```
Mnemos.exe          → interactive menu
Mnemos.exe --sync   → extract all history to SQLite
Mnemos.exe --watch  → real-time capture mode
Mnemos.exe --mcp    → MCP server mode (used by Claude Desktop)
```

### MCP tools available to Claude

| Tool | Description |
|------|-------------|
| `search_memory(query)` | BM25 + time decay search across all conversations |
| `get_recent_messages(uuid, n)` | Last N messages from a specific conversation |
| `get_stats()` | Number of conversations and messages indexed |

---

## Database schema

```sql
conversations   — uuid, name, created_at, message_count
messages        — uuid, sender, text, thinking, tier, importance, embedding
code_blocks     — language, code (trigram FTS5 indexed)
tool_calls      — name, arguments, result
conversation_summaries — for future hierarchical summarization
```

Search is hybrid: **BM25 (FTS5) × 0.7 + time decay × 0.3 + importance bonus**

---

## Architecture

```
Mnemos/
├── Core/
│   ├── SnappyDecompressor.cs   — FF 11 02 header + Snappy decompress
│   ├── V8Deserializer.cs       — V8 SSV opcodes parser (from scratch)
│   └── ConversationExtractor.cs
├── Watchers/
│   ├── CacheWatcher.cs         — zstd cache files watcher (real-time)
│   └── BlobWatcher.cs          — IndexedDB blob watcher (history)
├── Database/
│   └── MnemosDb.cs             — SQLite + FTS5 + tiered memory
├── Mcp/
│   └── McpTools.cs             — MCP server tools
├── Search/
│   └── SearchHandler.cs        — CLI search mode
├── Models/
│   └── Models.cs
└── Program.cs
```

---

## Roadmap

- [ ] macOS / Linux support
- [ ] ONNX local embeddings (semantic search on RTX GPU)
- [ ] Auto-summarization of old conversations
- [ ] Multi-instance support
- [ ] Web UI

---

## Tech stack

- **C# / .NET 9**
- **SQLite** with FTS5 + trigram tokenizer
- **Snappier** — Snappy decompression
- **ZstdSharp.Port** — Zstandard decompression
- **ModelContextProtocol** — official MCP C# SDK

---

## Why this exists

Claude has no memory between sessions. Every time you start a new conversation, it starts from zero. Mnemos fixes that — not by sending your data to a cloud service, but by reading what's already stored locally on your machine.

Your conversations never leave your computer.

---

## License

MIT