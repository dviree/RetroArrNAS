using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroArr.Core.Configuration;
using RetroArr.Core.Games;
using RetroArr.Core.IO;

namespace RetroArr.Core.Test.Games
{
    [TestFixture]
    public class LeftoverCleanerTest
    {
        private string _root = null!;
        private string _roms = null!;
        private string _work = null!;
        private string _owner = null!;
        private LeftoverCleaner _cleaner = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retroarr_cleanup_" + Path.GetRandomFileName())).FullName;
            var config = new ConfigurationService(Directory.CreateDirectory(Path.Combine(_root, "config")).Parent!.FullName);
            var media = config.LoadMediaSettings();
            media.FolderPath = media.DestinationPath = _roms = Directory.CreateDirectory(Path.Combine(_root, "roms")).FullName;
            media.TrashPath = Path.Combine(_root, "bin");
            config.SaveMediaSettings(media);
            _work = Directory.CreateDirectory(Path.Combine(_root, "temp", "retroarr-ps")).FullName;
            _owner = Directory.CreateDirectory(Path.Combine(_root, "incoming", ".old-tool-work")).FullName;
            _cleaner = new LeftoverCleaner(new TrashService(config), config) { WorkFolders = new[] { _work }, OwnerFolders = new[] { _owner }, DryRunOverride = false };
        }

        [TearDown]
        public void TearDown()
        {
            _cleaner.Dispose();
            Directory.Delete(_root, true);
        }

