using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RetroArr.Core.IO;

namespace RetroArr.Core.Test.IO
{
    /// <summary>
    /// Real locked archives from Fixtures/Archives, each holding
    /// "[SITE.COM]-PPSA00001-GRP/PPSA00001-app0/{eboot.bin (6000 bytes), sce_sys/param.json}":
    ///  - PPSA00001: RAR5, three volumes, headers encrypted, password "[SITE.COM]" (the tag with its brackets)
    ///  - PPSA00002: RAR5, only the data encrypted, password "SITE"
    ///  - PPSA00003: 7z, headers encrypted, password "site.com"
    ///  - PPSA00004: RAR5, headers encrypted, a password no name holds
    ///  - PPSA00005: RAR5, solid, only the data encrypted, password "SITE.COM" (one more file in it)
    ///  - PPSA00006: RAR5, solid, not locked
    /// </summary>
    [TestFixture]
    public class LockedArchiveTest
    {
        private string _root = null!;
        private string? _savedList;

        [SetUp]
        public void SetUp()
        {
            _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retroarr_locked_" + Path.GetRandomFileName())).FullName;
            _savedList = ArchiveService.PasswordFile;
            ArchiveService.PasswordFile = Path.Combine(_root, "archive-passwords.txt");
        }

        [TearDown]
        public void TearDown()
        {
            ArchiveService.PasswordFile = _savedList;
            Directory.Delete(_root, true);
        }

        // The release in its own folder, as a download client leaves it
        private string Release(string id)
        {
            var name = $"[SITE.COM]-{id}-GRP";
            var folder = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
            var fixtures = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Archives");
            foreach (var file in Directory.GetFiles(fixtures, name + ".*"))
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            return Directory.GetFiles(folder).OrderBy(f => f, System.StringComparer.Ordinal).First();
        }

        private static void AssertTheGameCameOut(string into)
        {
            var eboot = Directory.GetFiles(into, "eboot.bin", SearchOption.AllDirectories).Single();
            Assert.That(new FileInfo(eboot).Length, Is.EqualTo(6000));
            var param = Directory.GetFiles(into, "param.json", SearchOption.AllDirectories).Single();
            Assert.That(File.ReadAllText(param), Does.Contain("PPSA00001"));
        }

        [TestCase("PPSA00001", 3, TestName = "Rar5_ThreeVolumes_EncryptedHeaders_TheTagWithItsBrackets")]
        [TestCase("PPSA00002", 1, TestName = "Rar5_OnlyTheDataEncrypted_TheSiteName")]
        [TestCase("PPSA00003", 1, TestName = "SevenZip_EncryptedHeaders_TheDomain")]
        [TestCase("PPSA00005", 1, TestName = "Rar5_Solid_OnlyTheDataEncrypted_TheTagWithoutBrackets")]
        [TestCase("PPSA00006", 1, TestName = "Rar5_Solid_NotLocked")]
        public void ALockedArchive_IsUnpacked_WithThePasswordItsNameHolds(string id, int parts)
        {
            var first = Release(id);
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(first)!), Has.Length.EqualTo(parts));
            var into = Path.Combine(_root, "out");
            var volumes = new List<string>();

            Assert.That(new ArchiveService().Extract(first, into, volumes), Is.True);

            AssertTheGameCameOut(into);
            if (parts > 1) Assert.That(volumes, Has.Count.EqualTo(parts), "every volume was read");
        }

        [Test]
        public void NoPasswordFits_NothingIsUnpacked_UntilItIsOnTheOwnersList()
        {
            var first = Release("PPSA00004");
            var into = Path.Combine(_root, "out");

            Assert.That(new ArchiveService().Extract(first, into), Is.False);
            Assert.That(Directory.Exists(into) ? Directory.GetFiles(into, "*", SearchOption.AllDirectories) : new string[0], Is.Empty);

            File.WriteAllLines(ArchiveService.PasswordFile!, new[] { "wrong one", "not-in-any-name" });
            Assert.That(new ArchiveService().Extract(first, into), Is.True);
            AssertTheGameCameOut(into);
        }

        [Test]
        public void AWrongPassword_IsToldFromTheRightOne()
        {
            // Wrong ones come first, from the owner's list
            File.WriteAllLines(ArchiveService.PasswordFile!, new[] { "wrong one", "[site.com]", "SITE.COM " });
            Assert.That(ArchiveService.FindPassword(Release("PPSA00001")), Is.EqualTo("[SITE.COM]"));
            Assert.That(ArchiveService.FindPassword(Release("PPSA00002")), Is.EqualTo("SITE"));
            Assert.That(ArchiveService.FindPassword(Release("PPSA00003")), Is.EqualTo("site.com"));
            Assert.That(ArchiveService.FindPassword(Release("PPSA00005")), Is.EqualTo("SITE.COM"));
            Assert.That(ArchiveService.FindPassword(Release("PPSA00006")), Is.Null, "not locked: no password");
        }
    }
}
