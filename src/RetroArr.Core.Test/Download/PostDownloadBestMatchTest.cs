using System.Collections.Generic;
using NUnit.Framework;
using RetroArr.Core.Download;
using RetroArr.Core.Games;

namespace RetroArr.Core.Test.Download
{
    [TestFixture]
    public class PostDownloadBestMatchTest
    {
        private static List<Game> Results(params string[] titles) => new(System.Array.ConvertAll(titles, t => new Game { Title = t }));

        [Test]
        public void ExactTitle_WinsOverABundleRankedFirst()
        {
            var results = Results("Super Mario Galaxy + Super Mario Galaxy 2", "Super Mario Galaxy 2", "Super Mario Galaxy");
            Assert.That(PostDownloadProcessor.BestMatch(results, "Super Mario Galaxy 2").Title, Is.EqualTo("Super Mario Galaxy 2"));
        }

        [Test]
        public void PunctuationAndCase_DoNotMatter()
        {
            var results = Results("Zelda Collection", "The Legend of Zelda: Tears of the Kingdom");
            Assert.That(PostDownloadProcessor.BestMatch(results, "the legend of zelda tears of the kingdom").Title,
                Is.EqualTo("The Legend of Zelda: Tears of the Kingdom"));
        }

        [Test]
        public void NoExactTitle_KeepsTheFirstResult()
        {
            var results = Results("Graveyard Keeper", "Graveyard Keeper: Better Save Soul");
            Assert.That(PostDownloadProcessor.BestMatch(results, "Graveyard Keeper 2").Title, Is.EqualTo("Graveyard Keeper"));
        }
    }
}
