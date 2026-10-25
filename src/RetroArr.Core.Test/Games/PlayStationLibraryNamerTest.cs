using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroArr.Core.Configuration;
using RetroArr.Core.Games;

namespace RetroArr.Core.Test.Games
{
    [TestFixture]
    public class PlayStationLibraryNamerTest
    {
        private string _root = null!;
        private string _ps5 = null!;
        private string _lz4 = null!;
        private PlayStationLibraryNamer _namer = null!;
        private TimeSpan _quiet;

        [SetUp]
        public void SetUp()
        {
            _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retroarr_psnames_" + Path.GetRandomFileName())).FullName;
            var config = new ConfigurationService(Directory.CreateDirectory(Path.Combine(_root, "config")).Parent!.FullName);
            var media = config.LoadMediaSettings();
            media.FolderPath = media.DestinationPath = Path.Combine(_root, "roms");
            config.SaveMediaSettings(media);
            _namer = new PlayStationLibraryNamer(config);
            _ps5 = Directory.CreateDirectory(_namer.Folders().First()).FullName;
            _lz4 = Directory.CreateDirectory(Path.Combine(_root, "ps5-lz4")).FullName;
            Environment.SetEnvironmentVariable("PS_LIBRARY_EXTRA", _lz4);
            _quiet = PlayStationLibraryNamer.QuietFor;
            PlayStationLibraryNamer.QuietFor = TimeSpan.FromHours(6);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("PS_LIBRARY_EXTRA", null);
            PlayStationLibraryNamer.QuietFor = _quiet;
            _namer.Dispose();
            Directory.Delete(_root, true);
        }

        private string Game(string folder, string name, bool old = true)
        {
            var dir = Directory.CreateDirectory(Path.Combine(folder, name)).FullName;
            File.WriteAllText(Path.Combine(dir, "eboot.bin"), "x");
            Directory.CreateDirectory(Path.Combine(dir, "sce_sys"));
            File.WriteAllText(Path.Combine(dir, "sce_sys", "param.json"),
                "{\"titleId\":\"PPSA26344\",\"contentVersion\":\"01.000.002\",\"localizedParameters\":{\"defaultLanguage\":\"en-US\",\"en-US\":{\"titleName\":\"Ghost of Yōtei™\"}}}");
            File.WriteAllText(Path.Combine(dir, "ampr_assets_0001.bin"), "z");
            if (old)
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-1));
            return dir;
        }

        [TestCase("PPSA26344-app0-lz4", true)]
        [TestCase("_PPSA03671_-lz4", true)]
        [TestCase("CUSA00900.pkg", true)]
        [TestCase("FINAL FANTASY VII REMAKE [PPSA01903] [v1.5]-lz4", false)]
        [TestCase("Some Game", false)]
        public void OnlyNamesThatAreJustATitleId_NeedAName(string name, bool needs)
        {
            Assert.That(PlayStationLibraryNamer.NeedsName(name), Is.EqualTo(needs));
        }

        [Test]
        public async Task TitleIdFolders_GetTheGamesOwnTitle_KeepingTheLz4Mark()
        {
            Game(_lz4, "PPSA26344-app0-lz4");
            Game(_ps5, "PPSA26344-app0");

            var done = await _namer.RunOnceAsync();

            Assert.That(done, Has.Count.EqualTo(2));
            Assert.That(Directory.Exists(Path.Combine(_lz4, "Ghost of Yōtei [PPSA26344] [v1.0.2]-lz4")), Is.True);
            Assert.That(Directory.Exists(Path.Combine(_ps5, "Ghost of Yōtei [PPSA26344] [v1.0.2]")), Is.True);
            Assert.That(Directory.EnumerateFiles(_root, "ps-renames.json", SearchOption.AllDirectories).Count(), Is.EqualTo(1), "every rename can be undone");
        }

        [Test]
        public async Task NamedOrBusyFolders_AreLeftAlone()
        {
            Game(_lz4, "FINAL FANTASY VII REMAKE [PPSA01903] [v1.5]-lz4");
            Game(_lz4, "PPSA26344-app0-lz4", old: false);
            Directory.CreateDirectory(Path.Combine(_lz4, ".ps5lz4-work", "PPSA11111"));

            Assert.That(await _namer.RunOnceAsync(), Is.Empty);
        }

        [Test]
        public async Task WithoutAnyTitle_NothingIsRenamed()
        {
            var dir = Directory.CreateDirectory(Path.Combine(_lz4, "PPSA22222-app0-lz4")).FullName;
            File.WriteAllText(Path.Combine(dir, "data.bin"), "x");
            File.SetLastWriteTimeUtc(Path.Combine(dir, "data.bin"), DateTime.UtcNow.AddDays(-1));
            Assert.That(await _namer.RunOnceAsync(), Is.Empty);
        }
    }
}
