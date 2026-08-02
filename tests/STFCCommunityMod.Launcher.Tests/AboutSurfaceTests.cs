using System.Xml.Linq;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class AboutSurfaceTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [TestMethod]
    public void BundledCatalogProjectsIdentityProviderAndMaintainableNotices()
    {
        using var schema = typeof(SettingsViewModel).Assembly.GetManifestResourceStream(
            "STFCCommunityMod.Launcher.Schemas.Guffawaffle.v1.json");
        Assert.IsNotNull(schema);
        var configurationCatalog = LauncherConfigurationSchemaLoader.Load(schema);
        Uri? openedUri = null;
        var about = new LauncherAboutViewModel(
            BundledLauncherAboutCatalog.Load(),
            configurationCatalog,
            new(
                "Guffawaffle 2.1.0",
                "Active",
                "Verified fixture",
                "Principal",
                "guffawaffle",
                "Guffawaffle",
                "Stable",
                "Guffawaffle/stfc-mod"),
            uri => openedUri = uri);

        Assert.AreEqual(ModControlProductIdentity.ProductName, about.ProductName);
        Assert.AreEqual("Guffawaffle", about.Provider);
        Assert.AreEqual("guffawaffle", about.ProviderId);
        Assert.AreEqual("Stable", about.ReleaseChannel);
        Assert.AreEqual("Guffawaffle/stfc-mod", about.ReleaseRepository);
        Assert.AreEqual("https://github.com/Guffawaffle/stfc-mod", about.RuntimeRepositoryUrl);
        Assert.IsTrue(about.Contributors.Any(item => item.Name == "NetniV"));
        Assert.IsFalse(about.Contributors.Single(item => item.Name == "Tashcan").HasUrl);
        Assert.IsTrue(about.ThirdPartyNotices.Count >= 3);
        StringAssert.Contains(about.NoticeCoverageStatus, "does not claim legal completeness");
        StringAssert.Contains(about.NoticeCoverageStatus, "issue #30");

        Assert.IsTrue(about.OpenExternalLinkCommand.CanExecute(about.RepositoryUrl));
        about.OpenExternalLinkCommand.Execute(about.RepositoryUrl);
        Assert.AreEqual(new Uri(about.RepositoryUrl), openedUri);
        Assert.IsFalse(about.OpenExternalLinkCommand.CanExecute("file:///C:/unsafe"));
    }

    [TestMethod]
    public void AboutOwnsNoOperationalOrRawConfigurationActions()
    {
        var document = LoadSettingsXaml();
        var about = document.Descendants(Presentation + "ScrollViewer")
            .Single(element =>
                (string?)element.Attribute(Automation + "AutomationProperties.Name")
                == "About STFC Mod Control");
        var aboutText = about.ToString(SaveOptions.DisableFormatting);

        Assert.AreEqual("Disabled", (string?)about.Attribute("HorizontalScrollBarVisibility"));
        Assert.AreEqual("Auto", (string?)about.Attribute("VerticalScrollBarVisibility"));
        Assert.IsFalse(aboutText.Contains("OpenRawTomlCommand", StringComparison.Ordinal));
        Assert.IsFalse(aboutText.Contains("Diagnostics", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(aboutText.Contains("Recover", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(aboutText.Contains("Remove mod", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(aboutText.Contains("About.ThirdPartyNotices", StringComparison.Ordinal));
        Assert.IsTrue(aboutText.Contains("About.NoticeCoverageStatus", StringComparison.Ordinal));

        var rawTomlButton = document.Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute("Command") == "{Binding OpenRawTomlCommand}");
        Assert.AreEqual(
            "{Binding IsAdvancedSelected, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)rawTomlButton.Attribute("Visibility"));
    }

    [TestMethod]
    public void AboutLinksAndExpandableDetailsHaveAccessibleNames()
    {
        var document = LoadSettingsXaml();
        var about = document.Descendants(Presentation + "ScrollViewer")
            .Single(element =>
                (string?)element.Attribute(Automation + "AutomationProperties.Name")
                == "About STFC Mod Control");
        var namedButtons = about.Descendants(Presentation + "Button")
            .Select(element => (string?)element.Attribute(Automation + "AutomationProperties.Name"))
            .ToArray();

        CollectionAssert.Contains(namedButtons, "Open STFC Mod Control source repository");
        CollectionAssert.Contains(namedButtons, "Open STFC Mod Control releases");
        CollectionAssert.Contains(namedButtons, "Open STFC Mod Control license");
        CollectionAssert.Contains(namedButtons, "Open active mod provider repository");
        var contributionLink = about.Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute("Content") == "View contribution source");
        Assert.AreEqual(
            "{Binding HasUrl, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)contributionLink.Attribute("Visibility"));
        Assert.IsTrue(
            about.Descendants(Presentation + "Expander")
                .All(element => element.Attribute(Automation + "AutomationProperties.Name") is not null));
    }

    private static XDocument LoadSettingsXaml() =>
        XDocument.Load(
            Path.Combine(
                RepositoryRoot(),
                "src",
                "STFCCommunityMod.Launcher",
                "Views",
                "SettingsView.xaml"));

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
