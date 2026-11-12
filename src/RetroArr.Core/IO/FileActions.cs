using System;
using System.Linq;

namespace RetroArr.Core.IO
{
    /// <summary>
    /// File operations run by default. FILE_ACTIONS=off (or dryrun) only logs plans;
    /// a job list enables selected operations. Automatic trash purging always requires
    /// an explicit "trash" entry, so enabling file handling never empties the bin.
    /// </summary>
    public static class FileActions
    {
        // internal for tests: the suite runs with file actions on, a test of the dry run turns it back
        internal static bool? Override;

        // "on" enables file handling; automatic trash purging needs an explicit "trash" entry.
        public const string Import = "import", Cleanup = "cleanup", TrashPurge = "trash", Switch = "switch",
            Ps4Merge = "ps4merge", PsNames = "psnames", Bios = "bios";

        /// <summary>Whether general file handling is in dry-run mode.</summary>
        public static bool DryRun => DryRunFor("*");

        /// <summary>Whether this job should only log its planned changes.</summary>
        public static bool DryRunFor(string job)
        {
            if (Override.HasValue) return !Override.Value;
            var value = (Environment.GetEnvironmentVariable("FILE_ACTIONS") ?? string.Empty).Trim();
            if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "dryrun", StringComparison.OrdinalIgnoreCase)) return true;
            var jobs = value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (string.Equals(job, TrashPurge, StringComparison.OrdinalIgnoreCase))
                return !jobs.Any(j => string.Equals(j, TrashPurge, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(value) || jobs.Any(j => string.Equals(j, "on", StringComparison.OrdinalIgnoreCase))) return false;
            return !value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(j => string.Equals(j, job, StringComparison.OrdinalIgnoreCase));
        }
    }
}
