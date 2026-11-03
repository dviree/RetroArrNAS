using NUnit.Framework;
using RetroArr.Core.Download;

namespace RetroArr.Core.Test
{
    /// <summary>
    /// The tests write tiny stand-in PKGs for whole games, so the rule that a small PS5 PKG is an update or a DLC is
    /// off for them; the tests of that rule turn it on.
    /// </summary>
    [SetUpFixture]
    public class TestDefaults
    {
        [OneTimeSetUp]
        public void SmallPkgsAreGamesInTests() => PlayStationImporter.SmallPkg = 0;
    }
}
