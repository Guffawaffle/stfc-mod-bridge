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
    public void BoundedNumericSliderAndTextboxStaySynchronized()
    {
        using var fixture = SettingsFixture.Create();
        const string path = "ui.extend_chest_purchase_max";
        var setting = fixture.SettingsByPath[path];
        fixture.Select(fixture.Layout.Place(setting).Section);
        var row = fixture.Row(path);

        Assert.IsTrue(row.HasNumericSlider);
        Assert.AreEqual(0d, row.NumericSliderMinimum);
        Assert.AreEqual(160d, row.NumericSliderMaximum);
        Assert.AreEqual(1d, row.NumericSliderStep);
        Assert.AreEqual(160d, row.NumericSliderValue);

        row.NumericSliderValue = 123.6;
        Assert.AreEqual("124", row.NumericText);
        Assert.AreEqual(124d, row.NumericSliderValue);

        row.NumericText = "45";
        Assert.AreEqual(45d, row.NumericSliderValue);

        row.NumericText = "not-a-number";
        Assert.AreEqual("not-a-number", row.NumericText);
        Assert.AreEqual(45d, row.NumericSliderValue);
        Assert.IsTrue(row.NumericNeedsAttention);

        row.NumericText = "161";
        Assert.AreEqual("161", row.NumericText);
        Assert.AreEqual(45d, row.NumericSliderValue);
        Assert.IsTrue(row.NumericNeedsAttention);
    }

    [TestMethod]
    public void NumericSettingWithoutSliderMetadataRemainsTextboxOnly()
    {
        using var fixture = SettingsFixture.Create();
        var setting = fixture.SettingsByPath["graphics.ui_scale"];
        fixture.Select(fixture.Layout.Place(setting).Section);

        Assert.IsFalse(fixture.Row(setting.Path).HasNumericSlider);
    }

    [TestMethod]
    public void LargeSystemZoomRangeRetainsSingleUnitSliderSteps()
    {
        using var fixture = SettingsFixture.Create();
        var setting = fixture.SettingsByPath["graphics.default_system_zoom"];
        fixture.Select(fixture.Layout.Place(setting).Section);
        var row = fixture.Row(setting.Path);

        Assert.IsTrue(row.HasNumericSlider);
        Assert.AreEqual(0d, row.NumericSliderMinimum);
        Assert.AreEqual(5000d, row.NumericSliderMaximum);
        Assert.AreEqual(1d, row.NumericSliderStep);
        Assert.AreEqual(1750d, row.NumericSliderValue);

        row.NumericSliderValue = 4321.4;
        Assert.AreEqual("4321.0", row.NumericText);
        Assert.AreEqual(4321d, row.NumericSliderValue);
    }

    [TestMethod]
    public void SoftSliderRangeKeepsAccessibleDirectEntryValid()
    {
        using var fixture = SettingsFixture.Create();
        var setting = fixture.SettingsByPath["ui.escape_exit_timer"];
        fixture.Select(fixture.Layout.Place(setting).Section);
        var row = fixture.Row(setting.Path);

        Assert.IsTrue(row.HasNumericSlider);
        Assert.IsTrue(row.NumericSliderAllowsExtendedEntry);
        Assert.AreEqual(0d, row.NumericSliderMinimum);
        Assert.AreEqual(1000d, row.NumericSliderMaximum);
        Assert.AreEqual(25d, row.NumericSliderStep);

        row.NumericText = "1750";
        Assert.AreEqual("1750", row.NumericText);
        Assert.AreEqual(1000d, row.NumericSliderValue);
        Assert.IsFalse(row.NumericNeedsAttention);
        StringAssert.Contains(row.NumericValidationMessage, "larger values may be entered directly");

        row.NumericSliderValue = 750;
        Assert.AreEqual("750", row.NumericText);
        Assert.AreEqual(750d, row.NumericSliderValue);
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
