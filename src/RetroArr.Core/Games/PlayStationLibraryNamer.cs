using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RetroArr.Core.Configuration;
using RetroArr.Core.Download;
using RetroArr.Core.MetadataSource;

namespace RetroArr.Core.Games
{

    /// <summary>
    /// Gives PS4/PS5 games in roms/ps5 and roms/ps4 that are only a title ID ("PPSA26344-app0", "CUSA00900.pkg") a
    /// real name: "Ghost of Yotei [PPSA26344] [v1.0]". LZ4 folders and the other libraries (PS_LIBRARY_EXTRA, the LZ4
    /// library) are renamed in place too, keeping their -lz4 ending; nothing is ever moved out of them.
    /// The title comes from the game's own param.json/param.sfo, else from the AI. Names that already carry a title are
    /// left alone, as is anything written to in the last six hours (a converter may be at work). Every rename is logged
    /// to ps-renames.json in the config folder, so it can be undone.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public class PlayStationLibraryNamer : BackgroundService
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetLogger(Logging.AppLoggerService.General);

        private readonly ConfigurationService _config;
        private readonly AiTitleService? _ai;

        internal TimeSpan Interval = TimeSpan.FromHours(1);
        internal static TimeSpan QuietFor = TimeSpan.FromHours(6);
        // internal for tests: the rename itself, so a read-only mount can be renamed through the shared one
        internal Action<string, string> Rename = Move;

        private readonly TrashService? _trash;

        public PlayStationLibraryNamer(ConfigurationService config, AiTitleService? ai = null, TrashService? trash = null)
        {
            _config = config;
            _ai = ai;
            _trash = trash;
        }

