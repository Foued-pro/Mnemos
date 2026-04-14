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
                case 0xFF: ReadVarint(); continue;   // version header, skip
                case 0x00: continue;                 // padding
                case 0x3F: ReadVarint(); continue;   // verify object count, skip

                case 0x54: return true;              // 'T' true
                case 0x46: return false;             // 'F' false
                case 0x30: return null;              // '0' null
                case 0x5F: return null;              // '_' undefined
                case 0x2D: return null;              // '-' hole

                case 0x49:                           // 'I' int32 zigzag
                {
                    uint z = ReadVarint();
                    return (int)((z >> 1) ^ -(z & 1));
                }

                case 0x55: return ReadVarint();      // 'U' uint32

                case 0x4E:                           // 'N' double (8 bytes LE)
                {
                    double d = BitConverter.ToDouble(_data, _pos);
                    _pos += 8;
                    return d;
                }

                case 0x22:                           // '"' one-byte string
                {
                    int len = (int)ReadVarint();
                    string s = Encoding.Latin1.GetString(_data, _pos, len);
                    _pos += len;
                    return s;
                }

                case 0x63:                           // 'c' two-byte string (UTF-16LE)
                {
                    int len = (int)ReadVarint();
                    string s = Encoding.Unicode.GetString(_data, _pos, len);
                    _pos += len;
                    return s;
                }

                case 0x6F:                           // 'o' begin object
                {
                    var obj = new Dictionary<string, object?>();
                    _refs.Add(obj);
                    while (_pos < _data.Length && _data[_pos] != 0x7B)
                    {
                        object? key = ReadValue();
                        object? val = ReadValue();
                        if (key is string k) obj[k] = val;
                    }
                    _pos++;          // skip '{'
                    ReadVarint();    // num_properties
                    return obj;
                }

                case 0x41:                           // 'A' dense array
                {
                    uint length = ReadVarint();
                    var arr = new List<object?>();
                    _refs.Add(arr);
                    for (uint i = 0; i < length; i++)
                        arr.Add(_data[_pos] == 0x2D ? (object?)(_pos++ >= 0 ? null : null) : ReadValue());
                    while (_pos < _data.Length && _data[_pos] != 0x24)
                    { ReadValue(); ReadValue(); }    // extra properties
                    _pos++;          // skip '$'
                    ReadVarint();    // num_properties
                    ReadVarint();    // length
                    return arr;
                }

                case 0x61:                           // 'a' sparse array
                {
                    ReadVarint();    // length
                    var arr = new List<object?>();
                    _refs.Add(arr);
                    while (_pos < _data.Length && _data[_pos] != 0x40)
                    { ReadValue(); arr.Add(ReadValue()); }
                    _pos++;          // skip '@'
                    ReadVarint(); ReadVarint();
                    return arr;
                }

                case 0x44:                           // 'D' Date
                {
                    double ms = BitConverter.ToDouble(_data, _pos);
                    _pos += 8;
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToString("o");
                }

                case 0x5E:                           // '^' object reference
                {
                    uint idx = ReadVarint();
                    return idx < _refs.Count ? _refs[(int)idx] : null;
                }

                case 0x5C:                           // '\' host object (Blink) — on skip
                    return null;

                default:
                    return null;                     // tag inconnu, on abandonne
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