        private string Put(string path, bool old = true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
            if (old)
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-5));
                for (var dir = Path.GetDirectoryName(path); dir != null && dir.Length > _root.Length; dir = Path.GetDirectoryName(dir))
                    Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-5));
            }
            return path;
        }

        [Test]
        public async Task EmptyFoldersInADownloadFolder_Go_SortingFoldersAndFoldersWithFilesStay()
        {
            var downloads = Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName;
            _cleaner.DownloadRoots = new[] { downloads };
            void Old(string dir) => Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-5));
            var emptyRelease = Directory.CreateDirectory(Path.Combine(downloads, "Game-GRP", "PPSA00001-app0")).FullName;
            Old(emptyRelease); Old(Path.GetDirectoryName(emptyRelease)!);
            var extracted = Directory.CreateDirectory(Path.Combine(downloads, "extracted")).FullName;
            Old(extracted);
            var fresh = Directory.CreateDirectory(Path.Combine(downloads, "Still-Downloading")).FullName;
            var withFile = Put(Path.Combine(downloads, "Other-GRP", "sub", "game.pkg"));
            var hidden = Directory.CreateDirectory(Path.Combine(downloads, ".jd-temp")).FullName;
            Old(hidden);

            await _cleaner.RunOnceAsync();

            Assert.That(Directory.Exists(Path.Combine(downloads, "Game-GRP")), Is.False, "an empty release folder goes, with its empty folders");
            Assert.That(Directory.Exists(extracted), Is.True, "a sorting folder stays even when empty");
            Assert.That(Directory.Exists(fresh), Is.True, "a folder made lately may still be filled");
            Assert.That(File.Exists(withFile), Is.True);
            Assert.That(Directory.Exists(hidden) && Directory.Exists(downloads), Is.True);
        }

        [Test]
        public async Task ACopyStillRunning_WithItsFilesOldDates_IsNotOld()
        {
            // A copy or an unpack keeps the files' own dates (a game from last month), but its folders are new
            var copying = Path.Combine(_roms, "ps5", ".retroarr-ps-d7afb0bd", "cache_ps5", "meshes", "head.xmesh");
            Directory.CreateDirectory(Path.GetDirectoryName(copying)!);
            File.WriteAllText(copying, "x");
            File.SetLastWriteTimeUtc(copying, DateTime.UtcNow.AddDays(-30));
            var unpacked = Path.Combine(_owner, "Ghost of Yotei", "eboot.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(unpacked)!);
            File.WriteAllText(unpacked, "x");
            File.SetLastWriteTimeUtc(unpacked, DateTime.UtcNow.AddDays(-30));

            Assert.That(await _cleaner.RunOnceAsync(), Is.EqualTo(0));
            Assert.That(File.Exists(copying) && File.Exists(unpacked), Is.True);
        }

        [Test]
        public async Task OldLeftovers_GoToTheTrash_GamesAndFreshWorkStay()
        {
            var oldWork = Put(Path.Combine(_work, "1a2b3c4d", "game.exfat"));
            var freshWork = Put(Path.Combine(_work, "9f8e7d6c", "game.exfat"), old: false);
            var oldOwner = Put(Path.Combine(_owner, "8af6a96a67a07312", "Graveyard Keeper 2", "game.dll"));
            var partial = Put(Path.Combine(_roms, "ps5", ".retroarr-ps-1234abcd.exfat"));
            var partialPkg = Put(Path.Combine(_roms, "ps5", "pkg", ".retroarr-ps-5678abcd.pkg"));
            var game = Put(Path.Combine(_roms, "ps5", "Stray [PPSA02100] [v1.0].exfat"));
            var hidden = Put(Path.Combine(_roms, "ps5", ".ps5lz4-work", "keep.bin"));

            Assert.That(await _cleaner.RunOnceAsync(), Is.EqualTo(4));

            Assert.That(Directory.Exists(Path.GetDirectoryName(oldWork)), Is.False);
            Assert.That(File.Exists(freshWork), Is.True, "work written lately may still be in use");
            Assert.That(Directory.Exists(Path.Combine(_owner, "8af6a96a67a07312")), Is.False);
            Assert.That(File.Exists(partial) || File.Exists(partialPkg), Is.False);
            Assert.That(File.Exists(game) && File.Exists(hidden), Is.True, "games and other tools' folders in roms stay");
            Assert.That(Directory.Exists(_work) && Directory.Exists(_owner), Is.True, "the folders themselves stay");
            Assert.That(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "bin", TrashService.DayFolderName(DateTime.Now))).Count(), Is.GreaterThanOrEqualTo(4), "nothing deleted, all in the trash, in the day's folder");
        }

        [Test]
        public async Task DryRun_MovesNothing_AndListsIt()
        {
            _cleaner.DryRunOverride = true;
            var oldWork = Put(Path.Combine(_work, "1a2b3c4d", "game.exfat"));

            Assert.That(await _cleaner.RunOnceAsync(), Is.EqualTo(1));

            Assert.That(File.Exists(oldWork), Is.True);
            Assert.That(_cleaner.Plan, Has.One.StartsWith("TRASH").And.EndsWith("1a2b3c4d"));
            Assert.That(File.Exists(Path.Combine(_root, "config", "cleanup-plan.log")), Is.False, "the plan goes to files.log, no file of its own");
        }

        [Test]
        public async Task WithoutFileActionsOn_EverythingIsADryRun_AndTheTrashIsNotEmptied()
        {
            _cleaner.DryRunOverride = null;
            var oldWork = Put(Path.Combine(_work, "1a2b3c4d", "game.exfat"));
            var config = new ConfigurationService(_root);
            var trash = new TrashService(config);
            var trashed = Put(Path.Combine(_root, "old-release", "game.iso"));
            await trash.MoveAsync(Path.GetDirectoryName(trashed)!, null, "old release");
            var media = config.LoadMediaSettings();
            media.TrashRetentionDays = 1;
            config.SaveMediaSettings(media);
            var sidecar = Directory.GetFiles(Path.Combine(_root, "bin", ".trash-meta")).Single();
            File.WriteAllText(sidecar, File.ReadAllText(sidecar).Replace(DateTime.UtcNow.Year + "-", (DateTime.UtcNow.Year - 1) + "-"));

            FileActions.Override = false;
            try
            {
                Assert.That(await _cleaner.RunOnceAsync(), Is.EqualTo(1));
                Assert.That(trash.PurgeExpired(), Is.EqualTo(0));
            }
            finally { FileActions.Override = true; }

            Assert.That(File.Exists(oldWork), Is.True, "a dry run moved something");
            Assert.That(trash.List(), Has.Count.EqualTo(1), "a dry run emptied the trash");
        }
    }
}
