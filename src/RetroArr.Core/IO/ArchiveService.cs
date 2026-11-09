using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using System.Collections.Generic;

namespace RetroArr.Core.IO
{
    public interface IArchiveService
    {
        // volumes, if given, collects every file a multi-volume archive was read from
        bool Extract(string sourceFile, string destinationDirectory, ICollection<string>? volumes = null);
        bool IsArchive(string path);
    }

    public class ArchiveService : IArchiveService
    {
        /// <summary>The owner's password list, one per line (archive-passwords.txt in the config folder, or ARCHIVE_PASSWORDS_FILE).</summary>
        public static string? PasswordFile = Environment.GetEnvironmentVariable("ARCHIVE_PASSWORDS_FILE");
        private const string NoPasswordFits = "\0none";

        private static readonly NLog.Logger _logger = NLog.LogManager.GetLogger(Logging.AppLoggerService.DownloadsImport);
        private readonly string[] _supportedExtensions = { ".zip", ".rar", ".7z" };

        /// <summary>
        /// The password that opens the archive: null when it is not locked, NoPasswordFits when none of the candidates
        /// does. Candidates as JDownloader tries them: the owner's list, ARCHIVE_PASSWORDS, then what the names hold
        /// (a site tag like "[DLPSGAME.COM]", a domain, the release name).
        /// </summary>
        internal static string? FindPassword(string sourceFile)
        {
            var headersLocked = false;
            try
            {
                using var archive = ArchiveFactory.OpenArchive(sourceFile);
                if (!archive.Entries.Any(e => !e.IsDirectory && e.IsEncrypted)) return null;
            }
            catch (Exception) { headersLocked = true; }
            foreach (var candidate in PasswordCandidates(sourceFile))
                if (Opens(sourceFile, candidate, headersLocked))
                {
                    _logger.Info($"[ArchiveService] Found the password of {Path.GetFileName(sourceFile)}");
                    return candidate;
                }
            return NoPasswordFits;
        }

        internal static IEnumerable<string> PasswordCandidates(string sourceFile)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<string>();
            if (PasswordFile is { } file && File.Exists(file))
                try { list.AddRange(File.ReadAllLines(file)); } catch (Exception) { }
            list.AddRange((Environment.GetEnvironmentVariable("ARCHIVE_PASSWORDS") ?? string.Empty).Split('\n', ';'));
            var names = new[] { Path.GetFileName(sourceFile), Path.GetFileName(Path.GetDirectoryName(sourceFile) ?? string.Empty) };
            foreach (var name in names.Where(n => n.Length > 0))
            {
                var stem = System.Text.RegularExpressions.Regex.Replace(name, @"(\.part\d+)?\.(rar|7z|zip)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match tag in System.Text.RegularExpressions.Regex.Matches(stem, @"[\[\(]([^\]\)]+)[\]\)]"))
                {
                    // The tag as it stands, brackets and all ("[DLPSGAME.COM]"), then without them
                    list.Add(tag.Value);
                    list.AddRange(Variants(tag.Groups[1].Value));
                }
                foreach (System.Text.RegularExpressions.Match domain in System.Text.RegularExpressions.Regex.Matches(stem, @"(?i)(?:www\.)?[a-z0-9-]+\.(?:com|net|org|to|cc|ru|in|io|me|co|info|xyz)\b"))
                    list.AddRange(Variants(domain.Value));
                list.Add(stem);
                list.Add(System.Text.RegularExpressions.Regex.Replace(stem, @"^[\[\(][^\]\)]+[\]\)][\s._-]*", ""));
            }
            foreach (var p in list.Select(p => p.Trim()).Where(p => p.Length > 0))
                if (seen.Add(p)) yield return p;
        }

        // "DLPSGAME.COM" -> itself, lower case, www.-prefixed, without the domain ending
        private static IEnumerable<string> Variants(string tag)
        {
            var t = tag.Trim();
            yield return t;
            yield return t.ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[\w-]+\.[a-z]{2,5}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                yield return "www." + t.ToLowerInvariant();
                yield return t[..t.LastIndexOf('.')];
                yield return t[..t.LastIndexOf('.')].ToLowerInvariant();
            }
        }

        // Locked headers only list their files with the right password, so nothing more is read (a 100 GB set is
        // told in a second). Else the password fits when a locked file comes out whole (its checksum is checked at
        // its end): the smallest one, or the first one of a solid archive, which can only be read in order.
        private static bool Opens(string sourceFile, string password, bool headersLocked)
        {
            try
            {
                using var archive = ArchiveFactory.OpenArchive(sourceFile, new SharpCompress.Readers.ReaderOptions { Password = password });
                var files = archive.Entries.Where(e => !e.IsDirectory).ToList();
                if (files.Count == 0) return false;
                if (headersLocked) return true;
                var entry = archive.IsSolid ? files[0] : files.OrderBy(e => e.Size).First();
                using var stream = entry.OpenEntryStream();
                stream.CopyTo(Stream.Null);
                return true;
            }
            catch (Exception) { return false; }
        }

        public bool IsArchive(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var ext = Path.GetExtension(path).ToLower();
            return _supportedExtensions.Contains(ext);
        }

        public bool Extract(string sourceFile, string destinationDirectory, ICollection<string>? volumes = null)
        {
            try
            {
                if (!File.Exists(sourceFile)) return false;
                
                if (!Directory.Exists(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                var ext = Path.GetExtension(sourceFile).ToLower();
                _logger.Info($"[ArchiveService] Extracting {sourceFile} to {destinationDirectory}...");

                if (ext == ".zip")
                {
                    ZipFile.ExtractToDirectory(sourceFile, destinationDirectory, overwriteFiles: true);
                    return true;
                }
                else if (ext == ".rar" || ext == ".7z")
                {
                    // A locked archive: the passwords are tried as JDownloader does, from the list and from the names
                    var password = FindPassword(sourceFile);
                    if (password == NoPasswordFits)
                    {
                        _logger.Error($"[ArchiveService] {sourceFile} is password protected and no password fits (or it is damaged); add the password to {PasswordFile ?? "archive-passwords.txt"} (one per line)");
                        return false;
                    }
                    using (var archive = ArchiveFactory.OpenArchive(sourceFile, new SharpCompress.Readers.ReaderOptions { Password = password }))
                    {
                        foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                        {
                            entry.WriteToDirectory(destinationDirectory, new ExtractionOptions()
                            {
                                ExtractFullPath = true,
                                Overwrite = true
                            });
                        }
                        // a single file comes back without a name
                        foreach (var volume in archive.Volumes)
                        {
                            if (!string.IsNullOrEmpty(volume.FileName)) volumes?.Add(volume.FileName);
                        }
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[ArchiveService] Extraction failed: {ex.Message}");
            }
            return false;
        }
    }
}
