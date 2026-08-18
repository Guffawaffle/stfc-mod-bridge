using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.LocalGameIntegration.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("LocalGameMutation")]
public sealed partial class LiveProviderInstallIntegrationTests
{
    private const string MutationEnvironmentVariable =
        "STFC_BRIDGE_ALLOW_RESTORABLE_MUTATION";
    private const string LiveProvidersEnvironmentVariable =
        "STFC_BRIDGE_USE_LIVE_PROVIDER_RELEASES";
    private const string RecoveryEnvironmentVariable =
        "STFC_BRIDGE_EXERCISE_RECOVERY";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(600_000)]
    public async Task GuffawaffleAndNetnivInstallRemoveRestoreCleanBaseline()
    {
        var gameDirectory = RequireMutationTarget();
        if (!string.Equals(
                Environment.GetEnvironmentVariable(LiveProvidersEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Live provider releases are disabled. Add -UseLiveProviderReleases to the local runner.");
        }
        using var campaign = OpenCampaignThroughSanitizedBoundary(gameDirectory);
        Assert.IsFalse(
            File.Exists(Path.Combine(gameDirectory, "version.dll")),
            "Wave 1 requires the maintained clean target without version.dll.");
        Assert.IsFalse(
            File.Exists(Path.Combine(gameDirectory, "community_patch_settings.toml")),
            "The provider-switch journey currently requires the maintained clean target without TOML.");
        var catalog = LoadProviderCatalog();
        var reviewed = LoadReviewedReleases(catalog);
        using var httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        var endpoints = new Dictionary<string, ProviderEndpoint>(StringComparer.Ordinal);
        foreach (var providerId in new[] { "guffawaffle", "netniv" })
        {
            endpoints.Add(
                providerId,
                CreateEndpoint(
                    catalog.GetProvider(providerId),
                    reviewed,
                    campaign.StateDirectory,
                    httpClient));
        }

        Exception? journeyFailure = null;
        try
        {
            foreach (var providerId in new[] { "guffawaffle", "netniv" })
            {
                await InstallAndRemoveAsync(
                    endpoints[providerId],
                    gameDirectory,
                    campaign).ConfigureAwait(false);
                campaign.AssertBaseline(
                    $"{providerId} install/remove did not restore the exact game target.");
                TestContext.WriteLine($"{providerId}: trusted install and production removal passed");
            }
            await SwitchRoundTripAsync(
                catalog,
                endpoints,
                campaign.StateDirectory,
                gameDirectory,
                campaign).ConfigureAwait(false);
            campaign.AssertBaseline(
                "Provider switch round trip did not restore the exact game target.");
            TestContext.WriteLine(
                "provider switch: Guffawaffle → NetniV → Guffawaffle restored provider TOML and clean baseline");
        }
        catch (Exception exception)
        {
            journeyFailure = exception;
        }

        var cleanupFailure = await TryProductionCleanupAsync(
            endpoints).ConfigureAwait(false);
        try
        {
            campaign.AssertBaseline("Final production cleanup did not restore the campaign baseline.");
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure is null
                ? exception
                : new AggregateException(cleanupFailure, exception);
        }

        if (cleanupFailure is not null)
        {
            campaign.PreserveStateForRecovery();
            cleanupFailure = CombineFailures(
                cleanupFailure,
                TryEmergencyRestore(campaign));
        }
        if (journeyFailure is not null || cleanupFailure is not null)
        {
            campaign.PreserveStateForRecovery();
            var failure = journeyFailure is null
                ? cleanupFailure!
                : cleanupFailure is null
                    ? journeyFailure
                    : new AggregateException(journeyFailure, cleanupFailure);
            throw SanitizedFailure(
                "The live provider campaign failed; isolated recovery state was retained.",
                failure,
                gameDirectory,
                campaign.StateDirectory);
        }
    }

    [TestMethod]
    [Timeout(120_000)]
    [TestCategory("LocalGameRecovery")]
    public async Task ConfigurationHistoryRestoreAndRecoveryReturnCleanBaseline()
    {
        var gameDirectory = RequireRecoveryTarget();
        using var campaign = OpenCampaignThroughSanitizedBoundary(gameDirectory);
        try
        {
            RequireKnownGuffawaffleStableArtifact(gameDirectory);
        }
        catch (Exception exception) when (IsCampaignFailure(exception))
        {
            campaign.PreserveStateForRecovery();
            throw SanitizedFailure(
                "The recovery artifact preflight failed; isolated state was retained.",
                exception,
                gameDirectory,
                campaign.StateDirectory);
        }
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        byte[]? baselineConfiguration;
        try
        {
            baselineConfiguration = File.Exists(configurationPath)
                ? await File.ReadAllBytesAsync(configurationPath).ConfigureAwait(false)
                : null;
        }
        catch (Exception exception) when (IsCampaignFailure(exception))
        {
            campaign.PreserveStateForRecovery();
            throw SanitizedFailure(
                "The configuration recovery baseline could not be captured; isolated state was retained.",
                exception,
                gameDirectory,
                campaign.StateDirectory);
        }

        Exception? journeyFailure = null;
        try
        {
            await RunConfigurationRestoreRecoveryAsync(
                gameDirectory,
                configurationPath,
                campaign.StateDirectory,
                baselineConfiguration,
                campaign).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            journeyFailure = exception;
        }

        var cleanupFailure = await TryConfigurationRecoveryCleanupAsync(
            configurationPath,
            campaign.StateDirectory,
            baselineConfiguration,
            campaign).ConfigureAwait(false);
        try
        {
            campaign.AssertBaseline(
                "The configuration recovery lab did not restore the exact game target.");
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure is null
                ? exception
                : new AggregateException(cleanupFailure, exception);
        }

        if (cleanupFailure is not null)
        {
            campaign.PreserveStateForRecovery();
            cleanupFailure = CombineFailures(
                cleanupFailure,
                TryEmergencyRestore(campaign));
        }
        if (journeyFailure is not null || cleanupFailure is not null)
        {
            campaign.PreserveStateForRecovery();
            var failure = journeyFailure is null
                ? cleanupFailure!
                : cleanupFailure is null
                    ? journeyFailure
                    : new AggregateException(journeyFailure, cleanupFailure);
            throw SanitizedFailure(
                "The configuration recovery lab failed; isolated recovery state was retained.",
                failure,
                gameDirectory,
                campaign.StateDirectory);
        }

        TestContext.WriteLine(
            "configuration history: public restore and fresh-coordinator recovery restored exact bytes, receipts, and clean baseline");
    }

    private static async Task RunConfigurationRestoreRecoveryAsync(
        string gameDirectory,
        string configurationPath,
        string stateDirectory,
        byte[]? baselineConfiguration,
        RestorableGameInstallCampaign campaign,
        byte[]? createdConfigurationContents = null)
    {
        var selection = new LauncherProviderSelection("guffawaffle", "stable");
        var selectionStore = new JsonLauncherProviderSelectionStore(stateDirectory);
        selectionStore.Save(selection);
        var backupStore = new ProviderScopedConfigurationBackupStore(stateDirectory);
        var operationLock = new LauncherOperationLock(stateDirectory);
        var original = baselineConfiguration
            ?? createdConfigurationContents
            ?? "# local restore source\r\n[graphics]\r\nfree_resize = true\r\n"u8.ToArray();
        var changed = "# local recovery source\n[graphics]\nfree_resize = false\n"u8.ToArray();
        if (original.AsSpan().SequenceEqual(changed))
        {
            changed = "# alternate local recovery source\r\n[graphics]\r\nfree_resize = true\r\n"u8.ToArray();
        }

        if (baselineConfiguration is not null)
        {
            var read = new TomlConfigurationRepository().Read(configurationPath);
            Assert.AreEqual(
                ConfigurationRepositoryReadState.Succeeded,
                read.State,
                "The protected baseline TOML must be parser-safe before the recovery lab can mutate it.");
        }

        var sourceReceipt = await backupStore.CreateAsync(new(
            gameDirectory,
            selection.ProviderId,
            configurationPath,
            original,
            "local-integration-restore-source",
            ReleaseIdentity: "local-integration/restore-source")).ConfigureAwait(false);
        var preflightCoordinator = CreateConfigurationRestoreCoordinator(
            backupStore,
            selectionStore,
            selection,
            stateDirectory,
            configurationPath);
        var preflight = preflightCoordinator.LoadHistory().Single(entry =>
            string.Equals(
                entry.Receipt.BackupId,
                sourceReceipt.BackupId,
                StringComparison.Ordinal));
        Assert.IsTrue(
            preflight.CanRestore,
            "The protected baseline TOML must be restorable before the recovery lab can mutate it.");

        if (baselineConfiguration is null)
        {
            var create = await new TomlConfigurationRepository(
                    store: new AtomicTomlStore(
                        beforeReplace: null,
                        retainAdjacentBackup: true,
                        mutationAdmission: campaign.AtomicTomlMutationAdmission),
                    mutationAdmission: operationLock)
                .CommitDocumentAsync(new(
                    configurationPath,
                    ConfigurationDocumentRevision.FromContents([]),
                    [],
                    original,
                    baselineExisted: false)).ConfigureAwait(false);
            Assert.IsTrue(create.IsSuccess, create.Error);
            campaign.RecordOwnedGameFileRevision(
                "community_patch_settings.toml",
                original);
        }

        var mutate = await new TomlConfigurationRepository(
                store: new AtomicTomlStore(
                    new ProviderScopedConfigurationMutationBackup(
                        backupStore,
                        selection.ProviderId,
                        "local-integration/restore-source"),
                    beforeReplace: null,
                    retainAdjacentBackup: false,
                    mutationAdmission: campaign.AtomicTomlMutationAdmission),
                mutationAdmission: operationLock)
            .CommitDocumentAsync(new(
                configurationPath,
                ConfigurationDocumentRevision.FromContents(original),
                original,
                changed)).ConfigureAwait(false);
        Assert.IsTrue(mutate.IsSuccess, mutate.Error);
        campaign.RecordOwnedGameFileRevision(
            "community_patch_settings.toml",
            changed);
        Assert.IsNotNull(mutate.BackupReceipt);
        CollectionAssert.AreEqual(
            original,
            backupStore.Read(
                gameDirectory,
                selection.ProviderId,
                mutate.BackupReceipt.BackupId));
        CollectionAssert.AreEqual(changed, await File.ReadAllBytesAsync(configurationPath));

        var coordinator = CreateConfigurationRestoreCoordinator(
            backupStore,
            selectionStore,
            selection,
            stateDirectory,
            configurationPath);
        var sourceEntry = coordinator.LoadHistory().Single(entry =>
            string.Equals(
                entry.Receipt.BackupId,
                sourceReceipt.BackupId,
                StringComparison.Ordinal));
        Assert.IsTrue(sourceEntry.CanRestore, sourceEntry.CompatibilitySummary);
        var restorePreview = coordinator.Preview(sourceEntry.Receipt.BackupId);
        var restored = await coordinator.ExecuteAsync(
            restorePreview,
            restorePreview.ConfirmationText).ConfigureAwait(false);
        Assert.AreEqual(
            ProviderConfigurationRestoreResultState.Succeeded,
            restored.State,
            restored.Message);
        Assert.IsNotNull(restored.PreRestoreBackup);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(configurationPath));
        campaign.RecordOwnedGameFileRevision(
            "community_patch_settings.toml",
            original);
        CollectionAssert.AreEqual(
            changed,
            backupStore.Read(
                gameDirectory,
                selection.ProviderId,
                restored.PreRestoreBackup.BackupId));

        var checkpointObserved = false;
        var interruptedCoordinator = CreateConfigurationRestoreCoordinator(
            backupStore,
            selectionStore,
            selection,
            stateDirectory,
            configurationPath,
            (phase, _) =>
            {
                if (phase == ProviderConfigurationRestorePhase.ConfigurationCommitted)
                {
                    checkpointObserved = true;
                    throw new IOException("Injected local recovery-lab interruption after TOML commit.");
                }
                return ValueTask.CompletedTask;
            });
        var recoveryPreview = interruptedCoordinator.Preview(restored.PreRestoreBackup.BackupId);
        var interrupted = await interruptedCoordinator.ExecuteAsync(
            recoveryPreview,
            recoveryPreview.ConfirmationText).ConfigureAwait(false);
        Assert.IsTrue(checkpointObserved, "The post-commit recovery checkpoint was not exercised.");
        Assert.AreEqual(
            ProviderConfigurationRestoreResultState.RecoveryRequired,
            interrupted.State,
            interrupted.Message);
        CollectionAssert.AreEqual(changed, await File.ReadAllBytesAsync(configurationPath));
        Assert.AreEqual(
            ProviderConfigurationRestorePhase.RecoveryRequired,
            interruptedCoordinator.ReadJournal()?.Phase);
        campaign.RecordOwnedGameFileRevision(
            "community_patch_settings.toml",
            changed);

        var freshCoordinator = CreateConfigurationRestoreCoordinator(
            backupStore,
            selectionStore,
            selection,
            stateDirectory,
            configurationPath);
        var recovered = await freshCoordinator.RecoverAsync().ConfigureAwait(false);
        Assert.AreEqual(
            ProviderConfigurationRestoreResultState.Succeeded,
            recovered.State,
            recovered.Message);
        Assert.IsNotNull(recovered.PreRestoreBackup);
        Assert.IsNotNull(recovered.RestoredBackup);
        Assert.AreEqual(
            recoveryPreview.TransactionId,
            recovered.RestoredBackup.RestoreTransactionId);
        Assert.AreEqual(
            ProviderConfigurationRestorePhase.Completed,
            freshCoordinator.ReadJournal()?.Phase);
        CollectionAssert.AreEqual(changed, await File.ReadAllBytesAsync(configurationPath));
        campaign.RecordOwnedGameFileRevision(
            "community_patch_settings.toml",
            changed);
        CollectionAssert.AreEqual(
            original,
            backupStore.Read(
                gameDirectory,
                selection.ProviderId,
                recovered.PreRestoreBackup.BackupId));

        var finalCoordinator = CreateConfigurationRestoreCoordinator(
            backupStore,
            selectionStore,
            selection,
            stateDirectory,
            configurationPath);
        var finalPreview = finalCoordinator.Preview(sourceReceipt.BackupId);
        var finalRestore = await finalCoordinator.ExecuteAsync(
            finalPreview,
            finalPreview.ConfirmationText).ConfigureAwait(false);
        Assert.AreEqual(
            ProviderConfigurationRestoreResultState.Succeeded,
            finalRestore.State,
            finalRestore.Message);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(configurationPath));
        campaign.RecordOwnedGameFileRevision(
            "community_patch_settings.toml",
            original);
    }

    private static async Task<Exception?> TryConfigurationRecoveryCleanupAsync(
        string configurationPath,
        string stateDirectory,
        byte[]? baselineConfiguration,
        RestorableGameInstallCampaign campaign)
    {
        try
        {
            var selectionStore = new JsonLauncherProviderSelectionStore(stateDirectory);
            var selection = selectionStore.Load();
            if (selection is not null)
            {
                var coordinator = CreateConfigurationRestoreCoordinator(
                    new ProviderScopedConfigurationBackupStore(stateDirectory),
                    selectionStore,
                    selection,
                    stateDirectory,
                    configurationPath);
                var journal = coordinator.ReadJournal();
                if (journal is not null
                    && journal.Phase is not ProviderConfigurationRestorePhase.Completed
                        and not ProviderConfigurationRestorePhase.Failed)
                {
                    var recovery = await coordinator.RecoverAsync().ConfigureAwait(false);
                    if (!recovery.IsSuccess)
                    {
                        return new InvalidOperationException(recovery.Message);
                    }
                }
            }

            if (baselineConfiguration is not null)
            {
                var restoredContents = File.Exists(configurationPath)
                    ? await File.ReadAllBytesAsync(configurationPath).ConfigureAwait(false)
                    : null;
                if (restoredContents is null
                    || !baselineConfiguration.AsSpan().SequenceEqual(restoredContents))
                {
                    return new InvalidOperationException(
                        "Production configuration recovery did not restore the protected baseline bytes.");
                }
            }
            campaign.RestoreConfigurationBaseline();
            return null;
        }
        catch (Exception exception) when (IsCampaignFailure(exception))
        {
            return exception;
        }
    }

    private static ProviderConfigurationRestoreCoordinator CreateConfigurationRestoreCoordinator(
        ProviderScopedConfigurationBackupStore backupStore,
        ILauncherProviderSelectionStore selectionStore,
        LauncherProviderSelection selection,
        string stateDirectory,
        string configurationPath,
        Func<ProviderConfigurationRestorePhase, CancellationToken, ValueTask>? checkpoint = null)
    {
        var evidence = LauncherConfigurationDiagnosisEvidence.Supported(
            selection.ProviderId,
            selection.ReleaseChannelId,
            LauncherConfigurationSchemaLoader.LoadFile(Path.Combine(
                RepositoryRoot(),
                "docs",
                "windows-launcher",
                "config-schema.guffawaffle.v1.json")));
        return checkpoint is null
            ? new(
                backupStore,
                LoadProviderCatalog(),
                selectionStore,
                selection,
                evidence,
                stateDirectory,
                () => configurationPath)
            : new(
                backupStore,
                LoadProviderCatalog(),
                selectionStore,
                selection,
                evidence,
                stateDirectory,
                () => configurationPath,
                gameProcessInspector: null,
                timeProvider: null,
                checkpoint);
    }

    private static RestorableGameInstallCampaign OpenCampaignThroughSanitizedBoundary(
        string gameDirectory,
        Func<string, RestorableGameInstallCampaign>? factory = null)
    {
        try
        {
            if (new SystemGameProcessInspector().Inspect(gameDirectory)
                != GameProcessInspectionState.NotRunning)
            {
                throw new InvalidOperationException(
                    "The exact opted-in integration installation is running or cannot be attributed safely.");
            }
            return (factory ?? (path => new RestorableGameInstallCampaign(path)))(gameDirectory);
        }
        catch (Exception exception) when (IsCampaignFailure(exception))
        {
            throw SanitizedFailure(
                "The local recovery campaign could not be opened.",
                exception,
                gameDirectory,
                "%STATE_DIR%");
        }
    }

    private static AssertFailedException SanitizedFailure(
        string context,
        Exception failure,
        string gameDirectory,
        string stateDirectory)
    {
        var summary = failure.Message
            .Replace(gameDirectory, "%GAME_DIR%", StringComparison.OrdinalIgnoreCase)
            .Replace(stateDirectory, "%STATE_DIR%", StringComparison.OrdinalIgnoreCase);
        return new AssertFailedException(
            $"{context} Root cause: {failure.GetType().Name}: {summary}");
    }

    private static Exception? TryEmergencyRestore(RestorableGameInstallCampaign campaign)
    {
        try
        {
            campaign.EmergencyRestore();
            campaign.AssertBaseline("Emergency restoration could not restore the maintained target.");
            return null;
        }
        catch (Exception exception) when (IsCampaignFailure(exception))
        {
            return exception;
        }
    }

    private static bool IsCampaignFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException
            or JsonException
            or CryptographicException
            or AssertFailedException;

    private static Exception CombineFailures(Exception primary, Exception? secondary) =>
        secondary is null ? primary : new AggregateException(primary, secondary);

    private static void RequireKnownGuffawaffleStableArtifact(string gameDirectory)
    {
        var artifactPath = Path.Combine(gameDirectory, "version.dll");
        Assert.IsTrue(
            File.Exists(artifactPath),
            "The Guffawaffle recovery lab requires a known managed version.dll before attributing TOML history.");
        var artifact = new FileInfo(artifactPath);
        using var contents = artifact.OpenRead();
        var sha256 = Convert.ToHexString(SHA256.HashData(contents));
        var providerCatalog = LoadProviderCatalog();
        using var knownArtifacts = File.OpenRead(Path.Combine(
            RepositoryRoot(),
            "providers",
            "known-windows-artifacts.v1.json"));
        var provenance = new ModBinaryProvenanceResolver(
            new WindowsModBinaryVersionMetadataReader(),
            KnownModArtifactCatalogLoader.Load(knownArtifacts, providerCatalog))
            .Resolve(artifactPath, sha256, artifact.Length);
        Assert.AreEqual(
            ModBinaryProvenanceState.KnownProviderArtifact,
            provenance.State,
            "The recovery lab refuses to attribute configuration history from an unknown or modified DLL.");
        Assert.AreEqual(
            "guffawaffle",
            provenance.KnownArtifact?.ProviderId,
            "The current recovery lab is bound to Guffawaffle configuration evidence.");
        Assert.AreEqual(
            "stable",
            provenance.KnownArtifact?.TrackId,
            "The current recovery lab is bound to the Guffawaffle stable track.");
    }

    private static async Task SwitchRoundTripAsync(
        LauncherDistributionProviderCatalog catalog,
        IReadOnlyDictionary<string, ProviderEndpoint> endpoints,
        string stateDirectory,
        string gameDirectory,
        RestorableGameInstallCampaign campaign)
    {
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var guffawaffleConfiguration = "# local Guffawaffle integration profile\r\n[graphics]\r\nfree_resize = true\r\n"u8.ToArray();
        var netnivConfiguration = "# local NetniV integration profile\n[graphics]\nfree_resize = false\n"u8.ToArray();
        var selectionStore = new JsonLauncherProviderSelectionStore(stateDirectory);
        var backupStore = new ProviderScopedConfigurationBackupStore(stateDirectory);
        try
        {
            var guffawaffleInstall = await endpoints["guffawaffle"].Coordinator.PrepareLatestAsync(
                gameDirectory,
                isGameRunning: false).ConfigureAwait(false);
            var installed = await endpoints["guffawaffle"].Coordinator.ExecuteAsync(
                guffawaffleInstall).ConfigureAwait(false);
            Assert.IsTrue(installed.IsSuccess, installed.Message);
            campaign.RecordOwnedGameFileRevision(
                "version.dll",
                installed.InstalledState!.Sha256);
            campaign.WriteGameFileAtomically(
                "community_patch_settings.toml",
                guffawaffleConfiguration);
            selectionStore.Save(new("guffawaffle", "stable"));
            await backupStore.CreateAsync(new(
                gameDirectory,
                "netniv",
                configurationPath,
                netnivConfiguration,
                "local-integration-seed")).ConfigureAwait(false);
            var configurationSwitch = new LauncherProviderSourceSwitchService(
                catalog,
                selectionStore,
                stateDirectory);
            var switchCoordinator = new LauncherProviderAtomicSwitchCoordinator(
                configurationSwitch,
                endpoints.Values.Select(endpoint =>
                    new LauncherProviderSwitchEndpoint(endpoint.ProviderId, endpoint.Coordinator)),
                stateDirectory);

            var toNetniv = await switchCoordinator.PreviewAsync(
                "netniv",
                "stable",
                gameDirectory,
                isGameRunning: false,
                configurationPath).ConfigureAwait(false);
            var netnivResult = await switchCoordinator.ExecuteAsync(
                toNetniv,
                toNetniv.ConfirmationText).ConfigureAwait(false);
            Assert.AreEqual("netniv", netnivResult.InstalledArtifact!.ProviderId);
            campaign.RecordOwnedGameFileRevision(
                "version.dll",
                netnivResult.InstalledArtifact.Sha256);
            CollectionAssert.AreEqual(netnivConfiguration, File.ReadAllBytes(configurationPath));
            campaign.RecordOwnedGameFileRevision(
                "community_patch_settings.toml",
                netnivConfiguration);

            var toGuffawaffle = await switchCoordinator.PreviewAsync(
                "guffawaffle",
                "stable",
                gameDirectory,
                isGameRunning: false,
                configurationPath).ConfigureAwait(false);
            var guffawaffleResult = await switchCoordinator.ExecuteAsync(
                toGuffawaffle,
                toGuffawaffle.ConfirmationText).ConfigureAwait(false);
            Assert.AreEqual("guffawaffle", guffawaffleResult.InstalledArtifact!.ProviderId);
            campaign.RecordOwnedGameFileRevision(
                "version.dll",
                guffawaffleResult.InstalledArtifact.Sha256);
            CollectionAssert.AreEqual(guffawaffleConfiguration, File.ReadAllBytes(configurationPath));

            var removal = await endpoints["guffawaffle"].Coordinator.UninstallAsync(gameDirectory).ConfigureAwait(false);
            Assert.IsTrue(removal.IsSuccess, removal.Message);
        }
        finally
        {
            campaign.RestoreConfigurationBaseline();
        }
    }

    private static async Task InstallAndRemoveAsync(
        ProviderEndpoint endpoint,
        string gameDirectory,
        RestorableGameInstallCampaign campaign)
    {
        var preparation = await endpoint.Coordinator.PrepareLatestAsync(
            gameDirectory,
            isGameRunning: false).ConfigureAwait(false);
        Assert.AreEqual(ModOperationPreparationState.Ready, preparation.State);
        Assert.AreEqual(endpoint.ProviderId, preparation.ProviderId);

        var installation = await endpoint.Coordinator.ExecuteAsync(preparation).ConfigureAwait(false);
        Assert.IsTrue(installation.IsSuccess, installation.Message);
        Assert.AreEqual(endpoint.ProviderId, installation.InstalledState?.ProviderId);
        Assert.IsTrue(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        campaign.RecordOwnedGameFileRevision(
            "version.dll",
            installation.InstalledState!.Sha256);

        var removal = await endpoint.Coordinator.UninstallAsync(gameDirectory).ConfigureAwait(false);
        Assert.IsTrue(removal.IsSuccess, removal.Message);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        Assert.IsNull(endpoint.Deployment.ReadInstalledState());
    }

    private static async Task<Exception?> TryProductionCleanupAsync(
        IReadOnlyDictionary<string, ProviderEndpoint> endpoints)
    {
        try
        {
            var deployment = endpoints.Values.First().Deployment;
            var journal = deployment.ReadJournal();
            if (journal is not null
                && journal.Phase is not ModDeploymentPhase.Committed
                    and not ModDeploymentPhase.RolledBack)
            {
                var recovery = await deployment.RecoverAsync().ConfigureAwait(false);
                if (!recovery.IsSuccess)
                {
                    return new InvalidOperationException(recovery.Message);
                }
            }

            var installed = deployment.ReadInstalledState();
            if (installed is not null)
            {
                if (!endpoints.TryGetValue(installed.ProviderId, out var endpoint))
                {
                    return new InvalidOperationException(
                        "Production cleanup found an installed provider outside the campaign.");
                }
                var removal = await endpoint.Coordinator.UninstallAsync(installed.GameDirectory).ConfigureAwait(false);
                if (!removal.IsSuccess)
                {
                    return new InvalidOperationException(removal.Message);
                }
            }
            return null;
        }
        catch (Exception exception) when (IsCampaignFailure(exception))
        {
            return exception;
        }
    }

    private static ProviderEndpoint CreateEndpoint(
        LauncherDistributionProvider provider,
        ReviewedReleaseCertificationCatalog reviewed,
        string stateDirectory,
        HttpClient httpClient)
    {
        var channel = provider.DefaultReleaseChannel;
        var binding = LauncherProviderModBinding.Resolve(provider, channel, reviewed);
        Assert.IsTrue(binding.IsAvailable, binding.UnavailableReason);
        IWindowsReleaseDiscoveryClient releaseClient = binding.DiscoveryKind switch
        {
            LauncherProviderReleaseDiscoveryKind.ReleaseManifest =>
                binding.ReviewedCertification is null
                    ? new GitHubWindowsReleaseClient(
                        httpClient,
                        binding.Repository,
                        binding.ManifestAssetName!)
                    : new ManifestWithReviewedFallbackReleaseClient(
                        new GitHubWindowsReleaseClient(
                            httpClient,
                            binding.Repository,
                            binding.ManifestAssetName!),
                        new ReviewedGitHubReleaseAssetClient(
                            httpClient,
                            binding.ReviewedCertification),
                        binding.ReviewedCertification),
            LauncherProviderReleaseDiscoveryKind.GitHubReleaseAsset =>
                new ReviewedGitHubReleaseAssetClient(
                    httpClient,
                    binding.ReviewedCertification!),
            _ => throw new InvalidOperationException("Unsupported live release discovery kind."),
        };
        IModArtifactDownloader downloader = binding.ReviewedCertification is null
            ? new HttpModArtifactDownloader(httpClient)
            : binding.DiscoveryKind == LauncherProviderReleaseDiscoveryKind.ReleaseManifest
                ? new ManifestWithReviewedFallbackArtifactDownloader(
                    httpClient,
                    binding.ReviewedCertification)
                : new ReviewedZipModArtifactDownloader(
                    httpClient,
                    binding.ReviewedCertification);
        IModArtifactAuthenticityVerifier verifier = binding.TrustKind switch
        {
            LauncherProviderArtifactTrustKind.AuthenticodePublisher =>
                new WindowsAuthenticodeVerifier(
                    binding.WindowsPublisher!,
                    binding.WindowsArtifactSigningIdentityEku!),
            LauncherProviderArtifactTrustKind.ReviewedExactHash =>
                new ReviewedExactHashAuthenticityVerifier(binding.ReviewedCertification!),
            _ => throw new InvalidOperationException("Unsupported live artifact trust kind."),
        };
        var processInspector = new SystemGameProcessInspector();
        var attribution = new ModInstallationAttribution(
            binding.ProviderId,
            binding.ReleaseChannelId,
            provider.RuntimeDistributionId);
        var deployment = new ModDeploymentService(
            stateDirectory,
            downloader,
            new WindowsModArtifactVersionReader(provider.RuntimeDistributionId),
            verifier,
            gameDirectory =>
                processInspector.Inspect(gameDirectory) != GameProcessInspectionState.NotRunning,
            attribution,
            reviewedCertification: binding.ReviewedCertification);
        var health = new LauncherHealthService(
            new ModInstallationInspector(
                deployment,
                new SystemModInstallationFileSystem()),
            new(
                binding.ProviderId,
                binding.ReleaseChannelId,
                provider.RuntimeDistributionId,
                CanMutate: true,
                UnavailableReason: string.Empty));
        return new(
            binding.ProviderId,
            deployment,
            new ModManagementCoordinator(
                deployment,
                releaseClient,
                new Version(0, 1, 0),
                binding.ReleaseChannelId,
                healthService: health));
    }

    private static LauncherDistributionProviderCatalog LoadProviderCatalog()
    {
        var root = RepositoryRoot();
        using var index = File.OpenRead(
            Path.Combine(root, "providers", "bundled-provider-catalog.v1.json"));
        return LauncherDistributionProviderCatalogLoader.Load(
            index,
            resourceName => resourceName switch
            {
                "STFCCommunityMod.Launcher.ProviderPacks.Guffawaffle.v1.json" =>
                    File.OpenRead(Path.Combine(root, "providers", "guffawaffle", "provider-pack.v1.json")),
                "STFCCommunityMod.Launcher.ProviderPacks.Netniv.v1.json" =>
                    File.OpenRead(Path.Combine(root, "providers", "netniv", "provider-pack.v1.json")),
                _ => null,
            });
    }

    private static ReviewedReleaseCertificationCatalog LoadReviewedReleases(
        LauncherDistributionProviderCatalog catalog)
    {
        using var stream = File.OpenRead(
            Path.Combine(RepositoryRoot(), "providers", "reviewed-windows-releases.v1.json"));
        return ReviewedReleaseCertificationCatalogLoader.Load(stream, catalog);
    }

    private static string RequireMutationTarget()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(MutationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Restorable mutation is disabled. Add -AllowRestorableMutation to the local runner.");
        }
        return LocalGameIntegrationTarget.RequireOptedInDirectory();
    }

    private static string RequireRecoveryTarget()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(RecoveryEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "The recovery lab is disabled. Add -ExerciseRecovery to the local runner.");
        }
        return RequireMutationTarget();
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
            ?? throw new DirectoryNotFoundException("Could not locate the Mod Bridge repository root.");
    }

    private sealed record ProviderEndpoint(
        string ProviderId,
        ModDeploymentService Deployment,
        ModManagementCoordinator Coordinator);

    private enum DirectGameMutationKind
    {
        WriteTomlTemporary,
        PromoteTomlTemporary,
        DeleteTomlTemporary,
        WriteRestoreStage,
        PromoteRestoreStage,
        DeleteGameFile,
        DeleteRestoreStage,
        CommitRestoreStage,
        AfterRestoreReplacement,
        RestoreRollback,
        RestoreLastWriteTime,
        RestoreAttributes,
    }

    private sealed class RestorableGameInstallCampaign : IDisposable
    {
        private readonly string gameDirectory;
        private readonly Dictionary<string, FileBaseline> baseline;
        private readonly IGameProcessInspector gameProcessInspector;
        private readonly Action<DirectGameMutationKind, string>? beforeMutation;
        private readonly Action<string>? beforeReceiptPersist;
        private readonly Dictionary<string, HashSet<OwnedFileRevision>> ownedFileRevisions =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RecoveryStageReceipt> createdStages = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        private bool disposed;
        private bool preserveState;

        public RestorableGameInstallCampaign(
            string gameDirectory,
            IGameProcessInspector? gameProcessInspector = null,
            Action<DirectGameMutationKind, string>? beforeMutation = null,
            Action<string>? beforeReceiptPersist = null)
        {
            this.gameProcessInspector = gameProcessInspector ?? new SystemGameProcessInspector();
            this.beforeMutation = beforeMutation;
            this.beforeReceiptPersist = beforeReceiptPersist;
            try
            {
                this.gameDirectory = Path.GetFullPath(gameDirectory);
                baseline = Capture(this.gameDirectory);
                AtomicTomlMutationAdmission = new CampaignAtomicTomlMutationAdmission(this);
                StateDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "stfc-bridge-local-integration",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(StateDirectory);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or NotSupportedException
                    or ArgumentException
                    or CryptographicException)
            {
                throw new InvalidOperationException(
                    "The local recovery campaign could not capture its protected baseline or create isolated recovery state.");
            }
        }

        public string StateDirectory { get; }

        public IAtomicTomlMutationAdmission AtomicTomlMutationAdmission { get; }

        public void AssertBaseline(string message)
        {
            var current = Capture(gameDirectory);
            if (baseline.Count != current.Count
                || baseline.Any(pair =>
                    !current.TryGetValue(pair.Key, out var value)
                    || !pair.Value.Matches(value)))
            {
                throw new AssertFailedException(message);
            }
        }

        public void EmergencyRestore()
        {
            RecoverRetainedStages(
                gameDirectory,
                StateDirectory,
                gameProcessInspector,
                beforeMutation);
            createdStages.Clear();
            RestoreFile("version.dll");
            RestoreFile("community_patch_settings.toml");
        }

        public void RestoreConfigurationBaseline() =>
            RestoreFile("community_patch_settings.toml");

        public void WriteGameFileAtomically(string fileName, byte[] contents)
        {
            ValidateProtectedFileName(fileName);
            var path = Path.Combine(gameDirectory, fileName);
            var current = File.Exists(path) ? FileBaseline.Capture(path) : null;
            if (current is not null && !IsOwnedRevision(fileName, current))
            {
                throw new InvalidOperationException(
                    "The live protected file is not a campaign-owned revision and was preserved.");
            }
            ReplaceFileAtomically(path, contents, current, desiredMetadata: null);
            RecordOwnedGameFileRevision(fileName, contents);
        }

        public void RecordOwnedGameFileRevision(string fileName, byte[] contents)
        {
            ValidateProtectedFileName(fileName);
            var expectedSha256 = Convert.ToHexString(SHA256.HashData(contents));
            RecordOwnedGameFileRevision(fileName, expectedSha256);
        }

        public void EnsureGameClosedForMutation()
        {
            if (gameProcessInspector.Inspect(gameDirectory)
                != GameProcessInspectionState.NotRunning)
            {
                throw new InvalidOperationException(
                    "The exact opted-in integration installation started running before a direct harness mutation.");
            }
        }

        public void PreserveStateForRecovery() => preserveState = true;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            if (!preserveState && Directory.Exists(StateDirectory))
            {
                try
                {
                    Directory.Delete(StateDirectory, recursive: true);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    throw new AssertFailedException(
                        "The isolated %STATE_DIR% could not be removed; no raw path was emitted.");
                }
            }
        }

        private void RestoreFile(string fileName)
        {
            var path = Path.Combine(gameDirectory, fileName);
            if (!baseline.TryGetValue(fileName, out var original))
            {
                if (File.Exists(path))
                {
                    DeleteOwnedGameFile(fileName, path);
                }
                return;
            }

            var current = File.Exists(path)
                ? FileBaseline.Capture(path)
                : null;
            if (current is not null && original.Matches(current))
            {
                return;
            }
            if (current is null || !original.ContentsMatch(current))
            {
                if (current is not null && !IsOwnedRevision(fileName, current))
                {
                    throw new InvalidOperationException(
                        "The live protected file changed outside the campaign and was preserved.");
                }
                ReplaceFileAtomically(path, original.Contents!, current, original);
            }
            var metadataCurrent = FileBaseline.Capture(path);
            if (original.Identity is not null
                && metadataCurrent.Identity != original.Identity
                && !IsOwnedRevision(fileName, metadataCurrent))
            {
                throw new InvalidOperationException(
                    "The live protected file identity changed outside the campaign and was preserved.");
            }
            RestoreMetadata(path, original);
        }

        private void ReplaceFileAtomically(
            string path,
            byte[] contents,
            FileBaseline? expectedCurrent,
            FileBaseline? desiredMetadata)
        {
            var fileName = Path.GetFileName(path);
            var stagePath = Path.Combine(
                gameDirectory,
                $".stfc-bridge-integration-{fileName}.{Guid.NewGuid():N}.restore-stage");
            Exception? primaryFailure = null;
            try
            {
                AdmitMutation(DirectGameMutationKind.WriteRestoreStage, stagePath);
                PrepareStage(
                    RecoveryStageKind.WriteStage,
                    stagePath,
                    path,
                    contents.LongLength,
                    Convert.ToHexString(SHA256.HashData(contents)),
                    deletionAllowed: true,
                    committedDestinationSha256: null);
                var stageCreated = false;
                try
                {
                    EnsureGameClosedForMutation();
                    using var stream = new FileStream(
                        stagePath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.WriteThrough);
                    stageCreated = true;
                    MarkStageCreated(
                        stagePath,
                        CandidateFileNative.ReadIdentity(stream.SafeFileHandle));
                    stream.Write(contents);
                    stream.Flush(flushToDisk: true);
                }
                catch
                {
                    if (!stageCreated)
                    {
                        RemoveCreatedStageReceipt(stagePath);
                    }
                    throw;
                }
                if (desiredMetadata is not null)
                {
                    ApplyStageMetadata(stagePath, desiredMetadata);
                }
                CompleteStage(stagePath, deletionAllowed: true);
                AdmitMutation(DirectGameMutationKind.PromoteRestoreStage, path);
                if (expectedCurrent is null)
                {
                    if (File.Exists(path))
                    {
                        throw new InvalidOperationException(
                            "The protected file appeared before atomic promotion and was preserved.");
                    }
                    using var stage = OpenVerifiedStage(stagePath);
                    AdmitMutation(DirectGameMutationKind.CommitRestoreStage, path);
                    if (File.Exists(path))
                    {
                        throw new InvalidOperationException(
                            "The protected file appeared at the final promotion boundary and was preserved.");
                    }
                    stage.MoveNoReplace(path);
                    RecordOwnedGameFileRevision(
                        fileName,
                        stage.Identity,
                        Convert.ToHexString(SHA256.HashData(contents)));
                    RemoveCreatedStageReceipt(stagePath);
                }
                else
                {
                    using var stage = OpenVerifiedStage(stagePath);
                    var stageRevision = stage.CaptureRevision();
                    using var destination = ExactFileMutation.Open(path);
                    var promotionCurrent = FileBaseline.FromExact(destination);
                    if (!expectedCurrent.Matches(promotionCurrent))
                    {
                        throw new InvalidOperationException(
                            "The protected file changed before atomic promotion and was preserved.");
                    }
                    var rollbackPath = Path.Combine(
                        gameDirectory,
                        $".stfc-bridge-integration-{fileName}.{Guid.NewGuid():N}.restore-rollback");
                    PrepareStage(
                        RecoveryStageKind.Rollback,
                        rollbackPath,
                        path,
                        expectedCurrent.Length,
                        expectedCurrent.Sha256,
                        deletionAllowed: true,
                        committedDestinationSha256: stageRevision.Sha256,
                        expectedIdentity: destination.Identity,
                        expectedAttributes: promotionCurrent.Attributes,
                        expectedLastWriteTimeUtcTicks: promotionCurrent.LastWriteTimeUtcTicks,
                        committedDestinationAttributes: stageRevision.Attributes,
                        committedDestinationLastWriteTimeUtcTicks:
                            stageRevision.LastWriteTimeUtcTicks);
                    try
                    {
                        AdmitMutation(DirectGameMutationKind.CommitRestoreStage, path);
                        if (File.Exists(rollbackPath))
                        {
                            RemoveCreatedStageReceipt(rollbackPath);
                            throw new InvalidOperationException(
                                "The rollback path appeared before atomic promotion and was preserved.");
                        }
                        destination.MoveNoReplace(rollbackPath);
                        destination.Dispose();
                        stage.MoveNoReplace(path);
                        beforeMutation?.Invoke(
                            DirectGameMutationKind.AfterRestoreReplacement,
                            path);
                    }
                    catch
                    {
                        if (!File.Exists(rollbackPath))
                        {
                            RemoveCreatedStageReceipt(rollbackPath);
                        }
                        throw;
                    }

                    using (var rollback = ExactFileMutation.Open(rollbackPath))
                    {
                        if (rollback.Identity != destination.Identity)
                        {
                            throw new InvalidOperationException(
                                "The rollback identity changed after atomic promotion and was preserved.");
                        }
                        MarkStageCreated(rollbackPath, rollback.Identity);
                    }
                    CompleteStage(rollbackPath, deletionAllowed: true);
                    RecordOwnedGameFileRevision(
                        fileName,
                        stage.Identity,
                        Convert.ToHexString(SHA256.HashData(contents)));
                    RemoveCreatedStageReceipt(stagePath);
                    DeleteCreatedStage(rollbackPath);
                }
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
                throw;
            }
            finally
            {
                if (File.Exists(stagePath))
                {
                    try
                    {
                        DeleteCreatedStage(stagePath);
                    }
                    catch when (primaryFailure is not null)
                    {
                        // Preserve the original failure; exact-baseline validation will report residue.
                    }
                }
            }
        }

        private void RestoreMetadata(string path, FileBaseline original)
        {
            using var exact = ExactFileMutation.OpenForMetadata(path);
            var current = FileBaseline.FromExact(exact);
            if (!original.ContentsMatch(current))
            {
                throw new InvalidOperationException(
                    "The protected file changed before metadata restoration and was preserved.");
            }
            if (current.Attributes == original.Attributes
                && current.LastWriteTimeUtcTicks == original.LastWriteTimeUtcTicks)
            {
                return;
            }
            AdmitMutation(
                current.Attributes != original.Attributes
                    ? DirectGameMutationKind.RestoreAttributes
                    : DirectGameMutationKind.RestoreLastWriteTime,
                path);
            exact.SetMetadata(original.Attributes, original.LastWriteTimeUtcTicks);
        }

        private void ApplyStageMetadata(string path, FileBaseline desired)
        {
            using var exact = ExactFileMutation.OpenForMetadata(path);
            var current = FileBaseline.FromExact(exact);
            if (!desired.ContentsMatch(current))
            {
                throw new InvalidOperationException(
                    "The restore stage changed before metadata preparation and was preserved.");
            }
            AdmitMutation(DirectGameMutationKind.RestoreAttributes, path);
            exact.SetMetadata(desired.Attributes, desired.LastWriteTimeUtcTicks);
        }

        private ExactFileMutation OpenVerifiedStage(string path)
        {
            var canonicalPath = Path.GetFullPath(path);
            if (!createdStages.TryGetValue(canonicalPath, out var receipt)
                || receipt.Phase != RecoveryStagePhase.Complete
                || receipt.ExpectedIdentity is null)
            {
                throw new InvalidOperationException(
                    "The recovery harness cannot open an incomplete or unowned stage.");
            }
            var stage = ExactFileMutation.Open(canonicalPath);
            try
            {
                if (stage.Identity != receipt.ExpectedIdentity
                    || !ReceiptMatchesRevision(receipt, stage.CaptureRevision()))
                {
                    throw new InvalidOperationException(
                        "The recovery stage changed after its ownership receipt was recorded and was preserved.");
                }
                return stage;
            }
            catch
            {
                stage.Dispose();
                throw;
            }
        }

        private void DeleteOwnedGameFile(string fileName, string path)
        {
            if (!File.Exists(path))
            {
                return;
            }
            using var exact = ExactFileMutation.Open(path);
            var current = FileBaseline.FromExact(exact);
            if (!IsOwnedRevision(fileName, current))
            {
                throw new InvalidOperationException(
                    "The live protected file changed before cleanup and was preserved.");
            }
            AdmitMutation(DirectGameMutationKind.DeleteGameFile, path);
            exact.DeleteExact();
        }

        private void DeleteCreatedStage(string path)
        {
            var canonicalPath = Path.GetFullPath(path);
            if (!createdStages.TryGetValue(canonicalPath, out var receipt)
                || receipt.Phase != RecoveryStagePhase.Complete
                || receipt.ExpectedIdentity is null
                || !receipt.DeletionAllowed)
            {
                throw new InvalidOperationException(
                    "The recovery harness refused to delete an unowned stage path.");
            }
            using var stage = ExactFileMutation.Open(path);
            if (stage.Identity != receipt.ExpectedIdentity
                || !ReceiptMatchesRevision(receipt, stage.CaptureRevision()))
            {
                throw new InvalidOperationException(
                    "The recovery stage changed before exact cleanup and was preserved.");
            }
            AdmitMutation(DirectGameMutationKind.DeleteRestoreStage, path);
            stage.DeleteExact();
            RemoveCreatedStageReceipt(path);
        }

        private void AdmitMutation(DirectGameMutationKind kind, string path)
        {
            beforeMutation?.Invoke(kind, path);
            EnsureGameClosedForMutation();
        }

        private void AdmitAtomicTomlMutation(
            AtomicTomlMutationBoundary boundary,
            string temporaryPath,
            string destinationPath)
        {
            var kind = boundary switch
            {
                AtomicTomlMutationBoundary.TemporaryWrite =>
                    DirectGameMutationKind.WriteTomlTemporary,
                AtomicTomlMutationBoundary.Promotion =>
                    DirectGameMutationKind.PromoteTomlTemporary,
                AtomicTomlMutationBoundary.TemporaryDelete =>
                    DirectGameMutationKind.DeleteTomlTemporary,
                _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
            };
            var path = boundary == AtomicTomlMutationBoundary.Promotion
                ? destinationPath
                : temporaryPath;
            beforeMutation?.Invoke(kind, path);
            EnsureGameClosedForMutation();
            if (boundary == AtomicTomlMutationBoundary.TemporaryDelete)
            {
                var canonicalPath = Path.GetFullPath(temporaryPath);
                if (!createdStages.TryGetValue(canonicalPath, out var receipt)
                    || receipt.Phase is not (
                        RecoveryStagePhase.Created or RecoveryStagePhase.Complete)
                    || receipt.ExpectedIdentity is null
                    || !receipt.DeletionAllowed)
                {
                    throw new InvalidOperationException(
                        "The recovery harness refused to delete an unowned TOML stage path.");
                }
            }
        }

        private void PrepareStage(
            RecoveryStageKind kind,
            string stagePath,
            string destinationPath,
            long expectedSize,
            string expectedSha256,
            bool deletionAllowed,
            string? committedDestinationSha256,
            CandidateFileIdentity? expectedIdentity = null,
            FileAttributes? expectedAttributes = null,
            long? expectedLastWriteTimeUtcTicks = null,
            FileAttributes? committedDestinationAttributes = null,
            long? committedDestinationLastWriteTimeUtcTicks = null)
        {
            var receipt = CreateStageReceipt(
                gameDirectory,
                kind,
                stagePath,
                destinationPath,
                expectedSize,
                expectedSha256,
                deletionAllowed,
                committedDestinationSha256,
                expectedIdentity,
                expectedAttributes,
                expectedLastWriteTimeUtcTicks,
                committedDestinationAttributes,
                committedDestinationLastWriteTimeUtcTicks,
                RecoveryStagePhase.Prepared);
            createdStages[receipt.StagePath] = receipt;
            PersistCampaignStageReceipt(receipt);
        }

        private void MarkStageCreated(
            string stagePath,
            CandidateFileIdentity identity)
        {
            var canonicalPath = Path.GetFullPath(stagePath);
            if (!createdStages.TryGetValue(canonicalPath, out var receipt))
            {
                throw new InvalidOperationException(
                    "The recovery harness cannot authorize an unrecorded stage path.");
            }
            if (receipt.ExpectedIdentity is not null
                && receipt.ExpectedIdentity != identity)
            {
                throw new InvalidOperationException(
                    "The created recovery stage does not match its prepared file identity.");
            }
            receipt = receipt with
            {
                Phase = RecoveryStagePhase.Created,
                ExpectedIdentity = identity,
            };
            createdStages[canonicalPath] = receipt;
            PersistCampaignStageReceipt(receipt);
        }

        private void CompleteStage(string stagePath, bool deletionAllowed)
        {
            var canonicalPath = Path.GetFullPath(stagePath);
            if (!createdStages.TryGetValue(canonicalPath, out var receipt)
                || receipt.Phase != RecoveryStagePhase.Created)
            {
                throw new InvalidOperationException(
                    "The recovery harness cannot complete an uncreated stage path.");
            }
            using var stage = ExactFileMutation.Open(canonicalPath);
            var actual = stage.CaptureRevision();
            if (receipt.ExpectedIdentity is null
                || receipt.ExpectedIdentity != actual.Identity
                || receipt.Kind == RecoveryStageKind.WriteStage
                && (receipt.ExpectedSize != actual.Length
                    || !string.Equals(
                        receipt.ExpectedSha256,
                        actual.Sha256,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "The completed write stage does not match its prepared identity.");
            }
            receipt = receipt with
            {
                Phase = RecoveryStagePhase.Complete,
                ExpectedSize = actual.Length,
                ExpectedSha256 = actual.Sha256,
                ExpectedAttributes = actual.Attributes,
                ExpectedLastWriteTimeUtcTicks = actual.LastWriteTimeUtcTicks,
                DeletionAllowed = deletionAllowed,
            };
            createdStages[canonicalPath] = receipt;
            PersistCampaignStageReceipt(receipt);
        }

        private void PersistCampaignStageReceipt(RecoveryStageReceipt receipt)
        {
            beforeReceiptPersist?.Invoke(receipt.StagePath);
            PersistStageReceipt(StateDirectory, receipt);
        }

        private void RemoveCreatedStageReceipt(string stagePath)
        {
            var canonicalPath = Path.GetFullPath(stagePath);
            if (createdStages.TryGetValue(canonicalPath, out var receipt)
                && receipt.Kind == RecoveryStageKind.WriteStage
                && receipt.Phase == RecoveryStagePhase.Complete
                && receipt.ExpectedIdentity is not null
                && !File.Exists(canonicalPath)
                && File.Exists(receipt.DestinationPath))
            {
                RecordOwnedGameFileRevision(
                    Path.GetFileName(receipt.DestinationPath),
                    receipt.ExpectedIdentity,
                    receipt.ExpectedSha256);
            }
            createdStages.Remove(canonicalPath);
            var receiptPath = StageReceiptPath(StateDirectory, canonicalPath);
            if (File.Exists(receiptPath))
            {
                File.Delete(receiptPath);
            }
        }

        public static void RecoverRetainedStages(
            string gameDirectory,
            string stateDirectory,
            IGameProcessInspector gameProcessInspector,
            Action<DirectGameMutationKind, string>? beforeMutation = null)
        {
            if (!Directory.Exists(stateDirectory))
            {
                return;
            }
            foreach (var receiptPath in Directory.EnumerateFiles(
                         stateDirectory,
                         "recovery-stage-*.json",
                         SearchOption.TopDirectoryOnly))
            {
                var receipt = JsonSerializer.Deserialize<RecoveryStageReceipt>(
                    File.ReadAllBytes(receiptPath))
                    ?? throw new InvalidDataException("A recovery-stage receipt was empty.");
                ValidateStageReceipt(gameDirectory, receipt);
                if (!string.Equals(
                        Path.GetFullPath(receiptPath),
                        Path.GetFullPath(StageReceiptPath(stateDirectory, receipt.StagePath)),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "A recovery-stage receipt filename does not match its stage identity.");
                }
                if (!File.Exists(receipt.StagePath))
                {
                    File.Delete(receiptPath);
                    continue;
                }
                if (receipt.ExpectedIdentity is null)
                {
                    throw new InvalidOperationException(
                        "A prepared recovery path has no durable file identity and was preserved.");
                }
                using var stage = ExactFileMutation.Open(receipt.StagePath);
                if (stage.Identity != receipt.ExpectedIdentity)
                {
                    throw new InvalidOperationException(
                        "A retained recovery path no longer has its recorded file identity and was preserved.");
                }
                if (receipt.Kind == RecoveryStageKind.Rollback)
                {
                    if (!ReceiptMatchesRevision(receipt, stage.CaptureRevision()))
                    {
                        throw new InvalidOperationException(
                            "A retained rollback changed before recovery and was preserved.");
                    }
                    if (!File.Exists(receipt.DestinationPath))
                    {
                        beforeMutation?.Invoke(
                            DirectGameMutationKind.RestoreRollback,
                            receipt.DestinationPath);
                        EnsureGameClosedForRecovery(gameDirectory, gameProcessInspector);
                        stage.MoveNoReplace(receipt.DestinationPath);
                        File.Delete(receiptPath);
                        continue;
                    }
                    using var destination = ExactFileMutation.Open(receipt.DestinationPath);
                    if (!DestinationMatchesCommitted(
                            receipt,
                            destination.CaptureRevision()))
                    {
                        throw new InvalidOperationException(
                            "The recovery destination changed and every retained revision was preserved.");
                    }
                    if (!receipt.DeletionAllowed)
                    {
                        throw new InvalidOperationException(
                            "A rollback without deletion authority was preserved.");
                    }
                    beforeMutation?.Invoke(
                        DirectGameMutationKind.DeleteRestoreStage,
                        receipt.StagePath);
                    EnsureGameClosedForRecovery(gameDirectory, gameProcessInspector);
                    stage.DeleteExact();
                    File.Delete(receiptPath);
                    continue;
                }
                else if (receipt.Phase == RecoveryStagePhase.Complete
                    && !ReceiptMatchesRevision(receipt, stage.CaptureRevision()))
                {
                    throw new InvalidOperationException(
                        "A completed recovery stage changed before recovery and was preserved.");
                }

                if (!receipt.DeletionAllowed)
                {
                    throw new InvalidOperationException(
                        "A recovery stage without deletion authority was preserved.");
                }
                beforeMutation?.Invoke(
                    DirectGameMutationKind.DeleteRestoreStage,
                    receipt.StagePath);
                EnsureGameClosedForRecovery(gameDirectory, gameProcessInspector);
                stage.DeleteExact();
                File.Delete(receiptPath);
            }
        }

        private static void EnsureGameClosedForRecovery(
            string gameDirectory,
            IGameProcessInspector gameProcessInspector)
        {
            if (gameProcessInspector.Inspect(gameDirectory)
                != GameProcessInspectionState.NotRunning)
            {
                throw new InvalidOperationException(
                    "The exact opted-in integration installation started running before recovery mutation.");
            }
        }

        private static RecoveryStageReceipt CreateStageReceipt(
            string gameDirectory,
            RecoveryStageKind kind,
            string stagePath,
            string destinationPath,
            long expectedSize,
            string expectedSha256,
            bool deletionAllowed,
            string? committedDestinationSha256,
            CandidateFileIdentity? expectedIdentity,
            FileAttributes? expectedAttributes,
            long? expectedLastWriteTimeUtcTicks,
            FileAttributes? committedDestinationAttributes,
            long? committedDestinationLastWriteTimeUtcTicks,
            RecoveryStagePhase phase)
        {
            var receipt = new RecoveryStageReceipt(
                1,
                kind,
                phase,
                Path.GetFullPath(stagePath),
                Path.GetFullPath(destinationPath),
                expectedSize,
                expectedSha256,
                deletionAllowed,
                committedDestinationSha256,
                expectedIdentity,
                expectedAttributes,
                expectedLastWriteTimeUtcTicks,
                committedDestinationAttributes,
                committedDestinationLastWriteTimeUtcTicks);
            ValidateStageReceipt(gameDirectory, receipt);
            return receipt;
        }

        private static void ValidateStageReceipt(
            string gameDirectory,
            RecoveryStageReceipt receipt)
        {
            if (receipt.SchemaVersion != 1
                || !Enum.IsDefined(receipt.Kind)
                || !Enum.IsDefined(receipt.Phase)
                || receipt.ExpectedSize < 0
                || string.IsNullOrWhiteSpace(receipt.StagePath)
                || string.IsNullOrWhiteSpace(receipt.DestinationPath)
                || string.IsNullOrWhiteSpace(receipt.ExpectedSha256)
                || receipt.ExpectedSha256.Length != 64
                || receipt.ExpectedSha256.Any(character => !Uri.IsHexDigit(character))
                || (receipt.Phase != RecoveryStagePhase.Prepared
                    && receipt.ExpectedIdentity is null)
                || ((receipt.ExpectedAttributes is null)
                    != (receipt.ExpectedLastWriteTimeUtcTicks is null))
                || (receipt.Phase == RecoveryStagePhase.Complete
                    && (receipt.ExpectedAttributes is null
                        || receipt.ExpectedLastWriteTimeUtcTicks is null))
                || ((receipt.CommittedDestinationAttributes is null)
                    != (receipt.CommittedDestinationLastWriteTimeUtcTicks is null))
                || (receipt.Kind == RecoveryStageKind.Rollback
                    && (string.IsNullOrWhiteSpace(receipt.CommittedDestinationSha256)
                        || receipt.CommittedDestinationSha256.Length != 64
                        || receipt.CommittedDestinationSha256.Any(
                            character => !Uri.IsHexDigit(character))
                        || receipt.ExpectedIdentity is null
                        || receipt.ExpectedAttributes is null
                        || receipt.ExpectedLastWriteTimeUtcTicks is null
                        || receipt.CommittedDestinationAttributes is null
                        || receipt.CommittedDestinationLastWriteTimeUtcTicks is null)))
            {
                throw new InvalidDataException("A recovery-stage receipt is invalid.");
            }
            var canonicalGameDirectory = Path.GetFullPath(gameDirectory);
            var canonicalStagePath = Path.GetFullPath(receipt.StagePath);
            var canonicalDestinationPath = Path.GetFullPath(receipt.DestinationPath);
            if (!string.Equals(
                    Path.GetDirectoryName(canonicalStagePath),
                    canonicalGameDirectory,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetDirectoryName(canonicalDestinationPath),
                    canonicalGameDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A recovery-stage receipt escaped the exact opted-in installation.");
            }
            var destinationName = Path.GetFileName(canonicalDestinationPath);
            ValidateProtectedFileName(destinationName);
            var stageName = Path.GetFileName(canonicalStagePath);
            var directPrefix = $".stfc-bridge-integration-{destinationName}.";
            var atomicPrefix = $".{destinationName}.";
            var isWriteStage = HasGuidIdentity(stageName, directPrefix, ".restore-stage")
                || HasGuidIdentity(stageName, atomicPrefix, ".tmp");
            var isRollback = HasGuidIdentity(stageName, directPrefix, ".restore-rollback")
                || HasGuidIdentity(stageName, atomicPrefix, ".rollback");
            if ((receipt.Kind == RecoveryStageKind.WriteStage && !isWriteStage)
                || (receipt.Kind == RecoveryStageKind.Rollback && !isRollback))
            {
                throw new InvalidDataException(
                    "A recovery-stage receipt does not name a harness-owned stage pattern.");
            }
        }

        private static bool HasGuidIdentity(
            string fileName,
            string prefix,
            string suffix)
        {
            if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
                || !fileName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }
            var identity = fileName[prefix.Length..^suffix.Length];
            return Guid.TryParseExact(identity, "N", out _);
        }

        private static void EnsureStageMatches(RecoveryStageReceipt receipt)
        {
            if (receipt.Phase != RecoveryStagePhase.Complete)
            {
                throw new InvalidOperationException(
                    "An incomplete recovery stage was preserved.");
            }
            using var stage = ExactFileMutation.Open(receipt.StagePath);
            if (receipt.ExpectedIdentity is null
                || stage.Identity != receipt.ExpectedIdentity
                || !ReceiptMatchesRevision(receipt, stage.CaptureRevision()))
            {
                throw new InvalidOperationException(
                    "A recovery stage changed after its ownership receipt was recorded and was preserved.");
            }
        }

        private static bool StageBytesMatch(RecoveryStageReceipt receipt)
        {
            using var stage = ExactFileMutation.Open(receipt.StagePath);
            return receipt.ExpectedIdentity is not null
                && stage.Identity == receipt.ExpectedIdentity
                && ReceiptMatchesRevision(receipt, stage.CaptureRevision());
        }

        private static bool ReceiptMatchesRevision(
            RecoveryStageReceipt receipt,
            ExactFileRevision revision) =>
            revision.Length == receipt.ExpectedSize
            && string.Equals(
                revision.Sha256,
                receipt.ExpectedSha256,
                StringComparison.Ordinal)
            && (receipt.ExpectedAttributes is null
                || revision.Attributes == receipt.ExpectedAttributes)
            && (receipt.ExpectedLastWriteTimeUtcTicks is null
                || revision.LastWriteTimeUtcTicks == receipt.ExpectedLastWriteTimeUtcTicks);

        private static bool DestinationMatchesCommitted(
            RecoveryStageReceipt receipt,
            ExactFileRevision destination)
        {
            return string.Equals(
                destination.Sha256,
                receipt.CommittedDestinationSha256,
                StringComparison.Ordinal)
                && destination.Attributes == receipt.CommittedDestinationAttributes
                && destination.LastWriteTimeUtcTicks
                    == receipt.CommittedDestinationLastWriteTimeUtcTicks;
        }

        private static string StageReceiptPath(string stateDirectory, string stagePath)
        {
            var identity = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(Path.GetFullPath(stagePath).ToUpperInvariant())));
            return Path.Combine(stateDirectory, $"recovery-stage-{identity}.json");
        }

        private static void PersistStageReceipt(
            string stateDirectory,
            RecoveryStageReceipt receipt)
        {
            var receiptPath = StageReceiptPath(stateDirectory, receipt.StagePath);
            var temporaryPath = receiptPath + $".{Guid.NewGuid():N}.tmp";
            var contents = JsonSerializer.SerializeToUtf8Bytes(receipt);
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(contents);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(receiptPath))
                {
                    File.Replace(temporaryPath, receiptPath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, receiptPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        public void RecordOwnedGameFileRevision(string fileName, string sha256)
        {
            ValidateProtectedFileName(fileName);
            ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
            var path = Path.Combine(gameDirectory, fileName);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    "The campaign cannot record ownership for a missing protected file.");
            }
            using var exact = ExactFileMutation.Open(path);
            var live = FileBaseline.FromExact(exact);
            if (!string.Equals(live.Sha256, sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The live protected file does not match its completed mutation receipt.");
            }
            RecordOwnedGameFileRevision(fileName, live.Identity!, sha256);
        }

        private void RecordOwnedGameFileRevision(
            string fileName,
            CandidateFileIdentity identity,
            string sha256)
        {
            if (!ownedFileRevisions.TryGetValue(fileName, out var revisions))
            {
                revisions = [];
                ownedFileRevisions.Add(fileName, revisions);
            }
            revisions.Add(new(identity, sha256));
        }

        private bool IsOwnedRevision(string fileName, FileBaseline current) =>
            current.Identity is not null
            &&
            ownedFileRevisions.TryGetValue(fileName, out var revisions)
            && revisions.Contains(new(current.Identity, current.Sha256));

        private sealed record OwnedFileRevision(
            CandidateFileIdentity Identity,
            string Sha256);

        private static void EnsureContentsMatch(string path, FileBaseline expected)
        {
            var current = FileBaseline.Capture(path);
            if (!expected.ContentsMatch(current))
            {
                throw new InvalidOperationException(
                    "The protected file changed before metadata restoration and was preserved.");
            }
        }

        private static void ValidateProtectedFileName(string fileName)
        {
            if (fileName is not ("version.dll" or "community_patch_settings.toml"))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fileName),
                    "The recovery harness owns only its two protected game files.");
            }
        }

        private static Dictionary<string, FileBaseline> Capture(string directory) =>
            Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
                .ToDictionary(
                    path => Path.GetFileName(path)
                        ?? throw new InvalidDataException("A game target entry has no file name."),
                    path => FileBaseline.Capture(path),
                    StringComparer.OrdinalIgnoreCase);

        private sealed record FileBaseline(
            FileAttributes Attributes,
            long Length,
            long LastWriteTimeUtcTicks,
            string Sha256,
            byte[]? Contents)
        {
            public CandidateFileIdentity? Identity { get; init; }

            public static FileBaseline Capture(string path)
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    return new(
                        attributes,
                        -1,
                        File.GetLastWriteTimeUtc(path).Ticks,
                        string.Empty,
                        null);
                }
                var fileInfo = new FileInfo(path);
                if (Path.GetFileName(path) is "version.dll" or "community_patch_settings.toml")
                {
                    if (fileInfo.Length > 128L * 1024L * 1024L)
                    {
                        throw new InvalidDataException(
                            "A restorable integration baseline file exceeds 128 MiB.");
                    }
                    using var exact = ExactFileMutation.Open(path);
                    return FromExact(exact, includeContents: true);
                }
                using var stream = File.OpenRead(path);
                return new(
                    attributes,
                    fileInfo.Length,
                    File.GetLastWriteTimeUtc(path).Ticks,
                    Convert.ToHexString(SHA256.HashData(stream)),
                    null);
            }

            public static FileBaseline FromExact(
                ExactFileMutation exact,
                bool includeContents = false)
            {
                var revision = exact.CaptureRevision();
                return new(
                    revision.Attributes,
                    revision.Length,
                    revision.LastWriteTimeUtcTicks,
                    revision.Sha256,
                    includeContents ? exact.ReadAllBytes() : null)
                {
                    Identity = revision.Identity,
                };
            }

            public bool Matches(FileBaseline other) =>
                Attributes == other.Attributes
                && Length == other.Length
                && LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks
                && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);

            public bool ContentsMatch(FileBaseline other) =>
                Length == other.Length
                && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);

            public static FileBaseline FromContents(byte[] contents) =>
                new(
                    FileAttributes.Normal,
                    contents.LongLength,
                    0,
                    Convert.ToHexString(SHA256.HashData(contents)),
                    contents);
        }

        private enum RecoveryStageKind
        {
            WriteStage,
            Rollback,
        }

        private enum RecoveryStagePhase
        {
            Prepared,
            Created,
            Complete,
        }

        private sealed record RecoveryStageReceipt(
            int SchemaVersion,
            RecoveryStageKind Kind,
            RecoveryStagePhase Phase,
            string StagePath,
            string DestinationPath,
            long ExpectedSize,
            string ExpectedSha256,
            bool DeletionAllowed,
            string? CommittedDestinationSha256,
            CandidateFileIdentity? ExpectedIdentity,
            FileAttributes? ExpectedAttributes,
            long? ExpectedLastWriteTimeUtcTicks,
            FileAttributes? CommittedDestinationAttributes,
            long? CommittedDestinationLastWriteTimeUtcTicks);

        private sealed class CampaignAtomicTomlMutationAdmission(
            RestorableGameInstallCampaign campaign) : IAtomicTomlMutationAdmission
        {
            public ValueTask AdmitAsync(
                AtomicTomlMutationBoundary boundary,
                string temporaryPath,
                string destinationPath,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                campaign.AdmitAtomicTomlMutation(
                    boundary,
                    temporaryPath,
                    destinationPath);
                return ValueTask.CompletedTask;
            }

            public void TemporaryPreparing(
                AtomicTomlTemporaryRole role,
                string temporaryPath,
                string destinationPath,
                long expectedSize,
                string expectedSha256,
                bool deletionAllowed,
                string? committedDestinationSha256,
                CandidateFileIdentity? expectedIdentity = null,
                FileAttributes? expectedAttributes = null,
                long? expectedLastWriteTimeUtcTicks = null,
                FileAttributes? committedDestinationAttributes = null,
                long? committedDestinationLastWriteTimeUtcTicks = null) =>
                campaign.PrepareStage(
                    role == AtomicTomlTemporaryRole.Rollback
                        ? RecoveryStageKind.Rollback
                        : RecoveryStageKind.WriteStage,
                    temporaryPath,
                    destinationPath,
                    expectedSize,
                    expectedSha256,
                    deletionAllowed,
                    committedDestinationSha256,
                    expectedIdentity,
                    expectedAttributes,
                    expectedLastWriteTimeUtcTicks,
                    committedDestinationAttributes,
                    committedDestinationLastWriteTimeUtcTicks);

            public void TemporaryCreated(
                AtomicTomlTemporaryRole role,
                string temporaryPath,
                CandidateFileIdentity identity) =>
                campaign.MarkStageCreated(temporaryPath, identity);

            public void TemporaryCompleted(
                AtomicTomlTemporaryRole role,
                string temporaryPath,
                long actualSize,
                string actualSha256,
                bool deletionAllowed) =>
                campaign.CompleteStage(temporaryPath, deletionAllowed);

            public void TemporaryRemoved(string temporaryPath) =>
                campaign.RemoveCreatedStageReceipt(temporaryPath);

            public void VerifyCommitAllowed(
                AtomicTomlMutationBoundary boundary,
                string temporaryPath,
                string destinationPath) =>
                campaign.EnsureGameClosedForMutation();
        }
    }
}
