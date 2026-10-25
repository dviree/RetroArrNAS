using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RetroArr.Core.Games
{
    public enum PlayStationFormat
    {
        Unknown,
        // An app0 game folder: eboot.bin and sce_sys/param.json, as the console reads it
        Folder,
        // The same folder after LZ4 compression (ampr_assets*, *.lz4): never to be repacked or converted
        Lz4Folder,
        ExfatImage,
        FfpfscImage,
        Pkg,
        // RAR/7z/zip still to be unpacked
        Archive
    }

    /// <summary>What a PS4/PS5 release on disk is, and the title ID, version and name its param.json gives.</summary>
    public sealed record PlayStationPackage(PlayStationFormat Format, string? TitleId, string? Version, string? Title, string? GameFolder)
    {
        public bool IsCompressed => Format is PlayStationFormat.Lz4Folder or PlayStationFormat.FfpfscImage;
    }

    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public static class PlayStationPackageInspector
    {
        private static readonly Regex TitleIdRegex = new(@"\b(PPSA|CUSA|ECAS)\d{5}\b", RegexOptions.IgnoreCase);
        private static readonly Regex ArchiveRegex = new(@"\.(rar|7z|zip|r\d\d|\d{3})$", RegexOptions.IgnoreCase);

        public static PlayStationPackage Inspect(string path)
        {
            var idFromName = TitleIdRegex.Match(Path.GetFileName(path.TrimEnd('/', '\\'))).Value.ToUpperInvariant();
            if (File.Exists(path)) return ForFile(path, idFromName);
            if (!Directory.Exists(path)) return new(PlayStationFormat.Unknown, NullIfEmpty(idFromName), null, null, null);

            // GameSauce's rule: an LZ4 name anywhere in the folder means the folder is compressed
            if (Path.GetFileName(path).Contains("lz4", StringComparison.OrdinalIgnoreCase) || HasLz4Marker(path))
            {
                var root = FindGameRoot(path);
                var meta = root != null ? ReadParam(root) : default;
                return new(PlayStationFormat.Lz4Folder, meta.Id ?? NullIfEmpty(idFromName), meta.Version, meta.Title, root);
            }

            var gameRoot = FindGameRoot(path);
            if (gameRoot != null)
            {
                var (id, version, title) = ReadParam(gameRoot);
                return new(PlayStationFormat.Folder, id ?? NullIfEmpty(idFromName), version, title, gameRoot);
            }

            // A folder holding an image, a package or an archive is that
            var files = Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 })
                .Take(2000).ToList();
            foreach (var format in new[] { PlayStationFormat.FfpfscImage, PlayStationFormat.ExfatImage, PlayStationFormat.Pkg, PlayStationFormat.Archive })
            {
                var hit = files.FirstOrDefault(f => ForFile(f, idFromName).Format == format);
                if (hit != null)
                    return ForFile(hit, NullIfEmpty(idFromName) ?? TitleIdRegex.Match(Path.GetFileName(hit)).Value.ToUpperInvariant()) with { GameFolder = path };
            }
            return new(PlayStationFormat.Unknown, NullIfEmpty(idFromName), null, null, null);
        }

        private static PlayStationPackage ForFile(string file, string idFromName)
        {
            var name = Path.GetFileName(file);
            var id = NullIfEmpty(idFromName) ?? NullIfEmpty(TitleIdRegex.Match(name).Value.ToUpperInvariant());
            var format = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".ffpfsc" => PlayStationFormat.FfpfscImage,
                ".exfat" => PlayStationFormat.ExfatImage,
                ".pkg" => PlayStationFormat.Pkg,
                ".lz4" => PlayStationFormat.Lz4Folder,
                _ => ArchiveRegex.IsMatch(name) ? PlayStationFormat.Archive : PlayStationFormat.Unknown
            };
            return new(format, id, null, null, null);
        }

        private static bool HasLz4Marker(string folder) =>
            Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Select(Path.GetFileName)
                .Any(n => n != null && (n.StartsWith("ampr_assets", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("lz4", StringComparison.OrdinalIgnoreCase)));

        // The folder itself, or the single *-app0 (or other) folder in it that holds eboot.bin and sce_sys/param.json
        private static string? FindGameRoot(string folder)
        {
            static bool IsRoot(string d) => File.Exists(Path.Combine(d, "eboot.bin")) && File.Exists(Path.Combine(d, "sce_sys", "param.json"));
            if (IsRoot(folder)) return folder;
            var inner = Directory.EnumerateDirectories(folder).Where(IsRoot).ToList();
            return inner.Count == 1 ? inner[0] : null;
        }

        // PS5 param.json: titleId, contentVersion ("01.010.000"), localizedParameters.<defaultLanguage>.titleName
        private static (string? Id, string? Version, string? Title) ReadParam(string gameRoot)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(gameRoot, "sce_sys", "param.json")));
                var root = doc.RootElement;
                string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                string? title = null;
                if (root.TryGetProperty("localizedParameters", out var loc) && loc.ValueKind == JsonValueKind.Object)
                {
                    var lang = Str(loc, "defaultLanguage") ?? "en-US";
                    if (loc.TryGetProperty(lang, out var l) || loc.TryGetProperty("en-US", out l))
                        title = Str(l, "titleName");
                }
                return (Str(root, "titleId")?.ToUpperInvariant(), Str(root, "contentVersion"), title);
            }
            catch (Exception)
            {
                return (null, null, null);
            }
        }

        /// <summary>
        /// Compares versions as the scene writes them: "01.010.000", "v02.002.002", "v1.5". Positive when a is newer.
        /// </summary>
        public static int CompareVersions(string? a, string? b)
        {
            static List<int> Parts(string? v) => Regex.Matches(v ?? string.Empty, @"\d+").Select(m => int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            var x = Parts(a);
            var y = Parts(b);
            for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
            {
                var c = (i < x.Count ? x[i] : 0).CompareTo(i < y.Count ? y[i] : 0);
                if (c != 0) return c;
            }
            return 0;
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
