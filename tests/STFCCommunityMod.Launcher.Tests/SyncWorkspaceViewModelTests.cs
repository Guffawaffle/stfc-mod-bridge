using System.Text;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class SyncWorkspaceViewModelTests
{
    [TestMethod]
    public void OpeningExistingTopologyDoesNotMutateSourceOrRevealSavedToken()
    {
        using var fixture = SyncFixture.Create(
            """
            # source must remain byte-identical
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "vip-secret-value"
            """);
        var before = File.ReadAllBytes(fixture.Path);

        var viewModel = fixture.CreateViewModel();
        var target = viewModel.Targets.Single();

        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Path));
        Assert.AreEqual("community", target.Name);
        Assert.AreEqual("Saved token configured", target.TokenStatus);
        Assert.IsFalse(target.TokenStatus.Contains("vip-secret-value", StringComparison.Ordinal));
        Assert.IsFalse(target.ValidationSummary.Contains("vip-secret-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AddSurfaceOffersOnlyOrdinarySyncPresetsAndCustomDestination()
    {
        using var fixture = SyncFixture.Create("# empty\n");
        var viewModel = fixture.CreateViewModel();

        FinishWizard(
            viewModel,
            SyncTargetKind.LegacyCommunity,
            presetId: "spocks_club",
            identity: "spocksclub",
            endpoint: "https://spocks.example.invalid/sync");

        Assert.AreEqual(1, viewModel.Targets.Count);
        Assert.AreEqual("spocksclub", viewModel.Targets.Single().Name);
        Assert.AreEqual(string.Empty, viewModel.Targets.Single().KindLabel);
        viewModel.OpenAddDestinationCommand.Execute(null);
        Assert.IsTrue(viewModel.AddWizard!.Choices.All(choice => choice.Kind == SyncTargetKind.LegacyCommunity));
        Assert.IsTrue(viewModel.AddWizard.Choices.Any(choice => choice.Title == "Custom sync"));
        Assert.IsFalse(viewModel.AddWizard.Choices.Any(choice =>
            choice.Kind is SyncTargetKind.LocalSidecar or SyncTargetKind.MajelIngest));
        viewModel.AddWizard.CancelCommand.Execute(null);
    }

    [TestMethod]
    public void SidecarTabAppearsOnlyWhenSidecarExistsInToml()
    {
        using var ordinaryFixture = SyncFixture.Create(
            """
            [sync]
            jobs = true

            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """);
        using var sidecarFixture = SyncFixture.Create(
            """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"

            [sidecar.sync]
            enabled = true
            url = "http://127.0.0.1:43127/api/sidecar/ingest"
            token = "fixture-sidecar-secret"
            """);

        Assert.IsFalse(ordinaryFixture.CreateViewModel().Targets.Any(target => target.Name == "local-sidecar"));
        var sidecar = sidecarFixture.CreateViewModel().Targets.Single(target => target.Name == "local-sidecar");
        Assert.AreEqual("Sidecar", sidecar.KindLabel);
    }

    [TestMethod]
    public async Task SavingOrdinaryDefaultsDoesNotSynthesizeSidecarConfiguration()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync]
            jobs = true

            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """);
        var viewModel = fixture.CreateViewModel();
        viewModel.GlobalFeeds.Single(feed => feed.Label == "Jobs").IsEnabled = false;
        Assert.IsTrue(viewModel.CanSave, viewModel.SaveAvailability);

        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.HasPendingChanges);

        Assert.IsFalse(File.ReadAllText(fixture.Path).Contains("[sidecar.sync]", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SavingOrdinaryDefaultsPreservesExistingSidecarAndAdvancedMajelValues()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync]
            jobs = true

            [sidecar.sync]
            enabled = true
            url = "http://127.0.0.1:43127/api/sidecar/ingest"
            token = "fixture-sidecar-secret"
            fleet_runtime_mode = "request_only"
            future_sidecar_value = "preserve"

            [sync.targets.advanced]
            mode = "majel"
            url = "https://advanced.example.invalid/ingest"
            token = "fixture-majel-secret"
            battlelogs_realtime = true
            future_majel_value = "preserve"
            """);
        var viewModel = fixture.CreateViewModel();
        Assert.AreEqual(1, viewModel.Targets.Count);
        Assert.AreEqual("local-sidecar", viewModel.Targets.Single().Name);
        Assert.IsFalse(viewModel.Targets.Any(target => target.Name == "advanced"));
        viewModel.GlobalFeeds.Single(feed => feed.Label == "Jobs").IsEnabled = false;
        Assert.IsTrue(viewModel.CanSave, viewModel.SaveAvailability);

        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.HasPendingChanges);
        var updated = File.ReadAllText(fixture.Path);

        StringAssert.Contains(updated, "[sidecar.sync]");
        StringAssert.Contains(updated, "fleet_runtime_mode = \"request_only\"");
        StringAssert.Contains(updated, "future_sidecar_value = \"preserve\"");
        StringAssert.Contains(updated, "[sync.targets.advanced]");
        StringAssert.Contains(updated, "mode = \"majel\"");
        StringAssert.Contains(updated, "battlelogs_realtime = true");
        StringAssert.Contains(updated, "future_majel_value = \"preserve\"");
    }

    [TestMethod]
    public void FeedOverrideShowsEffectiveValueAndProvenance()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync]
            jobs = false

            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """);
        var target = fixture.CreateViewModel().Targets.Single();
        var jobs = target.Feeds.Single(feed => feed.Label == "Jobs");

        Assert.AreEqual(SyncBooleanOverrideChoice.UseGlobal, jobs.Choice);
        Assert.IsFalse(jobs.EffectiveEnabled);
        StringAssert.Contains(jobs.EffectiveSummary, "inherited");

        jobs.Choice = SyncBooleanOverrideChoice.Enabled;

        Assert.IsTrue(jobs.EffectiveEnabled);
        StringAssert.Contains(jobs.EffectiveSummary, "target override");
    }

    [TestMethod]
    public void SecretReplacementRequiresExplicitActionAndDisplayRemainsOpaque()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "old-secret"
            """);
        var viewModel = fixture.CreateViewModel();
        var target = viewModel.Targets.Single();
        target.SetReplacementToken("new-secret");

        Assert.IsFalse(viewModel.HasPendingChanges);
        target.ReplaceTokenCommand.Execute(null);

        Assert.IsTrue(viewModel.HasPendingChanges);
        Assert.AreEqual("Saved token configured", viewModel.Targets.Single().TokenStatus);
        Assert.IsFalse(viewModel.Targets.Single().TokenStatus.Contains("new-secret", StringComparison.Ordinal));
        Assert.IsFalse(File.ReadAllText(fixture.Path).Contains("new-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TargetOverridesRepresentInheritedExplicitFalseAndExplicitClear()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync]
            proxy = "http://global-proxy.example.invalid:8080"
            verify_ssl = true

            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """);
        var target = fixture.CreateViewModel().Targets.Single();

        StringAssert.Contains(target.ProxySummary, "Inherited");
        Assert.AreEqual(SyncBooleanOverrideChoice.UseGlobal, target.VerifySslChoice);

        target.ProxyChoice = SyncProxyOverrideChoice.NoProxy;
        target.VerifySslChoice = SyncBooleanOverrideChoice.Disabled;
        target.UnsafeTlsChoice = SyncBooleanOverrideChoice.Enabled;

        Assert.AreEqual("Explicitly cleared", target.ProxySummary);
        Assert.AreEqual(SyncBooleanOverrideChoice.Disabled, target.VerifySslChoice);
        Assert.AreEqual(SyncBooleanOverrideChoice.Enabled, target.UnsafeTlsChoice);
    }

    [TestMethod]
    public void ChoosingCustomProxyDoesNotPersistAPlaceholderValue()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """);
        var viewModel = fixture.CreateViewModel();
        var target = viewModel.Targets.Single();

        target.ProxyChoice = SyncProxyOverrideChoice.Custom;

        Assert.IsTrue(target.IsCustomProxy);
        Assert.IsFalse(viewModel.HasPendingChanges);
        Assert.AreEqual(string.Empty, target.ProxyText);
        StringAssert.Contains(target.ProxySummary, "Enter");

        target.ProxyText = "http://proxy.example.invalid:8080";

        Assert.IsTrue(viewModel.HasPendingChanges);
        Assert.AreEqual("http://proxy.example.invalid:8080", viewModel.Targets.Single().ProxyText);
    }

    [TestMethod]
    public void SidecarOnlyControlsAreExposedOnlyForSidecar()
    {
        using var fixture = SyncFixture.Create(
            """
            [sidecar.sync]
            enabled = true
            url = "http://127.0.0.1:43127/api/sidecar/ingest"
            token = "fixture-sidecar-secret"

            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-community-secret"
            """);
        var viewModel = fixture.CreateViewModel();
        var sidecar = viewModel.Targets.Single(target => target.Name == "local-sidecar");
        var community = viewModel.Targets.Single(target => target.Name == "community");

        Assert.IsTrue(sidecar.ShowTypeSpecificControls);
        Assert.IsFalse(community.ShowTypeSpecificControls);
        sidecar.BattlelogEnrichmentChoice = SyncBooleanOverrideChoice.Enabled;
        sidecar.FleetRuntimeModeChoice = "request_only";
        Assert.AreEqual(SyncBooleanOverrideChoice.Enabled, sidecar.BattlelogEnrichmentChoice);
        Assert.AreEqual("request_only", sidecar.FleetRuntimeModeChoice);
    }

    [TestMethod]
    public void PresetEndpointCannotProjectLegacyFeedsOntoSidecarKind()
    {
        using var fixture = SyncFixture.Create(
            """
            [sidecar.sync]
            enabled = false
            url = "https://spocks.club/sync/ingress/"
            token = "fixture-sidecar-secret"
            """);

        var sidecar = fixture.CreateViewModel().Targets.Single();

        Assert.AreEqual(SyncTargetKind.LocalSidecar, sidecar.Definition.Kind);
        CollectionAssert.AreEquivalent(
            SyncTargetTypeCatalog.Get(SyncTargetKind.LocalSidecar).SupportedDataKinds.ToArray(),
            sidecar.Feeds.Select(feed => feed.Kind).ToArray());
        Assert.IsFalse(sidecar.Feeds.Any(feed =>
            SyncTargetTypeCatalog.GetPreset("spocks_club").SupportedDataKinds.Contains(feed.Kind)));
    }

    [TestMethod]
    public void SiblingDraftBlocksSyncSaveWithoutDiscardingEitherDraft()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """);
        var siblingPending = false;
        var viewModel = fixture.CreateViewModel(() => siblingPending);
        viewModel.GlobalVerifySsl = false;
        viewModel.GlobalUnsafeTls = true;
        Assert.IsTrue(viewModel.CanSave);

        siblingPending = true;

        Assert.IsFalse(viewModel.CanSave);
        Assert.IsTrue(viewModel.HasPendingChanges);
        StringAssert.Contains(viewModel.SaveAvailability, "non-sync settings");
    }

    [TestMethod]
    public void LegacyRootEditRequiresExplicitMigrationConfirmation()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync]
            url = "https://community.example.invalid/sync"
            token = "fixture-legacy-secret"
            """);
        var viewModel = fixture.CreateViewModel();
        viewModel.Targets.Single().Url = "https://replacement.example.invalid/sync";

        Assert.IsTrue(viewModel.HasLegacyRootTarget);
        Assert.IsFalse(viewModel.CanSave);

        viewModel.MigrateLegacyRoot = true;

        Assert.IsTrue(viewModel.CanSave);
    }

    [TestMethod]
    public async Task SaveUsesAtomicWorkspaceAndReloadsCommittedTopology()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync]
            jobs = true

            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """);
        var viewModel = fixture.CreateViewModel();
        viewModel.GlobalFeeds.Single(feed => feed.Label == "Jobs").IsEnabled = false;

        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.HasPendingChanges);

        StringAssert.Contains(File.ReadAllText(fixture.Path), "jobs = false");
        Assert.IsTrue(File.Exists(fixture.Path + ".bak"));
        StringAssert.Contains(viewModel.OperationStatus, "Restart the game");
    }

    [TestMethod]
    public void CatalogSeparatesPersistenceKindsFromUiExposureAndProviderPresets()
    {
        Assert.AreEqual(
            "legacy,majel,sidecar",
            string.Join(',', SyncTargetTypeCatalog.All.Values.Select(type => type.Id).Order(StringComparer.Ordinal)));
        Assert.IsTrue(SyncTargetTypeCatalog.Presets.All(preset => preset.TargetKind == SyncTargetKind.LegacyCommunity));
        Assert.AreEqual(
            SyncTargetExposurePolicy.Creatable,
            SyncTargetTypeCatalog.Get(SyncTargetKind.LegacyCommunity).ExposurePolicy);
        Assert.AreEqual(
            SyncTargetExposurePolicy.Hidden,
            SyncTargetTypeCatalog.Get(SyncTargetKind.MajelIngest).ExposurePolicy);
        Assert.AreEqual(
            SyncTargetExposurePolicy.ExistingConfigurationOnly,
            SyncTargetTypeCatalog.Get(SyncTargetKind.LocalSidecar).ExposurePolicy);
        Assert.AreEqual(
            "next_spocks_club,spocks_club",
            string.Join(',', SyncTargetTypeCatalog.Presets.Select(preset => preset.Id).Order(StringComparer.Ordinal)));
    }

    [TestMethod]
    public void CancellingAddWizardLeavesTopologyUnchanged()
    {
        using var fixture = SyncFixture.Create("# empty\n");
        var viewModel = fixture.CreateViewModel();

        viewModel.OpenAddDestinationCommand.Execute(null);
        var wizard = viewModel.AddWizard!;
        wizard.SelectedChoice = wizard.Choices.Single(choice =>
            choice.Kind == SyncTargetKind.LegacyCommunity && choice.Preset is null);
        wizard.Endpoint = "https://custom.example.invalid/ingest";
        wizard.CancelCommand.Execute(null);

        Assert.IsFalse(viewModel.IsAddWizardOpen);
        Assert.IsFalse(viewModel.HasPendingChanges);
        Assert.AreEqual(0, viewModel.Targets.Count);
    }

    [TestMethod]
    public void InvalidWizardDestinationStaysInReviewWithoutMutatingTopology()
    {
        using var fixture = SyncFixture.Create("# empty\n");
        var viewModel = fixture.CreateViewModel();

        viewModel.OpenAddDestinationCommand.Execute(null);
        var wizard = viewModel.AddWizard!;
        wizard.SelectedChoice = wizard.Choices.Single(choice =>
            choice.Kind == SyncTargetKind.LegacyCommunity && choice.Preset is null);
        wizard.NextCommand.Execute(null);
        wizard.Identity = "invalid-endpoint";
        wizard.Endpoint = "not-a-valid-ingest-url";
        wizard.Token = "fixture-secret";
        wizard.NextCommand.Execute(null);
        wizard.FinishCommand.Execute(null);

        Assert.IsTrue(viewModel.IsAddWizardOpen);
        Assert.IsTrue(wizard.HasError);
        StringAssert.Contains(wizard.Error, "absolute HTTP or HTTPS");
        Assert.IsFalse(viewModel.HasPendingChanges);
        Assert.AreEqual(0, viewModel.Targets.Count);
    }

    [TestMethod]
    public void WizardPresetStagesOrdinaryDestinationWithCanonicalEndpointAndDocumentedFeeds()
    {
        using var fixture = SyncFixture.Create("# empty\n");
        var viewModel = fixture.CreateViewModel();
        viewModel.OpenAddDestinationCommand.Execute(null);
        var wizard = viewModel.AddWizard!;
        wizard.SelectedChoice = wizard.Choices.Single(choice => choice.Preset?.Id == "spocks_club");
        Assert.AreEqual("https://spocks.club/sync/ingress/", wizard.Endpoint);
        CollectionAssert.AreEquivalent(
            SyncTargetTypeCatalog.GetPreset("spocks_club").SupportedDataKinds.ToArray(),
            wizard.Feeds.Select(feed => feed.Kind).ToArray());
        Assert.IsFalse(wizard.Feeds.Single(feed => feed.Kind == SyncDataKind.Battlelogs).IsEnabled);
        Assert.IsTrue(wizard.Feeds.Single(feed => feed.Kind == SyncDataKind.Resources).IsEnabled);
        wizard.NextCommand.Execute(null);
        wizard.Identity = "vip-spocks";
        wizard.Token = "fixture-secret";
        wizard.NextCommand.Execute(null);
        wizard.FinishCommand.Execute(null);

        Assert.IsFalse(viewModel.IsAddWizardOpen);
        Assert.IsTrue(viewModel.HasPendingChanges);
        var target = viewModel.Targets.Single();
        Assert.AreEqual("vip-spocks", target.Name);
        Assert.AreEqual(string.Empty, target.KindLabel);
        Assert.AreEqual("https://spocks.club/sync/ingress/", target.Url);
        CollectionAssert.AreEquivalent(
            SyncTargetTypeCatalog.GetPreset("spocks_club").SupportedDataKinds.ToArray(),
            target.Feeds.Select(feed => feed.Kind).ToArray());
    }

    [TestMethod]
    public void NextSpocksPresetUsesCanonicalIdentityEndpointAndFeedDefaults()
    {
        using var fixture = SyncFixture.Create("# empty\n");
        var viewModel = fixture.CreateViewModel();
        viewModel.OpenAddDestinationCommand.Execute(null);
        var wizard = viewModel.AddWizard!;
        wizard.SelectedChoice = wizard.Choices.Single(choice => choice.Preset?.Id == "next_spocks_club");

        Assert.AreEqual("spocksclub-next", wizard.Identity);
        Assert.AreEqual("https://next.spocks.club/sync/ingress/", wizard.Endpoint);
        Assert.AreEqual(13, wizard.Feeds.Count);
        Assert.IsFalse(wizard.Feeds.Single(feed => feed.Kind == SyncDataKind.Battlelogs).IsEnabled);
        Assert.IsFalse(wizard.Feeds.Single(feed => feed.Kind == SyncDataKind.Jobs).IsEnabled);
        Assert.IsTrue(wizard.Feeds.Where(feed =>
            feed.Kind is not SyncDataKind.Battlelogs and not SyncDataKind.Jobs).All(feed => feed.IsEnabled));
    }

    [TestMethod]
    public void SwitchingTabsPreservesDraftAndDiscardRestoresWholeSyncSession()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """);
        var viewModel = fixture.CreateViewModel();
        var destinationTab = viewModel.Tabs.Single(tab => !tab.IsGlobal);
        viewModel.SelectedTab = destinationTab;
        destinationTab.Destination!.Feeds.Single(feed => feed.Kind == SyncDataKind.Jobs).Choice =
            SyncBooleanOverrideChoice.Disabled;
        viewModel.SelectedTab = viewModel.Tabs.Single(tab => tab.IsGlobal);
        viewModel.SelectedTab = viewModel.Tabs.Single(tab => !tab.IsGlobal);

        Assert.AreEqual(
            SyncBooleanOverrideChoice.Disabled,
            viewModel.SelectedDestination!.Feeds.Single(feed => feed.Kind == SyncDataKind.Jobs).Choice);
        viewModel.DiscardCommand.Execute(null);
        Assert.AreEqual(
            SyncBooleanOverrideChoice.UseGlobal,
            viewModel.Targets.Single().Feeds.Single(feed => feed.Kind == SyncDataKind.Jobs).Choice);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); ++attempt)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), "The asynchronous view-model action did not finish.");
    }

    private static void FinishWizard(
        SyncWorkspaceViewModel viewModel,
        SyncTargetKind kind,
        string identity,
        string? endpoint = null,
        string? presetId = null)
    {
        viewModel.OpenAddDestinationCommand.Execute(null);
        var wizard = viewModel.AddWizard!;
        wizard.SelectedChoice = wizard.Choices.Single(choice =>
            choice.Kind == kind && choice.Preset?.Id == presetId);
        wizard.NextCommand.Execute(null);
        wizard.Identity = identity;
        if (endpoint is not null)
        {
            wizard.Endpoint = endpoint;
        }
        wizard.Token = "fixture-secret";
        wizard.NextCommand.Execute(null);
        wizard.FinishCommand.Execute(null);
    }

    private sealed class SyncFixture : IDisposable
    {
        private SyncFixture(string path) => Path = path;
        public string Path { get; }

        public static SyncFixture Create(string contents)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"stfc-launcher-sync-{Guid.NewGuid():N}.toml");
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            return new(path);
        }

        public SyncWorkspaceViewModel CreateViewModel(Func<bool>? siblingPending = null) =>
            new(() => Path, new TomlConfigurationRepository(), siblingPending);

        public void Dispose()
        {
            foreach (var path in new[] { Path, Path + ".bak" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
