using System.Xml.Linq;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ProviderSourceDialogTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [TestMethod]
    public void ProviderPresentationUsesCatalogCopyWithoutInternalCapabilityIds()
    {
        var catalog = BundledLauncherProviderCatalog.Load();

        foreach (var provider in catalog.Providers.Values)
        {
            var description = LauncherProviderPresentation.Describe(provider);

            StringAssert.Contains(description, provider.DefaultReleaseChannel.DisplayName);
            StringAssert.Contains(description, provider.Description);
            Assert.IsFalse(description.Contains("capabilities supported", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(description.Contains(provider.GetType().FullName!, StringComparison.Ordinal));
            foreach (var capabilityId in LauncherProviderCapabilityIds.ContractCapabilities)
            {
                Assert.IsFalse(description.Contains(capabilityId, StringComparison.Ordinal));
            }
        }
    }

    [TestMethod]
    public void ProviderSelectorProjectsDisplayNameAndHasOneDismissalAction()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var dialog = document.Descendants()
            .Single(element => GetName(element) == "ProviderSwitchDialog");
        var selector = dialog.Descendants(Presentation + "ComboBox")
            .Single(element => GetName(element) == "ProviderSourceSelector");
        var displayBinding = selector.Descendants(Presentation + "TextBlock")
            .SelectMany(element => element.Attributes())
            .Single(attribute => attribute.Name.LocalName == "Text");

        Assert.AreEqual("Id", (string?)selector.Attribute("SelectedValuePath"));
        Assert.AreEqual("{Binding DisplayName}", displayBinding.Value);
        Assert.IsFalse(dialog.Descendants(Presentation + "Button")
            .Any(button => string.Equals((string?)button.Attribute("Content"), "_Cancel", StringComparison.Ordinal)));
        Assert.IsNull(dialog.Elements().Single().Attribute("Width"));
        Assert.IsFalse(dialog.Value.Contains("restart Mod Bridge", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(document.Descendants(Presentation + "Button")
            .Any(button => GetName(button) == "RetryProviderRecompositionButton"));
    }

    [TestMethod]
    public void ProviderReviewHasBoundedScrollingAndAStickySingleAction()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var dialog = document.Descendants()
            .Single(element => GetName(element) == "ProviderSwitchDialog");
        var contentGrid = dialog.Elements(Presentation + "Grid").Single();
        var scroller = contentGrid.Elements(Presentation + "ScrollViewer").Single();
        var action = contentGrid.Elements(Presentation + "Button")
            .Single(button => GetName(button) == "ProviderSwitchActionButton");

        Assert.IsNotNull(contentGrid.Attribute("MaxHeight"));
        Assert.AreEqual("Auto", (string?)scroller.Attribute("VerticalScrollBarVisibility"));
        Assert.AreEqual("0", (string?)scroller.Attribute("Grid.Row"));
        Assert.AreEqual("1", (string?)action.Attribute("Grid.Row"));
        Assert.AreEqual("Switch community mod source", AutomationName(action));
        Assert.IsFalse(dialog.Descendants(Presentation + "TextBox").Any());
        Assert.AreEqual(1, dialog.Descendants(Presentation + "Button").Count());
    }

    [TestMethod]
    public void PreparedReviewAllowsRetargetingAndSelectionInvalidatesThePreview()
    {
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var selectionHandler = Slice(
            source,
            "private void ProviderSourceSelector_SelectionChanged",
            "private async void ProviderSwitchActionButton_Click");
        var reviewBranch = Slice(
            source,
            "if (review.RequiresReview)",
            "operationWasPrepared = true;");

        StringAssert.Contains(selectionHandler, "pendingProviderSwitch = null;");
        StringAssert.Contains(reviewBranch, "ProviderSourceSelector.IsEnabled = true;");
        StringAssert.Contains(reviewBranch, "SetProviderSwitchAction(\"Switch\"");
    }

    [TestMethod]
    public void OpeningProviderDialogResetsVisibleAndAutomationActionLabels()
    {
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var openHandler = Slice(
            source,
            "private void ReleaseSourceButton_Click",
            "private void ProviderSourceSelector_SelectionChanged");

        StringAssert.Contains(
            openHandler,
            "SetProviderSwitchAction(\"Switch\", targetProvider: null, enabled: false);");
        Assert.IsFalse(openHandler.Contains(
            "ProviderSwitchActionButton.IsEnabled = false",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void StagedChangesKeepTheContextualActionRetryable()
    {
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var actionHandler = Slice(
            source,
            "private async void ProviderSwitchActionButton_Click",
            "private void ResetProviderSwitchReviewControls");
        var stagedChangesGuard = Slice(
            actionHandler,
            "if (SharedSettings.HasPendingChanges)",
            "if (DataContext is not MainWindowViewModel viewModel");

        StringAssert.Contains(stagedChangesGuard, "if (SharedSettings.HasPendingChanges)");
        StringAssert.Contains(stagedChangesGuard, "SetProviderSwitchAction(");
        StringAssert.Contains(stagedChangesGuard, "enabled: true");
        Assert.IsFalse(stagedChangesGuard.Contains(
            "ProviderSwitchActionButton.IsEnabled = false",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void RuntimeEvidenceRefreshCopyDoesNotClaimAnInFlightSaveLeftTheFileUnchanged()
    {
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var refresh = Slice(
            source,
            "private async Task RefreshRuntimeCompositionConsumersAsync",
            "private void ShowProviderRecompositionFailure");

        StringAssert.Contains(refresh, "waited for any active save to finish");
        StringAssert.Contains(refresh, "Review the saved Settings before continuing");
        Assert.IsFalse(refresh.Contains("the saved TOML file was not changed", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RuntimeRevalidationAndAsyncRefreshShareTheBoundedRecoveryBoundary()
    {
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var handler = Slice(
            source,
            "private async void MainViewModel_PropertyChanged",
            "private async Task RefreshRuntimeCompositionConsumersAsync");
        var tryIndex = handler.IndexOf("try", StringComparison.Ordinal);
        var revalidateIndex = handler.IndexOf(
            "ProviderSession.ApplicationComposition.RevalidateHomes();",
            StringComparison.Ordinal);
        var refreshIndex = handler.IndexOf(
            "await RefreshRuntimeCompositionConsumersAsync();",
            StringComparison.Ordinal);
        var catchIndex = handler.IndexOf("catch (Exception exception)", StringComparison.Ordinal);

        Assert.IsTrue(tryIndex >= 0 && tryIndex < revalidateIndex);
        Assert.IsTrue(revalidateIndex < refreshIndex && refreshIndex < catchIndex);
        StringAssert.Contains(handler, "ShowProviderRecompositionFailure(exception);");
    }

    [TestMethod]
    public void SuccessfulExecutionConsumesPreviewBeforeRecompositionAndFinalizer()
    {
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var actionHandler = Slice(
            source,
            "private async void ProviderSwitchActionButton_Click",
            "private void ResetProviderSwitchReviewControls");
        var executeIndex = actionHandler.IndexOf(
            "var result = await providerSourceSwitchCoordinator.ExecuteAsync(",
            StringComparison.Ordinal);
        var consumeIndex = actionHandler.IndexOf(
            "pendingProviderSwitch = null;",
            executeIndex,
            StringComparison.Ordinal);
        var recomposeIndex = actionHandler.IndexOf(
            "providerSessions.Recompose(result.Selection)",
            StringComparison.Ordinal);
        var selectedValueIndex = actionHandler.IndexOf(
            "ProviderSourceSelector.SelectedValue = selectedProvider.Id;",
            recomposeIndex,
            StringComparison.Ordinal);
        var canonicalActionIndex = actionHandler.IndexOf(
            "SetProviderSwitchAction(\"Switch\", targetProvider: null, enabled: false);",
            selectedValueIndex,
            StringComparison.Ordinal);
        var capabilityUpdateIndex = actionHandler.IndexOf(
            "UpdateProviderCapabilityText();",
            canonicalActionIndex,
            StringComparison.Ordinal);
        var finalizerIndex = actionHandler.IndexOf("finally", StringComparison.Ordinal);

        Assert.IsTrue(executeIndex >= 0);
        Assert.IsTrue(consumeIndex > executeIndex);
        Assert.IsTrue(recomposeIndex > consumeIndex);
        Assert.IsTrue(selectedValueIndex > recomposeIndex);
        Assert.IsTrue(canonicalActionIndex > selectedValueIndex);
        Assert.IsTrue(capabilityUpdateIndex > canonicalActionIndex);
        Assert.IsTrue(finalizerIndex > capabilityUpdateIndex);
        StringAssert.Contains(
            actionHandler[finalizerIndex..],
            "if (pendingProviderSwitch is not null)");
    }

    [TestMethod]
    public void ProviderReviewAcknowledgementIsCachedAndAdvancesOnlyAfterPersistence()
    {
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var constructor = Slice(
            source,
            "private MainWindow(",
            "private LauncherProviderSession CreateProviderSession");
        var reviewFlow = Slice(
            source,
            "private void ProviderSourceSelector_SelectionChanged",
            "private void SetProviderSwitchAction");
        var acknowledge = Slice(
            source,
            "private void AcknowledgeProviderSwitchReview",
            "private void UpdateProviderCapabilityText");
        var loadIndex = acknowledge.IndexOf("var preferences = uiPreferencesStore.Load();", StringComparison.Ordinal);
        var saveIndex = acknowledge.IndexOf("uiPreferencesStore.Save(", StringComparison.Ordinal);
        var cacheIndex = acknowledge.IndexOf("providerSwitchReviewAcknowledged = true;", StringComparison.Ordinal);

        StringAssert.Contains(constructor, "var initialPreferences = uiPreferencesStore.Load();");
        StringAssert.Contains(
            constructor,
            "providerSwitchReviewAcknowledged = initialPreferences.ProviderSwitchReviewAcknowledged;");
        Assert.IsFalse(reviewFlow.Contains("uiPreferencesStore.Load()", StringComparison.Ordinal));
        Assert.IsTrue(loadIndex >= 0);
        Assert.IsTrue(saveIndex > loadIndex);
        Assert.IsTrue(cacheIndex > saveIndex);
        StringAssert.Contains(acknowledge, "or NotSupportedException");
    }

    [TestMethod]
    public void ProviderSwitchFlowNeverRequestsABridgeRestart()
    {
        var sources = new[]
        {
            "src/STFCCommunityMod.Launcher/MainWindow.xaml",
            "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs",
            "src/STFCCommunityMod.Launcher.Core/LauncherProviderSelection.cs",
            "src/STFCCommunityMod.Launcher.Core/LauncherProviderSwitchCoordinator.cs",
        };

        foreach (var source in sources)
        {
            var text = File.ReadAllText(Path.Combine(RepositoryRoot(), source));
            Assert.IsFalse(
                text.Contains("restart Mod Bridge", StringComparison.OrdinalIgnoreCase),
                $"Provider-switch source '{source}' must recompose in-process.");
        }
    }

    private static XDocument LoadXaml(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), relativePath));

    private static string? GetName(XElement element) =>
        element.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == "Name")?.Value;

    private static string? AutomationName(XElement element) =>
        element.Attributes().SingleOrDefault(
            attribute => attribute.Name.LocalName == "AutomationProperties.Name"
                && attribute.Name.NamespaceName.Contains("automation", StringComparison.OrdinalIgnoreCase))?.Value;

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(startIndex >= 0, $"Could not find source marker '{start}'.");
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(endIndex > startIndex, $"Could not find source marker '{end}'.");
        return source[startIndex..endIndex];
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "STFCCommunityMod.Launcher.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
