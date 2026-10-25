using System.IO;
using NUnit.Framework;
using RetroArr.Core.Games;

namespace RetroArr.Core.Test.Games
{
    [TestFixture]
    public class PlayStationPackageInspectorTest
    {
        private string _root = null!;

        [SetUp]
        public void SetUp() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retroarr_ps_" + Path.GetRandomFileName())).FullName;

        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        private string Game(string folder, bool lz4 = false)
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, folder)).FullName;
            File.WriteAllText(Path.Combine(dir, "eboot.bin"), "x");
            Directory.CreateDirectory(Path.Combine(dir, "sce_sys"));
            File.WriteAllText(Path.Combine(dir, "sce_sys", "param.json"),
                "{\"titleId\":\"PPSA01903\",\"contentVersion\":\"01.010.000\",\"localizedParameters\":{\"defaultLanguage\":\"en-US\",\"en-US\":{\"titleName\":\"FINAL FANTASY VII REMAKE\"}}}");
            if (lz4) File.WriteAllText(Path.Combine(dir, "ampr_assets_0001.bin"), "z");
            return dir;
        }

        [Test]
        public void UncompressedFolder_WithItsParamJson()
        {
            var p = PlayStationPackageInspector.Inspect(Game("FF7R"));
            Assert.That(p.Format, Is.EqualTo(PlayStationFormat.Folder));
            Assert.That((p.TitleId, p.Version, p.Title), Is.EqualTo(("PPSA01903", "01.010.000", "FINAL FANTASY VII REMAKE")));
            Assert.That(p.IsCompressed, Is.False);
        }

        [Test]
        public void App0FolderInside_IsTheGameRoot()
        {
            var release = Directory.CreateDirectory(Path.Combine(_root, "Release")).FullName;
            var app0 = Game(Path.Combine("Release", "PPSA01903-app0"));
            var p = PlayStationPackageInspector.Inspect(release);
            Assert.That(p.Format, Is.EqualTo(PlayStationFormat.Folder));
            Assert.That(p.GameFolder, Is.EqualTo(app0));
        }

        [TestCase(true, "Game")]
        [TestCase(false, "Game-LZ4")]
        public void Lz4Markers_MakeItCompressed(bool marker, string folder)
        {
            var p = PlayStationPackageInspector.Inspect(Game(folder, lz4: marker));
            Assert.That(p.Format, Is.EqualTo(PlayStationFormat.Lz4Folder));
            Assert.That(p.IsCompressed, Is.True);
            Assert.That(p.TitleId, Is.EqualTo("PPSA01903"));
        }

        [TestCase("FINAL FANTASY VII REMAKE [PPSA01903] [v1.5].exfat", PlayStationFormat.ExfatImage)]
        [TestCase("game.ffpfsc", PlayStationFormat.FfpfscImage)]
        [TestCase("MOUSE [CUSA12345].pkg", PlayStationFormat.Pkg)]
        [TestCase("sackboy.part01.rar", PlayStationFormat.Archive)]
        [TestCase("sackboy.r00", PlayStationFormat.Archive)]
        public void Files_ByTheirExtension(string name, PlayStationFormat format)
        {
            var file = Path.Combine(_root, name);
            File.WriteAllText(file, "x");
            Assert.That(PlayStationPackageInspector.Inspect(file).Format, Is.EqualTo(format));
        }

        [Test]
        public void FolderHoldingAnImage_IsThatImage_WithTheIdFromItsName()
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, "FF7R [PPSA01903]")).FullName;
            File.WriteAllText(Path.Combine(dir, "game.exfat"), "x");
            var p = PlayStationPackageInspector.Inspect(dir);
            Assert.That((p.Format, p.TitleId), Is.EqualTo((PlayStationFormat.ExfatImage, "PPSA01903")));
        }

        [TestCase("v02.002.002", "01.010.000", 1)]
        [TestCase("01.010.000", "v1.10", 0)]
        [TestCase("v1.5", "1.10", -1)]
        [TestCase("1.0", "1.0.1", -1)]
        public void Versions_CompareByTheirNumbers(string a, string b, int sign)
        {
            Assert.That(System.Math.Sign(PlayStationPackageInspector.CompareVersions(a, b)), Is.EqualTo(sign));
        }
    }
}
