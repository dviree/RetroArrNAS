using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;

namespace RetroArr.Core.Games
{
    /// <summary>
    /// Reads one small file (sce_sys/param.json) out of an exFAT image without mounting it, so the title, ID and version
    /// come from the game itself and RetroArr needs no root. Read only; anything it does not understand gives null.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public static class ExFatImageReader
    {
        private const int MaxFileBytes = 4 * 1024 * 1024;
        private const int MaxDirectoryBytes = 8 * 1024 * 1024;

        private sealed record Entry(string Name, bool IsDirectory, uint FirstCluster, long Length, bool Contiguous);

        private sealed class Volume
        {
            public required FileStream Stream { get; init; }
            public long FatOffset { get; init; }
            public long HeapOffset { get; init; }
            public long ClusterSize { get; init; }
            public uint ClusterCount { get; init; }
        }

        /// <summary>The first of the paths found in the image, or inside its one top folder (PPSA01234-app0/...).</summary>
        public static byte[]? ReadFirst(string image, params string[] paths)
        {
            try
            {
                using var stream = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var boot = new byte[512];
                stream.ReadExactly(boot);
                if (Encoding.ASCII.GetString(boot, 3, 8) != "EXFAT   ") return null;
                long bytesPerSector = 1L << boot[108];
                var volume = new Volume
                {
                    Stream = stream,
                    FatOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(80)) * bytesPerSector,
                    HeapOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(88)) * bytesPerSector,
                    ClusterSize = bytesPerSector << boot[109],
                    ClusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(92))
                };
                var root = ListDirectory(volume, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(96)), MaxDirectoryBytes, false);
                foreach (var path in paths)
                {
                    var hit = Find(volume, root, path.Split('/'));
                    if (hit != null) return hit;
                }
                foreach (var top in root.Where(e => e.IsDirectory).Take(4))
                {
                    var inner = ListDirectory(volume, top.FirstCluster, top.Length, top.Contiguous);
                    foreach (var path in paths)
                    {
                        var hit = Find(volume, inner, path.Split('/'));
                        if (hit != null) return hit;
                    }
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static byte[]? Find(Volume volume, List<Entry> directory, string[] parts)
        {
            var current = directory;
            for (int i = 0; i < parts.Length; i++)
            {
                var entry = current.FirstOrDefault(e => e.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                if (entry == null) return null;
                if (i == parts.Length - 1)
                    return entry.IsDirectory || entry.Length > MaxFileBytes ? null : Read(volume, entry.FirstCluster, entry.Length, entry.Contiguous);
                if (!entry.IsDirectory) return null;
                current = ListDirectory(volume, entry.FirstCluster, entry.Length, entry.Contiguous);
            }
            return null;
        }

        // File (0x85), stream extension (0xC0) and name (0xC1) entries; 0x00 ends the directory
        private static List<Entry> ListDirectory(Volume volume, uint firstCluster, long length, bool contiguous)
        {
            var data = Read(volume, firstCluster, Math.Min(length, MaxDirectoryBytes), contiguous);
            var entries = new List<Entry>();
            for (int i = 0; i + 32 <= data.Length; i += 32)
            {
                var type = data[i];
                if (type == 0x00) break;
                if (type != 0x85) continue;
                int secondary = data[i + 1];
                var attributes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 4));
                int s = i + 32;
                if (secondary < 2 || s + 32 > data.Length || data[s] != 0xC0) continue;
                var noFatChain = (data[s + 1] & 0x02) != 0;
                int nameLength = data[s + 3];
                var cluster = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(s + 20));
                var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(s + 24));
                var name = new StringBuilder();
                for (int n = 1; n < secondary && s + 32 * n + 32 <= data.Length; n++)
                {
                    int e = s + 32 * n;
                    if (data[e] != 0xC1) break;
                    name.Append(Encoding.Unicode.GetString(data, e + 2, 30));
                }
                entries.Add(new Entry(name.ToString(0, Math.Min(nameLength, name.Length)), (attributes & 0x10) != 0, cluster, size, noFatChain));
                i += 32 * secondary;
            }
            return entries;
        }

        private static byte[] Read(Volume volume, uint firstCluster, long length, bool contiguous)
        {
            using var result = new MemoryStream();
            var buffer = new byte[volume.ClusterSize];
            var cluster = firstCluster;
            // A FAT-chained directory gives no length: read until the chain ends
            var left = length > 0 ? length : MaxDirectoryBytes;
            for (int guard = 0; left > 0 && cluster >= 2 && cluster < volume.ClusterCount + 2 && guard < 100000; guard++)
            {
                volume.Stream.Position = volume.HeapOffset + (cluster - 2L) * volume.ClusterSize;
                var take = (int)Math.Min(left, volume.ClusterSize);
                volume.Stream.ReadExactly(buffer, 0, take);
                result.Write(buffer, 0, take);
                left -= take;
                if (contiguous) cluster++;
                else
                {
                    var next = new byte[4];
                    volume.Stream.Position = volume.FatOffset + cluster * 4L;
                    volume.Stream.ReadExactly(next);
                    cluster = BinaryPrimitives.ReadUInt32LittleEndian(next);
                }
            }
            return result.ToArray();
        }
    }

    /// <summary>A PS4 param.sfo (PSF): TITLE, TITLE_ID, APP_VER, CATEGORY, CONTENT_ID.</summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public static class ParamSfo
    {
        public static Dictionary<string, string> Parse(byte[] data)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (data.Length < 20 || data[0] != 0 || data[1] != 'P' || data[2] != 'S' || data[3] != 'F') return values;
                var keys = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
                var table = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
                var count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)), 1000u);
                for (int i = 0; i < count && 20 + 16 * i + 16 <= data.Length; i++)
                {
                    var e = data.AsSpan(20 + 16 * i);
                    var keyAt = keys + BinaryPrimitives.ReadUInt16LittleEndian(e);
                    var format = BinaryPrimitives.ReadUInt16LittleEndian(e[2..]);
                    var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
                    var at = table + (int)BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
                    var end = Array.IndexOf(data, (byte)0, keyAt);
                    if (keyAt >= data.Length || end < 0 || at + length > data.Length) continue;
                    var key = Encoding.ASCII.GetString(data, keyAt, end - keyAt);
                    values[key] = format == 0x0404
                        ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : Encoding.UTF8.GetString(data, at, length).TrimEnd('\0').Trim();
                }
            }
            catch (Exception)
            {
                // A damaged file gives what was read before the damage
            }
            return values;
        }
    }

    /// <summary>
    /// The content ID and the param.sfo of a PS4 package (big-endian "\x7FCNT" header, entry 0x1000), read without
    /// decrypting anything. A PS5 package keeps its metadata encrypted; for those only the file name speaks.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public static class PkgReader
    {
        public static (string? ContentId, Dictionary<string, string> Sfo) Read(string file)
        {
            var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var header = new byte[0x80];
                stream.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0x7F434E54) return (null, empty);
                var contentId = Encoding.ASCII.GetString(header, 0x40, 0x24).TrimEnd('\0').Trim();
                var count = Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x10)), 10000u);
                stream.Position = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x18));
                var table = new byte[count * 32];
                stream.ReadExactly(table);
                for (int i = 0; i < count; i++)
                {
                    var e = table.AsSpan(i * 32);
                    if (BinaryPrimitives.ReadUInt32BigEndian(e) != 0x1000) continue;
                    var size = BinaryPrimitives.ReadUInt32BigEndian(e[20..]);
                    if (size > 1024 * 1024) break;
                    stream.Position = BinaryPrimitives.ReadUInt32BigEndian(e[16..]);
                    var sfo = new byte[size];
                    stream.ReadExactly(sfo);
                    return (NullIfEmpty(contentId), ParamSfo.Parse(sfo));
                }
                return (NullIfEmpty(contentId), empty);
            }
            catch (Exception)
            {
                return (null, empty);
            }
        }

        private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    }
}
