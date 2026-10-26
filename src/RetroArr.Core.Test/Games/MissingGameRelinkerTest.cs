using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using RetroArr.Core.Configuration;
using RetroArr.Core.Data;
using RetroArr.Core.Games;

namespace RetroArr.Core.Test.Games
{
    [TestFixture]
    public class MissingGameRelinkerTest
    {
        private string _root = null!;
        private string _roms = null!;
        private DbContextOptions<RetroArrDbContext> _db = null!;
        private SqliteGameRepository _repo = null!;
        private MissingGameRelinker _relinker = null!;

        private sealed class Factory : IDbContextFactory<RetroArrDbContext>
        {
            private readonly DbContextOptions<RetroArrDbContext> _o;
            public Factory(DbContextOptions<RetroArrDbContext> o) => _o = o;
            public RetroArrDbContext CreateDbContext() => new(_o);
        }

        [SetUp]
        public void SetUp()
        {
            _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retroarr_relink_" + Path.GetRandomFileName())).FullName;
            _roms = Directory.CreateDirectory(Path.Combine(_root, "roms")).FullName;
            var config = new ConfigurationService(Directory.CreateDirectory(Path.Combine(_root, "config")).Parent!.FullName);
            var media = config.LoadMediaSettings();
            media.FolderPath = media.DestinationPath = _roms;
            config.SaveMediaSettings(media);
            _db = new DbContextOptionsBuilder<RetroArrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            _repo = new SqliteGameRepository(new Factory(_db));
            _relinker = new MissingGameRelinker(config, _repo);
        }

        [TearDown]
        public void TearDown()
        {
            _relinker.Dispose();
            Directory.Delete(_root, true);
        }

        private async Task<int> Missing(string title, string path, int platformId = 24)
        {
            using var ctx = new RetroArrDbContext(_db);
            var g = new Game { Title = title, PlatformId = platformId, Path = path, ExecutablePath = path, Status = GameStatus.Missing, MissingSince = DateTime.UtcNow.AddHours(-1) };
            ctx.Games.Add(g);
            await ctx.SaveChangesAsync();
            return g.Id;
        }

        private string Entry(string relative, bool folder = false)
        {
            var path = Path.Combine(_roms, relative);
            if (folder) Directory.CreateDirectory(path);
            else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "x"); }
            return path;
        }

        [Test]
        public async Task RenamedPsGame_IsFoundByItsTitleId()
        {
            var now = Entry("ps5/Ghost of Yotei [PPSA26344] [v2.2]", folder: true);
            var id = await Missing("Ghost of Yotei", Path.Combine(_roms, "ps5", "PPSA26344-app0"));

            Assert.That(await _relinker.RunOnceAsync(), Is.EqualTo(1));
            var game = await _repo.GetByIdAsync(id);
            Assert.That(game!.ExecutablePath, Is.EqualTo(now));
            Assert.That(game.MissingSince, Is.Null);
        }

        [Test]
        public async Task PkgMovedIntoThePkgFolder_IsFound()
        {
            var now = Entry("ps5/pkg/Onimusha [PPSA27836].pkg");
            Entry("ps5/Onimusha [PPSA27836]-lz4", folder: true);
            var id = await Missing("Onimusha", Path.Combine(_roms, "ps5", "Onimusha [PPSA27836].pkg"));

            Assert.That(await _relinker.RunOnceAsync(), Is.EqualTo(1));
            Assert.That((await _repo.GetByIdAsync(id))!.ExecutablePath, Is.EqualTo(now), "the same kind of entry wins");
        }

        [Test]
        public async Task MergedSwitchGame_IsFoundByItsBaseId()
        {
            var now = Entry("switch/Zelda/The Legend of Zelda [0100F2C0115B6000][v393216].nsp");
            var id = await Missing("Zelda TotK", Path.Combine(_roms, "switch", "Zelda", "Zelda [0100F2C0115B6000][v0].nsp"), 23);

            Assert.That(await _relinker.RunOnceAsync(), Is.EqualTo(1));
            Assert.That((await _repo.GetByIdAsync(id))!.ExecutablePath, Is.EqualTo(now));
        }

        [Test]
        public async Task GameMovedToAnotherPlatformFolder_IsFoundByTitle_AndGetsThatPlatform()
        {
            var now = Entry("pc/Castlevania Belmonts Curse", folder: true);
            var id = await Missing("Castlevania Belmont's Curse", Path.Combine(_roms, "steam", "Castlevania Belmonts Curse"), 125);

            Assert.That(await _relinker.RunOnceAsync(), Is.EqualTo(1));
            var game = await _repo.GetByIdAsync(id);
            Assert.That(game!.Path, Is.EqualTo(now));
            Assert.That(game.PlatformId, Is.Not.EqualTo(125));
        }

        [Test]
        public async Task TwoCandidates_OrNone_LeaveTheGameAsItIs()
        {
            Entry("ps5/A [PPSA11111] [v1.0]", folder: true);
            Entry("ps5/B [PPSA11111] [v2.0]", folder: true);
            var two = await Missing("A", Path.Combine(_roms, "ps5", "PPSA11111"));
            var none = await Missing("Gone", Path.Combine(_roms, "ps5", "PPSA99999"));

            Assert.That(await _relinker.RunOnceAsync(), Is.EqualTo(0));
            Assert.That((await _repo.GetByIdAsync(two))!.MissingSince, Is.Not.Null);
            Assert.That((await _repo.GetByIdAsync(none))!.MissingSince, Is.Not.Null);
        }
    }
}
