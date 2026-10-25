using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RetroArr.Core.Indexers;
using RetroArr.Core.Prowlarr;

namespace RetroArr.Core.Download
{
    /// <summary>
    /// A folder on disk read as a download client: every game folder or game file directly in it is a finished
    /// download, so games that got there some other way (JDownloader, a manual copy, an old inbox) go through the same
    /// import as a grab: platform, IGDB name, library folder. It downloads nothing and, like a torrent client, its
    /// files are only ever copied out, never deleted (see PostDownloadProcessor.KeepSourceClients).
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public class WatchFolderClient : IDownloadClient
    {
        public const string ImplementationName = "WatchFolder";

        private static readonly NLog.Logger _logger = NLog.LogManager.GetLogger(Logging.AppLoggerService.DownloadClient);

        // Something written to in this window may still be on its way in
        internal static TimeSpan SettleTime = TimeSpan.FromMinutes(2);

        // PS4/PS5 images and packages are big and written slowly by converters: they have to be still for longer
        internal static TimeSpan PlayStationSettleTime = TimeSpan.FromMinutes(15);

        // While this file exists a PS5 converter is writing images; nothing PlayStation is taken meanwhile
        internal static string PlayStationHoldFile =
            Environment.GetEnvironmentVariable("PS_HOLD_FILE") ?? "/host-tmp/ps5-smart-converter.lock";

        // Half-finished downloads from JDownloader, browsers and the like
        private static readonly string[] PartialExtensions = { ".part", ".partial", ".crdownload", ".tmp", ".!qb", ".aria2" };

        // Extensions that name their platform, added to the title as the tag PlatformDetector knows ("Wii Party WBFS")
        private static readonly Dictionary<string, string> ExtensionTags = new(StringComparer.OrdinalIgnoreCase)
        {
            [".wbfs"] = "WBFS", [".nsp"] = "NSP", [".nsz"] = "NSP", [".xci"] = "XCI", [".xcz"] = "XCI",
            [".cia"] = "3DS", [".3ds"] = "3DS", [".nds"] = "NDS", [".gba"] = "GBA", [".gbc"] = "GBC", [".gb"] = "Game Boy",
            [".nes"] = "NES", [".sfc"] = "SNES", [".smc"] = "SNES", [".z64"] = "N64", [".n64"] = "N64", [".v64"] = "N64",
            [".wua"] = "Wii U", [".wux"] = "Wii U", [".wud"] = "Wii U", [".gdi"] = "Dreamcast", [".cdi"] = "Dreamcast",
            [".vpk"] = "PSVita", [".md"] = "Genesis", [".gen"] = "Genesis", [".smd"] = "Genesis", [".sms"] = "Master System",
            [".gg"] = "Game Gear", [".exfat"] = "PS5", [".ffpfsc"] = "PS5",
        };

        // Sony title IDs in a release or folder name
        private static readonly (Regex Pattern, string Tag)[] TitleIdTags =
        {
            (new Regex(@"(?<![A-Za-z0-9])(PPSA|ECAS)[-_]?\d{5}(?!\d)", RegexOptions.IgnoreCase), "PS5"),
            (new Regex(@"(?<![A-Za-z0-9])CUSA[-_]?\d{5}(?!\d)", RegexOptions.IgnoreCase), "PS4"),
            (new Regex(@"\b(BC|BL|NP)[UEJAK][SB]\d{5}\b", RegexOptions.IgnoreCase), "PS3"),
        };

        private readonly string _folder;
        private readonly string? _category;
        private readonly bool _recursive;

        // Torrent clients' save folders inside an inbox: their files seed and are never a watch folder's to take
        private static readonly HashSet<string> TorrentFolders = new(StringComparer.OrdinalIgnoreCase) { "torrents", "qbit-romarr" };

        // Leftovers and NAS bookkeeping that are never a game
        private static readonly HashSet<string> NeverGames = new(StringComparer.OrdinalIgnoreCase)
        {
            "_CommonRedist", "CommonRedist", "__MACOSX", "@eaDir", "#recycle", "$RECYCLE.BIN", "System Volume Information"
        };

        // Files that say nothing about whether a folder is a game
        private static readonly HashSet<string> JunkExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".nfo", ".url", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".sfv", ".md5", ".sha1", ".db", ".ini", ".log", ".xml", ".par2", ".torrent"
        };

