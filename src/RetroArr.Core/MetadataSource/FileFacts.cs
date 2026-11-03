using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RetroArr.Core.Games;

namespace RetroArr.Core.MetadataSource
{
    /// <summary>
    /// What a game's files say about themselves, read from inside them, for the AI: a PS4/PS5 PKG's content ID, title ID,
    /// own name and version, a PS image's param.json, an ISO's volume name, the game code and title in a GameCube/Wii,
    /// NDS, GBA or N64 header. A file name can mislead ("SABAEVEN" read as "Sable"); this does not.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public static class FileFacts
    {
        /// <summary>One line per file (the largest few of a folder): its name, size and what was read inside it.</summary>
        public static List<string> Describe(string? path, int maxFiles = 5)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(path)) return lines;
            try
            {
                IEnumerable<string> files = File.Exists(path) ? new[] { path }
                    : Directory.Exists(path)
                        ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 })
                            .Take(2000).OrderByDescending(f => new FileInfo(f).Length).Take(maxFiles)
                        : Enumerable.Empty<string>();
                var root = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
                foreach (var file in files)
                {
                    var facts = Read(file);
                    lines.Add($"{Path.GetRelativePath(root, file)} ({Size(new FileInfo(file).Length)})" + (facts.Length > 0 ? ": " + facts : ""));
                }
            }
            catch (Exception) { }
            return lines;
        }

        /// <summary>What is read from inside one file, or an empty string.</summary>
        internal static string Read(string file)
        {
            try
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".pkg" or ".exfat" or ".ffpfsc") return PlayStation(file);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var head = new byte[(int)Math.Min(stream.Length, 0x8080)];
                stream.ReadExactly(head);
                return ext switch
                {
                    ".nds" when head.Length >= 0x10 => Join(("title", Ascii(head, 0, 12)), ("game code", Ascii(head, 0xC, 4))),
                    ".gba" when head.Length >= 0xB0 => Join(("title", Ascii(head, 0xA0, 12)), ("game code", Ascii(head, 0xAC, 4))),
                    ".z64" when head.Length >= 0x40 => Join(("title", Ascii(head, 0x20, 20)), ("game code", Ascii(head, 0x3B, 4))),
                    ".iso" or ".gcm" or ".wbfs" when IsNintendoDisc(head) => Join(("game ID", Ascii(head, 0, 6)), ("title", Ascii(head, 0x20, 64))),
                    ".iso" when head.Length >= 0x8048 && Encoding.ASCII.GetString(head, 0x8001, 5) == "CD001" => Join(("volume", Ascii(head, 0x8028, 32))),
                    _ => string.Empty,
                };
            }
            catch (Exception) { return string.Empty; }
        }

        private static string PlayStation(string file)
        {
            var p = PlayStationPackageInspector.Inspect(file);
            var kind = p.Format == PlayStationFormat.Pkg
                ? (Ps5Magic(file) ? "PS5 PKG" : "PKG")
                : p.Format.ToString();
            return Join(("format", kind), ("title ID", p.TitleId), ("content ID", p.ContentId), ("its own name", p.Title), ("version", p.Version),
                ("kind", p.Content == PlayStationContent.Game ? null : p.Content.ToString()));
        }

        private static bool Ps5Magic(string file)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var magic = new byte[4];
                return stream.Read(magic, 0, 4) == 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(magic) == Ps5PkgReader.Magic;
            }
            catch (Exception) { return false; }
        }

        // GameCube (magic C2339F3D at 0x1C) or Wii (5D1C9EA3 at 0x18)
        private static bool IsNintendoDisc(byte[] head) =>
            head.Length >= 0x60 && (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x1C)) == 0xC2339F3D
                                    || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x18)) == 0x5D1C9EA3);

        private static string Ascii(byte[] data, int offset, int length)
        {
            if (offset + length > data.Length) return string.Empty;
            var s = Encoding.ASCII.GetString(data, offset, length).TrimEnd('\0', ' ');
            // Only text a person would read: printable ASCII
            return s.Length > 0 && s.All(c => c >= 0x20 && c < 0x7F) ? s : string.Empty;
        }

        private static string Join(params (string Key, string? Value)[] facts) =>
            string.Join(", ", facts.Where(f => !string.IsNullOrWhiteSpace(f.Value)).Select(f => $"{f.Key} {f.Value}"));

        private static string Size(long bytes) => bytes >= 1L << 30
            ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }
}
