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
        // For an archive: what its table of contents shows inside, read without unpacking anything
        public PlayStationFormat Inside { get; init; } = PlayStationFormat.Unknown;

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
                // An archive set is read through its first volume
                var candidates = format == PlayStationFormat.Archive ? files.OrderBy(f => FirstVolumeRank(f)).ToList() : files;
                var hit = candidates.FirstOrDefault(f => ForFile(f, idFromName).Format == format);
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
            if (format != PlayStationFormat.Archive) return new(format, id, null, null, null);
            var (inside, innerId) = PeekArchive(file);
            return new(format, id ?? innerId, null, null, null) { Inside = inside };
        }

        // The entry names in an archive's table of contents, by the same rules as a folder. Nothing is unpacked.
        internal static (PlayStationFormat Inside, string? TitleId) PeekArchive(string file)
        {
            try
            {
                using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(file);
                var names = archive.Entries.Select(e => e.Key ?? string.Empty).Take(100000).ToList();
                var id = names.Select(n => TitleIdRegex.Match(n).Value.ToUpperInvariant()).FirstOrDefault(v => v.Length > 0);
                bool Any(Func<string, bool> f) => names.Any(n => f(n.Replace('\\', '/')));
                PlayStationFormat inside;
                if (Any(n => n.Split('/').Any(p => p.StartsWith("ampr_assets", StringComparison.OrdinalIgnoreCase) || p.Contains("lz4", StringComparison.OrdinalIgnoreCase))))
                    inside = PlayStationFormat.Lz4Folder;
                else if (Any(n => n.EndsWith("sce_sys/param.json", StringComparison.OrdinalIgnoreCase)) && Any(n => n.EndsWith("eboot.bin", StringComparison.OrdinalIgnoreCase)))
                    inside = PlayStationFormat.Folder;
                else if (Any(n => n.EndsWith(".ffpfsc", StringComparison.OrdinalIgnoreCase))) inside = PlayStationFormat.FfpfscImage;
                else if (Any(n => n.EndsWith(".exfat", StringComparison.OrdinalIgnoreCase))) inside = PlayStationFormat.ExfatImage;
                else if (Any(n => n.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))) inside = PlayStationFormat.Pkg;
                else inside = PlayStationFormat.Unknown;
                return (inside, id);
            }
            catch (Exception)
            {
                // A later volume, a damaged or encrypted archive: its contents stay unknown
                return (PlayStationFormat.Unknown, null);
            }
        }

        private static int FirstVolumeRank(string f)
        {
            var n = Path.GetFileName(f).ToLowerInvariant();
            if (Regex.IsMatch(n, @"\.part0*1\.rar$") || (n.EndsWith(".rar") && !Regex.IsMatch(n, @"\.part\d+\.rar$"))) return 0;
            if (n.EndsWith(".7z") || n.EndsWith(".zip") || n.EndsWith(".001")) return 1;
            return 2;
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
