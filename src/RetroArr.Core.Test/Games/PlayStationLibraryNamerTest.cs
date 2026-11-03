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
            // Never the real default folder: every test has its own
            _namer = new PlayStationLibraryNamer(config) { ExtrasRoot = Path.Combine(_root, "backport-updates-dlc") };
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
            if (name.Contains("lz4")) File.WriteAllText(Path.Combine(dir, "ampr_assets_0001.bin"), "z");
            if (old)
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-1));
            return dir;
        }

        [TestCase("PPSA26344-app0-lz4", true)]
        [TestCase("_PPSA03671_-lz4", true)]
        [TestCase("CUSA00900.pkg", true)]
        [TestCase("FINAL FANTASY VII REMAKE [PPSA01903] [v1.5]-lz4", false)]
        [TestCase("Some Game", false)]
        [TestCase("261028 - Stray [PPSA02100] [VIKINGFILE].pkg", true)]
        [TestCase("Ghostbusters.The.Video.Game.Remastered_CUSA15558_v1.00_[6.72]_GROUP", true)]
        [TestCase("Ghostbusters The Video Game Remastered [CUSA15558]", false)]
        [TestCase("Sackboy A Big Adventure [PPSA01289] [DLC SABAPC0000000001].pkg", false)]
        [TestCase("Onimusha [PPSA27836] [Update] [v1.02].pkg", false)]
        [TestCase("Stray PPSA02100 Backport 4.xx", false)]
        public void OnlyNamesThatAreJustATitleId_NeedAName(string name, bool needs)
        {
            Assert.That(PlayStationLibraryNamer.NeedsName(name), Is.EqualTo(needs));
        }

        [Test]
        public async Task TitleIdFolders_GetTheGamesOwnTitle_Lz4FoldersInPlace()
        {
            Game(_lz4, "PPSA26344-app0-lz4");
            Game(_ps5, "PPSA26344-app0");
            Game(_ps5, "PPSA26344-app0-lz4");

            var done = await _namer.RunOnceAsync();

            Assert.That(done, Has.Count.EqualTo(3));
            Assert.That(Directory.Exists(Path.Combine(_lz4, "Ghost of Yōtei [PPSA26344] [v1.0.2]-lz4")), Is.True, "renamed where it is, never moved");
            Assert.That(Directory.Exists(Path.Combine(_ps5, "Ghost of Yōtei [PPSA26344] [v1.0.2]-lz4")), Is.True);
            Assert.That(Directory.Exists(Path.Combine(_ps5, "Ghost of Yōtei [PPSA26344] [v1.0.2]")), Is.True);
            Assert.That(Directory.EnumerateDirectories(_lz4).Count(), Is.EqualTo(1), "nothing left or added in the LZ4 library");
            Assert.That(Directory.EnumerateFiles(_root, "ps-renames.json", SearchOption.AllDirectories).Count(), Is.EqualTo(1), "every rename can be undone");
        }

        [Test]
        public async Task LoosePkgs_MoveIntoThePkgFolder()
        {
            var pkg = Path.Combine(_ps5, "Onimusha [PPSA11111] [v1.0].pkg");
            File.WriteAllText(pkg, "pkg");
            File.SetLastWriteTimeUtc(pkg, DateTime.UtcNow.AddDays(-1));

            await _namer.RunOnceAsync();

            Assert.That(File.Exists(Path.Combine(_ps5, "pkg", "Onimusha [PPSA11111] [v1.0].pkg")), Is.True);
            Assert.That(File.Exists(pkg), Is.False);
        }

        [Test]
        public async Task UpdateAndDlcPkgs_MoveToTheExtrasFolder_GamePkgsStay()
        {
            _namer.ExtrasRoot = Path.Combine(_root, "backport-updates-dlc");
            var pkgFolder = Directory.CreateDirectory(Path.Combine(_ps5, "pkg")).FullName;
            var oldDlcFolder = Directory.CreateDirectory(Path.Combine(pkgFolder, "Sackboy A Big Adventure [DLC]")).FullName;
            var files = new[]
            {
                Path.Combine(oldDlcFolder, "Sackboy A Big Adventure [PPSA01289] [DLC SABAPC0000000001].pkg"),
                Path.Combine(_ps5, "Sackboy A Big Adventure [PPSA01289] [DLC SABAPC0000000002].pkg"),
                Path.Combine(pkgFolder, "Onimusha [PPSA27836] [Update] [v1.02].pkg"),
            };
            var game = Path.Combine(pkgFolder, "Onimusha [PPSA27836] [v1.0].pkg");
            foreach (var f in files.Append(game))
            {
                File.WriteAllText(f, "pkg");
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-1));
            }

            await _namer.RunOnceAsync();

            Assert.That(Directory.EnumerateFiles(Path.Combine(_namer.ExtrasRoot, "dlc")).Select(Path.GetFileName), Is.EquivalentTo(files.Take(2).Select(Path.GetFileName)));
            Assert.That(Directory.EnumerateFiles(Path.Combine(_namer.ExtrasRoot, "updates")).Select(Path.GetFileName), Is.EqualTo(new[] { Path.GetFileName(files[2]) }));
            Assert.That(Directory.EnumerateFiles(_namer.ExtrasRoot), Is.Empty, "nothing loose in the extras folder");
            Assert.That(File.Exists(game), Is.True, "a game PKG stays in pkg");
            Assert.That(Directory.Exists(oldDlcFolder), Is.False, "the emptied DLC folder goes");
        }

        [Test]
        public async Task Ps4Pkgs_GoIntoOneFolderPerGame_NoPkgFolder()
        {
            var ps4 = Directory.CreateDirectory(_namer.Folders().First(f => f.EndsWith("ps4", StringComparison.Ordinal))).FullName;
            var existing = Directory.CreateDirectory(Path.Combine(ps4, "Ghostbusters")).FullName;
            File.WriteAllText(Path.Combine(existing, "Ghostbusters [CUSA12345] [v1.00].pkg"), "base");
            var loose = new[]
            {
                Path.Combine(ps4, "Bloodborne [CUSA00900] [v1.09].pkg"),
                Path.Combine(ps4, "Bloodborne [CUSA00900] [DLC THEOLDHUNTERS].pkg"),
                Path.Combine(ps4, "Ghostbusters [CUSA12345] [Update] [v1.02].pkg"),
            };
            foreach (var f in loose)
            {
                File.WriteAllText(f, "pkg");
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-1));
            }

            await _namer.RunOnceAsync();

            Assert.That(Directory.EnumerateFiles(ps4), Is.Empty, "no loose PKGs left");
            Assert.That(Directory.EnumerateFiles(Path.Combine(ps4, "Bloodborne [CUSA00900]")).Count(), Is.EqualTo(2), "base and DLC together");
            Assert.That(Directory.EnumerateFiles(existing).Count(), Is.EqualTo(2), "the update joins the folder the game is in already");
            Assert.That(Directory.Exists(Path.Combine(ps4, "pkg")), Is.False);
        }

        [Test]
        public async Task ReleaseNames_GetTheGamesName_FromTheirOwnTitle()
        {
            var pkgFolder = Directory.CreateDirectory(Path.Combine(_ps5, "pkg")).FullName;
            var stray = Path.Combine(pkgFolder, "261028 - Stray [PPSA02100] [VIKINGFILE].pkg");
            File.WriteAllText(stray, "pkg");
            File.SetLastWriteTimeUtc(stray, DateTime.UtcNow.AddDays(-1));

            await _namer.RunOnceAsync();

            Assert.That(Directory.EnumerateFiles(pkgFolder).Select(Path.GetFileName), Is.EqualTo(new[] { "Stray [PPSA02100].pkg" }));
        }

        [Test]
        public async Task TwoPs4FoldersOfOneGame_BecomeOne_OnlyWhenMergeIsOn()
        {
            var ps4 = Directory.CreateDirectory(_namer.Folders().First(f => f.EndsWith("ps4", StringComparison.Ordinal))).FullName;
            var a = Directory.CreateDirectory(Path.Combine(ps4, "261012 - Ghostbusters - The Video Game Remastered [CUSA15558] [1FICHIER]")).FullName;
            var b = Directory.CreateDirectory(Path.Combine(ps4, "Ghostbusters.The.Video.Game.Remastered_CUSA15558_v1.00_[6.72]_GROUP")).FullName;
            foreach (var f in new[] { Path.Combine(a, "base.pkg"), Path.Combine(b, "update.pkg"), Path.Combine(b, "base.pkg") })
            {
                File.WriteAllText(f, "pkg");
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-1));
            }

            _namer.MergeOverride = false;
            await _namer.RunOnceAsync();
            Assert.That(Directory.EnumerateDirectories(ps4).Count(), Is.EqualTo(2), "a dry run merges nothing");

            _namer.MergeOverride = true;
            await _namer.RunOnceAsync();

            var game = Path.Combine(ps4, "Ghostbusters - The Video Game Remastered [CUSA15558]");
            var dirs = Directory.EnumerateDirectories(ps4).Select(Path.GetFileName).ToList();
            Assert.That(dirs, Does.Contain(Path.GetFileName(game)));
            Assert.That(Directory.EnumerateFiles(game).Select(Path.GetFileName), Is.EquivalentTo(new[] { "base.pkg", "update.pkg" }));
            Assert.That(dirs, Has.Count.EqualTo(2), "the folder whose base.pkg was there already stays, with only that file");
            Assert.That(Directory.EnumerateFiles(b).Select(Path.GetFileName), Is.EqualTo(new[] { "base.pkg" }), "and keeps its own name: the game's name is the merged folder's");
        }

        [Test]
        public async Task TheOldDlcFolderInPkg_IsEmptiedIntoTheExtrasFolder()
        {
            _namer.ExtrasRoot = Path.Combine(_root, "backport-updates-dlc");
            var deep = Directory.CreateDirectory(Path.Combine(_ps5, "pkg", "dlc", "Sackboy A Big Adventure [DLC]")).FullName;
            var dlc = Path.Combine(deep, "Sackboy A Big Adventure [PPSA01289] [DLC SABAPC0000000001].pkg");
            File.WriteAllText(dlc, "pkg");
            File.SetLastWriteTimeUtc(dlc, DateTime.UtcNow.AddDays(-1));

            await _namer.RunOnceAsync();

            Assert.That(File.Exists(Path.Combine(_namer.ExtrasRoot, "dlc", Path.GetFileName(dlc))), Is.True);
            Assert.That(Directory.Exists(Path.Combine(_ps5, "pkg", "dlc")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(_ps5, "pkg")), Is.True);
        }

        [Test]
        public async Task WhatLiesLooseInTheExtrasFolder_IsSorted_OnlyWhenSortIsOn()
        {
            _namer.ExtrasRoot = Directory.CreateDirectory(Path.Combine(_root, "backport-updates-dlc")).FullName;
            var update = Path.Combine(_namer.ExtrasRoot, "Onimusha [PPSA27836] [Update] [v1.02].pkg");
            var dlc = Path.Combine(_namer.ExtrasRoot, "Sackboy A Big Adventure [PPSA01289] [DLC SABAPC0000000001].pkg");
            var backport = Directory.CreateDirectory(Path.Combine(_namer.ExtrasRoot, "Stray PPSA02100 Backport 4.xx")).FullName;
            foreach (var f in new[] { update, dlc, Path.Combine(backport, "fix.pkg") })
            {
                File.WriteAllText(f, "pkg");
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-1));
            }
            Directory.SetLastWriteTimeUtc(backport, DateTime.UtcNow.AddDays(-1));

            Assert.That(PlayStationLibraryNamer.SortExtrasOn, Is.True, "on unless PS_EXTRAS_SORT=false");
            _namer.SortOverride = false;
            await _namer.RunOnceAsync();
            Assert.That(File.Exists(update) && File.Exists(dlc) && Directory.Exists(backport), Is.True, "a dry run moves nothing");
            Assert.That(_namer.SortPlan, Has.Count.EqualTo(3));
            Assert.That(Directory.EnumerateFiles(_root, "extras-sort-plan.log", SearchOption.AllDirectories).Count(), Is.EqualTo(1));

            _namer.SortOverride = true;
            await _namer.RunOnceAsync();
            Assert.That(File.Exists(Path.Combine(_namer.ExtrasRoot, "updates", Path.GetFileName(update))), Is.True);
            Assert.That(File.Exists(Path.Combine(_namer.ExtrasRoot, "dlc", Path.GetFileName(dlc))), Is.True);
            Assert.That(File.Exists(Path.Combine(_namer.ExtrasRoot, "backport", "Stray PPSA02100 Backport 4.xx", "fix.pkg")), Is.True);
            Assert.That(Directory.EnumerateFileSystemEntries(_namer.ExtrasRoot).Select(Path.GetFileName), Is.EquivalentTo(new[] { "backport", "updates", "dlc" }));
        }

        [Test]
        public async Task APkgWithNoIdInItsName_IsNamedFromItsHeader_AndADlcGoesToDlc()
        {
            _namer.ExtrasRoot = Path.Combine(_root, "backport-updates-dlc");
            var pkgFolder = Directory.CreateDirectory(Path.Combine(_ps5, "pkg")).FullName;
            var game = Path.Combine(pkgFolder, "sable.pkg");
            File.WriteAllBytes(game, PlayStationReadersTest.Pkg("UP0000-CUSA12345_00-SABLE00000000000",
                PlayStationReadersTest.Sfo(("CATEGORY", "gd"), ("TITLE", "Sable"), ("TITLE_ID", "CUSA12345"), ("APP_VER", "01.05"))));
            var dlc = Path.Combine(pkgFolder, "costume.pkg");
            File.WriteAllBytes(dlc, PlayStationReadersTest.Pkg("EP9000-PPSA01289_00-SABAPC0000000009",
                PlayStationReadersTest.Sfo(("CATEGORY", "ac"), ("TITLE", "Sackboy Costume"), ("TITLE_ID", "PPSA01289"), ("APP_VER", "01.00"))));
            foreach (var f in new[] { game, dlc }) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-1));

            await _namer.RunOnceAsync();

            Assert.That(Directory.EnumerateFiles(pkgFolder).Select(Path.GetFileName), Is.EqualTo(new[] { "Sable [CUSA12345] [v1.5].pkg" }));
            Assert.That(Directory.EnumerateFiles(Path.Combine(_namer.ExtrasRoot, "dlc")).Count(), Is.EqualTo(1), "a DLC by its header, whatever its name");
            Assert.That(PlayStationLibraryNamer.NameSaysNothing(Path.Combine(pkgFolder, "Sable [CUSA12345] [v1.5].pkg")), Is.False);
        }

        [Test]
        public async Task APkgWithoutATitle_TakesTheGamesNameFromItsDlc_NeverItsFileName()
        {
            _namer.ExtrasRoot = Path.Combine(_root, "backport-updates-dlc");
            var dlcFolder = Directory.CreateDirectory(Path.Combine(_namer.ExtrasRoot, "dlc")).FullName;
            File.WriteAllText(Path.Combine(dlcFolder, "Sackboy A Big Adventure [PPSA01289] [DLC SABAKRATOS000001].pkg"), "dlc");
            var pkgFolder = Directory.CreateDirectory(Path.Combine(_ps5, "pkg")).FullName;
            var sable = Path.Combine(pkgFolder, "sable.pkg");
            File.WriteAllBytes(sable, PlayStationReadersTest.Pkg("EP9000-PPSA01289_00-SABAPC0000000001",
                PlayStationReadersTest.Sfo(("CATEGORY", "gd"), ("TITLE_ID", "PPSA01289"), ("APP_VER", "01.24"))));
            File.SetLastWriteTimeUtc(sable, DateTime.UtcNow.AddDays(-1));

            await _namer.RunOnceAsync();

            Assert.That(Directory.EnumerateFiles(pkgFolder).Select(Path.GetFileName), Is.EqualTo(new[] { "Sackboy A Big Adventure [PPSA01289] [v1.24].pkg" }),
                "the game's name, not \"Sable\" from the file name");
        }

        [Test]
        public async Task APkgWithoutATitleAnywhere_KeepsItsName()
        {
            var pkgFolder = Directory.CreateDirectory(Path.Combine(_ps5, "pkg")).FullName;
            var sable = Path.Combine(pkgFolder, "sable.pkg");
            File.WriteAllBytes(sable, PlayStationReadersTest.Pkg("EP9000-PPSA01289_00-SABAPC0000000001",
                PlayStationReadersTest.Sfo(("CATEGORY", "gd"), ("TITLE_ID", "PPSA01289"), ("APP_VER", "01.24"))));
            File.SetLastWriteTimeUtc(sable, DateTime.UtcNow.AddDays(-1));

            await _namer.RunOnceAsync();

            Assert.That(File.Exists(sable), Is.True);
        }

        [Test]
        public async Task RawDlcNames_GetTheGamesName_AndACopyOfANamedDlcGoesToTheTrash()
        {
            var config = new ConfigurationService(_root);
            var media = config.LoadMediaSettings();
            media.TrashPath = Path.Combine(_root, "bin");
            config.SaveMediaSettings(media);
            var namer = new PlayStationLibraryNamer(config, trash: new TrashService(config)) { ExtrasRoot = _namer.ExtrasRoot };
            var dlcFolder = Directory.CreateDirectory(Path.Combine(namer.ExtrasRoot, "dlc")).FullName;
            File.WriteAllText(Path.Combine(dlcFolder, "Sackboy A Big Adventure [PPSA01289] [DLC SABAAPPLE0000001].pkg"), "apple");
            var copy = Path.Combine(dlcFolder, "UP9000-PPSA01289_00-SABAAPPLE0000001.pkg");
            File.WriteAllText(copy, "apple");
            var other = Path.Combine(_ps5, "pkg", "UP9000-PPSA01289_00-SABAPRIDE0000001.pkg");
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.WriteAllText(other, "pride");
            foreach (var f in new[] { copy, other }) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-1));

            await namer.RunOnceAsync();
            await namer.RunOnceAsync();

            Assert.That(File.Exists(copy), Is.False, "the copy went to the trash");
            Assert.That(Directory.EnumerateFiles(Path.Combine(_root, "bin"), "*.pkg", SearchOption.AllDirectories).Count(), Is.EqualTo(1));
            Assert.That(Directory.EnumerateFiles(dlcFolder).Select(Path.GetFileName), Is.EquivalentTo(new[]
            {
                "Sackboy A Big Adventure [PPSA01289] [DLC SABAAPPLE0000001].pkg",
                "Sackboy A Big Adventure [PPSA01289] [DLC SABAPRIDE0000001].pkg",
            }), "the raw one got the game's name and went to dlc/, never \"Sable\"");
            namer.Dispose();
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

        [Test]
        public async Task UpdatesAndDlcOutsideTheLibrary_GetTheGamesName_Too()
        {
            _namer.ExtrasRoot = Directory.CreateDirectory(Path.Combine(_root, "backport-updates-dlc")).FullName;
            Game(Path.Combine(_namer.ExtrasRoot, "backport"), "PPSA26344-app0");

            await _namer.RunOnceAsync();

            Assert.That(Directory.Exists(Path.Combine(_namer.ExtrasRoot, "backport", "Ghost of Yōtei [PPSA26344] [v1.0.2]")), Is.True);
        }
    }
}
