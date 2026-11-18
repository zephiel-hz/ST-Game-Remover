using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SteamPluginManager.Services.Steam;

namespace SteamPluginManager.Tests;

public sealed class SteamAppInfoCacheTests
{
    [Theory]
    [InlineData(0x07564427u, 40)]
    [InlineData(0x07564428u, 60)]
    [InlineData(0x07564429u, 60)]
    public void ReadsEditsAndPreservesOtherApplicationBytes(uint version, int metadataSize)
    {
        using var directory = new TemporaryDirectory();
        string source = Path.Combine(directory.Path, "appinfo.vdf");
        string output = Path.Combine(directory.Path, "appinfo-edited.vdf");
        byte[] untouchedRecord = CreateFixture(source, version, metadataSize);

        using (var cache = new SteamAppInfoCache(source))
        {
            var application = cache.ReadApplication(101);
            Assert.NotNull(application);
            var body = application!.Value.Root.FindObject("appinfo");
            Assert.NotNull(body);
            body!.SetText("added_by_test", "value");

            var launchEntry = body.FindObject("config")!
                .FindObject("launch")!
                .FindObject("0")!;
            launchEntry.SetText("arguments", "-edited");

            cache.WriteCopy(output, new Dictionary<int, KeyValueObject>
            {
                [101] = application.Value.Root
            });
        }

        using var updated = new SteamAppInfoCache(output);
        var changed = updated.ReadApplication(101);
        Assert.NotNull(changed);
        Assert.Equal(17u, changed!.Value.ChangeNumber);
        var changedBody = changed.Value.Root.FindObject("appinfo")!;
        Assert.Equal("value", changedBody.FindText("added_by_test"));
        Assert.Equal("-edited", changedBody.FindObject("config")!
            .FindObject("launch")!.FindObject("0")!.FindText("arguments"));

        var untouched = updated.Entries[202];
        byte[] outputBytes = File.ReadAllBytes(output);
        byte[] outputUntouchedRecord = outputBytes
            .Skip(checked((int)untouched.Offset))
            .Take(8 + untouched.Size)
            .ToArray();
        Assert.Equal(untouchedRecord, outputUntouchedRecord);

        if (version >= 0x07564428)
        {
            var changedEntry = updated.Entries[101];
            byte[] metadata = outputBytes
                .Skip(checked((int)changedEntry.Offset + 8))
                .Take(metadataSize)
                .ToArray();
            byte[] blob = ExtractBlob(outputBytes, changedEntry.Offset, metadataSize, changedEntry.Size);
            Assert.Equal(SHA1.HashData(blob), metadata.Skip(40).Take(20));
        }
    }

    [Fact]
    public void RejectsUnsupportedAppInfoVersions()
    {
        using var directory = new TemporaryDirectory();
        string source = Path.Combine(directory.Path, "unsupported.vdf");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4, 1, 0, 0, 0 });

        Assert.Throws<SteamAppInfoFormatException>(() => new SteamAppInfoCache(source));
    }

    private static byte[] CreateFixture(string path, uint version, int metadataSize)
    {
        bool indexedKeys = version == 0x07564429;
        string[] keys =
        {
            "appinfo", "config", "launch", "0", "executable", "arguments",
            "type", "untouched", "added_by_test"
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(version);
        writer.Write(1u);
        long keyTablePosition = stream.Position;
        if (indexedKeys)
            writer.Write(0L);

        byte[] changedBlob = CreateApplicationBlob(indexedKeys, keys, "game.exe", "-old");
        byte[] otherBlob = CreateApplicationBlob(indexedKeys, keys, "other.exe", "-keep");
        WriteApplication(writer, 101, 17, metadataSize, changedBlob, version);
        long otherRecordStart = stream.Position;
        WriteApplication(writer, 202, 29, metadataSize, otherBlob, version);
        int otherRecordLength = checked((int)(stream.Position - otherRecordStart));
        writer.Write(0u);

        if (indexedKeys)
        {
            long tableOffset = stream.Position;
            writer.Flush();
            stream.Position = keyTablePosition;
            writer.Write(tableOffset);
            stream.Position = tableOffset;
            writer.Write((uint)keys.Length);
            foreach (string key in keys)
                WriteCString(writer, Encoding.UTF8.GetBytes(key));
        }

        writer.Flush();
        byte[] file = stream.ToArray();
        File.WriteAllBytes(path, file);
        return file.Skip(checked((int)otherRecordStart)).Take(otherRecordLength).ToArray();
    }

    private static byte[] CreateApplicationBlob(bool indexed, IReadOnlyList<string> keys, string executable, string arguments)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        StartTable(writer, "appinfo", indexed, keys);
        StartTable(writer, "config", indexed, keys);
        StartTable(writer, "launch", indexed, keys);
        StartTable(writer, "0", indexed, keys);
        WriteString(writer, "executable", executable, indexed, keys);
        WriteString(writer, "arguments", arguments, indexed, keys);
        WriteString(writer, "type", "default", indexed, keys);
        writer.Write((byte)8);
        writer.Write((byte)8);
        writer.Write((byte)8);
        writer.Write((byte)8);
        writer.Write((byte)8);
        return stream.ToArray();
    }

    private static void WriteApplication(
        BinaryWriter writer,
        uint appId,
        uint changeNumber,
        int metadataSize,
        byte[] blob,
        uint version)
    {
        var metadata = new byte[metadataSize];
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(36, 4), changeNumber);
        if (version >= 0x07564428)
            SHA1.HashData(blob).CopyTo(metadata, 40);

        writer.Write(appId);
        writer.Write(checked((uint)(metadata.Length + blob.Length)));
        writer.Write(metadata);
        writer.Write(blob);
    }

    private static void StartTable(BinaryWriter writer, string key, bool indexed, IReadOnlyList<string> keys)
    {
        writer.Write((byte)0);
        WriteKey(writer, key, indexed, keys);
    }

    private static void WriteString(BinaryWriter writer, string key, string value, bool indexed, IReadOnlyList<string> keys)
    {
        writer.Write((byte)1);
        WriteKey(writer, key, indexed, keys);
        WriteCString(writer, Encoding.UTF8.GetBytes(value));
    }

    private static void WriteKey(BinaryWriter writer, string key, bool indexed, IReadOnlyList<string> keys)
    {
        if (indexed)
            writer.Write((uint)Array.IndexOf(keys.ToArray(), key));
        else
            WriteCString(writer, Encoding.UTF8.GetBytes(key));
    }

    private static void WriteCString(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    private static byte[] ExtractBlob(byte[] file, long recordOffset, int metadataSize, int recordSize)
    {
        int blobStart = checked((int)recordOffset + 8 + metadataSize);
        int blobLength = recordSize - metadataSize;
        return file.Skip(blobStart).Take(blobLength).ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"appinfo-test-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
