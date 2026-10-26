using System.IO;
using System.Linq;
using NUnit.Framework;
using RetroArr.Core.Games;

namespace RetroArr.Core.Test.Games
{
    [TestFixture]
    public class PlayStationCopiesTest
    {
        private string _root = null!;

        [SetUp]
        public void SetUp() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pscopies-" + Path.GetRandomFileName())).FullName;

        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        private string Make(string relative, bool folder = false)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(folder ? path : Path.GetDirectoryName(path)!);
            if (!folder) File.WriteAllText(path, "x");
            return path;
        }

        [Test]
        public void Lz4Folder_ListsThePkgAndImageOfTheSameTitle()
        {
            var game = Make("ps5/Onimusha Way of the Sword [PPSA27836]-lz4", folder: true);
            var pkg = Make("ps5/pkg/Onimusha Way of the Sword [PPSA27836].pkg");
            var update = Make("ps5/pkg/Onimusha Way of the Sword [PPSA27836] [Update] [v1.02].pkg");
            var image = Make("ps5/Onimusha Way of the Sword [PPSA27836].exfat");
            Make("ps5/pkg/Stray [PPSA02561].pkg");

            Assert.That(MediaScannerService.PlayStationCopies(game), Is.EquivalentTo(new[] { pkg, update, image }));
        }

        [Test]
        public void PkgEntry_ListsTheImageBesideIt_NotItself()
        {
            var pkg = Make("ps5/pkg/Saros [PPSA07631].pkg");
            var image = Make("ps5/Saros [PPSA07631].exfat");

            Assert.That(MediaScannerService.PlayStationCopies(pkg), Is.EqualTo(new[] { image }));
        }

        [Test]
        public void OtherPlatforms_AndNamesWithoutTitleId_ListNothing()
        {
            Make("switch/Zelda [PPSA27836].pkg");
            Assert.That(MediaScannerService.PlayStationCopies(Make("switch/Zelda", folder: true)), Is.Empty);
            Assert.That(MediaScannerService.PlayStationCopies(Make("ps5/Some Game", folder: true)), Is.Empty);
        }
    }
}
