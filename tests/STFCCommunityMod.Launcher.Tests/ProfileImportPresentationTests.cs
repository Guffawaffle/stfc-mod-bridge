using System.IO;
using System.Xml.Linq;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ProfileImportPresentationTests
{
    private static readonly string[] ReviewButtons = ["_Not now", "_Continue"];
    [TestMethod]
    public void ReviewMakesSourceDestinationPreservationAndNativeCredentialPromptVisible()
    {
        var presentation = ProfileImportPresentation.From(Plan(true));
        StringAssert.Contains(presentation.Title, "other-user");
        StringAssert.Contains(presentation.CopyExplanation, "new profile for Windows user destination-user");
        StringAssert.Contains(presentation.CopyExplanation, "original setup will stay as it is");
        StringAssert.Contains(presentation.PermissionExplanation, "Read permission is needed for this selected source.");
        StringAssert.Contains(presentation.PermissionExplanation, "Windows will ask for an administrator’s username and password");
        StringAssert.Contains(presentation.Details, "does not ask for or store Windows passwords");
    }

    [TestMethod]
    public void AccessibleCurrentUserImportDoesNotClaimAdminApprovalIsNeeded()
    {
        var presentation = ProfileImportPresentation.From(Plan(false));
        StringAssert.Contains(presentation.PermissionExplanation, "one-time copy");
        Assert.IsFalse(presentation.PermissionExplanation.Contains("administrator", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(presentation.PermissionExplanation, "launch selection will stay as it is");
    }

    [TestMethod]
    public void EssentialExplanationIsVisibleAndWrapsWhileOnlyExtraDetailsAreCollapsed()
    {
        var xaml = LoadXaml();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace automation = "clr-namespace:System.Windows.Automation;assembly=PresentationCore";
        var dialog = xaml.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ProfileImportReviewDialog");
        var details = dialog.Descendants().Single(element => element.Name.LocalName == "Expander");
        foreach (var name in new[] { "ProfileImportCopyExplanation", "ProfileImportPermissionExplanation" })
        {
            var explanation = dialog.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.AreEqual("Wrap", (string?)explanation.Attribute("TextWrapping"));
            Assert.IsFalse(explanation.Ancestors().Contains(details));
            Assert.IsNull(explanation.Attribute("ToolTip"));
        }
        Assert.IsNotNull(details.Attribute(automation + "AutomationProperties.Name"));
        var buttons = dialog.Descendants().Where(element => element.Name.LocalName == "Button").ToArray();
        CollectionAssert.AreEquivalent(ReviewButtons, buttons.Select(button => (string)button.Attribute("Content")!).ToArray());
        Assert.IsTrue(buttons.All(button => button.Attribute(automation + "AutomationProperties.Name") is not null));
        Assert.IsTrue(dialog.Descendants().Any(element => element.Name.LocalName == "ScrollViewer"));
    }

    private static ProfileUserImportPlan Plan(bool elevation) => new("source", "other-user", "destination", "destination-user",
        "Main", "", elevation, "Read permission is needed for this selected source.");

    private static XDocument LoadXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln")))
            directory = directory.Parent;
        Assert.IsNotNull(directory);
        return XDocument.Load(Path.Combine(directory.FullName, "src", "STFCCommunityMod.Launcher", "MainWindow.xaml"));
    }
}
