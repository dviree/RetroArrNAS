using System;
using System.IO;
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
        public void ADownloadThatIsNotARarSet_NeverWaits()
        {
            var game = Directory.CreateDirectory(Path.Combine(_root, "Game")).FullName;
            File.WriteAllText(Path.Combine(game, "eboot.bin"), new string('g', 100000));
            File.WriteAllText(Path.Combine(game, "extras.zip"), "z");
            Assert.That(CompletedDownloadService.WaitBeforeUnpacking(game, _root, Now), Is.Null);
        }
    }
}
