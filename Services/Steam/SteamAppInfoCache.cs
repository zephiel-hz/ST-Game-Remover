using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SteamPluginManager.Models;

namespace SteamPluginManager.Services.Steam
{
    internal sealed class SteamAppInfoFormatException : Exception
    {
        public SteamAppInfoFormatException(string message) : base(message) { }
    }

    internal sealed class SteamAppInfoCache : IDisposable
    {
        private const uint Version27 = 0x07564427;
        private const uint Version28 = 0x07564428;
        private const uint Version29 = 0x07564429;
        private const int Sha1BinaryOffset = 40;
        private const int ChangeNumberOffset = 36;
        private const int MaximumStringBytes = 32 * 1024 * 1024;
        private const int MaximumDepth = 128;

        private readonly FileStream _input;
        private readonly List<byte[]> _keyBytes = new();
        private readonly List<string> _keyNames = new();
        private readonly Dictionary<string, uint> _keyIndices = new(StringComparer.Ordinal);
        private readonly Dictionary<int, AppEntry> _entries = new();
        private readonly long _dataStart;

        public uint Version { get; }
        public uint Universe { get; }
        public int MetadataSize => Version >= Version28 ? 60 : 40;
        public bool UsesIndexedKeys => Version >= Version29;
        public IReadOnlyDictionary<int, AppEntry> Entries => _entries;

        public SteamAppInfoCache(string path)
        {
            _input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try
            {
                Version = ReadUInt32(_input);
                Universe = ReadUInt32(_input);
                if (Version is not (Version27 or Version28 or Version29))
                    throw new SteamAppInfoFormatException($"Steam appinfo format 0x{Version:X8} is not supported.");

                if (UsesIndexedKeys)
                {
                    long tableOffset = ReadInt64(_input);
                    if (tableOffset < 16 || tableOffset >= _input.Length)
                        throw new SteamAppInfoFormatException("The appinfo key table offset is invalid.");

                    long resume = _input.Position;
                    ReadKeyTable(tableOffset);
                    _input.Position = resume;
                }

                _dataStart = _input.Position;
                BuildEntryIndex();
            }
            catch
            {
                _input.Dispose();
                throw;
            }
        }

        public (KeyValueObject Root, uint ChangeNumber)? ReadApplication(int appId)
        {
            if (!_entries.TryGetValue(appId, out var entry))
                return null;

            _input.Position = entry.Offset + 8 + MetadataSize;
            long blobEnd = entry.Offset + 8 + entry.Size;
            var root = ReadObject(_input, blobEnd, depth: 0);
            if (_input.Position > blobEnd)
                throw new InvalidDataException($"Appinfo entry {appId} exceeds its recorded size.");

            return (root, entry.ChangeNumber);
        }

        public void WriteCopy(string destination, IReadOnlyDictionary<int, KeyValueObject> replacements)
        {
            var outputKeys = new List<byte[]>(_keyBytes);
            var outputIndices = new Dictionary<string, uint>(_keyIndices, StringComparer.Ordinal);

            uint Intern(string key)
            {
                if (outputIndices.TryGetValue(key, out uint index))
                    return index;
                index = checked((uint)outputKeys.Count);
                outputKeys.Add(Encoding.UTF8.GetBytes(key));
                outputIndices.Add(key, index);
                return index;
            }

            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            WriteUInt32(output, Version);
            WriteUInt32(output, Universe);
            long keyTablePosition = output.Position;
            if (UsesIndexedKeys)
                WriteInt64(output, 0);

            foreach (var entry in _entries.Values.OrderBy(item => item.Offset))
            {
                _input.Position = entry.Offset + 8;
                byte[] metadata = ReadExact(_input, MetadataSize);

                if (replacements.TryGetValue(entry.AppId, out var replacement))
                {
                    using var blob = new MemoryStream();
                    WriteObject(blob, replacement, Intern, depth: 0);
                    byte[] blobBytes = blob.ToArray();
                    if (Version >= Version28)
                        SHA1.HashData(blobBytes).CopyTo(metadata, Sha1BinaryOffset);

                    WriteUInt32(output, checked((uint)entry.AppId));
                    WriteUInt32(output, checked((uint)(metadata.Length + blobBytes.Length)));
                    output.Write(metadata);
                    output.Write(blobBytes);
                }
                else
                {
                    WriteUInt32(output, checked((uint)entry.AppId));
                    WriteUInt32(output, checked((uint)entry.Size));
                    output.Write(metadata);
                    CopyBytes(_input, output, entry.Size - MetadataSize);
                }
            }

            WriteUInt32(output, 0);
            if (UsesIndexedKeys)
            {
                long tableOffset = output.Position;
                WriteUInt32(output, checked((uint)outputKeys.Count));
                foreach (byte[] key in outputKeys)
                    WriteCString(output, key);

                output.Position = keyTablePosition;
                WriteInt64(output, tableOffset);
            }
            output.Flush(flushToDisk: true);
        }

