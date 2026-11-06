using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroArr.Core.Configuration;
using RetroArr.Core.Games;
using RetroArr.Core.IO;

namespace RetroArr.Core.Test.Games
{
    [TestFixture]
    public class Ps4PkgMergeServiceTest
    {
        private string _root = null!, _library = null!, _trash = null!;
        private Ps4PkgMergeService _service = null!;
        private readonly List<string> _calls = new();
        private const string ContentId = "UP0000-CUSA00001_00-RETROARRTEST0000";

        private static byte[] Package(string category, string version, string id = ContentId) =>
            PlayStationReadersTest.Pkg(id, PlayStationReadersTest.Sfo(
                ("CATEGORY", category), ("TITLE_ID", "CUSA00001"), ("TITLE", "Test Game"), ("APP_VER", version)));

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "retroarr_ps4merge_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "config"));
            var config = new ConfigurationService(_root);
            var media = config.LoadMediaSettings();
            media.DestinationPath = media.FolderPath = Path.Combine(_root, "roms");
            media.TrashPath = _trash = Path.Combine(_root, "bin");
            config.SaveMediaSettings(media);
            _library = Directory.CreateDirectory(Path.Combine(media.DestinationPath, "ps4", "Test Game [CUSA00001]")).FullName;
            var tool = Path.Combine(_root, "tool");
            File.WriteAllText(tool, "stand-in");
            _service = new Ps4PkgMergeService(config, new TrashService(config), new FileMoverService())
            {
                Tool = tool, WorkRoot = Path.Combine(_root, "work"), Settle = TimeSpan.Zero,
                FreeSpace = _ => long.MaxValue,
            };
            _calls.Clear();
            _service.Run = (args, _, _) =>
            {
                _calls.Add(args[1]);
                if (args[1] == "merge")
                {
                    var output = args[Array.IndexOf(args, "--out") + 1];
                    File.WriteAllBytes(output, Package("gd", "01.02"));
                    Assert.That(args, Does.Contain("--validate"));
                }
                else
                {
                    Assert.That(File.Exists(Path.Combine(_library, "base.pkg")), Is.True, "source remains until final validation");
                    Assert.That(File.Exists(Path.Combine(_library, "update.pkg")), Is.True);
                }
                return Task.FromResult((0, "verified"));
            };
        }

        [TearDown]
        public void TearDown()
        {
            _service.Dispose();
            Directory.Delete(_root, true);
        }

