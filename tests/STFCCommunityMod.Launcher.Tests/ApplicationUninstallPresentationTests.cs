using System.Xml.Linq;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ApplicationUninstallPresentationTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [TestMethod]
    public void UninstallConfirmationKeepsDestructiveScopeExplicitAndOptIn()
    {
        var document = XDocument.Load(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher",
            "ApplicationUninstallWindow.xaml"));
        var window = document.Root!;
        var removeState = document.Descendants(Presentation + "CheckBox").Single();
        var text = document.ToString(SaveOptions.DisableFormatting);

        Assert.AreEqual("600", (string?)window.Attribute("MinWidth"));
        Assert.AreEqual("520", (string?)window.Attribute("MinHeight"));
        Assert.AreEqual("False", (string?)removeState.Attribute("IsChecked"));
        Assert.AreEqual(
            "Also remove Mod Bridge local data",
            (string?)removeState.Attribute(Automation + "AutomationProperties.Name"));
        StringAssert.Contains(text, "Community Mod TOML will not be changed");
        StringAssert.Contains(text, "Application files");
        StringAssert.Contains(text, "Local data");

        var namedButtons = document.Descendants(Presentation + "Button")
            .Select(element => (string?)element.Attribute(Automation + "AutomationProperties.Name"))
            .ToArray();
        CollectionAssert.Contains(namedButtons, "Cancel uninstall");
        CollectionAssert.Contains(namedButtons, "Uninstall STFC Mod Bridge");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate the launcher repository root.");
        return directory.FullName;
    }
}
