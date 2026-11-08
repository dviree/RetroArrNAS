using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RetroArr.Core.IO;

namespace RetroArr.Core.Test.IO
{
    [TestFixture]
    public class ArchivePasswordTest
    {
        [Test]
        public void Passwords_AreTriedFromTheOwnersList_ThenFromWhatTheNamesHold()
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retroarr_pw_" + Path.GetRandomFileName())).FullName;
            var saved = ArchiveService.PasswordFile;
            try
            {
                ArchiveService.PasswordFile = Path.Combine(root, "archive-passwords.txt");
                File.WriteAllLines(ArchiveService.PasswordFile, new[] { "my-own", "", "  second  " });
                var rar = Path.Combine(root, "[DLPSGAME.COM]-PPSA26344-DUPLEX", "[DLPSGAME.COM]-PPSA26344-DUPLEX.part01.rar");

                var candidates = ArchiveService.PasswordCandidates(rar).ToList();

                Assert.That(candidates.Take(2), Is.EqualTo(new[] { "my-own", "second" }), "the owner's list first, as it is");
                Assert.That(candidates, Is.SupersetOf(new[] { "[DLPSGAME.COM]", "DLPSGAME.COM", "dlpsgame.com", "www.dlpsgame.com", "DLPSGAME", "dlpsgame", "[DLPSGAME.COM]-PPSA26344-DUPLEX", "PPSA26344-DUPLEX" }));
                Assert.That(candidates, Is.Unique);
                Assert.That(candidates, Has.None.Empty);
            }
            finally
            {
                ArchiveService.PasswordFile = saved;
                Directory.Delete(root, true);
            }
        }
    }
}