        private string Part(string file, string category, string version, string id = ContentId)
        {
            var path = Path.Combine(_library, file);
            File.WriteAllBytes(path, Package(category, version, id));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));
            return path;
        }

        private void Pair()
        {
            Part("base.pkg", "gd", "01.00");
            Part("update.pkg", "gp", "01.02");
        }

        [Test]
        public async Task LatestUpdate_IsBuiltIntoOneGame_OnlyAfterVerification_AreOriginalsTrashed()
        {
            Pair();
            Part("old-update.pkg", "gp", "01.01");
            var dlc = Part("dlc.pkg", "ac", "01.00");

            Assert.That(await _service.RunOnceAsync(), Is.EqualTo(1), _service.LastError);

            var merged = Path.Combine(_library, "Test Game [CUSA00001] [v1.2].pkg");
            Assert.That(File.Exists(merged), Is.True);
            Assert.That(Ps4PkgMergeService.ReadPart(merged)?.Version, Is.EqualTo("01.02"));
            Assert.That(File.Exists(dlc), Is.True, "DLC is separate");
            Assert.That(Directory.GetFiles(_library).Select(Path.GetFileName), Is.EquivalentTo(new[] { Path.GetFileName(merged), "dlc.pkg" }));
            Assert.That(Directory.GetFiles(_trash, "*.pkg").Select(Path.GetFileName), Is.EquivalentTo(new[] { "base.pkg", "update.pkg", "old-update.pkg" }));
            Assert.That(_calls, Is.EqualTo(new[] { "merge", "validate" }));
            Assert.That(await _service.RunOnceAsync(), Is.Zero, "the finished merge is not merged again");
        }

        [TestCase("merge")]
        [TestCase("validate")]
        public async Task FailedBuildOrValidation_KeepsOriginals_AndPublishesNothing(string failAt)
        {
            Pair();
            var run = _service.Run;
            _service.Run = (args, cwd, ct) => args[1] == failAt ? Task.FromResult((1, "bad package")) : run(args, cwd, ct);

            Assert.That(await _service.RunOnceAsync(), Is.Zero);
            Assert.That(Directory.GetFiles(_library).Select(Path.GetFileName), Is.EquivalentTo(new[] { "base.pkg", "update.pkg" }));
            Assert.That(Directory.Exists(_trash), Is.False);
            Assert.That(Directory.GetDirectories(_service.WorkRoot), Is.Empty);
        }

        [Test]
        public async Task ChangedInput_IsNeverRetiredOrReplaced()
        {
            Pair();
            var run = _service.Run;
            _service.Run = async (args, cwd, ct) =>
            {
                var result = await run(args, cwd, ct);
                if (args[1] == "merge") File.AppendAllText(Path.Combine(_library, "base.pkg"), "new data");
                return result;
            };
            Assert.That(await _service.RunOnceAsync(), Is.Zero);
            Assert.That(_service.LastError, Does.Contain("input changed"));
            Assert.That(Directory.GetFiles(_library), Has.Length.EqualTo(2));
            Assert.That(Directory.Exists(_trash), Is.False);
        }

        [TestCase("gp", "01.02")]
        [TestCase("gd", "01.01")]
        [TestCase("gd", "01.03")]
        public async Task WrongOutputTypeOrVersion_IsRejectedBeforeAnySourceMove(string category, string version)
        {
            Pair();
            _service.Run = (args, _, _) =>
            {
                File.WriteAllBytes(args[Array.IndexOf(args, "--out") + 1], Package(category, version));
                return Task.FromResult((0, "done"));
            };
            Assert.That(await _service.RunOnceAsync(), Is.Zero);
            Assert.That(_service.LastError, Does.Contain("wrong type, identity or version"));
            Assert.That(Directory.GetFiles(_library), Has.Length.EqualTo(2));
            Assert.That(Directory.Exists(_trash), Is.False);
        }

        [Test]
        public async Task Cancellation_KeepsOriginals_AndReleasesTheJobLock()
        {
            Pair();
            var run = _service.Run;
            using var cancel = new CancellationTokenSource();
            _service.Run = (_, _, _) =>
            {
                cancel.Cancel();
                return Task.FromException<(int, string)>(new OperationCanceledException(cancel.Token));
            };
            Assert.ThrowsAsync<OperationCanceledException>(async () => await _service.RunOnceAsync(cancel.Token));
            Assert.That(Directory.GetFiles(_library), Has.Length.EqualTo(2));
            Assert.That(Directory.Exists(_trash), Is.False);
            _service.Run = run;
            Assert.That(await _service.RunOnceAsync(), Is.EqualTo(1), "a cancellation does not leave the gate locked");
        }

        [Test]
        public async Task DifferentContentIds_AreNotPaired_EvenWithTheSameTitleId()
        {
            Part("base.pkg", "gd", "01.00");
            Part("update.pkg", "gp", "01.02", ContentId.Replace("UP0000", "EP0000"));
            Assert.That(await _service.RunOnceAsync(), Is.Zero);
            Assert.That(_calls, Is.Empty);
        }

        [Test]
        public async Task NewerBase_OrDlcAlone_DoesNotTriggerAMerge()
        {
            Part("base.pkg", "gd", "01.03");
            Part("update.pkg", "gp", "01.02");
            Part("dlc.pkg", "ac", "01.04");
            Assert.That(await _service.RunOnceAsync(), Is.Zero);
            Assert.That(_calls, Is.Empty);
        }

        [Test]
        public async Task RecentlyWrittenPackages_WaitUntilSettled()
        {
            Pair();
            _service.Settle = TimeSpan.FromMinutes(10);
            File.SetLastWriteTimeUtc(Path.Combine(_library, "update.pkg"), DateTime.UtcNow);
            Assert.That(await _service.RunOnceAsync(), Is.Zero);
            Assert.That(_calls, Is.Empty);
        }

        [Test]
        public async Task InsufficientSpace_LeavesInputsInPlace()
        {
            Pair();
            _service.FreeSpace = _ => 0;
            Assert.That(await _service.RunOnceAsync(), Is.Zero);
            Assert.That(_calls, Is.Empty);
            Assert.That(Directory.GetFiles(_library), Has.Length.EqualTo(2));
        }

        [Test]
        public async Task ExistingOutput_IsNeverOverwritten()
        {
            Pair();
            var target = Path.Combine(_library, "Test Game [CUSA00001] [v1.2].pkg");
            File.WriteAllText(target, "existing content");
            Assert.That(await _service.RunOnceAsync(), Is.Zero);
            Assert.That(File.ReadAllText(target), Is.EqualTo("existing content"));
            Assert.That(_calls, Is.Empty);
        }

        [Test]
        public void NamesAlone_DoNotMakeAnInputPackage()
        {
            var file = Path.Combine(_library, "Test Game [CUSA00001] [Update] [v1.2].pkg");
            File.WriteAllText(file, "not a pkg");
            Assert.That(Ps4PkgMergeService.ReadPart(file), Is.Null);
        }
    }
}
