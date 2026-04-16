using System.Text;

namespace Mnemos;

public class V8Deserializer
{
    private readonly byte[] _data;
    private int _pos;
    private readonly List<object?> _refs = new();

    public V8Deserializer(byte[] data)
    {
        _data = data;
        _pos = 0;
    }

    public object? Deserialize() => ReadValue();

    private object? ReadValue()
    {
        while (_pos < _data.Length)
        {
            byte tag = _data[_pos++];
            switch (tag)
            {
                // Control tags
                case 0xFF: ReadVarint(); continue; // Version header
                case 0x00: continue;               // Padding
                case 0x3F: ReadVarint(); continue; // Object reference / padding

                // Primitives
                case 0x54: return true;            // 'T'
                case 0x46: return false;           // 'F'
                case 0x30: return null;            // '0' (The Hole)
                case 0x5F: return null;            // '_' (Undefined)
                case 0x2D: return null;            // '-' (Null)

                case 0x49:                         // 'I' (Int32 ZigZag)
                {
                    uint z = ReadVarint();
                    return (int)((z >> 1) ^ -(int)(z & 1));
                }

                case 0x55: return ReadVarint();    // 'U' (Uint32)

                case 0x4E:                         // 'N' (Double)
                {
                    if (_pos + 8 > _data.Length) return null;
                    var d = BitConverter.ToDouble(_data, _pos);
                    _pos += 8;
                    return d;
                }

                case 0x22:                         // '"' (String Latin1)
                {
                    int len = (int)ReadVarint();
                    var s = Encoding.GetEncoding("ISO-8859-1").GetString(_data, _pos, len);
                    _pos += len;
                    return s;
                }

                case 0x63:                         // 'c' (String UTF-16)
                {
                    int len = (int)ReadVarint();
                    var s = Encoding.Unicode.GetString(_data, _pos, len);
                    _pos += len;
                    return s;
                }

                case 0x6F:                         // 'o' (Object)
                {
                    var obj = new Dictionary<string, object?>();
                    _refs.Add(obj);
                    while (_pos < _data.Length && _data[_pos] != 0x7B)
                    {
                        if (ReadValue() is string key) obj[key] = ReadValue();
                    }
                    _pos++;
                    ReadVarint(); 
                    return obj;
                }

                case 0x41:                         // 'A' (Dense Array)
                {
                    uint length = ReadVarint();
                    var arr = new List<object?>();
                    _refs.Add(arr);
                    for (uint i = 0; i < length; i++)
                    {
                        if (_pos < _data.Length && _data[_pos] == 0x2D) { _pos++; arr.Add(null); }
                        else arr.Add(ReadValue());
                    }
                    while (_pos < _data.Length && _data[_pos] != 0x24) { ReadValue(); ReadValue(); }
                    _pos++; ReadVarint(); ReadVarint();
                    return arr;
                }

                case 0x61:                         // 'a' (Sparse Array)
                {
                    ReadVarint(); 
                    var arr = new List<object?>();
                    _refs.Add(arr);
                    while (_pos < _data.Length && _data[_pos] != 0x40)
                    {
                        ReadValue(); // index
                        arr.Add(ReadValue());
                    }
                    _pos++; // Skip '@'
                    ReadVarint(); ReadVarint();
                    return arr;
                }

                case 0x44:                         // 'D' (Date)
                {
                    var ms = BitConverter.ToDouble(_data, _pos);
                    _pos += 8;
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToString("o");
                }

                case 0x5E:                         // '^' (Back-reference)
                    return _refs.ElementAtOrDefault((int)ReadVarint());

                case 0x5C: return null;            // '\' (Host Object)

                default: return null;
            }
        }
        return null;
    }

    private uint ReadVarint()
    {
        uint result = 0; int shift = 0;
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