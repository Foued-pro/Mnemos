# Mnemos
Claude Desktop has no memory API. So I reverse-engineered its storage.

Mnemos is a local MCP server that intercepts Claude Desktop's conversation data in real-time, vectorizes it with a local ONNX model, and exposes hybrid search back to Claude — without any API calls, cloud sync, or data leaving your machine.

**v1.1** adds a native GUI for browsing, searching, and visualizing your conversation history as a semantic 3D constellation.

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
                               │
                               ▼
              Mnemos.App  (WPF + WebView2 + React)
```

Search uses **Reciprocal Rank Fusion** to merge semantic results (cosine similarity) with keyword results (BM25 via FTS5), giving better recall than either approach alone.

---

## Features

- **Hybrid search** — semantic + keyword, merged with RRF
- **Local embeddings** — `MiniLM-L6-v2` via ONNX Runtime, CUDA-accelerated when available
- **Real-time indexing** — OS file watchers + semaphore debouncing, zero polling overhead
- **Thinking extraction** — Claude's internal reasoning blocks are indexed alongside regular turns
- **Fully offline** — nothing leaves your machine

### v1.1 — GUI

- **Semantic constellation** — all indexed messages projected into 3D space via UMAP + K-Means clustering in 384D embedding space. Clusters computed on raw embeddings, not compressed coordinates — no information loss.
- **Time machine** — scrub through your conversation history chronologically and watch the constellation build itself
- **Hybrid search UI** — real-time search across all indexed conversations with semantic + keyword fusion
- **Dark/light mode** — with bloom effect in dark mode
- **Auto-configuration** — writes `claude_desktop_config.json` on first launch, no manual setup required
- **Recent messages** — live feed of the last intercepted messages, updates every 5 seconds

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

**MCP Server**
- C# / .NET 9
- SQLite with FTS5 and trigram tokenizer
- Microsoft.ML.OnnxRuntime
- ModelContextProtocol.Server (official MCP C# SDK)

**GUI (v1.1)**
- WPF + WebView2 (borderless, resizable)
- React + Vite + TypeScript + Tailwind
- Three.js + OrbitControls + UnrealBloomPass
- UMAP.NET for dimensionality reduction

---

## Download

**[→ Download Mnemos.App.exe (v1.1)](https://github.com/Foued-pro/Mnemos/releases/latest)**

No .NET installation required — self-contained executable. Launches the GUI and configures the MCP server automatically.

**MCP only (no GUI):**

**[→ Download Mnemos.exe (v1.0)](https://github.com/Foued-pro/Mnemos/releases)**

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

**Prerequisites:** Windows, .NET 9 SDK, Node.js 18+, Claude Desktop

```bash
git clone https://github.com/Foued-pro/mnemos
cd mnemos

# Build the React frontend
cd Mnemos-ui
npm install
npm run build
cd ..

# Copy frontend to WPF app
xcopy /E /Y Mnemos-ui\dist\* Mnemos.App\wwwroot\

# Publish everything to a single folder
dotnet publish Mnemos -c Release -r win-x64 --self-contained true -o publish_final
dotnet publish Mnemos.App -c Release -r win-x64 --self-contained true -o publish_final
xcopy /E /Y Mnemos.App\wwwroot publish_final\wwwroot\
```

Launch `publish_final\Mnemos.App.exe`.

---

## MCP tools

| Tool | Description |
|---|---|
| `search_hybrid(query, limit)` | Hybrid semantic + keyword search across all indexed conversations |
| `get_recent_messages(uuid, n)` | Fetch the last N messages from a specific thread |
| `get_file_history(file_path)` | Version history of a tracked code file |
| `get_stats()` | Database health, indexing progress, and memory usage |

---

## Roadmap

- macOS / Linux path support
- Auto-summarization of stale conversations
- Multi-instance support
- Project-level context graphs (v2)
- Semantic diff tracking across file versions (v2)

---

## License

MIT