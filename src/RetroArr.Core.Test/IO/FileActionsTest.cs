using System;
using NUnit.Framework;
using RetroArr.Core.IO;

namespace RetroArr.Core.Test.IO
{
    [TestFixture]
    public class FileActionsTest
    {
        [TestCase(null, false, false)]
        [TestCase("", false, false)]
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
                Assert.That(FileActions.DryRun, Is.EqualTo(value != "on"), "all jobs on only with FILE_ACTIONS=on");
            }
            finally
            {
                Environment.SetEnvironmentVariable("FILE_ACTIONS", saved);
                FileActions.Override = true;
            }
        }
    }
}
