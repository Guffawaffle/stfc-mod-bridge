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
    public void NumericEditorsUseSharedRangeBasedWidthClasses()
    {
        using var fixture = SettingsFixture.Create();

        fixture.Select(LauncherSettingsSection.Interface);
        Assert.AreEqual(
            SettingsInputWidth.Small,
            fixture.Row("ui.extend_chest_purchase_max").NumericInputWidth);

        fixture.Select(LauncherSettingsSection.Graphics);
        Assert.AreEqual(
            SettingsInputWidth.Medium,
            fixture.Row("graphics.default_system_zoom").NumericInputWidth);
        Assert.AreEqual(
            SettingsInputWidth.Large,
            fixture.Row("graphics.zoom").NumericInputWidth);
    }

    [TestMethod]
    public void SettingHelpShowsCatalogDefaultInsteadOfVisibleCurrentValue()
    {
        using var fixture = SettingsFixture.Create();

        fixture.Select(LauncherSettingsSection.Interface);
        var interfaceRow = fixture.Row("ui.extend_chest_purchase_max");
        StringAssert.Contains(interfaceRow.DefaultAndEffectiveHelp, "Default: 160");
        Assert.IsFalse(interfaceRow.DefaultAndEffectiveHelp.Contains("Current value:", StringComparison.Ordinal));
        interfaceRow.NumericText = "120";
        Assert.IsTrue(interfaceRow.DraftHasOverride);
        StringAssert.Contains(interfaceRow.DefaultAndEffectiveHelp, "Default: 160");
        Assert.IsTrue(interfaceRow.UseDefaultCommand.CanExecute(null));
        interfaceRow.UseDefaultCommand.Execute(null);
        Assert.IsFalse(interfaceRow.DraftHasOverride);

        fixture.Select(LauncherSettingsSection.Graphics);
        var graphicsRow = fixture.Row("graphics.default_system_zoom");
        Assert.AreEqual(
            "Setting details for Default system zoom",
            graphicsRow.DefaultAndEffectiveAutomationName);
        StringAssert.Contains(graphicsRow.DefaultAndEffectiveHelp, "Default: 1750");

        var booleanRow = fixture.Row("graphics.free_resize");
        StringAssert.Contains(booleanRow.DefaultAndEffectiveHelp, "Default:");
        Assert.IsFalse(booleanRow.DefaultAndEffectiveHelp.Contains("Current value:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SearchOpenClearAndCloseRemainDistinctCommands()
    {
        using var fixture = SettingsFixture.Create();

        fixture.ViewModel.SearchOpenCommand.Execute(null);
        Assert.IsTrue(fixture.ViewModel.IsSearchVisible);
        fixture.ViewModel.SearchText = "zoom";
        Assert.IsTrue(fixture.ViewModel.SearchClearCommand.CanExecute(null));

        fixture.ViewModel.SearchClearCommand.Execute(null);
        Assert.IsTrue(fixture.ViewModel.IsSearchVisible);
        Assert.AreEqual(string.Empty, fixture.ViewModel.SearchText);
        Assert.IsFalse(fixture.ViewModel.SearchClearCommand.CanExecute(null));

        fixture.ViewModel.SearchText = "notification";
        fixture.ViewModel.SearchCloseCommand.Execute(null);
        Assert.IsFalse(fixture.ViewModel.IsSearchVisible);
        Assert.AreEqual(string.Empty, fixture.ViewModel.SearchText);
    }

    [TestMethod]
    public void EmptySearchClosesForNonSearchWorkspacesButActiveQueryRemainsGlobal()
    {
        using var fixture = SettingsFixture.Create();

        fixture.ViewModel.SearchOpenCommand.Execute(null);
        fixture.Select(LauncherSettingsSection.DataSync);
        Assert.IsFalse(fixture.ViewModel.IsSearchVisible);
        Assert.IsTrue(fixture.ViewModel.IsDataSyncSelected);

        fixture.ViewModel.SearchOpenCommand.Execute(null);
        fixture.Select(LauncherSettingsSection.About);
        Assert.IsFalse(fixture.ViewModel.IsSearchVisible);
        Assert.IsTrue(fixture.ViewModel.IsAboutSelected);

        fixture.ViewModel.SearchOpenCommand.Execute(null);
        fixture.ViewModel.SearchText = "zoom";
        var dataSync = fixture.ViewModel.Sections.Single(
            item => item.Id == LauncherSettingsSection.DataSync);
        dataSync.SelectCommand.Execute(null);

        Assert.IsTrue(fixture.ViewModel.IsSearchVisible);
        Assert.IsTrue(fixture.ViewModel.IsSearchActive);
        Assert.AreEqual("Search results", fixture.ViewModel.WorkspaceTitle);
        Assert.IsFalse(fixture.ViewModel.IsDataSyncSelected);
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

    [TestMethod]
    public void PatchControlsRemainUnmaterializedUntilSessionAcknowledgement()
    {
        using var fixture = SettingsFixture.Create();
        var original = File.ReadAllText(fixture.ConfigurationPath);
        fixture.Select(LauncherSettingsSection.Advanced);
        var patchPaths = fixture.Catalog.VisibleSettings
            .Where(IsPatchSetting)
            .Select(setting => setting.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.IsFalse(fixture.ViewModel.IsPatchEditingUnlocked);
        Assert.AreEqual(original, File.ReadAllText(fixture.ConfigurationPath));
        Assert.IsFalse(
            fixture.ViewModel.ProjectionSnapshot.ConstructedSettingPaths.Any(patchPaths.Contains));
        var lockedGate = fixture.ViewModel.FilteredSettings
            .OfType<AdvancedPatchEditingGateViewModel>()
            .Single();
        Assert.IsTrue(lockedGate.IsLocked);
        Assert.AreEqual(patchPaths.Count, lockedGate.SettingCount);
        Assert.IsTrue(lockedGate.Summaries.All(summary => !summary.IsDirty));

        fixture.ViewModel.EnablePatchEditingCommand.Execute(null);

        Assert.IsTrue(fixture.ViewModel.IsPatchEditingUnlocked);
        Assert.IsTrue(
            fixture.ViewModel.ProjectionSnapshot.ConstructedSettingPaths.Any(patchPaths.Contains));
        Assert.AreEqual(original, File.ReadAllText(fixture.ConfigurationPath));
        Assert.IsFalse(fixture.ViewModel.HasPendingChanges);

        fixture.ViewModel.LockPatchEditingCommand.Execute(null);

        Assert.IsFalse(fixture.ViewModel.IsPatchEditingUnlocked);
        Assert.IsFalse(
            fixture.ViewModel.ProjectionSnapshot.ConstructedSettingPaths.Any(patchPaths.Contains));
        Assert.AreEqual(original, File.ReadAllText(fixture.ConfigurationPath));
        Assert.IsFalse(fixture.ViewModel.HasPendingChanges);
    }

    [TestMethod]
    public async Task RelockPreservesStagedPatchEditAndWorkspaceStillOwnsSaveAndDiscard()
    {
        using var fixture = SettingsFixture.Create();
        fixture.Select(LauncherSettingsSection.Advanced);
        var patch = fixture.Catalog.VisibleSettings.First(
            setting =>
                IsPatchSetting(setting)
                && setting.Control == LauncherConfigurationControl.Scalar
                && setting.ValueKind == LauncherConfigurationValueKind.Boolean);

        fixture.ViewModel.EnablePatchEditingCommand.Execute(null);
        var originalRow = fixture.Row(patch.Path);
        var originalValue = originalRow.BooleanValue;
        originalRow.BooleanValue = !originalValue;
        Assert.IsTrue(fixture.ViewModel.HasPendingChanges);

        fixture.ViewModel.LockPatchEditingCommand.Execute(null);

        Assert.IsFalse(fixture.ViewModel.IsPatchEditingUnlocked);
        Assert.IsTrue(fixture.ViewModel.HasPendingChanges);
        Assert.IsTrue(
            fixture.ViewModel.FilteredSettings
                .OfType<AdvancedPatchEditingGateViewModel>()
                .Single()
                .Summaries
                .Single(summary => summary.Title == patch.Presentation.Label)
                .IsDirty);

        originalRow.BooleanValue = originalValue;
        fixture.ViewModel.EnablePatchEditingCommand.Execute(null);
        Assert.AreEqual(!originalValue, fixture.Row(patch.Path).BooleanValue);

        fixture.ViewModel.LockPatchEditingCommand.Execute(null);
        fixture.ViewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => !fixture.ViewModel.HasPendingChanges);
        var persisted = File.ReadAllText(fixture.ConfigurationPath);
        StringAssert.Contains(persisted, $"{patch.Path.Split('.')[1]} = {!originalValue}".ToLowerInvariant());
        Assert.IsFalse(persisted.Contains("unlock", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(persisted.Contains("acknowledg", StringComparison.OrdinalIgnoreCase));

        var relaunched = fixture.CreateAdditionalViewModel();
        SettingsFixture.Select(relaunched, LauncherSettingsSection.Advanced);
        Assert.IsFalse(relaunched.IsPatchEditingUnlocked);
        Assert.IsFalse(
            relaunched.ProjectionSnapshot.ConstructedSettingPaths.Contains(
                patch.Path,
                StringComparer.OrdinalIgnoreCase));

        relaunched.EnablePatchEditingCommand.Execute(null);
        relaunched.FilteredSettings
            .OfType<SettingsRowViewModel>()
            .Single(row => row.Path == patch.Path)
            .BooleanValue = originalValue;
        relaunched.LockPatchEditingCommand.Execute(null);
        Assert.IsTrue(relaunched.HasPendingChanges);
        relaunched.DiscardCommand.Execute(null);
        Assert.IsFalse(relaunched.HasPendingChanges);
        Assert.AreEqual(persisted, File.ReadAllText(fixture.ConfigurationPath));
        Assert.IsFalse(relaunched.IsPatchEditingUnlocked);
    }

    [TestMethod]
    public void PatchGateRequiresEditableConfigurationAndRefreshesAvailability()
    {
        using var fixture = SettingsFixture.Create();
        string? selectedPath = null;
        var viewModel = fixture.CreateAdditionalViewModel(() => selectedPath);
        SettingsFixture.Select(viewModel, LauncherSettingsSection.Advanced);

        var unavailableGate = viewModel.FilteredSettings
            .OfType<AdvancedPatchEditingGateViewModel>()
            .Single();
        Assert.IsFalse(viewModel.IsConfigurationReady);
        Assert.IsFalse(viewModel.EnablePatchEditingCommand.CanExecute(null));
        Assert.IsFalse(unavailableGate.IsConfigurationAvailable);
        StringAssert.Contains(unavailableGate.SummaryTitle, "Schema-default");
        Assert.IsTrue(
            unavailableGate.Summaries.All(
                summary =>
                    !summary.IsConfigurationAvailable
                    && summary.ValueSource.Contains("Configuration unavailable", StringComparison.Ordinal)));
        viewModel.EnablePatchEditingCommand.Execute(null);
        Assert.IsFalse(viewModel.IsPatchEditingUnlocked);

        selectedPath = fixture.ConfigurationPath;
        viewModel.ReloadConfiguration();
        Assert.IsTrue(viewModel.IsConfigurationReady);
        Assert.IsTrue(viewModel.EnablePatchEditingCommand.CanExecute(null));
        Assert.IsTrue(
            viewModel.FilteredSettings
                .OfType<AdvancedPatchEditingGateViewModel>()
                .Single()
                .IsConfigurationAvailable);

        viewModel.EnablePatchEditingCommand.Execute(null);
        Assert.IsTrue(viewModel.IsPatchEditingUnlocked);
        selectedPath = null;
        viewModel.ReloadConfiguration();
        Assert.IsFalse(viewModel.IsConfigurationReady);
        Assert.IsFalse(viewModel.IsPatchEditingUnlocked);
        Assert.IsFalse(viewModel.EnablePatchEditingCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task SaveWhileUnlockedPreservesUnrelatedSparseToml()
    {
        const string original = "# keep this comment\n[custom]\nkeep = \"verbatim\"\n";
        using var fixture = SettingsFixture.Create(original);
        fixture.Select(LauncherSettingsSection.Advanced);
        var patch = fixture.Catalog.VisibleSettings.First(
            setting =>
                IsPatchSetting(setting)
                && setting.Control == LauncherConfigurationControl.Scalar
                && setting.ValueKind == LauncherConfigurationValueKind.Boolean);

        fixture.ViewModel.EnablePatchEditingCommand.Execute(null);
        var originalValue = fixture.Row(patch.Path).BooleanValue;
        fixture.Row(patch.Path).BooleanValue = !originalValue;
        fixture.ViewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => !fixture.ViewModel.HasPendingChanges);

        var persisted = File.ReadAllText(fixture.ConfigurationPath);
        Assert.IsTrue(fixture.ViewModel.IsPatchEditingUnlocked);
        StringAssert.Contains(persisted, "# keep this comment");
        StringAssert.Contains(persisted, "[custom]\nkeep = \"verbatim\"");
        StringAssert.Contains(persisted, $"{patch.Path.Split('.')[1]} = {!originalValue}".ToLowerInvariant());
    }

    [TestMethod]
    public void DiscardWhileUnlockedPreservesUnrelatedSparseToml()
    {
        const string original = "# keep this comment\n[custom]\nkeep = \"verbatim\"\n";
        using var fixture = SettingsFixture.Create(original);
        fixture.Select(LauncherSettingsSection.Advanced);
        var patch = fixture.Catalog.VisibleSettings.First(
            setting =>
                IsPatchSetting(setting)
                && setting.Control == LauncherConfigurationControl.Scalar
                && setting.ValueKind == LauncherConfigurationValueKind.Boolean);

        fixture.ViewModel.EnablePatchEditingCommand.Execute(null);
        var originalValue = fixture.Row(patch.Path).BooleanValue;
        fixture.Row(patch.Path).BooleanValue = !originalValue;
        Assert.IsTrue(fixture.ViewModel.HasPendingChanges);
        fixture.ViewModel.DiscardCommand.Execute(null);

        Assert.IsTrue(fixture.ViewModel.IsPatchEditingUnlocked);
        Assert.IsFalse(fixture.ViewModel.HasPendingChanges);
        Assert.AreEqual(original, File.ReadAllText(fixture.ConfigurationPath));
        Assert.AreEqual(originalValue, fixture.Row(patch.Path).BooleanValue);
    }

    [TestMethod]
    public void SparseConfigurationProjectsCompleteProviderNotificationCatalog()
    {
        using var fixture = SettingsFixture.Create("# no notification overrides\n");
        fixture.Select(LauncherSettingsSection.Notifications);

        var expected = fixture.Catalog.NotificationCatalog.Events
            .Count(definition => definition.Setting.IsDirectlyEditable);
        var rows = fixture.ViewModel.FilteredSettings
            .OfType<SettingsRowViewModel>()
            .ToArray();
        Assert.AreEqual(expected, rows.Length);
        Assert.IsTrue(rows.All(row => row.IsNotificationEditor));
        Assert.IsTrue(rows.All(row => row.EffectiveState == "Initial value"));
        Assert.IsTrue(rows.All(row => row.EffectiveValueSource.Contains("Provider default", StringComparison.Ordinal)));
        Assert.IsTrue(rows.All(row => !string.IsNullOrWhiteSpace(row.AccessibleName)));
        Assert.IsTrue(rows.All(row => !string.IsNullOrWhiteSpace(row.AccessibleHelp)));
        Assert.IsTrue(
            fixture.ViewModel.FilteredSettings.OfType<SettingsGroupHeaderViewModel>().Any());
        Assert.IsFalse(fixture.ViewModel.HasPendingChanges);
        Assert.AreEqual("# no notification overrides\n", File.ReadAllText(fixture.ConfigurationPath));
    }

    [TestMethod]
    public void NotificationAliasSearchAndCompatibilityProvenanceRemainDiscoverable()
    {
        const string source =
            "# preserve me\n"
            + "[notifications.events.fleet]\n"
            + "arrived_in_system = { system = false, audio = true, sound = \"arrival\" }\n";
        using var fixture = SettingsFixture.Create(source);
        var definition = fixture.Catalog.NotificationCatalog.Events.Single(
            item => item.Setting.Path == "notifications.fleet_arrived_in_system");
        var alias = definition.Aliases.Single(
            item => item.Path == "notifications.events.fleet.arrived_in_system");

        fixture.ViewModel.SearchText = alias.Path;
        var row = fixture.ViewModel.FilteredSettings
            .OfType<SettingsRowViewModel>()
            .Single();
        Assert.AreEqual(definition.Setting.Path, row.Path);
        Assert.IsTrue(row.IsCompatibilityResolved);
        Assert.AreEqual("Compatibility value", row.EffectiveState);
        Assert.AreEqual("Unknown", row.EffectiveValue);
        Assert.AreEqual("Compatibility policy · Runtime-resolved", row.NotificationDeliverySummary);
        StringAssert.Contains(row.DefaultAndEffectiveHelp, alias.Path);
        StringAssert.Contains(row.DefaultAndEffectiveHelp, "Unknown");
        StringAssert.Contains(row.NotificationPolicyHelp, "canonical whole-policy override");
        Assert.AreEqual(source, File.ReadAllText(fixture.ConfigurationPath));
        Assert.IsFalse(fixture.ViewModel.HasPendingChanges);
    }

    [TestMethod]
    public async Task CanonicalNotificationEditPreservesCompatibilityAndUnrelatedToml()
    {
        const string source =
            "# preserve me\n"
            + "[notifications.events.fleet]\n"
            + "arrived_in_system = { system = false, audio = true, sound = \"arrival\" }\n"
            + "[custom]\nkeep = \"verbatim\"\n";
        using var fixture = SettingsFixture.Create(source);
        fixture.Select(LauncherSettingsSection.Notifications);
        var row = fixture.Row("notifications.fleet_arrived_in_system");

        row.NotificationSystem = true;
        Assert.IsTrue(fixture.ViewModel.HasPendingChanges);
        Assert.AreEqual("Canonical TOML · notifications.fleet_arrived_in_system", row.EffectiveValueSource);
        StringAssert.Contains(row.DefaultAndEffectiveHelp, "Canonical precedence");

        fixture.ViewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => !fixture.ViewModel.HasPendingChanges);
        var persisted = File.ReadAllText(fixture.ConfigurationPath);
        StringAssert.Contains(persisted, "# preserve me");
        StringAssert.Contains(persisted, "arrived_in_system = { system = false, audio = true");
        StringAssert.Contains(persisted, "fleet_arrived_in_system = true");
        StringAssert.Contains(persisted, "[custom]\nkeep = \"verbatim\"");
    }

    private static bool IsPatchSetting(LauncherConfigurationSetting setting) =>
        string.Equals(setting.Category, "patches", StringComparison.OrdinalIgnoreCase);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(20);
        }

        Assert.IsTrue(predicate(), "Timed out waiting for the settings operation.");
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

        public static SettingsFixture Create(
            string contents = "# disposable launcher projection fixture\n")
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
                contents,
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

        public SettingsViewModel CreateAdditionalViewModel(
            Func<string?>? configurationPathProvider = null)
        {
            var command = new TestCommand();
            return new SettingsViewModel(
                Catalog,
                command,
                command,
                configurationPathProvider ?? (() => ConfigurationPath),
                Layout,
                new("Guffawaffle test", "Active", "Test fixture", Layout.DisplayName));
        }

        public static void Select(SettingsViewModel viewModel, LauncherSettingsSection section)
        {
            viewModel.SearchText = string.Empty;
            viewModel.Sections.Single(item => item.Id == section).SelectCommand.Execute(null);
        }

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