        public static bool Enabled =>
            !string.Equals(Environment.GetEnvironmentVariable("PS_RENAME"), "false", StringComparison.OrdinalIgnoreCase);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
            catch (TaskCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try { if (Enabled) await RunOnceAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.Warn($"[PSNames] Run failed: {ex.Message}"); }
                try { await Task.Delay(Interval, stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        private readonly HashSet<string> _ps5Folders = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ps4Folders = new(StringComparer.Ordinal);

        public IEnumerable<string> Folders()
        {
            var media = _config.LoadMediaSettings();
            var root = !string.IsNullOrEmpty(media.DestinationPath) && Path.IsPathRooted(media.DestinationPath) ? media.DestinationPath : media.FolderPath;
            if (!string.IsNullOrEmpty(root))
                foreach (var p in new[] { "ps5", "ps4" })
                {
                    var platform = PlatformDefinitions.AllPlatforms.FirstOrDefault(x => x.MatchesFolderName(p));
                    var folder = Path.Combine(root, platform?.GetEffectiveFolderName(media.FolderNamingMode) ?? p);
                    if (p == "ps5") _ps5Folders.Add(folder); else _ps4Folders.Add(folder);
                    yield return folder;
                    // Only PS5 has a pkg folder
                    if (p == "ps5") yield return Path.Combine(folder, PlayStationImporter.PkgFolder);
                }
            // The LZ4 library: names only, in place
            foreach (var extra in PlayStationImporter.OtherLibraries) yield return extra;
            // PS5 updates, DLC and backports, outside the library: they get the game's name too
            yield return ExtrasRoot;
            foreach (var sub in PlayStationImporter.ExtrasFolders)
            {
                var kind = Path.Combine(ExtrasRoot, sub);
                yield return kind;
                // and each game's folder in it
                string[] games;
                try { games = Directory.Exists(kind) ? Directory.GetDirectories(kind) : Array.Empty<string>(); }
                catch (Exception) { games = Array.Empty<string>(); }
                if (sub != PlayStationImporter.BackportFolder)
                    foreach (var game in games.Where(g => !Path.GetFileName(g).StartsWith('.')).OrderBy(g => g, StringComparer.Ordinal)) yield return game;
            }
        }

        /// <summary>Renames what needs a name. Returns the renames made, old path to new path.</summary>
        public async Task<List<(string From, string To)>> RunOnceAsync(CancellationToken ct = default)
        {
            var done = new List<(string, string)>();
            _votes.Clear();
            SortExtras(done);
            await SortIntoGameFoldersAsync(done);
            foreach (var folder in Folders().Where(Directory.Exists))
            {
                MovePkgsIntoTheirFolder(folder, done);
                if (_ps5Folders.Contains(folder)) MoveDlcIntoTheirFolder(folder, done);
                else if (_ps4Folders.Contains(folder)) { MergePs4Duplicates(folder, done); MovePs4PkgsIntoGameFolders(folder, done); }
                List<string> entries;
                // In name order, so a run does the same on every file system
                try { entries = Directory.EnumerateFileSystemEntries(folder).OrderBy(e => e, StringComparer.Ordinal).ToList(); }
                catch (Exception ex) { _logger.Warn($"[PSNames] Cannot read {folder}: {ex.Message}"); continue; }
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(entry);
                    if (name.StartsWith('.')) continue;
                    // A converter may still be at work on what changed lately
                    if (!(NeedsName(name) || NameSaysNothing(entry) || TitleDisagrees(entry)) || RecentlyWritten(entry)) continue;
                    // A second folder of a PS4 game keeps its name: the game's name is the folder it is merged into
                    if (_otherCopies.Contains(entry)) continue;
                    var target = await NewNameAsync(entry);
                    if (target == null || target == name) continue;
                    var to = Path.Combine(folder, target);
                    if (File.Exists(to) || Directory.Exists(to))
                    {
                        // The same file under its right name already: this one is a copy and goes to the trash
                        if (File.Exists(entry) && File.Exists(to) && new FileInfo(entry).Length == new FileInfo(to).Length && _trash != null
                            && await _trash.MoveAsync(entry, null, target) != null)
                            _logger.Info($"[PSNames] {name} is a copy of {target}; it went to the trash");
                        else _logger.Info($"[PSNames] {name}: {target} is already there, left as it is");
                        continue;
                    }
                    try
                    {
                        Rename(entry, to);
                        done.Add((entry, to));
                        _logger.Info($"[PSNames] Renamed {entry} -> {target}");
                    }
                    catch (Exception ex) { _logger.Warn($"[PSNames] Could not rename {entry}: {ex.Message}"); }
                }
            }
            if (done.Count > 0) Remember(done);
            return done;
        }

        // Loose PKGs in roms/ps4 go into their game's folder (roms/ps4/<Game> [CUSA01234]/), under the same name
        private void MovePs4PkgsIntoGameFolders(string ps4, List<(string, string)> done)
        {
            List<string> pkgs;
            try { pkgs = Directory.EnumerateFiles(ps4, "*.pkg").Where(f => !Path.GetFileName(f).StartsWith('.')).ToList(); }
            catch (Exception) { return; }
            foreach (var pkg in pkgs.Where(p => !RecentlyWritten(p)))
            {
                var name = Path.GetFileName(pkg);
                var id = PlayStationPackageInspector.TitleIdIn(name) is { Length: > 0 } fromName ? fromName : PlayStationPackageInspector.Inspect(pkg).TitleId;
                if (string.IsNullOrEmpty(id)) continue;
                var game = PlayStationImporter.GameTitleIn(id, ps4) ?? Regex.Replace(name, @"\s*\[.*$", "").Trim();
                if (game.Length == 0) continue;
                var to = Path.Combine(PlayStationImporter.Ps4GameFolder(ps4, id, game), name);
                if (File.Exists(to)) { TrashIfCopy(pkg, to); continue; }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    Rename(pkg, to);
                    done.Add((pkg, to));
                    _logger.Info($"[PSNames] Moved {name} into {Path.GetDirectoryName(to)}");
                }
                catch (Exception ex) { _logger.Warn($"[PSNames] Could not move {pkg}: {ex.Message}"); }
            }
        }

        // The title a well-named entry gives this ID ("Stray" from "Stray [PPSA02100] [v1.0].pkg"), never a release name
        private string LibraryTitle(string id) => LibraryTitleOf(id).Title;

        // The title, and whether only DLC carry this ID (a DLC's own ID: PPSA01289 for Sackboy's DLC, PPSA01288 the game)
        private readonly Dictionary<string, Dictionary<string, int>> _votes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A name of ours whose game disagrees with what most entries of its title ID say: "Sable [PPSA01289].pkg" among
        /// 55 "Sackboy A Big Adventure [PPSA01289] ..." (one title ID is one game). It needs a clear majority: 3 or more,
        /// and twice as many as its own name has.
        /// </summary>
        internal bool TitleDisagrees(string entry)
        {
            var name = Path.GetFileName(entry);
            var id = PlayStationPackageInspector.TitleIdIn(name);
            if (id.Length == 0 || PlayStationImporter.GameOfName(name) is not { } game) return false;
            if (!_votes.TryGetValue(id, out var votes)) _votes[id] = votes = PlayStationImporter.TitleVotes(id, Folders(), NeedsName);
            if (votes.Count == 0) return false;
            var best = votes.OrderByDescending(v => v.Value).First();
            return !string.Equals(best.Key, game, StringComparison.OrdinalIgnoreCase) && best.Value >= 3 && best.Value >= 2 * votes.GetValueOrDefault(game);
        }

        private (string Title, bool OnlyDlc) LibraryTitleOf(string id) =>
            PlayStationImporter.KnownTitle(id, Folders(), NeedsName);

        /// <summary>On unless PS4_MERGE=false (then it only logs what it would do): the owner asked for one folder per game.</summary>
        public static bool MergePs4 => !string.Equals(Environment.GetEnvironmentVariable("PS4_MERGE"), "false", StringComparison.OrdinalIgnoreCase);

        internal bool? MergeOverride;

        // PS4 folders of a game that holds another folder too, left after a merge or in a dry run
        private readonly HashSet<string> _otherCopies = new(StringComparer.Ordinal);

        // Two folders in roms/ps4 for one title ID (two releases of it) become one: what the others hold moves into the
        // best-named one, never over a file of the same name, and a folder left empty goes. PS4_MERGE=false keeps it a dry run.
        private void MergePs4Duplicates(string ps4, List<(string, string)> done)
        {
            List<(string Dir, string Id)> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(ps4).Where(d => !Path.GetFileName(d).StartsWith('.') && !RecentlyWritten(d))
                    .Select(d => (d, Ps4IdOf(d))).Where(x => x.Item2.Length > 0).ToList();
            }
            catch (Exception) { return; }
            var merge = MergeOverride ?? MergePs4;
            _otherCopies.Clear();
            foreach (var group in dirs.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var ordered = group.Select(x => x.Dir).OrderBy(d => NeedsName(Path.GetFileName(d)) ? 1 : 0).ThenBy(d => d, StringComparer.Ordinal).ToList();
                var target = ordered[0];
                foreach (var other in ordered.Skip(1))
                {
                    if (!merge) { _otherCopies.Add(other); _logger.Info($"[PSNames] Dry run: would merge {other} into {target} (PS4_MERGE=false keeps it a dry run)"); continue; }
                    foreach (var item in Directory.EnumerateFileSystemEntries(other).ToList())
                    {
                        var to = Path.Combine(target, Path.GetFileName(item));
                        if (File.Exists(to) || Directory.Exists(to))
                        {
                            // The same file in the game's folder already: this one is a copy and goes to the trash
                            if (File.Exists(item) && File.Exists(to) && new FileInfo(item).Length == new FileInfo(to).Length && _trash != null
                                && _trash.MoveAsync(item, null, Path.GetFileName(item)).GetAwaiter().GetResult() != null)
                                _logger.Info($"[PSNames] {item} is a copy of {to}; it went to the trash");
                            else _logger.Info($"[PSNames] {to} is there already, {item} stays");
                            continue;
                        }
                        try { Rename(item, to); done.Add((item, to)); }
                        catch (Exception ex) { _logger.Warn($"[PSNames] Could not move {item}: {ex.Message}"); }
                    }
                    try { if (!Directory.EnumerateFileSystemEntries(other).Any()) Directory.Delete(other); } catch (Exception) { }
                    if (Directory.Exists(other)) _otherCopies.Add(other);
                    _logger.Info($"[PSNames] Merged {other} into {target}");
                }
            }
        }

