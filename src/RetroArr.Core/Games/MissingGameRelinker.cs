using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RetroArr.Core.Configuration;

namespace RetroArr.Core.Games
{
    /// <summary>
    /// Finds games marked missing whose files were moved or renamed inside the library (a Switch merge, a PS rename, a
    /// PKG moved into roms/ps5/pkg, a game moved from roms/steam to roms/pc) and points them at where they are now.
    /// A game is relinked only when exactly one entry matches it: by its PS title ID, by its Switch base title ID, or
    /// by its title. Nothing on disk is changed. Runs a few minutes after start and then every hour.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public class MissingGameRelinker : BackgroundService
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetLogger(Logging.AppLoggerService.General);
        private static readonly Regex PsId = new(@"(?<![A-Za-z0-9])(PPSA|CUSA|ECAS)[-_]?(\d{5})(?!\d)", RegexOptions.IgnoreCase);
        private static readonly Regex SwitchId = new(@"(?<![0-9A-Fa-f])(01[0-9A-Fa-f]{14})(?![0-9A-Fa-f])");

        private readonly ConfigurationService _config;
        private readonly IGameRepository _games;

        public MissingGameRelinker(ConfigurationService config, IGameRepository games)
        {
            _config = config;
            _games = games;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); }
            catch (TaskCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunOnceAsync(); }
                catch (Exception ex) { _logger.Warn($"[Relink] Run failed: {ex.Message}"); }
                try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        /// <summary>Relinks what it can. Returns how many games were pointed at their new place.</summary>
        public async Task<int> RunOnceAsync()
        {
            var media = _config.LoadMediaSettings();
            var roots = new[] { media.DestinationPath, media.FolderPath }
                .Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Select(r => Path.TrimEndingDirectorySeparator(r!)).Distinct().ToList();
            if (roots.Count == 0) return 0;
            var relinked = 0;
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var game in await _games.GetMissingAsync())
            {
                var old = game.ExecutablePath ?? game.Path;
                if (string.IsNullOrEmpty(old) || File.Exists(old) || Directory.Exists(old)) continue;
                var root = roots.FirstOrDefault(r => old.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                if (root == null) continue;
                var platformFolder = Path.Combine(root, old[(root.Length + 1)..].Split(Path.DirectorySeparatorChar)[0]);
                var found = Find(game, old, platformFolder, taken) ?? Find(game, old, null, taken, roots);
                if (found == null) continue;
                taken.Add(found);
                var newPlatform = PlatformFolderOf(found, roots);
                var platformId = PlatformDefinitions.AllPlatforms.FirstOrDefault(p => p.MatchesFolderName(newPlatform))?.Id;
                var pathWasEntry = game.Path == null || game.Path == game.ExecutablePath || game.ExecutablePath == null;
                await _games.UpdateFieldsAsync(game.Id, g =>
                {
                    if (g.ExecutablePath != null) g.ExecutablePath = found;
                    g.Path = pathWasEntry || Directory.Exists(found) ? found : Path.GetDirectoryName(found);
                    if (platformId is > 0) g.PlatformId = platformId.Value;
                });
                await _games.ApplyContentStateAsync(game.Id, GameContent.Present, DateTime.UtcNow, found);
                _logger.Info($"[Relink] {game.Title}: {old} -> {found}");
                relinked++;
            }
            return relinked;
        }

        // The platform folder name an entry lies in ("pc" for roms/pc/Game/x.exe)
        private static string PlatformFolderOf(string path, IEnumerable<string> roots)
        {
            var root = roots.First(r => path.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            return path[(root.Length + 1)..].Split(Path.DirectorySeparatorChar)[0];
        }

        // One entry in the platform folder (or, by title only, in any platform folder) that is this game
        private static string? Find(Game game, string old, string? platformFolder, HashSet<string> taken, IReadOnlyList<string>? roots = null)
        {
            var oldName = Path.GetFileName(old);
            var psId = PsId.Match(oldName) is { Success: true } p ? (p.Groups[1].Value + p.Groups[2].Value).ToUpperInvariant() : null;
            var switchBase = SwitchId.Match(oldName) is { Success: true } s ? SwitchBase(s.Groups[1].Value) : (ulong?)null;
            var titleKey = Key(game.Title);

            IEnumerable<string> entries;
            if (platformFolder != null)
            {
                if (!Directory.Exists(platformFolder)) return null;
                var top = Directory.EnumerateFileSystemEntries(platformFolder).ToList();
                entries = top.Concat(top.Where(Directory.Exists).SelectMany(SafeEntries));
            }
            else
            {
                // Another platform folder: only an exact title match, so a game moved from roms/steam to roms/pc is found
                if (psId != null || switchBase != null || titleKey.Length < 4) return null;
                entries = roots!.SelectMany(r => Directory.EnumerateDirectories(r)).SelectMany(SafeEntries);
            }

            var matches = entries.Where(e => !taken.Contains(e) && !Path.GetFileName(e).StartsWith('.')).Where(e =>
            {
                var name = Path.GetFileName(e);
                if (psId != null) return PsId.Match(name) is { Success: true } m && (m.Groups[1].Value + m.Groups[2].Value).ToUpperInvariant() == psId;
                if (switchBase != null) return SwitchId.Match(name) is { Success: true } m && SwitchBase(m.Groups[1].Value) == switchBase;
                return Key(Regex.Replace(Path.GetFileNameWithoutExtension(name), @"[\[\(][^\]\)]*[\]\)]", "")) == titleKey;
            }).ToList();
            // Prefer the same kind of entry the game had (a file for a file, a folder for a folder, the same extension)
            var sameKind = matches.Where(m => string.Equals(Path.GetExtension(m), Path.GetExtension(old), StringComparison.OrdinalIgnoreCase)).ToList();
            var pick = sameKind.Count == 1 ? sameKind : matches;
            return pick.Count == 1 ? pick[0] : null;
        }

        private static IEnumerable<string> SafeEntries(string folder)
        {
            try { return Directory.EnumerateFileSystemEntries(folder).ToList(); }
            catch (Exception) { return Array.Empty<string>(); }
        }

        // Update = base + 0x800, DLC = base + 0x1000 + n
        private static ulong SwitchBase(string hex)
        {
            var id = Convert.ToUInt64(hex, 16);
            return (id & 0xFFF) switch { 0 => id, 0x800 => id - 0x800, _ => (id & ~0xFFFUL) - 0x1000 };
        }

        private static string Key(string? s) => new string((s ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
