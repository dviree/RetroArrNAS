using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
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

    public enum PlayStationContent { Game, Update, Dlc }

    /// <summary>What a PS4/PS5 release on disk is, and the title ID, version and name its param.json gives.</summary>
    public sealed record PlayStationPackage(PlayStationFormat Format, string? TitleId, string? Version, string? Title, string? GameFolder)
    {
        // For an archive: what its table of contents shows inside, read without unpacking anything
        public PlayStationFormat Inside { get; init; } = PlayStationFormat.Unknown;

        // The image or package file itself, also when a folder holding it was inspected
        public string? ImageFile { get; init; }

        public PlayStationContent Content { get; init; } = PlayStationContent.Game;

        // EP5291-PPSA34547_00-0210080241603648: from a package header or its file name
        public string? ContentId { get; init; }

        public bool IsCompressed => Format is PlayStationFormat.Lz4Folder or PlayStationFormat.FfpfscImage;
    }

    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public static class PlayStationPackageInspector
    {
        // Also inside a content ID, where an underscore follows: UP9000-PPSA26344_00-GHOST2SHIP000000
        private static readonly Regex TitleIdRegex = new(@"(?<![A-Za-z0-9])(PPSA|CUSA|ECAS)[-_]?(\d{5})(?!\d)", RegexOptions.IgnoreCase);
        private static readonly Regex ContentIdRegex = new(@"[A-Z]{2}\d{4}-(?:PPSA|CUSA)\d{5}_\d{2}-[A-Z0-9]{16}", RegexOptions.IgnoreCase);
        private static readonly Regex UpdateWords = new(@"(?<![A-Za-z])(update|patch)(?![A-Za-z])", RegexOptions.IgnoreCase);
        private static readonly Regex DlcWords = new(@"(?<![A-Za-z])DLC(?![A-Za-z])", RegexOptions.IgnoreCase);

        /// <summary>PPSA01234 out of a name, whatever separates it ("PPSA-01234", "PPSA01234_00"), or an empty string.</summary>
        public static string TitleIdIn(string? name)
        {
            var m = TitleIdRegex.Match(name ?? string.Empty);
            return m.Success ? (m.Groups[1].Value + m.Groups[2].Value).ToUpperInvariant() : string.Empty;
        }
        private static readonly Regex ArchiveRegex = new(@"\.(rar|7z|zip|r\d\d|\d{3})$", RegexOptions.IgnoreCase);

        public static PlayStationPackage Inspect(string path)
        {
            var trimmed = path.TrimEnd('/', '\\');
            // A file called game.ffpfsc says nothing; the folder JDownloader put it in does
            var idFromName = TitleIdIn(Path.GetFileName(trimmed));
            if (idFromName.Length == 0 && File.Exists(path)) idFromName = TitleIdIn(Path.GetFileName(Path.GetDirectoryName(trimmed)));
            if (File.Exists(path)) return ForFile(path, idFromName);
            if (!Directory.Exists(path)) return new(PlayStationFormat.Unknown, NullIfEmpty(idFromName), null, null, null);

            // An LZ4 name anywhere in the folder means the folder is compressed
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
            // A backport (a PKG patched for older firmware, "Backport 4.xx/...") is kept apart, never the game itself
            var outside = files.Where(f => !Path.GetRelativePath(path, f).Split(Path.DirectorySeparatorChar).Any(p => p.Contains("backport", StringComparison.OrdinalIgnoreCase))).ToList();
            if (outside.Count > 0) files = outside;
            foreach (var format in new[] { PlayStationFormat.FfpfscImage, PlayStationFormat.ExfatImage, PlayStationFormat.Pkg, PlayStationFormat.Archive })
            {
                // An archive set is read through its first volume
                var candidates = format == PlayStationFormat.Archive ? files.OrderBy(f => FirstVolumeRank(f)).ToList() : files;
                var hit = candidates.FirstOrDefault(f => FormatOf(Path.GetFileName(f)) == format);
                if (hit != null)
                    return ForFile(hit, NullIfEmpty(idFromName) ?? TitleIdIn(Path.GetFileName(hit))) with { GameFolder = path };
            }
            return new(PlayStationFormat.Unknown, NullIfEmpty(idFromName), null, null, null);
        }

        private static PlayStationPackage ForFile(string file, string idFromName)
        {
            var name = Path.GetFileName(file);
            var id = NullIfEmpty(idFromName) ?? NullIfEmpty(TitleIdIn(name));
            var contentId = NullIfEmpty(ContentIdRegex.Match(name).Value.ToUpperInvariant());
            var format = FormatOf(name);
            var content = DlcWords.IsMatch(name) ? PlayStationContent.Dlc : UpdateWords.IsMatch(name) ? PlayStationContent.Update : PlayStationContent.Game;
            switch (format)
            {
                case PlayStationFormat.ExfatImage:
                {
                    // The image's own param.json (PS5) or param.sfo (PS4), read without mounting it
                    var data = ExFatImageReader.ReadFirst(file, "sce_sys/param.json", "sce_sys/param.sfo");
                    var meta = data == null ? default : data.Length > 0 && data[0] == 0 ? FromSfo(ParamSfo.Parse(data)) : ParseParamJson(Encoding.UTF8.GetString(data));
                    return new(format, meta.Id ?? id, meta.Version, meta.Title, null) { ImageFile = file, Content = content, ContentId = contentId };
                }
                case PlayStationFormat.Pkg:
                {
                    var (headerId, sfo) = PkgReader.Read(file);
                    var meta = FromSfo(sfo);
                    content = sfo.TryGetValue("CATEGORY", out var category) && category.StartsWith("gp", StringComparison.OrdinalIgnoreCase) ? PlayStationContent.Update
                        : category?.StartsWith("ac", StringComparison.OrdinalIgnoreCase) == true ? PlayStationContent.Dlc
                        : sfo.Count > 0 ? PlayStationContent.Game : content;
                    contentId = NullIfEmpty(headerId?.ToUpperInvariant()) ?? contentId;
                    return new(format, meta.Id ?? id ?? NullIfEmpty(TitleIdIn(contentId)), meta.Version, meta.Title, null) { ImageFile = file, Content = content, ContentId = contentId };
                }
                case PlayStationFormat.Archive:
                {
                    var (inside, innerId) = PeekArchive(file);
                    return new(format, id ?? innerId, null, null, null) { Inside = inside, ImageFile = file, Content = content };
                }
                default:
                    return new(format, id, null, null, null) { ImageFile = format == PlayStationFormat.Unknown ? null : file, Content = content, ContentId = contentId };
            }
        }

        private static PlayStationFormat FormatOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".ffpfsc" => PlayStationFormat.FfpfscImage,
            ".exfat" => PlayStationFormat.ExfatImage,
            ".pkg" => PlayStationFormat.Pkg,
            ".lz4" => PlayStationFormat.Lz4Folder,
            _ => ArchiveRegex.IsMatch(name) ? PlayStationFormat.Archive : PlayStationFormat.Unknown
        };

        private static (string? Id, string? Version, string? Title) FromSfo(Dictionary<string, string> sfo)
        {
            string? Get(string key) => sfo.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
            return (Get("TITLE_ID")?.ToUpperInvariant(), Get("APP_VER") ?? Get("VERSION"), Get("TITLE"));
        }

        // The entry names in an archive's table of contents, by the same rules as a folder. Nothing is unpacked.
        internal static (PlayStationFormat Inside, string? TitleId) PeekArchive(string file)
        {
            try
            {
                using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(file);
                var names = archive.Entries.Select(e => e.Key ?? string.Empty).Take(100000).ToList();
                var id = names.Select(TitleIdIn).FirstOrDefault(v => v.Length > 0);
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
            static bool IsRoot(string d) => File.Exists(Path.Combine(d, "eboot.bin"))
                && (File.Exists(Path.Combine(d, "sce_sys", "param.json")) || File.Exists(Path.Combine(d, "sce_sys", "param.sfo")));
            if (IsRoot(folder)) return folder;
            var inner = Directory.EnumerateDirectories(folder).Where(IsRoot).ToList();
            return inner.Count == 1 ? inner[0] : null;
        }

        // PS5 param.json, or a PS4 folder's param.sfo
        private static (string? Id, string? Version, string? Title) ReadParam(string gameRoot)
        {
            try
            {
                var json = Path.Combine(gameRoot, "sce_sys", "param.json");
                if (File.Exists(json)) return ParseParamJson(File.ReadAllText(json));
                var sfo = Path.Combine(gameRoot, "sce_sys", "param.sfo");
                return File.Exists(sfo) ? FromSfo(ParamSfo.Parse(File.ReadAllBytes(sfo))) : default;
            }
            catch (Exception)
            {
                return (null, null, null);
            }
        }

        // titleId, contentVersion ("01.010.000"), localizedParameters.<defaultLanguage>.titleName
        internal static (string? Id, string? Version, string? Title) ParseParamJson(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
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
