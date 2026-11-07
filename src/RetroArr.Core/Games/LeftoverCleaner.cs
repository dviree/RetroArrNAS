using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RetroArr.Core.Configuration;
using RetroArr.Core.Download;

namespace RetroArr.Core.Games
{
    /// <summary>
    /// Sends what is left behind to the trash, on its own: RetroArr's work folders (PS staging, Switch merges) and its
    /// half-written files in the library (".retroarr-ps-*", ".retroarr-merge-*", ".retroarr-extract-*") that a crash or
    /// restart left, plus the folders the owner names in CLEANUP_FOLDERS (an old tool's work folder, a test folder).
    /// Only what was not written to for CLEANUP_DAYS (default 2) goes, never the folder itself, and nothing is deleted:
    /// it all goes to the trash. CLEANUP=false turns it off; CLEANUP=dryrun, or FILE_ACTIONS not on, only lists it in files.log.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public class LeftoverCleaner : BackgroundService
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetLogger(Logging.AppLoggerService.General);
        private static readonly string[] OurPrefixes = { ".retroarr-ps-", ".retroarr-merge-", ".retroarr-extract-" };

        private readonly TrashService _trash;
        private readonly ConfigurationService _config;

        internal TimeSpan Interval = TimeSpan.FromHours(6);
        internal TimeSpan Age = TimeSpan.FromDays(double.TryParse(Environment.GetEnvironmentVariable("CLEANUP_DAYS"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? d : 2);
        internal bool? DryRunOverride;
        internal readonly List<string> Plan = new();

        // internal for tests: RetroArr's own work folders
        internal string[] WorkFolders =
        {
            Environment.GetEnvironmentVariable("PS_STAGING") ?? "/temp/retroarr-ps",
            Environment.GetEnvironmentVariable("SWITCH_WORK") ?? "/temp/retroarr-switch",
        };
        internal string[] OwnerFolders = (Environment.GetEnvironmentVariable("CLEANUP_FOLDERS") ?? string.Empty)
            .Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        public LeftoverCleaner(TrashService trash, ConfigurationService config)
        {
            _trash = trash;
            _config = config;
        }

        private static string Mode => (Environment.GetEnvironmentVariable("CLEANUP") ?? "true").Trim().ToLowerInvariant();
        public static bool Enabled => Mode != "false";
        private bool DryRun => DryRunOverride ?? (Mode == "dryrun" || IO.FileActions.DryRun);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (TaskCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try { if (Enabled) await RunOnceAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.Warn($"[Cleanup] Run failed: {ex.Message}"); }
                try { await Task.Delay(Interval, stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        /// <summary>Sends what is left behind to the trash; returns how many entries went (or would go, in a dry run).</summary>
        public async Task<int> RunOnceAsync(CancellationToken ct = default)
        {
            Plan.Clear();
            var gone = 0;
            // The trash keeps each folder whole: entries laid out flat before go back into their folders
            if (!DryRun)
                try { await _trash.RegroupFlatEntriesAsync(); }
                catch (Exception ex) { _logger.Warn($"[Cleanup] Could not regroup the trash: {ex.Message}"); }
            foreach (var entry in Leftovers())
            {
                ct.ThrowIfCancellationRequested();
                Plan.Add("TRASH " + entry);
                if (DryRun) { gone++; IO.FileLog.Planned("TRASH", entry, null, "cleanup: left behind, not written to for " + Age.TotalDays + " days"); continue; }
                try
                {
                    if (await _trash.MoveAsync(entry, null, Path.GetFileName(entry)) != null)
                    {
                        gone++;
                        _logger.Info($"[Cleanup] {entry} was left behind; it went to the trash");
                    }
                }
                catch (Exception ex) { _logger.Warn($"[Cleanup] Could not trash {entry}: {ex.Message}"); }
            }
            return gone;
        }

        private IEnumerable<string> Leftovers()
        {
            // Everything in RetroArr's work folders and the owner's folders, not the folders themselves
            foreach (var folder in WorkFolders.Concat(OwnerFolders).Distinct(StringComparer.Ordinal).Where(Directory.Exists))
                foreach (var entry in Entries(folder).Where(Old))
                    yield return entry;
            // RetroArr's half-written files in the library: roms/<platform>/ and one level below (roms/ps5/pkg)
            foreach (var platform in LibraryFolders())
                foreach (var entry in Entries(platform).Where(e => OurPrefixes.Any(p => Path.GetFileName(e).StartsWith(p, StringComparison.Ordinal))).Where(Old))
                    yield return entry;
        }

        private IEnumerable<string> LibraryFolders()
        {
            var media = _config.LoadMediaSettings();
            var root = !string.IsNullOrEmpty(media.DestinationPath) && Path.IsPathRooted(media.DestinationPath) ? media.DestinationPath : media.FolderPath;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;
            foreach (var platform in SafeDirectories(root))
            {
                yield return platform;
                foreach (var sub in SafeDirectories(platform).Where(d => !Path.GetFileName(d).StartsWith('.'))) yield return sub;
            }
        }

        private static IEnumerable<string> SafeDirectories(string folder)
        {
            try { return Directory.GetDirectories(folder); }
            catch (Exception) { return Array.Empty<string>(); }
        }

        private static IEnumerable<string> Entries(string folder)
        {
            try { return Directory.GetFileSystemEntries(folder).OrderBy(e => e, StringComparer.Ordinal).ToList(); }
            catch (Exception) { return Array.Empty<string>(); }
        }

        // Not written to for Age: the newest file or folder inside counts. A copy or an unpacked archive keeps its
        // files' old dates, but the folders it creates are new, so a copy or an unpack still running is never old.
        private bool Old(string entry)
        {
            try
            {
                var newest = Directory.Exists(entry)
                    ? Directory.EnumerateFileSystemEntries(entry, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Take(50000).Select(e => Directory.Exists(e) ? Directory.GetLastWriteTimeUtc(e) : File.GetLastWriteTimeUtc(e))
                        .Append(Directory.GetLastWriteTimeUtc(entry)).Max()
                    : File.GetLastWriteTimeUtc(entry);
                return DateTime.UtcNow - newest >= Age;
            }
            catch (Exception) { return false; }
        }
    }
}
