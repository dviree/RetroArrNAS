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
            var config = new ConfigurationService(Directory.CreateDirectory(Path.Combine(_root, "config")).Parent!.FullName);
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
                NscbRust = Path.Combine(_root, "nscb_rust"),
                NscbRustUrl = null,
                PartsFolder = Path.Combine(_root, "switch-parts"),
                KeysFile = keys,
                WorkRoot = Path.Combine(_root, "temp"),
                FreeSpace = _ => long.MaxValue,
            };
            _switch = Directory.CreateDirectory(_service.LibraryFolder!).FullName;
            // NSC_Builder stand-in: a merge writes the parts back to back into -o, a verify prints the verdict
            _service.Run = (args, cwd, timeout, ct) =>
            {
                _calls.Add(args);
                if (args.Contains("-d") && !args.Contains("-dmul"))
                {
                    var output = args[Array.IndexOf(args, "-o") + 1];
                    var inputs = args.Skip(2).TakeWhile(x => x != "-t").ToList();
                    File.WriteAllBytes(Path.Combine(output, _mergedName), inputs.SelectMany(File.ReadAllBytes).ToArray());
                    return Task.FromResult((0, "done"));
                }
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
            SwitchMergeService.VerifySettle = TimeSpan.FromHours(1);
            SwitchMergeService.PartSettle = TimeSpan.FromMinutes(10);
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
            Assert.That(left, Is.EqualTo(new[] { "The Legend of Zelda Tears of the Kingdom [0100F2C0115B6000][v393216].nsp" }),
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

        // A PFS0 header whose string table names these files, followed by padding
        private string Nsp(string relative, params string[] names)
        {
            var table = System.Text.Encoding.ASCII.GetBytes(string.Join("\0", names) + "\0");
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(0x30534650);
                writer.Write(names.Length);
                writer.Write(table.Length);
                writer.Write(0);
                var offset = 0;
                foreach (var name in names)
                {
                    writer.Write(0L); writer.Write(10L); writer.Write(offset); writer.Write(0);
                    offset += name.Length + 1;
                }
                writer.Write(table);
                writer.Write(new byte[64]);
            }
            var path = Path.Combine(_switch, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, stream.ToArray());
            return path;
        }

        [Test]
        public void NamesWithoutTitleId_AreReadFromTheTicketInsideTheNsp()
        {
            Nsp("Inazuma/INAZUMA ELEVEN Victory Road.nsp", "1234abcd.nca", "0100A6301214E0000000000000000004.tik", "0100A6301214E0000000000000000004.cert");
            Nsp("Inazuma/INAZUMA ELEVEN DLC 1.nsp", "0100A6301214F0010000000000000004.tik");
            Nsp("Inazuma/INAZUMA ELEVEN Update v131072.nsp", "0100A6301214E8000000000000000004.tik");
            Nsp("Inazuma/No ticket.nsp", "abcd.nca");

            var parts = SwitchMergeService.Scan(_switch).OrderBy(p => p.TitleId).ToList();
            Assert.That(parts.Select(p => (p.TitleId, p.Version)), Is.EqualTo(new[]
            {
                (0x0100A6301214E000UL, 0), (0x0100A6301214E800UL, 131072), (0x0100A6301214F001UL, 0)
            }));
            Assert.That(SwitchMergeService.Plan(parts).Single().Inputs.Count(), Is.EqualTo(3));
        }

        [Test]
        public async Task WithNscbRust_ItMergesAndVerifies_InsteadOfThePythonTool()
        {
            File.WriteAllText(_service.NscbRust, "");
            Part("Zelda/Zelda [0100F2C0115B6000][v0].nsp", 100);
            Part("Zelda/Zelda [0100F2C0115B6800][v393216].nsp", 30);

            Assert.That(await _service.RunOnceAsync(), Is.EqualTo(1));

            Assert.That(_calls.Select(c => c[0]), Is.All.EqualTo(_service.NscbRust));
            Assert.That(_calls[0], Does.Contain("-d").And.Contain("--keys").And.Contain("--temp-dir"));
            Assert.That(_calls[1], Does.Contain("-v").And.Contain("lv3").And.Contain("--text_file"));
            Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(_service.Nscb)!, "keys.txt")), Is.False, "the keys are passed, never copied");
            Assert.That(Directory.EnumerateFiles(_switch, "*.nsp", SearchOption.AllDirectories).Single(), Does.EndWith("[0100F2C0115B6000][v393216].nsp"));
        }

        [Test]
        public async Task CompressedParts_AreMergedWithNscbRust()
        {
            File.WriteAllText(_service.NscbRust, "");
            Part("Zelda/Zelda [0100F2C0115B6000][v0].nsz", 100);
            Part("Zelda/Zelda [0100F2C0115B6800][v393216].nsz", 30);
            Assert.That(await _service.RunOnceAsync(), Is.EqualTo(1));
            Assert.That(_calls[0].Count(a => a.EndsWith(".nsz")), Is.EqualTo(2));
        }

        [Test]
        public async Task WithoutNscbRust_AnXciPart_IsLeftAlone()
        {
            Part("Zelda/Zelda [0100F2C0115B6000][v0].xci", 100);
            Part("Zelda/Zelda [0100F2C0115B6800][v393216].nsp", 30);
            Assert.That(await _service.RunOnceAsync(), Is.EqualTo(0));
            Assert.That(_calls, Is.Empty);
        }

        [Test]
        public async Task LibraryCheck_VerifiesEachFileOnce_AndNamesTheBrokenOnes()
        {
            File.WriteAllText(_service.NscbRust, "");
            SwitchMergeService.VerifySettle = TimeSpan.Zero;
            var good = Part("Mario/Mario [0100000000010000][v0].nsp", 10);
            var bad = Part("Broken/Broken [0100000000020000][v0].nsz", 10);
            foreach (var f in new[] { good, bad }) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-1));
            _service.Run = (args, cwd, timeout, ct) =>
            {
                _calls.Add(args);
                return Task.FromResult((0, args[2] == bad ? "VERDICT: NSZ FILE IS CORRUPT OR MISSES FILES" : "VERDICT: NSZ FILE IS CORRECT\nVERDICT: NSP  FILE IS SAFE"));
            };

            Assert.That(await _service.VerifyLibraryAsync(TimeSpan.FromHours(1)), Is.EqualTo(2));
            Assert.That(await _service.VerifyLibraryAsync(TimeSpan.FromHours(1)), Is.Zero, "nothing changed, nothing checked again");

            var state = _service.LoadVerifyState();
            Assert.That((state[good].Ok, state[bad].Ok), Is.EqualTo((true, false)));
            var log = File.ReadAllText(Path.Combine(_root, "config", "switch-verify.log"));
            Assert.That(log, Does.Contain("BROKEN " + bad));
            Assert.That(File.Exists(bad), Is.True, "nothing is moved or deleted");
        }

        [Test]
        public void Tidy_LeavesOneFilePerGameInRoms_PartsWaitOutside()
        {
            SwitchMergeService.PartSettle = TimeSpan.Zero;
            Part("Mario Kart/Mario Kart [0100152000022000][v0].nsp", 100);
            Part("Mario Kart/Mario Kart [0100152000022800][v262144].nsp", 30);
            Part("Splatoon/Splatoon 3 [0100C2500FC20000][v0].nsp", 50);
            Part("Lonely Update [0100AAAA00000800][v65536].nsp", 5);

            _service.Tidy(_switch);

            Assert.That(Directory.EnumerateFileSystemEntries(_switch).Select(Path.GetFileName), Is.EqualTo(new[] { "Splatoon 3 [0100C2500FC20000][v0].nsp" }),
                "only single-file games in roms/switch, and no folders");
            Assert.That(Directory.EnumerateFiles(_service.PartsFolder).Count(), Is.EqualTo(3), "Mario Kart's two parts and the update without its base");
        }

        [Test]
        public async Task AMergeFromThePartsFolder_LandsInRomsAsOneFile()
        {
            File.WriteAllText(_service.NscbRust, "");
            SwitchMergeService.PartSettle = TimeSpan.Zero;
            Part("Zelda/Zelda [0100F2C0115B6000][v0].nsp", 100);
            Part("Zelda/Zelda [0100F2C0115B6800][v393216].nsp", 30);

            Assert.That(await _service.RunOnceAsync(), Is.EqualTo(1));

            Assert.That(Directory.EnumerateFileSystemEntries(_switch).Select(Path.GetFileName),
                Is.EqualTo(new[] { "The Legend of Zelda Tears of the Kingdom [0100F2C0115B6000][v393216].nsp" }));
            Assert.That(Directory.Exists(_service.PartsFolder) ? Directory.EnumerateFileSystemEntries(_service.PartsFolder) : Array.Empty<string>(), Is.Empty);
        }

        [TestCase("Tomodachi Life Living the Dream.nsp", 0x0100A6301214E000UL, 0, "Tomodachi Life Living the Dream [0100A6301214E000][v0].nsp")]
        [TestCase("INAZUMA_ELEVEN_Victory_Road_Switch_NSP_Base_Game.nsp", 0x0100B36008F90000UL, 0, "INAZUMA ELEVEN Victory Road [0100B36008F90000][v0].nsp")]
        [TestCase("Zelda [0100F2C0115B6000][v0].nsp", 0x0100F2C0115B6000UL, 0, "Zelda [0100F2C0115B6000][v0].nsp")]
        public void SingleGames_GetTheirTitleIdAndVersionInTheName(string file, ulong id, int version, string expected)
        {
            Assert.That(SwitchMergeService.NameFor(new SwitchMergeService.Part("/x/" + file, id, version, 1)), Is.EqualTo(expected));
        }

        [Test]
        public async Task AFolderWithNoGameLeft_GoesToTheTrash_GamesStay()
        {
            SwitchMergeService.PartSettle = TimeSpan.Zero;
            Part("Inazuma Eleven/Update v1245184/update.rar", 50);
            Part("Inazuma Eleven/info.nfo", 1);
            Part("Kingdom Hearts/Kingdom Hearts [010090C022978000][v0].nsp", 50);

            Assert.That(await _service.ClearLeftoversAsync(_switch), Is.EqualTo(1));

            Assert.That(Directory.Exists(Path.Combine(_switch, "Inazuma Eleven")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(_switch, "Kingdom Hearts")), Is.True);
            Assert.That(Directory.EnumerateFiles(Path.Combine(_root, "bin")).Select(Path.GetFileName), Is.EquivalentTo(new[] { "update.rar", "info.nfo" }));
        }
    }
}
