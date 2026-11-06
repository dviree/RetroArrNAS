using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RetroArr.Core.Configuration;
using RetroArr.Core.IO;

namespace RetroArr.Core.Games
{
    /// <summary>Builds a full PS4 game PKG containing its latest matching update.</summary>
    public sealed class Ps4PkgMergeService : BackgroundService
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetLogger(Logging.AppLoggerService.General);
        private readonly ConfigurationService _config;
        private readonly TrashService _trash;
        private readonly IFileMoverService _mover;
        private readonly IGameRepository? _games;
        private readonly SemaphoreSlim _gate = new(1, 1);
        internal string Tool = Environment.GetEnvironmentVariable("PS4_PKG_TOOL") ?? "/opt/ps4pkg/OrbisPkgTool";
        internal string WorkRoot = Environment.GetEnvironmentVariable("PS4_PKG_WORK") ?? "/temp/retroarr-ps4-merge";
        internal TimeSpan Settle = TimeSpan.FromMinutes(10);
        internal Func<string[], string, CancellationToken, Task<(int Code, string Output)>> Run = RunToolAsync;
        internal Func<string, long> FreeSpace = path => new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;
        public static bool IsEnabled => !string.Equals(Environment.GetEnvironmentVariable("PS4_PKG_MERGE"), "false", StringComparison.OrdinalIgnoreCase);
        public string? LastError { get; private set; }
        public string? LastOutput { get; private set; }

        internal sealed record Part(string Path, string TitleId, string ContentId, string Title, string Version, bool Patch, long Size, DateTime Written);

