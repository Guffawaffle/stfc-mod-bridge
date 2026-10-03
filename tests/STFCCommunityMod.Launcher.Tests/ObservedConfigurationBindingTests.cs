using System.Text;
using System.Text.Json;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ObservedConfigurationBindingTests
{
    private const string ReviewedCommit = "e80a303a9949c89100b6e59b8a5e5cc2271e7144";

    [TestMethod]
    public void ProfilesOnlyComposesWithoutBorrowingACommunitySettingsCatalog()
    {
        using var fixture = new Fixture();
        var provider = fixture.Providers.GetProvider("profiles");
        var catalog = fixture.Resolver.ResolveCatalog(new("profiles", "development"), null);
        Assert.AreEqual("profiles", catalog.Source.StableId);
        Assert.IsFalse(catalog.IsQualified);
        Assert.AreEqual(0, catalog.VisibleSettings.Count);
        var composition = LauncherStartupComposition.Create(provider, provider.DefaultReleaseChannel,
            configurationCatalog: catalog);
        Assert.IsNotNull(composition);
    }

    [TestMethod]
    public void PersistedNewRepositoryReleaseCannotBorrowHistoricalTypedSettings()
    {
        using var fixture = new Fixture();
        fixture.Record("1.1.8.0", new string('a', 40));
        var catalog = fixture.Resolve();
        Assert.IsFalse(catalog.IsQualified);
        Assert.AreEqual("1.1.8.0", catalog.Identity.ReleaseVersion);
        Assert.AreEqual(new string('a', 40), catalog.Identity.SourceCommit);
        Assert.AreEqual(0, catalog.VisibleSettings.Count);
        var evidence = fixture.Resolver.ResolveEvidence(new("netniv", "stable"), fixture.Game);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Unknown, evidence.CapabilityStatus);
        var snapshot = new ConfigurationDocumentSnapshot(fixture.Config, fixture.Raw);
        Assert.AreEqual(ConfigurationEffectiveExportState.Unavailable,
            ConfigurationEffectiveExportService.Build(snapshot, evidence).State);
        var diagnosis = new ConfigurationHealthAnalyzer().Analyze(snapshot, evidence);
        Assert.IsNull(diagnosis.Binding.CatalogId);

        var provider = fixture.Providers.GetProvider("netniv");
        var composition = LauncherStartupComposition.Create(
            provider, provider.DefaultReleaseChannel, configurationCatalog: catalog);
        var rawOpened = false;
        var rawCommand = LauncherRawConfigurationCommand.Create(() => fixture.Config, _ => rawOpened = true);
        var settings = new SettingsViewModel(
            catalog, rawCommand, rawCommand, () => fixture.Config,
            composition.SettingsLayout, composition.SettingsDiagnostics);
        Assert.IsFalse(settings.CanEdit);
        Assert.IsFalse(settings.SyncWorkspace.CanEdit);
        Assert.IsFalse(settings.SyncWorkspace.IsConfigurationReady);
        Assert.AreEqual(0, settings.SyncWorkspace.Targets.Count);
        Assert.IsTrue(settings.OpenRawTomlCommand.CanExecute(null));
        settings.OpenRawTomlCommand.Execute(null);
        Assert.IsTrue(rawOpened);
        CollectionAssert.AreEqual(fixture.Raw, File.ReadAllBytes(fixture.Config));

        // The shared core path must not independently grant topology editing.
        var load = ConfigurationWorkspace.Load(
            fixture.Config, catalog, new TomlConfigurationRepository(), out var workspace);
        Assert.IsTrue(load.IsSuccess);
        Assert.IsNotNull(workspace);
        Assert.IsFalse(workspace.CreateSyncTopologyEditSession(out var sync).IsValid);
        Assert.IsNull(sync);
    }

    [TestMethod]
    public void CorruptPersistedPathReturnsUnknownWithoutPreventingSettingsOrRawAccess()
    {
        using var fixture = new Fixture();
        fixture.Record("1.1.8.0", new string('a', 40));
        var registry = Path.Combine(fixture.State, "installed-mod.json");
        var contents = File.ReadAllText(registry);
        var validPath = JsonSerializer.Serialize(fixture.Game);
        Assert.IsTrue(contents.Contains(validPath, StringComparison.Ordinal));
        File.WriteAllText(registry, contents.Replace(
            validPath, JsonSerializer.Serialize(fixture.Game + "\0"), StringComparison.Ordinal));
        var catalog = fixture.Resolve();
        Assert.IsFalse(catalog.IsQualified);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Unknown,
            fixture.Resolver.ResolveEvidence(new("netniv", "stable"), fixture.Game).CapabilityStatus);
        var provider = fixture.Providers.GetProvider("netniv");
        var composition = LauncherStartupComposition.Create(
            provider, provider.DefaultReleaseChannel, configurationCatalog: catalog);
        var rawOpened = false;
        var rawCommand = LauncherRawConfigurationCommand.Create(() => fixture.Config, _ => rawOpened = true);
        var settings = new SettingsViewModel(
            catalog, rawCommand, rawCommand, () => fixture.Config,
            composition.SettingsLayout, composition.SettingsDiagnostics);
        Assert.IsFalse(settings.CanEdit);
        Assert.IsTrue(settings.OpenRawTomlCommand.CanExecute(null));
        settings.OpenRawTomlCommand.Execute(null);
        Assert.IsTrue(rawOpened);
        CollectionAssert.AreEqual(fixture.Raw, File.ReadAllBytes(fixture.Config));
    }

    [TestMethod]
    public void ExactObservedKnownReleaseRetainsCatalogButWrongCommitDoesNot()
    {
        using var fixture = new Fixture();
        fixture.Record("1.1.6.0", ReviewedCommit);
        var catalog = fixture.Resolve();
        Assert.IsTrue(catalog.IsQualified);
        Assert.AreEqual("netniv.configuration.stable-1.1.6.0", catalog.Identity.CatalogId);
        Assert.AreEqual(155, catalog.VisibleSettings.Count);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Supported,
            fixture.Resolver.ResolveEvidence(new("netniv", "stable"), fixture.Game).CapabilityStatus);

        fixture.Record("1.1.6.0", new string('b', 40));
        var changed = fixture.Resolve();
        Assert.IsFalse(changed.IsQualified);
        Assert.AreEqual("1.1.6.0", changed.Identity.ReleaseVersion);
        Assert.AreEqual(new string('b', 40), changed.Identity.SourceCommit);
    }

    [TestMethod]
    public void ObservedNetnivReceiptDoesNotBorrowDifferentPreferredProviderCatalog()
    {
        using var fixture = new Fixture();
        fixture.Record("1.1.8.0", new string('a', 40));
        var catalog = fixture.Resolver.ResolveCatalog(new("guffawaffle", "stable"), fixture.Game);
        Assert.IsFalse(catalog.IsQualified);
        Assert.AreEqual("guffawaffle", catalog.Source.StableId);
        Assert.AreEqual("1.1.8.0", catalog.Identity.ReleaseVersion);
    }

    [DataTestMethod]
    [DataRow("guffawaffle", "stable", "guffawaffle.stfc-community-mod")]
    [DataRow("netniv", "preview", "netniv.stfc-community-mod")]
    [DataRow("netniv", "stable", "different.runtime")]
    public void ReceiptWithoutRepositoryObservationCannotAuthorizeAnotherSelection(
        string receiptProviderId, string receiptChannelId, string receiptRuntimeId)
    {
        using var fixture = new Fixture();
        fixture.Record("2.1.0.8", observationCommit: null,
            receiptProviderId, receiptChannelId, receiptRuntimeId);
        var registry = Path.Combine(fixture.State, "installed-mod.json");
        var registryBefore = File.ReadAllBytes(registry);
        var selected = new LauncherProviderSelection("netniv", "stable");
        var selectionStore = new JsonLauncherProviderSelectionStore(fixture.State);
        selectionStore.Save(selected);
        new JsonGameInstallSelectionStore(fixture.State).Save(fixture.Game);

        Assert.IsFalse(fixture.Resolver.ResolveCatalog(selected, fixture.Game).IsQualified);
        var evidence = fixture.Resolver.ResolveEvidence(selected, fixture.Game);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Unknown, evidence.CapabilityStatus);
        Assert.AreEqual(ConfigurationEffectiveExportState.Unavailable,
            ConfigurationEffectiveExportService.Build(
                new ConfigurationDocumentSnapshot(fixture.Config, fixture.Raw), evidence).State);
        var cleanup = MainWindow.CapturePersistedConfigurationMigrationAuthority(
            fixture.State, fixture.Providers, selectionStore);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Unknown,
            cleanup.DiagnosisEvidence.CapabilityStatus);
        CollectionAssert.AreEqual(registryBefore, File.ReadAllBytes(registry));
        CollectionAssert.AreEqual(fixture.Raw, File.ReadAllBytes(fixture.Config));
    }

    [TestMethod]
    public void NoRepositoryObservationKeepsExistingCustomReleaseFallback()
    {
        using var fixture = new Fixture();
        fixture.Record("1.1.9.1", observationCommit: null);
        var catalog = fixture.Resolve();
        Assert.IsTrue(catalog.IsQualified);
        Assert.AreEqual("1.1.6.0", catalog.Identity.ReleaseVersion);
        Assert.AreEqual(155, catalog.VisibleSettings.Count);
    }

    [TestMethod]
    public void CatalogChangeAdvancesBoundSettingsRevisionWithoutRuntimeEvidenceChange()
    {
        using var fixture = new Fixture();
        fixture.Record("1.1.6.0", ReviewedCommit);
        var known = fixture.Resolve();
        var provider = fixture.Providers.GetProvider("netniv");
        var preferences = new LauncherBattlePreferences(
            LauncherPlayerFeaturePreference.Unset, LauncherPlayerFeaturePreference.Unset);
        var composition = LauncherStartupComposition.Create(
            provider, provider.DefaultReleaseChannel, configurationCatalog: known);
        var slot = new LauncherRuntimeCompositionSlot(
            provider, provider.DefaultReleaseChannel, composition, null, known);
        Assert.IsFalse(slot.Refresh(null, preferences, fixture.Resolve()));
        fixture.Record("1.1.8.0", new string('a', 40));
        Assert.IsTrue(slot.Refresh(null, preferences, fixture.Resolve()));
        Assert.AreEqual(1L, slot.SettingsRevision);
        Assert.IsFalse(slot.Refresh(null, preferences, fixture.Resolve()));
        Assert.AreEqual(1L, slot.SettingsRevision);
        Assert.IsInstanceOfType<AlphabeticalSettingsLayoutProvider>(slot.Current.SettingsLayout);
    }

    [TestMethod]
    public void ProspectiveNetnivTargetIsUnknownUntilInstalled()
    {
        using var fixture = new Fixture();
        var evidence = fixture.Resolver.ResolveSwitchEvidence(
            new("netniv", "stable"), new("guffawaffle", "stable"), fixture.Game);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Unknown, evidence.CapabilityStatus);
        CollectionAssert.AreEqual(fixture.Raw, File.ReadAllBytes(fixture.Config));
    }

    [TestMethod]
    public void PersistedCleanupAuthorityReadsActualObservationAtApplyTime()
    {
        using var fixture = new Fixture();
        var selectionStore = new JsonLauncherProviderSelectionStore(fixture.State);
        selectionStore.Save(new("netniv", "stable"));
        new JsonGameInstallSelectionStore(fixture.State).Save(fixture.Game);
        fixture.Record("1.1.6.0", ReviewedCommit);
        var known = MainWindow.CapturePersistedConfigurationMigrationAuthority(
            fixture.State, fixture.Providers, selectionStore);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Supported, known.DiagnosisEvidence.CapabilityStatus);
        fixture.Record("1.1.8.0", new string('a', 40));
        var current = MainWindow.CapturePersistedConfigurationMigrationAuthority(
            fixture.State, fixture.Providers, selectionStore);
        Assert.AreEqual(LauncherProviderCapabilityStatus.Unknown, current.DiagnosisEvidence.CapabilityStatus);
        CollectionAssert.AreEqual(fixture.Raw, File.ReadAllBytes(fixture.Config));
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly string root = Path.Combine(Path.GetTempPath(), "observed-configuration-" + Guid.NewGuid().ToString("N"));
        public string State { get; }
        public string Game { get; }
        public string Config { get; }
        public byte[] Raw { get; } = Encoding.UTF8.GetBytes(
            "# retained comment\r\n[unknown]\r\nkey = \"preserve\"\r\n[sync]\r\nurl = \"https://example.invalid/data\"\r\n");
        public LauncherDistributionProviderCatalog Providers { get; } = BundledLauncherProviderCatalog.Load();
        public LauncherInstalledConfigurationResolver Resolver { get; }

        public Fixture()
        {
            State = Path.Combine(root, "state");
            Game = Path.Combine(root, "game");
            Config = Path.Combine(Game, "community_patch_settings.toml");
            Directory.CreateDirectory(State);
            Directory.CreateDirectory(Game);
            File.WriteAllBytes(Path.Combine(Game, "prime.exe"), [1]);
            File.WriteAllBytes(Config, Raw);
            Resolver = new(
                Providers, BundledLauncherProviderCatalog.LoadReviewedWindowsReleases(Providers),
                LauncherInstalledConfigurationResolver.CreateReadOnlyStateReader(State));
        }

        public LauncherConfigurationCatalog Resolve() =>
            Resolver.ResolveCatalog(new("netniv", "stable"), Game);

        public void Record(string version, string? observationCommit,
            string providerId = "netniv", string channelId = "stable",
            string runtimeDistributionId = "netniv.stfc-community-mod")
        {
            var observation = observationCommit is null ? null : new NetnivRepositoryReleaseObservation(
                1, 2, "v" + version, observationCommit,
                new("https://github.com/netniV/stfc-mod/releases/download/v" + version + "/stfc-community-mod.zip"),
                200, new string('c', 64), 100, new string('d', 64), version, DateTimeOffset.UtcNow);
            var receipt = new ModInstalledArtifactState(
                1, Game, "version.dll", version, 100, new string('d', 64), DateTimeOffset.UtcNow,
                null, providerId, channelId, runtimeDistributionId,
                ReleaseProductVersion: observation?.Tag, RepositoryRelease: observation);
            File.WriteAllText(Path.Combine(State, "installed-mod.json"),
                JsonSerializer.Serialize(new ModInstalledArtifactRegistry(2, [receipt]),
                    JsonOptions));
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
