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

        public PlayStationLibraryNamer(ConfigurationService config, AiTitleService? ai = null)
        {
            _config = config;
            _ai = ai;
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

        public IEnumerable<string> Folders()
        {
            var media = _config.LoadMediaSettings();
            var root = !string.IsNullOrEmpty(media.DestinationPath) && Path.IsPathRooted(media.DestinationPath) ? media.DestinationPath : media.FolderPath;
            if (!string.IsNullOrEmpty(root))
                foreach (var p in new[] { "ps5", "ps4" })
                {
                    var platform = PlatformDefinitions.AllPlatforms.FirstOrDefault(x => x.MatchesFolderName(p));
                    var folder = Path.Combine(root, platform?.GetEffectiveFolderName(media.FolderNamingMode) ?? p);
                    if (p == "ps5") _ps5Folders.Add(folder);
                    yield return folder;
                    // Only PS5 has a pkg folder
                    if (p == "ps5") yield return Path.Combine(folder, PlayStationImporter.PkgFolder);
                }
            // The LZ4 library: names only, in place
            foreach (var extra in PlayStationImporter.OtherLibraries) yield return extra;
        }

        /// <summary>Renames what needs a name. Returns the renames made, old path to new path.</summary>
        public async Task<List<(string From, string To)>> RunOnceAsync(CancellationToken ct = default)
        {
            var done = new List<(string, string)>();
            foreach (var folder in Folders().Where(Directory.Exists))
            {
                MovePkgsIntoTheirFolder(folder, done);
                if (_ps5Folders.Contains(folder)) MoveDlcIntoTheirFolder(folder, done);
                List<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(folder).ToList(); }
                catch (Exception ex) { _logger.Warn($"[PSNames] Cannot read {folder}: {ex.Message}"); continue; }
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(entry);
                    if (name.StartsWith('.')) continue;
                    // A converter may still be at work on what changed lately
                    if (!NeedsName(name) || RecentlyWritten(entry)) continue;
                    var target = await NewNameAsync(entry);
                    if (target == null || target == name) continue;
                    var to = Path.Combine(folder, target);
                    if (File.Exists(to) || Directory.Exists(to)) { _logger.Info($"[PSNames] {name}: {target} is already there, left as it is"); continue; }
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

        // DLC PKGs in roms/ps5/pkg go into roms/ps5/pkg/dlc/<Game> [DLC]/, under the same name
        private void MoveDlcIntoTheirFolder(string ps5, List<(string, string)> done)
        {
            var pkgFolder = Path.Combine(ps5, PlayStationImporter.PkgFolder);
            if (!Directory.Exists(pkgFolder)) return;
            List<string> pkgs;
            try { pkgs = Directory.EnumerateFiles(pkgFolder, "*.pkg").Where(f => !Path.GetFileName(f).StartsWith('.')).ToList(); }
            catch (Exception) { return; }
            foreach (var pkg in pkgs.Where(p => !RecentlyWritten(p)))
            {
                if (DlcFolderFor(pkg, ps5) is not { } dlc) continue;
                var to = Path.Combine(dlc, Path.GetFileName(pkg));
                if (File.Exists(to)) continue;
                try
                {
                    Directory.CreateDirectory(dlc);
                    Rename(pkg, to);
                    done.Add((pkg, to));
                    _logger.Info($"[PSNames] Moved DLC {Path.GetFileName(pkg)} into {dlc}");
                }
                catch (Exception ex) { _logger.Warn($"[PSNames] Could not move {pkg}: {ex.Message}"); }
            }
        }

        // roms/ps5/pkg/dlc/<Game> [DLC] for a DLC PKG (its name says [DLC ...], or the package does), else null
        internal static string? DlcFolderFor(string pkg, string ps5)
        {
            var name = Path.GetFileName(pkg);
            var package = name.Contains("[DLC", StringComparison.OrdinalIgnoreCase) ? null : PlayStationPackageInspector.Inspect(pkg);
            if (package != null && package.Content != PlayStationContent.Dlc) return null;
            var id = package?.TitleId ?? PlayStationPackageInspector.TitleIdIn(name);
            var pkgFolder = Path.Combine(ps5, PlayStationImporter.PkgFolder);
            var game = (id.Length > 0 ? PlayStationImporter.GameTitleIn(id, ps5, pkgFolder) : null)
                ?? Regex.Replace(name, @"\s*\[.*$", "").Trim();
            if (game.Length == 0) return null;
            return Path.Combine(pkgFolder, PlayStationImporter.DlcFolder, PlayStationImporter.DlcGameFolder(game));
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
                var to = Path.Combine(folder, PlayStationImporter.PkgFolder, Path.GetFileName(pkg));
                if (DlcFolderFor(pkg, folder) is { } dlc) to = Path.Combine(dlc, Path.GetFileName(pkg));
                if (File.Exists(to)) continue;
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

        /// <summary>A name that is nothing but a title ID and format words: "PPSA26344-app0-lz4", "_PPSA03671_", "CUSA00900.pkg".</summary>
        internal static bool NeedsName(string name)
        {
            if (PlayStationPackageInspector.TitleIdIn(name).Length == 0) return false;
            var stem = Regex.Replace(name, @"\.(exfat|ffpfsc|pkg)$", "", RegexOptions.IgnoreCase);
            return PlayStationImporter.CleanReleaseName(stem).Length == 0;
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
            var title = PlayStationImporter.SafeTitle(package.Title);
            if (title.Length == 0 && _ai is { IsEnabled: true })
            {
                var guess = await _ai.IdentifyAsync(name + " " + id, new[] { name });
                if (guess != null && PlayStationImporter.IsPlayStation(guess.Platform)) title = PlayStationImporter.SafeTitle(guess.Title);
            }
            if (title.Length == 0) return null;
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
