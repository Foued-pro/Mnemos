using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Mnemos;

public enum RecordType : byte
{
    Zero   = 0,
    Full   = 1,
    First  = 2,
    Middle = 3,
    Last   = 4
}

public class LevelDbLogReader : IDisposable
{
    private readonly FileStream       _stream;
    private readonly byte[]           _buffer;
    private readonly SemaphoreSlim    _signal = new(0);
    private readonly FileSystemWatcher _watcher;

    private int _bufferOffset = 0;
    private int _bufferCount  = 0;
    private const int BlockSize = 32768;

    public LevelDbLogReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _buffer = new byte[BlockSize];

        var directory = Path.GetDirectoryName(path)!;
        var fileName  = Path.GetFileName(path);

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Changed += (s, e) => { if (_signal.CurrentCount == 0) _signal.Release(); };
        _watcher.EnableRaisingEvents = true;
    }

    public async IAsyncEnumerable<LevelDbRecord> ReadRecordsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var accumulator = new MemoryStream();

        while (!ct.IsCancellationRequested)
        {
            if (_bufferOffset >= _bufferCount)
            {
                long currentPos = _stream.Position;
                _bufferCount = await _stream.ReadAsync(_buffer, 0, _buffer.Length, ct);
                _bufferOffset = 0;

                if (_bufferCount == 0)
                {
                    _stream.Seek(currentPos, SeekOrigin.Begin); // Cache-buster
                    await _signal.WaitAsync(TimeSpan.FromSeconds(1), ct);
                    continue;
                }
            }

            ReadOnlySpan<byte> span = _buffer.AsSpan(_bufferOffset, _bufferCount - _bufferOffset);

            if (span.Length < 7) 
            { 
                _stream.Seek(-span.Length, SeekOrigin.Current);
                _bufferOffset = _bufferCount; 
                continue; 
            }

            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4, 2));
            RecordType type = (RecordType)span[6];

            if (type == RecordType.Zero || length == 0) 
            { 
                _bufferOffset = _bufferCount; 
                continue; 
            }

            if (span.Length < 7 + length) 
            {
                _stream.Seek(-span.Length, SeekOrigin.Current);
                _bufferOffset = _bufferCount; 
                continue;
            }

            ReadOnlySpan<byte> recordData = span.Slice(7, length);
            _bufferOffset += 7 + length;

            switch (type)
            {
                case RecordType.Full:
                    foreach (var r in ParseBatch(recordData.ToArray())) yield return r;
                    break;
                case RecordType.First:
                    accumulator.SetLength(0);
                    accumulator.Write(recordData);
                    break;
                case RecordType.Middle:
                    accumulator.Write(recordData);
                    break;
                case RecordType.Last:
                    accumulator.Write(recordData);
                    foreach (var r in ParseBatch(accumulator.ToArray())) yield return r;
                    accumulator.SetLength(0);
                    break;
            }
        }
    }

    private static List<LevelDbRecord> ParseBatch(byte[] batch)
    {
        var records = new List<LevelDbRecord>();
        if (batch.Length < 12) return records;

        try
        {
            using var stream = new MemoryStream(batch);
            stream.Seek(12, SeekOrigin.Begin); // Skip Seq (8) + Count (4)

            while (stream.Position < stream.Length)
            {
                int typeByte = stream.ReadByte();
                if (typeByte == -1) break;

                bool isInsertion = (typeByte == 1);

                int keyLen = ReadVarint32(stream);
                byte[] keyBytes = new byte[keyLen];
                stream.ReadExactly(keyBytes);

                byte[] valueBytes = Array.Empty<byte>();
                if (isInsertion)
                {
                    int valLen = ReadVarint32(stream);
                    valueBytes = new byte[valLen];
                    stream.ReadExactly(valueBytes);
                }

                // LE CORRECTIF EST LÀ :
                // On extrait la clé brute et on supprime les caractères de contrôle invisibles.
                string rawKey = Encoding.UTF8.GetString(keyBytes);
                string safeKey = new string(rawKey.Where(c => !char.IsControl(c)).ToArray());

                // On filtre avec safeKey (le prefixe n'est plus détruit)
                if (safeKey.Contains("chat-draft") || safeKey.Contains("react-query-cache"))
                {
                    records.Add(new LevelDbRecord(safeKey, valueBytes, isInsertion));
                }
            }
        }
        catch (Exception)
        {
            // En cas d'erreur de parsing binaire, on ignore silencieusement pour ne pas crasher le service
        }

        return records;
    }

    public static int ReadVarint32(Stream stream)
    {
        int result = 0, shift = 0;
        while (shift <= 28)
        {
            int b = stream.ReadByte();
            if (b == -1) throw new EndOfStreamException();
            result |= (b & 0x7f) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
        throw new FormatException("Varint too big");
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _stream.Dispose();
        _signal.Dispose();
    }
}

public record LevelDbRecord(string Key, byte[] Value, bool IsLive)
{
    public string ValueString => Encoding.UTF8.GetString(Value);
}