        public Ps4PkgMergeService(ConfigurationService config, TrashService trash, IFileMoverService mover, IGameRepository? games = null)
        {
            _config = config;
            _trash = trash;
            _mover = mover;
            _games = games;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
            catch (OperationCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try { if (IsEnabled) await RunOnceAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"[PS4Merge] {ex.Message}"); }
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        internal static Part? ReadPart(string path)
        {
            var (contentId, sfo) = PkgReader.Read(path);
            if (contentId == null || !sfo.TryGetValue("TITLE_ID", out var id) || !Regex.IsMatch(id, @"^CUSA\d{5}$")) return null;
            if (!sfo.TryGetValue("CATEGORY", out var category) || (category != "gd" && category != "gp")) return null;
            if (!sfo.TryGetValue("APP_VER", out var version) || !Regex.IsMatch(version, @"^\d+(\.\d+)+$")) return null;
            if (PlayStationPackageInspector.TitleIdIn(contentId) != id) return null;
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return new(path, id, contentId, sfo.GetValueOrDefault("TITLE") ?? id, version, category == "gp", info.Length, info.LastWriteTimeUtc);
        }

        private static bool Unchanged(Part part)
        {
            var info = new FileInfo(part.Path);
            return info.Exists && info.Length == part.Size && info.LastWriteTimeUtc == part.Written;
        }

        public async Task<int> RunOnceAsync(CancellationToken ct = default)
        {
            if (!IsEnabled || !await _gate.WaitAsync(0, ct)) return 0;
            try
            {
                if (!File.Exists(Tool)) { LastError = "PS4 PKG merge tool is unavailable."; Log.Warn($"[PS4Merge] {LastError}"); return 0; }
                var media = _config.LoadMediaSettings();
                var root = !string.IsNullOrWhiteSpace(media.DestinationPath) ? media.DestinationPath : media.FolderPath;
                if (string.IsNullOrWhiteSpace(root)) return 0;
                var library = Path.Combine(root, "ps4");
                if (!Directory.Exists(library)) return 0;
                var files = Directory.EnumerateFiles(library, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }).Where(f => Path.GetExtension(f).Equals(".pkg", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetRelativePath(library, f).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.'))).ToList();
                var parts = files.Select(ReadPart).Where(p => p != null).Cast<Part>().ToList();
                int merged = 0;
                foreach (var family in parts.GroupBy(p => p.ContentId, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    var ordered = family.OrderByDescending(p => p.Version, Comparer<string>.Create(PlayStationPackageInspector.CompareVersions)).ToList();
                    var game = ordered.FirstOrDefault(p => !p.Patch);
                    var patch = ordered.FirstOrDefault(p => p.Patch);
                    if (game == null || patch == null || game.TitleId != patch.TitleId
                        || PlayStationPackageInspector.CompareVersions(patch.Version, game.Version) <= 0) continue;
                    // A newly arrived file can still be an import in progress; wait for the entire family to settle.
                    if (family.Any(p => DateTime.UtcNow - p.Written < Settle)) continue;
                    try { if (await MergeAsync(game, patch, ordered, ct)) merged++; }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LastError = ex.Message;
                        Log.Warn($"[PS4Merge] {game.TitleId}: {ex.Message}; original PKGs retained.");
                    }
                }
                return merged;
            }
            finally { _gate.Release(); }
        }

        private async Task<bool> MergeAsync(Part game, Part patch, List<Part> family, CancellationToken ct)
        {
            var folder = Path.GetDirectoryName(game.Path)!;
            var title = FileNameSanitizer.Sanitize(game.Title, game.TitleId);
            var version = string.Join(".", patch.Version.Split('.').Select(s => int.Parse(s).ToString()));
            var target = Path.Combine(folder, $"{title} [{game.TitleId}] [v{version}].pkg");
            if (File.Exists(target)) throw new IOException($"Target already exists, not overwriting: {target}");
            Directory.CreateDirectory(WorkRoot);
            var need = checked((game.Size + patch.Size) * 4);
            if (FreeSpace(WorkRoot) < need || FreeSpace(folder) < game.Size + patch.Size)
                throw new IOException("Not enough free space for extraction, rebuild and publication.");
            var work = Directory.CreateDirectory(Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"))).FullName;
            var output = Path.Combine(work, "merged.pkg");
            var partial = Path.Combine(folder, ".retroarr-ps4-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Log.Info($"[PS4Merge] Building {game.TitleId}: base v{game.Version} + update v{patch.Version}");
                var build = await Run(new[] { Tool, "merge", game.Path, patch.Path, "--out", output,
                    "--work-dir", work, "--workers", "1", "--validate" }, work, ct);
                if (build.Code != 0) throw new IOException($"Merge failed (exit {build.Code}): {build.Output}");
                LastOutput = build.Output;
                var built = ReadPart(output);
                if (built == null || built.Patch || built.ContentId != game.ContentId || built.TitleId != game.TitleId
                    || PlayStationPackageInspector.CompareVersions(built.Version, patch.Version) != 0)
                    throw new IOException("Merged package has the wrong type, identity or version.");
                if (family.Any(p => !Unchanged(p))) throw new IOException("An input changed during the merge.");
                if (!_mover.ImportFile(output, partial, out var error)) throw new IOException(error ?? "Could not publish merged PKG.");
                if (new FileInfo(partial).Length != built.Size) throw new IOException("Published copy has the wrong size.");
                var verify = await Run(new[] { Tool, "validate", partial }, work, ct);
                if (verify.Code != 0) throw new IOException($"Published PKG failed validation: {verify.Output}");
                if (family.Any(p => !Unchanged(p))) throw new IOException("An input changed during output validation.");
                ct.ThrowIfCancellationRequested();
                File.Move(partial, target); // Atomic publication; never replaces a file that appeared meanwhile.
                // Save the new library path before retiring originals; a DB failure keeps every original in place.
                if (_games != null)
                {
                    var old = family.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
                    foreach (var entry in (await _games.GetAllLightAsync()).Where(g =>
                        (g.ExecutablePath != null && old.Contains(g.ExecutablePath)) || (g.Path != null && old.Contains(g.Path))))
                        await _games.UpdateFieldsAsync(entry.Id, g => { g.ExecutablePath = target; if (g.Path != null && old.Contains(g.Path)) g.Path = target; });
                }
                foreach (var part in family)
                {
                    if (!Unchanged(part)) { Log.Warn($"[PS4Merge] Changed input kept: {part.Path}"); continue; }
                    await _trash.MoveAsync(part.Path, null, game.Title);
                }
                LastError = null;
                Log.Info($"[PS4Merge] Verified merged PKG: {target}; original game and updates moved to trash.");
                return true;
            }
            finally
            {
                // Only files and directories created for this job are removed, never an input PKG.
                if (File.Exists(partial)) File.Delete(partial);
                if (Directory.Exists(work)) Directory.Delete(work, true);
            }
        }

        private static async Task<(int, string)> RunToolAsync(string[] args, string cwd, CancellationToken ct)
        {
            var info = new ProcessStartInfo(args[0]) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args.Skip(1)) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Could not start PS4 PKG tool.");
            var log = new StringBuilder();
            var reported = DateTime.MinValue;
            async Task ReadAsync(StreamReader reader)
            {
                while (await reader.ReadLineAsync() is { } line)
                    lock (log)
                    {
                        log.AppendLine(line);
                        if (log.Length > 32768) log.Remove(0, log.Length - 32768);
                        if (DateTime.UtcNow - reported >= TimeSpan.FromSeconds(10))
                        {
                            Log.Info($"[PS4Merge] {line}");
                            reported = DateTime.UtcNow;
                        }
                    }
            }
            var stdout = ReadAsync(process.StandardOutput);
            var stderr = ReadAsync(process.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromHours(24));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("PS4 PKG tool timed out; original PKGs retained.");
            }
            await Task.WhenAll(stdout, stderr);
            return (process.ExitCode, log.ToString());
        }
    }
}
