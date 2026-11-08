using System;
using System.Linq;

namespace RetroArr.Core.IO
{
    /// <summary>
    /// The one switch for everything RetroArr does to files on its own: imports, the cleanup, the trash's own emptying,
    /// the Switch and PS4 merges, the PlayStation naming and the BIOS sorter. Unless FILE_ACTIONS=on, or a list naming
    /// the job (FILE_ACTIONS=psnames turns on only the PlayStation naming), it is a dry run:
    /// nothing is moved, copied, renamed, trashed or deleted, and what would have been done goes to files.log as
    /// "WOULD ..." lines. The owner reads the plan first and only then turns it on.
    /// </summary>
    public static class FileActions
    {
        // internal for tests: the suite runs with file actions on, a test of the dry run turns it back
        internal static bool? Override;

        // The jobs, as FILE_ACTIONS names them: "on" turns all of them on, or a list ("psnames,switch") only those
        public const string Import = "import", Cleanup = "cleanup", TrashPurge = "trash", Switch = "switch",
            Ps4Merge = "ps4merge", PsNames = "psnames", Bios = "bios";

        /// <summary>True unless FILE_ACTIONS is "on" (every job may change files).</summary>
        public static bool DryRun => DryRunFor("*");

        /// <summary>True unless FILE_ACTIONS is "on" or names this job (FILE_ACTIONS=psnames,switch).</summary>
        public static bool DryRunFor(string job)
        {
            if (Override.HasValue) return !Override.Value;
            var value = (Environment.GetEnvironmentVariable("FILE_ACTIONS") ?? string.Empty).Trim();
            if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)) return false;
            return !value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(j => string.Equals(j, job, StringComparison.OrdinalIgnoreCase));
        }
    }
}
