using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace RetroArr.Core.IO
{
    /// <summary>
    /// Docker gives each folder its own mount (/media, /downloads/qbittorrent, /trash), and Linux refuses a hard link or
    /// a rename between two mounts even when they are the same disk. When the whole volume is also mounted once
    /// (HARDLINK_ROOT, default /nas, e.g. "/srv:/nas"), a path in any of those mounts can be written as the same
    /// file under that one mount, where links and renames work. Read from /proc/self/mountinfo; never guesses.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public static class SharedMount
    {
        public sealed record Mount(string Device, string Root, string MountPoint);

        internal static string Root = Environment.GetEnvironmentVariable("HARDLINK_ROOT") ?? "/nas";
        internal static Func<string> ReadMountInfo = () => File.ReadAllText("/proc/self/mountinfo");

        private static List<Mount>? _mounts;

        internal static void Reset() => _mounts = null;

        /// <summary>The same file under the shared mount, or null when there is none or the path is on another device.</summary>
        public static string? Translate(string path)
        {
            List<Mount> mounts;
            try { mounts = _mounts ??= Parse(ReadMountInfo()); }
            catch (Exception) { return null; }
            var shared = mounts.LastOrDefault(m => m.MountPoint == Root.TrimEnd('/'));
            if (shared == null) return null;
            var full = Path.GetFullPath(path);
            var own = mounts.Where(m => Under(full, m.MountPoint)).OrderByDescending(m => m.MountPoint.Length).FirstOrDefault();
            if (own == null || own.Device != shared.Device || !Under(own.Root, shared.Root)) return null;
            // own.Root is where the mount starts on the disk; the shared mount shows the disk from shared.Root
            var onDisk = Join(own.Root, Relative(full, own.MountPoint));
            return Join(shared.MountPoint, Relative(onDisk, shared.Root));
        }

        // "36 35 98:0 /games/roms /media rw,noatime - btrfs /dev/mapper/sda1 rw"
        internal static List<Mount> Parse(string mountInfo) =>
            mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split(' '))
                .Where(f => f.Length > 4)
                .Select(f => new Mount(f[2], Unescape(f[3]), Unescape(f[4])))
                .ToList();

        private static string Unescape(string s) => s.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

        private static bool Under(string path, string folder) =>
            folder == "/" || path == folder || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);

        private static string Relative(string path, string folder) =>
            folder == "/" ? path.TrimStart('/') : path.Length <= folder.Length ? string.Empty : path[(folder.TrimEnd('/').Length + 1)..];

        private static string Join(string a, string b) => b.Length == 0 ? a : a.TrimEnd('/') + "/" + b;
    }
}
