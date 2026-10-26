using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroArr.Core.Configuration;
using RetroArr.Core.Games;
using RetroArr.Core.IO;

namespace RetroArr.Core.Test.Games
{
    [TestFixture]
    public class BiosSorterTest
    {
        private string _root = null!;
        private string _bios = null!;
        private TrashService _trash = null!;
        private BiosSorter _sorter = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retroarr_bios_" + Path.GetRandomFileName())).FullName;
            var config = new ConfigurationService(Directory.CreateDirectory(Path.Combine(_root, "config")).Parent!.FullName);
            var media = config.LoadMediaSettings();
            media.TrashPath = Path.Combine(_root, "bin");
            config.SaveMediaSettings(media);
            _trash = new TrashService(config);
            _bios = Directory.CreateDirectory(Path.Combine(_root, "bios")).FullName;
            _sorter = new BiosSorter(_trash, new ArchiveService()) { Root = _bios, SettleTime = TimeSpan.Zero };
        }

        [TearDown]
        public void TearDown()
        {
            _sorter.Dispose();
            Directory.Delete(_root, true);
        }

        private string Drop(string relative, string content = "bios")
        {
            var path = Path.Combine(_bios, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        private string[] Sorted() => Directory.EnumerateFiles(_bios, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_bios, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToArray();

        [TestCase("scph5501.bin", null, "psx")]
        [TestCase("scph7502.bin", null, "psx")]
        [TestCase("SCPH-39001.bin", null, "ps2")]
        [TestCase("prod.keys", null, "switch/keys")]
        [TestCase("dc_boot.bin", null, "dc")]
        [TestCase("gba_bios.bin", null, "gba")]
        [TestCase("bios.bin", "Sega Saturn BIOS", "saturn")]
        [TestCase("bios.bin", "PS2 BIOS pack", "ps2")]
        [TestCase("0a1b2c.nca", "Switch Firmware 19.0.1", "switch/firmware")]
        [TestCase("bios.bin", "ups2x", "unsorted")]
        [TestCase("something.bin", null, "unsorted")]
        public void FolderFor_KnownNamesAndPlatformWords(string name, string? cameIn, string folder)
        {
            Assert.That(BiosSorter.FolderFor(name, cameIn), Is.EqualTo(folder));
        }

        [Test]
        public async Task LooseFilesAndFolders_AreSorted_JunkGoesToTheTrash()
        {
            Drop("scph5501.bin");
            Drop("prod.keys", "keys");
            Drop("Sega Saturn BIOS/sega_101.bin", "saturn");
            Drop("Sega Saturn BIOS/readme.nfo", "junk");
            Drop("mystery.bin", "?");

            Assert.That(await _sorter.RunOnceAsync(), Is.EqualTo(4));

            Assert.That(Sorted(), Is.EqualTo(new[] { "psx/scph5501.bin", "saturn/sega_101.bin", "switch/keys/prod.keys", "unsorted/mystery.bin" }));
            Assert.That(_trash.List(), Has.Count.EqualTo(2), "the .nfo and the emptied folder");
        }

        [Test]
        public async Task SortedFolders_AreNotSortedAgain()
        {
            Drop("psx/custom.bin");
            Drop("unsorted/mystery.bin");
            Assert.That(await _sorter.RunOnceAsync(), Is.Zero);
            Assert.That(Sorted(), Is.EqualTo(new[] { "psx/custom.bin", "unsorted/mystery.bin" }));
        }

        [Test]
        public async Task AnExactDuplicate_OrASmallerFileOfTheSameName_GoesToTheTrash()
        {
            Drop("psx/scph5501.bin", "original");
            Drop("scph5501.bin", "original");
            Drop("ps2/scph39001.bin", "full bios");
            Drop("scph39001.bin", "short");

            await _sorter.RunOnceAsync();

            Assert.That(Sorted(), Is.EqualTo(new[] { "ps2/scph39001.bin", "psx/scph5501.bin" }));
            Assert.That(File.ReadAllText(Path.Combine(_bios, "ps2", "scph39001.bin")), Is.EqualTo("full bios"));
            Assert.That(_trash.List(), Has.Count.EqualTo(2));
        }

        [Test]
        public async Task AZip_IsUnpackedAndSorted_ThenGoesToTheTrash()
        {
            var zip = Path.Combine(_bios, "Dreamcast BIOS.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(archive.CreateEntry("dc_boot.bin").Open())) w.Write("boot");
                using (var w = new StreamWriter(archive.CreateEntry("flash.bin").Open())) w.Write("flash");
            }

            Assert.That(await _sorter.RunOnceAsync(), Is.EqualTo(2));

            Assert.That(Sorted(), Is.EqualTo(new[] { "dc/dc_boot.bin", "dc/flash.bin" }), "flash.bin by the archive's name");
            Assert.That(_trash.List().Single().OriginalPath, Does.EndWith("Dreamcast BIOS.zip"));
        }

        [Test]
        public async Task ASwitchFirmwareZip_GoesWholeIntoSwitchFirmware()
        {
            var zip = Path.Combine(_bios, "Firmware 19.0.1.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                for (var i = 0; i < 12; i++)
                    using (var w = new StreamWriter(archive.CreateEntry($"{i:x32}.nca").Open())) w.Write(i);

            await _sorter.RunOnceAsync();

            Assert.That(Directory.EnumerateFiles(Path.Combine(_bios, "switch", "firmware", "Firmware 19.0.1")).Count(), Is.EqualTo(12));
            Assert.That(File.Exists(zip), Is.False);
        }
    }
}
