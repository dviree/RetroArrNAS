using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using RetroArr.Core.Data;
using RetroArr.Core.Games;
using RetroArr.Core.MetadataSource;

namespace RetroArr.Core.Test.Games
{
    [TestFixture]
    public class AiMetadataFixerTest
    {
        private sealed class Model : HttpMessageHandler
        {
            public string Text = "";
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { output_text = Text }), Encoding.UTF8, "application/json")
                });
        }

        private sealed class Factory : IDbContextFactory<RetroArrDbContext>
        {
            private readonly DbContextOptions<RetroArrDbContext> _o;
            public Factory(DbContextOptions<RetroArrDbContext> o) => _o = o;
            public RetroArrDbContext CreateDbContext() => new(_o);
        }

        private DbContextOptions<RetroArrDbContext> _db = null!;
        private SqliteGameRepository _repo = null!;
        private Model _model = null!;
        private AiMetadataFixer _fixer = null!;

        [SetUp]
        public void SetUp()
        {
            _db = new DbContextOptionsBuilder<RetroArrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            _repo = new SqliteGameRepository(new Factory(_db));
            _model = new Model { Text = "{\"platform\": \"ps5\", \"title\": \"Saros\", \"confidence\": 0.97}" };
            var ai = new AiTitleService(new HttpClient(_model), "sk-test", "m", "http://openai.test/v1", 0.7);
            _fixer = new AiMetadataFixer(_repo, null!, ai)
            {
                Search = (variants, platform) => Task.FromResult(new List<(int, string)> { (321, "Saros"), (999, "Saros Something Else") }),
                Details = (id, platform) => Task.FromResult<Game?>(new Game { IgdbId = id, Title = "Saros", Overview = "A shooter", Year = 2026 })
            };
        }

        [TearDown]
        public void TearDown()
        {
            _fixer.Dispose();
            _model.Dispose();
        }

        private async Task<int> Add(string title, int? igdb = null, bool confirmed = false)
        {
            using var ctx = new RetroArrDbContext(_db);
            var g = new Game { Title = title, PlatformId = 24, IgdbId = igdb, MetadataConfirmedByUser = confirmed };
            ctx.Games.Add(g);
            await ctx.SaveChangesAsync();
            return g.Id;
        }

        [Test]
        public async Task ReleaseNamedEntry_GetsTheAiTitle_ThenIgdbMetadata()
        {
            var id = await Add("SAROS.PS5-DUPLEX");

            Assert.That(await _fixer.RunOnceAsync(), Is.EqualTo(1));
            var game = await _repo.GetByIdAsync(id);
            Assert.That((game!.Title, game.IgdbId, game.Overview), Is.EqualTo(("Saros", 321, "A shooter")));
        }

        [TestCase("Ghost of Yotei", null, false)]
        [TestCase("SAROS.PS5-DUPLEX", 321, false)]
        [TestCase("SAROS.PS5-DUPLEX", null, true)]
        public async Task ProperOrMatchedOrConfirmedEntries_AreLeftAlone(string title, int? igdb, bool confirmed)
        {
            var id = await Add(title, igdb, confirmed);
            Assert.That(await _fixer.RunOnceAsync(), Is.EqualTo(0));
            Assert.That((await _repo.GetByIdAsync(id))!.Title, Is.EqualTo(title));
        }

        [Test]
        public async Task WithoutAnExactIgdbMatch_OnlyTheTitleIsCleaned()
        {
            _fixer.Search = (v, p) => Task.FromResult(new List<(int, string)> { (999, "Saros Something Else") });
            var id = await Add("SAROS.PS5-DUPLEX");

            await _fixer.RunOnceAsync();

            var game = await _repo.GetByIdAsync(id);
            Assert.That((game!.Title, game.IgdbId), Is.EqualTo(("Saros", (int?)null)));
        }

        [TestCase("SAROS.PS5-DUPLEX", true)]
        [TestCase("Stray_PPSA02561", true)]
        [TestCase("Ghost.of.Yotei.PS5-BlaZe", true)]
        [TestCase("Super Mario World (USA).sfc", false)]
        [TestCase("Castlevania - Symphony of the Night (USA) [!].bin", false)]
        [TestCase("Hollow Knight", false)]
        public void ScannerAsksTheAi_OnlyForReleaseNames(string name, bool expected) =>
            Assert.That(AiMetadataFixer.LooksLikeRelease(name), Is.EqualTo(expected));
    }
}
