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
        // AlreadyInLibrary: the game is in roms in some form, nothing was added; a watch folder in move mode lets the
        // release go to the trash
        public sealed record Outcome(bool Success, string? Destination, string? Reason, string? Title = null, string? VerifyFrom = null, bool Unpacked = false,
            bool AlreadyInLibrary = false, bool Backport = false)
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
        /// Uncompressed and LZ4 game folders are imported like images. PS_IMPORT_FOLDERS=false leaves them where they are
        /// for a converter that turns them into exFAT/FFPFSC images, and only the images are imported.
        /// </summary>
        public static bool ImportFolders =>
            !string.Equals(Environment.GetEnvironmentVariable("PS_IMPORT_FOLDERS"), "false", StringComparison.OrdinalIgnoreCase);

        /// <summary>The subfolder of roms/ps5 that fake PKGs go to, PS_PKG_FOLDER, default "pkg". PS4 has none.</summary>
        public static string PkgFolder => Environment.GetEnvironmentVariable("PS_PKG_FOLDER") is { Length: > 0 } f ? f : "pkg";

        /// <summary>
        /// PS5 updates, DLC and backports share one folder outside the library, PS_BACKPORT_FOLDER (default
        /// /nas/games/backport-updates-dlc): update and DLC PKGs right in it, backports in a folder per release.
        /// </summary>
        internal static string DefaultExtrasRoot => Environment.GetEnvironmentVariable("PS_BACKPORT_FOLDER") ?? "/nas/games/backport-updates-dlc";

        /// <summary>An update or DLC PKG, by its name ("[Update]", "[DLC ...]") or else by the package itself.</summary>
        internal static bool IsUpdateOrDlcPkg(string pkg)
        {
            var name = Path.GetFileName(pkg);
            if (Regex.IsMatch(name, @"\[(Update|DLC)\b", RegexOptions.IgnoreCase)) return true;
            return PlayStationPackageInspector.Inspect(pkg).Content is PlayStationContent.Update or PlayStationContent.Dlc;
        }

        /// <summary>
        /// The folder in roms/ps4 that holds every PKG of a PS4 game (base, updates, DLC): one whose name carries the
        /// title ID, or that holds a PKG of it already ("Ghostbusters/..."), else "&lt;Game&gt; [CUSA01234]".
        /// </summary>
        internal static string Ps4GameFolder(string ps4, string titleId, string gameTitle)
        {
            var tag = "[" + titleId + "]";
            if (Directory.Exists(ps4))
                foreach (var dir in Directory.EnumerateDirectories(ps4).Where(d => !Path.GetFileName(d).StartsWith('.')))
                {
                    if (Path.GetFileName(dir).Contains(tag, StringComparison.OrdinalIgnoreCase)) return dir;
                    try
                    {
                        if (Directory.EnumerateFiles(dir, "*.pkg").Any(f => (PlayStationPackageInspector.TitleIdIn(Path.GetFileName(f)) is { Length: > 0 } id
                                ? id : PlayStationPackageInspector.Inspect(f).TitleId ?? string.Empty).Equals(titleId, StringComparison.OrdinalIgnoreCase)))
                            return dir;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            return Path.Combine(ps4, SafeTitle(gameTitle) + " " + tag);
        }

        /// <summary>The game's name the library gives a title ID ("Sackboy A Big Adventure" for PPSA01289), from its
        /// non-DLC entries in these folders; null when there is none.</summary>
        internal static string? GameTitleIn(string titleId, params string[] folders)
        {
            var tag = " [" + titleId + "]";
            return folders.Where(Directory.Exists).SelectMany(f => Directory.EnumerateFileSystemEntries(f)).Select(Path.GetFileName)
                .Where(n => n != null && !n.StartsWith('.') && n.Contains(tag, StringComparison.OrdinalIgnoreCase) && !n.Contains("[DLC", StringComparison.OrdinalIgnoreCase))
                .Select(n => n![..n!.IndexOf(tag, StringComparison.OrdinalIgnoreCase)].Trim())
                .FirstOrDefault(t => t.Length > 0);
        }

        /// <summary>"ps5" for PPSA/ECAS, "ps4" for CUSA, null when there is no title ID.</summary>
        public static string? PlatformForTitleId(string? titleId) =>
            string.IsNullOrEmpty(titleId) ? null
            : titleId.StartsWith("CUSA", StringComparison.OrdinalIgnoreCase) ? "ps4"
            : titleId.StartsWith("PPSA", StringComparison.OrdinalIgnoreCase) || titleId.StartsWith("ECAS", StringComparison.OrdinalIgnoreCase) ? "ps5"
            : null;

        public static bool IsPs5(string? platformFolder) =>
            PlatformDefinitions.AllPlatforms.FirstOrDefault(p => p.MatchesFolderName(platformFolder ?? string.Empty))?.MatchesFolderName("ps5") == true
            || string.Equals(platformFolder, "ps5", StringComparison.OrdinalIgnoreCase);

        public static bool IsPlayStation(string? platformFolder) =>
            string.Equals(platformFolder, "ps5", StringComparison.OrdinalIgnoreCase) || string.Equals(platformFolder, "ps4", StringComparison.OrdinalIgnoreCase);

        /// <summary>Backports (PKGs patched for older PS5 firmware) are kept apart there too, one folder per release.</summary>
        internal string BackportRoot = DefaultExtrasRoot;

        internal static bool IsBackport(string name) => name.Contains("backport", StringComparison.OrdinalIgnoreCase);

        // A release that is a backport by what it holds, whatever its name ("[DLPSGAME]"): a fakelib folder (the
        // libraries a backport brings for older firmware) and no game data, so a few GB at most. A whole game with a
        // fakelib folder is the game itself.
        internal static long BackportMaxSize = 5L * 1024 * 1024 * 1024;

        internal static bool IsBackportContent(string path)
        {
            try
            {
                return Directory.Exists(path)
                    && Directory.EnumerateDirectories(path, "fakelib", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true }).Any()
                    && SizeOf(path) < BackportMaxSize;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        // The backports in a release (the release itself, or its "Backport 4.xx" folders and files) copied into
        // BackportRoot/<release>. Returns what was saved, or an error.
        private (List<string> Saved, string? Error) SaveBackports(string source, string releaseName)
        {
            var saved = new List<string>();
            var target = Path.Combine(BackportRoot, SafeTitle(releaseName) is { Length: > 0 } t ? t : "Backport");
            IEnumerable<string> items;
            if (IsBackport(Path.GetFileName(source)) || IsBackport(releaseName) || IsBackportContent(source)) items = new[] { source };
            else if (Directory.Exists(source))
            {
                var all = Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).Where(e => IsBackport(Path.GetFileName(e))).ToList();
                // The outermost only: a "Backport 4.xx" folder, not each file in it again
                items = all.Where(e => !all.Any(o => o != e && e.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.Ordinal))).ToList();
            }
            else items = Array.Empty<string>();
            foreach (var item in items)
            {
                var folder = Directory.Exists(item);
                // A loose backport file goes into its release's folder too: BackportRoot/<release>/<file>
                var to = item == source ? (folder ? target : Path.Combine(target, Path.GetFileName(item))) : Path.Combine(target, Path.GetRelativePath(source, item));
                if (SizeOf(to) == SizeOf(item)) { saved.Add(to); continue; }
                Directory.CreateDirectory(folder ? to : Path.GetDirectoryName(to)!);
                var error = folder ? CopyFolder(item, to) : CopyFile(item, to);
                if (error != null) return (saved, error);
                _logger.Info($"[PlayStation] Backport {item} kept in {to}");
                saved.Add(to);
            }
            return (saved, null);
        }

        public Task<Outcome> ImportAsync(string source, string releaseName, string libraryRoot, string platformFolder)
        {
            var (backports, backportError) = SaveBackports(source, releaseName);
            if (backportError != null) return Task.FromResult(Outcome.Fail(backportError));
            // A release that is only a backport is done once it is kept
            if (backports.Count > 0 && (IsBackport(Path.GetFileName(source)) || IsBackport(releaseName) || IsBackportContent(source)))
                return Task.FromResult(new Outcome(true, backports[0], null, releaseName, VerifyFrom: source, Backport: true));
            var package = PlayStationPackageInspector.Inspect(source);
            if (package.Format == PlayStationFormat.Archive)
            {
                var there = AlreadyThere(Path.Combine(libraryRoot, platformFolder), package, releaseName);
                if (there != null)
                {
                    _logger.Info($"[PlayStation] {releaseName}: {package.TitleId} is already in {there}, nothing unpacked");
                    return Task.FromResult(new Outcome(true, there, null, AlreadyInLibrary: true));
                }
                return ImportArchiveAsync(source, releaseName, libraryRoot, platformFolder);
            }
            return ImportOneAsync(source, releaseName, libraryRoot, platformFolder, ImportFolders);
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
            // Backport archives were kept apart already (SaveBackports); they are not unpacked into the library
            var backportArchives = firsts.Where(f => IsBackport(Path.GetFileName(f))).ToList();
            firsts = firsts.Except(backportArchives).ToList();
            if (firsts.Count == 0 && backportArchives.Count > 0)
                return new Outcome(true, Path.Combine(BackportRoot, SafeTitle(releaseName)), null, releaseName, VerifyFrom: null, Unpacked: true, Backport: true);
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

                var (_, unpackedBackportError) = SaveBackports(work, releaseName);
                if (unpackedBackportError != null) return Outcome.Fail(unpackedBackportError);
                var found = Importables(work).Where(f => !Path.GetRelativePath(work, f).Split(Path.DirectorySeparatorChar).Any(IsBackport) && !IsBackportContent(f)).ToList();
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
                    return Outcome.Fail("Game folder left for the converter (PS_IMPORT_FOLDERS=false)");
            }

            var isFolderGame = package.Format is PlayStationFormat.Folder or PlayStationFormat.Lz4Folder;
            var from = isFolderGame ? package.GameFolder ?? source : package.ImageFile;
            if (from == null) return Outcome.Fail("No PlayStation game, image or package found");
            if (!isFolderGame && Directory.Exists(source))
            {
                var ext = Path.GetExtension(from);
                var images = Directory.EnumerateFiles(source, "*" + ext, SearchOption.AllDirectories)
                    .Count(f => !Path.GetRelativePath(source, f).Split(Path.DirectorySeparatorChar).Any(IsBackport));
                if (images > 1) return Outcome.Fail($"{images} {ext} files in one folder: add the folder as a recursive watch so each is its own game");
            }

            // PS5 fake PKGs get a folder of their own (roms/ps5/pkg); everything else, PS4 PKGs too, lies in the platform folder
            var platformPath = Path.Combine(libraryRoot, platformFolder);
            var folder = package.Format == PlayStationFormat.Pkg && IsPs5(platformFolder) ? Path.Combine(platformPath, PkgFolder) : platformPath;
            Directory.CreateDirectory(platformPath);
            var title = await TitleForAsync(package, source, releaseName, from, platformPath);
            // A PS5 update or DLC goes with the backports, outside the library
            if (package.Format == PlayStationFormat.Pkg && IsPs5(platformFolder) && package.Content is PlayStationContent.Update or PlayStationContent.Dlc)
                folder = BackportRoot;
            // A PS4 PKG goes into its game's folder, with the game's other PKGs: roms/ps4/<Game> [CUSA01234]/
            var ps4Pkg = package.Format == PlayStationFormat.Pkg && !IsPs5(platformFolder) && package.TitleId != null;
            if (ps4Pkg)
                folder = Ps4GameFolder(platformPath, package.TitleId!, GameTitleIn(package.TitleId!, platformPath) ?? title);
            Directory.CreateDirectory(folder);
            var extension = isFolderGame ? string.Empty : Path.GetExtension(from).ToLowerInvariant();
            var name = BuildName(title, package, extension);
            var target = Path.Combine(folder, name);

            // The game in any form (PKG, LZ4 folder, uncompressed folder, image) in this or a newer version, or in a
            // version that can't be told, means it is already there: nothing is added. Only an older version in the
            // same format is replaced, and never an LZ4 folder; those are the owner's to manage.
            var older = new List<string>();
            // PKGs are kept on their own: a PKG is added even when the game is there as an LZ4 folder, a folder or an
            // image, and those never stop or replace a PKG
            var isPkg = package.Format == PlayStationFormat.Pkg;
            // A PS4 PKG still loose in roms/ps4 (not moved into its game's folder yet) counts too
            var candidates = ps4Pkg ? SameGame(folder, package, name).Concat(SameGame(platformPath, package, name)) : SameGame(folder, package, name);
            foreach (var existing in candidates.Where(e => (KindOf(e) == PlayStationFormat.Pkg) == isPkg))
            {
                var kind = KindOf(existing);
                if (kind == package.Format && SizeOf(existing) == SizeOf(from))
                {
                    _logger.Info($"[PlayStation] {releaseName} is already in the library as {existing}");
                    return new Outcome(true, existing, null, title, from);
                }
                if (!IsOlder(existing, package.Version))
                {
                    _logger.Info($"[PlayStation] {releaseName}: already in the library as {Path.GetFileName(existing)} ({kind}), not added");
                    return new Outcome(true, existing, null, title, AlreadyInLibrary: true);
                }
                if (kind == package.Format && kind != PlayStationFormat.Lz4Folder) older.Add(existing);
            }
            // Folders one level down (roms/ps5/pkg, roms/ps5/_extracted) are only looked at, never changed
            var elsewhere = InOtherLibraries(package, platformPath)
                .FirstOrDefault(e => !older.Contains(e) && (KindOf(e) == PlayStationFormat.Pkg) == isPkg && !IsOlder(e, package.Version));
            if (elsewhere != null)
            {
                _logger.Info($"[PlayStation] {releaseName} is already in {elsewhere}, not added");
                return new Outcome(true, elsewhere, null, title, AlreadyInLibrary: true);
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
            if (!Directory.Exists(folder)) return Array.Empty<string>();
            if (package.TitleId == null || package.Content == PlayStationContent.Dlc)
                return Directory.EnumerateFileSystemEntries(folder).Where(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase));
            return Directory.EnumerateFileSystemEntries(folder).Where(p => SameKind(Path.GetFileName(p), package)).ToList();
        }

        private static bool SameKind(string entryName, PlayStationPackage package)
        {
            if (entryName.StartsWith('.') || PlayStationPackageInspector.TitleIdIn(entryName) != package.TitleId) return false;
            if (Regex.IsMatch(entryName, @"(?<![A-Za-z])DLC(?![A-Za-z])", RegexOptions.IgnoreCase)) return false;
            var isUpdate = Regex.IsMatch(entryName, @"(?<![A-Za-z])(update|patch)(?![A-Za-z])", RegexOptions.IgnoreCase);
            return isUpdate == (package.Content == PlayStationContent.Update);
        }

        /// <summary>
        /// Other folders holding PlayStation games, as PS_LIBRARY_EXTRA lists them (comma separated container paths, like
        /// the LZ4 library): a game found there by title ID is not copied into the library again.
        /// </summary>
        internal static IReadOnlyList<string> OtherLibraries =>
            (Environment.GetEnvironmentVariable("PS_LIBRARY_EXTRA") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // main: the library's own platform folder, looked into one level down too (roms/ps5/_extracted/...)
        private static IEnumerable<string> InOtherLibraries(PlayStationPackage package, string? main = null)
        {
            if (package.TitleId == null || package.Content == PlayStationContent.Dlc) yield break;
            // Only the library itself counts: the LZ4 library (PS_LIBRARY_EXTRA) is the owner's test bench, a game there
            // is not in the library yet
            foreach (var library in new[] { main }.OfType<string>().Where(Directory.Exists))
            {
                List<string> entries;
                try
                {
                    // The games themselves, or one level down when the library sorts them into folders
                    entries = Directory.EnumerateFileSystemEntries(library).ToList();
                    entries.AddRange(entries.Where(Directory.Exists).Where(d => PlayStationPackageInspector.TitleIdIn(Path.GetFileName(d)).Length == 0)
                        .SelectMany(d => Directory.EnumerateFileSystemEntries(d)).ToList());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Warn($"[PlayStation] Cannot read {library}: {ex.Message}");
                    continue;
                }
                foreach (var e in entries.Where(e => SameKind(Path.GetFileName(e), package))) yield return e;
            }
        }

        // "v1.05", "v01.010.000", "A0105-V0100" style versions in a release or library name
        private static readonly Regex NameVersion = new(@"(?<![A-Za-z0-9])v(\d+(?:\.\d+)+)(?![\d.])", RegexOptions.IgnoreCase);
        internal static string? VersionInName(string? name) => NameVersion.Match(name ?? string.Empty) is { Success: true } m ? m.Groups[1].Value : null;

        /// <summary>
        /// Before unpacking: a release whose title ID the library (or another PS library) already has in this or a
        /// newer version is skipped, so nothing is unpacked or copied for a game that is already there.
        /// </summary>
        internal static string? AlreadyThere(string folder, PlayStationPackage package, string releaseName)
        {
            // An LZ4 folder, an uncompressed folder or an image counts for each other; a PKG only for a PKG. What the
            // archive holds has to be known from its table of contents
            if (package.TitleId == null || package.Content != PlayStationContent.Game || package.Inside == PlayStationFormat.Unknown) return null;
            var isPkg = package.Inside == PlayStationFormat.Pkg;
            var version = package.Version ?? VersionInName(releaseName);
            var here = Directory.Exists(folder) ? SameGame(folder, package, string.Empty) : Enumerable.Empty<string>();
            return here.Concat(InOtherLibraries(package, Directory.Exists(folder) ? folder : null))
                .FirstOrDefault(e => (KindOf(e) == PlayStationFormat.Pkg) == isPkg && !IsOlder(e, version));
        }

        // Older only when both versions are known and the entry's is lower
        private static bool IsOlder(string entry, string? version)
        {
            var name = Path.GetFileName(entry);
            var existing = VersionIn(name) ?? VersionInName(name);
            return existing != null && version != null && PlayStationPackageInspector.CompareVersions(existing, version) < 0;
        }

        /// <summary>The format a library entry is in, by its name and, for a folder, an LZ4 marker at its top.</summary>
        internal static PlayStationFormat KindOf(string path)
        {
            if (File.Exists(path))
                return Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".pkg" => PlayStationFormat.Pkg,
                    ".exfat" => PlayStationFormat.ExfatImage,
                    ".ffpfsc" => PlayStationFormat.FfpfscImage,
                    _ => PlayStationFormat.Unknown
                };
            if (!Directory.Exists(path)) return PlayStationFormat.Unknown;
            if (Path.GetFileName(path).Contains("lz4", StringComparison.OrdinalIgnoreCase)) return PlayStationFormat.Lz4Folder;
            try
            {
                return Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName)
                    .Any(n => n != null && n.StartsWith("ampr_assets", StringComparison.OrdinalIgnoreCase))
                    ? PlayStationFormat.Lz4Folder : PlayStationFormat.Folder;
            }
            catch (IOException) { return PlayStationFormat.Folder; }
            catch (UnauthorizedAccessException) { return PlayStationFormat.Folder; }
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