        private void ReadKeyTable(long offset)
        {
            _input.Position = offset;
            uint count = ReadUInt32(_input);
            if (count > 2_000_000)
                throw new SteamAppInfoFormatException("The appinfo key table is unexpectedly large.");

            for (uint i = 0; i < count; i++)
            {
                byte[] raw = ReadCString(_input, _input.Length);
                _keyBytes.Add(raw);
                string text = Encoding.UTF8.GetString(raw);
                _keyNames.Add(text);
                _keyIndices.TryAdd(text, i);
            }
        }

        private void BuildEntryIndex()
        {
            _input.Position = _dataStart;
            while (_input.Position + 4 <= _input.Length)
            {
                long offset = _input.Position;
                uint rawAppId = ReadUInt32(_input);
                if (rawAppId == 0)
                    return;
                if (_input.Position + 4 > _input.Length)
                    throw new SteamAppInfoFormatException("An appinfo entry is missing its size field.");

                uint rawSize = ReadUInt32(_input);
                if (rawAppId > int.MaxValue || rawSize > int.MaxValue || rawSize < MetadataSize)
                    throw new SteamAppInfoFormatException("An appinfo entry has invalid metadata.");

                long entryEnd = checked(offset + 8 + rawSize);
                if (entryEnd > _input.Length)
                    throw new SteamAppInfoFormatException("An appinfo entry extends beyond the file.");

                byte[] metadata = ReadExact(_input, MetadataSize);
                uint changeNumber = BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(ChangeNumberOffset, 4));
                int appId = (int)rawAppId;
                _entries[appId] = new AppEntry(appId, offset, (int)rawSize, changeNumber);
                _input.Position = entryEnd;
            }

            throw new SteamAppInfoFormatException("The appinfo cache is missing its end marker.");
        }

        private KeyValueObject ReadObject(Stream stream, long end, int depth)
        {
            if (depth > MaximumDepth)
                throw new InvalidDataException("The appinfo key-value nesting is too deep.");

            var result = new KeyValueObject();
            while (stream.Position < end)
            {
                int marker = stream.ReadByte();
                if (marker < 0)
                    throw new EndOfStreamException("Unexpected end of appinfo key-value data.");
                if (marker is 0x08 or 0x0B)
                {
                    result.EndMarker = (byte)marker;
                    return result;
                }

                byte kind = (byte)marker;
                string name = UsesIndexedKeys
                    ? GetIndexedKey(ReadUInt32(stream), end)
                    : Encoding.UTF8.GetString(ReadCString(stream, end));

                object value = kind switch
                {
                    0x00 => ReadObject(stream, end, depth + 1),
                    0x01 => ReadCString(stream, end),
                    0x05 => ReadWideString(stream, end),
                    0x02 or 0x03 or 0x04 or 0x06 => ReadExact(stream, 4, end),
                    0x07 or 0x0A => ReadExact(stream, 8, end),
                    _ => throw new InvalidDataException($"Unsupported appinfo value type 0x{kind:X2}.")
                };
                result.Properties.Add(new KeyValueProperty(name, kind, value));
            }

            throw new InvalidDataException("An appinfo key-value object has no end marker.");
        }

        private string GetIndexedKey(uint index, long end)
        {
            EnsureCanRead(_input, end, 4);
            if (index >= _keyNames.Count)
                throw new InvalidDataException($"Appinfo refers to missing key index {index}.");
            return _keyNames[(int)index];
        }

        private static byte[] ReadWideString(Stream stream, long end)
        {
            using var result = new MemoryStream();
            while (stream.Position + 2 <= end && result.Length < MaximumStringBytes)
            {
                byte[] pair = ReadExact(stream, 2, end);
                if (pair[0] == 0 && pair[1] == 0)
                    return result.ToArray();
                result.Write(pair);
            }
            throw new InvalidDataException("A wide appinfo string is invalid or too long.");
        }

