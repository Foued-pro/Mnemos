# Reverse engineering Claude Desktop's storage

## TL;DR

Full extraction of conversation history and real-time monitoring, achieved by reverse-engineering Chromium/Blink/V8 proprietary storage formats — and then discovering that the Chromium HTTP cache was the better solution all along.

---

## Context

Claude Desktop is built on Electron. It stores conversations locally in a Chromium IndexedDB. There is no public documentation on the format. The goal was to read that data programmatically to build Mnemos — a persistent memory layer for Claude via MCP.

---

## What didn't work

Before finding the right path, four approaches failed in a row.

**LevelDB.NET** — the first instinct was to read the underlying LevelDB database directly using a C# wrapper. It returned `Corruption: corrupted compressed block contents` immediately. Chromium uses a custom Snappy build with different compilation flags than the one the wrapper expects.

**Brute force binary scraping** — reading `.blob` files as raw bytes and extracting UTF-8 sequences with Regex. Output was fragmented text with sentences cut in half. Snappy compression pointers and V8 markers split strings at arbitrary offsets.

**ccl_chromium_reader** — a Python library specifically built for this kind of work. Crashed with a `RecursionError` on the `react-query-cache` key. The library doesn't handle circular V8 object references.

**Native `v8.deserialize()` in Node.js** — the most promising attempt. Failed with `Unable to deserialize cloned data due to invalid or unsupported version`. The V8 stream contains Blink host objects (`0x5C`) that Node's V8 engine doesn't know how to handle without the Blink delegate.

---

## What the LevelDB actually contains

The `.ldb` files were the first target. After writing a full SSTable parser (block format, restart points, delta-encoded keys) and successfully reading 4,589 entries, the dump revealed:

| Store | Index | Count | Contents |
|---|---|---|---|
| `db=1 store=1 index=1` | data | 226 | TipTap draft states (JSON) |
| `db=1 store=1 index=2` | exists | 227 | Version markers |
| `db=1 store=1 index=3` | blobs | 276 | External object refs |

One object store. No conversation history. The LevelDB is exclusively used to persist the editor state between sessions — Claude autosaves the input field, not the messages.

The blob files referenced by `index=3` contain the actual conversation data, but only for sessions large enough to exceed Chromium's in-memory threshold (~64 KB). Short sessions never hit disk.

**Conclusion:** The LevelDB is a dead end for conversation extraction. The HTTP cache covers everything the blobs miss.

---

## Phase 1 — Cracking the blob format

### Identifying the compression

Inspecting the first bytes of a large blob file (`2cb0`, 2.17 MB):

```
FF 11 02 C1 C8 D9 02 0C ...
```

Reading `idb_value_wrapping.cc` in the Chromium source gave the answer directly:

```cpp
wire_data_buffer_[0] = static_cast<uint8_t>(kVersionTag);   // 0xFF
wire_data_buffer_[1] = kRequiresProcessingSSVPseudoVersion;  // 0x11
wire_data_buffer_[2] = kCompressedWithSnappy;                // 0x02
```

`FF 11 02` is a fixed 3-byte header. Everything after it is raw Snappy without framing.

### Snappy decompression

```python
import snappy
data = open('2cb0', 'rb').read()
decompressed = snappy.decompress(data[3:])
# 2,171,261 bytes → 5,661,761 bytes
```

### The Blink wrapper

First bytes after decompression:

```
FF 15 FE 00 00 00 00 00 00 00 00 00 00 00 00 FF 0F 6F ...
```

- `FF 15` → Blink version 21
- `FE 00 00 ... (×13)` → external blobs table (13 bytes of padding)
- `FF 0F` → V8 version 15 — the actual V8 stream starts here

That's a fixed 15-byte offset. Confirmed empirically by bruteforcing offsets 0 through 30 and checking which one `v8.deserialize()` accepted.

### What comes out

After decompression and skipping the 15-byte Blink prefix:

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

Conversations live in `clientState.queries`, filtered by `queryKey[0] == "chat_conversation_tree"`. Each entry contains the full conversation tree, including thinking blocks, timestamps, and parent-child message relationships.

### The V8 deserializer

Since neither Node nor any existing C# library could handle the stream cleanly, the only option was to write a deserializer from scratch. The opcodes encountered in real data:

