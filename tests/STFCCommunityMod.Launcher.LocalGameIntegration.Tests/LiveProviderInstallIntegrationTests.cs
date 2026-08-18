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
        if (new SystemGameProcessInspector().Inspect(gameDirectory) != GameProcessInspectionState.NotRunning)
        {
            Assert.Fail(
                "The exact opted-in integration installation is running or cannot be attributed safely.");
        }

        using var campaign = new RestorableGameInstallCampaign(gameDirectory);
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
        if (new SystemGameProcessInspector().Inspect(gameDirectory) != GameProcessInspectionState.NotRunning)
        {
            Assert.Fail(
                "The exact opted-in integration installation is running or cannot be attributed safely.");
        }
        RequireKnownGuffawaffleStableArtifact(gameDirectory);

        using var campaign = new RestorableGameInstallCampaign(gameDirectory);
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
        private readonly Dictionary<string, HashSet<string>> ownedFileRevisions =
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
            this.gameDirectory = Path.GetFullPath(gameDirectory);
            this.gameProcessInspector = gameProcessInspector ?? new SystemGameProcessInspector();
            this.beforeMutation = beforeMutation;
            this.beforeReceiptPersist = beforeReceiptPersist;
            baseline = Capture(this.gameDirectory);
            AtomicTomlMutationAdmission = new CampaignAtomicTomlMutationAdmission(this);
            StateDirectory = Path.Combine(
                Path.GetTempPath(),
                "stfc-bridge-local-integration",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(StateDirectory);
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
            ReplaceFileAtomically(path, contents, current);
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
                ReplaceFileAtomically(path, original.Contents!, current);
            }
            RestoreMetadata(path, original);
        }

        private void ReplaceFileAtomically(
            string path,
            byte[] contents,
            FileBaseline? expectedCurrent)
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
                    using var stream = new FileStream(
                        stagePath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.WriteThrough);
                    stageCreated = true;
                    MarkStageCreated(stagePath);
                    stream.Write(contents);
                    stream.Flush(flushToDisk: true);
                    CompleteStage(
                        stagePath,
                        contents.LongLength,
                        Convert.ToHexString(SHA256.HashData(contents)),
                        deletionAllowed: true);
                }
                catch
                {
                    if (!stageCreated)
                    {
                        RemoveCreatedStageReceipt(stagePath);
                    }
                    throw;
                }
                AdmitMutation(DirectGameMutationKind.PromoteRestoreStage, path);
                if (expectedCurrent is null)
                {
                    if (File.Exists(path))
                    {
                        throw new InvalidOperationException(
                            "The protected file appeared before atomic promotion and was preserved.");
                    }
                    AdmitMutation(DirectGameMutationKind.CommitRestoreStage, path);
                    EnsureStageMatches(createdStages[Path.GetFullPath(stagePath)]);
                    if (File.Exists(path))
                    {
                        throw new InvalidOperationException(
                            "The protected file appeared at the final promotion boundary and was preserved.");
                    }
                    EnsureGameClosedForMutation();
                    File.Move(stagePath, path);
                    RecordOwnedGameFileRevision(fileName, contents);
                    RemoveCreatedStageReceipt(stagePath);
                }
                else
                {
                    var promotionCurrent = File.Exists(path)
                        ? FileBaseline.Capture(path)
                        : null;
                    if (promotionCurrent is null || !expectedCurrent.Matches(promotionCurrent))
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
                        committedDestinationSha256: Convert.ToHexString(
                            SHA256.HashData(contents)));
                    try
                    {
                        AdmitMutation(DirectGameMutationKind.CommitRestoreStage, path);
                        EnsureStageMatches(createdStages[Path.GetFullPath(stagePath)]);
                        if (File.Exists(rollbackPath))
                        {
                            RemoveCreatedStageReceipt(rollbackPath);
                            throw new InvalidOperationException(
                                "The rollback path appeared before atomic promotion and was preserved.");
                        }
                        EnsureGameClosedForMutation();
                        File.Replace(
                            stagePath,
                            path,
                            rollbackPath,
                            ignoreMetadataErrors: true);
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

                    MarkStageCreated(rollbackPath);
                    var displaced = FileBaseline.Capture(rollbackPath);
                    var displacedMatchesExpected = expectedCurrent.Matches(displaced);
                    CompleteStage(
                        rollbackPath,
                        displaced.Length,
                        displaced.Sha256,
                        deletionAllowed: displacedMatchesExpected);
                    RecordOwnedGameFileRevision(fileName, contents);
                    RemoveCreatedStageReceipt(stagePath);
                    if (!displacedMatchesExpected)
                    {
                        var promoted = FileBaseline.Capture(path);
                        if (!promoted.ContentsMatch(FileBaseline.FromContents(contents)))
                        {
                            throw new InvalidOperationException(
                                "The protected file changed again after promotion; all conflicting bytes were preserved.");
                        }
                        AdmitMutation(DirectGameMutationKind.RestoreRollback, path);
                        EnsureStageMatches(createdStages[Path.GetFullPath(rollbackPath)]);
                        EnsureContentsMatch(path, FileBaseline.FromContents(contents));
                        EnsureGameClosedForMutation();
                        File.Replace(
                            rollbackPath,
                            path,
                            null,
                            ignoreMetadataErrors: true);
                        RemoveCreatedStageReceipt(rollbackPath);
                        throw new InvalidOperationException(
                            "The protected file changed during atomic promotion and was restored unchanged.");
                    }
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
            var attributes = File.GetAttributes(path);
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path).Ticks;
            if (lastWriteTimeUtc != original.LastWriteTimeUtcTicks
                && attributes.HasFlag(FileAttributes.ReadOnly))
            {
                AdmitMutation(DirectGameMutationKind.RestoreAttributes, path);
                EnsureContentsMatch(path, original);
                EnsureGameClosedForMutation();
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
            if (lastWriteTimeUtc != original.LastWriteTimeUtcTicks)
            {
                AdmitMutation(DirectGameMutationKind.RestoreLastWriteTime, path);
                EnsureContentsMatch(path, original);
                EnsureGameClosedForMutation();
                File.SetLastWriteTimeUtc(
                    path,
                    new(original.LastWriteTimeUtcTicks, DateTimeKind.Utc));
            }
            if (File.GetAttributes(path) != original.Attributes)
            {
                AdmitMutation(DirectGameMutationKind.RestoreAttributes, path);
                EnsureContentsMatch(path, original);
                EnsureGameClosedForMutation();
                File.SetAttributes(path, original.Attributes);
            }
        }

        private void DeleteOwnedGameFile(string fileName, string path)
        {
            AdmitMutation(DirectGameMutationKind.DeleteGameFile, path);
            var current = File.Exists(path) ? FileBaseline.Capture(path) : null;
            if (current is null)
            {
                return;
            }
            if (!IsOwnedRevision(fileName, current))
            {
                throw new InvalidOperationException(
                    "The live protected file changed before cleanup and was preserved.");
            }
            EnsureGameClosedForMutation();
            File.Delete(path);
        }

        private void DeleteCreatedStage(string path)
        {
            var canonicalPath = Path.GetFullPath(path);
            if (!createdStages.TryGetValue(canonicalPath, out var receipt)
                || receipt.Phase != RecoveryStagePhase.Complete
                || !receipt.DeletionAllowed)
            {
                throw new InvalidOperationException(
                    "The recovery harness refused to delete an unowned stage path.");
            }
            AdmitMutation(DirectGameMutationKind.DeleteRestoreStage, path);
            EnsureStageMatches(receipt);
            EnsureGameClosedForMutation();
            File.Delete(path);
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
                    || receipt.Phase != RecoveryStagePhase.Complete
                    || !receipt.DeletionAllowed)
                {
                    throw new InvalidOperationException(
                        "The recovery harness refused to delete an unowned TOML stage path.");
                }
                EnsureStageMatches(receipt);
            }
        }

        private void PrepareStage(
            RecoveryStageKind kind,
            string stagePath,
            string destinationPath,
            long expectedSize,
            string expectedSha256,
            bool deletionAllowed,
            string? committedDestinationSha256)
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
                RecoveryStagePhase.Prepared);
            createdStages[receipt.StagePath] = receipt;
            PersistCampaignStageReceipt(receipt);
        }

        private void MarkStageCreated(string stagePath)
        {
            var canonicalPath = Path.GetFullPath(stagePath);
            if (!createdStages.TryGetValue(canonicalPath, out var receipt))
            {
                throw new InvalidOperationException(
                    "The recovery harness cannot authorize an unrecorded stage path.");
            }
            receipt = receipt with { Phase = RecoveryStagePhase.Created };
            createdStages[canonicalPath] = receipt;
            PersistCampaignStageReceipt(receipt);
        }

        private void CompleteStage(
            string stagePath,
            long actualSize,
            string actualSha256,
            bool deletionAllowed)
        {
            var canonicalPath = Path.GetFullPath(stagePath);
            if (!createdStages.TryGetValue(canonicalPath, out var receipt)
                || receipt.Phase != RecoveryStagePhase.Created)
            {
                throw new InvalidOperationException(
                    "The recovery harness cannot complete an uncreated stage path.");
            }
            if (receipt.Kind == RecoveryStageKind.WriteStage
                && (receipt.ExpectedSize != actualSize
                    || !string.Equals(
                        receipt.ExpectedSha256,
                        actualSha256,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "The completed write stage does not match its prepared identity.");
            }
            receipt = receipt with
            {
                Phase = RecoveryStagePhase.Complete,
                ExpectedSize = actualSize,
                ExpectedSha256 = actualSha256,
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
                if (receipt.Kind == RecoveryStageKind.Rollback
                    && receipt.Phase == RecoveryStagePhase.Prepared)
                {
                    beforeMutation?.Invoke(
                        DirectGameMutationKind.DeleteRestoreStage,
                        receipt.StagePath);
                    if (!StageBytesMatch(receipt)
                        || !DestinationMatchesCommitted(receipt))
                    {
                        throw new InvalidOperationException(
                            "A prepared rollback changed before recovery and was preserved.");
                    }
                    if (gameProcessInspector.Inspect(gameDirectory)
                        != GameProcessInspectionState.NotRunning)
                    {
                        throw new InvalidOperationException(
                            "The exact opted-in integration installation started running before rollback recovery.");
                    }
                    File.Delete(receipt.StagePath);
                    File.Delete(receiptPath);
                    continue;
                }
                if (receipt.Phase != RecoveryStagePhase.Complete
                    || !receipt.DeletionAllowed)
                {
                    throw new InvalidOperationException(
                        "An incomplete or externally changed recovery stage was preserved.");
                }
                beforeMutation?.Invoke(DirectGameMutationKind.DeleteRestoreStage, receipt.StagePath);
                EnsureStageMatches(receipt);
                if (gameProcessInspector.Inspect(gameDirectory)
                    != GameProcessInspectionState.NotRunning)
                {
                    throw new InvalidOperationException(
                        "The exact opted-in integration installation started running before stage recovery.");
                }
                File.Delete(receipt.StagePath);
                File.Delete(receiptPath);
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
                committedDestinationSha256);
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
                || (receipt.Kind == RecoveryStageKind.Rollback
                    && (string.IsNullOrWhiteSpace(receipt.CommittedDestinationSha256)
                        || receipt.CommittedDestinationSha256.Length != 64
                        || receipt.CommittedDestinationSha256.Any(
                            character => !Uri.IsHexDigit(character)))))
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
            if (!StageBytesMatch(receipt))
            {
                throw new InvalidOperationException(
                    "A recovery stage changed after its ownership receipt was recorded and was preserved.");
            }
        }

        private static bool StageBytesMatch(RecoveryStageReceipt receipt)
        {
            var current = FileBaseline.Capture(receipt.StagePath);
            return current.Length == receipt.ExpectedSize
                && string.Equals(
                    current.Sha256,
                    receipt.ExpectedSha256,
                    StringComparison.Ordinal);
        }

        private static bool DestinationMatchesCommitted(RecoveryStageReceipt receipt)
        {
            if (string.IsNullOrWhiteSpace(receipt.CommittedDestinationSha256)
                || !File.Exists(receipt.DestinationPath))
            {
                return false;
            }
            using var stream = File.OpenRead(receipt.DestinationPath);
            return string.Equals(
                Convert.ToHexString(SHA256.HashData(stream)),
                receipt.CommittedDestinationSha256,
                StringComparison.Ordinal);
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
            using (var stream = File.OpenRead(path))
            {
                var liveSha256 = Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(liveSha256, sha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The live protected file does not match its completed mutation receipt.");
                }
            }
            if (!ownedFileRevisions.TryGetValue(fileName, out var revisions))
            {
                revisions = new(StringComparer.Ordinal);
                ownedFileRevisions.Add(fileName, revisions);
            }
            revisions.Add(sha256);
        }

        private bool IsOwnedRevision(string fileName, FileBaseline current) =>
            ownedFileRevisions.TryGetValue(fileName, out var revisions)
            && revisions.Contains(current.Sha256);

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
                byte[]? contents = null;
                if (Path.GetFileName(path) is "version.dll" or "community_patch_settings.toml")
                {
                    if (fileInfo.Length > 128L * 1024L * 1024L)
                    {
                        throw new InvalidDataException(
                            "A restorable integration baseline file exceeds 128 MiB.");
                    }
                    contents = File.ReadAllBytes(path);
                }
                using var stream = File.OpenRead(path);
                return new(
                    attributes,
                    fileInfo.Length,
                    File.GetLastWriteTimeUtc(path).Ticks,
                    Convert.ToHexString(SHA256.HashData(stream)),
                    contents);
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
            string? CommittedDestinationSha256);

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
                string? committedDestinationSha256) =>
                campaign.PrepareStage(
                    role == AtomicTomlTemporaryRole.Rollback
                        ? RecoveryStageKind.Rollback
                        : RecoveryStageKind.WriteStage,
                    temporaryPath,
                    destinationPath,
                    expectedSize,
                    expectedSha256,
                    deletionAllowed,
                    committedDestinationSha256);

            public void TemporaryCreated(
                AtomicTomlTemporaryRole role,
                string temporaryPath) =>
                campaign.MarkStageCreated(temporaryPath);

            public void TemporaryCompleted(
                AtomicTomlTemporaryRole role,
                string temporaryPath,
                long actualSize,
                string actualSha256,
                bool deletionAllowed) =>
                campaign.CompleteStage(
                    temporaryPath,
                    actualSize,
                    actualSha256,
                    deletionAllowed);

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
