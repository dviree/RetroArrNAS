using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroArr.Core.Configuration;
using RetroArr.Core.Download;
using RetroArr.Core.Games;
using RetroArr.Core.IO;
using RetroArr.Core.Test.Games;

namespace RetroArr.Core.Test.Download
{
    [TestFixture]
    public class PlayStationImporterTest
    {
        private string _root = null!;
        private string _library = null!;
        private string _inbox = null!;
        private PlayStationImporter _importer = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retroarr_psimport_" + Path.GetRandomFileName())).FullName;
            _library = Directory.CreateDirectory(Path.Combine(_root, "roms")).FullName;
            _inbox = Directory.CreateDirectory(Path.Combine(_root, "jdownloader")).FullName;
            var config = new ConfigurationService(Directory.CreateDirectory(Path.Combine(_root, "config")).Parent!.FullName);
            var media = config.LoadMediaSettings();
            media.TrashPath = Path.Combine(_root, "bin");
            config.SaveMediaSettings(media);
            _importer = new PlayStationImporter(new FileMoverService(), null, new TrashService(config), new ArchiveService(), Path.Combine(_root, "temp"));
            _importer.BackportRoot = Path.Combine(_root, "backport ps5");
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("PS_IMPORT_FOLDERS", null);
            Environment.SetEnvironmentVariable("PS_LIBRARY_EXTRA", null);
            Directory.Delete(_root, true);
        }

        private string Image(string relative, string version = "01.010.000", int padding = 700)
        {
            var json = PlayStationReadersTest.ParamJson.Replace("01.010.000", version);
            var path = Path.Combine(_inbox, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, PlayStationReadersTest.ExFat(("sce_sys/param.json", Encoding.UTF8.GetBytes(json)), ("eboot.bin", new byte[padding])));
            return path;
        }

        private string Ps5 => Path.Combine(_library, "ps5");

        private Task<PlayStationImporter.Outcome> Import(string source) =>
            _importer.ImportAsync(source, Path.GetFileName(source), _library, "ps5");

        [Test]
        public async Task ExfatImage_IsNamedFromItsOwnMetadata_AndCopiedIntoTheLibrary()
        {
            var source = Image("exfat/ff7r_repack.exfat");

            var result = await Import(source);

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(Directory.GetFileSystemEntries(Ps5).Select(Path.GetFileName), Is.EqualTo(new[] { "FINAL FANTASY VII REMAKE [PPSA01903] [v1.10].exfat" }));
            Assert.That(File.Exists(source), Is.True, "the importer copies; the watch folder decides about the source");
            Assert.That(result.VerifyFrom, Is.EqualTo(source));
        }

        [Test]
        public async Task NewerVersion_ReplacesTheOlder_WhichGoesToTheTrash()
        {
            Directory.CreateDirectory(Ps5);
            var old = Path.Combine(Ps5, "FINAL FANTASY VII REMAKE [PPSA01903] [v1.0].exfat");
            File.WriteAllText(old, "old");

            var result = await Import(Image("new.exfat", "02.000.000"));

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(Directory.GetFileSystemEntries(Ps5).Select(Path.GetFileName), Is.EqualTo(new[] { "FINAL FANTASY VII REMAKE [PPSA01903] [v2.0].exfat" }));
            Assert.That(Directory.EnumerateFiles(Path.Combine(_root, "bin"), "*.exfat", SearchOption.AllDirectories).Single(), Does.EndWith("[v1.0].exfat"));
        }

