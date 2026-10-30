using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RetroArr.Core.Configuration;

namespace RetroArr.Core.Games
{
    public class TrashEntry
    {
        public string Id { get; set; } = string.Empty;
        public int? GameId { get; set; }
        public string? GameTitle { get; set; }
        public string OriginalPath { get; set; } = string.Empty;
        public string TrashPath { get; set; } = string.Empty;
        public DateTime DeletedAt { get; set; }
        public long SizeBytes { get; set; }
        public bool IsDirectory { get; set; }
        // Until the source is gone, and for good when the move stops halfway: the entry may hold the only copy of part
        // of the payload, so it is listed but never purged with the rest, only put back or deleted by hand
        public bool Incomplete { get; set; }
        // A trashed folder is laid out flat: each of its files lies right in the trash under its own name (Name),
        // with where in the folder it came from (Original, relative). Null for a file, and for entries from before.
        public List<TrashedFile>? Files { get; set; }
    }

    public class TrashedFile
    {
        public string Name { get; set; } = string.Empty;
        public string Original { get; set; } = string.Empty;
    }

    // Part of a payload is in this entry, the rest is still where it was
    public class TrashPartialMoveException : IOException
    {
        public TrashEntry Entry { get; }

        public TrashPartialMoveException(string message, TrashEntry entry, Exception inner) : base(message, inner) => Entry = entry;
    }

    public class TrashService
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetLogger(Logging.AppLoggerService.General);
        private readonly ConfigurationService _config;

