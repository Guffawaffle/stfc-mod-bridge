using System.Xml.Linq;
using STFCCommunityMod.Launcher;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class WorkspaceWindowSizingTests
{
    [TestMethod]
    public void InitialHomeAndNavigationHomeResolveToTheSameContract()
    {
        var initial = MainWindow.ResolveWorkspaceSizing(
            LauncherWorkspace.Home,
            currentWidth: 0,
            currentHeight: 0,
            workAreaWidth: 1920,
            workAreaHeight: 1040);
        var returning = MainWindow.ResolveWorkspaceSizing(
            LauncherWorkspace.Home,
            currentWidth: 1120,
            currentHeight: 740,
            workAreaWidth: 1920,
            workAreaHeight: 1040);

        Assert.AreEqual(initial, returning);
        Assert.AreEqual(new WorkspaceWindowSizing(560, 620, 680, 680), initial);
    }

    [TestMethod]
    public void ExpandedWorkspacesShareTheirContractAndClampToTheWorkArea()
    {
        var settings = MainWindow.ResolveWorkspaceSizing(
            LauncherWorkspace.Settings,
            currentWidth: 680,
            currentHeight: 680,
            workAreaWidth: 1024,
            workAreaHeight: 700);
        var diagnostics = MainWindow.ResolveWorkspaceSizing(
            LauncherWorkspace.Diagnostics,
            currentWidth: 680,
            currentHeight: 680,
            workAreaWidth: 1024,
            workAreaHeight: 700);

        Assert.AreEqual(settings, diagnostics);
        Assert.AreEqual(new WorkspaceWindowSizing(960, 620, 1024, 700), settings);
    }

    [TestMethod]
    public void XamlDoesNotOwnACompetingStartupSizeContract()
    {
        var root = XDocument.Load(RepositoryPath("src/STFCCommunityMod.Launcher/MainWindow.xaml")).Root!;

        Assert.IsNull(root.Attribute("Width"));
        Assert.IsNull(root.Attribute("Height"));
        Assert.IsNull(root.Attribute("MinWidth"));
        Assert.IsNull(root.Attribute("MinHeight"));

        var source = File.ReadAllText(RepositoryPath("src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var initialization = source.IndexOf("InitializeComponent();", StringComparison.Ordinal);
        var initialSizing = source.IndexOf(
            "ApplyWorkspaceSizing(LauncherWorkspace.Home);",
            initialization,
            StringComparison.Ordinal);
        var preferences = source.IndexOf("uiPreferencesStore =", initialization, StringComparison.Ordinal);

        Assert.IsTrue(initialization >= 0);
        Assert.IsTrue(initialSizing > initialization);
        Assert.IsTrue(preferences > initialSizing);
    }

    private static string RepositoryPath(string relativePath) =>
        Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
