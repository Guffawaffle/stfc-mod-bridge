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
    private static readonly LauncherSettingsSection[] NetnivNavigationSections =
    [
        LauncherSettingsSection.General,
        LauncherSettingsSection.Interface,
        LauncherSettingsSection.Graphics,
        LauncherSettingsSection.Hotkeys,
        LauncherSettingsSection.DataSync,
        LauncherSettingsSection.About,
    ];

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
    public void InvalidStagedSettingProjectsActionAndCorrectionReenablesSave()
    {
        const string source = "[graphics]\nfree_resize = true\n";
        using var fixture = SettingsFixture.Create(source);
        fixture.Select(LauncherSettingsSection.Graphics);
        fixture.Row("graphics.free_resize").BooleanValue = false;
        var numeric = fixture.SettingsByPath["ui.extend_chest_purchase_max"];
        fixture.Select(fixture.Layout.Place(numeric).Section);
        fixture.Row(numeric.Path).NumericText = "not-a-number";

        var blocked = fixture.ViewModel.SaveState;
        Assert.AreEqual(WorkspaceSaveStateKind.Blocked, blocked.Kind);
        Assert.AreEqual(WorkspaceSaveBlockerKind.InvalidSetting, blocked.Blocker);
        Assert.AreEqual(WorkspaceSaveRecoveryKind.ReviewSetting, blocked.Recovery);
        Assert.AreEqual(numeric.Path, blocked.TargetId);
        StringAssert.Contains(blocked.Message, numeric.Presentation.Label);
        Assert.AreEqual(source, File.ReadAllText(fixture.ConfigurationPath));

        fixture.ViewModel.SaveRecoveryCommand.Execute(null);

        Assert.AreEqual(fixture.Layout.Place(numeric).Section, fixture.ViewModel.SelectedSection);
        Assert.AreEqual(numeric.Path, fixture.ViewModel.RecoveryFocusTargetId);
        Assert.IsTrue(fixture.ViewModel.RecoveryFocusRevision > 0);
        fixture.Row(numeric.Path).NumericText = "100";
        Assert.IsFalse(fixture.ViewModel.HasInvalidInput);
        Assert.IsTrue(fixture.ViewModel.CanSave, fixture.ViewModel.SaveAvailability);

        fixture.Row(numeric.Path).NumericText = "still-not-a-number";
        fixture.ViewModel.DiscardCommand.Execute(null);
        Assert.IsFalse(fixture.ViewModel.HasPendingChanges);
        Assert.IsFalse(fixture.ViewModel.HasInvalidInput);
        Assert.AreEqual(source, File.ReadAllText(fixture.ConfigurationPath));
    }

    [TestMethod]
    public void StagedShortcutConflictNamesBothCommandsAndTargetsFirstConflict()
    {
        using var fixture = SettingsFixture.Create();
        var original = File.ReadAllBytes(fixture.ConfigurationPath);
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

        var blocked = fixture.ViewModel.SaveState;
        Assert.AreEqual(WorkspaceSaveBlockerKind.InvalidSetting, blocked.Blocker);
        Assert.IsTrue(candidates.Any(candidate => candidate.Path == blocked.TargetId));
        var blockedCandidate = candidates.Single(candidate => candidate.Path == blocked.TargetId);
        var otherCandidate = candidates.Single(candidate => candidate.Path != blocked.TargetId);
        StringAssert.Contains(blocked.Message, blockedCandidate.Setting.Presentation.Label);
        StringAssert.Contains(blocked.Message, otherCandidate.Setting.Title);
        Assert.IsFalse(fixture.ViewModel.CanSave);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(fixture.ConfigurationPath));
    }

    [TestMethod]
    public void EditingDiagnosedShortcutMakesItBlockingUntilDiscardRestoresBaseline()
    {
        const string source =
            "[shortcuts]\n"
            + "action_primary = \"MOUSE1\"\n"
            + "action_queue = \"MOUSE1\"\n";
        using var fixture = SettingsFixture.Create(source, LoadNetniVStableCatalog());
        fixture.Select(LauncherSettingsSection.Hotkeys);
        var diagnosed = fixture.Row("shortcuts.action_primary");
        Assert.IsTrue(diagnosed.KeybindingNeedsAttention);
        Assert.IsFalse(fixture.ViewModel.HasInvalidInput);

        diagnosed.AddKeybindingCommand.Execute("CTRL-ALT-F12");

        Assert.IsTrue(fixture.ViewModel.HasPendingChanges);
        Assert.IsTrue(fixture.ViewModel.HasInvalidInput);
        Assert.AreEqual(
            WorkspaceSaveBlockerKind.InvalidSetting,
            fixture.ViewModel.SaveState.Blocker);
        fixture.ViewModel.DiscardCommand.Execute(null);

        Assert.IsFalse(fixture.ViewModel.HasPendingChanges);
        Assert.IsFalse(fixture.ViewModel.HasInvalidInput);
        Assert.AreEqual(source, File.ReadAllText(fixture.ConfigurationPath));
        fixture.Select(LauncherSettingsSection.Hotkeys);
        Assert.IsTrue(fixture.Row("shortcuts.action_primary").KeybindingNeedsAttention);
    }

    [TestMethod]
    public void SiblingDraftActionsNavigateBetweenSettingsAndDataSync()
    {
        using var fixture = SettingsFixture.Create(
            "[graphics]\nfree_resize = true\n\n[sync]\njobs = true\n");
        fixture.Select(LauncherSettingsSection.Graphics);
        fixture.Row("graphics.free_resize").BooleanValue = false;
        fixture.ViewModel.SyncWorkspace.GlobalFeeds.Single(feed => feed.Label == "Jobs").IsEnabled = false;

        Assert.AreEqual(
            WorkspaceSaveRecoveryKind.GoToDataSync,
            fixture.ViewModel.SaveState.Recovery);
        fixture.ViewModel.SaveRecoveryCommand.Execute(null);
        Assert.IsTrue(fixture.ViewModel.IsDataSyncSelected);
        Assert.AreEqual(
            WorkspaceSaveRecoveryKind.GoToSettings,
            fixture.ViewModel.SyncWorkspace.SaveState.Recovery);

        fixture.ViewModel.SyncWorkspace.SaveRecoveryCommand.Execute(null);
        Assert.AreEqual(LauncherSettingsSection.Graphics, fixture.ViewModel.SelectedSection);
        Assert.IsTrue(fixture.ViewModel.HasPendingChanges);
        Assert.IsTrue(fixture.ViewModel.SyncWorkspace.HasPendingChanges);
    }

    [TestMethod]
    public void SelectedInstallationConflictDiscardsAndReloadsWithoutLeakingPaths()
    {
        using var fixture = SettingsFixture.Create("[graphics]\nfree_resize = true\n");
        var otherPath = Path.Combine(Path.GetTempPath(), $"stfc-launcher-other-{Guid.NewGuid():N}.toml");
        File.WriteAllText(otherPath, "[graphics]\nfree_resize = false\n", new UTF8Encoding(false));
        try
        {
            var selectedPath = fixture.ConfigurationPath;
            var viewModel = fixture.CreateAdditionalViewModel(() => selectedPath);
            SettingsFixture.Select(viewModel, LauncherSettingsSection.Graphics);
            viewModel.FilteredSettings.OfType<SettingsRowViewModel>()
                .Single(row => row.Path == "graphics.free_resize").BooleanValue = false;
            selectedPath = otherPath;

            var blocked = viewModel.SaveState;
            Assert.AreEqual(WorkspaceSaveBlockerKind.SelectedConfigurationChanged, blocked.Blocker);
            Assert.AreEqual(WorkspaceSaveRecoveryKind.DiscardAndReload, blocked.Recovery);
            Assert.IsFalse(blocked.Message.Contains(fixture.ConfigurationPath, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(blocked.Message.Contains(otherPath, StringComparison.OrdinalIgnoreCase));

            viewModel.SaveRecoveryCommand.Execute(null);
            Assert.IsFalse(viewModel.HasPendingChanges);
            Assert.AreEqual("[graphics]\nfree_resize = true\n", File.ReadAllText(fixture.ConfigurationPath));
            Assert.AreEqual("[graphics]\nfree_resize = false\n", File.ReadAllText(otherPath));
        }
        finally
        {
            File.Delete(otherPath);
        }
    }

    [TestMethod]
    public async Task ExternalSettingsChangeBecomesBlockedUntilExplicitDiscardAndReload()
    {
        using var fixture = SettingsFixture.Create("[graphics]\nfree_resize = true\n");
        fixture.Select(LauncherSettingsSection.Graphics);
        fixture.Row("graphics.free_resize").BooleanValue = false;
        const string external = "# external\n[graphics]\nfree_resize = true\n";
        File.WriteAllText(fixture.ConfigurationPath, external, new UTF8Encoding(false));

        fixture.ViewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() =>
            fixture.ViewModel.SaveState.Blocker == WorkspaceSaveBlockerKind.ExternalChange);

        Assert.IsFalse(fixture.ViewModel.CanSave);
        Assert.AreEqual(external, File.ReadAllText(fixture.ConfigurationPath));
        fixture.ViewModel.SaveRecoveryCommand.Execute(null);
        Assert.IsFalse(fixture.ViewModel.HasPendingChanges);
        Assert.AreEqual(external, File.ReadAllText(fixture.ConfigurationPath));
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
    public void NumericEditorsUseCatalogWidthClassesAcrossRepresentativeRows()
    {
        using var fixture = SettingsFixture.Create();

        fixture.Select(LauncherSettingsSection.Interface);
        Assert.AreEqual(
            LauncherConfigurationEditorWidth.Compact,
            fixture.Row("ui.extend_chest_purchase_max").NumericInputWidth);
        Assert.AreEqual(
            LauncherConfigurationEditorWidth.Compact,
            fixture.Row("ui.escape_exit_timer").NumericInputWidth);

        fixture.Select(LauncherSettingsSection.Graphics);
        Assert.AreEqual(
            LauncherConfigurationEditorWidth.Compact,
            fixture.Row("graphics.default_system_zoom").NumericInputWidth);
        Assert.AreEqual(
            LauncherConfigurationEditorWidth.Compact,
            fixture.Row("graphics.keyboard_zoom_speed").NumericInputWidth);
        Assert.AreEqual(
            LauncherConfigurationEditorWidth.Compact,
            fixture.Row("graphics.loader_logo_scale").NumericInputWidth);

        Assert.IsTrue(fixture.Row("graphics.default_system_zoom").HasNumericSlider);
        Assert.IsFalse(fixture.Row("graphics.keyboard_zoom_speed").HasNumericSlider);
        Assert.IsTrue(fixture.Row("graphics.keyboard_zoom_speed").IsNumericTextOnly);
        Assert.IsFalse(fixture.Row("graphics.loader_logo_scale").HasNumericSlider);
        Assert.IsTrue(fixture.Row("graphics.loader_logo_scale").IsNumericTextOnly);
    }

    [TestMethod]
    public void DirtyAndRestoreKeepBoundedNumericGeometryClassificationStable()
    {
        using var fixture = SettingsFixture.Create();
        fixture.Select(LauncherSettingsSection.Interface);
        var row = fixture.Row("ui.extend_chest_purchase_max");
        var width = row.NumericInputWidth;
        var sliderMinimum = row.NumericSliderMinimum;
        var sliderMaximum = row.NumericSliderMaximum;

        row.NumericText = "120";

        Assert.IsTrue(row.IsDirty);
        Assert.AreEqual(width, row.NumericInputWidth);
        Assert.AreEqual(sliderMinimum, row.NumericSliderMinimum);
        Assert.AreEqual(sliderMaximum, row.NumericSliderMaximum);
        Assert.IsTrue(row.HasNumericSlider);

        row.RevertDraftCommand.Execute(null);

        Assert.IsFalse(row.IsDirty);
        Assert.AreEqual(width, row.NumericInputWidth);
        Assert.AreEqual(sliderMinimum, row.NumericSliderMinimum);
        Assert.AreEqual(sliderMaximum, row.NumericSliderMaximum);
        Assert.IsTrue(row.HasNumericSlider);
    }

    [TestMethod]
    public void SettingHelpShowsCatalogDefaultInsteadOfVisibleCurrentValue()
    {
        using var fixture = SettingsFixture.Create();

        fixture.Select(LauncherSettingsSection.Interface);
        var interfaceRow = fixture.Row("ui.extend_chest_purchase_max");
        StringAssert.Contains(interfaceRow.SettingDetailsHelp, "Default: 160");
        StringAssert.Contains(
            interfaceRow.SettingDetailsHelp,
            $"Default: 160{Environment.NewLine}Runtime path:");
        Assert.IsFalse(interfaceRow.SettingDetailsHelp.Contains("Current value:", StringComparison.Ordinal));
        interfaceRow.NumericText = "120";
        Assert.IsTrue(interfaceRow.DraftHasOverride);
        StringAssert.Contains(interfaceRow.SettingDetailsHelp, "Default: 160");
        StringAssert.Contains(interfaceRow.RevertDraftAutomationHelp, "Default: 160");
        StringAssert.Contains(interfaceRow.RevertDraftAutomationHelp, interfaceRow.Description);

        fixture.Select(LauncherSettingsSection.Graphics);
        var graphicsRow = fixture.Row("graphics.default_system_zoom");
        Assert.AreEqual(
            "Setting details for Default system zoom",
            graphicsRow.SettingDetailsAutomationName);
        StringAssert.Contains(graphicsRow.SettingDetailsHelp, "Default: 1750");

        var booleanRow = fixture.Row("graphics.free_resize");
        StringAssert.Contains(booleanRow.SettingDetailsHelp, "Default:");
        Assert.IsFalse(booleanRow.SettingDetailsHelp.Contains("Current value:", StringComparison.Ordinal));

        fixture.ViewModel.SearchText = "config.assets_url_override";
        var blankDefaultRow = fixture.Row("config.assets_url_override");
        StringAssert.Contains(
            blankDefaultRow.SettingDetailsHelp,
            $"Default: (blank){Environment.NewLine}Runtime path: config.assets_url_override");
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
    public async Task ExistingShortcutDiagnosticsDoNotBlockAnUnrelatedSparseSave()
    {
        const string source =
            "# preserve the player's existing shortcut diagnostics\r\n"
            + "[graphics]\r\n"
            + "free_resize = true\r\n"
            + "\r\n"
            + "[shortcuts]\r\n"
            + "action_primary = \"MOUSE1\"\r\n"
            + "action_queue = \"MOUSE4|MOUSE1\"\r\n"
            + "action_queue_clear = \"CTRL-C|MOUSE3\"\r\n"
            + "action_repair = \"MOUSE3\"\r\n"
            + "zoom_in = \"EQUAL\"\r\n"
            + "\r\n"
            + "[custom]\r\n"
            + "keep = \"verbatim\"\r\n";
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        using var fixture = SettingsFixture.Create(
            source,
            LoadNetniVStableCatalog(),
            encoding: encoding);

        fixture.Select(LauncherSettingsSection.Hotkeys);
        var primary = fixture.Row("shortcuts.action_primary");
        var zoomIn = fixture.Row("shortcuts.zoom_in");
        Assert.IsTrue(primary.KeybindingNeedsAttention);
        StringAssert.Contains(primary.KeybindingValidationMessage, "conflicts with");
        Assert.IsTrue(zoomIn.KeybindingNeedsAttention);
        StringAssert.Contains(zoomIn.KeybindingValidationMessage, "configured shortcut is invalid");
        Assert.IsFalse(
            fixture.ViewModel.HasInvalidInput,
            "Pre-existing shortcut diagnostics must remain visible without becoming staged-input blockers.");

        fixture.Select(LauncherSettingsSection.Graphics);
        fixture.Row("graphics.free_resize").BooleanValue = false;

        Assert.IsTrue(fixture.ViewModel.CanSave, fixture.ViewModel.SaveAvailability);
        fixture.ViewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => !fixture.ViewModel.HasPendingChanges);

        var expectedText = source.Replace(
            "free_resize = true",
            "free_resize = false",
            StringComparison.Ordinal);
        var expectedBytes = encoding.GetPreamble().Concat(encoding.GetBytes(expectedText)).ToArray();
        CollectionAssert.AreEqual(expectedBytes, await File.ReadAllBytesAsync(fixture.ConfigurationPath));
        Assert.IsFalse(fixture.ViewModel.HasInvalidInput);
    }

    [TestMethod]
    public void NetnivSemanticNavigationOmitsEmptySections()
    {
        using var fixture = SettingsFixture.Create(catalog: LoadNetniVStableCatalog());

        CollectionAssert.AreEqual(
            NetnivNavigationSections,
            fixture.ViewModel.Sections.Select(section => section.Id).ToArray());
        Assert.AreEqual("General", fixture.ViewModel.WorkspaceTitle);
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
        Assert.AreSame(fixture.ViewModel.FilteredSettings[^1], lockedGate);
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
    public async Task MissingTomlRemainsEditableAndIsCreatedOnlyAfterExplicitSave()
    {
        using var fixture = SettingsFixture.Create();
        File.Delete(fixture.ConfigurationPath);
        var viewModel = fixture.CreateAdditionalViewModel();
        SettingsFixture.Select(viewModel, LauncherSettingsSection.Graphics);
        var setting = fixture.Catalog.VisibleSettings.Single(
            item => item.Path == "graphics.free_resize");
        var row = viewModel.FilteredSettings
            .OfType<SettingsRowViewModel>()
            .Single(item => item.Path == setting.Path);

        Assert.IsTrue(viewModel.IsConfigurationReady);
        StringAssert.Contains(viewModel.ConfigurationStatus, "first saved change");
        Assert.IsFalse(File.Exists(fixture.ConfigurationPath));

        row.BooleanValue = !row.BooleanValue;
        Assert.IsTrue(viewModel.CanSave, viewModel.SaveAvailability);
        Assert.IsFalse(File.Exists(fixture.ConfigurationPath));
        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.HasPendingChanges);

        Assert.IsTrue(File.Exists(fixture.ConfigurationPath));
        StringAssert.Contains(
            await File.ReadAllTextAsync(fixture.ConfigurationPath),
            "free_resize =");
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
        StringAssert.Contains(row.SettingDetailsHelp, alias.Path);
        StringAssert.Contains(row.SettingDetailsHelp, "Unknown");
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
        StringAssert.Contains(row.SettingDetailsHelp, "Canonical precedence");

        fixture.ViewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => !fixture.ViewModel.HasPendingChanges);
        var persisted = File.ReadAllText(fixture.ConfigurationPath);
        StringAssert.Contains(persisted, "# preserve me");
        StringAssert.Contains(persisted, "arrived_in_system = { system = false, audio = true");
        StringAssert.Contains(persisted, "fleet_arrived_in_system = true");
        StringAssert.Contains(persisted, "[custom]\nkeep = \"verbatim\"");
    }

    [TestMethod]
    public async Task SharedSettingsInvalidationDiscardsAndMakesRetainedWorkspaceInert()
    {
        const string source = "[graphics]\nfree_resize = true\n";
        using var fixture = SettingsFixture.Create(source);
        var replacement = fixture.CreateAdditionalViewModel();
        var instances = new Queue<SettingsViewModel>([fixture.ViewModel, replacement]);
        var owner = new LauncherSettingsWorkspace(() => instances.Dequeue());
        LauncherSettingsInvalidatedEventArgs? invalidation = null;
        owner.Invalidated += (_, value) => invalidation = value;
        var retained = owner.GetOrCreate();
        fixture.Select(LauncherSettingsSection.Graphics);
        fixture.Row("graphics.free_resize").BooleanValue = false;
        Assert.IsTrue(owner.HasPendingChanges);

        await owner.InvalidateAsync(LauncherSettingsInvalidationReason.RuntimeActivationChanged);

        Assert.IsFalse(owner.HasPendingChanges);
        Assert.IsFalse(retained.HasPendingChanges);
        Assert.IsFalse(retained.CanSave);
        Assert.IsFalse(retained.SyncWorkspace.CanSave);
        retained.ReloadConfiguration();
        retained.SyncWorkspace.Reload();
        Assert.IsFalse(retained.IsConfigurationReady);
        Assert.IsFalse(retained.SyncWorkspace.IsConfigurationReady);
        Assert.AreSame(retained, invalidation?.Workspace);
        Assert.AreEqual(LauncherSettingsInvalidationReason.RuntimeActivationChanged, invalidation?.Reason);
        Assert.AreSame(replacement, owner.GetOrCreate());
        Assert.AreEqual(source, File.ReadAllText(fixture.ConfigurationPath));
    }

    [TestMethod]
    public void SharedSettingsCompositionReturnsOneInstanceAcrossConcurrentConsumers()
    {
        using var fixture = SettingsFixture.Create("[graphics]\nfree_resize = true\n");
        var factoryCalls = 0;
        var owner = new LauncherSettingsWorkspace(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            return fixture.ViewModel;
        });
        var resolved = new SettingsViewModel[16];

        Parallel.For(0, resolved.Length, index => resolved[index] = owner.GetOrCreate());

        Assert.AreEqual(1, factoryCalls);
        Assert.IsTrue(resolved.All(item => ReferenceEquals(fixture.ViewModel, item)));
    }

    [TestMethod]
    public async Task SharedSettingsInvalidationWaitsForPausedSettingsSaveBeforeReplacement()
    {
        const string source = "[graphics]\nfree_resize = true\n";
        var pause = new PausedAtomicSave();
        var repository = new TomlConfigurationRepository(new AtomicTomlStore(pause.BeforeReplaceAsync));
        using var fixture = SettingsFixture.Create(source, repository: repository);
        var replacement = fixture.CreateAdditionalViewModel();
        var instances = new Queue<SettingsViewModel>([fixture.ViewModel, replacement]);
        var owner = new LauncherSettingsWorkspace(() => instances.Dequeue());
        var retained = owner.GetOrCreate();
        fixture.Select(LauncherSettingsSection.Graphics);
        fixture.Row("graphics.free_resize").BooleanValue = false;
        var activeSave = retained.SaveAsync();
        await pause.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var repeatedSave = retained.SaveAsync();

        Assert.AreSame(activeSave, repeatedSave);
        Assert.IsFalse(repeatedSave.IsCompleted);

        var invalidation = owner.InvalidateAsync(LauncherSettingsInvalidationReason.RuntimeActivationChanged);

        Assert.IsFalse(invalidation.IsCompleted);
        Assert.IsFalse(retained.IsConfigurationReady);
        Assert.IsFalse(retained.CanSave);
        Assert.ThrowsException<InvalidOperationException>(() => owner.GetOrCreate());
        fixture.Row("graphics.free_resize").BooleanValue = true;
        retained.SaveCommand.Execute(null);
        pause.Release();
        await Task.WhenAll(activeSave, repeatedSave, invalidation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, pause.SaveCount);
        StringAssert.Contains(File.ReadAllText(fixture.ConfigurationPath), "free_resize = false");
        Assert.IsFalse(retained.HasPendingChanges);
        Assert.AreSame(replacement, owner.GetOrCreate());
    }

    [TestMethod]
    public async Task SharedSettingsInvalidationWaitsForPausedDataSyncSaveBeforeReplacement()
    {
        const string source = "[sync]\njobs = true\n";
        var pause = new PausedAtomicSave();
        var repository = new TomlConfigurationRepository(new AtomicTomlStore(pause.BeforeReplaceAsync));
        using var fixture = SettingsFixture.Create(source, repository: repository);
        var replacement = fixture.CreateAdditionalViewModel();
        var instances = new Queue<SettingsViewModel>([fixture.ViewModel, replacement]);
        var owner = new LauncherSettingsWorkspace(() => instances.Dequeue());
        var retained = owner.GetOrCreate();
        var jobs = retained.SyncWorkspace.GlobalFeeds.Single(feed => feed.Label == "Jobs");
        jobs.IsEnabled = false;
        var activeSave = retained.SyncWorkspace.SaveAsync();
        await pause.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var repeatedSave = retained.SyncWorkspace.SaveAsync();

        Assert.AreSame(activeSave, repeatedSave);
        Assert.IsFalse(repeatedSave.IsCompleted);

        var invalidation = owner.InvalidateAsync(LauncherSettingsInvalidationReason.RuntimeActivationChanged);

        Assert.IsFalse(invalidation.IsCompleted);
        Assert.IsFalse(retained.SyncWorkspace.IsConfigurationReady);
        Assert.IsFalse(retained.SyncWorkspace.CanSave);
        Assert.ThrowsException<InvalidOperationException>(() => owner.GetOrCreate());
        jobs.IsEnabled = true;
        retained.SyncWorkspace.SaveCommand.Execute(null);
        pause.Release();
        await Task.WhenAll(activeSave, repeatedSave, invalidation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, pause.SaveCount);
        StringAssert.Contains(File.ReadAllText(fixture.ConfigurationPath), "jobs = false");
        Assert.IsFalse(retained.SyncWorkspace.HasPendingChanges);
        Assert.AreSame(replacement, owner.GetOrCreate());
    }

    [TestMethod]
    public async Task OverlappingInvalidationWaitsForFailingPausedSaveAndStillAllowsOneReplacement()
    {
        const string source = "[graphics]\nfree_resize = true\n";
        var pause = new PausedAtomicSave(failAfterRelease: true);
        var repository = new TomlConfigurationRepository(new AtomicTomlStore(pause.BeforeReplaceAsync));
        using var fixture = SettingsFixture.Create(source, repository: repository);
        var replacement = fixture.CreateAdditionalViewModel();
        var instances = new Queue<SettingsViewModel>([fixture.ViewModel, replacement]);
        var owner = new LauncherSettingsWorkspace(() => instances.Dequeue());
        fixture.Select(LauncherSettingsSection.Graphics);
        fixture.Row("graphics.free_resize").BooleanValue = false;
        owner.GetOrCreate().SaveCommand.Execute(null);
        await pause.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var first = owner.InvalidateAsync(LauncherSettingsInvalidationReason.RuntimeActivationChanged);
        var overlapping = owner.InvalidateAsync(LauncherSettingsInvalidationReason.RuntimeActivationChanged);

        Assert.AreSame(first, overlapping);
        Assert.IsNull(owner.Current);
        Assert.ThrowsException<InvalidOperationException>(() => owner.GetOrCreate());
        pause.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(source, File.ReadAllText(fixture.ConfigurationPath));
        Assert.AreSame(replacement, owner.GetOrCreate());
    }

    [TestMethod]
    public async Task ProviderSessionEndPermanentlyClosesRetainedSettingsOwner()
    {
        using var fixture = SettingsFixture.Create("[graphics]\nfree_resize = true\n");
        var owner = new LauncherSettingsWorkspace(() => fixture.ViewModel);
        _ = owner.GetOrCreate();

        await owner.InvalidateAsync(LauncherSettingsInvalidationReason.ProviderSessionEnded);

        Assert.IsNull(owner.Current);
        Assert.ThrowsException<ObjectDisposedException>(() => owner.GetOrCreate());
        Assert.ThrowsException<ObjectDisposedException>(() => owner.GetOrCreate());
    }

    [TestMethod]
    public async Task SessionEndDuringPausedRuntimeInvalidationPermanentlyClosesOwner()
    {
        const string source = "[graphics]\nfree_resize = true\n";
        var pause = new PausedAtomicSave();
        var repository = new TomlConfigurationRepository(new AtomicTomlStore(pause.BeforeReplaceAsync));
        using var fixture = SettingsFixture.Create(source, repository: repository);
        var owner = new LauncherSettingsWorkspace(() => fixture.ViewModel);
        fixture.Select(LauncherSettingsSection.Graphics);
        fixture.Row("graphics.free_resize").BooleanValue = false;
        owner.GetOrCreate().SaveCommand.Execute(null);
        await pause.Started.WaitAsync(TimeSpan.FromSeconds(5));
        var runtimeInvalidation = owner.InvalidateAsync(
            LauncherSettingsInvalidationReason.RuntimeActivationChanged);

        var sessionEnd = owner.InvalidateAsync(LauncherSettingsInvalidationReason.ProviderSessionEnded);

        Assert.AreSame(runtimeInvalidation, sessionEnd);
        Assert.ThrowsException<ObjectDisposedException>(() => owner.GetOrCreate());
        pause.Release();
        await sessionEnd.WaitAsync(TimeSpan.FromSeconds(5));
        StringAssert.Contains(File.ReadAllText(fixture.ConfigurationPath), "free_resize = false");
        Assert.ThrowsException<ObjectDisposedException>(() => owner.GetOrCreate());
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

    private static LauncherConfigurationCatalog LoadNetniVStableCatalog()
    {
        var path = FindRepositoryFile(
            "providers",
            "netniv",
            "configuration-schema-set.v1.json");
        using var stream = File.OpenRead(path);
        return LauncherConfigurationSchemaSetLoader.Load(
            stream,
            new(
                "netniv",
                "stable",
                "1.1.4",
                "d912611fa1eca49fc54f363bdf8377dfebf8def0"));
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
            string contents = "# disposable launcher projection fixture\n",
            LauncherConfigurationCatalog? catalog = null,
            IConfigurationRepository? repository = null,
            Encoding? encoding = null)
        {
            if (catalog is null)
            {
                using var schema = typeof(SettingsViewModel).Assembly
                    .GetManifestResourceStream(SchemaResource);
                Assert.IsNotNull(schema);
                catalog = LauncherConfigurationSchemaLoader.Load(schema);
            }
            var layout = new PrincipalCatalogSettingsLayoutProvider();
            var configurationPath = Path.Combine(
                Path.GetTempPath(),
                $"stfc-launcher-projection-{Guid.NewGuid():N}.toml");
            File.WriteAllText(
                configurationPath,
                contents,
                encoding ?? new UTF8Encoding(false));
            var command = new TestCommand();
            var viewModel = new SettingsViewModel(
                catalog,
                command,
                command,
                () => configurationPath,
                layout,
                new("Guffawaffle test", "Active", "Test fixture", layout.DisplayName),
                repository: repository);
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

    private sealed class PausedAtomicSave(bool failAfterRelease = false)
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public int SaveCount { get; private set; }

        public async ValueTask BeforeReplaceAsync(
            string temporaryPath,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            _ = temporaryPath;
            _ = destinationPath;
            SaveCount++;
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            if (failAfterRelease)
            {
                throw new IOException("Deterministic paused-save failure.");
            }
        }

        public void Release() => release.TrySetResult();
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
