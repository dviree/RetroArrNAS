using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroArr.Core.Data;
using RetroArr.Core.Download;
using RetroArr.Core.Download.History;
using RetroArr.Core.Download.TrackedDownloads;
using RetroArr.Core.MetadataSource;
using Microsoft.EntityFrameworkCore;

namespace RetroArr.Core.Test.MetadataSource
{
    [TestFixture]
    public class AiTitleServiceTest
    {
        // Answers every request with the model text it is given, wrapped as the Responses API does
        private sealed class FakeOpenAi : HttpMessageHandler
        {
            public string Text { get; set; } = "";
            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
            public List<string> Bodies { get; } = new();
            public string? Auth { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
                Auth = request.Headers.Authorization?.ToString();
                var body = JsonSerializer.Serialize(new
                {
                    output = new object[] { new { type = "message", content = new object[] { new { type = "output_text", text = Text } } } }
                });
                return new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
        }

        private sealed class DbFactory : IDbContextFactory<RetroArrDbContext>
        {
            private readonly DbContextOptions<RetroArrDbContext> _options;
            public DbFactory(DbContextOptions<RetroArrDbContext> options) => _options = options;
            public RetroArrDbContext CreateDbContext() => new RetroArrDbContext(_options);
        }

        private static (AiTitleService Service, FakeOpenAi Fake) Create(string text, string? key = "sk-test")
        {
            var fake = new FakeOpenAi { Text = text };
            return (new AiTitleService(new HttpClient(fake), key, "test-model", "http://openai.test/v1", 0.70), fake);
        }

        [Test]
        public async Task ConfidentAnswer_GivesPlatformAndTitle_AndSendsTheModelAndKey()
        {
            var (ai, fake) = Create("```json\n{\"platform\": \"wii\", \"title\": \"Super Mario Galaxy 2\", \"confidence\": 0.95, \"reason\": \"wbfs\"}\n```");

            var guess = await ai.IdentifyAsync("[Nintendo Wii] Super Mario Galaxy 2 [NTSC]", new[] { "game.wbfs" });

            Assert.That(guess, Is.Not.Null);
            Assert.That(guess!.Platform, Is.EqualTo("wii"));
            Assert.That(guess.Title, Is.EqualTo("Super Mario Galaxy 2"));
            Assert.That(fake.Bodies[0], Does.Contain("\"model\":\"test-model\""));
            Assert.That(fake.Bodies[0], Does.Contain("game.wbfs"));
            Assert.That(fake.Auth, Is.EqualTo("Bearer sk-test"));
        }

        [TestCase("steam")]
        [TestCase("gog")]
        [TestCase("epic")]
        public async Task AGameFiledUnderAStore_IsAPcGame(string store)
        {
            var (ai, fake) = Create($"{{\"platform\": \"{store}\", \"title\": \"Castlevania Belmont's Curse\", \"confidence\": 0.95}}");
            var guess = await ai.IdentifyAsync("Castlevania Belmonts Curse", new[] { "Castlevania.exe" });
            Assert.That(guess!.Platform, Is.EqualTo("pc"), "the PC folder");
            Assert.That(fake.Bodies[0], Does.Not.Contain("steam: Steam"), "stores are not offered as platforms");
        }

        [TestCase("{\"platform\": \"wii\", \"title\": \"Something\", \"confidence\": 0.4}")]
        [TestCase("{\"platform\": \"unknown\", \"title\": \"Something\", \"confidence\": 0.9}")]
        [TestCase("{\"platform\": \"toaster\", \"title\": \"Something\", \"confidence\": 0.9}")]
        [TestCase("{\"platform\": \"wii\", \"title\": \"\", \"confidence\": 0.9}")]
        [TestCase("not json at all")]
        public async Task UnsureOrUnusableAnswer_IsNoGuess(string text)
        {
            var (ai, _) = Create(text);
            Assert.That(await ai.IdentifyAsync("Release", new[] { "a.bin" }), Is.Null);
        }

        [Test]
        public async Task SameRelease_IsAskedOnce()
        {
            var (ai, fake) = Create("{\"platform\": \"gba\", \"title\": \"Advance Wars\", \"confidence\": 0.9}");
            await ai.IdentifyAsync("Advance Wars.gba", new[] { "Advance Wars.gba" });
            await ai.IdentifyAsync("Advance Wars.gba", new[] { "Advance Wars.gba" });
            Assert.That(fake.Bodies, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task FailedCall_IsNoGuess_AndIsAskedAgainLater()
        {
            var (ai, fake) = Create("{\"platform\": \"gba\", \"title\": \"Advance Wars\", \"confidence\": 0.9}");
            fake.Status = HttpStatusCode.TooManyRequests;
            Assert.That(await ai.IdentifyAsync("Advance Wars", new[] { "a.gba" }), Is.Null);
            fake.Status = HttpStatusCode.OK;
            Assert.That(await ai.IdentifyAsync("Advance Wars", new[] { "a.gba" }), Is.Not.Null);
        }

        [Test]
        public async Task WithoutAKey_ItIsOff_AndCallsNothing()
        {
            var (ai, fake) = Create("{}", key: "");
            Assert.That(ai.IsEnabled, Is.False);
            Assert.That(await ai.IdentifyAsync("Game", new[] { "a.iso" }), Is.Null);
            Assert.That(fake.Bodies, Is.Empty);
        }

        [Test]
        public async Task UnmappedDownload_GetsItsPlatformFromTheModel()
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retroarr_ai_" + Path.GetRandomFileName())).FullName;
            try
            {
                var release = Directory.CreateDirectory(Path.Combine(root, "Mystery Release")).FullName;
                File.WriteAllText(Path.Combine(release, "game.iso"), "x");
                var (ai, _) = Create("{\"platform\": \"ps2\", \"title\": \"Okami\", \"confidence\": 0.9}");
                var options = new DbContextOptionsBuilder<RetroArrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
                var factory = new DbFactory(options);
                var tracked = new TrackedDownloadService(root);
                var service = new CompletedDownloadService(null!, new DownloadPlatformTracker(root), tracked,
                    new DownloadHistoryRepository(factory), new DownloadBlacklistRepository(factory), null!,
                    NullLogger<CompletedDownloadService>.Instance, ai);
                var download = tracked.TrackDownload(new DownloadStatus { Id = "1", Name = "Mystery Release", DownloadPath = release, State = DownloadState.Completed }, 1, "inbox");

                await service.CheckAsync(download, new DownloadClient { Id = 1, Implementation = WatchFolderClient.ImplementationName });

                Assert.That(download.PlatformFolder, Is.EqualTo("ps2"));
                Assert.That(download.State, Is.EqualTo(TrackedDownloadState.ImportPending));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
