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

        // PS4/PS5 packages stay with their own tooling, they are never copied around by the watcher
        private static readonly HashSet<string> SkippedPlatforms = new(StringComparer.OrdinalIgnoreCase) { "ps4", "ps5" };

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
            [".gg"] = "Game Gear",
        };

        // Sony title IDs in a release or folder name
        private static readonly (Regex Pattern, string Tag)[] TitleIdTags =
        {
            (new Regex(@"\b(PPSA|ECAS)\d{5}\b", RegexOptions.IgnoreCase), "PS5"),
            (new Regex(@"\bCUSA\d{5}\b", RegexOptions.IgnoreCase), "PS4"),
            (new Regex(@"\b(BC|BL|NP)[UEJAK][SB]\d{5}\b", RegexOptions.IgnoreCase), "PS3"),
        };

        private readonly string _folder;
        private readonly string? _category;

        public WatchFolderClient(string folder, string? category = null)
        {
            _folder = folder;
            _category = category;
        }

        public Task<bool> TestConnectionAsync() => Task.FromResult(Directory.Exists(_folder));

        public Task<string> GetVersionAsync() => Task.FromResult(Directory.Exists(_folder) ? "folder" : string.Empty);

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

            foreach (var entry in new DirectoryInfo(_folder).EnumerateFileSystemInfos())
            {
                // Hidden entries are other tools' work folders (.gamesauce-work, .retroarr-extract-...)
                if (entry.Name.StartsWith('.')) continue;
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

            var platform = DetectPlatform(entry.Name, files);
            if (platform != null && SkippedPlatforms.Contains(platform)) return null;

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
