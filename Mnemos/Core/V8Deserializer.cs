namespace Mnemos.Core;

/// <summary>
/// Reads V8-serialized values from a byte array and reconstructs
/// the corresponding .NET object graph (primitives, strings,
/// arrays, objects, dates, back-references).
/// Based on the SSV (Script Serialization Value) opcode set
/// found in Blink's idb_value_wrapping.cc and V8 serialization.h.
/// </summary>
public class V8Deserializer
{
    private readonly byte[] _data;
    private int _pos;
    private readonly List<object?> _refs = new(); // back-reference table
    private readonly Action<string>? _log;

    /// <summary>
    /// Initializes the deserializer with raw V8 bytes.
    /// </summary>
    /// <param name="data">Decompressed payload from IndexedDB.</param>
    /// <param name="log">Optional diagnostic logger.</param>
    public V8Deserializer(byte[] data, Action<string>? log = null)
    {
        _data = data;
        _log  = log;
    }

    /// <summary>
    /// Main entry point: deserializes the entire payload and returns
    /// the root object (typically a Dictionary or List), or null on failure.
    /// </summary>
    public object? Deserialize()
    {
        try
        {
            return ReadValue();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[V8] Parse failed at offset {_pos}: {ex.Message}");
            return null;
        }
    }

    // ── Opcode interpreter ─────────────────────────────────

    /// <summary>
    /// Reads a single V8-encoded value starting at the current position.
    /// Each opcode byte determines the type and structure that follows.
    /// </summary>
    private object? ReadValue()
    {
        while (_pos < _data.Length)
        {
            byte tag = _data[_pos++];

            switch (tag)
            {
                // --- Structural markers ---
                case 0xFF: ReadVarint(); continue;  // Version prefix — skip
                case 0x00: continue;                // Zero-padding byte

                // --- Object reference slot ---
                case 0x3F:
                    ReadVarint(); // reference ID — consumed for structure
                    continue;

                // --- Literals ---
                case 0x54: return true;             // 'T'
                case 0x46: return false;            // 'F'
                case 0x30: return null;             // '0' — the undefined slot ("hole") in sparse arrays
                case 0x5F: return null;             // '_' — explicit undefined
                case 0x2D: return null;             // '-' — explicit null

                // --- Numeric types ---
                case 0x49:                          // 'I' — ZigZag-encoded int32
                {
                    uint z = ReadVarint();
                    return (int)((z >> 1) ^ -(int)(z & 1));
                }

                case 0x55:                          // 'U' — raw uint32
                    return ReadVarint();

                case 0x4E:                          // 'N' — IEEE 754 double (8 bytes)
                {
                    if (_pos + 8 > _data.Length) return null;
                    double d = BitConverter.ToDouble(_data, _pos);
                    _pos += 8;
                    return d;
                }

                // --- Strings ---
                case 0x22:                          // '"' — Latin-1 string
                {
                    int len = (int)ReadVarint();
                    string s = System.Text.Encoding.GetEncoding("ISO-8859-1")
                        .GetString(_data, _pos, len);
                    _pos += len;
                    return s;
                }

                case 0x63:                          // 'c' — UTF-16LE string
                {
                    int len = (int)ReadVarint();
                    string s = System.Text.Encoding.Unicode.GetString(_data, _pos, len);
                    _pos += len;
                    return s;
                }

                // --- Container objects ---
                case 0x6F:                          // 'o' — dictionary object
                {
                    var obj = new Dictionary<string, object?>();
                    _refs.Add(obj);                 // store for back-references
                    // Read key-value pairs until end-marker 0x7B ('{')
                    while (_pos < _data.Length && _data[_pos] != 0x7B)
                    {
                        if (ReadValue() is string key)
                            obj[key] = ReadValue();
                    }
                    _pos++;                         // skip 0x7B
                    ReadVarint();                   // trailing length marker
                    return obj;
                }

                case 0x41:                          // 'A' — dense array
                {
                    uint length = ReadVarint();
                    var arr = new List<object?>();
                    _refs.Add(arr);
                    for (uint i = 0; i < length; i++)
                    {
                        if (_pos < _data.Length && _data[_pos] == 0x2D)
                        {
                            _pos++;                 // skip explicit null marker
                            arr.Add(null);
                        }
                        else
                        {
                            arr.Add(ReadValue());
                        }
                    }
                    // Skip optional property bag (key-value until 0x24 '$')
                    while (_pos < _data.Length && _data[_pos] != 0x24)
                    {
                        ReadValue();
                        ReadValue();
                    }
                    _pos++;                         // skip 0x24
                    ReadVarint();                   // trailing length
                    ReadVarint();
                    return arr;
                }

                case 0x61:                          // 'a' — sparse array
                {
                    ReadVarint();                   // length (unused for parsing)
                    var arr = new List<object?>();
                    _refs.Add(arr);
                    while (_pos < _data.Length && _data[_pos] != 0x40) // 0x40 = '@'
                    {
                        ReadValue();                // index (discarded)
                        arr.Add(ReadValue());
                    }
                    _pos++;                         // skip 0x40
                    ReadVarint();
                    ReadVarint();
                    return arr;
                }

                // --- Special objects ---
                case 0x44:                          // 'D' — Date (ms since epoch as double)
                {
                    double ms = BitConverter.ToDouble(_data, _pos);
                    _pos += 8;
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToString("o");
                }

                case 0x5E:                          // '^' — back-reference
                    return _refs.ElementAtOrDefault((int)ReadVarint());

                case 0x5C:                          // '\' — host object (e.g. Blink internal), ignored
                    return null;

                // --- Unknown opcode — skip silently ---
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads an unsigned LEB128 variable-length integer (varint).
    /// Used extensively throughout the V8 serialization format.
    /// </summary>
    private uint ReadVarint()
    {
        uint result = 0;
        int shift = 0;

        while (_pos < _data.Length)
        {
            byte b = _data[_pos++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }

        return result;
    }
} 