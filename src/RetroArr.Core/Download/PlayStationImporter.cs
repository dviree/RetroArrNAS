using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RetroArr.Core.Games;
using RetroArr.Core.IO;
using RetroArr.Core.MetadataSource;

namespace RetroArr.Core.Download
{
    /// <summary>
    /// Brings a finished PS4/PS5 release into the library the way the NAS's PS organizer and PS5 finalizer did:
    /// the name comes from the game's own param.json/param.sfo (read out of folders, exFAT images and PS4 packages
    /// without mounting anything), then the AI, then the release name; it lands as "Title [PPSA01234] [v1.2]" in
    /// &lt;library&gt;/ps5 (or ps4), and a newer version replaces an older one, which goes to the trash. Nothing is converted.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public class PlayStationImporter
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetLogger(Logging.AppLoggerService.DownloadsImport);

        // Names stay well inside what exFAT, SMB and the PS5 itself accept
        private const int MaxNameBytes = 120;
        private const string StagingPrefix = ".retroarr-ps-";

        // Unpacked: the release was an archive, unpacked to staging and imported from there; the archive itself
        // has no twin in the library to verify against, a clean unpack and import is its verification
        public sealed record Outcome(bool Success, string? Destination, string? Reason, string? Title = null, string? VerifyFrom = null, bool Unpacked = false)
        {
            public static Outcome Fail(string reason) => new(false, null, reason);
        }

        private readonly IFileMoverService _mover;
        private readonly AiTitleService? _ai;
        private readonly TrashService? _trash;
        private readonly IArchiveService? _archives;
        private readonly string _staging;

        // internal for tests: a full volume
        internal Func<string, long> FreeSpace = path => new DriveInfo(path).AvailableFreeSpace;

        public PlayStationImporter(IFileMoverService mover, AiTitleService? ai = null, TrashService? trash = null, IArchiveService? archives = null, string? staging = null)
        {
            _mover = mover;
            _ai = ai;
            _trash = trash;
            _archives = archives;
            _staging = staging ?? Environment.GetEnvironmentVariable("PS_STAGING") ?? "/temp/retroarr-ps";
        }

        /// <summary>
        /// Uncompressed and LZ4 game folders are imported only when PS_IMPORT_FOLDERS=true: by default they are left where
        /// they are for the converter that turns them into exFAT/FFPFSC images, and the images are what gets imported.
        /// </summary>
        public static bool ImportFolders =>
            string.Equals(Environment.GetEnvironmentVariable("PS_IMPORT_FOLDERS"), "true", StringComparison.OrdinalIgnoreCase);

        public static bool IsPlayStation(string? platformFolder) =>
            string.Equals(platformFolder, "ps5", StringComparison.OrdinalIgnoreCase) || string.Equals(platformFolder, "ps4", StringComparison.OrdinalIgnoreCase);

        public Task<Outcome> ImportAsync(string source, string releaseName, string libraryRoot, string platformFolder)
        {
            var package = PlayStationPackageInspector.Inspect(source);
            return package.Format == PlayStationFormat.Archive
                ? ImportArchiveAsync(source, releaseName, libraryRoot, platformFolder)
                : ImportOneAsync(source, releaseName, libraryRoot, platformFolder, ImportFolders);
        }

