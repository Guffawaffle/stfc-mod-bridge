using System.Xml.Linq;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ProfileWorkspaceTransitionTests
{
    [TestMethod]
    public async Task StayingPreservesBothDraftsAndDoesNotSaveOrDiscard()
    {
        var drafts = true;
        var writes = 0;
        var discards = 0;
        var switched = await MainWindow.ResolveDraftTransitionAsync(() => drafts, () => false,
            () => Task.FromResult(ProfileDraftDecision.Stay),
            () => { writes++; drafts = false; return Task.CompletedTask; }, () => { discards++; drafts = false; });
        Assert.IsFalse(switched);
        Assert.IsTrue(drafts);
        Assert.AreEqual(0, writes);
        Assert.AreEqual(0, discards);
    }

    [TestMethod]
    public async Task FailedSaveCannotRetargetRemainingDrafts()
    {
        var saves = 0;
        var switched = await MainWindow.ResolveDraftTransitionAsync(() => true, () => false,
            () => Task.FromResult(ProfileDraftDecision.Save),
            () => { saves++; return Task.CompletedTask; }, () => Assert.Fail("Save must not discard."));
        Assert.IsFalse(switched);
        Assert.AreEqual(1, saves);
    }

    [TestMethod]
    public async Task ActiveSavePreventsContextChoiceAndRetarget()
    {
        var switched = await MainWindow.ResolveDraftTransitionAsync(() => true, () => true,
            () => { Assert.Fail("An active save cannot be interrupted by a context prompt."); return Task.FromResult(ProfileDraftDecision.Discard); },
            () => { Assert.Fail(); return Task.CompletedTask; }, () => Assert.Fail());
        Assert.IsFalse(switched);
    }

    [TestMethod]
    public async Task CompletedExplicitDiscardAllowsTransitionWithoutSaving()
    {
        var drafts = true;
        var switched = await MainWindow.ResolveDraftTransitionAsync(() => drafts, () => false,
            () => Task.FromResult(ProfileDraftDecision.Discard),
            () => { Assert.Fail("Discard cannot write staged configuration."); return Task.CompletedTask; }, () => drafts = false);
        Assert.IsTrue(switched);
    }

    [TestMethod]
    public void BayActionsNameAndCarryTheirOwnProfileIdentity()
    {
        var document = XDocument.Load(Path.Combine(RepositoryRoot(), "src/STFCCommunityMod.Launcher/MainWindow.xaml"));
        var buttons = document.Descendants().Where(element => element.Name.LocalName == "Button");
        var launch = buttons.Single(element => (string?)element.Attribute("Click") == "LaunchProfileCardButton_Click");
        var configure = buttons.Single(element => (string?)element.Attribute("Click") == "ConfigureProfileButton_Click");
        Assert.AreEqual("{Binding Id}", (string?)launch.Attribute("Tag"));
        Assert.AreEqual("{Binding ActionLabel}", (string?)launch.Attribute("Content"));
        Assert.AreEqual("{Binding Id}", (string?)configure.Attribute("Tag"));
        Assert.IsFalse(buttons.Any(element => (string?)element.Attribute("Click") is "UseSelectedProfileButton_Click" or "UseDefaultProfileButton_Click"));
        Assert.IsTrue(document.Descendants().Any(element => element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "ProfilesWorkspace")));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Bridge repository was not found.");
    }
}
