using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ConfigurationWorkspaceTests
{
    [TestMethod]
    public void WorkspacePreparesTypedSemanticChanges()
    {
        var catalog = LoadCatalog();
        var snapshot = new ConfigurationDocumentSnapshot(
            Path.Combine(Path.GetTempPath(), $"workspace-{Guid.NewGuid():N}.toml"),
            Encoding.UTF8.GetBytes("# typed change-set fixture\n"));
        var repository = new RecordingRepository(
            snapshot,
            static request =>
                new(
                    AtomicTomlWriteState.NoChange,
                    new ConfigurationDocumentSnapshot(
                        request.Path,
                        request.BaselineContents)));
        var load = ConfigurationWorkspace.Load(
            snapshot.Path,
            catalog,
            repository,
            out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);
        var booleanSetting = catalog.Settings.Single(
            item => item.Path == "graphics.free_resize");
        var keybindingSetting = catalog.VisibleSettings.First(
            item => item.Control == LauncherConfigurationControl.Keybinding);
        var notificationSetting = catalog.Settings.Single(
            item => item.Path == "notifications.fleet_arrived_in_system");
        var sounds = LauncherNotificationPolicyParser
            .ReadAllowedSounds(notificationSetting);
        Assert.IsTrue(sounds.Count > 0);
        var sound = sounds[0];
        var policy = new LauncherNotificationPolicy(true, true, sound);

        workspace!.StageSet(booleanSetting, "false");
        workspace.StageSet(
            keybindingSetting,
            LauncherTomlValue.RenderString("CTRL-Q"));
        workspace.StageSet(notificationSetting, policy.Render());
        var changes = workspace.PrepareChangeSet().Changes;

        Assert.AreEqual(
            false,
            changes.Single(
                change => change.CanonicalPath == booleanSetting.Path).Value);
        Assert.AreEqual(
            "CTRL-Q",
            changes.Single(
                change => change.CanonicalPath == keybindingSetting.Path).Value);
        Assert.AreEqual(
            policy,
            changes.Single(
                change => change.CanonicalPath == notificationSetting.Path).Value);
    }

    [TestMethod]
    public async Task DataSyncCommitUsesConfigurationWorkspaceDocumentTransaction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"workspace-sync-{Guid.NewGuid():N}.toml");
        await File.WriteAllTextAsync(path, "# shared transaction fixture\n", new UTF8Encoding(false));
        try
        {
            var load = ConfigurationWorkspace.Load(
                path,
                LoadCatalog(),
                new TomlConfigurationRepository(),
                out var workspace);
            Assert.IsTrue(load.IsSuccess, load.Error);
            var syncLoad = workspace!.CreateSyncTopologyEditSession(out var session);
            Assert.IsTrue(syncLoad.IsValid, syncLoad.Error?.Message);

            var added = session!.Desired.AddTarget("community", SyncTargetKind.LegacyCommunity);
            var enabled = added.Topology.SetTargetEnabled("community", true);
            var configured = enabled.Topology.UpdateTarget(
                "community",
                target => target.WithConnection(
                    "https://community.example.invalid/sync",
                    SyncSecret.FromPlainText("fixture-secret")));
            session.Stage(configured.Topology);

            var result = await workspace.CommitSyncAsync(session);

            Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State, result.Error);
            Assert.IsFalse(session.HasPendingChanges);
            Assert.AreEqual(workspace.BaselineRevision, session.BaselineRevision);
            StringAssert.Contains(await File.ReadAllTextAsync(path), "[sync.targets.community]");
            Assert.IsTrue(File.Exists(path + ".bak"));
        }
        finally
        {
            foreach (var candidate in new[] { path, path + ".bak" })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
        }
    }

    [TestMethod]
    public async Task DataSyncCommitRejectsSessionFromDifferentDocumentWithIdenticalRevision()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var firstPath = Path.Combine(temporaryDirectory.Path, "first.toml");
        var secondPath = Path.Combine(temporaryDirectory.Path, "second.toml");
        var original = Encoding.UTF8.GetBytes("[sync]\njobs = true\n");
        await File.WriteAllBytesAsync(firstPath, original);
        await File.WriteAllBytesAsync(secondPath, original);
        var repository = new TomlConfigurationRepository();
        var firstLoad = ConfigurationWorkspace.Load(
            firstPath,
            LoadCatalog(),
            repository,
            out var firstWorkspace);
        var secondLoad = ConfigurationWorkspace.Load(
            secondPath,
            LoadCatalog(),
            repository,
            out var secondWorkspace);
        Assert.IsTrue(firstLoad.IsSuccess, firstLoad.Error);
        Assert.IsTrue(secondLoad.IsSuccess, secondLoad.Error);
        var syncLoad = firstWorkspace!.CreateSyncTopologyEditSession(out var session);
        Assert.IsTrue(syncLoad.IsValid, syncLoad.Error?.Message);
        session!.Stage(
            session.Desired.WithGlobalDefaults(
                session.Desired.GlobalDefaults.WithDataKind(SyncDataKind.Jobs, false)));

        var result = await secondWorkspace!.CommitSyncAsync(session);

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State);
        Assert.IsTrue(session.IsStale);
        Assert.IsTrue(session.HasPendingChanges);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(firstPath));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(secondPath));
        Assert.IsFalse(File.Exists(secondPath + ".bak"));
    }

    [TestMethod]
    public async Task DataSyncCommitPropagatesDurableBackupReceipt()
    {
        var snapshot = new ConfigurationDocumentSnapshot(
            Path.Combine(Path.GetTempPath(), $"workspace-sync-{Guid.NewGuid():N}.toml"),
            Encoding.UTF8.GetBytes("# receipt fixture\n"));
        var receipt = new ConfigurationBackupReceipt(
            "backup-id",
            "installation-id",
            "guffawaffle",
            null,
            new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero),
            snapshot.Revision.Sha256,
            "data-sync-save",
            "guffawaffle/stable");
        var repository = new RecordingRepository(
            snapshot,
            static _ =>
                new(
                    AtomicTomlWriteState.IoFailure,
                    Error: "Settings commit is not part of this fixture."),
            request =>
                new(
                    AtomicTomlWriteState.Succeeded,
                    new ConfigurationDocumentSnapshot(
                        request.Path,
                        request.DesiredContents),
                    BackupReceipt: receipt));
        var load = ConfigurationWorkspace.Load(
            snapshot.Path,
            LoadCatalog(),
            repository,
            out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);
        var syncLoad = workspace!.CreateSyncTopologyEditSession(out var session);
        Assert.IsTrue(syncLoad.IsValid, syncLoad.Error?.Message);
        var added = session!.Desired.AddTarget("community", SyncTargetKind.LegacyCommunity);
        session.Stage(added.Topology.SetTargetEnabled("community", true).Topology);

        var result = await workspace.CommitSyncAsync(session);

        Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State, result.Error);
        Assert.AreEqual(receipt, result.BackupReceipt);
    }

    [TestMethod]
    public void DiscardPublishesOneBatchedStructuralTransition()
    {
        var catalog = LoadCatalog();
        var snapshot = new ConfigurationDocumentSnapshot(
            Path.Combine(Path.GetTempPath(), $"workspace-{Guid.NewGuid():N}.toml"),
            Encoding.UTF8.GetBytes("# batched event fixture\n"));
        var repository = new RecordingRepository(
            snapshot,
            static _ =>
                new(
                    AtomicTomlWriteState.IoFailure,
                    Error: "Commit is not part of this fixture."));
        var load = ConfigurationWorkspace.Load(
            snapshot.Path,
            catalog,
            repository,
            out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);
        var settings = catalog.VisibleSettings
            .Where(
                setting =>
                    setting.Control == LauncherConfigurationControl.Scalar
                    && setting.ValueKind == LauncherConfigurationValueKind.Boolean)
            .Take(2)
            .ToArray();
        var transitions = new List<ConfigurationWorkspaceChangedEventArgs>();
        workspace!.WorkspaceChanged += (_, transition) => transitions.Add(transition);
        workspace.StageSet(settings[0], "false");
        workspace.StageSet(settings[1], "false");
        transitions.Clear();

        workspace.Discard();

        Assert.AreEqual(1, transitions.Count);
        var transition = transitions[0];
        Assert.AreEqual(
            ConfigurationWorkspaceTransitionReason.Discarded,
            transition.Reason);
        Assert.AreEqual(2, transition.ChangedIds.Count);
        Assert.AreEqual(0, transition.AddedIds.Count);
        Assert.AreEqual(0, transition.RemovedIds.Count);
        Assert.IsTrue(
            transition.Invalidations.HasFlag(
                ConfigurationWorkspaceInvalidation.Query));
        Assert.IsFalse(
            transition.Invalidations.HasFlag(
                ConfigurationWorkspaceInvalidation.Layout));
        Assert.AreEqual(workspace.Revision, transition.WorkspaceRevision);
    }

    [TestMethod]
    public void RepeatingTheCurrentDraftDoesNotPublishAWorkspaceTransition()
    {
        var catalog = LoadCatalog();
        var snapshot = new ConfigurationDocumentSnapshot(
            Path.Combine(Path.GetTempPath(), $"workspace-{Guid.NewGuid():N}.toml"),
            Encoding.UTF8.GetBytes("[graphics]\nfree_resize = true\n"));
        var repository = new RecordingRepository(
            snapshot,
            static _ =>
                new(
                    AtomicTomlWriteState.IoFailure,
                    Error: "Commit is not part of this fixture."));
        var load = ConfigurationWorkspace.Load(
            snapshot.Path,
            catalog,
            repository,
            out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);
        var setting = catalog.Settings.Single(
            item => item.Path == "graphics.free_resize");
        var transitions = new List<ConfigurationWorkspaceChangedEventArgs>();
        workspace!.WorkspaceChanged += (_, transition) => transitions.Add(transition);

        var first = workspace.StageSet(setting, "false");
        var repeated = workspace.StageSet(setting, "false");

        Assert.IsTrue(first.IsValid, first.Error?.Message);
        Assert.IsTrue(repeated.IsValid, repeated.Error?.Message);
        Assert.AreEqual(1, transitions.Count);
        Assert.AreEqual(1, workspace.Revision);
    }

    [TestMethod]
    public async Task SuccessfulCommitAdvancesBaselineOnlyAfterAtomicWrite()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string original =
            "# keep this comment\n[graphics]\nfree_resize = true\nunknown = \"keep\"\n";
        await File.WriteAllTextAsync(path, original);
        var catalog = LoadCatalog();
        var repository = new TomlConfigurationRepository();
        var load = ConfigurationWorkspace.Load(
            path,
            catalog,
            repository,
            out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);
        var setting = catalog.Settings.Single(
            item => item.Path == "graphics.free_resize");
        var originalRevision = workspace!.BaselineRevision;

        var stage = workspace.StageSet(setting, "false");
        ConfigurationWorkspaceChangedEventArgs? committedTransition = null;
        workspace.WorkspaceChanged += (_, transition) =>
        {
            if (transition.Reason
                == ConfigurationWorkspaceTransitionReason.Committed)
            {
                committedTransition = transition;
            }
        };
        var result = await workspace.CommitAsync();

        Assert.IsTrue(stage.IsValid, stage.Error?.Message);
        Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State, result.Error);
        Assert.IsFalse(workspace.HasPendingChanges);
        Assert.AreNotEqual(originalRevision, workspace.BaselineRevision);
        var saved = await File.ReadAllTextAsync(path);
        StringAssert.Contains(saved, "free_resize = false");
        StringAssert.Contains(saved, "# keep this comment");
        StringAssert.Contains(saved, "unknown = \"keep\"");
        Assert.AreEqual(original, await File.ReadAllTextAsync(path + ".bak"));
        Assert.IsNotNull(committedTransition);
        Assert.IsTrue(committedTransition.ChangedIds.Contains(setting.Path));
        Assert.AreEqual(workspace.Revision, committedTransition.WorkspaceRevision);
    }

    [TestMethod]
    public async Task MissingConfigurationIsEditableAndFirstSaveCreatesOnlyTheStagedOverride()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var catalog = LoadCatalog();
        var load = ConfigurationWorkspace.Load(
            path,
            catalog,
            new TomlConfigurationRepository(),
            out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);
        Assert.IsNotNull(workspace);
        Assert.IsFalse(workspace.DocumentExists);
        Assert.IsFalse(File.Exists(path));
        var setting = catalog.Settings.Single(
            item => item.Path == "graphics.free_resize");

        var stage = workspace.StageSet(setting, "false");
        var result = await workspace.CommitAsync();

        Assert.IsTrue(stage.IsValid, stage.Error?.Message);
        Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State, result.Error);
        Assert.IsTrue(workspace.DocumentExists);
        Assert.IsTrue(File.Exists(path));
        StringAssert.Contains(await File.ReadAllTextAsync(path), "free_resize = false");
        Assert.IsFalse(File.Exists(path + ".bak"));
    }

    [TestMethod]
    public async Task MissingConfigurationNoOpCommitDoesNotCreateAnEmptyFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var load = ConfigurationWorkspace.Load(
            path,
            LoadCatalog(),
            new TomlConfigurationRepository(),
            out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);

        var result = await workspace!.CommitAsync();

        Assert.AreEqual(AtomicTomlWriteState.NoChange, result.State, result.Error);
        Assert.IsFalse(workspace.DocumentExists);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task FirstSaveConflictsWithAnExternallyCreatedConfiguration()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var catalog = LoadCatalog();
        var load = ConfigurationWorkspace.Load(
            path,
            catalog,
            new TomlConfigurationRepository(),
            out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);
        var setting = catalog.Settings.Single(
            item => item.Path == "graphics.free_resize");
        workspace!.StageSet(setting, "false");
        const string external = "# created outside Mod Bridge\n[custom]\nkeep = true\n";
        await File.WriteAllTextAsync(path, external);

        var result = await workspace.CommitAsync();

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State);
        Assert.IsTrue(workspace.HasPendingChanges);
        Assert.IsTrue(workspace.IsStale);
        Assert.AreEqual(external, await File.ReadAllTextAsync(path));
        Assert.IsFalse(File.Exists(path + ".bak"));
    }

    [TestMethod]
    public async Task RepositoryFailureLeavesDraftAndBaselineUnchanged()
    {
        var catalog = LoadCatalog();
        var contents = Encoding.UTF8.GetBytes(
            "[graphics]\nfree_resize = true\n");
        var snapshot = new ConfigurationDocumentSnapshot(
            Path.Combine(Path.GetTempPath(), $"workspace-{Guid.NewGuid():N}.toml"),
            contents);
        var repository = new RecordingRepository(
            snapshot,
            static _ =>
                new(
                    AtomicTomlWriteState.IoFailure,
                    Error: "Injected repository failure."));
        var load = ConfigurationWorkspace.Load(
            snapshot.Path,
            catalog,
            repository,
            out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);
        var setting = catalog.Settings.Single(
            item => item.Path == "graphics.free_resize");
        var originalRevision = workspace!.BaselineRevision;
        workspace.StageSet(setting, "false");

        var result = await workspace.CommitAsync();

        Assert.AreEqual(AtomicTomlWriteState.IoFailure, result.State);
        Assert.IsTrue(workspace.HasPendingChanges);
        Assert.AreEqual(originalRevision, workspace.BaselineRevision);
        Assert.IsFalse(workspace.IsStale);
        Assert.IsNotNull(repository.LastCommitRequest);
        Assert.AreEqual(
            originalRevision,
            repository.LastCommitRequest.ExpectedRevision);
        Assert.AreEqual(1, repository.LastCommitRequest.Changes.Changes.Count);
    }

    [TestMethod]
    public async Task ExternalChangeMarksWorkspaceStaleAndPreservesDraft()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string original = "[graphics]\nfree_resize = true\n";
        const string external =
            "[graphics]\nfree_resize = true\n# external player edit\n";
        await File.WriteAllTextAsync(path, original);
        var catalog = LoadCatalog();
        var load = ConfigurationWorkspace.Load(
            path,
            catalog,
            new TomlConfigurationRepository(),
            out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);
        var setting = catalog.Settings.Single(
            item => item.Path == "graphics.free_resize");
        var originalRevision = workspace!.BaselineRevision;
        workspace.StageSet(setting, "false");
        await File.WriteAllTextAsync(path, external);
        ConfigurationWorkspaceChangedEventArgs? conflictTransition = null;
        workspace.WorkspaceChanged += (_, transition) =>
        {
            if (transition.Reason
                == ConfigurationWorkspaceTransitionReason.ExternalConflict)
            {
                conflictTransition = transition;
            }
        };

        var result = await workspace.CommitAsync();

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State);
        Assert.IsTrue(workspace.HasPendingChanges);
        Assert.IsTrue(workspace.IsStale);
        Assert.AreEqual(originalRevision, workspace.BaselineRevision);
        Assert.AreEqual(external, await File.ReadAllTextAsync(path));
        Assert.IsFalse(File.Exists(path + ".bak"));
        Assert.IsNotNull(conflictTransition);
        Assert.AreEqual(0, conflictTransition.ChangedIds.Count);
        Assert.IsTrue(
            conflictTransition.Invalidations.HasFlag(
                ConfigurationWorkspaceInvalidation.ExternalState));
    }

    [TestMethod]
    public async Task RepositoryRejectsMismatchedExpectedRevision()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var contents = Encoding.UTF8.GetBytes(
            "[graphics]\nfree_resize = true\n");
        await File.WriteAllBytesAsync(path, contents);
        var setting = LoadCatalog().Settings.Single(
            item => item.Path == "graphics.free_resize");
        var changes = new ConfigurationChangeSet(
        [
            new(
                "graphics.free_resize",
                setting,
                ConfigurationSemanticChangeKind.SetOverride,
                false),
        ]);
        var request = new ConfigurationCommitRequest(
            path,
            new ConfigurationDocumentRevision("NOT-THE-BASELINE"),
            contents,
            changes);

        var result = await new TomlConfigurationRepository()
            .CommitAsync(request);

        Assert.AreEqual(AtomicTomlWriteState.Invalid, result.State);
        CollectionAssert.AreEqual(contents, await File.ReadAllBytesAsync(path));
        Assert.IsFalse(File.Exists(path + ".bak"));
    }

    [TestMethod]
    public async Task SettingsCommitReturnsBusyWhileRootMutationLeaseIsHeld()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var path = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var original = Encoding.UTF8.GetBytes("[graphics]\nfree_resize = true\n");
        await File.WriteAllBytesAsync(path, original);
        var repository = new TomlConfigurationRepository(
            mutationAdmission: new LauncherOperationLock(stateDirectory));
        var load = ConfigurationWorkspace.Load(path, LoadCatalog(), repository, out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);
        var setting = LoadCatalog().Settings.Single(item => item.Path == "graphics.free_resize");
        workspace!.StageSet(setting, "false");

        await using (var lease = await new LauncherOperationLock(stateDirectory).TryAcquireAsync())
        {
            Assert.IsNotNull(lease);
            var busy = await workspace.CommitAsync();

            Assert.AreEqual(AtomicTomlWriteState.Busy, busy.State, busy.Error);
            Assert.IsTrue(workspace.HasPendingChanges);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
            Assert.IsFalse(File.Exists(path + ".bak"));
        }

        var committed = await workspace.CommitAsync();
        Assert.AreEqual(AtomicTomlWriteState.Succeeded, committed.State, committed.Error);
        Assert.IsFalse(workspace.HasPendingChanges);
    }

    [TestMethod]
    public async Task DocumentCommitReturnsBusyBeforeBackupOrReplacement()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var path = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var original = Encoding.UTF8.GetBytes("# original\n[graphics]\nfree_resize = true\n");
        var desired = Encoding.UTF8.GetBytes("# original\n[graphics]\nfree_resize = false\n");
        await File.WriteAllBytesAsync(path, original);
        var repository = new TomlConfigurationRepository(
            mutationAdmission: new LauncherOperationLock(stateDirectory));
        var request = new ConfigurationDocumentCommitRequest(
            path,
            ConfigurationDocumentRevision.FromContents(original),
            original,
            desired);

        await using var lease = await new LauncherOperationLock(stateDirectory).TryAcquireAsync();
        Assert.IsNotNull(lease);
        var result = await repository.CommitDocumentAsync(request);

        Assert.AreEqual(AtomicTomlWriteState.Busy, result.State, result.Error);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
        Assert.IsFalse(File.Exists(path + ".bak"));
    }

    [TestMethod]
    public async Task RepositoryHoldsRootMutationLeaseThroughAtomicReplacement()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var path = Path.Combine(temporaryDirectory.Path, "settings.toml");
        await File.WriteAllTextAsync(path, "[graphics]\nfree_resize = true\n");
        var pause = new PausedAtomicWrite();
        var repository = new TomlConfigurationRepository(
            store: new AtomicTomlStore(pause.BeforeReplaceAsync),
            mutationAdmission: new LauncherOperationLock(stateDirectory));
        var load = ConfigurationWorkspace.Load(path, LoadCatalog(), repository, out var workspace);
        Assert.IsTrue(load.IsSuccess, load.Error);
        var setting = LoadCatalog().Settings.Single(item => item.Path == "graphics.free_resize");
        workspace!.StageSet(setting, "false");

        var commit = workspace.CommitAsync();
        await pause.Started;
        try
        {
            await using var competingLease = await new LauncherOperationLock(stateDirectory)
                .TryAcquireAsync();
            Assert.IsNull(competingLease);
        }
        finally
        {
            pause.Release();
        }

        var result = await commit;
        Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State, result.Error);
    }

    private static LauncherConfigurationCatalog LoadCatalog()
    {
        var schemaPath = FindRepositoryFile(
            "docs",
            "windows-launcher",
            "config-schema.guffawaffle.v1.json");
        return LauncherConfigurationSchemaLoader.LoadFile(schemaPath);
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

    private sealed class RecordingRepository(
        ConfigurationDocumentSnapshot snapshot,
        Func<ConfigurationCommitRequest, ConfigurationRepositoryCommitResult> commit,
        Func<ConfigurationDocumentCommitRequest, ConfigurationRepositoryCommitResult>? documentCommit = null) :
        IConfigurationRepository
    {
        public ConfigurationCommitRequest? LastCommitRequest { get; private set; }

        public ConfigurationRepositoryReadResult Read(string? configurationPath) =>
            new(ConfigurationRepositoryReadState.Succeeded, snapshot);

        public Task<ConfigurationRepositoryCommitResult> CommitAsync(
            ConfigurationCommitRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCommitRequest = request;
            return Task.FromResult(commit(request));
        }

        public Task<ConfigurationRepositoryCommitResult> CommitDocumentAsync(
            ConfigurationDocumentCommitRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                documentCommit?.Invoke(request)
                    ?? new ConfigurationRepositoryCommitResult(
                        AtomicTomlWriteState.Invalid,
                        Error: "Document commit is not part of this fixture."));
        }
    }

    private sealed class PausedAtomicWrite
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public void Release() => released.TrySetResult();

        public async ValueTask BeforeReplaceAsync(
            string temporaryPath,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            _ = temporaryPath;
            _ = destinationPath;
            started.TrySetResult();
            await released.Task.WaitAsync(cancellationToken);
        }
    }
}