        // The title ID of a PS4 game folder: in its name, else of a PKG right in it
        private static string Ps4IdOf(string dir)
        {
            if (PlayStationPackageInspector.TitleIdIn(Path.GetFileName(dir)) is { Length: > 0 } id) return id.ToUpperInvariant();
            try
            {
                foreach (var pkg in Directory.EnumerateFiles(dir, "*.pkg"))
                    if ((PlayStationPackageInspector.TitleIdIn(Path.GetFileName(pkg)) is { Length: > 0 } fromName ? fromName : PlayStationPackageInspector.Inspect(pkg).TitleId) is { Length: > 0 } found)
                        return found.ToUpperInvariant();
            }
            catch (Exception) { }
            return string.Empty;
        }

        // A file whose place holds the same file already (same size) is a copy: it goes to the trash
        private void TrashIfCopy(string file, string existing)
        {
            try
            {
                if (_trash == null || new FileInfo(file).Length != new FileInfo(existing).Length) return;
                if (_trash.MoveAsync(file, null, Path.GetFileName(file)).GetAwaiter().GetResult() != null)
                    _logger.Info($"[PSNames] {file} is a copy of {existing}; it went to the trash");
            }
            catch (Exception ex) { _logger.Warn($"[PSNames] Could not trash the copy {file}: {ex.Message}"); }
        }

