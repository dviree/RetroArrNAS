using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;

namespace RetroArr.Core.IO
{
    public interface IFileMoverService
    {
        bool ImportFile(string sourceFile, string destinationFile);
        bool ImportFile(string sourceFile, string destinationFile, out string? failureReason);
    }

    [SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
    [SuppressMessage("Microsoft.Globalization", "CA2101:SpecifyMarshalingForPInvokeStringArguments")]
    [SuppressMessage("Microsoft.Interoperability", "CA5392:UseDefaultDllImportSearchPathsAttribute")]
    [SuppressMessage("Microsoft.Globalization", "CA1303:DoNotPassLiteralsAsLocalizedParameters")]
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public class FileMoverService : IFileMoverService
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetLogger(Logging.AppLoggerService.DownloadsImport);
        public bool ImportFile(string sourceFile, string destinationFile)
            => ImportFile(sourceFile, destinationFile, out _);

        public bool ImportFile(string sourceFile, string destinationFile, out string? failureReason)
        {
            failureReason = null;
            if (!File.Exists(sourceFile))
            {
                failureReason = $"Source file not found: {sourceFile}";
                _logger.Info($"[FileMover] {failureReason}");
                return false;
            }

            var destDir = Path.GetDirectoryName(destinationFile);
            if (destDir != null && !Directory.Exists(destDir))
            {
                try { Directory.CreateDirectory(destDir); }
                catch (Exception ex)
                {
                    failureReason = $"Cannot create destination folder '{destDir}': {ex.Message}";
                    _logger.Error($"[FileMover] {failureReason}");
                    return false;
                }
            }

            // Never replaces a file. A link to the source is the one exception: the source may be deleted after the import.
            // File.Exists is true for a dangling link as well, so that is refused too.
            if (File.Exists(destinationFile) || Directory.Exists(destinationFile))
            {
                if (!LinksTo(destinationFile, sourceFile))
                {
                    failureReason = $"Target exists, not overwriting: {destinationFile}";
                    _logger.Warn($"[FileMover] {failureReason}");
                    return false;
                }
                try { File.Delete(destinationFile); }
                catch (Exception ex)
                {
                    failureReason = $"Could not remove link {destinationFile}: {ex.Message}";
                    _logger.Error($"[FileMover] {failureReason}");
                    return false;
                }
            }

            try
            {
                if (TryCreateHardLink(sourceFile, destinationFile))
                {
                    _logger.Info($"[FileMover] Hardlink created: {destinationFile} -> {sourceFile}");
                    return true;
                }
                _logger.Info($"[FileMover] Hardlink returned false. Falling back to copy.");
            }
            catch (Exception ex)
            {
                _logger.Error($"[FileMover] Hardlink failed ({ex.Message}). Falling back to copy.");
            }

            // btrfs shares (subvolumes) refuse hard links between them but share data blocks with a reflink:
            // an independent file that takes no extra space until one of them changes
            try
            {
                if (TryReflink(sourceFile, destinationFile))
                {
                    _logger.Info($"[FileMover] Reflinked (no extra space): {destinationFile} <- {sourceFile}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Info($"[FileMover] Reflink not possible ({ex.Message}). Copying.");
            }

            try
            {
                _logger.Info($"[FileMover] Copying file: {sourceFile} -> {destinationFile}");
                File.Copy(sourceFile, destinationFile, overwrite: false);
                return true;
            }
            catch (Exception ex)
            {
                failureReason = $"Copy {sourceFile} -> {destinationFile} failed: {ex.Message}";
                _logger.Error($"[FileMover] {failureReason}");
                return false;
            }
        }

        internal static bool LinksTo(string link, string target)
        {
            try
            {
                var info = new FileInfo(link);
                return info.LinkTarget != null
                    && string.Equals(info.ResolveLinkTarget(true)?.FullName, Path.GetFullPath(target), StringComparison.Ordinal);
            }
            catch (IOException) { return false; }
        }

        private bool TryCreateHardLink(string source, string destination)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return CreateHardLink(destination, source, IntPtr.Zero);
            }
            else
            {
                // Unix (Linux/macOS)
                if (Link(source, destination) == 0) return true;
                // Two Docker mounts of the same disk: link through the one mount that holds both
                var (s, d) = (SharedMount.Translate(source), SharedMount.Translate(destination));
                if (s == null || d == null) return false;
                if (Link(s, d) != 0) return false;
                _logger.Info($"[FileMover] Linked through {SharedMount.Root}: {d} -> {s}");
                return true;
            }
        }

        // FICLONE: the destination gets the source's data blocks (btrfs, XFS). Tried directly, then through the shared
        // mount, since Linux refuses it between two mounts. Leaves nothing behind when it fails.
        private const ulong FICLONE = 0x40049409;

        private static bool TryReflink(string source, string destination)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;
            if (Clone(source, destination)) return true;
            var (s, d) = (SharedMount.Translate(source), SharedMount.Translate(destination));
            return s != null && d != null && Clone(s, d);
        }

        private static bool Clone(string source, string destination)
        {
            var ok = false;
            var created = false;
            try
            {
                using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using (var dst = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
                {
                    created = true;
                    ok = Ioctl((int)dst.SafeFileHandle.DangerousGetHandle(), FICLONE, (int)src.SafeFileHandle.DangerousGetHandle()) == 0;
                }
                ok = ok && new FileInfo(destination).Length == src.Length;
                return ok;
            }
            finally
            {
                // Only a file this call made is removed, never one that was there
                if (!ok && created)
                {
                    try { File.Delete(destination); }
                    catch (IOException) { }
                }
            }
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
        private static extern int Ioctl(int fd, ulong request, int arg);

        // Windows Kernel32
        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        // Unix Libc
        [DllImport("libc", SetLastError = true, EntryPoint = "link")]
        private static extern int Link(string oldpath, string newpath);
    }
}
