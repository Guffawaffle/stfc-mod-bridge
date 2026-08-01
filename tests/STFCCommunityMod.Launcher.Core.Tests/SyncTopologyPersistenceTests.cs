using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class SyncTopologyPersistenceTests
{
    [TestMethod]
    public void LockedCorpusLoadsWithoutChangingAnySourceBytes()
    {
        var corpusDirectory = Path.GetDirectoryName(
            FindRepositoryFile("docs", "windows-launcher", "sync-target-corpus", "cases.json"))!;
        foreach (var path in Directory.GetFiles(corpusDirectory, "*.toml"))
        {
            var contents = File.ReadAllBytes(path);

            var load = SyncTopologyTomlAdapter.Load(contents);
            var sparseLoad = SparseTomlDocument.Load(contents, out var document);
            var validation = document!.ValidateForMutation();

            Assert.IsTrue(load.IsValid, Path.GetFileName(path));
            Assert.IsNotNull(load.Topology, Path.GetFileName(path));
            Assert.IsTrue(sparseLoad.IsValid, Path.GetFileName(path));
            Assert.IsFalse(validation.Changed, Path.GetFileName(path));
            CollectionAssert.AreEqual(contents, validation.Contents, Path.GetFileName(path));
        }
    }

    [TestMethod]
    public void AdapterNativeDefaultsMatchTheGeneratedRuntimeSchema()
    {
        var catalog = LauncherConfigurationSchemaLoader.LoadFile(
            FindRepositoryFile("docs", "windows-launcher", "config-schema.guffawaffle.v1.json"));
        var defaults = SyncTopologyTomlAdapter.NativeGlobalDefaults;

        Assert.AreEqual(
            defaults.Proxy,
            catalog.Settings.Single(item => item.Path == "sync.proxy").DefaultValue.GetString());
        Assert.AreEqual(
            defaults.VerifySsl,
            catalog.Settings.Single(item => item.Path == "sync.verify_ssl").DefaultValue.GetBoolean());
        Assert.AreEqual(
            defaults.AllowUnsafeTlsWithoutCertificateValidation,
            catalog.Settings.Single(
                item => item.Path == "sync.allow_unsafe_tls_without_certificate_validation").DefaultValue.GetBoolean());
        foreach (var (kind, key) in SyncTopologyTomlAdapter.DataKeys)
        {
            Assert.AreEqual(
                defaults.DataKinds[kind],
                catalog.Settings.Single(item => item.Path == $"sync.{key}").DefaultValue.GetBoolean(),
                key);
        }
    }

    [TestMethod]
    public void AdapterPreservesExplicitFalseEmptyAndInheritedResolution()
    {
        var load = Load(
            """
            [sync]
            proxy = "http://proxy.example.invalid:8080"
            battlelogs = true

            [sync.targets.direct]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            proxy = ""
            battlelogs = false
            """);

        var target = load.Topology!.Targets["direct"];
        Assert.AreEqual(SyncOverride.Explicit(string.Empty), target.Proxy);
        Assert.AreEqual(SyncOverride.Explicit(false), target.DataOverrides[SyncDataKind.Battlelogs]);
        var resolved = load.Topology.Resolve().Targets.Single();
        Assert.AreEqual(SyncValueProvenance.ExplicitEmpty, resolved.Proxy.Provenance);
        Assert.AreEqual(SyncValueProvenance.ExplicitFalse, resolved.DataKinds[SyncDataKind.Battlelogs].Provenance);
    }

    [TestMethod]
    public void SidecarBrokerModeIsABlockingExternalTargetDiagnostic()
    {
        var load = Load(
            """
            [sync.targets.external]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            mode = "sidecar_broker"
            """);

        var plan = SyncTopologyPersistencePlanner.Build(load, load.Topology!);

        Assert.IsFalse(plan.IsValid);
        Assert.IsTrue(plan.Diagnostics.Any(item => item.Code == "SYNC_TARGET_SIDECAR_NAMESPACE_INVALID"));
    }

    [TestMethod]
    public void EmptyTargetTableIsDiscoveredAndRejectedAsMalformed()
    {
        var load = Load(
            """
            [sync.targets.empty]
            # An explicitly declared target cannot disappear merely because it has no assignments.
            """);

        Assert.IsTrue(load.Topology!.Targets.ContainsKey("empty"));
        var plan = SyncTopologyPersistencePlanner.Build(load, load.Topology);
        Assert.IsFalse(plan.IsValid);
        Assert.IsTrue(plan.Diagnostics.Any(item => item.Code == "SYNC_ENDPOINT_INVALID"));
        Assert.IsTrue(plan.Diagnostics.Any(item => item.Code == "SYNC_CREDENTIALS_INCOMPLETE"));
    }

    [TestMethod]
    public void ClearingOverridesRemovesOnlyOwnedAssignments()
    {
        const string source = """
            [sync]
            proxy = "http://proxy.example.invalid:8080"
            battlelogs = true

            [sync.targets.direct]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            proxy = ""
            battlelogs = false
            future = "preserve" # unknown ownership
            """;
        var baseline = Load(source);
        var desired = RequireSuccess(
            baseline.Topology!.UpdateTarget(
                "direct",
                target => target
                    .WithProxy(SyncOverride.Inherited<string>())
                    .WithDataOverride(SyncDataKind.Battlelogs, SyncOverride.Inherited<bool>())));

        var plan = SyncTopologyPersistencePlanner.Build(baseline, desired);
        var edit = plan.Apply(Encoding.UTF8.GetBytes(source));
        var updated = Encoding.UTF8.GetString(edit.Contents!);

        Assert.IsTrue(plan.IsValid);
        Assert.IsTrue(edit.IsValid, edit.Error?.Message);
        Assert.IsFalse(updated.Contains("proxy = \"\"", StringComparison.Ordinal));
        Assert.IsFalse(updated.Contains("battlelogs = false", StringComparison.Ordinal));
        StringAssert.Contains(updated, "proxy = \"http://proxy.example.invalid:8080\"");
        StringAssert.Contains(updated, "battlelogs = true");
        StringAssert.Contains(updated, "future = \"preserve\" # unknown ownership");
    }

    [TestMethod]
    public void AddingTargetWritesExplicitModeAndKeepsSecretsOutOfPlanDisplay()
    {
        const string secret = "never-show-this-token";
        var baseline = Load("# empty sync topology\n");
        var desired = RequireSuccess(
            baseline.Topology!.AddTarget("majel", SyncTargetKind.MajelIngest));
        desired = RequireSuccess(
            desired.UpdateTarget(
                "majel",
                target => target
                    .WithConnection(
                        "https://majel.example.invalid/api/ingest/events",
                        SyncSecret.FromPlainText(secret))
                    .WithEnabled(true)
                    .WithDataOverride(SyncDataKind.BattlelogsRealtime, SyncOverride.Explicit(true))));

        var plan = SyncTopologyPersistencePlanner.Build(baseline, desired);
        var display = string.Join('\n', plan.Mutations.Select(mutation => mutation.ToString()));
        var edit = plan.Apply(Encoding.UTF8.GetBytes("# empty sync topology\n"));
        var updated = Encoding.UTF8.GetString(edit.Contents!);

        Assert.IsTrue(plan.IsValid);
        Assert.IsFalse(display.Contains(secret, StringComparison.Ordinal));
        StringAssert.Contains(display, "[secret]");
        StringAssert.Contains(updated, "[sync.targets.majel]");
        StringAssert.Contains(updated, "mode = \"majel\"");
        StringAssert.Contains(updated, "battlelogs_realtime = true");
        StringAssert.Contains(updated, $"token = \"{secret}\"");
    }

    [TestMethod]
    public void UnchangedSecretProducesNoSecretMutation()
    {
        var baseline = Load(
            """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            mode = "legacy"
            """);

        var plan = SyncTopologyPersistencePlanner.Build(baseline, baseline.Topology!);

        Assert.IsTrue(plan.IsValid);
        Assert.AreEqual(0, plan.Mutations.Count);
    }

    [TestMethod]
    public void RenamePreservesUnknownTargetBodyAndComments()
    {
        const string source = """
            # provider target
            [sync.targets.old]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            mode = "legacy"
            future = "preserve" # exact comment
            """;
        var baseline = Load(source);
        var desired = RequireSuccess(baseline.Topology!.RenameTarget("old", "renamed"));

        var plan = SyncTopologyPersistencePlanner.Build(
            baseline,
            desired,
            new Dictionary<string, string> { ["old"] = "renamed" });
        var edit = plan.Apply(Encoding.UTF8.GetBytes(source));
        var updated = Encoding.UTF8.GetString(edit.Contents!);

        Assert.IsTrue(plan.IsValid);
        StringAssert.Contains(updated, "[sync.targets.renamed]");
        Assert.IsFalse(updated.Contains("[sync.targets.old]", StringComparison.Ordinal));
        StringAssert.Contains(updated, "future = \"preserve\" # exact comment");
    }

    [TestMethod]
    public void RemovingTargetRemovesWholeOwnedTableOnly()
    {
        const string source = """
            [sync.targets.remove]
            url = "https://remove.example.invalid/sync"
            token = "fixture-remove"
            unknown = "remove too"

            [sync.targets.keep]
            url = "https://keep.example.invalid/sync"
            token = "fixture-keep"
            unknown = "keep"
            """;
        var baseline = Load(source);
        var desired = RequireSuccess(baseline.Topology!.RemoveTarget("remove"));

        var plan = SyncTopologyPersistencePlanner.Build(baseline, desired);
        var updated = Encoding.UTF8.GetString(plan.Apply(Encoding.UTF8.GetBytes(source)).Contents!);

        Assert.IsFalse(updated.Contains("sync.targets.remove", StringComparison.Ordinal));
        Assert.IsFalse(updated.Contains("remove too", StringComparison.Ordinal));
        StringAssert.Contains(updated, "[sync.targets.keep]");
        StringAssert.Contains(updated, "unknown = \"keep\"");
    }

    [TestMethod]
    public void LegacyRootIsVirtualUntilMigrationIsExplicitlyConfirmed()
    {
        const string source = """
            [sync]
            url = "https://legacy.example.invalid/sync"
            token = "fixture-legacy"
            battlelogs = true
            """;
        var baseline = Load(source);
        Assert.IsTrue(baseline.HasLegacyRootTarget);
        var desired = RequireSuccess(
            baseline.Topology!.UpdateTarget(
                "default",
                target => target.WithDataOverride(SyncDataKind.Battlelogs, SyncOverride.Explicit(false))));

        var blocked = SyncTopologyPersistencePlanner.Build(baseline, desired);
        var migration = SyncTopologyPersistencePlanner.Build(
            baseline,
            desired,
            migrateLegacyRoot: true);
        var updated = Encoding.UTF8.GetString(migration.Apply(Encoding.UTF8.GetBytes(source)).Contents!);

        Assert.IsFalse(blocked.IsValid);
        Assert.IsTrue(blocked.Diagnostics.Any(item => item.Code == "SYNC_LEGACY_MIGRATION_REQUIRED"));
        Assert.IsTrue(migration.IsValid);
        StringAssert.Contains(updated, "[sync.targets.default]");
        StringAssert.Contains(updated, "battlelogs = false");
        var migrated = Load(updated);
        var sparseLoad = SparseTomlDocument.Load(Encoding.UTF8.GetBytes(updated), out var document);
        var overrides = document!.ReadOverrides();
        Assert.IsTrue(sparseLoad.IsValid);
        Assert.IsFalse(overrides.Overrides!.ContainsKey("sync.url"));
        Assert.IsFalse(overrides.Overrides.ContainsKey("sync.token"));
        Assert.IsTrue(overrides.Overrides.ContainsKey("sync.targets.default.url"));
        Assert.IsFalse(migrated.HasLegacyRootTarget);
    }

    [TestMethod]
    public void ExternalDisabledDraftRequiresAnExplicitPersistenceChoice()
    {
        var baseline = Load(
            """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """);
        var desired = RequireSuccess(baseline.Topology!.SetTargetEnabled("community", false));

        var plan = SyncTopologyPersistencePlanner.Build(baseline, desired);

        Assert.IsFalse(plan.IsValid);
        Assert.IsTrue(plan.Diagnostics.Any(item => item.Code == "SYNC_EXTERNAL_DISABLED_PERSISTENCE_REQUIRED"));
    }

    [TestMethod]
    public void KindChangeRequiresExplicitCompatibleOrResetDecision()
    {
        const string source = """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            mode = "legacy"
            proxy = "target-proxy"
            jobs = false
            """;
        var baseline = Load(source);
        var preserved = RequireSuccess(
            baseline.Topology!.ChangeTargetKind("community", SyncTargetKind.MajelIngest));
        var reset = RequireSuccess(
            baseline.Topology.ChangeTargetKind(
                "community",
                SyncTargetKind.MajelIngest,
                SyncTargetKindChangePolicy.ResetOverrides));

        var missingDecision = SyncTopologyPersistencePlanner.Build(baseline, preserved);
        var mismatchedReset = SyncTopologyPersistencePlanner.Build(
            baseline,
            preserved,
            kindChangeDecisions: new Dictionary<string, SyncTargetKindChangePolicy>
            {
                ["community"] = SyncTargetKindChangePolicy.ResetOverrides,
            });
        var validReset = SyncTopologyPersistencePlanner.Build(
            baseline,
            reset,
            kindChangeDecisions: new Dictionary<string, SyncTargetKindChangePolicy>
            {
                ["community"] = SyncTargetKindChangePolicy.ResetOverrides,
            });
        var updated = Encoding.UTF8.GetString(validReset.Apply(Encoding.UTF8.GetBytes(source)).Contents!);

        Assert.IsFalse(missingDecision.IsValid);
        Assert.IsTrue(missingDecision.Diagnostics.Any(item => item.Code == "SYNC_KIND_CHANGE_DECISION_REQUIRED"));
        Assert.IsFalse(mismatchedReset.IsValid);
        Assert.IsTrue(mismatchedReset.Diagnostics.Any(item => item.Code == "SYNC_KIND_CHANGE_RESET_MISMATCH"));
        Assert.IsTrue(validReset.IsValid);
        StringAssert.Contains(updated, "mode = \"majel\"");
        Assert.IsFalse(updated.Contains("proxy =", StringComparison.Ordinal));
        Assert.IsFalse(updated.Contains("jobs = false", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PersistedEffectiveValuesMatchTheLauncherPreviewAfterReload()
    {
        const string source = """
            [sync]
            proxy = "global-proxy"
            jobs = true

            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            mode = "legacy"
            """;
        var baseline = Load(source);
        var desired = baseline.Topology!.WithGlobalDefaults(
            baseline.Topology.GlobalDefaults.WithDataKind(SyncDataKind.Jobs, false));
        desired = RequireSuccess(
            desired.UpdateTarget(
                "community",
                target => target
                    .WithProxy(SyncOverride.Explicit(string.Empty))
                    .WithVerifySsl(SyncOverride.Explicit(false))
                    .WithUnsafeTls(SyncOverride.Explicit(true))));
        var preview = desired.Resolve().Targets.Single();

        var plan = SyncTopologyPersistencePlanner.Build(baseline, desired);
        var edit = plan.Apply(Encoding.UTF8.GetBytes(source));
        var reloaded = SyncTopologyTomlAdapter.Load(edit.Contents!).Topology!.Resolve().Targets.Single();

        Assert.IsTrue(plan.IsValid);
        Assert.AreEqual(preview.Kind, reloaded.Kind);
        Assert.AreEqual(preview.Url, reloaded.Url);
        Assert.AreEqual(preview.CredentialsConfigured, reloaded.CredentialsConfigured);
        Assert.AreEqual(preview.Proxy, reloaded.Proxy);
        Assert.AreEqual(preview.VerifySsl, reloaded.VerifySsl);
        Assert.AreEqual(
            preview.AllowUnsafeTlsWithoutCertificateValidation,
            reloaded.AllowUnsafeTlsWithoutCertificateValidation);
        Assert.AreEqual(preview.DataKinds[SyncDataKind.Jobs], reloaded.DataKinds[SyncDataKind.Jobs]);
    }

    [TestMethod]
    public async Task WorkspaceCommitUsesAtomicBackupAndDiscardRestoresBaseline()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string source = """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            mode = "legacy"
            """;
        await File.WriteAllTextAsync(path, source);
        var snapshot = new ConfigurationDocumentSnapshot(path, Encoding.UTF8.GetBytes(source));
        var load = SyncTopologyPersistenceWorkspace.Load(snapshot, out var workspace);
        Assert.IsTrue(load.IsValid, load.Error?.Message);

        var staged = RequireSuccess(
            workspace!.Desired.UpdateTarget(
                "community",
                target => target.WithProxy(SyncOverride.Explicit(string.Empty))));
        workspace.Stage(staged);
        Assert.IsTrue(workspace.HasPendingChanges);
        workspace.Discard();
        Assert.IsFalse(workspace.HasPendingChanges);
        Assert.IsFalse(workspace.Desired.Targets["community"].Proxy.IsExplicit);

        workspace.Stage(staged);
        var result = await workspace.CommitAsync();

        Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State, result.Error);
        Assert.IsFalse(workspace.HasPendingChanges);
        StringAssert.Contains(await File.ReadAllTextAsync(path), "proxy = \"\"");
        Assert.AreEqual(source, await File.ReadAllTextAsync(path + ".bak"));
    }

    [TestMethod]
    public async Task WorkspaceConflictPreservesExternalFileAndPendingDraft()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string source = """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """;
        await File.WriteAllTextAsync(path, source);
        var snapshot = new ConfigurationDocumentSnapshot(path, Encoding.UTF8.GetBytes(source));
        SyncTopologyPersistenceWorkspace.Load(snapshot, out var workspace);
        var staged = RequireSuccess(
            workspace!.Desired.UpdateTarget(
                "community",
                target => target.WithDataOverride(SyncDataKind.Jobs, SyncOverride.Explicit(false))));
        workspace.Stage(staged);
        const string external = "# external change\n" + source;
        await File.WriteAllTextAsync(path, external);

        var result = await workspace.CommitAsync();

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State);
        Assert.IsTrue(workspace.IsStale);
        Assert.IsTrue(workspace.HasPendingChanges);
        Assert.AreEqual(external, await File.ReadAllTextAsync(path));
        Assert.IsFalse(File.Exists(path + ".bak"));
    }

    [TestMethod]
    public void WorkspaceSemanticNoOpDoesNotCreatePendingChanges()
    {
        const string source = """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """;
        var snapshot = new ConfigurationDocumentSnapshot(
            "settings.toml",
            Encoding.UTF8.GetBytes(source));
        SyncTopologyPersistenceWorkspace.Load(snapshot, out var workspace);

        workspace!.Stage(workspace.Desired);

        Assert.IsFalse(workspace.HasPendingChanges);
    }

    [TestMethod]
    public async Task WorkspaceConflictRemainsStaleAcrossStageAndDiscard()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string source = """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """;
        await File.WriteAllTextAsync(path, source);
        var snapshot = new ConfigurationDocumentSnapshot(path, Encoding.UTF8.GetBytes(source));
        SyncTopologyPersistenceWorkspace.Load(snapshot, out var workspace);
        var staged = RequireSuccess(
            workspace!.Desired.UpdateTarget(
                "community",
                target => target.WithDataOverride(SyncDataKind.Jobs, SyncOverride.Explicit(false))));
        workspace.Stage(staged);
        await File.WriteAllTextAsync(path, "# external change\n" + source);
        Assert.AreEqual(AtomicTomlWriteState.Conflict, (await workspace.CommitAsync()).State);

        workspace.Stage(staged);
        Assert.IsTrue(workspace.IsStale);
        workspace.Discard();

        Assert.IsTrue(workspace.IsStale);
        Assert.IsFalse(workspace.HasPendingChanges);
    }

    private static SyncTopologyTomlLoadResult Load(string source)
    {
        var load = SyncTopologyTomlAdapter.Load(Encoding.UTF8.GetBytes(source));
        Assert.IsTrue(load.IsValid, load.Error?.Message);
        Assert.IsNotNull(load.Topology);
        return load;
    }

    private static SyncDesiredTopology RequireSuccess(SyncTopologyTransitionResult result)
    {
        Assert.IsTrue(result.Succeeded, result.Diagnostic?.Message);
        return result.Topology;
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
}