        // Where PS5 update and DLC PKGs go: the folder they share with the backports, outside the library
        internal string ExtrasRoot = PlayStationImporter.DefaultExtrasRoot;

        /// <summary>
        /// On unless PS_EXTRAS_SORT=false (then it only writes the plan). The owner asked for this sort, and the extras
        /// folder holds only what RetroArr put there.
        /// </summary>
        public static bool SortExtrasOn => !string.Equals(Environment.GetEnvironmentVariable("PS_EXTRAS_SORT"), "false", StringComparison.OrdinalIgnoreCase);

        internal bool? SortOverride;
        internal readonly List<string> SortPlan = new();

        // What lies loose in the extras folder from before goes into its three folders: update PKGs into updates/, DLC
        // PKGs into dlc/, everything else (a backport's release folder or file) into backport/. Never over a file of the
        // same name. PS_EXTRAS_SORT=false only writes extras-sort-plan.log.
        private void SortExtras(List<(string, string)> done)
        {
            SortPlan.Clear();
            if (!Directory.Exists(ExtrasRoot)) return;
            List<string> loose;
            try
            {
                loose = Directory.EnumerateFileSystemEntries(ExtrasRoot)
                    .Where(e => Path.GetFileName(e) is var n && !n.StartsWith('.') && !PlayStationImporter.ExtrasFolders.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .Where(e => !RecentlyWritten(e)).ToList();
            }
            catch (Exception) { return; }
            var sort = SortOverride ?? SortExtrasOn;
            foreach (var entry in loose)
            {
                var into = File.Exists(entry) && entry.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) && PlayStationImporter.IsUpdateOrDlcPkg(entry)
                    ? PlayStationImporter.ExtrasTargetFor(ExtrasRoot, entry)
                    : Path.Combine(ExtrasRoot, PlayStationImporter.BackportFolder);
                var to = Path.Combine(into, Path.GetFileName(entry));
                if (File.Exists(to) || Directory.Exists(to)) { SortPlan.Add($"KEEP {entry} ({to} is there already)"); continue; }
                SortPlan.Add($"MOVE {entry} -> {to}");
                if (!sort) continue;
                try
                {
                    Directory.CreateDirectory(into);
                    Rename(entry, to);
                    done.Add((entry, to));
                    _logger.Info($"[PSNames] Sorted {Path.GetFileName(entry)} into {into}");
                }
                catch (Exception ex) { _logger.Warn($"[PSNames] Could not sort {entry}: {ex.Message}"); }
            }
            if (!sort && SortPlan.Count > 0)
                try
                {
                    File.WriteAllLines(Path.Combine(_config.GetConfigDirectory(), "extras-sort-plan.log"), SortPlan);
                    _logger.Info($"[PSNames] Dry run: {SortPlan.Count} entries in {ExtrasRoot} would be sorted, see extras-sort-plan.log (PS_EXTRAS_SORT=false keeps it a dry run)");
                }
                catch (Exception ex) { _logger.Warn($"[PSNames] Could not write the sort plan: {ex.Message}"); }
        }

