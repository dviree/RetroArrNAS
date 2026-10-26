using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RetroArr.Core.Configuration;

namespace RetroArr.Core.Games
{
    /// <summary>
    /// Merges a Switch game's base, latest update and DLC in the library into one NSP with NSC_Builder (bundled in the
    /// image at /opt/nscb), the way GameSauce's switch merger and the old host worker did. The merged file is verified
    /// (lv3: decryption, signatures and hashes) before it is published next to the base; only then do the parts go to
    /// the trash. Off until a keys file is there (SWITCH_KEYS_FILE, default /app/config/switch/prod.keys).
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public class SwitchMergeService : BackgroundService
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetLogger(Logging.AppLoggerService.General);

        public sealed record Part(string Path, ulong TitleId, int Version, long Size)
        {
            public ulong BaseId => (TitleId & 0xFFF) switch
            {
                0 => TitleId,
                0x800 => TitleId - 0x800,
                _ => (TitleId & ~0xFFFUL) - 0x1000
            };
            public bool IsBase => (TitleId & 0xFFF) == 0;
            public bool IsUpdate => (TitleId & 0xFFF) == 0x800;
        }

        public sealed record Family(ulong BaseId, Part Base, Part? Update, IReadOnlyList<Part> Dlcs, IReadOnlyList<Part> Superseded)
        {
            public IEnumerable<Part> Inputs => new[] { Base }.Concat(Update != null ? new[] { Update } : Array.Empty<Part>()).Concat(Dlcs);
            public string Fingerprint => string.Join("|", Inputs.Select(p => $"{p.Path}:{p.Size}"));
        }

        private readonly ConfigurationService _config;
        private readonly TrashService _trash;
        private readonly IGameRepository? _games;

        internal string Nscb = Environment.GetEnvironmentVariable("SWITCH_NSCB") ?? "/opt/nscb/squirrel.py";
        internal string KeysFile = Environment.GetEnvironmentVariable("SWITCH_KEYS_FILE") ?? "/app/config/switch/prod.keys";
        internal string WorkRoot = Environment.GetEnvironmentVariable("SWITCH_WORK") ?? "/temp/retroarr-switch";
        internal TimeSpan Interval = TimeSpan.FromMinutes(
            int.TryParse(Environment.GetEnvironmentVariable("SWITCH_MERGE_MINUTES"), out var m) && m > 0 ? m : 30);
        // A family that failed is tried again once its files change, or after this long
        internal static readonly TimeSpan RetryAfter = TimeSpan.FromHours(24);

        // internal for tests: runs NSC_Builder, returns the exit code and what it printed
        internal Func<string[], string, TimeSpan, CancellationToken, Task<(int Code, string Output)>> Run = RunProcessAsync;
        internal Func<string, long> FreeSpace = path => new DriveInfo(path).AvailableFreeSpace;

        public SwitchMergeService(ConfigurationService config, TrashService trash, IGameRepository? games = null)
        {
            _config = config;
            _trash = trash;
            _games = games;
        }

        internal string? LastError { get; private set; }

