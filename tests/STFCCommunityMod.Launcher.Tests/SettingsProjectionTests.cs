using System.Text;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class SettingsProjectionTests
{
    private const string SchemaResource =
        "STFCCommunityMod.Launcher.Schemas.Guffawaffle.v1.json";

    [TestMethod]
    public void InitialSectionConstructsNoUnrelatedRows()
    {
        using var fixture = SettingsFixture.Create();
        var snapshot = fixture.ViewModel.ProjectionSnapshot;

        Assert.IsTrue(snapshot.ConstructedRowCount > 0);
        Assert.IsTrue(snapshot.ConstructedRowCount < fixture.Catalog.VisibleSettings.Count);
        Assert.IsTrue(
            snapshot.ConstructedSettingPaths.All(
                path =>
                    fixture.Layout.Place(fixture.SettingsByPath[path]).Section
                    == LauncherSettingsSection.General));
        Assert.IsFalse(
            snapshot.ConstructedSettingPaths.Any(
                path =>
                    fixture.SettingsByPath[path].Control
                    == LauncherConfigurationControl.Keybinding));
    }

    [TestMethod]
    public void InvalidEditorTextSurvivesProjectionRecreation()
    {
        using var fixture = SettingsFixture.Create();
        var numericSetting = fixture.Catalog.VisibleSettings.First(
            setting =>
                setting.ValueKind
                    is LauncherConfigurationValueKind.Integer
                    or LauncherConfigurationValueKind.Number);
        var numericSection = fixture.Layout.Place(numericSetting).Section;
        fixture.Select(numericSection);
        var originalRow = fixture.Row(numericSetting.Path);

        originalRow.NumericText = "not-a-number";
        Assert.IsTrue(fixture.ViewModel.HasInvalidInput);

        fixture.Select(OtherSection(numericSection));
        fixture.Select(numericSection);
        var recreatedRow = fixture.Row(numericSetting.Path);

        Assert.AreNotSame(originalRow, recreatedRow);
        Assert.AreEqual("not-a-number", recreatedRow.NumericText);
        Assert.IsTrue(recreatedRow.NumericNeedsAttention);
        Assert.IsTrue(fixture.ViewModel.HasInvalidInput);
    }

    [TestMethod]
    public void SearchProjectionRetainsConflictsWithHiddenCommands()
    {
        using var fixture = SettingsFixture.Create();
        fixture.Select(LauncherSettingsSection.Hotkeys);
        var candidates = fixture.ViewModel.FilteredSettings
            .OfType<SettingsRowViewModel>()
            .Where(row => row.IsKeybindingEditor)
            .GroupBy(row => row.Setting.KeybindingMetadata?.ConflictGroup)
            .First(group =>
                !string.IsNullOrWhiteSpace(group.Key)
                && !string.Equals(group.Key, "None", StringComparison.OrdinalIgnoreCase)
                && group.Count() >= 2)
            .Take(2)
            .ToArray();

        candidates[0].AddKeybindingCommand.Execute("CTRL-ALT-F12");
        candidates[1].AddKeybindingCommand.Execute("CTRL-ALT-F12");
        fixture.ViewModel.SearchText = candidates[0].Path;

        var visibleRows = fixture.ViewModel.FilteredSettings
            .OfType<SettingsRowViewModel>()
            .ToArray();
        Assert.AreEqual(1, visibleRows.Length);
        Assert.AreEqual(candidates[0].Path, visibleRows[0].Path);
        Assert.IsFalse(
            fixture.ViewModel.ProjectionSnapshot.ConstructedSettingPaths.Contains(
                candidates[1].Path,
                StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(visibleRows[0].KeybindingNeedsAttention);
        StringAssert.Contains(
            visibleRows[0].KeybindingValidationMessage,
            "conflicts with");
        Assert.IsTrue(fixture.ViewModel.HasInvalidInput);
    }

    private static LauncherSettingsSection OtherSection(
        LauncherSettingsSection section) =>
        section == LauncherSettingsSection.General
            ? LauncherSettingsSection.Interface
            : LauncherSettingsSection.General;

    private sealed class SettingsFixture : IDisposable
    {
        private SettingsFixture(
            string configurationPath,
            LauncherConfigurationCatalog catalog,
            PrincipalCatalogSettingsLayoutProvider layout,
            SettingsViewModel viewModel)
        {
            ConfigurationPath = configurationPath;
            Catalog = catalog;
            Layout = layout;
            ViewModel = viewModel;
            SettingsByPath = catalog.VisibleSettings.ToDictionary(
                setting => setting.Path,
                StringComparer.OrdinalIgnoreCase);
        }

        public string ConfigurationPath { get; }

        public LauncherConfigurationCatalog Catalog { get; }

        public PrincipalCatalogSettingsLayoutProvider Layout { get; }

        public SettingsViewModel ViewModel { get; }

        public IReadOnlyDictionary<string, LauncherConfigurationSetting> SettingsByPath { get; }

        public static SettingsFixture Create()
        {
            using var schema = typeof(SettingsViewModel).Assembly
                .GetManifestResourceStream(SchemaResource);
            Assert.IsNotNull(schema);
            var catalog = LauncherConfigurationSchemaLoader.Load(schema);
            var layout = new PrincipalCatalogSettingsLayoutProvider();
            var configurationPath = Path.Combine(
                Path.GetTempPath(),
                $"stfc-launcher-projection-{Guid.NewGuid():N}.toml");
            File.WriteAllText(
                configurationPath,
                "# disposable launcher projection fixture\n",
                new UTF8Encoding(false));
            var command = new TestCommand();
            var viewModel = new SettingsViewModel(
                catalog,
                command,
                command,
                () => configurationPath,
                layout,
                new("Guffawaffle test", "Active", "Test fixture", layout.DisplayName));
            return new(configurationPath, catalog, layout, viewModel);
        }

        public void Select(LauncherSettingsSection section)
        {
            ViewModel.SearchText = string.Empty;
            var navigation = ViewModel.Sections.Single(item => item.Id == section);
            navigation.SelectCommand.Execute(null);
        }

        public SettingsRowViewModel Row(string path) =>
            ViewModel.FilteredSettings
                .OfType<SettingsRowViewModel>()
                .Single(row => string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase));

        public void Dispose()
        {
            if (File.Exists(ConfigurationPath))
            {
                File.Delete(ConfigurationPath);
            }

            var backupPath = ConfigurationPath + ".bak";
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
    }

    private sealed class TestCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }
}
