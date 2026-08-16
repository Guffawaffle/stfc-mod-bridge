using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherOperationContentionMatrixTests
{
    private const string OwnerStateEnvironment = "STFC_BRIDGE_CONTENTION_OWNER_STATE";
    private const string OwnerReadyEnvironment = "STFC_BRIDGE_CONTENTION_OWNER_READY";
    private const string OwnerReleaseEnvironment = "STFC_BRIDGE_CONTENTION_OWNER_RELEASE";
    private static readonly byte[] InstalledArtifact = [0x47, 0x55, 0x46, 0x46, 0x41];
    private static readonly byte[] UpdatedArtifact = [0x4e, 0x45, 0x54, 0x4e, 0x49, 0x56];

    [DataTestMethod]
    [DataRow(ContentionOperation.ProviderSwitch)]
    [DataRow(ContentionOperation.Update)]
    [DataRow(ContentionOperation.Remove)]
    [DataRow(ContentionOperation.Settings)]
    [DataRow(ContentionOperation.DataSync)]
    [DataRow(ContentionOperation.Migration)]
    [DataRow(ContentionOperation.Restore)]
    public async Task RootLeaseRejectsEveryLosingMutationBeforeDurableSideEffects(
        ContentionOperation operation)
    {
        using var directory = new TemporaryDirectory();
        var scenario = await CreateScenarioAsync(directory, operation);
        var readyPath = Path.Combine(directory.Path, $"owner-{operation}-ready");
        var releasePath = Path.Combine(directory.Path, $"owner-{operation}-release");
        using var owner = StartLockOwner(scenario.StateDirectory, readyPath, releasePath);
        await WaitForLockOwnerAsync(owner, readyPath, operation);
        try
        {
            var before = CaptureDurableFiles(directory.Path, readyPath, releasePath);

            await scenario.AssertBusyAsync();

            scenario.AssertNoTransientMutation();
            AssertDurableFilesEqual(
                before,
                CaptureDurableFiles(directory.Path, readyPath, releasePath),
                operation);
        }
        finally
        {
            await ReleaseLockOwnerAsync(owner, releasePath, operation);
        }
    }

    [TestMethod]
    public async Task LauncherOperationLockOwnerProbe()
    {
        var stateDirectory = Environment.GetEnvironmentVariable(OwnerStateEnvironment);
        var readyPath = Environment.GetEnvironmentVariable(OwnerReadyEnvironment);
        var releasePath = Environment.GetEnvironmentVariable(OwnerReleaseEnvironment);
        if (string.IsNullOrWhiteSpace(stateDirectory)
            || string.IsNullOrWhiteSpace(readyPath)
            || string.IsNullOrWhiteSpace(releasePath))
        {
            Assert.Inconclusive("Contention lock-owner probe runs only in its isolated child process.");
        }

        await using var owner = await new LauncherOperationLock(stateDirectory).TryAcquireAsync();
        Assert.IsNotNull(owner, "The contention child could not acquire the root operation lease.");
        await File.WriteAllTextAsync(readyPath, "owned");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(releasePath))
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    private static Task<ContentionScenario> CreateScenarioAsync(
        TemporaryDirectory directory,
        ContentionOperation operation) =>
        operation switch
        {
            ContentionOperation.ProviderSwitch => CreateProviderSwitchScenarioAsync(directory),
            ContentionOperation.Update => CreateUpdateScenarioAsync(directory),
            ContentionOperation.Remove => CreateRemoveScenarioAsync(directory),
            ContentionOperation.Settings => Task.FromResult(CreateSettingsScenario(directory)),
            ContentionOperation.DataSync => Task.FromResult(CreateDataSyncScenario(directory)),
            ContentionOperation.Migration => Task.FromResult(CreateMigrationScenario(directory)),
            ContentionOperation.Restore => CreateRestoreScenarioAsync(directory),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

    private static async Task<ContentionScenario> CreateProviderSwitchScenarioAsync(
        TemporaryDirectory directory)
    {
        var mutationSentinel = new MutationSentinel(ContentionOperation.ProviderSwitch);
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var sourceContents = Encoding.UTF8.GetBytes(
            "# guffawaffle\r\n[graphics]\r\nfree_resize = true\r\n");
        var targetContents = Encoding.UTF8.GetBytes(
            "# netniv\n[graphics]\nfree_resize = false\n");
        await File.WriteAllBytesAsync(configurationPath, sourceContents);
        var selectionStore = new SentinelSelectionStore(
            new JsonLauncherProviderSelectionStore(stateDirectory),
            mutationSentinel);
        selectionStore.Save(new("guffawaffle", "stable"));
        var backupStore = CreateBackupStore(stateDirectory, mutationSentinel);
        await backupStore.CreateAsync(new(
            gameDirectory,
            "netniv",
            configurationPath,
            targetContents,
            "contention-seed"));
        var sourceArtifact = Artifact(InstalledArtifact, "2.1.0.8");
        var targetArtifact = Artifact(UpdatedArtifact, "1.1.5.1");
        var sourceDownloader = new CountingDownloader(InstalledArtifact);
        var targetDownloader = new CountingDownloader(UpdatedArtifact);
        var sourceDeployment = Deployment(
            stateDirectory,
            sourceDownloader,
            sourceArtifact.ExpectedVersion,
            new("guffawaffle", "stable", "guffawaffle.windows"),
            mutationSentinel);
        var targetDeployment = Deployment(
            stateDirectory,
            targetDownloader,
            targetArtifact.ExpectedVersion,
            new("netniv", "stable", "netniv.stfc-community-mod"),
            mutationSentinel);
        var installed = await sourceDeployment.DeployAsync(
            gameDirectory,
            sourceArtifact,
            ExistingArtifactPolicy.Reject);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, installed.State, installed.Message);
        var sourceDownloadCount = sourceDownloader.CallCount;
        var sourceManagement = Management(
            sourceDeployment,
            sourceArtifact,
            "guffawaffle",
            "guffawaffle.windows");
        var targetManagement = Management(
            targetDeployment,
            targetArtifact,
            "netniv",
            "netniv.stfc-community-mod");
        var configurationSwitch = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            selectionStore,
            backupStore,
            backupCompleted: null);
        var coordinator = new LauncherProviderAtomicSwitchCoordinator(
            configurationSwitch,
            [
                new("guffawaffle", sourceManagement),
                new("netniv", targetManagement),
            ],
            stateDirectory,
            timeProvider: null,
            checkpoint: (phase, _) => mutationSentinel.ObserveAsync(
                $"provider-switch journal phase {phase}"));
        var preview = await coordinator.PreviewAsync(
            "netniv",
            "stable",
            gameDirectory,
            isGameRunning: false,
            configurationPath);
        Assert.IsNotNull(preview.Artifact, "This matrix row must exercise the artifact-switch boundary.");
        mutationSentinel.Arm();

        return new(
            stateDirectory,
            async () =>
            {
                var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => coordinator.ExecuteAsync(preview, preview.ConfirmationText));
                StringAssert.Contains(
                    exception.Message,
                    "Another Mod Bridge mutation is already active");
                Assert.AreEqual(
                    sourceDownloadCount,
                    sourceDownloader.CallCount,
                    "Busy switch touched the source downloader.");
                Assert.AreEqual(0, targetDownloader.CallCount, "Busy switch touched the target downloader.");
            },
            [mutationSentinel]);
    }

    private static async Task<ContentionScenario> CreateUpdateScenarioAsync(
        TemporaryDirectory directory)
    {
        var mutationSentinel = new MutationSentinel(ContentionOperation.Update);
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory);
        var attribution = new ModInstallationAttribution(
            "guffawaffle",
            "stable",
            "guffawaffle.windows");
        var installedDownloader = new CountingDownloader(InstalledArtifact);
        var installer = Deployment(
            stateDirectory,
            installedDownloader,
            "2.1.0.8",
            attribution);
        var installed = await installer.DeployAsync(
            gameDirectory,
            Artifact(InstalledArtifact, "2.1.0.8"),
            ExistingArtifactPolicy.Reject);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, installed.State, installed.Message);
        var updateDownloader = new CountingDownloader(UpdatedArtifact);
        var updater = Deployment(
            stateDirectory,
            updateDownloader,
            "2.1.0.9",
            attribution,
            mutationSentinel);
        var updateManagement = Management(
            updater,
            Artifact(UpdatedArtifact, "2.1.0.9"),
            attribution.ProviderId,
            attribution.RuntimeDistributionId);
        var preparation = await updateManagement.PrepareLatestAsync(
            gameDirectory,
            isGameRunning: false);
        Assert.AreEqual(ModOperationPreparationState.Ready, preparation.State, preparation.Message);
        mutationSentinel.Arm();

        return new(
            stateDirectory,
            async () =>
            {
                var result = await updateManagement.ExecuteAsync(preparation);
                Assert.AreEqual(ModDeploymentResultState.Busy, result.State, result.Message);
                Assert.AreEqual(0, updateDownloader.CallCount, "Busy update attempted a download.");
            },
            [mutationSentinel]);
    }

    private static async Task<ContentionScenario> CreateRemoveScenarioAsync(
        TemporaryDirectory directory)
    {
        var mutationSentinel = new MutationSentinel(ContentionOperation.Remove);
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory);
        var downloader = new CountingDownloader(InstalledArtifact);
        var installer = Deployment(
            stateDirectory,
            downloader,
            "2.1.0.8",
            new("guffawaffle", "stable", "guffawaffle.windows"));
        var installed = await installer.DeployAsync(
            gameDirectory,
            Artifact(InstalledArtifact, "2.1.0.8"),
            ExistingArtifactPolicy.Reject);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, installed.State, installed.Message);
        var service = Deployment(
            stateDirectory,
            downloader,
            "2.1.0.8",
            new("guffawaffle", "stable", "guffawaffle.windows"),
            mutationSentinel);
        var management = Management(
            service,
            Artifact(InstalledArtifact, "2.1.0.8"),
            "guffawaffle",
            "guffawaffle.windows");
        var downloadCount = downloader.CallCount;
        mutationSentinel.Arm();

        return new(
            stateDirectory,
            async () =>
            {
                var result = await management.UninstallAsync(gameDirectory);
                Assert.AreEqual(ModDeploymentResultState.Busy, result.State, result.Message);
                Assert.AreEqual(downloadCount, downloader.CallCount, "Busy remove touched the downloader.");
            },
            [mutationSentinel]);
    }

    private static ContentionScenario CreateSettingsScenario(TemporaryDirectory directory)
    {
        var mutationSentinel = new MutationSentinel(ContentionOperation.Settings);
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        File.WriteAllText(
            configurationPath,
            "# settings contention\n[graphics]\nfree_resize = true\n",
            new UTF8Encoding(false));
        var repository = Repository(stateDirectory, "settings-save", mutationSentinel);
        var catalog = LoadConfigurationCatalog();
        var load = ConfigurationWorkspace.Load(
            configurationPath,
            catalog,
            repository,
            out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);
        var setting = catalog.Settings.Single(item => item.Path == "graphics.free_resize");
        var stage = workspace!.StageSet(setting, "false");
        Assert.IsTrue(stage.IsValid, stage.Error?.Message);
        mutationSentinel.Arm();

        return new(
            stateDirectory,
            async () =>
            {
                var result = await workspace.CommitAsync();
                Assert.AreEqual(AtomicTomlWriteState.Busy, result.State, result.Error);
                Assert.IsTrue(workspace.HasPendingChanges);
            },
            [mutationSentinel]);
    }

    private static ContentionScenario CreateDataSyncScenario(TemporaryDirectory directory)
    {
        var mutationSentinel = new MutationSentinel(ContentionOperation.DataSync);
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        File.WriteAllText(
            configurationPath,
            "# data sync contention\n",
            new UTF8Encoding(false));
        var load = ConfigurationWorkspace.Load(
            configurationPath,
            LoadConfigurationCatalog(),
            Repository(stateDirectory, "data-sync-save", mutationSentinel),
            out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);
        var syncLoad = workspace!.CreateSyncTopologyEditSession(out var session);
        Assert.IsTrue(syncLoad.IsValid, syncLoad.Error?.Message);
        var added = session!.Desired.AddTarget("community", SyncTargetKind.LegacyCommunity);
        session.Stage(added.Topology.SetTargetEnabled("community", true).Topology);
        mutationSentinel.Arm();

        return new(
            stateDirectory,
            async () =>
            {
                var result = await workspace.CommitSyncAsync(session);
                Assert.AreEqual(AtomicTomlWriteState.Busy, result.State, result.Error);
                Assert.IsTrue(session.HasPendingChanges);
            },
            [mutationSentinel]);
    }

    private static ContentionScenario CreateMigrationScenario(TemporaryDirectory directory)
    {
        var mutationSentinel = new MutationSentinel(ContentionOperation.Migration);
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var original = Encoding.UTF8.GetBytes(
            "[shortcuts]\nset_hotkeys_disable = \"CTRL-ALT-MINUS\"\n");
        File.WriteAllBytes(configurationPath, original);
        var baseline = new ConfigurationDocumentSnapshot(configurationPath, original);
        var catalog = LoadConfigurationCatalog();
        var evidence = LauncherConfigurationDiagnosisEvidence.Supported(
            "guffawaffle",
            "stable",
            catalog);
        var diagnosis = new ConfigurationHealthAnalyzer().Analyze(baseline, evidence);
        var remediationId = diagnosis.Findings.Single(
            finding => finding.Code == "CONFIG_ALIAS_PRESENT").RemediationId;
        Assert.IsFalse(string.IsNullOrWhiteSpace(remediationId));
        var plan = new ConfigurationMigrationPlanner().Plan(
            baseline,
            evidence,
            diagnosis,
            [remediationId!]);
        Assert.AreEqual(ConfigurationMigrationPlanState.Ready, plan.State, plan.Message);
        var coordinator = new ConfigurationMigrationApplyCoordinator(
            Repository(stateDirectory, "configuration-migration", mutationSentinel));
        mutationSentinel.Arm();

        return new(
            stateDirectory,
            async () =>
            {
                var result = await coordinator.ApplyAsync(new(baseline, plan, evidence));
                Assert.AreEqual(AtomicTomlWriteState.Busy, result.State, result.Error);
            },
            [mutationSentinel]);
    }

    private static async Task<ContentionScenario> CreateRestoreScenarioAsync(
        TemporaryDirectory directory)
    {
        var mutationSentinel = new MutationSentinel(ContentionOperation.Restore);
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        await File.WriteAllTextAsync(
            configurationPath,
            "# current configuration\n",
            new UTF8Encoding(false));
        var selection = new LauncherProviderSelection("guffawaffle", "stable");
        var selectionStore = new JsonLauncherProviderSelectionStore(stateDirectory);
        selectionStore.Save(selection);
        var backupStore = CreateBackupStore(stateDirectory, mutationSentinel);
        var backup = await backupStore.CreateAsync(new(
            gameDirectory,
            selection.ProviderId,
            configurationPath,
            Encoding.UTF8.GetBytes("# history configuration\n"),
            "settings-save",
            ReleaseIdentity: "guffawaffle/stable"));
        var evidence = LauncherConfigurationDiagnosisEvidence.Supported(
            selection.ProviderId,
            selection.ReleaseChannelId,
            LoadConfigurationCatalog());
        var coordinator = new ProviderConfigurationRestoreCoordinator(
            backupStore,
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            selectionStore,
            selection,
            evidence,
            stateDirectory,
            () => configurationPath,
            new NotRunningProcessInspector(),
            timeProvider: null,
            checkpoint: (phase, _) => mutationSentinel.ObserveAsync(
                $"configuration-restore journal phase {phase}"));
        var preview = coordinator.Preview(backup.BackupId);
        mutationSentinel.Arm();

        return new(
            stateDirectory,
            async () =>
            {
                var result = await coordinator.ExecuteAsync(preview, preview.ConfirmationText);
                Assert.AreEqual(ProviderConfigurationRestoreResultState.Busy, result.State, result.Message);
            },
            [mutationSentinel]);
    }

    private static TomlConfigurationRepository Repository(
        string stateDirectory,
        string reason,
        MutationSentinel mutationSentinel)
    {
        var mutationBackup = new SentinelMutationBackup(
                new ProviderScopedConfigurationMutationBackup(
                    CreateBackupStore(stateDirectory),
                    "guffawaffle",
                    "guffawaffle/stable",
                    reason),
                mutationSentinel);
        return new(
            store: new AtomicTomlStore(
                mutationBackup,
                (_, _, _) => mutationSentinel.ObserveAsync(
                    "configuration staging file")),
            mutationAdmission: new LauncherOperationLock(stateDirectory));
    }

    private static ProviderScopedConfigurationBackupStore CreateBackupStore(
        string stateDirectory,
        MutationSentinel? mutationSentinel = null) =>
        mutationSentinel is null
            ? new(
                stateDirectory,
                new ReversingProtector(),
                new NoOpStorageSecurity())
            : new(
                stateDirectory,
                new ReversingProtector(),
                new NoOpStorageSecurity(),
                timeProvider: null,
                retentionCount: ProviderScopedConfigurationBackupStore.DefaultRetentionCount,
                checkpoint: (checkpoint, _) => mutationSentinel.ObserveAsync(
                    $"configuration-backup checkpoint {checkpoint}"));

    private static ModDeploymentService Deployment(
        string stateDirectory,
        CountingDownloader downloader,
        string version,
        ModInstallationAttribution attribution,
        MutationSentinel? mutationSentinel = null) =>
        new(
            stateDirectory,
            downloader,
            new FakeVersionReader(version),
            new FakeAuthenticityVerifier(),
            _ => false,
            attribution,
            timeProvider: null,
            afterPhasePersisted: mutationSentinel is null
                ? null
                : (phase, _) => mutationSentinel.ObserveAsync(
                    $"deployment journal phase {phase}"),
            reviewedCertification: null,
            afterFileCheckpoint: mutationSentinel is null
                ? null
                : (checkpoint, _) => mutationSentinel.ObserveAsync(
                    $"deployment file checkpoint {checkpoint}"));

    private static ModManagementCoordinator Management(
        ModDeploymentService deployment,
        ModReleaseArtifact artifact,
        string providerId,
        string runtimeDistributionId) =>
        new(
            deployment,
            new FakeReleaseDiscoveryClient(artifact),
            new Version(0, 1, 0),
            healthService: new LauncherHealthService(
                new ModInstallationInspector(
                    deployment,
                    new SystemModInstallationFileSystem()),
                new(
                    providerId,
                    "stable",
                    runtimeDistributionId,
                    CanMutate: true,
                    UnavailableReason: string.Empty)));

    private static ModReleaseArtifact Artifact(byte[] contents, string version) =>
        new(
            new Uri("https://example.invalid/version.dll"),
            "version.dll",
            contents.LongLength,
            Convert.ToHexString(SHA256.HashData(contents)),
            version);

    private static LauncherConfigurationCatalog LoadConfigurationCatalog() =>
        LauncherConfigurationSchemaLoader.LoadFile(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Configuration",
            "config-schema.guffawaffle.v1.json"));

    private static string CreateGameDirectory(TemporaryDirectory directory)
    {
        var gameDirectory = directory.CreateDirectory("game");
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        return gameDirectory;
    }

    private static Process StartLockOwner(
        string stateDirectory,
        string readyPath,
        string releasePath)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(LauncherOperationContentionMatrixTests).Assembly.Location);
        start.ArgumentList.Add(
            "--Tests:STFCCommunityMod.Launcher.Core.Tests."
            + "LauncherOperationContentionMatrixTests.LauncherOperationLockOwnerProbe");
        start.Environment[OwnerStateEnvironment] = stateDirectory;
        start.Environment[OwnerReadyEnvironment] = readyPath;
        start.Environment[OwnerReleaseEnvironment] = releasePath;
        return Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the contention lock-owner probe.");
    }

    private static async Task WaitForLockOwnerAsync(
        Process owner,
        string readyPath,
        ContentionOperation operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (!File.Exists(readyPath))
            {
                if (owner.HasExited)
                {
                    var output = await owner.StandardOutput.ReadToEndAsync();
                    var error = await owner.StandardError.ReadToEndAsync();
                    Assert.Fail(
                        $"The {operation} lock-owner probe exited before acquiring the root lease. "
                        + $"Output: {output} Error: {error}");
                }
                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await TerminateLockOwnerAsync(owner);
            var output = await owner.StandardOutput.ReadToEndAsync();
            var error = await owner.StandardError.ReadToEndAsync();
            Assert.Fail(
                $"Timed out waiting for the {operation} child process to own the root lease. "
                + $"Output: {output} Error: {error}");
        }
    }

    private static async Task ReleaseLockOwnerAsync(
        Process owner,
        string releasePath,
        ContentionOperation operation)
    {
        if (!owner.HasExited)
        {
            await File.WriteAllTextAsync(releasePath, "release");
        }
        try
        {
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            await TerminateLockOwnerAsync(owner);
            Assert.Fail($"The {operation} lock-owner probe did not release its root lease.");
        }
        if (owner.ExitCode != 0)
        {
            var output = await owner.StandardOutput.ReadToEndAsync();
            var error = await owner.StandardError.ReadToEndAsync();
            Assert.Fail(
                $"The {operation} lock-owner probe failed with exit code {owner.ExitCode}. "
                + $"Output: {output} Error: {error}");
        }
    }

    private static async Task TerminateLockOwnerAsync(Process owner)
    {
        if (!owner.HasExited)
        {
            owner.Kill(entireProcessTree: true);
        }
        await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static Dictionary<string, byte[]> CaptureDurableFiles(
        string root,
        string readyPath,
        string releasePath) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            // The switch takes its narrower provider-switch lease before it discovers the
            // held root lease. Lock files are synchronization primitives, not mutation
            // receipts. The ready/release files are child-process test controls, not product
            // state; every user/configuration/deployment file remains in this snapshot.
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                "operation.lock",
                StringComparison.OrdinalIgnoreCase)
                && !PathEquals(path, readyPath)
                && !PathEquals(path, releasePath))
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static void AssertDurableFilesEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual,
        ContentionOperation operation)
    {
        CollectionAssert.AreEquivalent(
            expected.Keys.ToArray(),
            actual.Keys.ToArray(),
            $"{operation} created or removed a durable file before returning Busy.");
        foreach (var pair in expected)
        {
            CollectionAssert.AreEqual(
                pair.Value,
                actual[pair.Key],
                $"{operation} changed '{pair.Key}' before returning Busy.");
        }
    }

    public enum ContentionOperation
    {
        ProviderSwitch,
        Update,
        Remove,
        Settings,
        DataSync,
        Migration,
        Restore,
    }

    private sealed record ContentionScenario(
        string StateDirectory,
        Func<Task> AssertBusyAsync,
        IReadOnlyList<MutationSentinel> MutationSentinels)
    {
        public void AssertNoTransientMutation()
        {
            foreach (var sentinel in MutationSentinels)
            {
                sentinel.AssertUnobserved();
            }
        }
    }

    private sealed class MutationSentinel(ContentionOperation operation)
    {
        private int armed;
        private int observationCount;
        private string? firstBoundary;

        public void Arm()
        {
            firstBoundary = null;
            Interlocked.Exchange(ref observationCount, 0);
            Volatile.Write(ref armed, 1);
        }

        public void Observe(string boundary)
        {
            if (Volatile.Read(ref armed) == 0)
            {
                return;
            }
            if (Interlocked.Increment(ref observationCount) == 1)
            {
                firstBoundary = boundary;
            }
            Assert.Fail(
                $"{operation} reached '{boundary}' instead of returning Busy before mutation.");
        }

        public ValueTask ObserveAsync(string boundary)
        {
            Observe(boundary);
            return ValueTask.CompletedTask;
        }

        public void AssertUnobserved() =>
            Assert.AreEqual(
                0,
                Volatile.Read(ref observationCount),
                $"{operation} reached transient mutation boundary '{firstBoundary}'.");
    }

    private sealed class SentinelSelectionStore(
        ILauncherProviderSelectionStore inner,
        MutationSentinel mutationSentinel) : ILauncherProviderSelectionStore
    {
        public LauncherProviderSelection? Load() => inner.Load();

        public void Save(LauncherProviderSelection selection)
        {
            mutationSentinel.Observe("provider selection save");
            inner.Save(selection);
        }

        public void Clear()
        {
            mutationSentinel.Observe("provider selection clear");
            inner.Clear();
        }
    }

    private sealed class SentinelMutationBackup(
        IConfigurationMutationBackup inner,
        MutationSentinel mutationSentinel) : IConfigurationMutationBackup
    {
        public ValueTask<ConfigurationBackupReceipt> BeforeReplaceAsync(
            string configurationPath,
            byte[] expectedContents,
            CancellationToken cancellationToken)
        {
            mutationSentinel.Observe("configuration backup");
            return inner.BeforeReplaceAsync(
                configurationPath,
                expectedContents,
                cancellationToken);
        }
    }

    private sealed class CountingDownloader(byte[] contents) : IModArtifactDownloader
    {
        public int CallCount { get; private set; }

        public Task<ModArtifactDownload> DownloadAsync(
            Uri uri,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new ModArtifactDownload(
                HttpStatusCode.OK,
                contents,
                contents.LongLength));
        }
    }

    private sealed class FakeVersionReader(string version) : IModArtifactVersionReader
    {
        public string? ReadVersion(string artifactPath) => version;
    }

    private sealed class FakeAuthenticityVerifier : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) =>
            new(true, "trusted contention fixture");
    }

    private sealed class FakeReleaseDiscoveryClient(ModReleaseArtifact artifact)
        : IWindowsReleaseDiscoveryClient
    {
        public Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
            string channel,
            Version currentLauncherVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsReleaseDiscovery(
                new(
                    1,
                    artifact.ExpectedVersion,
                    $"v{artifact.ExpectedVersion}",
                    channel,
                    "active",
                    currentLauncherVersion,
                    new("example/repository", new string('0', 40)),
                    "none",
                    []),
                artifact));
    }

    private sealed class ReversingProtector : IConfigurationBackupProtector
    {
        public string SchemeId => "test-reverse-v1";

        public byte[] Protect(byte[] contents) => [.. contents.Reverse()];

        public byte[] Unprotect(byte[] protectedContents) => [.. protectedContents.Reverse()];
    }

    private sealed class NoOpStorageSecurity : IConfigurationBackupStorageSecurity
    {
        public void SecureDirectory(string directory) => Directory.CreateDirectory(directory);
    }

    private sealed class NotRunningProcessInspector : IGameProcessInspector
    {
        public GameProcessInspectionState Inspect(string gameDirectory) =>
            GameProcessInspectionState.NotRunning;
    }
}
