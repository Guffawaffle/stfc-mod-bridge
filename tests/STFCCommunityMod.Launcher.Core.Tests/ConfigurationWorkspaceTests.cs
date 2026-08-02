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
        Func<ConfigurationCommitRequest, ConfigurationRepositoryCommitResult> commit) :
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
    }
}
