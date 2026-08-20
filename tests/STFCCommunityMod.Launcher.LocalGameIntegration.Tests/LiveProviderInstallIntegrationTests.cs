using System.Security.Cryptography;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.LocalGameIntegration.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("LocalGameMutation")]
public sealed partial class LiveProviderInstallIntegrationTests
{
    private const string RuntimeManifestFileName = "stfc-runtime-manifest.json";
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
        if (!IsCleanProviderJourneyTarget(gameDirectory))
        {
            Assert.Inconclusive(
                "The provider install/switch journey requires a clean target without version.dll, "
                    + "runtime manifest, or TOML; "
                    + "the separately isolated manual-adoption journey can still run on this target.");
        }
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
                    httpClient,
                    campaign));
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

        await FinishLiveCampaignAsync(
            gameDirectory,
            campaign,
            endpoints,
            journeyFailure,
            "The live provider campaign failed; isolated recovery state was retained.")
            .ConfigureAwait(false);
    }

    [TestMethod]
    [Timeout(600_000)]
    public async Task ManualDeveloperDllAdoptionRestoresExactFilesAndLeavesNoResidue()
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
        if (!File.Exists(Path.Combine(gameDirectory, "version.dll")))
        {
            Assert.Inconclusive(
                "The manual-adoption journey requires an existing human-managed version.dll; "
                    + "it never replaces human files with a test fixture before production adoption begins.");
        }
        var catalog = LoadProviderCatalog();
        var reviewed = LoadReviewedReleases(catalog);
        using var httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        var endpoint = CreateEndpoint(
            catalog.GetProvider("guffawaffle"),
            reviewed,
            campaign.StateDirectory,
            httpClient,
            campaign);
        var endpoints = new Dictionary<string, ProviderEndpoint>(StringComparer.Ordinal)
        {
            [endpoint.ProviderId] = endpoint,
        };

        Exception? journeyFailure = null;
        try
        {
            await ManualAdoptionRoundTripAsync(
                endpoint,
                gameDirectory,
                campaign).ConfigureAwait(false);
            campaign.AssertBaseline(
                "Manual adoption did not restore the exact game target.");
            TestContext.WriteLine(
                "manual adoption: refusal made no download or write; explicit adoption and removal restored exact developer bytes");
        }
        catch (Exception exception)
        {
            journeyFailure = exception;
        }

        await FinishLiveCampaignAsync(
            gameDirectory,
            campaign,
            endpoints,
            journeyFailure,
            "The manual-adoption campaign failed; isolated recovery state was retained.")
            .ConfigureAwait(false);
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
        Action? beforeDirectConfigurationMutation = null,
        byte[]? createdConfigurationContents = null,
        LauncherConfigurationDiagnosisEvidence? preflightEvidence = null)
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
            configurationPath,
            diagnosisEvidence: preflightEvidence,
            atomicTomlMutationAdmission: campaign.AtomicTomlMutationAdmission);
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
            var create = await ExecuteDirectConfigurationMutationAsync(
                campaign,
                beforeDirectConfigurationMutation,
                () => new TomlConfigurationRepository(
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
                        baselineExisted: false))).ConfigureAwait(false);
            Assert.IsTrue(create.IsSuccess, create.Error);
            campaign.RecordOwnedGameFileRevision(
                "community_patch_settings.toml",
                original);
        }

        var mutate = await ExecuteDirectConfigurationMutationAsync(
            campaign,
            beforeDirectConfigurationMutation,
            () => new TomlConfigurationRepository(
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
                    changed))).ConfigureAwait(false);
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
            configurationPath,
            atomicTomlMutationAdmission: campaign.AtomicTomlMutationAdmission);
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
            },
            atomicTomlMutationAdmission: campaign.AtomicTomlMutationAdmission);
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
            configurationPath,
            atomicTomlMutationAdmission: campaign.AtomicTomlMutationAdmission);
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
            configurationPath,
            atomicTomlMutationAdmission: campaign.AtomicTomlMutationAdmission);
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

    private static async Task<ConfigurationRepositoryCommitResult> ExecuteDirectConfigurationMutationAsync(
        RestorableGameInstallCampaign campaign,
        Action? beforeAdmission,
        Func<Task<ConfigurationRepositoryCommitResult>> mutation)
    {
        beforeAdmission?.Invoke();
        campaign.EnsureGameClosedForMutation();
        return await mutation().ConfigureAwait(false);
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
                    configurationPath,
                    atomicTomlMutationAdmission: campaign.AtomicTomlMutationAdmission);
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
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or System.Text.Json.JsonException
                or System.Security.Cryptography.CryptographicException)
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
        Func<ProviderConfigurationRestorePhase, CancellationToken, ValueTask>? checkpoint = null,
        LauncherConfigurationDiagnosisEvidence? diagnosisEvidence = null,
        IAtomicTomlMutationAdmission? atomicTomlMutationAdmission = null)
    {
        var evidence = diagnosisEvidence ?? LauncherConfigurationDiagnosisEvidence.Supported(
            selection.ProviderId,
            selection.ReleaseChannelId,
            LauncherConfigurationSchemaLoader.LoadFile(Path.Combine(
                RepositoryRoot(),
                "docs",
                "windows-launcher",
                "config-schema.guffawaffle.v1.json")));
        return new(
            backupStore,
            LoadProviderCatalog(),
            selectionStore,
            selection,
            evidence,
            stateDirectory,
            () => configurationPath,
            gameProcessInspector: null,
            timeProvider: null,
            checkpoint,
            atomicTomlMutationAdmission);
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
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or AssertFailedException)
        {
            return exception;
        }
    }

    private static Exception CombineFailures(Exception primary, Exception? secondary) =>
        secondary is null ? primary : new AggregateException(primary, secondary);

    private static bool IsCampaignFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException
            or System.Text.Json.JsonException
            or CryptographicException
            or AssertFailedException;

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
                backupStore,
                backupCompleted: null,
                configurationEvidenceResolver: null,
                atomicTomlMutationAdmission: campaign.AtomicTomlMutationAdmission);
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
            campaign.RecordOwnedGameFileRevision(
                "community_patch_settings.toml",
                netnivConfiguration);
            CollectionAssert.AreEqual(netnivConfiguration, File.ReadAllBytes(configurationPath));

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
            campaign.RecordOwnedGameFileRevision(
                "community_patch_settings.toml",
                guffawaffleConfiguration);
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

    private static bool IsCleanProviderJourneyTarget(string gameDirectory) =>
        !File.Exists(Path.Combine(gameDirectory, "version.dll"))
        && !File.Exists(Path.Combine(gameDirectory, RuntimeManifestFileName))
        && !File.Exists(Path.Combine(gameDirectory, "community_patch_settings.toml"));

    private static async Task ManualAdoptionRoundTripAsync(
        ProviderEndpoint endpoint,
        string gameDirectory,
        RestorableGameInstallCampaign campaign)
    {
        var manualDll = await File.ReadAllBytesAsync(
            Path.Combine(gameDirectory, "version.dll")).ConfigureAwait(false);
        var runtimeManifestPath = Path.Combine(gameDirectory, RuntimeManifestFileName);
        var manualRuntimeManifest = File.Exists(runtimeManifestPath)
            ? await File.ReadAllBytesAsync(runtimeManifestPath).ConfigureAwait(false)
            : null;
        var manualDllRevision = campaign.CaptureProtectedRevision("version.dll");
        var manualRuntimeRevision = manualRuntimeManifest is null
            ? null
            : campaign.CaptureProtectedRevision(RuntimeManifestFileName);

        var manualHealth = endpoint.Coordinator.CaptureHealth(
            gameDirectory,
            isGameRunning: false);
        Assert.AreEqual(
            ModInstallationEvidenceState.ManualInstallation,
            manualHealth.Installation.State);
        Assert.AreEqual(
            ModManagementActionKind.UpdateManualInstallation,
            manualHealth.ModManagement.ActionKind);

        var preparation = await endpoint.Coordinator.PrepareLatestAsync(
            gameDirectory,
            isGameRunning: false).ConfigureAwait(false);
        Assert.AreEqual(ModOperationPreparationState.Ready, preparation.State);
        Assert.AreEqual(
            ExistingArtifactPolicy.AdoptAndPreserve,
            preparation.ExistingArtifactPolicy);
        Assert.AreEqual(
            ModManagementActionKind.UpdateManualInstallation,
            preparation.ActionKind);

        var downloadsBeforeRefusal = endpoint.Downloader.CallCount;
        var rejection = await endpoint.Coordinator.ExecuteAsync(
            preparation with { ExistingArtifactPolicy = ExistingArtifactPolicy.Reject })
            .ConfigureAwait(false);
        Assert.AreEqual(
            ModDeploymentResultState.ExistingArtifactRequiresAdoption,
            rejection.State,
            rejection.Message);
        Assert.AreEqual(
            downloadsBeforeRefusal,
            endpoint.Downloader.CallCount,
            "Refusing an unattributed DLL must happen before artifact download.");
        campaign.AssertProtectedRevision(
            "version.dll",
            manualDllRevision,
            "Refusing adoption changed the manual DLL.");
        if (manualRuntimeRevision is not null)
        {
            campaign.AssertProtectedRevision(
                RuntimeManifestFileName,
                manualRuntimeRevision,
                "Refusing adoption changed the manual runtime manifest.");
        }
        Assert.IsNull(endpoint.Deployment.ReadInstalledState(gameDirectory));

        var installation = await endpoint.Coordinator.ExecuteAsync(preparation).ConfigureAwait(false);
        Assert.IsTrue(installation.IsSuccess, installation.Message);
        Assert.AreEqual(endpoint.ProviderId, installation.InstalledState?.ProviderId);
        Assert.IsNotNull(installation.InstalledState?.PreviousArtifactBackupPath);
        Assert.IsNull(
            installation.InstalledState?.RuntimeManifest,
            "A newer signed DLL must not inherit reviewed runtime activation from an older catalog entry.");
        Assert.IsNull(installation.InstalledState?.PreviousRuntimeManifestBackupPath);
        campaign.RecordOwnedGameFileRevision(
            "version.dll",
            installation.InstalledState!.Sha256);

        var managedHealth = endpoint.Coordinator.CaptureHealth(
            gameDirectory,
            isGameRunning: false);
        Assert.AreEqual(
            ModInstallationEvidenceState.ManagedVerified,
            managedHealth.Installation.State);
        Assert.AreEqual(
            ManagedRuntimeManifestEvidenceState.NotManaged,
            managedHealth.Installation.RuntimeManifestState);

        var removal = await endpoint.Coordinator.UninstallAsync(gameDirectory).ConfigureAwait(false);
        Assert.IsTrue(removal.IsSuccess, removal.Message);
        CollectionAssert.AreEqual(
            manualDll,
            File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")),
            "Remove did not restore the exact manual DLL bytes.");
        if (manualRuntimeManifest is not null)
        {
            CollectionAssert.AreEqual(
                manualRuntimeManifest,
                File.ReadAllBytes(runtimeManifestPath),
                "Install/remove changed the independently human-managed runtime-manifest bytes.");
        }
        else
        {
            Assert.IsFalse(
                File.Exists(runtimeManifestPath),
                "Install/remove introduced an unauthorized runtime manifest.");
        }
        Assert.IsNull(endpoint.Deployment.ReadInstalledState(gameDirectory));
    }

    private static async Task FinishLiveCampaignAsync(
        string gameDirectory,
        RestorableGameInstallCampaign campaign,
        IReadOnlyDictionary<string, ProviderEndpoint> endpoints,
        Exception? journeyFailure,
        string failureContext)
    {
        var cleanupFailure = await TryProductionCleanupAsync(endpoints).ConfigureAwait(false);
        try
        {
            campaign.AssertBaseline("Final production cleanup did not restore the campaign baseline.");
            campaign.AssertNoFinalResidue(
                endpoints.Values.Select(endpoint => endpoint.Deployment));
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
                failureContext,
                failure,
                gameDirectory,
                campaign.StateDirectory);
        }

        var isolatedStateDirectory = campaign.StateDirectory;
        campaign.Dispose();
        Assert.IsFalse(
            Directory.Exists(isolatedStateDirectory),
            "The verified isolated campaign state was not removed.");
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
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException)
        {
            return exception;
        }
    }

    private static ProviderEndpoint CreateEndpoint(
        LauncherDistributionProvider provider,
        ReviewedReleaseCertificationCatalog reviewed,
        string stateDirectory,
        HttpClient httpClient,
        RestorableGameInstallCampaign campaign)
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
        var countingDownloader = new CountingModArtifactDownloader(downloader);
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
        ModDeploymentService? deployment = null;
        deployment = new ModDeploymentService(
            stateDirectory,
            countingDownloader,
            new WindowsModArtifactVersionReader(provider.RuntimeDistributionId),
            verifier,
            gameDirectory =>
                processInspector.Inspect(gameDirectory) != GameProcessInspectionState.NotRunning,
            attribution,
            timeProvider: null,
            afterPhasePersisted: null,
            reviewedCertification: binding.ReviewedCertification,
            afterFileCheckpoint: (checkpoint, _) =>
            {
                campaign.ObserveDeploymentCheckpoint(deployment!, checkpoint);
                return ValueTask.CompletedTask;
            },
            afterArtifactCommitted: (path, revision) =>
                campaign.TryRecordCommittedGameFileRevision(Path.GetFileName(path), revision),
            afterRuntimeManifestCommitted: (path, revision) =>
                campaign.TryRecordCommittedGameFileRevision(Path.GetFileName(path), revision),
            reviewedCertifications: reviewed.ReleaseEvidence);
        var health = new LauncherHealthService(
            new ModInstallationInspector(
                deployment,
                new SystemModInstallationFileSystem(),
                reviewedCertification: binding.ReviewedCertification),
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
                healthService: health),
            countingDownloader);
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
        ModManagementCoordinator Coordinator,
        CountingModArtifactDownloader Downloader);

    private sealed class CountingModArtifactDownloader(IModArtifactDownloader inner)
        : IModArtifactDownloader
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public Task<ModArtifactDownload> DownloadAsync(
            Uri uri,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            return inner.DownloadAsync(uri, cancellationToken);
        }
    }

    private enum DirectGameMutationKind
    {
        WriteTomlTemporary,
        PromoteTomlTemporary,
        DeleteTomlTemporary,
        WriteRestoreStage,
        PromoteRestoreStage,
        PromoteRestoreDestination,
        DeleteGameFile,
        DeleteRestoreStage,
        RestoreLastWriteTime,
        RestoreAttributes,
    }

    private sealed class RestorableGameInstallCampaign : IDisposable
    {
        private readonly string gameDirectory;
        private readonly Dictionary<string, FileBaseline> baseline;
        private readonly IGameProcessInspector gameProcessInspector;
        private readonly Action<DirectGameMutationKind, string>? beforeMutation;
        private readonly Action<string>? beforeStateDirectoryDelete;
        private readonly Action<string>? beforeStageReceipt;
        private readonly Action<string>? beforeStageFlush;
        private readonly Action<string, string>? beforePromotionCommit;
        private readonly Action<string, string>? beforeExactPromotionLock;
        private readonly Action<string>? afterPromotionBeforeOwnership;
        private readonly Dictionary<string, ExactFileRevision> createdStages = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        private readonly Dictionary<string, ExactFileRevision> pendingDeploymentPromotions = new(
            StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<OwnedFileRevision>> ownedFileRevisions =
            new(StringComparer.OrdinalIgnoreCase);
        private bool disposed;
        private bool preserveState;

        public RestorableGameInstallCampaign(
            string gameDirectory,
            IGameProcessInspector? gameProcessInspector = null,
            Action<DirectGameMutationKind, string>? beforeMutation = null,
            Action<string>? beforeStateDirectoryDelete = null,
            Action<string>? beforeStageReceipt = null,
            Action<string>? beforeStageFlush = null,
            Action<string, string>? beforePromotionCommit = null,
            Action<string, string>? beforeExactPromotionLock = null,
            Action<string>? afterPromotionBeforeOwnership = null)
        {
            this.gameProcessInspector = gameProcessInspector ?? new SystemGameProcessInspector();
            this.beforeMutation = beforeMutation;
            this.beforeStateDirectoryDelete = beforeStateDirectoryDelete;
            this.beforeStageReceipt = beforeStageReceipt;
            this.beforeStageFlush = beforeStageFlush;
            this.beforePromotionCommit = beforePromotionCommit;
            this.beforeExactPromotionLock = beforeExactPromotionLock;
            this.afterPromotionBeforeOwnership = afterPromotionBeforeOwnership;
            try
            {
                this.gameDirectory = Path.GetFullPath(gameDirectory);
                baseline = Capture(this.gameDirectory);
                StateDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "stfc-bridge-local-integration",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(StateDirectory);
                AtomicTomlMutationAdmission = new CampaignAtomicTomlMutationAdmission(this);
            }
            catch (Exception exception) when (IsCampaignFailure(exception))
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
            RestoreFile("version.dll");
            RestoreFile(RuntimeManifestFileName);
            RestoreFile("community_patch_settings.toml");
            foreach (var path in createdStages.Keys.ToArray())
            {
                DeleteCreatedStage(path);
            }
        }

        public void RestoreProtectedBaseline() => EmergencyRestore();

        public void RestoreConfigurationBaseline() =>
            RestoreFile("community_patch_settings.toml");

        public void WriteGameFileAtomically(string fileName, byte[] contents)
        {
            if (fileName is not (
                "version.dll" or RuntimeManifestFileName or "community_patch_settings.toml"))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fileName),
                    "The recovery harness writes only its protected game files.");
            }
            var path = Path.Combine(gameDirectory, fileName);
            var current = File.Exists(path) ? FileBaseline.Capture(path) : null;
            if (current is not null && !CanMutateProtectedRevision(fileName, current))
            {
                throw new InvalidOperationException(
                    "The live protected file changed outside the campaign and was preserved.");
            }
            ReplaceFileAtomically(path, contents, current);
        }

        public ExactFileRevision CaptureProtectedRevision(string fileName)
        {
            ValidateProtectedFileName(fileName);
            using var exact = ExactFileMutation.Open(Path.Combine(gameDirectory, fileName));
            return exact.CaptureRevision();
        }

        public void AssertProtectedRevision(
            string fileName,
            ExactFileRevision expected,
            string message)
        {
            var actual = CaptureProtectedRevision(fileName);
            if (!expected.Matches(actual))
            {
                throw new AssertFailedException(message);
            }
        }

        public void RecordOwnedGameFileRevision(string fileName, byte[] contents) =>
            RecordOwnedGameFileRevision(
                fileName,
                Convert.ToHexString(SHA256.HashData(contents)));

        public void RecordOwnedGameFileRevision(string fileName, string sha256)
        {
            ValidateProtectedFileName(fileName);
            var path = Path.Combine(gameDirectory, fileName);
            using var exact = ExactFileMutation.Open(path);
            var live = FileBaseline.FromExact(exact);
            if (!string.Equals(live.Sha256, sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The live protected file does not match its completed mutation receipt.");
            }
            if (!IsOwnedRevision(fileName, live))
            {
                throw new InvalidOperationException(
                    "The live protected file has no exact campaign commit receipt.");
            }
        }

        public void RecordFixtureOwnedGameFileRevision(string fileName, byte[] contents)
        {
            ValidateProtectedFileName(fileName);
            var path = Path.Combine(gameDirectory, fileName);
            using var exact = ExactFileMutation.Open(path);
            var live = FileBaseline.FromExact(exact);
            if (!live.ContentsMatch(FileBaseline.FromContents(contents)))
            {
                throw new InvalidOperationException(
                    "The fixture-owned protected file does not match its declared bytes.");
            }
            RecordOwnedGameFileRevision(fileName, live);
        }

        public bool TryRecordCommittedGameFileRevision(
            string fileName,
            ExactFileRevision expectedRevision)
        {
            ValidateProtectedFileName(fileName);
            RecordOwnedGameFileRevision(fileName, expectedRevision);
            return true;
        }

        public void ObserveDeploymentCheckpoint(
            ModDeploymentService deployment,
            ModDeploymentFileCheckpoint checkpoint)
        {
            ArgumentNullException.ThrowIfNull(deployment);
            var journal = deployment.ReadJournal()
                ?? throw new InvalidOperationException(
                    "The deployment checkpoint has no durable transaction receipt.");
            switch (checkpoint)
            {
                case ModDeploymentFileCheckpoint.AdoptedDllRestoreStaged:
                    CaptureDeploymentPromotion(journal.StagePath, "version.dll");
                    break;
                case ModDeploymentFileCheckpoint.AdoptedDllRestored:
                    CommitAdoptedRestoration("version.dll");
                    break;
                case ModDeploymentFileCheckpoint.AdoptedRuntimeManifestRestoreStaged:
                    CaptureDeploymentPromotion(
                        Path.Combine(
                            journal.GameDirectory,
                            $".{RuntimeManifestFileName}.{journal.TransactionId}.stage"),
                        RuntimeManifestFileName);
                    break;
                case ModDeploymentFileCheckpoint.AdoptedRuntimeManifestRestored:
                    CommitAdoptedRestoration(RuntimeManifestFileName);
                    break;
            }
        }

        public void AssertNoFinalResidue(IEnumerable<ModDeploymentService> deployments)
        {
            if (pendingDeploymentPromotions.Count != 0 || createdStages.Count != 0)
            {
                throw new AssertFailedException(
                    "The campaign retained an unfinished exact-file promotion receipt.");
            }
            foreach (var deployment in deployments.Distinct())
            {
                Assert.AreEqual(
                    0,
                    deployment.ReadInstalledStates().Count,
                    "Final cleanup retained a managed-installation receipt.");
                var journal = deployment.ReadJournal();
                Assert.IsTrue(
                    journal is null
                        || journal.Phase is ModDeploymentPhase.Committed
                            or ModDeploymentPhase.RolledBack,
                    "Final cleanup retained a nonterminal deployment journal.");
            }

            var residue = Directory.EnumerateFiles(
                    StateDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(StateDirectory, path))
                .FirstOrDefault(IsTransactionResidue);
            if (residue is not null)
            {
                throw new AssertFailedException(
                    "The isolated campaign state retained transaction staging or rollback bytes.");
            }
            if (gameProcessInspector.Inspect(gameDirectory)
                != GameProcessInspectionState.NotRunning)
            {
                throw new AssertFailedException(
                    "The exact opted-in installation was not closed at final audit.");
            }
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
                    beforeStateDirectoryDelete?.Invoke(StateDirectory);
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

        public void CaptureDeploymentPromotion(string stagePath, string targetFileName)
        {
            ValidateProtectedFileName(targetFileName);
            using var stage = ExactFileMutation.Open(stagePath);
            pendingDeploymentPromotions[targetFileName] = stage.CaptureRevision();
        }

        public void CommitAdoptedRestoration(string targetFileName)
        {
            if (!pendingDeploymentPromotions.TryGetValue(
                    targetFileName,
                    out var stagedRevision))
            {
                throw new InvalidOperationException(
                    "The adopted protected file has no exact restoration-stage receipt.");
            }
            using var exact = ExactFileMutation.Open(Path.Combine(gameDirectory, targetFileName));
            var restored = FileBaseline.FromExact(exact);
            if (!stagedRevision.Matches(exact.CaptureRevision()))
            {
                throw new InvalidOperationException(
                    "The adopted protected file did not retain its exact staged identity.");
            }
            pendingDeploymentPromotions.Remove(targetFileName);
            if (!baseline.TryGetValue(targetFileName, out var original)
                || !original.Matches(restored))
            {
                throw new InvalidOperationException(
                    "Production restored an adopted human-managed revision that differs from the campaign baseline. "
                    + "The restored file was preserved without granting cleanup ownership.");
            }
        }

        private static bool IsTransactionResidue(string relativePath)
        {
            var segments = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment =>
                    string.Equals(segment, "rollback", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        segment,
                        "copy-stage-ownership",
                        StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            var fileName = segments.LastOrDefault() ?? string.Empty;
            return fileName.EndsWith(".stage", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".rollback", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".restore", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".download", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".previous", StringComparison.OrdinalIgnoreCase);
        }

        private void RestoreFile(string fileName)
        {
            var path = Path.Combine(gameDirectory, fileName);
            if (!baseline.TryGetValue(fileName, out var original))
            {
                if (File.Exists(path))
                {
                    var appeared = FileBaseline.Capture(path);
                    if (!IsOwnedRevision(fileName, appeared))
                    {
                        throw new InvalidOperationException(
                            "An unowned protected file appeared during cleanup and was preserved.");
                    }
                    DeleteOwnedGameFile(fileName, path, appeared);
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
                current = FileBaseline.Capture(path);
            }
            else if (!IsOwnedRevision(fileName, current))
            {
                throw new InvalidOperationException(
                    "An external protected-file revision was preserved without metadata mutation.");
            }
            RestoreMetadata(fileName, path, original, current);
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
                using (var stream = CreateStageWriteStream(stagePath))
                {
                    CandidateFileIdentity identity;
                    try
                    {
                        identity = CandidateFileNative.ReadIdentity(stream.SafeFileHandle);
                        createdStages.Add(
                            stagePath,
                            CaptureOpenStageRevision(stagePath, stream, identity, []));
                        beforeStageReceipt?.Invoke(stagePath);
                    }
                    catch
                    {
                        CandidateFileNative.TryMarkDeleteOnClose(stream.SafeFileHandle);
                        throw;
                    }
                    AdmitMutation(DirectGameMutationKind.WriteRestoreStage, stagePath);
                    try
                    {
                        stream.Write(contents);
                        beforeStageFlush?.Invoke(stagePath);
                        stream.Flush(flushToDisk: true);
                        createdStages[stagePath] = CaptureOpenStageRevision(
                            stagePath,
                            stream,
                            identity,
                            contents);
                    }
                    catch
                    {
                        try
                        {
                            var writtenLength = checked((int)Math.Min(stream.Length, contents.LongLength));
                            createdStages[stagePath] = CaptureOpenStageRevision(
                                stagePath,
                                stream,
                                identity,
                                contents.AsSpan(0, writtenLength).ToArray());
                            CandidateFileNative.TryMarkDeleteOnClose(stream.SafeFileHandle);
                        }
                        catch
                        {
                            CandidateFileNative.TryMarkDeleteOnClose(stream.SafeFileHandle);
                        }
                        throw;
                    }
                }
                AdmitMutation(DirectGameMutationKind.PromoteRestoreStage, stagePath);
                AdmitMutation(DirectGameMutationKind.PromoteRestoreDestination, path);
                VerifyRestoreStage(stagePath);
                VerifyPromotionDestination(path, expectedCurrent);
                beforePromotionCommit?.Invoke(stagePath, path);
                VerifyRestoreStage(stagePath);
                VerifyPromotionDestination(path, expectedCurrent);
                beforeExactPromotionLock?.Invoke(stagePath, path);
                EnsureGameClosedForMutation();
                var promotedStage = createdStages[stagePath];
                using (var exactStage = ExactFileMutation.OpenForMetadata(stagePath))
                {
                    if (!promotedStage.Matches(exactStage.CaptureRevision()))
                    {
                        throw new InvalidOperationException(
                            "The restore stage changed before its exact promotion and was preserved.");
                    }
                    RecordOwnedGameFileRevision(fileName, promotedStage);
                    if (expectedCurrent is null)
                    {
                        if (File.Exists(path))
                        {
                            throw new InvalidOperationException(
                                "A protected file appeared before exact promotion and was preserved.");
                        }
                        exactStage.MoveExactNoReplace(path);
                    }
                    else
                    {
                        using var exactDestination = ExactFileMutation.OpenForMetadata(path);
                        if (expectedCurrent.Identity != exactDestination.Identity
                            || !expectedCurrent.Matches(FileBaseline.FromExact(exactDestination)))
                        {
                            throw new InvalidOperationException(
                                "The protected file changed before exact promotion and was preserved.");
                        }
                        var displacedPath = stagePath + ".destination";
                        exactDestination.MoveExactNoReplace(displacedPath);
                        try
                        {
                            exactStage.MoveExactNoReplace(path);
                        }
                        catch
                        {
                            if (!File.Exists(path))
                            {
                                exactDestination.MoveExactNoReplace(path);
                            }
                            throw;
                        }
                        exactDestination.DeleteExactIgnoringReadOnly();
                    }
                }
                afterPromotionBeforeOwnership?.Invoke(path);
                var promoted = FileBaseline.Capture(path);
                if (promoted.Identity != promotedStage.Identity
                    || !promotedStage.Matches(promoted.ToExactRevision()))
                {
                    throw new InvalidOperationException(
                        "The promoted protected file does not match its owned stage identity.");
                }
                RecordOwnedGameFileRevision(fileName, promoted);
                createdStages.Remove(stagePath);
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

        private void VerifyRestoreStage(string stagePath)
        {
            using var stage = ExactFileMutation.Open(stagePath);
            if (!createdStages.TryGetValue(stagePath, out var stageRevision)
                || !stageRevision.Matches(stage.CaptureRevision()))
            {
                throw new InvalidOperationException(
                    "The restore stage changed outside the campaign and was preserved.");
            }
        }

        private static void VerifyPromotionDestination(
            string path,
            FileBaseline? expectedCurrent)
        {
            if (expectedCurrent is null)
            {
                if (File.Exists(path))
                {
                    throw new InvalidOperationException(
                        "A protected file appeared before atomic promotion and was preserved.");
                }
                return;
            }
            using var destination = ExactFileMutation.Open(path);
            if (expectedCurrent.Identity != destination.Identity
                || !expectedCurrent.Matches(FileBaseline.FromExact(destination)))
            {
                throw new InvalidOperationException(
                    "The protected file changed before atomic promotion and was preserved.");
            }
        }

        private void RestoreMetadata(
            string fileName,
            string path,
            FileBaseline original,
            FileBaseline expectedCurrent)
        {
            using var exact = ExactFileMutation.OpenForMetadata(path);
            var locked = FileBaseline.FromExact(exact);
            if (expectedCurrent.Identity != exact.Identity
                || !expectedCurrent.Matches(locked)
                || !IsOwnedRevision(fileName, locked))
            {
                throw new InvalidOperationException(
                    "The protected file changed before metadata restoration and was preserved.");
            }
            var attributes = locked.Attributes;
            var lastWriteTimeUtc = locked.LastWriteTimeUtcTicks;
            if (lastWriteTimeUtc != original.LastWriteTimeUtcTicks
                && attributes.HasFlag(FileAttributes.ReadOnly))
            {
                AdmitMutation(DirectGameMutationKind.RestoreAttributes, path);
                exact.SetMetadata(attributes & ~FileAttributes.ReadOnly, lastWriteTimeUtc);
                attributes &= ~FileAttributes.ReadOnly;
            }
            if (lastWriteTimeUtc != original.LastWriteTimeUtcTicks)
            {
                AdmitMutation(DirectGameMutationKind.RestoreLastWriteTime, path);
                exact.SetMetadata(attributes, original.LastWriteTimeUtcTicks);
                lastWriteTimeUtc = original.LastWriteTimeUtcTicks;
            }
            if (attributes != original.Attributes)
            {
                AdmitMutation(DirectGameMutationKind.RestoreAttributes, path);
                exact.SetMetadata(original.Attributes, lastWriteTimeUtc);
            }
        }

        private void AdmitMutation(DirectGameMutationKind kind, string path)
        {
            beforeMutation?.Invoke(kind, path);
            EnsureGameClosedForMutation();
        }

        private static ExactFileRevision CaptureOpenStageRevision(
            string path,
            FileStream stream,
            CandidateFileIdentity identity,
            byte[] contents) =>
            new(
                identity,
                stream.Length,
                Convert.ToHexString(SHA256.HashData(contents)),
                File.GetAttributes(path),
                File.GetLastWriteTimeUtc(path).Ticks);

        private static FileStream CreateStageWriteStream(string path)
        {
            Microsoft.Win32.SafeHandles.SafeFileHandle handle;
            try
            {
                handle = CandidateFileNative.CreateWriteDelete(path);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new IOException("The restore staging file could not be created.", exception);
            }
            try
            {
                return new FileStream(handle, FileAccess.Write, bufferSize: 4096, isAsync: true);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private void DeleteOwnedGameFile(
            string fileName,
            string path,
            FileBaseline expected)
        {
            using var exact = ExactFileMutation.Open(path);
            var locked = FileBaseline.FromExact(exact);
            if (expected.Identity != exact.Identity
                || !expected.Matches(locked)
                || !IsOwnedRevision(fileName, locked))
            {
                throw new InvalidOperationException(
                    "The protected file changed before owned cleanup and was preserved.");
            }
            AdmitMutation(DirectGameMutationKind.DeleteGameFile, path);
            exact.DeleteExact();
        }

        private void DeleteCreatedStage(string path)
        {
            if (!createdStages.TryGetValue(path, out var expectedIdentity))
            {
                return;
            }
            if (!File.Exists(path))
            {
                createdStages.Remove(path);
                return;
            }
            using var exact = ExactFileMutation.Open(path);
            if (!expectedIdentity.Matches(exact.CaptureRevision()))
            {
                return;
            }
            AdmitMutation(DirectGameMutationKind.DeleteRestoreStage, path);
            exact.DeleteExact();
            createdStages.Remove(path);
        }

        private void AdmitAtomicTomlMutation(
            AtomicTomlMutationBoundary boundary,
            string temporaryPath,
            string destinationPath)
        {
            var canonicalDestination = Path.GetFullPath(destinationPath);
            if (!string.Equals(
                    Path.GetDirectoryName(canonicalDestination),
                    gameDirectory,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetFileName(canonicalDestination),
                    "community_patch_settings.toml",
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetDirectoryName(Path.GetFullPath(temporaryPath)),
                    gameDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Atomic TOML admission escaped the exact opted-in installation.");
            }
            AdmitMutation(
                boundary switch
                {
                    AtomicTomlMutationBoundary.TemporaryWrite =>
                        DirectGameMutationKind.WriteTomlTemporary,
                    AtomicTomlMutationBoundary.Promotion =>
                        DirectGameMutationKind.PromoteTomlTemporary,
                    AtomicTomlMutationBoundary.TemporaryDelete =>
                        DirectGameMutationKind.DeleteTomlTemporary,
                    _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
                },
                boundary == AtomicTomlMutationBoundary.Promotion
                    ? canonicalDestination
                    : temporaryPath);
        }

        private void ValidateObservedAtomicDestination(
            string destinationPath,
            ExactFileRevision revision)
        {
            var canonicalDestination = Path.GetFullPath(destinationPath);
            if (!string.Equals(
                    canonicalDestination,
                    Path.Combine(gameDirectory, "community_patch_settings.toml"),
                    StringComparison.OrdinalIgnoreCase)
                || !CanMutateProtectedRevision(
                    "community_patch_settings.toml",
                    FileBaseline.FromExactRevision(revision)))
            {
                throw new InvalidOperationException(
                    "The observed configuration revision is not part of the protected campaign baseline.");
            }
        }

        private void DeleteAtomicCreatedDestination(
            string destinationPath,
            string expectedSha256)
        {
            var canonicalDestination = Path.GetFullPath(destinationPath);
            if (!string.Equals(
                    canonicalDestination,
                    Path.Combine(gameDirectory, "community_patch_settings.toml"),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Atomic TOML deletion escaped the protected configuration file.");
            }
            using var exact = ExactFileMutation.Open(canonicalDestination);
            var current = FileBaseline.FromExact(exact);
            if (!string.Equals(current.Sha256, expectedSha256, StringComparison.Ordinal)
                || !IsOwnedRevision("community_patch_settings.toml", current))
            {
                throw new InvalidOperationException(
                    "The provider-switch configuration changed outside the campaign and was preserved.");
            }
            AdmitMutation(DirectGameMutationKind.DeleteGameFile, canonicalDestination);
            exact.DeleteExact();
        }

        private void RecordOwnedGameFileRevision(
            string fileName,
            FileBaseline revision) =>
            RecordOwnedGameFileRevision(fileName, revision.ToExactRevision());

        private void RecordOwnedGameFileRevision(
            string fileName,
            ExactFileRevision exactRevision)
        {
            ValidateProtectedFileName(fileName);
            if (!ownedFileRevisions.TryGetValue(fileName, out var revisions))
            {
                revisions = [];
                ownedFileRevisions.Add(fileName, revisions);
            }
            revisions.Add(new(
                exactRevision.Identity,
                exactRevision.Length,
                exactRevision.Sha256,
                exactRevision.Attributes,
                exactRevision.LastWriteTimeUtcTicks));
        }

        private bool CanMutateProtectedRevision(string fileName, FileBaseline current) =>
            (baseline.TryGetValue(fileName, out var original)
                && original.Identity == current.Identity
                && original.Matches(current))
            || IsOwnedRevision(fileName, current);

        private bool IsOwnedRevision(string fileName, FileBaseline current) =>
            current.Identity is not null
            && ownedFileRevisions.TryGetValue(fileName, out var revisions)
            && revisions.Contains(new(
                current.Identity,
                current.Length,
                current.Sha256,
                current.Attributes,
                current.LastWriteTimeUtcTicks));

        private static void ValidateProtectedFileName(string fileName)
        {
            if (fileName is not (
                "version.dll" or RuntimeManifestFileName or "community_patch_settings.toml"))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fileName),
                    "The recovery harness owns only its protected game files.");
            }
        }

        private sealed record OwnedFileRevision(
            CandidateFileIdentity Identity,
            long Length,
            string Sha256,
            FileAttributes Attributes,
            long LastWriteTimeUtcTicks);

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
                campaign.AdmitAtomicTomlMutation(boundary, temporaryPath, destinationPath);
                return ValueTask.CompletedTask;
            }

            public void TemporaryCreated(
                string temporaryPath,
                ExactFileRevision revision) =>
                campaign.createdStages[Path.GetFullPath(temporaryPath)] = revision;

            public void TemporaryCompleted(
                string temporaryPath,
                ExactFileRevision revision) =>
                campaign.createdStages[Path.GetFullPath(temporaryPath)] = revision;

            public void BeforeTemporaryFlush(string temporaryPath)
            {
            }

            public void TemporaryRemoved(string temporaryPath) =>
                campaign.createdStages.Remove(Path.GetFullPath(temporaryPath));

            public void BeforeCommitValidation(
                string temporaryPath,
                string destinationPath)
            {
            }

            public void DestinationObserved(
                string destinationPath,
                ExactFileRevision revision) =>
                campaign.ValidateObservedAtomicDestination(destinationPath, revision);

            public void DestinationPrepared(
                string destinationPath,
                ExactFileRevision revision)
            {
                ValidateDestination(destinationPath);
                campaign.RecordOwnedGameFileRevision(
                    "community_patch_settings.toml",
                    revision);
            }

            public void AfterPromotionBeforeOwnership(string destinationPath)
            {
            }

            public void DestinationCommitted(
                string destinationPath,
                ExactFileRevision revision)
            {
                ValidateDestination(destinationPath);
                campaign.RecordOwnedGameFileRevision(
                    "community_patch_settings.toml",
                    revision);
            }

            public void DeleteCreatedDestination(
                string destinationPath,
                string expectedSha256) =>
                campaign.DeleteAtomicCreatedDestination(destinationPath, expectedSha256);

            public void VerifyCommitAllowed(
                AtomicTomlMutationBoundary boundary,
                string temporaryPath,
                string destinationPath) =>
                campaign.EnsureGameClosedForMutation();

            private static void ValidateDestination(string destinationPath)
            {
                if (!string.Equals(
                        Path.GetFileName(destinationPath),
                        "community_patch_settings.toml",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Atomic TOML ownership escaped the protected configuration file.");
                }
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
                byte[]? contents = null;
                if (Path.GetFileName(path) is
                    "version.dll" or RuntimeManifestFileName or "community_patch_settings.toml")
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
                    contents);
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

            public static FileBaseline FromContents(byte[] contents) =>
                new(
                    FileAttributes.Normal,
                    contents.LongLength,
                    0,
                    Convert.ToHexString(SHA256.HashData(contents)),
                    contents);

            public static FileBaseline FromExactRevision(ExactFileRevision revision) =>
                new(
                    revision.Attributes,
                    revision.Length,
                    revision.LastWriteTimeUtcTicks,
                    revision.Sha256,
                    null)
                {
                    Identity = revision.Identity,
                };

            public bool Matches(FileBaseline other) =>
                Attributes == other.Attributes
                && Length == other.Length
                && LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks
                && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);

            public bool ContentsMatch(FileBaseline other) =>
                Length == other.Length
                && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);

            public ExactFileRevision ToExactRevision() =>
                new(
                    Identity
                        ?? throw new InvalidOperationException(
                            "The protected file revision has no exact identity."),
                    Length,
                    Sha256,
                    Attributes,
                    LastWriteTimeUtcTicks);
        }
    }
}
