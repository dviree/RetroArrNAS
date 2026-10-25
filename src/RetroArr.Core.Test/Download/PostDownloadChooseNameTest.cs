using System.Collections.Generic;
using NUnit.Framework;
using RetroArr.Core.Download;
using RetroArr.Core.Games;
using RetroArr.Core.MetadataSource;

namespace RetroArr.Core.Test.Download
{
    [TestFixture]
    public class PostDownloadChooseNameTest
    {
        private static List<Game> Igdb(params string[] titles) => new(System.Array.ConvertAll(titles, t => new Game { Title = t }));
        private static AiTitleService.Guess Ai(string title) => new("wii", title, 0.9, "");

        [Test]
        public void IgdbTitleMatchingTheAiTitle_IsVerifiedAndWins()
        {
            var (name, source) = PostDownloadProcessor.ChooseName("Super Mario Galaxy 2 NTSC", Ai("Super Mario Galaxy 2"), "Super Mario Galaxy 2",
                Igdb("Super Mario Galaxy + Super Mario Galaxy 2", "Super Mario Galaxy 2"));
            Assert.That((name, source), Is.EqualTo(("Super Mario Galaxy 2", "IGDB, verified")));
        }

        [Test]
        public void IgdbTitleMatchingTheReleaseName_OutranksADifferentAiTitle()
        {
            var (name, _) = PostDownloadProcessor.ChooseName("Okami", Ai("Okamiden"), "Okamiden", Igdb("Okami", "Okamiden HD"));
            Assert.That(name, Is.EqualTo("Okami"));
        }

        [Test]
        public void NothingVerified_TheAiCandidateStandsIn_OverAnUnrelatedIgdbResult()
        {
            var (name, source) = PostDownloadProcessor.ChooseName("pokemon prism hack", Ai("Pokemon Prism"), "Pokemon Prism", Igdb("Pokemon Crystal"));
            Assert.That((name, source), Is.EqualTo(("Pokemon Prism", "AI candidate, not on IGDB")));
        }

        [Test]
        public void WithoutTheAiSourceRole_IgdbsFirstResultIsKept()
        {
            var (name, _) = PostDownloadProcessor.ChooseName("pokemon prism hack", null, "Pokemon Prism", Igdb("Pokemon Crystal"));
            Assert.That(name, Is.EqualTo("Pokemon Crystal"));
        }

        [Test]
        public void NoResultsAndNoAi_GivesNoName()
        {
            Assert.That(PostDownloadProcessor.ChooseName("Mystery", null, null, Igdb()).Name, Is.Null);
        }
    }
}