        private static readonly Regex ArchiveFile = new(@"\.(rar|7z|zip|001)$", RegexOptions.IgnoreCase);
        private static readonly Regex LaterRarPart = new(@"\.part0*([2-9]|\d{2,})\.rar$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Unpacks every archive set in the release (first volumes only) into staging, imports what came out,
        /// and removes the staging folder again. The archives themselves are only read.
        /// </summary>
        private async Task<Outcome> ImportArchiveAsync(string source, string releaseName, string libraryRoot, string platformFolder)
        {
            if (_archives == null) return Outcome.Fail("PlayStation archive: no archive support configured");
            var all = Directory.Exists(source)
                ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList()
                : new List<string> { source };
            var firsts = all.Where(f => ArchiveFile.IsMatch(f) && !LaterRarPart.IsMatch(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (firsts.Count == 0) return Outcome.Fail("PlayStation archive: no first volume found");

            var needed = all.Where(f => ArchiveFile.IsMatch(f) || Regex.IsMatch(f, @"\.(r\d\d|\d{3})$", RegexOptions.IgnoreCase)).Sum(f => new FileInfo(f).Length);
            Directory.CreateDirectory(_staging);
            var free = FreeSpace(_staging);
            if (free < needed + needed / 20)
                return Outcome.Fail($"Not enough space to unpack: {needed / 1_000_000_000.0:0.0} GB needed in {_staging}, {free / 1_000_000_000.0:0.0} GB free");

            var work = Path.Combine(_staging, Guid.NewGuid().ToString("N")[..12]);
            try
            {
                for (int i = 0; i < firsts.Count; i++)
                {
                    var into = Path.Combine(work, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    _logger.Info($"[PlayStation] Unpacking {firsts[i]} -> {into}");
                    if (!await Task.Run(() => _archives.Extract(firsts[i], into)))
                        return Outcome.Fail($"Could not unpack {Path.GetFileName(firsts[i])} (damaged, incomplete or password protected)");
                }

                var found = Importables(work).ToList();
                if (found.Count == 0) return Outcome.Fail("The archive holds no PlayStation game, image or package");
                Outcome? first = null;
                foreach (var item in found)
                {
                    // An unpacked folder is in staging, out of the converter's way: it is imported as it is
                    var name = item == work ? releaseName : Path.GetFileName(item);
                    var outcome = await ImportOneAsync(item, found.Count == 1 ? releaseName : name, libraryRoot, platformFolder, true);
                    if (!outcome.Success) return outcome with { Reason = $"{Path.GetFileName(item)}: {outcome.Reason}" };
                    first ??= outcome;
                }
                return first! with { VerifyFrom = null, Unpacked = true };
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); }
                catch (Exception ex) { _logger.Warn($"[PlayStation] Could not remove staging folder {work}: {ex.Message}"); }
            }
        }

        // Game folders (eboot.bin + sce_sys) and image/package files in an unpacked archive
        private static IEnumerable<string> Importables(string root)
        {
            var folders = Directory.EnumerateFiles(root, "eboot.bin", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).Where(d => d != null && Directory.Exists(Path.Combine(d, "sce_sys"))).Select(d => d!).ToList();
            foreach (var folder in folders) yield return folder;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".exfat" or ".ffpfsc" or ".pkg" && !folders.Any(f => file.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                    yield return file;
            }
        }

        private async Task<Outcome> ImportOneAsync(string source, string releaseName, string libraryRoot, string platformFolder, bool importFolders)
        {
            var package = PlayStationPackageInspector.Inspect(source);
            switch (package.Format)
            {
                case PlayStationFormat.Archive:
                    return Outcome.Fail("An archive inside an archive is not unpacked");
                case PlayStationFormat.Unknown:
                    return Outcome.Fail("No PlayStation game, image or package found");
                case PlayStationFormat.Folder or PlayStationFormat.Lz4Folder when !importFolders:
                    return Outcome.Fail("Game folder left for the converter (set PS_IMPORT_FOLDERS=true to import folders)");
            }

            var isFolderGame = package.Format is PlayStationFormat.Folder or PlayStationFormat.Lz4Folder;
            var from = isFolderGame ? package.GameFolder ?? source : package.ImageFile;
            if (from == null) return Outcome.Fail("No PlayStation game, image or package found");
            if (!isFolderGame && Directory.Exists(source))
            {
                var ext = Path.GetExtension(from);
                var images = Directory.EnumerateFiles(source, "*" + ext, SearchOption.AllDirectories).Count();
                if (images > 1) return Outcome.Fail($"{images} {ext} files in one folder: add the folder as a recursive watch so each is its own game");
            }

            var folder = Path.Combine(libraryRoot, platformFolder);
            Directory.CreateDirectory(folder);
            var title = await TitleForAsync(package, source, releaseName, from, folder);
            var extension = isFolderGame ? string.Empty : Path.GetExtension(from).ToLowerInvariant();
            var name = BuildName(title, package, extension);
            var target = Path.Combine(folder, name);

            // The same game already in the library: a newer or equal version keeps its place, an older one is replaced
            var older = new List<string>();
            foreach (var existing in SameGame(folder, package, name))
            {
                var existingVersion = VersionIn(Path.GetFileName(existing));
                var compare = PlayStationPackageInspector.CompareVersions(existingVersion, package.Version);
                var sameKind = Directory.Exists(existing) == isFolderGame
                    && string.Equals(Path.GetExtension(existing), extension, StringComparison.OrdinalIgnoreCase);
                if (compare > 0)
                    return Outcome.Fail($"The library already has a newer version: {Path.GetFileName(existing)}");
                if (compare == 0 && sameKind)
                {
                    if (SizeOf(existing) == SizeOf(from))
                    {
                        _logger.Info($"[PlayStation] {releaseName} is already in the library as {existing}");
                        return new Outcome(true, existing, null, title, from);
                    }
                    return Outcome.Fail($"The same version is already in the library with a different size: {Path.GetFileName(existing)}");
                }
                if (compare < 0) older.Add(existing);
            }
            if (File.Exists(target) || Directory.Exists(target))
                return Outcome.Fail($"{name} is already in the library and is not the same release");

            var staging = Path.Combine(folder, StagingPrefix + Guid.NewGuid().ToString("N")[..8] + extension);
            try
            {
                var error = isFolderGame ? CopyFolder(from, staging) : CopyFile(from, staging);
                if (error != null) return Outcome.Fail(error);
                if (isFolderGame) Directory.Move(staging, target);
                else File.Move(staging, target);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                else if (File.Exists(staging)) File.Delete(staging);
            }
            _logger.Info($"[PlayStation] Imported {from} -> {target}");

            foreach (var old in older)
            {
                if (_trash == null)
                {
                    _logger.Warn($"[PlayStation] No trash configured, older version stays: {old}");
                    continue;
                }
                try
                {
                    await _trash.MoveAsync(old, null, title);
                    _logger.Info($"[PlayStation] Older version to the trash: {old}");
                }
                catch (Exception ex) { _logger.Warn($"[PlayStation] Could not move older version {old} to the trash: {ex.Message}"); }
            }
            return new Outcome(true, target, null, title, from);
        }

        // param.json/param.sfo first, then the name the library already gives the title ID, then the AI, then the release name
        private async Task<string> TitleForAsync(PlayStationPackage package, string source, string releaseName, string from, string folder)
        {
            var own = SafeTitle(package.Title);
            if (own.Length > 0) return own;
            if (package.TitleId != null)
            {
                var tag = " [" + package.TitleId + "]";
                var known = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName)
                    .Where(n => n != null && !n.StartsWith('.') && n.Contains(tag, StringComparison.OrdinalIgnoreCase))
                    .Select(n => n![..n!.IndexOf(tag, StringComparison.OrdinalIgnoreCase)].Trim())
                    .FirstOrDefault(t => t.Length > 0);
                if (known != null) return known;
            }
            if (_ai is { IsEnabled: true })
            {
                var files = Directory.Exists(source)
                    ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Take(50).Select(f => Path.GetRelativePath(source, f))
                    : new[] { Path.GetFileName(source) };
                var hint = string.Join(" ", new[] { releaseName, package.ContentId, package.TitleId }.Where(s => !string.IsNullOrEmpty(s)).Distinct());
                var guess = await _ai.IdentifyAsync(hint, files);
                if (guess != null && IsPlayStation(guess.Platform) && SafeTitle(guess.Title).Length > 0) return SafeTitle(guess.Title);
            }
            var stem = File.Exists(source) ? Path.GetFileNameWithoutExtension(releaseName) : releaseName;
            var cleaned = CleanReleaseName(stem);
            if (cleaned.Length == 0) cleaned = CleanReleaseName(Path.GetFileNameWithoutExtension(from));
            if (cleaned.Length == 0 && File.Exists(source)) cleaned = CleanReleaseName(Path.GetFileName(Path.GetDirectoryName(source)));
            return cleaned.Length > 0 ? cleaned : package.TitleId ?? "PlayStation Game";
        }

        /// <summary>"Title [PPSA01234] [v1.2].exfat", "Title [CUSA01234] [Update] [v1.05].pkg", "Title [PPSA01234] [DLC LABEL].pkg".</summary>
        internal static string BuildName(string title, PlayStationPackage package, string extension)
        {
            var suffix = new StringBuilder();
            if (package.TitleId != null) suffix.Append(" [").Append(package.TitleId).Append(']');
            if (package.Content == PlayStationContent.Update) suffix.Append(" [Update]");
            if (package.Content == PlayStationContent.Dlc)
                suffix.Append(package.ContentId is { Length: > 16 } id ? $" [DLC {id[^16..]}]" : " [DLC]");
            var version = NormalizeVersion(package.Version);
            if (version.Length > 0 && package.Content != PlayStationContent.Dlc) suffix.Append(" [v").Append(version).Append(']');
            var room = MaxNameBytes - Encoding.UTF8.GetByteCount(suffix.ToString() + extension);
            return Truncate(title, Math.Max(room, 8)) + suffix + extension;
        }

        // "01.010.000" -> "1.10", "v02.002.002" -> "2.2.2"
        internal static string NormalizeVersion(string? value)
        {
            var parts = Regex.Matches(value ?? string.Empty, @"\d+").Select(m => int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
            while (parts.Count > 2 && parts[^1] == "0") parts.RemoveAt(parts.Count - 1);
            return string.Join(".", parts);
        }

        private static readonly Regex VersionTag = new(@"\[v(\d+(?:\.\d+)*)\]", RegexOptions.IgnoreCase);
        private static string? VersionIn(string name) => VersionTag.Match(name) is { Success: true } m ? m.Groups[1].Value : null;

        // Library entries for the same title ID and the same kind (game, update, one DLC)
        private static IEnumerable<string> SameGame(string folder, PlayStationPackage package, string name)
        {
            if (package.TitleId == null || package.Content == PlayStationContent.Dlc)
                return Directory.EnumerateFileSystemEntries(folder).Where(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase));
            var tag = "[" + package.TitleId + "]";
            return Directory.EnumerateFileSystemEntries(folder).Where(p =>
            {
                var n = Path.GetFileName(p);
                if (n.StartsWith('.') || !n.Contains(tag, StringComparison.OrdinalIgnoreCase) || n.Contains("[DLC", StringComparison.OrdinalIgnoreCase)) return false;
                return n.Contains("[Update]", StringComparison.OrdinalIgnoreCase) == (package.Content == PlayStationContent.Update);
            }).ToList();
        }

        private string? CopyFile(string from, string to)
        {
            if (!_mover.ImportFile(from, to, out var reason)) return $"Could not copy {from}: {reason}";
            return new FileInfo(to).Length == new FileInfo(from).Length ? null : $"Copy of {from} has the wrong size";
        }

        private string? CopyFolder(string from, string to)
        {
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var error = CopyFile(file, dest);
                if (error != null) return error;
            }
            return null;
        }

