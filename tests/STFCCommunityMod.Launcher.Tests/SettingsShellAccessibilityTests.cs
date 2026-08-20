using System.Xml.Linq;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class SettingsShellAccessibilityTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [TestMethod]
    public void SearchClearAndClosePublishDistinctUiAutomationContracts()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(Automation + "AutomationProperties.Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        CollectionAssert.Contains(names, "Open settings search");
        CollectionAssert.Contains(names, "Clear settings search query");
        CollectionAssert.Contains(names, "Close settings search");
    }

    [TestMethod]
    public void HelpFlyoutPublishesNameAndHelpToUiAutomation()
    {
        var document = LoadXaml(
            "src/STFCCommunityMod.Launcher/Controls/HelpFlyoutButton.xaml");
        var root = document.Root;
        Assert.IsNotNull(root);

        Assert.AreEqual(
            "{Binding AutomationName, ElementName=Root}",
            (string?)root.Attribute(Automation + "AutomationProperties.Name"));
        Assert.AreEqual(
            "{Binding HelpText, ElementName=Root}",
            (string?)root.Attribute(Automation + "AutomationProperties.HelpText"));
    }

    [TestMethod]
    public void OpenSearchReclaimsDecorativeTitleBarSpaceAtMinimumWidth()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");

        AssertSearchCollapseTrigger(document, "ProductTitleText");
        AssertSearchCollapseTrigger(document, "SettingsDiagnosticsTitleBarButton");
    }

    [TestMethod]
    public void HelpFlyoutClosesThroughItsUnloadContract()
    {
        var document = LoadXaml(
            "src/STFCCommunityMod.Launcher/Controls/HelpFlyoutButton.xaml");
        var root = document.Root;
        Assert.IsNotNull(root);

        Assert.AreEqual(
            "HelpFlyoutButton_Unloaded",
            (string?)root.Attribute("Unloaded"));
        var popup = document.Descendants(Presentation + "Popup").Single();
        Assert.AreEqual("True", (string?)popup.Attribute("StaysOpen"));
    }

    [TestMethod]
    public void HelpFlyoutAllowsPointerTransferIntoItsPopup()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/Controls/HelpFlyoutButton.xaml.cs"));

        StringAssert.Contains(source, "TimeSpan.FromMilliseconds(250)");
        StringAssert.Contains(source, "closeTimer.Stop();");
        StringAssert.Contains(source, "closeTimer.Start();");
    }

    [TestMethod]
    public void HelpFlyoutIsTransientAndClosesWhenItsWindowDeactivates()
    {
        var document = LoadXaml(
            "src/STFCCommunityMod.Launcher/Controls/HelpFlyoutButton.xaml");
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/Controls/HelpFlyoutButton.xaml.cs"));

        var root = document.Root;
        Assert.IsNotNull(root);
        Assert.AreEqual("HelpFlyoutButton_Loaded", (string?)root.Attribute("Loaded"));
        Assert.IsFalse(source.Contains("IsPinned", StringComparison.Ordinal));
        StringAssert.Contains(source, "previous.CloseFromPeer();");
        StringAssert.Contains(source, "ownerWindow.Deactivated += OwnerWindow_Deactivated;");
        StringAssert.Contains(source, "ownerWindow.Deactivated -= OwnerWindow_Deactivated;");
        StringAssert.Contains(source, "CloseFromPeer();");
    }

    [TestMethod]
    public void SettingRowsDoNotExposeDefaultAsAPeerAction()
    {
        var document = LoadXaml(
            "src/STFCCommunityMod.Launcher/Controls/SettingsRowActions.xaml");

        Assert.IsFalse(document.Descendants().Any(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName is "Text" or "Content"
                && attribute.Value.TrimStart('_').Equals("Default", StringComparison.OrdinalIgnoreCase))));
    }

    [TestMethod]
    public void PatchGatePublishesWarningStateAndKeyboardActions()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/Views/SettingsView.xaml");
        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(Automation + "AutomationProperties.Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        CollectionAssert.Contains(names, "Patch editing safety warning");
        CollectionAssert.Contains(names, "Enable patch editing");
        CollectionAssert.Contains(names, "Lock patch editing");
        CollectionAssert.Contains(names, "Read-only patch value summary");

        var accessTextValues = document.Descendants(Presentation + "AccessText")
            .Select(element => (string?)element.Attribute("Text"))
            .ToArray();
        CollectionAssert.Contains(accessTextValues, "_Enable patch editing");
        CollectionAssert.Contains(accessTextValues, "_Lock patch editing");

        var enable = document.Descendants(Presentation + "Button")
            .Single(
                element =>
                    (string?)element.Attribute(Automation + "AutomationProperties.Name")
                    == "Enable patch editing");
        Assert.AreEqual(
            "{Binding EnableAutomationHelp}",
            (string?)enable.Attribute(Automation + "AutomationProperties.HelpText"));
        Assert.AreEqual(
            "{Binding ElementName=PatchEditingWarning}",
            (string?)enable.Attribute(Automation + "AutomationProperties.LabeledBy"));
    }

    [TestMethod]
    public void SettingsFooterKeepsBlockedSaveReasonAndRecoveryVisibleAndAccessible()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/Views/SettingsView.xaml");
        var blocker = document.Descendants(Presentation + "TextBlock").Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "SettingsSaveBlockerText");
        var recovery = document.Descendants(Presentation + "Button").Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "SettingsSaveRecoveryButton");
        var settingsList = document.Descendants(Presentation + "ListBox").Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "SettingsList");
        var blockerBorder = blocker.Ancestors(Presentation + "Border").FirstOrDefault();

        Assert.IsNotNull(blockerBorder);
        Assert.AreEqual("{Binding SaveAvailability}", (string?)blocker.Attribute("Text"));
        Assert.AreEqual("Wrap", (string?)blocker.Attribute("TextWrapping"));
        Assert.AreEqual("Polite", (string?)blocker.Attribute(Automation + "AutomationProperties.LiveSetting"));
        Assert.AreEqual(
            "{Binding IsSaveBlocked, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)blockerBorder.Attribute("Visibility"));
        Assert.AreEqual("{Binding SaveRecoveryCommand}", (string?)recovery.Attribute("Command"));
        Assert.AreEqual(
            "{Binding SaveState.RecoveryActionLabel}",
            (string?)recovery.Attribute(Automation + "AutomationProperties.Name"));
        Assert.AreEqual(
            "{Binding SaveAvailability}",
            (string?)recovery.Attribute(Automation + "AutomationProperties.HelpText"));
        Assert.AreEqual("{Binding CanEdit}", (string?)settingsList.Attribute("IsEnabled"));
    }

    [TestMethod]
    public void DiagnosticsUsesDedicatedStructuredAccessibleWorkspace()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(Automation + "AutomationProperties.Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        CollectionAssert.Contains(names, "Mod Bridge Diagnostics workspace");
        CollectionAssert.Contains(names, "Re-run Diagnostics checks");
        CollectionAssert.Contains(names, "Open detected game folder");
        CollectionAssert.Contains(names, "Open community mod logs folder");
        CollectionAssert.Contains(names, "Copy the displayed redacted diagnostic summary");
        CollectionAssert.Contains(names, "Review eligible configuration cleanup");
        CollectionAssert.Contains(names, "Apply selected reviewed configuration cleanup");
        CollectionAssert.Contains(names, "Cancel configuration cleanup without changing the file");
        CollectionAssert.Contains(names, "Review local unredacted effective configuration export");
        CollectionAssert.Contains(names, "Confirm unredacted effective configuration export");
        CollectionAssert.Contains(names, "Show raw redacted diagnostic JSON");
        CollectionAssert.Contains(
            names,
            "Recover transaction. Recover the incomplete journaled community mod transaction.");
        CollectionAssert.Contains(names, "Retry exact reviewed candidate recovery");
        CollectionAssert.Contains(names, "Review removal of the Mod Bridge-managed community mod");
        CollectionAssert.Contains(
            names,
            "Review stopping Mod Bridge management for the selected installation");

        Assert.IsFalse(
            document.Descendants()
                .Any(element => (string?)element.Attribute(Xaml + "Name") == "DiagnosticsDialog"));
    }

    [TestMethod]
    public void StopManagingConfirmationNamesTargetAndPreservesStagedSettingsGuard()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var handler = Slice(
            source,
            "private void DiagnosticsStopManagingButton_Click",
            "private async void CheckLauncherUpdateButton_Click");
        var confirmation = Slice(
            source,
            "private void ShowMaintenanceConfirmation",
            "private async void ConfirmMaintenanceButton_Click");
        var dialog = document.Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "MaintenanceDialog");

        StringAssert.Contains(handler, "SharedSettings.HasPendingChanges");
        StringAssert.Contains(handler, "Save or discard");
        StringAssert.Contains(confirmation, "MaintenanceAction.StopManaging");
        StringAssert.Contains(confirmation, "ownership receipt for this exact installation");
        StringAssert.Contains(confirmation, "viewModel.IncompleteProviderSwitchGameDirectory");
        StringAssert.Contains(confirmation, "?? viewModel.SelectedGameDirectory");
        var dispatch = Slice(
            source,
            "private async void ConfirmMaintenanceButton_Click",
            "private async void ConfirmModOperationButton_Click");
        StringAssert.Contains(dispatch, "switch (action)");
        StringAssert.Contains(dispatch, "case MaintenanceAction.StopManaging:");
        StringAssert.Contains(dispatch, "default:");
        StringAssert.Contains(dispatch, "return;");
        Assert.IsTrue(dialog.Descendants(Presentation + "TextBlock").Any(element =>
            (string?)element.Attribute(Xaml + "Name") == "MaintenanceTarget"));
    }

    [TestMethod]
    public void PackagedUpdateHandoffKeepsBridgeOpenForDownloadGuidance()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var handler = Slice(
            source,
            "private async void CheckLauncherUpdateButton_Click",
            "private void ConfirmLauncherUpdateButton_Click");

        StringAssert.Contains(handler, "TryOpenPackagedLauncherUpdateSource");
        var feedback = document.Descendants(Presentation + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "{Binding LauncherUpdateFeedback}");
        Assert.AreEqual(
            "Mod Bridge update status",
            (string?)feedback.Attribute(Automation + "AutomationProperties.Name"));
        Assert.AreEqual(
            "Polite",
            (string?)feedback.Attribute(Automation + "AutomationProperties.LiveSetting"));
        Assert.AreEqual(
            "{Binding LauncherUpdateFeedback}",
            feedback.Attributes().Single(attribute => attribute.Name.LocalName == "LiveRegionBehavior.Announcement").Value);
        Assert.IsFalse(
            handler.Contains("Application.Current.Shutdown()", StringComparison.Ordinal),
            "Opening the supported packaged-update source must not close Bridge before the user opens the downloaded descriptor.");

        StringAssert.StartsWith(
            MainWindowViewModel.DescribeLauncherUpdateActionAutomationName(false, string.Empty),
            "Check Mod Bridge update");
        StringAssert.StartsWith(
            MainWindowViewModel.DescribeLauncherUpdateActionAutomationName(true, "Checking"),
            "Checking for Mod Bridge update…");
    }

    [TestMethod]
    public void CommunityModUpdateIsOneGuidedHomeActionSeparateFromBridgeUpdate()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var modAction = document.Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ModActionButton");
        var releaseSource = document.Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ReleaseSourceButton");
        var confirmation = document.Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ModOperationDialog");
        var prepareHandler = Slice(
            source,
            "private async void ModActionButton_Click",
            "private void DiagnosticsButton_Click");
        var folderHandler = Slice(
            source,
            "private void ChooseGameFolderButton_Click",
            "private async void ModActionButton_Click");
        var executeHandler = Slice(
            source,
            "private async void ConfirmModOperationButton_Click",
            "private void ReleaseSourceButton_Click");

        Assert.AreEqual("{Binding ModActionLabel}", (string?)modAction.Attribute("Content"));
        Assert.AreEqual(
            "{StaticResource UtilityActionButtonStyle}",
            (string?)modAction.Attribute("Style"));
        Assert.AreEqual(
            "CommunityModPrimaryAction",
            (string?)modAction.Attribute(Automation + "AutomationProperties.AutomationId"));
        Assert.AreEqual(
            "{Binding ModActionAutomationName}",
            (string?)modAction.Attribute(Automation + "AutomationProperties.Name"));
        Assert.AreEqual(
            "{Binding ModActionHelpText}",
            (string?)modAction.Attribute(Automation + "AutomationProperties.HelpText"));
        StringAssert.Contains(
            (string?)releaseSource.Attribute(Automation + "AutomationProperties.HelpText"),
            "community mod release source");
        Assert.IsFalse(
            ((string?)releaseSource.Attribute(Automation + "AutomationProperties.HelpText"))!
                .Contains("Mod Bridge update source", StringComparison.Ordinal));

        StringAssert.Contains(prepareHandler, "ModManagementActionKind.Recover");
        StringAssert.Contains(prepareHandler, "ShowMaintenanceConfirmation(MaintenanceAction.Recover");
        Assert.IsFalse(folderHandler.Contains("MaintenanceAction.Recover", StringComparison.Ordinal));
        StringAssert.Contains(prepareHandler, "PrepareModOperationAsync");
        StringAssert.Contains(prepareHandler, "ModOperationPreparationState.Ready");
        StringAssert.Contains(prepareHandler, "ModOperationSource.Text = viewModel.SelectedModReleaseSource");
        StringAssert.Contains(prepareHandler, "pendingModOperation.IsAdoptionOnly");
        StringAssert.Contains(prepareHandler, "Manage mod with Mod Bridge");
        StringAssert.Contains(prepareHandler, "AutomationProperties.SetName");
        StringAssert.Contains(prepareHandler, "ModOperationDialog.IsOpen = true");
        StringAssert.Contains(executeHandler, "ExecuteModOperationAsync");

        Assert.IsTrue(confirmation.Descendants(Presentation + "TextBlock").Any(element =>
            (string?)element.Attribute(Xaml + "Name") == "ModOperationSource"));
        var confirmationText = string.Join(
            " ",
            confirmation.Descendants(Presentation + "TextBlock")
                .Select(element => (string?)element.Attribute("Text"))
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        StringAssert.Contains(confirmationText, "your settings stay unchanged");
        StringAssert.Contains(confirmationText, "preserves its recovery information");
        StringAssert.Contains(confirmationText, "shows the available recovery action");
        Assert.IsFalse(confirmationText.Contains("failed operation restores", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IncompleteProviderSwitchRecoveryOverridesAnUnavailableOrdinaryProviderAction()
    {
        Assert.IsTrue(MainWindowViewModel.ResolveModActionAvailability(
            hasIncompleteProviderSwitch: true,
            isGameRunning: false,
            ordinaryActionCanExecute: false));
        Assert.IsFalse(MainWindowViewModel.ResolveModActionAvailability(
            hasIncompleteProviderSwitch: true,
            isGameRunning: true,
            ordinaryActionCanExecute: true));
        Assert.IsFalse(MainWindowViewModel.ResolveModActionAvailability(
            hasIncompleteProviderSwitch: false,
            isGameRunning: false,
            ordinaryActionCanExecute: false));
        Assert.IsFalse(MainWindowViewModel.ResolveModContextChangeAvailability(
            recoveryRequired: true,
            isModOperationInProgress: false,
            isLaunchInProgress: false));
        Assert.IsFalse(MainWindowViewModel.ResolveModContextChangeAvailability(
            recoveryRequired: false,
            isModOperationInProgress: true,
            isLaunchInProgress: false));
        Assert.IsTrue(MainWindowViewModel.ResolveModContextChangeAvailability(
            recoveryRequired: false,
            isModOperationInProgress: false,
            isLaunchInProgress: false));

        StringAssert.StartsWith(
            MainWindowViewModel.DescribeProviderSwitchRecoveryAvailability(true, true),
            "Close Star Trek Fleet Command");
        var artifactRecovery = MainWindowViewModel.DescribeProviderSwitchRecoveryAvailability(false, true);
        StringAssert.Contains(artifactRecovery, "version.dll");
        StringAssert.Contains(artifactRecovery, "provider selection");
        StringAssert.Contains(artifactRecovery, "exact TOML bytes");
        var configurationRecovery = MainWindowViewModel.DescribeProviderSwitchRecoveryAvailability(false, false);
        StringAssert.Contains(configurationRecovery, "no DLL change");

        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var gameFolderAction = document.Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute("Click") == "ChooseGameFolderButton_Click");
        Assert.AreEqual(
            "{Binding CanChangeGameFolder}",
            (string?)gameFolderAction.Attribute("IsEnabled"));

        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/ViewModels/MainWindowViewModel.cs"));
        var attachment = Slice(
            source,
            "internal LauncherProviderAtomicSwitchCoordinator? ProviderSwitchCoordinator",
            "internal LauncherFeatureRemediationCoordinator? FeatureRemediationCoordinator");
        var update = Slice(
            source,
            "private void UpdateModActionAvailability()",
            "private void UpdateLaunchActionAvailability()");

        StringAssert.Contains(attachment, "NotifyModPresentationChanged()");
        StringAssert.Contains(update, "ResolveModActionAvailability");
        StringAssert.Contains(update, "HasIncompleteProviderSwitch");
    }

    [TestMethod]
    public void AdoptionOnlyFeedbackKeepsTheManageModLanguageAfterConfirmation()
    {
        var preparation = new ModOperationPreparation(
            ModOperationPreparationState.Ready,
            "Ready",
            "game",
            "2.1.0-guffa.10",
            new(
                new Uri("https://example.invalid/version.dll"),
                "version.dll",
                42,
                new('A', 64),
                "2.1.0.0"),
            ExistingArtifactPolicy.AdoptAndPreserve,
            ModManagementActionKind.UpdateManualInstallation,
            "guffawaffle",
            IsAdoptionOnly: true);

        var accepted = MainWindowViewModel.ModOperationAcceptedMessage(preparation);
        var completed = MainWindowViewModel.ModOperationSucceededMessage(preparation);

        StringAssert.StartsWith(accepted, "Management accepted");
        Assert.IsFalse(accepted.Contains("Installing", StringComparison.Ordinal));
        StringAssert.Contains(completed, "now manages");
        StringAssert.Contains(completed, "previously installed file was preserved");
    }

    [TestMethod]
    public void RecoveryConfirmationAndCompletionStayStateSpecificAndReloadTheProviderWorkspace()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/ViewModels/MainWindowViewModel.cs"));
        var confirmation = Slice(
            source,
            "private void ShowMaintenanceConfirmation",
            "private async void ConfirmMaintenanceButton_Click");
        var execution = Slice(
            source,
            "private async void ConfirmMaintenanceButton_Click",
            "private async void ConfirmModOperationButton_Click");

        StringAssert.Contains(confirmation, "no DLL change was part of this switch");
        StringAssert.Contains(confirmation, "IncompleteProviderSwitchGameDirectory");
        StringAssert.Contains(confirmation, "Recover community mod transaction");
        StringAssert.Contains(confirmation, "Remove mod managed by Mod Bridge");
        StringAssert.Contains(confirmation, "Stop managing this installation");
        StringAssert.Contains(execution, "SharedSettings.HasPendingChanges");
        StringAssert.Contains(execution, "IncompleteProviderSwitchSourceSelection");
        StringAssert.Contains(execution, "BeginRecoveryWorkspaceTransition()");
        StringAssert.Contains(execution, "SetSettingsWorkspaceOpen(false)");
        StringAssert.Contains(execution, "EndRecoveryWorkspaceTransition()");
        StringAssert.Contains(execution, "restoredProviderSelection is not null");
        StringAssert.Contains(execution, "RecomposeAfterSuccessfulRecovery(");
        Assert.IsTrue(
            execution.IndexOf("BeginRecoveryWorkspaceTransition()", StringComparison.Ordinal)
                < execution.IndexOf("await viewModel.RecoverModAsync", StringComparison.Ordinal));
        var recomposition = Slice(
            source,
            "private void RecomposeAfterSuccessfulRecovery",
            "private async void ConfirmModOperationButton_Click");
        StringAssert.Contains(recomposition, "providerSessions.Recompose(restoredProviderSelection)");
        StringAssert.Contains(recomposition, "ApplyProviderSession(session)");
        StringAssert.Contains(recomposition, "ReportRecoveryCompletion(changed, recoveryMessage)");
        StringAssert.Contains(viewModelSource, "catch (LauncherProviderSwitchJournalException)");
        StringAssert.Contains(viewModelSource, "saved recovery details are damaged");
        StringAssert.Contains(viewModelSource, "Do not retry recovery until those details are repaired");
        StringAssert.Contains(viewModelSource, "export a diagnostic report");
        StringAssert.Contains(viewModelSource, "share it when asking for help");
    }

    [TestMethod]
    public void ConfigurationCleanupAndEffectiveExportKeepDistinctSafetyContracts()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var cleanup = document.Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ConfigurationCleanupDialog");
        var export = document.Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "EffectiveConfigurationExportDialog");
        var cleanupText = string.Join(" ", cleanup.Descendants().Attributes("Text").Select(attribute => attribute.Value));
        var exportText = string.Join(" ", export.Descendants().Attributes("Text").Select(attribute => attribute.Value));
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));

        StringAssert.Contains(source, "unknown content and values remain untouched");
        StringAssert.Contains(cleanupText, "creates and verifies a protected provider-scoped backup");
        StringAssert.Contains(cleanupText, "Mod Bridge does not restart");
        StringAssert.Contains(exportText, "intentionally unredacted");
        StringAssert.Contains(exportText, "Nothing is uploaded automatically");
        Assert.IsTrue(cleanup.Descendants(Presentation + "Button").Any(button =>
            (string?)button.Attribute("Click") == "CancelConfigurationCleanupButton_Click"));
        Assert.IsTrue(cleanup.Descendants(Presentation + "Button").Any(button =>
            (string?)button.Attribute("Click") == "ConfirmConfigurationCleanupButton_Click"));
    }

    [TestMethod]
    public void DiagnosticTechnicalReportUsesOneWayBinding()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var report = document.Descendants(Presentation + "TextBox")
            .Single(element =>
                (string?)element.Attribute(Automation + "AutomationProperties.Name")
                    == "Exact redacted diagnostic JSON preview");

        Assert.AreEqual(
            "{Binding DiagnosticTechnicalReport, Mode=OneWay}",
            (string?)report.Attribute("Text"));
    }

    [TestMethod]
    public void DiagnosticStatusBadgesShareFixedCenteredGeometryAcrossWrappedRows()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var badgeStyle = document.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "DiagnosticsStatusBadgeStyle");
        var textStyle = document.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "DiagnosticsStatusBadgeTextStyle");
        var badge = document.Descendants(Presentation + "Border")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "DiagnosticStatusBadge");
        var statusText = document.Descendants(Presentation + "TextBlock")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "DiagnosticStatusText");
        var title = badge.Parent!
            .Elements(Presentation + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "{Binding Name}");

        AssertStyleSetter(badgeStyle, "Width", "112");
        AssertStyleSetter(badgeStyle, "Height", "24");
        AssertStyleSetter(badgeStyle, "Padding", "8,0");
        AssertStyleSetter(badgeStyle, "VerticalAlignment", "Center");
        AssertStyleSetter(textStyle, "HorizontalAlignment", "Stretch");
        AssertStyleSetter(textStyle, "VerticalAlignment", "Center");
        AssertStyleSetter(textStyle, "LineHeight", "16");
        AssertStyleSetter(textStyle, "LineStackingStrategy", "BlockLineHeight");
        AssertStyleSetter(textStyle, "TextAlignment", "Center");

        Assert.AreEqual(
            "{StaticResource DiagnosticsStatusBadgeStyle}",
            (string?)badge.Attribute("Style"));
        Assert.AreEqual(
            "{StaticResource DiagnosticsStatusBadgeTextStyle}",
            (string?)statusText.Attribute("Style"));
        Assert.AreEqual("Wrap", (string?)title.Attribute("TextWrapping"));

        var geometryProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "Height",
            "HorizontalAlignment",
            "Margin",
            "MinHeight",
            "MinWidth",
            "Padding",
            "VerticalAlignment",
            "Width",
        };
        Assert.IsFalse(
            textStyle.Descendants(Presentation + "DataTrigger")
                .Descendants(Presentation + "Setter")
                .Any(setter => geometryProperties.Contains((string?)setter.Attribute("Property") ?? string.Empty)),
            "Semantic status triggers may change color, not badge geometry.");
    }

    [TestMethod]
    public void SettingsRequiresPresentModButNotHealthyModProvenance()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var method = Slice(
            source,
            "private bool EnsureSettingsWorkspaceInitialized()",
            "private SettingsViewModel CreateSettingsViewModel");

        StringAssert.Contains(method, "HasUnsafeModDeploymentTransaction");
        StringAssert.Contains(method, "Path.Combine(viewModel.SelectedGameDirectory, \"version.dll\")");
        StringAssert.Contains(method, "Community Mod is not installed");
        Assert.IsTrue(
            method.IndexOf("version.dll", StringComparison.Ordinal)
            < method.IndexOf("if (isSettingsWorkspaceInitialized)", StringComparison.Ordinal),
            "An initialized Settings workspace must not bypass a later mod removal.");
        Assert.IsFalse(
            method.Contains("ManagedVerified", StringComparison.Ordinal),
            "A present proxy must not require managed provenance before Settings can open.");
    }

    private static void AssertSearchCollapseTrigger(XDocument document, string elementName)
    {
        var element = document.Descendants()
            .Single(item => (string?)item.Attribute(Xaml + "Name") == elementName);
        var trigger = element.Descendants(Presentation + "DataTrigger")
            .SingleOrDefault(
                item =>
                    (string?)item.Attribute("Binding")
                        == "{Binding DataContext.IsSearchVisible, ElementName=SettingsWorkspace}"
                    && (string?)item.Attribute("Value") == "True");

        Assert.IsNotNull(trigger, $"{elementName} must react to open settings search.");
        Assert.IsTrue(
            trigger.Elements(Presentation + "Setter").Any(
                setter =>
                    (string?)setter.Attribute("Property") == "Visibility"
                    && (string?)setter.Attribute("Value") == "Collapsed"),
            $"{elementName} must collapse while settings search is open.");
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        return source[start..end];
    }

    private static void AssertStyleSetter(XElement style, string property, string value)
    {
        Assert.IsTrue(
            style.Elements(Presentation + "Setter").Any(
                setter =>
                    (string?)setter.Attribute("Property") == property
                    && (string?)setter.Attribute("Value") == value),
            $"The shared style must set {property} to {value}.");
    }

    private static XDocument LoadXaml(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate the launcher repository root.");
        return XDocument.Load(Path.Combine(directory.FullName, relativePath));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the launcher repository root.");
    }
}
