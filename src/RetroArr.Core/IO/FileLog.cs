using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace RetroArr.Core.IO
{
    /// <summary>
    /// One line in RetroArr's log for everything it does to a file or folder: moved, copied, renamed, sent to the
    /// trash, put back, purged from the trash or deleted, with where from, where to and why, and in a dry run what it
    /// would have done. It all lands in one file, files.log, whatever the log level, so what happened to a game can
    /// always be looked up in one place.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public static class FileLog
    {
        public const string LoggerName = "RetroArr.Files";
        private static readonly NLog.Logger _logger = NLog.LogManager.GetLogger(LoggerName);

        public static void Moved(string from, string to, string why) => Write("MOVE", from, to, why);
        public static void Copied(string from, string to, string why) => Write("COPY", from, to, why);
        public static void Trashed(string from, string to, string why) => Write("TRASH", from, to, why);
        public static void Restored(string from, string to, string why) => Write("RESTORE", from, to, why);
        public static void Purged(string path, string why) => Write("PURGE", path, null, why);
        public static void Deleted(string path, string why) => Write("DELETE", path, null, why);
        /// <summary>What a dry run would have done (FILE_ACTIONS is not on): nothing was changed.</summary>
        public static void Planned(string action, string path, string? to, string why)
        {
            // Each planned step once while RetroArr runs, not again on every round of the job
            lock (_planned)
                if (!_planned.Add(action + "|" + path + "|" + to)) return;
            Write("WOULD " + action, path, to, why + " (dry run, nothing changed)");
        }

        private static readonly System.Collections.Generic.HashSet<string> _planned = new(StringComparer.Ordinal);

        public static void Failed(string action, string path, string? to, string why) => Write("FAILED " + action, path, to, why, warn: true);

        /// <summary>Deletes a file or a folder with everything in it, after logging it. Only for RetroArr's own work files.</summary>
        public static void Delete(string path, string why)
        {
            if (Directory.Exists(path))
            {
                Write("DELETE", path, null, why + Describe(path));
                Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                Write("DELETE", path, null, why + Describe(path));
                File.Delete(path);
            }
        }

        private static string Describe(string path)
        {
            try
            {
                if (File.Exists(path)) return $" ({new FileInfo(path).Length} bytes)";
                var count = 0;
                long bytes = 0;
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    count++;
                    bytes += file.Length;
                    if (count >= 100000) break;
                }
                return $" ({count} files, {bytes} bytes)";
            }
            catch (Exception) { return string.Empty; }
        }

        private static void Write(string action, string from, string? to, string why, bool warn = false)
        {
            try
            {
                var line = to == null ? $"{action} {from} | {why}" : $"{action} {from} -> {to} | {why}";
                if (warn) _logger.Warn(line); else _logger.Info(line);
            }
            catch (Exception) { }
        }
    }
}