        private static long SizeOf(string path) => Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : File.Exists(path) ? new FileInfo(path).Length : -1;

        // A title fit for a file name: no trademark signs, nothing a file system or SMB refuses
        internal static string SafeTitle(string? value)
        {
            var s = Regex.Replace(value ?? string.Empty, "[®™℠©]", "");
            s = Regex.Replace(s, @"[:/\\?*""<>|\x00-\x1f]", " ");
            return Regex.Replace(s, @"\s+", " ").Trim(' ', '.', '-', '_');
        }

        private static readonly Regex[] ReleaseNoise =
        {
            new(@"^\d{6}\s*-\s*"),                                                  // 250101 - date prefix
            new(@"[A-Z]{2}\d{4}-(?:PPSA|CUSA)\d{5}_\d{2}-[A-Z0-9]{16}", RegexOptions.IgnoreCase),
            new(@"(?<![A-Za-z0-9])(?:PPSA|CUSA|ECAS)[-_]?\d{5}(?!\d)", RegexOptions.IgnoreCase),
            new(@"\[[^\]]*\]|\([^)]*\)|\{[^}]*\}"),
        };

        private static readonly Regex ReleaseTail = new(
            @"(?<![A-Za-z0-9])(?:PS[45]|PlayStation\s*[45]|DLC|app0|exfat|ffpfsc|lz4|fpkg|update|patch|backport|v?\d+\.\d+(?:\.\d+)*)(?![A-Za-z0-9]).*$",
            RegexOptions.IgnoreCase);

        /// <summary>The title in a release name: "Ghost.of.Yotei.PS5-DUPLEX" -> "Ghost of Yotei".</summary>
        internal static string CleanReleaseName(string? name)
        {
            var s = name ?? string.Empty;
            foreach (var noise in ReleaseNoise) s = noise.Replace(s, " ");
            if (!s.Trim().Contains(' ')) s = s.Replace('.', ' ').Replace('_', ' ');
            s = s.Replace('_', ' ');
            s = ReleaseTail.Replace(s, "");
            s = Regex.Replace(s, @"-[A-Za-z0-9]+$", m => m.Value.Skip(1).All(c => char.IsUpper(c) || char.IsDigit(c)) ? "" : m.Value);
            s = SafeTitle(s);
            return s.Any(char.IsLetter) ? s : string.Empty;
        }

        private static string Truncate(string value, int maxBytes)
        {
            var s = value;
            while (s.Length > 0 && Encoding.UTF8.GetByteCount(s) > maxBytes) s = s[..^1];
            return s.TrimEnd(' ', '.', '-', '_');
        }
    }
}