        // Films, series and music that end up in a download folder too: never a game, and never taken
        private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".webm", ".flv", ".srt", ".sub", ".idx",
            ".ass", ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".wav", ".opus"
        };

        // One game per file, as in a ROM set; multi-disc and multi-track files share a name once their disc/track part is gone
        private static readonly HashSet<string> RomExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".nes", ".fds", ".sfc", ".smc", ".gb", ".gbc", ".gba", ".nds", ".3ds", ".cia", ".n64", ".z64", ".v64", ".md", ".gen", ".smd",
            ".sms", ".gg", ".32x", ".pce", ".a26", ".a78", ".lnx", ".ws", ".wsc", ".ngp", ".ngc", ".vb", ".iso", ".chd", ".cso", ".rvz",
            ".gcz", ".wbfs", ".wua", ".nsp", ".nsz", ".xci", ".xcz", ".pbp", ".cue", ".bin", ".gdi", ".cdi", ".zip", ".7z",
            ".exfat", ".ffpfsc", ".pkg"
        };
        private static readonly HashSet<string> PlayStationFiles = new(StringComparer.OrdinalIgnoreCase) { ".exfat", ".ffpfsc", ".pkg" };
        private static readonly Regex DiscPart = new(@"\s*[\(\[]?\b(disc|disk|cd|track)\s*\d+[\)\]]?", RegexOptions.IgnoreCase);
        private const int MaxDepth = 6;

        public WatchFolderClient(string folder, string? category = null, bool recursive = false)
        {
            _folder = folder;
            _category = category;
            _recursive = recursive;
        }

        public Task<bool> TestConnectionAsync() => Task.FromResult(Readable(_folder));

        public Task<string> GetVersionAsync() => Task.FromResult(Readable(_folder) ? "folder" : string.Empty);

        // A folder RetroArr may not list (made by root with mode 700) exists, yet every poll would fail on it
        private static bool Readable(string folder)
        {
            try
            {
                using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
                entries.MoveNext();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public Task<bool> AddTorrentAsync(string url, string? category = null) =>
            throw new NotSupportedException("A watch folder downloads nothing. Configure qBittorrent, Transmission or Deluge for torrents.");

        public Task<bool> AddNzbAsync(string url, string? category = null) =>
            throw new NotSupportedException("A watch folder downloads nothing. Configure SABnzbd or NZBGet for usenet.");

        // The folder is the user's: nothing in it is removed, paused or resumed from RetroArr
        public Task<bool> RemoveDownloadAsync(string id, bool deleteFiles) => Task.FromResult(false);
        public Task<bool> PauseDownloadAsync(string id) => Task.FromResult(false);
        public Task<bool> ResumeDownloadAsync(string id) => Task.FromResult(false);

        public Task<List<DownloadStatus>> GetDownloadsAsync()
        {
            var result = new List<DownloadStatus>();
            if (!Directory.Exists(_folder))
            {
                _logger.Warn($"[WatchFolder] {_folder} does not exist or is not mounted");
                return Task.FromResult(result);
            }

            var entries = _recursive
                ? Units(new DirectoryInfo(_folder), 0)
                : new DirectoryInfo(_folder).EnumerateFileSystemInfos().Where(e => !e.Name.StartsWith('.'));
            foreach (var entry in entries)
            {
                try
                {
                    var status = Describe(entry);
                    if (status != null) result.Add(status);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[WatchFolder] Skipping {entry.FullName}: {ex.Message}");
                }
            }
            return Task.FromResult(result);
        }

        private DownloadStatus? Describe(FileSystemInfo entry)
        {
            var files = entry is DirectoryInfo dir
                ? dir.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList()
                : new List<FileInfo> { (FileInfo)entry };
            files = files.Where(f => !f.Name.StartsWith('.')).ToList();
            if (files.Count == 0) return null;
            if (files.All(f => MediaExtensions.Contains(f.Extension) || JunkExtensions.Contains(f.Extension))) return null;

            var platform = DetectPlatform(entry.Name, files);
            if (PlayStationImporter.IsPlayStation(platform))
                return DescribePlayStation(entry, files, platform!);

            var newest = files.Max(f => f.LastWriteTimeUtc);
            var settled = DateTime.UtcNow - newest >= SettleTime
                && !files.Any(f => PartialExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase));

            return new DownloadStatus
            {
                Id = IdFor(entry.FullName),
                Name = entry.Name,
                Size = files.Sum(f => f.Length),
                Progress = settled ? 100 : 0,
                State = settled ? DownloadState.Completed : DownloadState.Downloading,
                Category = _category,
                DownloadPath = entry.FullName,
                PlatformFolder = platform
            };
        }

        private DownloadStatus DescribePlayStation(FileSystemInfo entry, List<FileInfo> files, string platform)
        {
            var package = Games.PlayStationPackageInspector.Inspect(entry.FullName);
            var what = $"{platform.ToUpperInvariant()} {Describe(package)}";
            string? held = package.Format switch
            {
                Games.PlayStationFormat.Archive => "not imported, unpacking PlayStation archives is not built yet",
                Games.PlayStationFormat.Unknown => "not imported, no game, image or package found",
                Games.PlayStationFormat.Folder or Games.PlayStationFormat.Lz4Folder when !PlayStationImporter.ImportFolders =>
                    "left for the converter (PS_IMPORT_FOLDERS=true imports game folders)",
                _ => null
            };
            var settled = DateTime.UtcNow - files.Max(f => f.LastWriteTimeUtc) >= PlayStationSettleTime
                && !files.Any(f => PartialExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                && !File.Exists(PlayStationHoldFile);
            return new DownloadStatus
            {
                Id = IdFor(entry.FullName),
                Name = entry.Name,
                Size = files.Sum(f => f.Length),
                Progress = held == null && settled ? 100 : 0,
                State = held != null ? DownloadState.Paused : settled ? DownloadState.Completed : DownloadState.Downloading,
                Category = _category,
                DownloadPath = entry.FullName,
                PlatformFolder = platform,
                StatusMessages = { held != null ? $"{what}: {held}" : settled ? what : $"{what}: waiting until it is no longer written to" }
            };
        }

        /// <summary>
        /// The games under a folder: a folder holding game files (or a PS game folder) is one game, a folder of folders is
        /// a collection to look into, and a folder of single-file ROMs with different names is one game per file.
        /// Hidden folders and torrent save folders are left out.
        /// </summary>
        private IEnumerable<FileSystemInfo> Units(DirectoryInfo dir, int depth)
        {
            IEnumerable<FileSystemInfo> children;
            try { children = dir.EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn($"[WatchFolder] Cannot list {dir.FullName}: {ex.Message}");
                yield break;
            }
            foreach (var entry in children)
            {
                if (entry.Name.StartsWith('.')) continue;
                if (entry is FileInfo file)
                {
                    if (!JunkExtensions.Contains(file.Extension) && !MediaExtensions.Contains(file.Extension)) yield return file;
                    continue;
                }
                var sub = (DirectoryInfo)entry;
                if (TorrentFolders.Contains(sub.Name) || NeverGames.Contains(sub.Name)) continue;
                // A game is taken whole; a collection or a ROM set is looked into, where loose ROMs are one game each
                if (Classify(sub, depth) == Kind.Game) yield return sub;
                else foreach (var unit in Units(sub, depth + 1)) yield return unit;
            }
        }

        private enum Kind { Game, Collection, RomSet }

        private static Kind Classify(DirectoryInfo dir, int depth)
        {
            if (depth >= MaxDepth) return Kind.Game;
            if (File.Exists(Path.Combine(dir.FullName, "eboot.bin")) || Directory.Exists(Path.Combine(dir.FullName, "sce_sys"))) return Kind.Game;
            var files = dir.EnumerateFiles().Where(f => !f.Name.StartsWith('.') && !JunkExtensions.Contains(f.Extension)
                && !MediaExtensions.Contains(f.Extension)).ToList();
            if (files.Count == 0) return Kind.Collection;
            // A ROM set: only single-file ROMs, more than one game among them, each game one file
            if (files.All(f => RomExtensions.Contains(f.Extension)))
            {
                var games = files.GroupBy(f => DiscPart.Replace(Path.GetFileNameWithoutExtension(f.Name), "").Trim().ToLowerInvariant()).ToList();
                if (games.Count > 1 && games.All(g => g.Count() == 1)) return Kind.RomSet;
                // One ROM in a folder named for something else (a site, a source) is the game, not the folder
                // except an image in a folder that names its title ID ("PPSA01234 Game.ffpfsc/game.ffpfsc")
                if (games.Count == 1 && games[0].Count() == 1 && !NamesAlike(dir.Name, games[0].Key)
                    && !(PlayStationFiles.Contains(files[0].Extension) && Games.PlayStationPackageInspector.TitleIdIn(dir.Name).Length > 0))
                    return Kind.RomSet;
            }
            return Kind.Game;
        }

        // At least half of the folder name's words appear in the file's name
        private static bool NamesAlike(string folder, string file)
        {
            static HashSet<string> Words(string s) => Regex.Matches(s.ToLowerInvariant(), @"[a-z0-9]+").Select(m => m.Value).ToHashSet();
            var a = Words(folder);
            return a.Count > 0 && a.Count(Words(file).Contains) * 2 >= a.Count;
        }

        internal static string Describe(Games.PlayStationPackage p)
        {
            var format = p.Format switch
            {
                Games.PlayStationFormat.Folder => "uncompressed game folder",
                Games.PlayStationFormat.Lz4Folder => "LZ4-compressed game folder",
                Games.PlayStationFormat.ExfatImage => "exFAT image",
                Games.PlayStationFormat.FfpfscImage => "FFPFSC image",
                Games.PlayStationFormat.Pkg => "PKG",
                Games.PlayStationFormat.Archive => "archive (not unpacked)",
                _ => "unknown format"
            };
            if (p.Format == Games.PlayStationFormat.Archive && p.Inside != Games.PlayStationFormat.Unknown)
                format = $"archive holding {Describe(p with { Format = p.Inside, TitleId = null, Version = null })}";
            var id = string.Join(" ", new[] { p.TitleId, p.Version != null ? "v" + p.Version : null }.Where(s => s != null));
            return id.Length > 0 ? $"{format} ({id})" : format;
        }

        // The path decides the id, so a folder that is renamed or replaced counts as a new download
        internal static string IdFor(string path) =>
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();

        /// <summary>The platform folder for a name and the files under it, or null when nothing names one.</summary>
        internal static string? DetectPlatform(string name, IEnumerable<FileInfo> files)
        {
            var tags = files.Select(f => ExtensionTags.TryGetValue(f.Extension, out var tag) ? tag : null)
                .Where(t => t != null).Distinct().ToList();
            tags.AddRange(TitleIdTags.Where(t => t.Pattern.IsMatch(name)).Select(t => t.Tag));
            var probe = new SearchResult { Title = $"{name} {string.Join(' ', tags)}" };
            PlatformDetector.DetectPlatform(probe);
            return string.IsNullOrEmpty(probe.PlatformFolder) || probe.PlatformFolder == "unknown" ? null : probe.PlatformFolder;
        }
    }
}
