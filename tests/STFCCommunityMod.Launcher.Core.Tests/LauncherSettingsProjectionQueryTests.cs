using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherSettingsProjectionQueryTests
{
    [TestMethod]
    public void SectionProjectionContainsNoUnrelatedRows()
    {
        var catalog = LoadCatalog();
        var query = new LauncherSettingsProjectionQuery(
            catalog,
            new PrincipalCatalogSettingsLayoutProvider());

        var projection = query.Project(LauncherSettingsSection.General, null);
        var rows = projection.OfType<LauncherSettingRowProjection>().ToArray();

        Assert.IsTrue(rows.Length > 0);
        Assert.IsTrue(
            rows.All(row => row.Placement.Section == LauncherSettingsSection.General));
        Assert.IsTrue(rows.Length < catalog.VisibleSettings.Count);
        Assert.IsFalse(
            rows.Any(row => row.Setting.Control == LauncherConfigurationControl.Keybinding));
    }

    [TestMethod]
    public void ProjectionIsOneFlatOrderedSequence()
    {
        var query = new LauncherSettingsProjectionQuery(
            LoadCatalog(),
            new PrincipalCatalogSettingsLayoutProvider());

        var projection = query.Project(LauncherSettingsSection.Hotkeys, null);
        var firstFamilyIndex = IndexOf<LauncherSettingsFamilyHeaderProjection>(projection);
        var firstRowIndex = IndexOf<LauncherSettingRowProjection>(projection);

        Assert.IsInstanceOfType<LauncherSettingsGroupHeaderProjection>(projection[0]);
        Assert.IsTrue(firstFamilyIndex >= 0);
        Assert.IsTrue(firstRowIndex >= 0);
        Assert.IsInstanceOfType<LauncherSettingRowProjection>(
            projection[firstFamilyIndex + 1]);
        Assert.IsTrue(
            projection.OfType<LauncherSettingRowProjection>()
                .All(row => row.Placement.Section == LauncherSettingsSection.Hotkeys));
    }

    [TestMethod]
    public void SearchProjectsMatchingRowsAcrossSections()
    {
        var query = new LauncherSettingsProjectionQuery(
            LoadCatalog(),
            new PrincipalCatalogSettingsLayoutProvider());

        var rows = query
            .Project(LauncherSettingsSection.General, "fleet arrived")
            .OfType<LauncherSettingRowProjection>()
            .ToArray();

        Assert.IsTrue(rows.Length > 0);
        Assert.IsTrue(rows.Any(row => row.Placement.Section == LauncherSettingsSection.Notifications));
    }

    private static int IndexOf<T>(IReadOnlyList<LauncherSettingsProjectionItem> items)
        where T : LauncherSettingsProjectionItem
    {
        for (var index = 0; index < items.Count; ++index)
        {
            if (items[index] is T)
            {
                return index;
            }
        }

        return -1;
    }

    private static LauncherConfigurationCatalog LoadCatalog()
    {
        var schemaPath = FindRepositoryFile(
            "docs",
            "windows-launcher",
            "config-schema.guffawaffle.v1.json");
        return LauncherConfigurationSchemaLoader.LoadFile(schemaPath);
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail($"Could not find repository file '{Path.Combine(relativeParts)}'.");
        return string.Empty;
    }
}
