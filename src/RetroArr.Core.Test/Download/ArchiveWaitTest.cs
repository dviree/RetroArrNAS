using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RetroArr.Core.Download.TrackedDownloads;

namespace RetroArr.Core.Test.Download
{
    [TestFixture]
    public class ArchiveWaitTest
    {
        private string _root = null!;
        private string _set = null!;
        private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

        [SetUp]
        public void SetUp()
        {
            _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retroarr_wait_" + Path.GetRandomFileName())).FullName;
            _set = Directory.CreateDirectory(Path.Combine(_root, "[SITE.COM]-PPSA26344-DUPLEX")).FullName;
            foreach (var part in new[] { "part01", "part02" })
            {
                var file = Path.Combine(_set, $"[SITE.COM]-PPSA26344-DUPLEX.{part}.rar");
                File.WriteAllText(file, new string('r', 1000));
            }
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        private void Age(TimeSpan age)
        {
            foreach (var f in Directory.GetFiles(_set)) File.SetLastWriteTimeUtc(f, Now - age);
        }

        [Test]
        public void ARarSet_WaitsThreeHours_ForJDownloader()
        {
            Age(TimeSpan.FromHours(1));
            Assert.That(CompletedDownloadService.WaitBeforeUnpacking(_set, _root, Now), Does.StartWith("Waiting for JDownloader"));
            Age(TimeSpan.FromHours(3.1));
            Assert.That(CompletedDownloadService.WaitBeforeUnpacking(_set, _root, Now), Is.Null, "after 3 hours RetroArr unpacks it");
        }

        [Test]
        public void WhatJDownloaderUnpacked_IsImported_TheSetWaits()
        {
            Age(TimeSpan.FromHours(5));
            var unpacked = Directory.CreateDirectory(Path.Combine(_root, "extracted", "[SITE.COM]-PPSA26344-DUPLEX", "PPSA26344-app0")).FullName;
            Assert.That(CompletedDownloadService.WaitBeforeUnpacking(_set, _root, Now), Is.Null, "an empty folder left by a stopped unpack is no copy");

            File.WriteAllText(Path.Combine(unpacked, "eboot.bin"), "game");
            Assert.That(CompletedDownloadService.WaitBeforeUnpacking(_set, _root, Now), Does.StartWith("JDownloader unpacked it"));
        }

        [Test]
        public void ASetWhoseFirstPartsAreGone_IsBeingUnpacked_AndWaits_EvenWithTheHalfUnpackedGameBesideIt()
        {
            // JDownloader removes each part once it is through it, and unpacks into the same folder
            File.Delete(Path.Combine(_set, "[SITE.COM]-PPSA26344-DUPLEX.part01.rar"));
            var game = Directory.CreateDirectory(Path.Combine(_set, "[SITE.COM] PPSA26344", "Game", "exec")).FullName;
            File.WriteAllText(Path.Combine(game, "eboot.bin"), new string('g', 50000));
            Age(TimeSpan.FromHours(6));

            Assert.That(CompletedDownloadService.WaitBeforeUnpacking(_set, _root, Now), Does.StartWith("JDownloader is unpacking it"));

            // Once the last part is gone too, the unpacked game is a download like any other
            File.Delete(Path.Combine(_set, "[SITE.COM]-PPSA26344-DUPLEX.part02.rar"));
            Assert.That(CompletedDownloadService.WaitBeforeUnpacking(_set, _root, Now), Is.Null);
        }

        [Test]
        public void AGameStillBeingUnpacked_IsNotTakenForAFinishedOne_ThoughItsFilesCarryOldDates()
        {
            // unrar and JDownloader give each file the date it has in the archive; the folders they make are new
            var game = Directory.CreateDirectory(Path.Combine(_root, "Unpacking", "PPSA26344-app0", "sce_sys")).FullName;
            var eboot = Path.Combine(Path.GetDirectoryName(game)!, "eboot.bin");
            File.WriteAllText(eboot, "x");
            File.SetLastWriteTimeUtc(eboot, DateTime.UtcNow.AddDays(-30));
            var files = new DirectoryInfo(Path.Combine(_root, "Unpacking")).GetFiles("*", SearchOption.AllDirectories).ToList();

            var newest = RetroArr.Core.Download.WatchFolderClient.NewestChange(new DirectoryInfo(Path.Combine(_root, "Unpacking")), files);

            Assert.That(DateTime.UtcNow - newest, Is.LessThan(TimeSpan.FromMinutes(5)), "the new folders count, so it is still being written");
        }

        [Test]
        public void ADownloadThatIsNotARarSet_NeverWaits()
        {
            var game = Directory.CreateDirectory(Path.Combine(_root, "Game")).FullName;
            File.WriteAllText(Path.Combine(game, "eboot.bin"), new string('g', 100000));
            File.WriteAllText(Path.Combine(game, "extras.zip"), "z");
            Assert.That(CompletedDownloadService.WaitBeforeUnpacking(game, _root, Now), Is.Null);
        }
    }
}
