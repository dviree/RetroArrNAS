using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RetroArr.Core.Configuration;
using RetroArr.Core.IO;

namespace RetroArr.Core.Games
{
    /// <summary>
    /// GameSauce's BIOS/firmware sorter: what is dropped into the BIOS folder (files, folders, ZIP/7z/RAR) is sorted into
    /// a folder per platform ("psx/scph5501.bin", "switch/keys/prod.keys", "switch/firmware/..."), by the known BIOS
    /// file names and the platform words in the name or the folder it came in. What cannot be placed goes to
    /// "unsorted". The platform folders themselves are never sorted again. Nothing is deleted: an exact duplicate, a
    /// smaller file of the same name, an unpacked archive and junk (images, .nfo, .url) go to the trash.
    /// BIOS_SORT_FOLDER picks the folder (default /nas/games/bios = /volume1/games/bios); BIOS_SORT=false turns it off.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public class BiosSorter : BackgroundService
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetLogger(Logging.AppLoggerService.General);

        private readonly TrashService _trash;
        private readonly IArchiveService _archives;

        internal string Root = Environment.GetEnvironmentVariable("BIOS_SORT_FOLDER") ?? "/nas/games/bios";
        internal TimeSpan SettleTime = TimeSpan.FromSeconds(60);
        internal TimeSpan Interval = TimeSpan.FromMinutes(5);

        public BiosSorter(TrashService trash, IArchiveService archives)
        {
            _trash = trash;
            _archives = archives;
        }

