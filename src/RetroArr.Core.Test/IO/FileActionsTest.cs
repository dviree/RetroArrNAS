using System;
using NUnit.Framework;
using RetroArr.Core.IO;

namespace RetroArr.Core.Test.IO
{
    [TestFixture]
    public class FileActionsTest
    {
        [TestCase(null, true, true)]
        [TestCase("", true, true)]
        [TestCase("  ", true, true)]
        [TestCase("off", false, false)]
        [TestCase("false", false, false)]
        [TestCase("dryrun", false, false)]
        [TestCase("on,trash", true, true)]
        [TestCase("on", true, true)]
        [TestCase("psnames", true, false)]
        [TestCase("PSNames, switch", true, false)]
        [TestCase("import", false, true)]
        public void OnlyWhatFileActionsNames_MayChangeFiles(string? value, bool namesOn, bool importOn)
        {
            var saved = Environment.GetEnvironmentVariable("FILE_ACTIONS");
            FileActions.Override = null;
            try
            {
                Environment.SetEnvironmentVariable("FILE_ACTIONS", value);
                Assert.That(FileActions.DryRunFor(FileActions.PsNames), Is.EqualTo(!namesOn));
                Assert.That(FileActions.DryRunFor(FileActions.Import), Is.EqualTo(!importOn));
                Assert.That(FileActions.DryRunFor(FileActions.TrashPurge), Is.EqualTo(value != "on,trash"), "the bin is never auto-emptied merely by enabling file operations");
                Assert.That(FileActions.DryRun, Is.EqualTo(!(string.IsNullOrWhiteSpace(value) || value.StartsWith("on", StringComparison.OrdinalIgnoreCase))), "general file handling follows the default or explicit on");
            }
            finally
            {
                Environment.SetEnvironmentVariable("FILE_ACTIONS", saved);
                FileActions.Override = true;
            }
        }
    }
}