        // internal for unit tests: a rename that fails as it does across volumes, a delete that stops halfway, a full volume
        internal Action<string, string> Rename = Directory.Move;
        internal Action<string> Delete = path =>
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        };
        internal Func<string, long> FreeSpace = path => new DriveInfo(path).AvailableFreeSpace;

        public TrashService(ConfigurationService config)
        {
            _config = config;
        }

        // Entries are folders named like 20260929-183000_1a2b3c4d, nothing else in the trash folder is RetroArr's
        private static readonly System.Text.RegularExpressions.Regex _entryName = new(@"^\d{8}-\d{6}_[0-9a-f]{8}$");

        private static bool IsEntry(string dir) => _entryName.IsMatch(Path.GetFileName(dir));

        // What lies in the trash lies there under its own name, easy to look through; what the trash knows about it is
        // kept beside, in this hidden folder (one <entry>.json each). Entries from before (a folder per entry with its
        // meta.json) are still listed, restored and purged.
        private const string MetaFolder = ".trash-meta";

        private static string FreeName(string root, string name)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Directory.Exists(path) ? string.Empty : Path.GetExtension(name);
            if (ext.Length == 0) stem = name;
            for (var i = 2; ; i++)
            {
                var candidate = Path.Combine(root, $"{stem} ({i}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        private string GetTrashRoot()
        {
            var settings = _config.LoadMediaSettings();
            var path = string.IsNullOrWhiteSpace(settings.TrashPath)
                ? Path.Combine(_config.GetConfigDirectory(), "trash")
                : settings.TrashPath;
            Directory.CreateDirectory(path);
            return path;
        }

        public async Task<TrashEntry?> MoveAsync(string sourcePath, int? gameId = null, string? gameTitle = null)
        {
            if (string.IsNullOrEmpty(sourcePath)) return null;
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                _logger.Warn($"[Trash] Source path missing, nothing to move: {sourcePath}");
                return null;
            }

            var root = GetTrashRoot();
            var isDir = Directory.Exists(sourcePath);
            var entryId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var metaDir = Directory.CreateDirectory(Path.Combine(root, MetaFolder)).FullName;
            var sidecarPath = Path.Combine(metaDir, entryId + ".json");

            var payloadName = isDir ? new DirectoryInfo(Path.TrimEndingDirectorySeparator(sourcePath)).Name : Path.GetFileName(sourcePath);
            if (payloadName.StartsWith('.')) payloadName = "_" + payloadName;
            var destPath = FreeName(root, payloadName);
            var entry = new TrashEntry
            {
                Id = entryId,
                GameId = gameId,
                GameTitle = gameTitle,
                OriginalPath = sourcePath,
                TrashPath = destPath,
                DeletedAt = DateTime.UtcNow,
                IsDirectory = isDir,
                SizeBytes = TryMeasure(sourcePath),
                Incomplete = true,
            };

            // The sidecar comes first: a payload the trash can't list could never be put back
            try
            {
                await WriteSidecarAsync(sidecarPath, entry);
            }
            catch (Exception ex)
            {
                ForgetSidecar(sidecarPath);
                throw new IOException($"Could not write the trash entry for {sourcePath}, nothing was moved: {ex.Message}", ex);
            }

            try
            {
                Move(sourcePath, destPath);
            }
            catch (Exception ex)
            {
                _logger.Error($"[Trash] Move failed {sourcePath} -> {destPath}: {ex.Message}");
                // Nothing reached the trash, the source is as it was
                if (!File.Exists(destPath) && !Directory.Exists(destPath))
                {
                    ForgetSidecar(sidecarPath);
                    throw;
                }
                // What got to the trash stays listed there, so it can be found and put back
                throw new TrashPartialMoveException($"{sourcePath} could not be moved to the trash whole, what got there is kept in trash entry {entryId}: {ex.Message}", entry, ex);
            }

            // The source is gone, the entry holds all of it
            entry.Incomplete = false;
            try { await WriteSidecarAsync(sidecarPath, entry); }
            catch (Exception ex) { _logger.Warn($"[Trash] Could not mark trash entry {entryId} complete, it stays until deleted by hand: {ex.Message}"); }

            if (isDir && Flatten) await FlattenAsync(entry, root, sidecarPath);

            _logger.Info($"[Trash] {(isDir ? "Directory" : "File")} moved to trash: {sourcePath} (entry {entryId})");
            return entry;
        }

        // The files of a trashed folder go right into the trash, each under its own name, so the trash is easy to look
        // through. Renames within the trash only; the sidecar is written after each, so it always says where all lies.
        internal bool Flatten = true;

        private async Task FlattenAsync(TrashEntry entry, string root, string sidecarPath)
        {
            // A folder with links in it (or a link to a folder) stays whole: its links only make sense where they are
            var info = new DirectoryInfo(entry.TrashPath);
            if (info.LinkTarget != null || info.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(i => i.LinkTarget != null)) return;
            entry.Files ??= new List<TrashedFile>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(entry.TrashPath, "*", SearchOption.AllDirectories).ToList())
                {
                    var name = Path.GetFileName(file);
                    var to = FreeName(root, name.StartsWith('.') ? "_" + name : name);
                    File.Move(file, to);
                    entry.Files.Add(new TrashedFile { Name = Path.GetFileName(to), Original = Path.GetRelativePath(entry.TrashPath, file) });
                    await WriteSidecarAsync(sidecarPath, entry);
                }
                // What is left is empty folders
                if (!Directory.EnumerateFiles(entry.TrashPath, "*", SearchOption.AllDirectories).Any()) Directory.Delete(entry.TrashPath, true);
            }
            catch (Exception ex) { _logger.Warn($"[Trash] Could not lay out {entry.TrashPath} flat, the rest stays in its folder: {ex.Message}"); }
        }

        // A move that never happened leaves nothing behind, not even an empty .trash-meta
        private static void ForgetSidecar(string sidecarPath)
        {
            try
            {
                File.Delete(sidecarPath);
                var dir = Path.GetDirectoryName(sidecarPath)!;
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { }
        }

        private static Task WriteSidecarAsync(string sidecarPath, TrashEntry entry) =>
            File.WriteAllTextAsync(sidecarPath, JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));

        public List<TrashEntry> List()
        {
            var root = GetTrashRoot();
            var entries = new List<TrashEntry>();
            if (!Directory.Exists(root)) return entries;

            foreach (var dir in Directory.EnumerateDirectories(root).Where(IsEntry))
            {
                var sidecar = Path.Combine(dir, "meta.json");
                if (!File.Exists(sidecar)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<TrashEntry>(File.ReadAllText(sidecar));
                    var payload = Path.GetFileName(entry?.TrashPath);
                    if (entry == null || string.IsNullOrEmpty(payload)) continue;
                    // The payload lies next to its sidecar, so a restore or purge never reaches outside the entry
                    entry.TrashPath = Path.Combine(dir, payload);
                    entries.Add(entry);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[Trash] Could not read sidecar {sidecar}: {ex.Message}");
                }
            }

            var metaDir = Path.Combine(root, MetaFolder);
            if (Directory.Exists(metaDir))
                foreach (var sidecar in Directory.EnumerateFiles(metaDir, "*.json"))
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<TrashEntry>(File.ReadAllText(sidecar));
                        var payload = Path.GetFileName(entry?.TrashPath);
                        if (entry == null || string.IsNullOrEmpty(payload) || entry.Id != Path.GetFileNameWithoutExtension(sidecar)) continue;
                        // Each file lies right in the trash; nothing can point outside it
                        if (entry.Files != null && entry.Files.Any(f => Path.GetFileName(f.Name) != f.Name || f.Name.Length == 0 || Path.IsPathRooted(f.Original) || f.Original.Split('/', '\\').Contains(".."))) continue;
                        // The payload lies right in the trash, so a restore or purge never reaches outside it
                        entry.TrashPath = Path.Combine(root, payload);
                        entries.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"[Trash] Could not read sidecar {sidecar}: {ex.Message}");
                    }
                }

            return entries.OrderByDescending(e => e.DeletedAt).ToList();
        }

        public TrashEntry? Get(string id) =>
            List().FirstOrDefault(e => e.Id == id);

        public bool Restore(string id)
        {
            var entry = Get(id);
            if (entry == null) return false;
            if (!File.Exists(entry.TrashPath) && !Directory.Exists(entry.TrashPath) && !(entry.Files?.Count > 0))
            {
                _logger.Warn($"[Trash] Restore: payload missing for {id}");
                return false;
            }

            var target = entry.OriginalPath;
            try
            {
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                if (File.Exists(target) || Directory.Exists(target))
                {
                    _logger.Error($"[Trash] Restore refused: original path already occupied ({target}).");
                    return false;
                }

                if (entry.Files == null || Directory.Exists(entry.TrashPath)) Move(entry.TrashPath, target);
                else Directory.CreateDirectory(target);
                // A folder laid out flat gets its files back where they were in it
                foreach (var file in entry.Files ?? new List<TrashedFile>())
                {
                    var from = Path.Combine(GetTrashRoot(), file.Name);
                    var to = Path.Combine(target, file.Original);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    if (File.Exists(from)) Move(from, to);
                }

                DropEntry(entry);
                _logger.Info($"[Trash] Restored {id} to {target}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"[Trash] Restore failed for {id}: {ex.Message}");
                return false;
            }
        }

        public bool PurgeOne(string id)
        {
            var entry = Get(id);
            return entry != null && Purge(entry);
        }

        // The entry's description, and for an old entry its folder (now empty of its payload)
        private void DropEntry(TrashEntry entry)
        {
            var root = GetTrashRoot();
            var oldDir = Path.Combine(root, entry.Id);
            try
            {
                if (Directory.Exists(oldDir) && SamePath(Path.GetDirectoryName(entry.TrashPath), oldDir)) Directory.Delete(oldDir, true);
                else File.Delete(Path.Combine(root, MetaFolder, entry.Id + ".json"));
            }
            catch (Exception) { }
        }

        private static bool SamePath(string? a, string b) =>
            a != null && string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);

        private bool Purge(TrashEntry entry)
        {
            var root = GetTrashRoot();
            var oldDir = Path.Combine(root, entry.Id);
            try
            {
                if (Directory.Exists(oldDir) && SamePath(Path.GetDirectoryName(entry.TrashPath), oldDir)) Directory.Delete(oldDir, true);
                else
                {
                    // Only what lies right in the trash, never anything outside it
                    if (!SamePath(Path.GetDirectoryName(entry.TrashPath), root)) return false;
                    if (Directory.Exists(entry.TrashPath)) Delete(entry.TrashPath);
                    else if (File.Exists(entry.TrashPath)) File.Delete(entry.TrashPath);
                    foreach (var file in entry.Files ?? new List<TrashedFile>())
                        if (File.Exists(Path.Combine(root, file.Name))) File.Delete(Path.Combine(root, file.Name));
                    File.Delete(Path.Combine(root, MetaFolder, entry.Id + ".json"));
                }
                _logger.Info($"[Trash] Purged {entry.Id}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"[Trash] Purge failed for {entry.Id}: {ex.Message}");
                return false;
            }
        }

        // What the trash lists as moved whole. An incomplete entry stays, only the user puts it back or deletes it.
        public int PurgeAll()
        {
            var count = 0;
            var kept = 0;
            foreach (var entry in List())
            {
                if (entry.Incomplete) kept++;
                else if (Purge(entry)) count++;
            }
            _logger.Info($"[Trash] Emptied: {count} entries{(kept > 0 ? $", kept {kept} whose move did not complete" : "")}");
            return count;
        }

        public int PurgeExpired()
        {
            var settings = _config.LoadMediaSettings();
            var days = settings.TrashRetentionDays;
            if (days <= 0) return 0;

            var cutoff = DateTime.UtcNow.AddDays(-days);
            var count = 0;
            foreach (var entry in List())
            {
                if (entry.DeletedAt > cutoff || entry.Incomplete) continue;
                if (Purge(entry)) count++;
            }
            if (count > 0) _logger.Info($"[Trash] Auto-purged {count} entries older than {days}d");
            return count;
        }

        // Across volumes a move is a copy and a delete. The copy only starts when it fits, and when the delete stops
        // halfway the source gets back what it lost before the copy goes, so the payload is whole on one side at all times.
        private void Move(string src, string dst)
        {
            try
            {
                // Renames files too and, unlike File.Move, never copies to another volume by itself
                Rename(src, dst);
                return;
            }
            catch (IOException ex) when (IsCrossDevice(ex, src, dst))
            {
                // another volume; anything else refuses the move with its error
            }

            // Another Docker mount of the same disk: rename through the one mount that holds both, no copy
            var (sharedSrc, sharedDst) = (IO.SharedMount.Translate(src), IO.SharedMount.Translate(dst));
            if (sharedSrc != null && sharedDst != null)
            {
                try
                {
                    Rename(sharedSrc, sharedDst);
                    return;
                }
                catch (IOException ex) when (IsCrossDevice(ex, sharedSrc, sharedDst)) { }
            }

            var need = TryMeasure(src);
            var dir = Path.GetDirectoryName(Path.GetFullPath(dst))!;
            long free;
            try { free = FreeSpace(dir); }
            catch { free = long.MaxValue; }
            if (need > free)
                throw new IOException($"Not enough free space in {dir}: {need / 1048576.0:0.#} MB needed, {free / 1048576.0:0.#} MB free.");

            try
            {
                Copy(src, dst, strict: true);
            }
            catch
            {
                try { Delete(dst); } catch { }
                throw;
            }

            Exception? lastDeleteError = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    Delete(src);
                    return;
                }
                catch (Exception ex)
                {
                    lastDeleteError = ex;
                    System.Threading.Thread.Sleep(250 * attempt);
                }
            }

            Copy(dst, src, strict: false);
            Delete(dst);
            throw new IOException($"Could not remove {src} after copying it, it stays where it was: {lastDeleteError?.Message}", lastDeleteError);
        }

        // A rename that failed because it goes to another volume: EXDEV on Linux and macOS, ERROR_NOT_SAME_DEVICE on
        // Windows, which also refuses a move between drives before it tries one
        internal static bool IsCrossDevice(IOException ex, string src, string dst) =>
            ex.HResult == (OperatingSystem.IsWindows() ? unchecked((int)0x80070011) : 18)
            || !string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dst)), StringComparison.OrdinalIgnoreCase);

        // Copies src to dst. A link is copied as the link and never followed, so a restore brings the link back and a
        // link to a folder above can't loop. The copy of a payload is strict: a name that is there already fails it, as
        // a volume that ignores case folds Data and data into one and the source would then go with one of them lost.
        // The copy back into a source only fills in what it lacks.
        internal static void Copy(string src, string dst, bool strict)
        {
            var taken = File.Exists(dst) || Directory.Exists(dst);
            if (taken && strict)
                throw new IOException($"{dst} is there already: that volume doesn't tell apart names that differ only in case or form");
            var link = LinkTarget(src);
            if (link != null)
            {
                if (!taken)
                {
                    if (Directory.Exists(src)) Directory.CreateSymbolicLink(dst, link);
                    else File.CreateSymbolicLink(dst, link);
                }
                return;
            }
            if (File.Exists(src))
            {
                if (!taken) File.Copy(src, dst);
                return;
            }
            Directory.CreateDirectory(dst);
            foreach (var entry in Directory.GetFileSystemEntries(src))
                Copy(entry, Path.Combine(dst, Path.GetFileName(entry)), strict);
        }

        private static string? LinkTarget(string path) =>
            (Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path)).LinkTarget;

        // Links take no room, they are copied as links
        private static long TryMeasure(string path)
        {
            try
            {
                if (LinkTarget(path) != null) return 0;
                if (File.Exists(path)) return new FileInfo(path).Length;
                if (Directory.Exists(path))
                    return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                        .Sum(f => f.Length);
            }
            catch { }
            return 0;
        }
    }
}
