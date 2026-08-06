using System.Xml.Linq;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ReleaseSecurityGuidanceTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [TestMethod]
    public void CanonicalGuidesAreBundledAndKeepTrustClaimsSeparate()
    {
        var guidance = BundledReleaseSecurityGuidance.Load();

        StringAssert.Contains(guidance.IndependentVerification, "local integrity");
        StringAssert.Contains(guidance.IndependentVerification, "publisher evidence");
        StringAssert.Contains(guidance.IndependentVerification, "build origin");
        StringAssert.Contains(guidance.IndependentVerification, "release authorization and freshness");
        StringAssert.Contains(
            guidance.IndependentVerification,
            "stfc-mod-bridge-release-selection-attestation.json");
        StringAssert.Contains(guidance.IndependentVerification, "--custom-trusted-root");
        StringAssert.Contains(guidance.IndependentVerification, "None of those claims proves");

        StringAssert.Contains(guidance.CompromiseResponse, "does not wait for a higher replacement");
        StringAssert.Contains(guidance.CompromiseResponse, "Do not delete local or hosted evidence");
        StringAssert.Contains(guidance.CompromiseResponse, "Offline clients cannot learn later withdrawal");
        StringAssert.Contains(guidance.CompromiseResponse, "issue #71");
    }

    [TestMethod]
    public void GuidanceWindowIsSelectableReadOnlyAndKeyboardReachable()
    {
        var document = XDocument.Load(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher",
            "ReleaseSecurityGuidanceWindow.xaml"));
        var textBoxes = document.Descendants(Presentation + "TextBox").ToArray();

        Assert.AreEqual(2, textBoxes.Length);
        Assert.IsTrue(textBoxes.All(element => (string?)element.Attribute("IsReadOnly") == "True"));
        Assert.IsTrue(textBoxes.All(element =>
            element.Attribute(Automation + "AutomationProperties.Name") is not null));
        Assert.AreEqual(2, document.Descendants(Presentation + "TabItem").Count());
        Assert.IsTrue(document.Descendants(Presentation + "Button").Any(element =>
            (string?)element.Attribute("IsCancel") == "True"
            && (string?)element.Attribute(Automation + "AutomationProperties.Name")
                == "Close verification and recovery guidance"));
    }

    [TestMethod]
    public void AboutAndDiagnosticsExposeTheSameOfflineGuidanceAction()
    {
        var root = RepositoryRoot();
        var settings = XDocument.Load(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher",
            "Views",
            "SettingsView.xaml"));
        var main = XDocument.Load(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher",
            "MainWindow.xaml"));

        var settingsButton = settings.Descendants(Presentation + "Button").Single(element =>
            (string?)element.Attribute(Automation + "AutomationProperties.Name")
                == "Open bundled verification and recovery guidance");
        var diagnosticsButton = main.Descendants(Presentation + "Button").Single(element =>
            (string?)element.Attribute(Automation + "AutomationProperties.Name")
                == "Open bundled verification and recovery guidance");

        Assert.AreEqual(
            "{Binding About.OpenReleaseSecurityGuidanceCommand}",
            (string?)settingsButton.Attribute("Command"));
        Assert.AreEqual(
            "ReleaseSecurityGuidanceButton_Click",
            (string?)diagnosticsButton.Attribute("Click"));
    }

    [TestMethod]
    public void RepositoryGuidesReconcileWithdrawalAndQualificationBoundaries()
    {
        var root = RepositoryRoot();
        var verification = File.ReadAllText(Path.Combine(
            root,
            "docs",
            "windows-launcher",
            "INDEPENDENT_VERIFICATION.md"));
        var response = File.ReadAllText(Path.Combine(
            root,
            "docs",
            "windows-launcher",
            "COMPROMISE_RESPONSE.md"));
        var withdrawal = File.ReadAllText(Path.Combine(root, "docs", "release-withdrawals", "README.md"));

        foreach (var required in new[]
                 {
                     "gh attestation verify",
                     "--signer-workflow",
                     "--source-ref",
                     "--source-digest",
                     "--deny-self-hosted-runners",
                     "--format json",
                     "signtool verify /pa /all /v /debug",
                     "stfc-identity-v1",
                     "Evidence receipt",
                 })
        {
            StringAssert.Contains(verification, required);
        }

        StringAssert.Contains(response, "Azure Artifact Signing");
        StringAssert.Contains(response, "GitHub repository or workflow");
        StringAssert.Contains(response, "Sigstore root, Fulcio identity, or Rekor/log evidence");
        StringAssert.Contains(response, "missed the overlap");
        StringAssert.Contains(withdrawal, "Emergency containment never waits for a higher replacement");
        StringAssert.Contains(withdrawal, "authenticated runtime denylist");
        StringAssert.Contains(withdrawal, "not a prerequisite");
        Assert.IsFalse(withdrawal.Contains("removes the affected GitHub release and tag", StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the launcher repository root.");
    }
}
