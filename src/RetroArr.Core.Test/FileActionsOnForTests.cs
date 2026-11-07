using NUnit.Framework;
using RetroArr.Core.IO;

// No namespace: runs once around every test in the assembly
[SetUpFixture]
public class FileActionsOnForTests
{
    // RetroArr only plans file actions unless FILE_ACTIONS=on; the suite tests the actions themselves
    [OneTimeSetUp]
    public void TurnOn() => FileActions.Override = true;

    [OneTimeTearDown]
    public void Reset() => FileActions.Override = null;
}