        // In updates/ and dlc/ each game has its folder ("dlc/Sackboy A Big Adventure/"), and in backport/ each game's
        // releases share one ("backport/Onimusha/<release>/"). A file whose name does not say its game yet waits until
        // it is named. Never over a file of the same name; a same-size copy goes to the trash. On unless PS_EXTRAS_SORT=false.
        private async Task SortIntoGameFoldersAsync(List<(string, string)> done)
        {
            if (!(SortOverride ?? SortExtrasOn)) return;
            foreach (var sub in PlayStationImporter.ExtrasFolders)
            {
                var kind = Path.Combine(ExtrasRoot, sub);
                if (!Directory.Exists(kind)) continue;
                var backport = sub == PlayStationImporter.BackportFolder;
                List<string> loose;
                try
                {
                    loose = (backport ? Directory.EnumerateFileSystemEntries(kind) : Directory.EnumerateFiles(kind)
                            // and a file in another game's folder ("dlc/Sable/Sackboy A Big Adventure [PPSA01289] ...")
                            .Concat(Directory.EnumerateDirectories(kind).SelectMany(d => Directory.EnumerateFiles(d)
                                .Where(f => PlayStationImporter.GameOfName(Path.GetFileName(f)) is { } g && !string.Equals(g, Path.GetFileName(d), StringComparison.OrdinalIgnoreCase)))))
                        .Where(e => !Path.GetFileName(e).StartsWith('.') && !RecentlyWritten(e)).OrderBy(e => e, StringComparer.Ordinal).ToList();
                }
                catch (Exception) { continue; }
                foreach (var entry in loose)
                {
                    var name = Path.GetFileName(entry);
                    string? game;
                    if (!backport) game = PlayStationImporter.GameOfName(name);
                    else
                    {
                        // A backport release names its game by its title ID; a folder without one is a game's folder already
                        var id = PlayStationPackageInspector.TitleIdIn(name);
                        if (id.Length == 0) continue;
                        game = LibraryTitle(id) is { Length: > 0 } known ? known : PlayStationImporter.CleanReleaseName(name);
                    }
                    if (string.IsNullOrEmpty(game) || string.Equals(game, name, StringComparison.Ordinal)) continue;
                    var into = Path.Combine(kind, game);
                    var to = Path.Combine(into, name);
                    if (File.Exists(to) || Directory.Exists(to))
                    {
                        if (File.Exists(entry) && File.Exists(to) && new FileInfo(entry).Length == new FileInfo(to).Length && _trash != null
                            && await _trash.MoveAsync(entry, null, name) != null)
                        {
                            _logger.Info($"[PSNames] {entry} is a copy of {to}; it went to the trash");
                            var left = Path.GetDirectoryName(entry)!;
                            if (!string.Equals(left, kind, StringComparison.Ordinal) && !Directory.EnumerateFileSystemEntries(left).Any()) Directory.Delete(left);
                        }
                        continue;
                    }
                    try
                    {
                        Directory.CreateDirectory(into);
                        Rename(entry, to);
                        done.Add((entry, to));
                        _logger.Info($"[PSNames] Sorted {name} into {into}");
                        // A game folder this left empty goes
                        var from = Path.GetDirectoryName(entry)!;
                        if (!string.Equals(from, kind, StringComparison.Ordinal) && !Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
                    }
                    catch (Exception ex) { _logger.Warn($"[PSNames] Could not sort {entry}: {ex.Message}"); }
                }
            }
        }

        // PS5 update and DLC PKGs in roms/ps5/pkg (and its "<Game> [DLC]" folders from before) go to ExtrasRoot
        private void MoveDlcIntoTheirFolder(string ps5, List<(string, string)> done)
        {
            var pkgFolder = Path.Combine(ps5, PlayStationImporter.PkgFolder);
            if (!Directory.Exists(pkgFolder)) return;
            List<string> pkgs;
            try
            {
                // Any depth: the old layouts had pkg/<Game> [DLC]/ and pkg/dlc/<Game> [DLC]/
                pkgs = Directory.EnumerateFiles(pkgFolder, "*.pkg", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Where(f => !Path.GetRelativePath(pkgFolder, f).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.'))).ToList();
            }
            catch (Exception) { return; }
            var left = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pkg in pkgs.Where(p => !RecentlyWritten(p) && PlayStationImporter.IsUpdateOrDlcPkg(p)))
            {
                var into = PlayStationImporter.ExtrasTargetFor(ExtrasRoot, pkg);
                // A title ID only its folder's name carried ("PPSA01289/patch.pkg") goes with it, so it can still be named
                var fileName = Path.GetFileName(pkg);
                if (PlayStationPackageInspector.TitleIdIn(fileName).Length == 0 && PlayStationPackageInspector.Inspect(pkg).TitleId is { Length: > 0 } folderId)
                    fileName = folderId + " " + fileName;
                var to = Path.Combine(into, fileName);
                if (File.Exists(to)) { TrashIfCopy(pkg, to); continue; }
                try
                {
                    Directory.CreateDirectory(into);
                    Rename(pkg, to);
                    done.Add((pkg, to));
                    left.Add(Path.GetDirectoryName(pkg)!);
                    _logger.Info($"[PSNames] Moved {Path.GetFileName(pkg)} into {into}");
                }
                catch (Exception ex) { _logger.Warn($"[PSNames] Could not move {pkg}: {ex.Message}"); }
            }
            // A folder this left empty goes, and so do its parents up to pkg ("dlc/<Game> [DLC]")
            foreach (var start in left)
                for (var dir = start; !string.Equals(dir, pkgFolder, StringComparison.Ordinal) && dir.StartsWith(pkgFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal); dir = Path.GetDirectoryName(dir)!)
                    try { if (Directory.EnumerateFileSystemEntries(dir).Any()) break; Directory.Delete(dir); } catch (Exception) { break; }
        }

        // Loose PKGs in roms/ps5 go into roms/ps5/pkg, under the same name
        private void MovePkgsIntoTheirFolder(string folder, List<(string, string)> done)
        {
            // Only PS5 has a pkg folder; PS4 PKGs stay where they are
            if (Path.GetFileName(folder).Equals(PlayStationImporter.PkgFolder, StringComparison.OrdinalIgnoreCase) || !_ps5Folders.Contains(folder)) return;
            List<string> pkgs;
            try { pkgs = Directory.EnumerateFiles(folder, "*.pkg").Where(f => !Path.GetFileName(f).StartsWith('.')).ToList(); }
            catch (Exception) { return; }
            foreach (var pkg in pkgs.Where(p => !RecentlyWritten(p)))
            {
                var to = PlayStationImporter.IsUpdateOrDlcPkg(pkg)
                    ? Path.Combine(PlayStationImporter.ExtrasTargetFor(ExtrasRoot, pkg), Path.GetFileName(pkg))
                    : Path.Combine(folder, PlayStationImporter.PkgFolder, Path.GetFileName(pkg));
                if (File.Exists(to)) { TrashIfCopy(pkg, to); continue; }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    Rename(pkg, to);
                    done.Add((pkg, to));
                    _logger.Info($"[PSNames] Moved {pkg} into {Path.GetDirectoryName(to)}");
                }
                catch (Exception ex) { _logger.Warn($"[PSNames] Could not move {pkg}: {ex.Message}"); }
            }
        }

        // The tags a name of ours carries: [PPSA01234], [v1.2], [Update], [DLC ABCD0000000001]
        private static readonly Regex OurTag = new(@"^\[(?:(?:PPSA|CUSA|ECAS)\d{5}|v\d+(?:\.\d+)*|Update|DLC(?: [A-Za-z0-9]+)?)\]$", RegexOptions.IgnoreCase);

        /// <summary>
        /// A name with a title ID that is not one of ours: nothing but the ID and format words ("PPSA26344-app0-lz4",
        /// "CUSA00900.pkg"), or a release name ("261028 - Stray [PPSA02100] [VIKINGFILE].pkg",
        /// "Ghostbusters.Remastered_CUSA15558_v1.00_[6.72]_GROUP"). Backports keep their release name.
        /// </summary>
        internal static bool NeedsName(string name)
        {
            if (PlayStationPackageInspector.TitleIdIn(name).Length == 0) return false;
            var stem = Regex.Replace(name, @"\.(exfat|ffpfsc|pkg)$", "", RegexOptions.IgnoreCase);
            if (PlayStationImporter.CleanReleaseName(stem).Length == 0) return true;
            if (PlayStationImporter.IsBackport(stem)) return false;
            stem = Regex.Replace(stem, @"[-_ ]lz4$", "", RegexOptions.IgnoreCase);
            if (Regex.IsMatch(stem, @"^\d{6}\s*-")) return true;
            if (!Regex.IsMatch(stem, @"\[(?:PPSA|CUSA|ECAS)\d{5}\]", RegexOptions.IgnoreCase)) return true;
            return Regex.Matches(stem, @"\[[^\]]*\]").Any(m => !OurTag.IsMatch(m.Value));
        }

        /// <summary>
        /// A PKG or image whose name carries no title ID ("sable.pkg") but whose own header does: it is named from that
        /// like a title-ID-only name.
        /// </summary>
        internal static bool NameSaysNothing(string entry)
        {
            var name = Path.GetFileName(entry);
            if (!File.Exists(entry) || PlayStationPackageInspector.TitleIdIn(name).Length > 0) return false;
            if (!Regex.IsMatch(name, @"\.(pkg|exfat|ffpfsc)$", RegexOptions.IgnoreCase)) return false;
            try { return PlayStationPackageInspector.Inspect(entry).TitleId is { Length: > 0 }; }
            catch (Exception) { return false; }
        }

        private static bool RecentlyWritten(string entry)
        {
            try
            {
                var newest = Directory.Exists(entry)
                    ? Directory.EnumerateFiles(entry, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Take(5000).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(entry)).Max()
                    : File.GetLastWriteTimeUtc(entry);
                return DateTime.UtcNow - newest < QuietFor;
            }
            catch (Exception) { return true; }
        }

        // "Title [PPSA26344] [v1.0]" plus the format the old name ended in (-lz4, .exfat, ...)
        internal async Task<string?> NewNameAsync(string entry)
        {
            var name = Path.GetFileName(entry);
            var package = PlayStationPackageInspector.Inspect(entry);
            var id = package.TitleId ?? NullIfEmpty(PlayStationPackageInspector.TitleIdIn(name));
            if (id == null) return null;
            // An update or DLC PKG, also one known only by its size; a DLC's own name is the DLC's ("Kratos Costume")
            if (File.Exists(entry) && package.Format == PlayStationFormat.Pkg && package.Content == PlayStationContent.Game
                && PlayStationImporter.ExtrasKind(entry, package) is { } kindFirst)
                package = package with { Content = kindFirst };
            var title = package.Content == PlayStationContent.Dlc || TitleDisagrees(entry) ? string.Empty : PlayStationImporter.SafeTitle(package.Title);
            // Else the name the library gives that game already, the AI's, or the title in the release name
            if (title.Length == 0)
            {
                var (known, onlyDlc) = LibraryTitleOf(id);
                title = known;
                // A PKG whose header says nothing (no title, no version), with an ID only DLC carry, is one more DLC of that game
                if (onlyDlc && package.Format == PlayStationFormat.Pkg && package.Content == PlayStationContent.Game
                    && package.Title == null && package.Version == null)
                    package = package with { Content = PlayStationContent.Dlc };
            }
            if (title.Length == 0 && _ai is { IsEnabled: true })
            {
                var guess = await _ai.IdentifyAsync(name + " " + id, new[] { name }, default, MetadataSource.FileFacts.Describe(entry));
                if (guess != null && PlayStationImporter.IsPlayStation(guess.Platform)) title = PlayStationImporter.SafeTitle(guess.Title);
            }
            if (title.Length == 0) title = PlayStationImporter.SafeTitle(package.Title);
            // The title in a release name, only when the name names the game ("Stray [PPSA02100] [VIKINGFILE]"): a name
            // without a title ID ("sable.pkg") says nothing about which game it is
            if (title.Length == 0 && PlayStationPackageInspector.TitleIdIn(name).Length > 0)
                title = PlayStationImporter.CleanReleaseName(Regex.Replace(name, @"\.(exfat|ffpfsc|pkg)$", "", RegexOptions.IgnoreCase));
            if (title.Length == 0) return null;
            // A PS4 game folder holds every PKG of the game: "<Game> [CUSA01234]", no version
            if (Directory.Exists(entry) && _ps4Folders.Contains(Path.GetDirectoryName(entry)!))
                return PlayStationImporter.SafeTitle(title) + " [" + id + "]";
            // An update or DLC PKG says so in its name ("[DLC ...]", "[Update]"), also one known only by its size
            if (File.Exists(entry) && package.Format == PlayStationFormat.Pkg && package.Content == PlayStationContent.Game
                && PlayStationImporter.ExtrasKind(entry, package) is { } kind)
                package = package with { Content = kind };
            var extension = File.Exists(entry) ? Path.GetExtension(name).ToLowerInvariant() : string.Empty;
            var suffix = Regex.IsMatch(name, @"(?i)[-_ ]lz4$") ? "-lz4" : string.Empty;
            var version = package.Version ?? PlayStationImporter.VersionInName(name);
            var named = PlayStationImporter.BuildName(title, package with { TitleId = id, Version = version }, extension);
            return suffix.Length == 0 ? named : named + suffix;
        }

        // A read-only mount (the LZ4 library is mounted :ro) is renamed through the shared mount when there is one
        private static void Move(string from, string to)
        {
            var (f, t) = (IO.SharedMount.Translate(from), IO.SharedMount.Translate(to));
            try
            {
                if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
            }
            catch (Exception) when (f != null && t != null)
            {
                if (Directory.Exists(f)) Directory.Move(f, t); else File.Move(f, t);
            }
        }

        private void Remember(List<(string From, string To)> renames)
        {
            try
            {
                var path = Path.Combine(_config.GetConfigDirectory(), "ps-renames.json");
                var log = File.Exists(path) ? JsonSerializer.Deserialize<List<Dictionary<string, string>>>(File.ReadAllText(path)) ?? new() : new();
                log.AddRange(renames.Select(r => new Dictionary<string, string> { ["at"] = DateTime.UtcNow.ToString("o"), ["from"] = r.From, ["to"] = r.To }));
                File.WriteAllText(path, JsonSerializer.Serialize(log, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { _logger.Warn($"[PSNames] Could not write the rename log: {ex.Message}"); }
        }

        private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    }
}