        public static bool Enabled =>
            !string.Equals(Environment.GetEnvironmentVariable("BIOS_SORT"), "false", StringComparison.OrdinalIgnoreCase);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(4), stoppingToken); }
            catch (TaskCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try { if (Enabled) await RunOnceAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.Warn($"[BIOS] Run failed: {ex.Message}"); }
                try { await Task.Delay(Interval, stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        // The platform folders (RomM's names, as GameSauce used them)
        internal static readonly HashSet<string> Platforms = new(StringComparer.OrdinalIgnoreCase)
        {
            "psx", "ps2", "ps3", "psp", "dc", "saturn", "segacd", "sega32", "gba", "nds", "3ds", "switch", "ngc", "wii", "wiiu",
            "xbox", "xbox360", "neogeo", "3do", "pce", "pcecd", "amiga", "c64", "msx", "arcade", "lynx", "satellaview", "snes",
            "zxs", "colecovision", "atari7800", "sms", "pc-fx", "odyssey-2", "fairchild-channel-f", "appleii", "gb", "gbc", "vectrex",
        };
        private const string Unsorted = "unsorted";

        private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".url", ".html", ".htm", ".nfo", ".sfv", ".md" };

        private static readonly Dictionary<string, (string Kind, string Platform)> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            ["prod.keys"] = ("keys", "switch"), ["title.keys"] = ("keys", "switch"),
            ["scph5500.bin"] = ("bios", "psx"), ["scph5501.bin"] = ("bios", "psx"), ["scph5502.bin"] = ("bios", "psx"), ["scph1001.bin"] = ("bios", "psx"),
            ["dc_boot.bin"] = ("bios", "dc"), ["dc_flash.bin"] = ("bios", "dc"),
            ["gba_bios.bin"] = ("bios", "gba"), ["bios7.bin"] = ("bios", "nds"), ["bios9.bin"] = ("bios", "nds"),
            ["ps3updat.pup"] = ("firmware", "ps3"),
            ["sega_101.bin"] = ("bios", "saturn"), ["mpr-17933.bin"] = ("bios", "saturn"),
            ["bios_cd_e.bin"] = ("bios", "segacd"), ["bios_cd_u.bin"] = ("bios", "segacd"), ["bios_cd_j.bin"] = ("bios", "segacd"),
            ["32x_g_bios.bin"] = ("bios", "sega32"), ["32x_m_bios.bin"] = ("bios", "sega32"), ["32x_s_bios.bin"] = ("bios", "sega32"),
            ["lynxboot.img"] = ("bios", "lynx"), ["neogeo.zip"] = ("bios", "neogeo"), ["kick34005.a500"] = ("bios", "amiga"),
            ["dmg.bin"] = ("bios", "gb"), ["cgb.bin"] = ("bios", "gbc"), ["vectrex.bin"] = ("bios", "vectrex"), ["trdos.rom"] = ("bios", "zxs"),
            ["coleco.rom"] = ("bios", "colecovision"), ["bios_j.sms"] = ("bios", "sms"),
        };

        // File-name prefixes that name the platform outright
        private static readonly (string Prefix, string Platform)[] Prefixes =
        {
            ("bs-x", "satellaview"), ("snes_", "snes"), ("st010", "snes"), ("st011", "snes"), ("st018", "snes"), ("zx_", "zxs"),
            ("coleco_bios", "colecovision"), ("a78_", "atari7800"), ("7800_bios", "atari7800"), ("sms_", "sms"), ("pcfx_", "pc-fx"),
            ("pc-fx", "pc-fx"), ("o2_", "odyssey-2"), ("channelf_", "fairchild-channel-f"), ("channel_f", "fairchild-channel-f"),
            ("appleii", "appleii"), ("apple2", "appleii"), ("vectrex_", "vectrex"), ("sat_", "saturn"),
        };

        // Words in the name or the folder it came in, the more specific first
        private static readonly (string Platform, string[] Words)[] Hints =
        {
            ("ps2", new[] { "playstation 2", "playstation2", "pcsx2", "ps2" }),
            ("psx", new[] { "playstation 1", "playstation1", "ps1", "psx", "duckstation", "swanstation" }),
            ("ps3", new[] { "playstation 3", "rpcs3", "ps3" }),
            ("psp", new[] { "ppsspp", "psp" }),
            ("dc", new[] { "dreamcast", "flycast" }),
            ("sega32", new[] { "sega 32x", "sega32x", "32x" }),
            ("segacd", new[] { "sega cd", "segacd", "mega cd", "megacd" }),
            ("saturn", new[] { "saturn" }),
            ("satellaview", new[] { "satellaview", "bs-x" }),
            ("gba", new[] { "game boy advance", "gameboy advance", "gba" }),
            ("nds", new[] { "nintendo ds", "melonds", "nds" }),
            ("3ds", new[] { "nintendo 3ds", "citra", "3ds" }),
            ("switch", new[] { "nintendo switch", "switch", "ryujinx", "yuzu", "eden" }),
            ("wiiu", new[] { "wii u", "wiiu", "cemu" }),
            ("ngc", new[] { "gamecube", "dolphin" }),
            ("wii", new[] { "wii" }),
            ("xbox360", new[] { "xbox 360", "xbox360", "xenia" }),
            ("xbox", new[] { "xbox" }),
            ("neogeo", new[] { "neo geo", "neogeo" }),
            ("3do", new[] { "3do" }),
            ("pcecd", new[] { "pc engine cd", "turbografx cd", "pcecd" }),
            ("pce", new[] { "pc engine", "turbografx" }),
            ("pc-fx", new[] { "pc-fx", "pcfx" }),
            ("amiga", new[] { "amiga", "kickstart" }),
            ("c64", new[] { "commodore 64", "c64" }),
            ("msx", new[] { "msx" }),
            ("lynx", new[] { "atari lynx", "lynx" }),
            ("atari7800", new[] { "atari 7800" }),
            ("colecovision", new[] { "colecovision", "coleco vision" }),
            ("sms", new[] { "sega master system", "master system" }),
            ("zxs", new[] { "zx spectrum", "tr-dos", "trdos" }),
            ("odyssey-2", new[] { "odyssey 2", "odyssey2" }),
            ("fairchild-channel-f", new[] { "channel f", "channelf" }),
            ("appleii", new[] { "apple ii", "apple iie" }),
            ("vectrex", new[] { "vectrex" }),
            ("arcade", new[] { "arcade", "mame" }),
        };

        /// <summary>The folder under the BIOS root a file belongs in ("psx", "switch/keys"), or "unsorted".</summary>
        internal static string FolderFor(string fileName, string? cameIn = null)
        {
            if (Known.TryGetValue(fileName, out var known))
                return known.Kind switch { "keys" => "switch/keys", "firmware" when known.Platform == "switch" => "switch/firmware", _ => known.Platform };
            var name = fileName.ToLowerInvariant();
            var prefix = Prefixes.FirstOrDefault(p => name.StartsWith(p.Prefix, StringComparison.Ordinal));
            if (prefix.Platform != null) return prefix.Platform;
            if (name.EndsWith(".sms", StringComparison.Ordinal)) return "sms";
            // Sony's own BIOS names: four digits for the first PlayStation (scph7502), five for the PS2 (scph39001)
            if (System.Text.RegularExpressions.Regex.Match(name, @"^scph-?(\d+)") is { Success: true } scph)
                return scph.Groups[1].Value.Length == 4 ? "psx" : "ps2";
            foreach (var text in new[] { name, cameIn?.ToLowerInvariant() })
            {
                if (string.IsNullOrEmpty(text)) continue;
                var hint = Hints.FirstOrDefault(h => h.Words.Any(w => ContainsWord(text, w)));
                if (hint.Platform != null)
                    return hint.Platform == "switch" && Path.GetExtension(name) is ".nca" or ".bin" ? "switch/firmware" : hint.Platform;
            }
            return Unsorted;
        }

        // "ps2" in "ps2_bios.bin" or "Sony PS2", not in "ups2x"
        private static bool ContainsWord(string text, string word)
        {
            for (var i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
            {
                var before = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
                var end = i + word.Length;
                var after = end == text.Length || !char.IsLetter(text[end]);
                if (before && after) return true;
            }
            return false;
        }

        /// <summary>Sorts what lies loose in the BIOS folder. Returns how many files were placed.</summary>
        public async Task<int> RunOnceAsync(CancellationToken ct = default)
        {
            if (!Directory.Exists(Root)) return 0;
            Directory.CreateDirectory(Path.Combine(Root, Unsorted));
            var placed = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(Root).ToList())
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.') || Platforms.Contains(name) || name.Equals(Unsorted, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Settled(entry)) continue;
                try { placed += await SortAsync(entry, name, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.Warn($"[BIOS] Could not sort {entry}: {ex.Message}"); }
            }
            return placed;
        }

        private async Task<int> SortAsync(string entry, string cameIn, CancellationToken ct)
        {
            if (Directory.Exists(entry))
            {
                var placed = 0;
                foreach (var file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).ToList())
                    placed += await SortFileAsync(file, Path.GetRelativePath(Root, Path.GetDirectoryName(file)!), ct);
                // What is left is empty folders
                if (!Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).Any()) await _trash.MoveAsync(entry, null, "BIOS");
                return placed;
            }
            return await SortFileAsync(entry, null, ct);
        }

        private async Task<int> SortFileAsync(string file, string? cameIn, CancellationToken ct)
        {
            var name = Path.GetFileName(file);
            if (Junk.Contains(Path.GetExtension(name))) { await _trash.MoveAsync(file, null, "BIOS"); return 0; }
            if (_archives.IsArchive(file) && !Known.ContainsKey(name)) return await SortArchiveAsync(file, cameIn, ct);
            return await PlaceAsync(file, FolderFor(name, cameIn)) ? 1 : 0;
        }

        // An archive is unpacked and its files sorted; a Switch firmware package (many NCAs) goes whole into
        // switch/firmware/<name>
        private async Task<int> SortArchiveAsync(string archive, string? cameIn, CancellationToken ct)
        {
            var staging = Path.Combine(Root, ".retroarr-bios-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var volumes = new List<string>();
                if (!_archives.Extract(archive, staging, volumes))
                {
                    _logger.Warn($"[BIOS] Could not unpack {archive}; it goes to unsorted");
                    return await PlaceAsync(archive, Unsorted) ? 1 : 0;
                }
                var stem = Path.GetFileNameWithoutExtension(archive);
                var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).ToList();
                var placed = 0;
                if (files.Count(f => f.EndsWith(".nca", StringComparison.OrdinalIgnoreCase)) >= 10)
                {
                    var target = Path.Combine(Root, "switch", "firmware", stem);
                    if (Directory.Exists(target)) _logger.Info($"[BIOS] {target} is there already; {Path.GetFileName(archive)} goes to the trash");
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        Directory.Move(staging, target);
                        _logger.Info($"[BIOS] Switch firmware {Path.GetFileName(archive)} -> {target}");
                        placed = files.Count;
                    }
                }
                else
                    foreach (var file in files)
                        placed += await SortFileAsync(file, string.Join(' ', new[] { cameIn, stem }.Where(s => !string.IsNullOrEmpty(s))), ct);
                foreach (var part in volumes.Append(archive).Distinct().Where(File.Exists)) await _trash.MoveAsync(part, null, "BIOS");
                return placed;
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception) { }
            }
        }

        // Into Root/folder under its own name. The same content there already, or a bigger file of the same name,
        // sends this one to the trash; a smaller one there goes to the trash for it.
        private async Task<bool> PlaceAsync(string file, string folder)
        {
            var dir = Path.Combine(Root, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, Path.GetFileName(file));
            var size = new FileInfo(file).Length;
            var hash = Hash(file);
            var same = Directory.EnumerateFiles(dir).FirstOrDefault(f => new FileInfo(f).Length == size && Hash(f) == hash);
            if (same != null)
            {
                _logger.Info($"[BIOS] {Path.GetFileName(file)} is in {folder} already as {Path.GetFileName(same)}; to the trash");
                await _trash.MoveAsync(file, null, "BIOS");
                return false;
            }
            if (File.Exists(target))
            {
                if (new FileInfo(target).Length >= size)
                {
                    _logger.Info($"[BIOS] {folder}/{Path.GetFileName(file)} is there and not smaller; the new one goes to the trash");
                    await _trash.MoveAsync(file, null, "BIOS");
                    return false;
                }
                if (await _trash.MoveAsync(target, null, "BIOS") == null) return false;
            }
            File.Move(file, target);
            _logger.Info($"[BIOS] {Path.GetFileName(file)} -> {folder}");
            return true;
        }

        private bool Settled(string entry)
        {
            try
            {
                var newest = Directory.Exists(entry)
                    ? Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(entry)).Max()
                    : File.GetLastWriteTimeUtc(entry);
                return DateTime.UtcNow - newest >= SettleTime;
            }
            catch (Exception) { return false; }
        }

        private static string Hash(string file)
        {
            using var stream = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
