using System;

namespace RetroArr.Core.IO
{
    /// <summary>
    /// The one switch for everything RetroArr does to files on its own: imports, the cleanup, the trash's own emptying,
    /// the Switch and PS4 merges, the PlayStation naming and the BIOS sorter. Unless FILE_ACTIONS=on, it is a dry run:
    /// nothing is moved, copied, renamed, trashed or deleted, and what would have been done goes to files.log as
    /// "WOULD ..." lines. The owner reads the plan first and only then turns it on.
    /// </summary>
    public static class FileActions
    {
        // internal for tests: the suite runs with file actions on, a test of the dry run turns it back
        internal static bool? Override;

        public static bool DryRun => Override.HasValue
            ? !Override.Value
            : !string.Equals((Environment.GetEnvironmentVariable("FILE_ACTIONS") ?? string.Empty).Trim(), "on", StringComparison.OrdinalIgnoreCase);
    }
}