| Opcode | Description |
|---|---|
| `0xFF` | Version header |
| `0x54 / 0x46` | `true` / `false` |
| `0x49` | int32 ZigZag encoded |
| `0x55` | uint32 |
| `0x4E` | double (8 bytes LE) |
| `0x22` | string Latin-1 |
| `0x63` | string UTF-16LE |
| `0x6F` | begin object |
| `0x7B` | end object |
| `0x41` | dense array |
| `0x5E` | object reference |
| `0x5C` | Blink host object (skipped) |

**Result:** 18,960 lines of conversations extracted cleanly, thinking blocks included.

### Deduplication

Claude autosaves the TipTap editor state on every keystroke. The LevelDB `store=1` contains dozens of intermediate draft versions of the same message. The pipeline deduplicates using the `updatedAt` timestamp — only the latest version of each entry is kept.

---

## Phase 2 — The real-time problem

### The limitation

Blobs only exist for large conversations (>64 KB). Short sessions never cross the threshold — they stay in RAM and never get flushed to disk. The blob approach worked well for history, but was useless for the main Mnemos use case: continuity across short sessions.

### Watching everything

The approach was simple: monitor the entire `AppData\Roaming\Claude` directory in real-time and log every file that changed while sending a message.

```
[CHANGE]  \sentry\scope_v3.json
[CHANGE]  \logs\claude.ai-web.log
[NEW]     \Cache\Cache_Data\f_0003d9  (275,556 bytes)
[CHANGE]  \Local Storage\leveldb\004581.log
[NEW]     \Cache\Cache_Data\f_0003da  (274,931 bytes)
```

Two new files in `Cache\Cache_Data` on every single prompt/response cycle.

What every other folder contained:

| Folder | Contents |
|---|---|
| `IndexedDB/.../blob` | Large conversations, V8+Snappy — good for history |
| `IndexedDB/.../leveldb` | Drafts only, doesn't update during streaming |
| `Session Storage/leveldb` | UUIDs and timestamps per conversation — signal only |
| `Local Storage/leveldb` | Nothing useful for messages |
| `Cache/Cache_Data/f_*` | **Full HTTP responses, zstd-compressed — the real thing** |

---

## Phase 3 — The HTTP cache

### The discovery

The `f_*` files open with `28 B5 2F FD` — the Zstandard magic bytes. Chromium is caching the full HTTP response from Claude's API, compressed in zstd.

```python
import zstandard as zstd
with open('f_0003e1', 'rb') as f:
    data = f.read()
decompressed = zstd.ZstdDecompressor().decompress(data, max_output_size=10_000_000)
# 868,685 bytes of clean JSON
```

Output:

```json
{
  "uuid": "959dc6fa-...",
  "name": "Vérifier le texte avant d'ajouter le regex",
  "chat_messages": [
    { "sender": "human", "content": [{ "type": "text", "text": "azy test" }] },
    { "sender": "assistant", "content": [{ "type": "text", "text": "Colle le résultat." }] }
  ]
}
```

Clean JSON, every message, every conversation — with no size threshold.

### Why this is the better solution

The HTTP cache makes the blob deserializer look like archaeology. No size threshold, no complex V8 parsing, no Blink offsets. Every conversation, every time, as clean JSON, available the moment a response arrives.

The blob pipeline still matters for initial history sync. But for everything real-time, the cache wins.

---

## Final pipeline

```
History sync
  IndexedDB blob files
    → strip FF 11 02 header
    → Snappy decompress
    → skip 15-byte Blink prefix
    → V8 deserialize
    → extract chat_conversation_tree queries
    → messages + thinking blocks

Real-time watch
  Cache/Cache_Data/f_* (FileSystemWatcher)
    → verify 28 B5 2F FD magic bytes
    → zstd decompress
    → parse JSON
    → messages + thinking blocks

Both pipelines feed into:
  SQLite (FTS5 + vector BLOBs)
    → ONNX embeddings (MiniLM-L6-v2)
    → MCP server
```

---

## Stack

- **C# / .NET 9**
- **Snappier** — Snappy decompression (blobs)
- **ZstdSharp.Port** — Zstandard decompression (HTTP cache)
- **System.Text.Json** — JSON parsing
- **Python + node-snappy + zstandard** — investigation phase
- **Chromium source** (`idb_value_wrapping.cc`) — format reference