using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroArr.Core.Download;
using RetroArr.Core.Download.TrackedDownloads;

namespace RetroArr.Core.Test.Download
{
    [TestFixture]
    public class WatchFolderClientTest
    {
        private string _root = null!;
        private TimeSpan _settle;

        [SetUp]
        public void SetUp()
        {
            _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retroarr_watch_" + Path.GetRandomFileName())).FullName;
            _settle = WatchFolderClient.SettleTime;
            WatchFolderClient.SettleTime = TimeSpan.FromMinutes(2);
        }

        [TearDown]
        public void TearDown()
        {
            WatchFolderClient.SettleTime = _settle;
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private string Write(string relative, string content = "data", bool old = true)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            if (old) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
            return path;
        }

        private async Task<List<DownloadStatus>> List() => await new WatchFolderClient(_root, "games").GetDownloadsAsync();

        [Test]
        public async Task GameFolderAndLooseFile_AreFinishedDownloads_WithTheirPlatform()
        {
            Write("[Nintendo Wii] Super Mario Galaxy 2 [NTSC]/[Nintendo Wii] Super Mario Galaxy 2 [NTSC].wbfs", "wii-game");
            Write("Advance Wars.gba");

            var downloads = await List();

            Assert.That(downloads, Has.Count.EqualTo(2));
            var wii = downloads.Single(d => d.Name.StartsWith("[Nintendo Wii]"));
            Assert.That(wii.State, Is.EqualTo(DownloadState.Completed));
            Assert.That(wii.PlatformFolder, Is.EqualTo("wii"));
            Assert.That(wii.Category, Is.EqualTo("games"));
            Assert.That(wii.Size, Is.EqualTo("wii-game".Length));
            Assert.That(wii.DownloadPath, Is.EqualTo(Path.Combine(_root, "[Nintendo Wii] Super Mario Galaxy 2 [NTSC]")));
            Assert.That(downloads.Single(d => d.Name == "Advance Wars.gba").PlatformFolder, Is.EqualTo("gba"));
        }

        [Test]
        public async Task ExtensionAlone_NamesThePlatform()
        {
            Write("Some Game/game.nsp");
            Assert.That((await List()).Single().PlatformFolder, Is.EqualTo("switch"));
        }

        [Test]
        public async Task NothingNamingAPlatform_LeavesItUnmapped()
        {
            Write("Mystery/readme.bin");
            var download = (await List()).Single();
            Assert.That(download.PlatformFolder, Is.Null);
            Assert.That(download.State, Is.EqualTo(DownloadState.Completed));
        }

        [Test]
        public async Task RecentlyWrittenOrPartialFiles_AreStillDownloading()
        {
            Write("Fresh Game/game.wbfs", old: false);
            Write("Partial Game/game.wbfs");
            Write("Partial Game/game.iso.part");

            var downloads = await List();

            Assert.That(downloads.Select(d => d.State), Is.All.EqualTo(DownloadState.Downloading));
        }

        [TestCase("Ghost.of.Yotei.Complete.Edition.PS5-DUPLEX/game.pkg")]
        [TestCase("FINAL FANTASY VII REMAKE [PPSA01903]/game.exfat")]
        [TestCase("Bloodborne [CUSA00900]/game.pkg")]
        public async Task PlayStation4And5_AreListedButHeldBack(string relative)
        {
            Write(relative);
            var download = (await List()).Single();
            Assert.That(download.State, Is.EqualTo(DownloadState.Paused), "a held-back release is never imported");
            Assert.That(download.StatusMessages.Single(), Does.Contain("not imported"));
        }

        [Test]
        public async Task Lz4Folder_SaysSo()
        {
            Write("Ghost of Yotei [PPSA12345]/ampr_assets_0001.bin");
            Write("Ghost of Yotei [PPSA12345]/eboot.bin");
            Assert.That((await List()).Single().StatusMessages.Single(), Does.Contain("LZ4-compressed game folder (PPSA12345)"));
        }

        [Test]
        public async Task HiddenWorkFoldersAndEmptyFolders_AreSkipped()
        {
            Write(".gamesauce-work/job/game.wbfs");
            Directory.CreateDirectory(Path.Combine(_root, "Empty"));
            Assert.That(await List(), Is.Empty);
        }

        [Test]
        public async Task MissingFolder_ListsNothing_AndFailsTheConnectionTest()
        {
            var client = new WatchFolderClient(Path.Combine(_root, "not-there"));
            Assert.That(await client.GetDownloadsAsync(), Is.Empty);
            Assert.That(await client.TestConnectionAsync(), Is.False);
            Assert.That(await new WatchFolderClient(_root).TestConnectionAsync(), Is.True);
        }

        [Test]
        public async Task FolderItMayNotList_FailsTheConnectionTest()
        {
            if (OperatingSystem.IsWindows() || Environment.UserName == "root")
                Assert.Ignore("root reads any folder; the mode only stops other users");
            var locked = Directory.CreateDirectory(Path.Combine(_root, "locked")).FullName;
            File.SetUnixFileMode(locked, UnixFileMode.UserWrite);
            try
            {
                Assert.That(await new WatchFolderClient(locked).TestConnectionAsync(), Is.False);
            }
            finally
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        [Test]
        public async Task SameFolder_KeepsItsId()
        {
            Write("Advance Wars.gba");
            Assert.That((await List()).Single().Id, Is.EqualTo((await List()).Single().Id));
        }

        [Test]
        public void NeverTakesAGrab()
        {
            var client = new WatchFolderClient(_root);
            Assert.ThrowsAsync<NotSupportedException>(() => client.AddTorrentAsync("magnet:?xt=urn:btih:x"));
            Assert.ThrowsAsync<NotSupportedException>(() => client.AddNzbAsync("http://x/y.nzb"));
        }

        [Test]
        public async Task TrackedDownload_KeepsTheWatchFolderPlatform_WhenNoGrabKnowsIt()
        {
            Write("Advance Wars.gba");
            var configDir = Directory.CreateDirectory(Path.Combine(_root, ".config")).FullName;
            var tracker = new DownloadPlatformTracker(configDir);
            var service = new TrackedDownloadService(configDir);

            var tracked = service.TrackDownload((await List()).Single(), 1, "watch", tracker);

            Assert.That(tracked.PlatformFolder, Is.EqualTo("gba"));
            Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportPending));
        }
    }
}