        public bool IsEnabled => File.Exists(Nscb) && File.Exists(KeysFile);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
            catch (TaskCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (IsEnabled) await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.Warn($"[SwitchMerge] Run failed: {ex.Message}"); }
                try { await Task.Delay(Interval, stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        public string? LibraryFolder
        {
            get
            {
                var media = _config.LoadMediaSettings();
                var root = !string.IsNullOrEmpty(media.DestinationPath) && Path.IsPathRooted(media.DestinationPath) ? media.DestinationPath
                    : !string.IsNullOrEmpty(media.FolderPath) && Path.IsPathRooted(media.FolderPath) ? media.FolderPath : null;
                if (root == null) return null;
                var platform = PlatformDefinitions.AllPlatforms.FirstOrDefault(p => p.MatchesFolderName("switch"));
                return Path.Combine(root, platform?.GetEffectiveFolderName(media.FolderNamingMode) ?? "switch");
            }
        }

        /// <summary>Merges every family that needs it, one at a time. Returns how many were merged.</summary>
        public async Task<int> RunOnceAsync(CancellationToken ct = default)
        {
            var library = LibraryFolder;
            if (library == null || !Directory.Exists(library)) return 0;
            var state = LoadState();
            var merged = 0;
            foreach (var family in Plan(Scan(library)))
            {
                ct.ThrowIfCancellationRequested();
                if (state.TryGetValue(family.Fingerprint, out var failedAt) && DateTime.UtcNow - failedAt < RetryAfter) continue;
                var error = await MergeAsync(family, ct);
                if (error == null) { merged++; state.Remove(family.Fingerprint); Note($"merged {family.Base.Path}"); }
                else
                {
                    _logger.Warn($"[SwitchMerge] {Path.GetFileName(family.Base.Path)}: {error}");
                    Note($"failed {family.Base.Path}: {error}");
                    LastError = error;
                    state[family.Fingerprint] = DateTime.UtcNow;
                }
                SaveState(state);
            }
            return merged;
        }

        private static readonly Regex TitleIdRegex = new(@"\[([0-9A-Fa-f]{16})\]");
        private static readonly Regex VersionRegex = new(@"\[v(\d+)\]", RegexOptions.IgnoreCase);
        private static readonly Regex LooseVersionRegex = new(@"(?<![A-Za-z0-9])v(\d+)(?![\d.])", RegexOptions.IgnoreCase);
        private static readonly Regex TicketName = new(@"^([0-9A-Fa-f]{16})[0-9A-Fa-f]{16}\.tik$");

        /// <summary>The title ID of an NSP from the name of its ticket ("0100ABCD12340000000000000000000A.tik"); null without one.</summary>
        internal static string? NspTitleId(string file)
        {
            if (!Path.GetExtension(file).Equals(".nsp", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                using var stream = File.OpenRead(file);
                using var reader = new BinaryReader(stream);
                if (stream.Length < 16 || reader.ReadUInt32() != 0x30534650) return null; // "PFS0"
                var count = reader.ReadInt32();
                var tableSize = reader.ReadInt32();
                reader.ReadInt32();
                if (count is <= 0 or > 4096 || tableSize is <= 0 or > 1 << 20) return null;
                stream.Seek(16 + count * 0x18L, SeekOrigin.Begin);
                var names = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(tableSize)).Split('\0');
                return names.Select(n => TicketName.Match(n)).FirstOrDefault(m => m.Success)?.Groups[1].Value.ToUpperInvariant();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        private static readonly HashSet<string> Mergeable = new(StringComparer.OrdinalIgnoreCase) { ".nsp", ".xci" };

        /// <summary>The .nsp/.xci files under the library that carry a title ID in their name.</summary>
        internal static List<Part> Scan(string library)
        {
            var parts = new List<Part>();
            foreach (var file in Directory.EnumerateFiles(library, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith('.') || !Mergeable.Contains(Path.GetExtension(file))) continue;
                if (Path.GetRelativePath(library, file).Split(Path.DirectorySeparatorChar).Any(d => d.StartsWith('.'))) continue;
                var id = TitleIdRegex.Match(name);
                // A name without its title ID ("INAZUMA ELEVEN DLC.nsp"): the ticket inside the NSP is named after it
                var titleId = id.Success ? id.Groups[1].Value : NspTitleId(file);
                if (titleId == null) continue;
                var version = VersionRegex.Match(name) is { Success: true } v ? v : LooseVersionRegex.Match(Path.GetFileNameWithoutExtension(name));
                parts.Add(new Part(file, Convert.ToUInt64(titleId, 16), version.Success ? int.Parse(version.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0,
                    new FileInfo(file).Length));
            }
            return parts;
        }

        /// <summary>
        /// Families with something to merge: one base (the largest, which is the merged one when there is one), the
        /// highest update and every DLC in its highest version. Lower versions and duplicate bases are superseded.
        /// A base alone, or an update without its base, is left alone.
        /// </summary>
        internal static List<Family> Plan(IEnumerable<Part> parts)
        {
            var families = new List<Family>();
            foreach (var group in parts.GroupBy(p => p.BaseId))
            {
                var bases = group.Where(p => p.IsBase).OrderByDescending(p => p.Size).ToList();
                if (bases.Count == 0) continue;
                var updates = group.Where(p => p.IsUpdate).OrderByDescending(p => p.Version).ThenByDescending(p => p.Size).ToList();
                var dlcs = group.Where(p => !p.IsBase && !p.IsUpdate).GroupBy(p => p.TitleId)
                    .Select(g => g.OrderByDescending(p => p.Version).ThenByDescending(p => p.Size).ToList()).ToList();
                if (updates.Count == 0 && dlcs.Count == 0) continue;
                var superseded = bases.Skip(1).Concat(updates.Skip(1)).Concat(dlcs.SelectMany(d => d.Skip(1))).ToList();
                families.Add(new Family(group.Key, bases[0], updates.FirstOrDefault(), dlcs.Select(d => d[0]).ToList(), superseded));
            }
            return families;
        }

        /// <summary>Merge, verify, publish, retire. Null when done; otherwise why nothing was changed.</summary>
        internal async Task<string?> MergeAsync(Family family, CancellationToken ct)
        {
            var inputs = family.Inputs.ToList();
            var total = inputs.Sum(p => p.Size);
            Directory.CreateDirectory(WorkRoot);
            if (FreeSpace(WorkRoot) < total * 2 + total / 10)
                return $"not enough space in {WorkRoot} ({total * 2 / 1_000_000_000.0:0.0} GB needed)";

            var work = Path.Combine(WorkRoot, $"{family.BaseId:X16}-{Guid.NewGuid().ToString("N")[..8]}");
            var output = Path.Combine(work, "out");
            Directory.CreateDirectory(output);
            try
            {
                // NSC_Builder changes to its own folder and reads keys.txt there; the keys never reach the library or the logs
                File.Copy(KeysFile, Path.Combine(Path.GetDirectoryName(Nscb)!, "keys.txt"), overwrite: true);
                var list = Path.Combine(work, "list.txt");
                await File.WriteAllLinesAsync(list, inputs.Select(p => p.Path), ct);
                _logger.Info($"[SwitchMerge] Merging {inputs.Count} parts of {family.BaseId:X16}: {string.Join(", ", inputs.Select(p => Path.GetFileName(p.Path)))}");

                var (code, log) = await Run(new[] { Nscb, "-b", "65536", "-t", "nsp", "-o", output, "-tfile", list, "-dmul", "calculate" }, work, TimeSpan.FromHours(6), ct);
                var made = Directory.EnumerateFiles(output, "*.nsp").ToList();
                if (code != 0 || made.Count != 1) return $"NSC_Builder made {made.Count} files (exit {code}): {Tail(log)}";
                var mergedFile = made[0];
                var size = new FileInfo(mergedFile).Length;
                if (size < inputs.Max(p => p.Size) || size > total + total / 20)
                    return $"merged file has an unlikely size ({size} bytes for {total} in): {Tail(log)}";

                var verifyList = Path.Combine(work, "verify.txt");
                await File.WriteAllLinesAsync(verifyList, new[] { mergedFile }, ct);
                var (verifyCode, verifyLog) = await Run(new[] { Nscb, "-b", "65536", "-vt", "lv3", "-tfile", verifyList, "-o", Path.Combine(work, "info"), "-v", "" }, work, TimeSpan.FromHours(3), ct);
                var verdict = Verdict(verifyLog);
                if (verifyCode != 0 || verdict != null) return $"verification failed: {verdict ?? $"exit {verifyCode}"}";

                var folder = Path.GetDirectoryName(family.Base.Path)!;
                // NSC_Builder names its output from the game's own control data (NACP): the official title
                var target = Path.Combine(folder, PublishName(family, Path.GetFileName(mergedFile)));
                if (File.Exists(target) && !inputs.Any(p => SamePath(p.Path, target))) return $"{Path.GetFileName(target)} is already there";
                var partial = Path.Combine(folder, ".retroarr-merge-" + Guid.NewGuid().ToString("N")[..8] + ".nsp");
                File.Copy(mergedFile, partial);
                if (new FileInfo(partial).Length != size) { File.Delete(partial); return "copy into the library came out short"; }

                // The base may carry the published name already (a merge of a merge): it goes to the trash first
                foreach (var part in inputs.Concat(family.Superseded).Where(p => File.Exists(p.Path)))
                {
                    var entry = await _trash.MoveAsync(part.Path, null, Path.GetFileName(family.Base.Path));
                    if (entry == null && File.Exists(part.Path))
                    {
                        File.Move(partial, target, overwrite: false);
                        return $"merged, but {Path.GetFileName(part.Path)} could not go to the trash; both are kept";
                    }
                }
                File.Move(partial, target);
                await PointLibraryAtAsync(inputs.Concat(family.Superseded).Select(p => p.Path), target);
                RemoveEmptyFolders(inputs.Concat(family.Superseded).Select(p => Path.GetDirectoryName(p.Path)!), Path.GetDirectoryName(folder)!);
                _logger.Info($"[SwitchMerge] Published {target}; {inputs.Count + family.Superseded.Count} parts to the trash");
                return null;
            }
            finally
            {
                try { Directory.Delete(work, true); }
                catch (Exception ex) { _logger.Warn($"[SwitchMerge] Could not remove {work}: {ex.Message}"); }
            }
        }

        /// <summary>Null when every verdict says the NSP is correct or safe; otherwise the first bad or missing verdict.</summary>
        internal static string? Verdict(string log)
        {
            var verdicts = log.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("VERDICT:", StringComparison.OrdinalIgnoreCase)).ToList();
            if (verdicts.Count == 0) return "no verdict";
            return verdicts.FirstOrDefault(v => !Regex.IsMatch(v, @"^VERDICT:\s+(NSP|XCI|NCA)\s+FILE\s+IS\s+(CORRECT|SAFE)$", RegexOptions.IgnoreCase));
        }

        /// <summary>
        /// "Title [0100ABCD12340000][v131072].nsp": the title NSC_Builder read from the game itself (else the base file's),
        /// the base ID, the update's version.
        /// </summary>
        internal static string PublishName(Family family, string? mergedName = null)
        {
            static string TitleOf(string? name) =>
                Regex.Replace(Regex.Replace(Path.GetFileNameWithoutExtension(name ?? string.Empty), @"\s*[\[\(][^\]\)]*[\]\)]", ""), @"[:/\\?*""<>|]", " ")
                    .Replace("  ", " ").Trim(' ', '.', '-');
            var title = TitleOf(mergedName);
            if (!title.Any(char.IsLetter) || Regex.IsMatch(title, @"^[0-9A-Fa-f]{16}$")) title = TitleOf(family.Base.Path);
            if (title.Length == 0) title = $"{family.BaseId:X16}";
            var version = family.Update?.Version ?? family.Base.Version;
            return $"{title} [{family.BaseId:X16}][v{version}].nsp";
        }

        // The library entry that pointed at the base (or another part) now points at the merged file
        private async Task PointLibraryAtAsync(IEnumerable<string> oldPaths, string target)
        {
            if (_games == null) return;
            try
            {
                var old = new HashSet<string>(oldPaths, StringComparer.Ordinal);
                foreach (var game in (await _games.GetAllLightAsync()).Where(g => g.ExecutablePath != null && old.Contains(g.ExecutablePath)))
                    await _games.UpdateFieldsAsync(game.Id, g => g.ExecutablePath = target);
            }
            catch (Exception ex) { _logger.Warn($"[SwitchMerge] {target} is published, but the library list is not updated yet (the next scan does it): {ex.Message}"); }
        }

        private static void RemoveEmptyFolders(IEnumerable<string> folders, string stopAt)
        {
            foreach (var folder in folders.Distinct().OrderByDescending(f => f.Length))
            {
                var dir = folder;
                while (!SamePath(dir, stopAt) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    dir = Path.GetDirectoryName(dir)!;
                }
            }
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);

        private static string Tail(string s) => s.Length > 400 ? s[^400..] : s;

        private string StatePath => Path.Combine(_config.GetConfigDirectory(), "switch-merge-state.json");

        private Dictionary<string, DateTime> LoadState()
        {
            try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(StatePath)) ?? new() : new(); }
            catch (Exception) { return new(); }
        }

        // switch-merge.log in the config folder: every merge and why one failed, kept past container restarts
        private void Note(string line)
        {
            try { File.AppendAllText(Path.Combine(_config.GetConfigDirectory(), "switch-merge.log"), $"{DateTime.UtcNow:o} {line}{Environment.NewLine}"); }
            catch (Exception) { }
        }

        private void SaveState(Dictionary<string, DateTime> state)
        {
            try { File.WriteAllText(StatePath, JsonSerializer.Serialize(state)); }
            catch (Exception ex) { _logger.Warn($"[SwitchMerge] Could not save state: {ex.Message}"); }
        }

        private static async Task<(int Code, string Output)> RunProcessAsync(string[] args, string workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            var info = new ProcessStartInfo("python3") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var process = Process.Start(info)!;
            process.StandardInput.Close(); // NSC_Builder asks questions on some paths; an empty answer ends them
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (Exception) { }
                return (-1, "timed out");
            }
            return (process.ExitCode, await stdout + await stderr);
        }
    }
}
