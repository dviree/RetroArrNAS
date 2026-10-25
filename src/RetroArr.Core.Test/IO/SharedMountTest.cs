using NUnit.Framework;
using RetroArr.Core.IO;

namespace RetroArr.Core.Test.IO
{
    [TestFixture]
    public class SharedMountTest
    {
        private const string MountInfo =
            "20 1 0:30 / / rw - overlay overlay rw\n" +
            "41 20 253:1 /games/roms /media rw,noatime - btrfs /dev/sda1 rw\n" +
            "42 20 253:1 /downloads/torrents /downloads/qbittorrent ro,noatime - btrfs /dev/sda1 rw\n" +
            "43 20 253:1 /games/bin /trash rw,noatime - btrfs /dev/sda1 rw\n" +
            "44 20 253:1 / /nas rw,noatime - btrfs /dev/sda1 rw\n" +
            "45 20 253:7 /docker/retroarr/config /app/config rw - btrfs /dev/sdb1 rw\n" +
            "46 20 253:1 /games/My\\040Games /spaced rw - btrfs /dev/sda1 rw\n";

        [SetUp]
        public void SetUp()
        {
            SharedMount.ReadMountInfo = () => MountInfo;
            SharedMount.Root = "/nas";
            SharedMount.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            SharedMount.ReadMountInfo = () => System.IO.File.ReadAllText("/proc/self/mountinfo");
            SharedMount.Root = "/nas";
            SharedMount.Reset();
        }

        [TestCase("/media/switch/Zelda.nsp", "/nas/games/roms/switch/Zelda.nsp")]
        [TestCase("/downloads/qbittorrent/Zelda [NSP]/Zelda.nsp", "/nas/downloads/torrents/Zelda [NSP]/Zelda.nsp")]
        [TestCase("/trash/20261025-120000_abcd1234/x.nsp", "/nas/games/bin/20261025-120000_abcd1234/x.nsp")]
        [TestCase("/media", "/nas/games/roms")]
        [TestCase("/spaced/a.iso", "/nas/games/My Games/a.iso")]
        public void PathsInMountsOfTheSameDisk_AreFoundUnderTheSharedMount(string path, string expected)
        {
            Assert.That(SharedMount.Translate(path), Is.EqualTo(expected));
        }

        [TestCase("/app/config/x.json")]
        [TestCase("/tmp/x")]
        public void OtherDisks_HaveNoSharedPath(string path)
        {
            Assert.That(SharedMount.Translate(path), Is.Null);
        }

        [Test]
        public void WithoutTheSharedMount_NothingIsTranslated()
        {
            SharedMount.ReadMountInfo = () => MountInfo.Replace(" / /nas ", " / /elsewhere ");
            SharedMount.Reset();
            Assert.That(SharedMount.Translate("/media/a"), Is.Null);
        }
    }
}