        [Test]
        public async Task OlderVersion_IsNotImported()
        {
            Directory.CreateDirectory(Ps5);
            File.WriteAllText(Path.Combine(Ps5, "FINAL FANTASY VII REMAKE [PPSA01903] [v3.0].exfat"), "newer");

            var result = await Import(Image("old.exfat"));

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(result.Destination, Does.EndWith("[v3.0].exfat"));
            Assert.That(Directory.GetFiles(Ps5), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task SameRelease_AlreadyThere_IsADuplicate_NotASecondCopy()
        {
            var source = Image("a.exfat");
            Assert.That((await Import(source)).Success, Is.True);

            var again = await Import(source);

            Assert.That(again.Success, Is.True);
            Assert.That(again.Destination, Does.EndWith("[v1.10].exfat"));
            Assert.That(Directory.GetFiles(Ps5), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task DlcAndUpdates_NeverReplaceTheGame()
        {
            Directory.CreateDirectory(Ps5);
            File.WriteAllText(Path.Combine(Ps5, "Ghost of Yotei [PPSA26344] [v1.0].exfat"), "game");
            var dlc = Path.Combine(_inbox, "EP9000-PPSA26344_00-LEGENDSPACK00001 DLC.pkg");
            File.WriteAllText(dlc, "dlc");

            var result = await _importer.ImportAsync(dlc, Path.GetFileName(dlc), _library, "ps5");

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(Directory.GetFiles(Ps5, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Ps5, f)), Is.EquivalentTo(new[]
            {
                "Ghost of Yotei [PPSA26344] [v1.0].exfat",
                Path.Combine("pkg", "Ghost of Yotei [DLC]", "Ghost of Yotei [PPSA26344] [DLC LEGENDSPACK00001].pkg")
            }), "the DLC takes the name of the game with its title ID, in the pkg folder");
            Assert.That(Directory.GetFiles(Ps5), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task GameFolder_IsImported_UnlessLeftForAConverter()
        {
            var folder = Directory.CreateDirectory(Path.Combine(_inbox, "Release", "PPSA01903-app0")).FullName;
            File.WriteAllText(Path.Combine(folder, "eboot.bin"), "x");
            Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
            File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), PlayStationReadersTest.ParamJson);
            var release = Path.Combine(_inbox, "Release");

            Environment.SetEnvironmentVariable("PS_IMPORT_FOLDERS", "false");
            Assert.That((await Import(release)).Reason, Does.Contain("converter"));

            Environment.SetEnvironmentVariable("PS_IMPORT_FOLDERS", null);
            var result = await Import(release);

            Assert.That(result.Success, Is.True, result.Reason);
            var landed = Path.Combine(Ps5, "FINAL FANTASY VII REMAKE [PPSA01903] [v1.10]");
            Assert.That(File.Exists(Path.Combine(landed, "eboot.bin")), Is.True, "the app0 folder is flattened into the game folder");
            Assert.That(File.Exists(Path.Combine(landed, "sce_sys", "param.json")), Is.True);
        }

        [Test]
        public async Task SeveralImagesInOneFolder_AreNotGuessedAt()
        {
            Image("exfat/a.exfat");
            Image("exfat/b.exfat");
            var result = await Import(Path.Combine(_inbox, "exfat"));
            Assert.That(result.Success, Is.False);
            Assert.That(result.Reason, Does.Contain("recursive"));
        }

        private string Zip(string relative, params (string Entry, byte[] Content)[] entries)
        {
            var path = Path.Combine(_inbox, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
            foreach (var (entry, content) in entries)
            {
                using var stream = zip.CreateEntry(entry).Open();
                stream.Write(content);
            }
            return path;
        }

        [Test]
        public async Task Archive_IsUnpackedToStaging_ImportedAndCleanedUp_WithTheArchiveUntouched()
        {
            var image = PlayStationReadersTest.ExFat(("sce_sys/param.json", Encoding.UTF8.GetBytes(PlayStationReadersTest.ParamJson)), ("eboot.bin", new byte[700]));
            var archive = Zip("FF7R.PS5-DUPLEX/ff7r.zip", ("ff7r.exfat", image));
            var before = File.ReadAllBytes(archive);

            var result = await Import(Path.GetDirectoryName(archive)!);

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(result.Unpacked, Is.True);
            Assert.That(Directory.GetFiles(Ps5).Select(Path.GetFileName), Is.EqualTo(new[] { "FINAL FANTASY VII REMAKE [PPSA01903] [v1.10].exfat" }));
            Assert.That(File.ReadAllBytes(archive), Is.EqualTo(before), "the archive is only read");
            Assert.That(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "temp")), Is.Empty, "staging is cleaned up");
        }

        [Test]
        public async Task ArchivedGameFolder_IsImportedAsAFolder_SinceTheConverterNeverSeesStaging()
        {
            var archive = Zip("Astro.zip",
                ("PPSA01903-app0/eboot.bin", new byte[10]),
                ("PPSA01903-app0/sce_sys/param.json", Encoding.UTF8.GetBytes(PlayStationReadersTest.ParamJson)));

            var result = await Import(archive);

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(File.Exists(Path.Combine(Ps5, "FINAL FANTASY VII REMAKE [PPSA01903] [v1.10]", "eboot.bin")), Is.True);
        }

        [Test]
        public async Task Archive_WithoutRoomToUnpack_IsLeftAlone()
        {
            var archive = Zip("Big.zip", ("x.exfat", new byte[100]));
            _importer.FreeSpace = _ => 10;
            var result = await Import(archive);
            Assert.That(result.Success, Is.False);
            Assert.That(result.Reason, Does.Contain("Not enough space"));
        }

        [Test]
        public async Task DamagedArchive_FailsWithoutImporting()
        {
            var path = Path.Combine(_inbox, "Broken [PPSA01903].zip");
            File.WriteAllText(path, "not a zip");
            var result = await Import(path);
            Assert.That(result.Success, Is.False);
            Assert.That(Directory.Exists(Ps5) ? Directory.GetFileSystemEntries(Ps5) : Array.Empty<string>(), Is.Empty);
        }

        [Test]
        public async Task ArchiveOfAGameTheLibraryHas_IsNotEvenUnpacked()
        {
            Directory.CreateDirectory(Ps5);
            File.WriteAllText(Path.Combine(Ps5, "FINAL FANTASY VII REMAKE [PPSA01903] [v1.10].exfat"), "there");
            var archive = Zip("FF7R [PPSA01903]/ff7r.zip", ("ff7r.exfat", new byte[100]));
            var extracted = 0;
            _importer.FreeSpace = _ => { extracted++; return long.MaxValue; };

            var result = await Import(Path.GetDirectoryName(archive)!);

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(result.Destination, Does.EndWith(".exfat"));
            Assert.That(extracted, Is.EqualTo(0), "no space check, so no unpacking was even started");
            Assert.That(Directory.GetFiles(Ps5), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task GameOnlyInTheLz4TestLibrary_IsImported()
        {
            var lz4 = Directory.CreateDirectory(Path.Combine(_root, "ps5-lz4", "FINAL FANTASY VII REMAKE [PPSA01903] [v1.10]-lz4")).FullName;
            Environment.SetEnvironmentVariable("PS_LIBRARY_EXTRA", Path.GetDirectoryName(lz4));
            var archive = Zip("ff7r-lz4.zip",
                ("PPSA01903-app0/eboot.bin", new byte[10]),
                ("PPSA01903-app0/ampr_assets_0001.bin", new byte[10]),
                ("PPSA01903-app0/sce_sys/param.json", Encoding.UTF8.GetBytes(PlayStationReadersTest.ParamJson)));

            var result = await Import(archive);

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(result.AlreadyInLibrary, Is.False, "the LZ4 library is a test bench, not the library");
            Assert.That(Directory.GetDirectories(Ps5).Single(), Does.Contain("[PPSA01903]"));
            Assert.That(Directory.Exists(lz4), Is.True);
        }

        [Test]
        public async Task AGameInAnyForm_IsAlreadyThere_AndNothingIsReplacedAcrossForms()
        {
            Directory.CreateDirectory(Ps5);
            var lz4 = Directory.CreateDirectory(Path.Combine(Ps5, "Onimusha [PPSA01903]-lz4")).FullName;
            var pkg = Path.Combine(Ps5, "FINAL FANTASY VII REMAKE [PPSA01903].pkg");
            File.WriteAllText(pkg, "pkg without a version in its name");

            var result = await Import(Image("ff7r.exfat", "02.000.000"));

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(File.Exists(pkg) && Directory.Exists(lz4), Is.True, "nothing is put in the trash");
            Assert.That(Directory.GetFiles(Ps5), Has.Length.EqualTo(1), "a game that is there in some form is not added again");
        }

        [Test]
        public async Task AnLz4FolderInTheLibrary_MakesTheReleaseAlreadyThere_SoItCanGo()
        {
            Directory.CreateDirectory(Path.Combine(Ps5, "Onimusha [PPSA01903]-lz4"));
            var result = await Import(Image("ff7r.exfat"));
            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(result.AlreadyInLibrary, Is.True);
            Assert.That(Directory.GetFiles(Ps5), Is.Empty);
        }

        [Test]
        public async Task APkg_IsAdded_EvenWhenTheGameIsThereAsLz4FolderOrImage()
        {
            Directory.CreateDirectory(Path.Combine(Ps5, "Onimusha Way of the Sword _PPSA27836_ _v1.0.1_-lz4"));
            File.WriteAllText(Path.Combine(Ps5, "Onimusha [PPSA27836] [v1.0].exfat"), "image");
            var pkg = Path.Combine(_inbox, "EP0102-PPSA27836_00-ONIMUSHAWOTS0000.pkg");
            File.WriteAllText(pkg, "fpkg");

            var result = await _importer.ImportAsync(pkg, Path.GetFileName(pkg), _library, "ps5");

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(result.AlreadyInLibrary, Is.False, "the PKG is not counted as there, so its release is not just thrown away");
            Assert.That(Directory.GetFiles(Path.Combine(Ps5, "pkg")).Single(), Does.Contain("[PPSA27836]"));
            Assert.That(Directory.GetDirectories(Ps5), Has.Length.EqualTo(2), "the LZ4 folder stays");
        }

        [Test]
        public async Task APkgInThePkgFolder_DoesNotStopAnLz4OrImage()
        {
            Directory.CreateDirectory(Path.Combine(Ps5, "pkg"));
            File.WriteAllText(Path.Combine(Ps5, "pkg", "FINAL FANTASY VII REMAKE [PPSA01903].pkg"), "pkg");
            var result = await Import(Image("ff7r.exfat"));
            Assert.That(result.AlreadyInLibrary, Is.False);
            Assert.That(Directory.GetFiles(Ps5).Single(), Does.EndWith("[v1.10].exfat"));
        }

        [Test]
        public async Task AnOlderLz4Folder_IsKept_WhenANewerGameArrives()
        {
            Directory.CreateDirectory(Ps5);
            var lz4 = Directory.CreateDirectory(Path.Combine(Ps5, "FF7R [PPSA01903] [v1.0]-lz4")).FullName;

            var result = await Import(Image("ff7r.exfat", "02.000.000"));

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(Directory.Exists(lz4), Is.True, "LZ4 folders are never replaced");
            Assert.That(Directory.GetFiles(Ps5).Single(), Does.EndWith("[v2.0].exfat"));
        }

        [Test]
        public async Task OlderGameInTheLz4Library_DoesNotStopANewerOne_AndIsLeftAlone()
        {
            var lz4 = Directory.CreateDirectory(Path.Combine(_root, "ps5-lz4", "FF7R [PPSA01903] [v1.0]")).FullName;
            Environment.SetEnvironmentVariable("PS_LIBRARY_EXTRA", Path.GetDirectoryName(lz4));

            var result = await Import(Image("ff7r.exfat", "02.000.000"));

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(Directory.GetFiles(Ps5).Single(), Does.EndWith("[v2.0].exfat"));
            Assert.That(Directory.Exists(lz4), Is.True, "another library is never changed");
        }

        [Test]
        public async Task SameVersionInAnotherFormat_IsAlreadyThere()
        {
            Directory.CreateDirectory(Ps5);
            File.WriteAllText(Path.Combine(Ps5, "FINAL FANTASY VII REMAKE [PPSA01903] [v1.10].ffpfsc"), "lz4");

            var result = await Import(Image("ff7r.exfat"));

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(Directory.GetFiles(Ps5), Has.Length.EqualTo(1));
        }

        [TestCase("PPSA02100", "ps5")]
        [TestCase("ECAS00001", "ps5")]
        [TestCase("CUSA00900", "ps4")]
        [TestCase(null, null)]
        public void TheTitleId_DecidesTheConsole(string? id, string? platform)
        {
            Assert.That(PlayStationImporter.PlatformForTitleId(id), Is.EqualTo(platform));
        }

        [TestCase("Ghost.of.Yotei.PS5-DUPLEX", "Ghost of Yotei")]
        [TestCase("250101 - Astro Bot [PPSA01234] [v1.05] [DUPLEX]", "Astro Bot")]
        [TestCase("Marvels_Spider-Man_2_PPSA03016_v1.002", "Marvels Spider-Man 2")]
        [TestCase("Black Myth Wukong PPSA23226-app0", "Black Myth Wukong")]
        [TestCase("UP9000-PPSA26344_00-GHOST2SHIP000000", "")]
        public void ReleaseNames_AreCleaned(string name, string title)
        {
            Assert.That(PlayStationImporter.CleanReleaseName(name), Is.EqualTo(title));
        }

        [Test]
        public void Names_StayShort_AndCarryIdVersionAndKind()
        {
            var game = new PlayStationPackage(PlayStationFormat.ExfatImage, "PPSA01903", "01.010.000", null, null);
            Assert.That(PlayStationImporter.BuildName("FF7 Remake", game, ".exfat"), Is.EqualTo("FF7 Remake [PPSA01903] [v1.10].exfat"));
            var update = game with { Content = PlayStationContent.Update };
            Assert.That(PlayStationImporter.BuildName("FF7", update, ".pkg"), Is.EqualTo("FF7 [PPSA01903] [Update] [v1.10].pkg"));
            var name = PlayStationImporter.BuildName(new string('A', 300), game, ".exfat");
            Assert.That(Encoding.UTF8.GetByteCount(name), Is.LessThanOrEqualTo(120));
            Assert.That(name, Does.EndWith(" [PPSA01903] [v1.10].exfat"));
            Assert.That(PlayStationImporter.SafeTitle("FINAL FANTASY VII REMAKE™: Intergrade"), Is.EqualTo("FINAL FANTASY VII REMAKE Intergrade"));
        }

        [Test]
        public async Task Ps4Pkgs_LandInTheGamesFolder_WithItsOtherPkgs()
        {
            var ps4 = Path.Combine(_library, "ps4");
            var game = Directory.CreateDirectory(Path.Combine(ps4, "Ghostbusters")).FullName;
            File.WriteAllText(Path.Combine(game, "Ghostbusters [CUSA12345] [v1.00].pkg"), "base");
            var dlc = Path.Combine(_inbox, "EP0000-CUSA12345_00-SLIMERPACK000001 DLC.pkg");
            File.WriteAllText(dlc, "dlc");

            var result = await _importer.ImportAsync(dlc, Path.GetFileName(dlc), _library, "ps4");

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(Path.GetDirectoryName(result.Destination), Is.EqualTo(game));
            Assert.That(Directory.EnumerateFiles(ps4), Is.Empty);
        }

        [Test]
        public async Task ABackportInARelease_IsKeptInTheBackportFolder_TheGameIsImported()
        {
            var release = Directory.CreateDirectory(Path.Combine(_inbox, "Onimusha Way of the Sword PPSA27836")).FullName;
            File.WriteAllText(Path.Combine(release, "EP0102-PPSA27836_00-ONIMUSHAWAYSWORD.pkg"), "game");
            var backport = Directory.CreateDirectory(Path.Combine(release, "Backport 4.xx")).FullName;
            File.WriteAllText(Path.Combine(backport, "fix.pkg"), "patched");

            var result = await _importer.ImportAsync(release, Path.GetFileName(release), _library, "ps5");

            Assert.That(result.Success, Is.True, result.Reason);
            Assert.That(result.Backport, Is.False);
            Assert.That(File.ReadAllText(Path.Combine(_root, "backport ps5", "Onimusha Way of the Sword PPSA27836", "Backport 4.xx", "fix.pkg")), Is.EqualTo("patched"));
            Assert.That(File.Exists(Path.Combine(release, "Backport 4.xx", "fix.pkg")), Is.True, "only copied; the source is retired by the caller");
        }

        [Test]
        public async Task AReleaseThatIsOnlyABackport_GoesToTheBackportFolder_AndNotIntoTheLibrary()
        {
            var release = Directory.CreateDirectory(Path.Combine(_inbox, "Backport 4.xx PPSA27836")).FullName;
            File.WriteAllText(Path.Combine(release, "fix.pkg"), "patched");

            var result = await _importer.ImportAsync(release, Path.GetFileName(release), _library, "ps5");

            Assert.That((result.Success, result.Backport), Is.EqualTo((true, true)), result.Reason);
            Assert.That(File.Exists(Path.Combine(_root, "backport ps5", "Backport 4.xx PPSA27836", "fix.pkg")), Is.True);
            Assert.That(Directory.Exists(Path.Combine(_library, "ps5")) && Directory.EnumerateFileSystemEntries(Path.Combine(_library, "ps5"), "*", SearchOption.AllDirectories).Any(), Is.False);
        }
    }
}