        private static byte[] ReadCString(Stream stream, long end)
        {
            using var result = new MemoryStream();
            while (stream.Position < end && result.Length < MaximumStringBytes)
            {
                int value = stream.ReadByte();
                if (value < 0)
                    break;
                if (value == 0)
                    return result.ToArray();
                result.WriteByte((byte)value);
            }
            throw new InvalidDataException("An appinfo string is missing its terminator or is too long.");
        }

        private static byte[] ReadExact(Stream stream, int count, long end)
        {
            EnsureCanRead(stream, end, count);
            byte[] result = new byte[count];
            stream.ReadExactly(result);
            return result;
        }

        private static void EnsureCanRead(Stream stream, long end, int count)
        {
            if (count < 0 || stream.Position + count > end)
                throw new InvalidDataException("Appinfo data ended unexpectedly.");
        }

        private void WriteObject(Stream stream, KeyValueObject value, Func<string, uint> intern, int depth)
        {
            if (depth > MaximumDepth)
                throw new InvalidDataException("The appinfo key-value nesting is too deep.");

            foreach (var property in value.Properties)
            {
                stream.WriteByte(property.Kind);
                if (UsesIndexedKeys)
                    WriteUInt32(stream, intern(property.Name));
                else
                    WriteCString(stream, Encoding.UTF8.GetBytes(property.Name));

                switch (property.Kind)
                {
                    case 0x00:
                        WriteObject(stream, (KeyValueObject)property.Value, intern, depth + 1);
                        break;
                    case 0x01:
                        WriteCString(stream, (byte[])property.Value);
                        break;
                    case 0x05:
                        stream.Write((byte[])property.Value);
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                        break;
                    default:
                        stream.Write((byte[])property.Value);
                        break;
                }
            }
            stream.WriteByte(value.EndMarker);
        }

        private static void CopyBytes(Stream input, Stream output, int length)
        {
            byte[] buffer = new byte[81920];
            while (length > 0)
            {
                int read = input.Read(buffer, 0, Math.Min(buffer.Length, length));
                if (read == 0)
                    throw new EndOfStreamException("An appinfo blob ended unexpectedly.");
                output.Write(buffer, 0, read);
                length -= read;
            }
        }

        private static uint ReadUInt32(Stream stream) => BinaryPrimitives.ReadUInt32LittleEndian(ReadExact(stream, 4));
        private static long ReadInt64(Stream stream) => BinaryPrimitives.ReadInt64LittleEndian(ReadExact(stream, 8));

        private static byte[] ReadExact(Stream stream, int count)
        {
            byte[] bytes = new byte[count];
            stream.ReadExactly(bytes);
            return bytes;
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void WriteInt64(Stream stream, long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void WriteCString(Stream stream, byte[] value)
        {
            stream.Write(value);
            stream.WriteByte(0);
        }

        public void Dispose() => _input.Dispose();

        internal sealed record AppEntry(int AppId, long Offset, int Size, uint ChangeNumber);
    }

    internal sealed class KeyValueObject
    {
        public List<KeyValueProperty> Properties { get; } = new();
        public byte EndMarker { get; set; } = 0x08;

        public KeyValueObject? FindObject(string name) =>
            Properties.FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                ?.Value as KeyValueObject;

        public string? FindText(string name)
        {
            var property = Properties.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            return property?.Value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;
        }

        public KeyValueObject GetOrAddObject(string name)
        {
            if (FindObject(name) is { } existing)
                return existing;
            var created = new KeyValueObject();
            Properties.Add(new KeyValueProperty(name, 0x00, created));
            return created;
        }

        public void SetText(string name, string? value)
        {
            Remove(name);
            if (!string.IsNullOrEmpty(value))
                Properties.Add(new KeyValueProperty(name, 0x01, Encoding.UTF8.GetBytes(value)));
        }

        public void Remove(string name)
        {
            Properties.RemoveAll(property =>
                string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public KeyValueObject DeepCopy()
        {
            var copy = new KeyValueObject { EndMarker = EndMarker };
            foreach (var property in Properties)
            {
                object value = property.Value switch
                {
                    KeyValueObject child => child.DeepCopy(),
                    byte[] bytes => bytes.ToArray(),
                    _ => property.Value
                };
                copy.Properties.Add(new KeyValueProperty(property.Name, property.Kind, value));
            }
            return copy;
        }
    }

    internal sealed record KeyValueProperty(string Name, byte Kind, object Value);
}
