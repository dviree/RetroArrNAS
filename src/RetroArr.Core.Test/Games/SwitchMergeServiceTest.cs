using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroArr.Core.Configuration;
using RetroArr.Core.Games;

namespace RetroArr.Core.Test.Games
{
    [TestFixture]
    public class SwitchMergeServiceTest
    {
        private string _root = null!;
        private string _switch = null!;
        private SwitchMergeService _service = null!;
        private readonly List<string[]> _calls = new();
        private string _mergedName = "The Legend of Zelda: Tears of the Kingdom [0100F2C0115B6000][v393216] (1G+1U+1D).nsp";
        private string _verdict = "VERDICT: NSP FILE IS CORRECT\nVERDICT: NSP FILE IS SAFE";

        [SetUp]
        public void SetUp()
        {
            _verdict = "VERDICT: NSP FILE IS CORRECT\nVERDICT: NSP FILE IS SAFE";
            _mergedName = "The Legend of Zelda: Tears of the Kingdom [0100F2C0115B6000][v393216] (1G+1U+1D).nsp";
            _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retroarr_switchmerge_" + Path.GetRandomFileName())).FullName;
            var config = new ConfigurationService(Path.Combine(_root, "config"));
            var media = config.LoadMediaSettings();
            media.FolderPath = Path.Combine(_root, "roms");
            media.DestinationPath = Path.Combine(_root, "roms");
            media.TrashPath = Path.Combine(_root, "bin");
            config.SaveMediaSettings(media);
            var nscb = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "nscb")).FullName, "squirrel.py");
            File.WriteAllText(nscb, "");
            var keys = Path.Combine(_root, "prod.keys");
            File.WriteAllText(keys, "secret");
            _calls.Clear();
            _service = new SwitchMergeService(config, new TrashService(config))
            {
                Nscb = nscb,
                KeysFile = keys,
                WorkRoot = Path.Combine(_root, "temp"),
                FreeSpace = _ => long.MaxValue,
            };
            _switch = Directory.CreateDirectory(_service.LibraryFolder!).FullName;
            // NSC_Builder stand-in: a merge writes the parts back to back into -o, a verify prints the verdict
            _service.Run = (args, cwd, timeout, ct) =>
            {
                _calls.Add(args);
                if (args.Contains("-dmul"))
                {
                    var output = args[Array.IndexOf(args, "-o") + 1];
                    var inputs = File.ReadAllLines(args[Array.IndexOf(args, "-tfile") + 1]);
                    File.WriteAllBytes(Path.Combine(output, _mergedName), inputs.SelectMany(File.ReadAllBytes).ToArray());
                    return Task.FromResult((0, "done"));
                }
                return Task.FromResult((0, _verdict));
            };
        }

        [TearDown]
        public void TearDown()
        {
            _service.Dispose();
            Directory.Delete(_root, true);
        }

        private string Part(string relative, int bytes = 10)
        {
            var path = Path.Combine(_switch, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        [Test]
        public async Task BaseUpdateAndDlc_BecomeOneVerifiedNsp_AndThePartsGoToTheTrash()
        {
            Part("Zelda/Zelda [0100F2C0115B6000][v0].nsp", 100);
            Part("Zelda/Patches/Zelda [0100F2C0115B6800][v393216].nsp", 30);
            Part("Zelda/Patches/Zelda [0100F2C0115B6800][v131072].nsp", 20);
            Part("Zelda/DLC/Zelda Pack [0100F2C0115B7001][v0].nsp", 5);

            Assert.That(await _service.RunOnceAsync(), Is.EqualTo(1));

            var left = Directory.EnumerateFiles(_switch, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_switch, f)).ToList();
            Assert.That(left, Is.EqualTo(new[] { Path.Combine("Zelda", "The Legend of Zelda Tears of the Kingdom [0100F2C0115B6000][v393216].nsp") }),
                "the official title NSC_Builder read from the game");
            Assert.That(new FileInfo(Path.Combine(_switch, left[0])).Length, Is.EqualTo(135), "base + newest update + DLC");
            Assert.That(Directory.EnumerateFiles(Path.Combine(_root, "bin"), "*.nsp", SearchOption.AllDirectories).Count(), Is.EqualTo(4), "every part, the old update too");
            Assert.That(_calls.Single(c => c.Contains("-v")), Does.Contain("lv3"));
            Assert.That(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "temp")), Is.Empty);
            Assert.That(await _service.RunOnceAsync(), Is.EqualTo(0), "a merged game alone has nothing left to merge");
        }

        [Test]
        public async Task FailedVerification_ChangesNothing_AndIsNotRetriedAtOnce()
        {
            Part("Game [0100000000010000][v0].nsp", 50);
            Part("Game [0100000000010800][v65536].nsp", 10);
            _verdict = "VERDICT: NSP FILE IS CORRECT\nVERDICT: NSP FILE IS CORRUPT";

            Assert.That(await _service.RunOnceAsync(), Is.EqualTo(0));
            Assert.That(Directory.GetFiles(_switch).Select(Path.GetFileName), Is.EquivalentTo(new[]
            {
                "Game [0100000000010000][v0].nsp", "Game [0100000000010800][v65536].nsp"
            }));
            Assert.That(Directory.Exists(Path.Combine(_root, "bin")) ? Directory.EnumerateFiles(Path.Combine(_root, "bin"), "*", SearchOption.AllDirectories) : Enumerable.Empty<string>(), Is.Empty);

            _calls.Clear();
            await _service.RunOnceAsync();
            Assert.That(_calls, Is.Empty, "the same files are tried again only after a day");
        }

        [Test]
        public async Task BaseAlone_OrUpdateWithoutBase_IsLeftAlone()
        {
            Part("Solo [0100000000020000][v0].nsp");
            Part("Orphan [0100000000030800][v65536].nsp");
            Assert.That(await _service.RunOnceAsync(), Is.EqualTo(0));
            Assert.That(_calls, Is.Empty);
        }

        [Test]
        public async Task NotEnoughRoom_MergesNothing()
        {
            Part("Game [0100000000010000][v0].nsp", 50);
            Part("Game [0100000000010800][v65536].nsp", 10);
            _service.FreeSpace = _ => 1;
            Assert.That(await _service.RunOnceAsync(), Is.EqualTo(0));
            Assert.That(_calls, Is.Empty);
        }

        [Test]
        public void WithoutKeys_TheServiceIsOff()
        {
            File.Delete(_service.KeysFile);
            Assert.That(_service.IsEnabled, Is.False);
        }

        [TestCase(0x0100F2C0115B6000UL, 0x0100F2C0115B6000UL)]
        [TestCase(0x0100F2C0115B6800UL, 0x0100F2C0115B6000UL)]
        [TestCase(0x0100F2C0115B7001UL, 0x0100F2C0115B6000UL)]
        [TestCase(0x0100F2C0115B7023UL, 0x0100F2C0115B6000UL)]
        public void TitleIds_BelongToTheirBase(ulong id, ulong baseId)
        {
            Assert.That(new SwitchMergeService.Part("x", id, 0, 1).BaseId, Is.EqualTo(baseId));
        }

        [Test]
        public async Task WithoutATitleFromTheGame_TheBaseFileNameIsUsed()
        {
            _mergedName = "0100000000010000 [0100000000010000][v65536] (1G+1U).nsp";
            Part("Mario Kart 8 Deluxe [0100000000010000][v0].nsp", 50);
            Part("Mario Kart 8 Deluxe [0100000000010800][v65536].nsp", 10);
            Assert.That(await _service.RunOnceAsync(), Is.EqualTo(1), _service.LastError);
            Assert.That(Directory.GetFiles(_switch).Select(Path.GetFileName), Is.EqualTo(new[] { "Mario Kart 8 Deluxe [0100000000010000][v65536].nsp" }));
        }

        [TestCase("VERDICT: NSP FILE IS CORRECT\nVERDICT: NSP FILE IS SAFE", null)]
        [TestCase("nothing", "no verdict")]
        [TestCase("VERDICT: NSP FILE IS CORRECT\nVERDICT: NSP FILE COULD'VE BEEN TAMPERED WITH", "VERDICT: NSP FILE COULD'VE BEEN TAMPERED WITH")]
        public void Verdicts(string log, string? problem)
        {
            Assert.That(SwitchMergeService.Verdict(log), Is.EqualTo(problem));
        }
    }
}
