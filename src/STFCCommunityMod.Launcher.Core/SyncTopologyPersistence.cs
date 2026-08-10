using System.Collections.ObjectModel;

namespace STFCCommunityMod.Launcher.Core;

public enum SyncTomlMutationKind
{
    SetOverride,
    ClearOverride,
    RenameTable,
    RemoveTable,
}

public sealed class SyncTomlMutation
{
    private SyncTomlMutation(
        SyncTomlMutationKind kind,
        string path,
        string? renderedValue,
        string? destinationPath,
        bool containsSecret)
    {
        Kind = kind;
        Path = path;
        RenderedValue = renderedValue;
        DestinationPath = destinationPath;
        ContainsSecret = containsSecret;
    }

    public SyncTomlMutationKind Kind { get; }

    public string Path { get; }

    public string? DestinationPath { get; }

    public bool ContainsSecret { get; }

    internal string? RenderedValue { get; }

    internal static SyncTomlMutation Set(string path, string renderedValue, bool containsSecret = false) =>
        new(SyncTomlMutationKind.SetOverride, path, renderedValue, null, containsSecret);

    internal static SyncTomlMutation Clear(string path) =>
        new(SyncTomlMutationKind.ClearOverride, path, null, null, false);

    internal static SyncTomlMutation Rename(string path, string destinationPath) =>
        new(SyncTomlMutationKind.RenameTable, path, null, destinationPath, false);

    internal static SyncTomlMutation Remove(string path) =>
        new(SyncTomlMutationKind.RemoveTable, path, null, null, false);

    public override string ToString() =>
        Kind switch
        {
            SyncTomlMutationKind.RenameTable => $"{Kind}: {Path} -> {DestinationPath}",
            SyncTomlMutationKind.SetOverride when ContainsSecret => $"{Kind}: {Path} [secret]",
            _ => $"{Kind}: {Path}",
        };
}

public sealed record SyncTopologyPersistencePlan(
    bool IsValid,
    IReadOnlyList<SyncTomlMutation> Mutations,
    IReadOnlyList<SyncTopologyDiagnostic> Diagnostics)
{
    public SparseTomlEditResult Apply(byte[] baselineContents)
    {
        ArgumentNullException.ThrowIfNull(baselineContents);
        if (!IsValid)
        {
            return SparseTomlEditResult.Invalid(
                new(
                    SparseTomlErrorCode.InvalidValue,
                    "The sync topology contains persistence errors and was not applied."));
        }

        byte[] contents = [.. baselineContents];
        var changed = false;
        foreach (var mutation in Mutations)
        {
            var load = SparseTomlDocument.Load(contents, out var document);
            if (!load.IsValid || document is null)
            {
                return load;
            }

            var edit = mutation.Kind switch
            {
                SyncTomlMutationKind.SetOverride when mutation.RenderedValue is not null =>
                    document.SetOverride(mutation.Path, mutation.RenderedValue),
                SyncTomlMutationKind.ClearOverride => document.RemoveOverride(mutation.Path),
                SyncTomlMutationKind.RenameTable when mutation.DestinationPath is not null =>
                    document.RenameTable(mutation.Path, mutation.DestinationPath),
                SyncTomlMutationKind.RemoveTable => document.RemoveTable(mutation.Path),
                _ => SparseTomlEditResult.Invalid(
                    new(SparseTomlErrorCode.InvalidValue, $"Mutation '{mutation.Kind}' is incomplete.")),
            };
            if (!edit.IsValid || edit.Contents is null)
            {
                return edit;
            }

            contents = edit.Contents;
            changed |= edit.Changed;
        }

        return changed
            ? SparseTomlEditResult.Updated(contents)
            : SparseTomlEditResult.Unchanged(contents);
    }
}

public static class SyncTopologyPersistencePlanner
{
    public static SyncTopologyPersistencePlan Build(
        SyncTopologyTomlLoadResult baseline,
        SyncDesiredTopology desired,
        IReadOnlyDictionary<string, string>? renames = null,
        IReadOnlyDictionary<string, SyncTargetKindChangePolicy>? kindChangeDecisions = null,
        bool migrateLegacyRoot = false)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(desired);
        if (!baseline.IsValid || baseline.Topology is null)
        {
            return Invalid("SYNC_BASELINE_INVALID", "The baseline sync topology is invalid.");
        }

        var diagnostics = baseline.Diagnostics.Concat(desired.Resolve().Diagnostics).ToList();
        foreach (var target in desired.Targets.Values.Where(target =>
                     target.Kind != SyncTargetKind.LocalSidecar && !target.Enabled))
        {
            diagnostics.Add(Error(
                "SYNC_EXTERNAL_DISABLED_PERSISTENCE_REQUIRED",
                target.Name,
                "enabled",
                "External targets cannot be persisted as disabled; remove the target or keep it enabled."));
        }

        var renameMap = renames?.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var kindDecisions = kindChangeDecisions?.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, SyncTargetKindChangePolicy>(StringComparer.Ordinal);
        ValidateRenames(baseline.Topology, desired, renameMap, diagnostics);
        ValidateKindChanges(baseline.Topology, desired, renameMap, kindDecisions, diagnostics);
        if (diagnostics.Any(item => item.Severity == SyncTopologyDiagnosticSeverity.Error))
        {
            return new(false, [], diagnostics.AsReadOnly());
        }

        var mutations = new List<SyncTomlMutation>();
        AddGlobalChanges(mutations, baseline.Topology.GlobalDefaults, desired.GlobalDefaults);

        var baselineTargets = baseline.Topology.Targets;
        var consumedDesired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (baselineName, baselineTarget) in baselineTargets)
        {
            var desiredName = renameMap.GetValueOrDefault(baselineName, baselineName);
            var hasDesired = desired.Targets.TryGetValue(desiredName, out var desiredTarget);
            var isLegacyVirtual = baseline.HasLegacyRootTarget
                && string.Equals(baselineName, "default", StringComparison.Ordinal);

            if (!hasDesired)
            {
                if (isLegacyVirtual)
                {
                    mutations.Add(SyncTomlMutation.Clear("sync.url"));
                    mutations.Add(SyncTomlMutation.Clear("sync.token"));
                }
                else
                {
                    mutations.Add(SyncTomlMutation.Remove(TargetRoot(baselineTarget)));
                }

                continue;
            }

            consumedDesired.Add(desiredName);
            if (isLegacyVirtual)
            {
                var changed = !TargetsEquivalent(baselineTarget, desiredTarget!);
                var renamed = !string.Equals(baselineName, desiredName, StringComparison.Ordinal);
                if ((changed || renamed) && !migrateLegacyRoot)
                {
                    diagnostics.Add(Error(
                        "SYNC_LEGACY_MIGRATION_REQUIRED",
                        desiredName,
                        null,
                        "Editing or renaming the virtual root destination requires explicit migration confirmation."));
                    continue;
                }

                if (changed || renamed)
                {
                    mutations.Add(SyncTomlMutation.Clear("sync.url"));
                    mutations.Add(SyncTomlMutation.Clear("sync.token"));
                    AddTarget(mutations, desiredTarget!);
                }

                continue;
            }

            if (!string.Equals(baselineName, desiredName, StringComparison.Ordinal))
            {
                mutations.Add(SyncTomlMutation.Rename(TargetRoot(baselineTarget), TargetRoot(desiredTarget!)));
            }

            AddTargetChanges(mutations, baselineTarget, desiredTarget!);
        }

        foreach (var (name, target) in desired.Targets)
        {
            if (!consumedDesired.Contains(name))
            {
                AddTarget(mutations, target);
            }
        }

        var valid = diagnostics.All(item => item.Severity != SyncTopologyDiagnosticSeverity.Error);
        return new(valid, valid ? mutations.AsReadOnly() : [], diagnostics.AsReadOnly());
    }

    private static void AddGlobalChanges(
        List<SyncTomlMutation> mutations,
        SyncGlobalDefaults baseline,
        SyncGlobalDefaults desired)
    {
        AddChanged(mutations, "sync.proxy", baseline.Proxy, desired.Proxy);
        AddChanged(mutations, "sync.verify_ssl", baseline.VerifySsl, desired.VerifySsl);
        AddChanged(
            mutations,
            "sync.allow_unsafe_tls_without_certificate_validation",
            baseline.AllowUnsafeTlsWithoutCertificateValidation,
            desired.AllowUnsafeTlsWithoutCertificateValidation);
        foreach (var (kind, key) in SyncTopologyTomlAdapter.DataKeys)
        {
            AddChanged(mutations, $"sync.{key}", baseline.DataKinds[kind], desired.DataKinds[kind]);
        }
    }

    private static void AddTarget(List<SyncTomlMutation> mutations, SyncTargetDraft target)
    {
        var root = TargetRoot(target);
        if (target.Kind == SyncTargetKind.LocalSidecar)
        {
            mutations.Add(SyncTomlMutation.Set(root + ".enabled", Render(target.Enabled)));
            AddOverride(mutations, root + ".transport", target.LocalTransport, RenderLocalTransport);
            AddOverride(mutations, root + ".pipe_name", target.LocalPipeName, LauncherTomlValue.RenderString);
        }
        else
        {
            mutations.Add(SyncTomlMutation.Set(root + ".mode", LauncherTomlValue.RenderString(Mode(target.Kind))));
        }

        if (!target.UsesNamedPipe)
        {
            mutations.Add(SyncTomlMutation.Set(root + ".url", LauncherTomlValue.RenderString(target.Url)));
        }
        mutations.Add(SyncTomlMutation.Set(
            root + ".token",
            LauncherTomlValue.RenderString(target.Token.RevealForPersistence()),
            containsSecret: true));
        AddOverride(mutations, root + ".proxy", target.Proxy, LauncherTomlValue.RenderString);
        AddOverride(mutations, root + ".verify_ssl", target.VerifySsl, Render);
        AddOverride(
            mutations,
            root + ".allow_unsafe_tls_without_certificate_validation",
            target.AllowUnsafeTlsWithoutCertificateValidation,
            Render);
        foreach (var (kind, value) in target.DataOverrides)
        {
            AddOverride(mutations, $"{root}.{SyncTopologyTomlAdapter.DataKey(kind)}", value, Render);
        }

        if (target.Kind == SyncTargetKind.LocalSidecar)
        {
            AddOverride(mutations, root + ".battlelog_enrichment", target.BattlelogEnrichment, Render);
            AddOverride(mutations, root + ".fleet_runtime_mode", target.FleetRuntimeMode, LauncherTomlValue.RenderString);
        }
    }

    private static void AddTargetChanges(
        List<SyncTomlMutation> mutations,
        SyncTargetDraft baseline,
        SyncTargetDraft desired)
    {
        var root = TargetRoot(desired);
        if (baseline.Kind != desired.Kind)
        {
            mutations.Add(SyncTomlMutation.Set(root + ".mode", LauncherTomlValue.RenderString(Mode(desired.Kind))));
        }

        if (desired.Kind == SyncTargetKind.LocalSidecar)
        {
            AddChanged(mutations, root + ".enabled", baseline.Enabled, desired.Enabled);
            AddOverrideChange(
                mutations,
                root + ".transport",
                baseline.LocalTransport,
                desired.LocalTransport,
                RenderLocalTransport);
            AddOverrideChange(
                mutations,
                root + ".pipe_name",
                baseline.LocalPipeName,
                desired.LocalPipeName,
                LauncherTomlValue.RenderString);
        }

        if (desired.UsesNamedPipe)
        {
            if (!baseline.UsesNamedPipe)
            {
                mutations.Add(SyncTomlMutation.Clear(root + ".url"));
            }
        }
        else
        {
            AddChanged(mutations, root + ".url", baseline.Url, desired.Url);
        }
        if (!baseline.Token.Equals(desired.Token))
        {
            mutations.Add(SyncTomlMutation.Set(
                root + ".token",
                LauncherTomlValue.RenderString(desired.Token.RevealForPersistence()),
                containsSecret: true));
        }

        AddOverrideChange(mutations, root + ".proxy", baseline.Proxy, desired.Proxy, LauncherTomlValue.RenderString);
        AddOverrideChange(mutations, root + ".verify_ssl", baseline.VerifySsl, desired.VerifySsl, Render);
        AddOverrideChange(
            mutations,
            root + ".allow_unsafe_tls_without_certificate_validation",
            baseline.AllowUnsafeTlsWithoutCertificateValidation,
            desired.AllowUnsafeTlsWithoutCertificateValidation,
            Render);
        foreach (var kind in baseline.DataOverrides.Keys.Union(desired.DataOverrides.Keys))
        {
            AddOverrideChange(
                mutations,
                $"{root}.{SyncTopologyTomlAdapter.DataKey(kind)}",
                baseline.DataOverrides.GetValueOrDefault(kind, SyncOverride.Inherited<bool>()),
                desired.DataOverrides.GetValueOrDefault(kind, SyncOverride.Inherited<bool>()),
                Render);
        }

        AddOverrideChange(
            mutations,
            root + ".battlelog_enrichment",
            baseline.BattlelogEnrichment,
            desired.BattlelogEnrichment,
            Render);
        AddOverrideChange(
            mutations,
            root + ".fleet_runtime_mode",
            baseline.FleetRuntimeMode,
            desired.FleetRuntimeMode,
            LauncherTomlValue.RenderString);
    }

    private static void AddChanged(List<SyncTomlMutation> mutations, string path, string baseline, string desired)
    {
        if (!string.Equals(baseline, desired, StringComparison.Ordinal))
        {
            mutations.Add(SyncTomlMutation.Set(path, LauncherTomlValue.RenderString(desired)));
        }
    }

    private static void AddChanged(List<SyncTomlMutation> mutations, string path, bool baseline, bool desired)
    {
        if (baseline != desired)
        {
            mutations.Add(SyncTomlMutation.Set(path, Render(desired)));
        }
    }

    private static void AddOverride<T>(
        List<SyncTomlMutation> mutations,
        string path,
        SyncOverride<T> value,
        Func<T, string> render)
    {
        if (value.IsExplicit)
        {
            mutations.Add(SyncTomlMutation.Set(path, render(value.Value)));
        }
    }

    private static void AddOverrideChange<T>(
        List<SyncTomlMutation> mutations,
        string path,
        SyncOverride<T> baseline,
        SyncOverride<T> desired,
        Func<T, string> render)
    {
        if (baseline.Equals(desired))
        {
            return;
        }

        mutations.Add(desired.IsExplicit
            ? SyncTomlMutation.Set(path, render(desired.Value))
            : SyncTomlMutation.Clear(path));
    }

    private static void ValidateRenames(
        SyncDesiredTopology baseline,
        SyncDesiredTopology desired,
        IReadOnlyDictionary<string, string> renames,
        List<SyncTopologyDiagnostic> diagnostics)
    {
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (source, destination) in renames)
        {
            if (!baseline.Targets.ContainsKey(source)
                || !desired.Targets.ContainsKey(destination)
                || !destinations.Add(destination))
            {
                diagnostics.Add(Error(
                    "SYNC_RENAME_INVALID",
                    source,
                    "identity",
                    "The target rename does not match the baseline and desired topology."));
            }
        }
    }

    private static void ValidateKindChanges(
        SyncDesiredTopology baseline,
        SyncDesiredTopology desired,
        IReadOnlyDictionary<string, string> renames,
        Dictionary<string, SyncTargetKindChangePolicy> decisions,
        List<SyncTopologyDiagnostic> diagnostics)
    {
        foreach (var (baselineName, baselineTarget) in baseline.Targets)
        {
            var desiredName = renames.GetValueOrDefault(baselineName, baselineName);
            if (!desired.Targets.TryGetValue(desiredName, out var desiredTarget)
                || baselineTarget.Kind == desiredTarget.Kind)
            {
                continue;
            }

            if (!decisions.TryGetValue(desiredName, out var decision))
            {
                diagnostics.Add(Error(
                    "SYNC_KIND_CHANGE_DECISION_REQUIRED",
                    desiredName,
                    "kind",
                    "Changing target kind requires an explicit preserve-or-reset decision."));
                continue;
            }

            if (decision == SyncTargetKindChangePolicy.ResetOverrides && HasExplicitOverrides(desiredTarget))
            {
                diagnostics.Add(Error(
                    "SYNC_KIND_CHANGE_RESET_MISMATCH",
                    desiredName,
                    "kind",
                    "The reset decision requires all target overrides to use inherited values."));
            }
        }
    }

    private static bool HasExplicitOverrides(SyncTargetDraft target) =>
        target.Proxy.IsExplicit
        || target.VerifySsl.IsExplicit
        || target.AllowUnsafeTlsWithoutCertificateValidation.IsExplicit
        || target.BattlelogEnrichment.IsExplicit
        || target.FleetRuntimeMode.IsExplicit
        || target.LocalTransport.IsExplicit
        || target.LocalPipeName.IsExplicit
        || target.DataOverrides.Values.Any(value => value.IsExplicit);

    private static bool TargetsEquivalent(SyncTargetDraft left, SyncTargetDraft right) =>
        left.Kind == right.Kind
        && left.Enabled == right.Enabled
        && string.Equals(left.Url, right.Url, StringComparison.Ordinal)
        && left.Token.Equals(right.Token)
        && left.Proxy.Equals(right.Proxy)
        && left.VerifySsl.Equals(right.VerifySsl)
        && left.AllowUnsafeTlsWithoutCertificateValidation.Equals(right.AllowUnsafeTlsWithoutCertificateValidation)
        && left.BattlelogEnrichment.Equals(right.BattlelogEnrichment)
        && left.FleetRuntimeMode.Equals(right.FleetRuntimeMode)
        && left.LocalTransport.Equals(right.LocalTransport)
        && left.LocalPipeName.Equals(right.LocalPipeName)
        && left.DataOverrides.Count == right.DataOverrides.Count
        && left.DataOverrides.All(item => right.DataOverrides.TryGetValue(item.Key, out var value) && value.Equals(item.Value));

    private static string TargetRoot(SyncTargetDraft target) =>
        target.Kind == SyncTargetKind.LocalSidecar
            ? "sidecar.sync"
            : $"sync.targets.{target.Name}";

    private static string Mode(SyncTargetKind kind) =>
        kind == SyncTargetKind.MajelIngest ? "majel" : "legacy";

    private static string Render(bool value) => value ? "true" : "false";

    private static string RenderLocalTransport(SyncLocalTransport value) =>
        LauncherTomlValue.RenderString(value switch
        {
            SyncLocalTransport.LegacyHttp => "legacy_http",
            SyncLocalTransport.NamedPipe => "named_pipe",
            _ => throw new InvalidOperationException("The local transport is unsupported."),
        });

    private static SyncTopologyPersistencePlan Invalid(string code, string message) =>
        new(false, [], [Error(code, null, null, message)]);

    private static SyncTopologyDiagnostic Error(string code, string? targetName, string? field, string message) =>
        new(code, SyncTopologyDiagnosticSeverity.Error, message, targetName, field);
}

public sealed record SyncTopologyPersistenceCommitResult(
    AtomicTomlWriteState State,
    ConfigurationDocumentSnapshot? Snapshot = null,
    SyncTopologyPersistencePlan? Plan = null,
    string? BackupPath = null,
    SparseTomlError? ValidationError = null,
    string? Error = null,
    ConfigurationBackupReceipt? BackupReceipt = null);

public sealed class SyncTopologyEditSession
{
    private readonly AtomicTomlStore store;
    private SyncTopologyTomlLoadResult baseline;
    private ConfigurationDocumentSnapshot snapshot;
    private IReadOnlyDictionary<string, string> renames = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.Ordinal));
    private IReadOnlyDictionary<string, SyncTargetKindChangePolicy> kindChangeDecisions =
        new ReadOnlyDictionary<string, SyncTargetKindChangePolicy>(
            new Dictionary<string, SyncTargetKindChangePolicy>(StringComparer.Ordinal));

    private SyncTopologyEditSession(
        ConfigurationDocumentSnapshot snapshot,
        SyncTopologyTomlLoadResult baseline,
        AtomicTomlStore store)
    {
        this.snapshot = snapshot;
        this.baseline = baseline;
        this.store = store;
        Desired = baseline.Topology!;
    }

    public SyncDesiredTopology Desired { get; private set; }

    public bool HasPendingChanges { get; private set; }

    public bool IsStale { get; private set; }

    public ConfigurationDocumentRevision BaselineRevision => snapshot.Revision;

    public bool HasLegacyRootTarget => baseline.HasLegacyRootTarget;

    public SyncTopologyPersistencePlan PreparePlan(bool migrateLegacyRoot = false) =>
        SyncTopologyPersistencePlanner.Build(
            baseline,
            Desired,
            renames,
            kindChangeDecisions,
            migrateLegacyRoot);

    public static SyncTopologyTomlLoadResult Load(
        ConfigurationDocumentSnapshot snapshot,
        out SyncTopologyEditSession? workspace,
        AtomicTomlStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var load = SyncTopologyTomlAdapter.Load(snapshot.Contents);
        workspace = load.IsValid && load.Topology is not null
            ? new(snapshot, load, store ?? new AtomicTomlStore())
            : null;
        return load;
    }

    public void Stage(
        SyncDesiredTopology desired,
        IReadOnlyDictionary<string, string>? targetRenames = null,
        IReadOnlyDictionary<string, SyncTargetKindChangePolicy>? targetKindChangeDecisions = null)
    {
        Desired = desired ?? throw new ArgumentNullException(nameof(desired));
        renames = new ReadOnlyDictionary<string, string>(
            targetRenames?.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal));
        kindChangeDecisions = new ReadOnlyDictionary<string, SyncTargetKindChangePolicy>(
            targetKindChangeDecisions?.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, SyncTargetKindChangePolicy>(StringComparer.Ordinal));
        var plan = SyncTopologyPersistencePlanner.Build(
            baseline,
            Desired,
            renames,
            kindChangeDecisions);
        HasPendingChanges = !plan.IsValid || plan.Mutations.Count > 0;
    }

    public void Discard()
    {
        Desired = baseline.Topology!;
        renames = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
        kindChangeDecisions = new ReadOnlyDictionary<string, SyncTargetKindChangePolicy>(
            new Dictionary<string, SyncTargetKindChangePolicy>(StringComparer.Ordinal));
        HasPendingChanges = false;
    }

    public async Task<SyncTopologyPersistenceCommitResult> CommitAsync(
        bool migrateLegacyRoot = false,
        CancellationToken cancellationToken = default)
    {
        var plan = PreparePlan(migrateLegacyRoot);
        if (!plan.IsValid)
        {
            return new(AtomicTomlWriteState.Invalid, Plan: plan);
        }

        var edit = plan.Apply(snapshot.Contents);
        if (!edit.IsValid || edit.Contents is null)
        {
            return new(AtomicTomlWriteState.Invalid, Plan: plan, ValidationError: edit.Error);
        }

        var write = await store.SaveDocumentAsync(
            snapshot.Path,
            snapshot.Contents,
            edit.Contents,
            cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess)
        {
            if (write.State == AtomicTomlWriteState.Conflict)
            {
                IsStale = true;
            }

            return new(
                write.State,
                Plan: plan,
                BackupPath: write.BackupPath,
                ValidationError: write.ValidationError,
                Error: write.Error,
                BackupReceipt: write.BackupReceipt);
        }

        snapshot = new ConfigurationDocumentSnapshot(snapshot.Path, edit.Contents);
        baseline = SyncTopologyTomlAdapter.Load(edit.Contents);
        Desired = baseline.Topology!;
        renames = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
        kindChangeDecisions = new ReadOnlyDictionary<string, SyncTargetKindChangePolicy>(
            new Dictionary<string, SyncTargetKindChangePolicy>(StringComparer.Ordinal));
        HasPendingChanges = false;
        IsStale = false;
        return new(
            write.State,
            snapshot,
            plan,
            write.BackupPath,
            BackupReceipt: write.BackupReceipt);
    }

    internal SyncTopologyTomlLoadResult AcceptCommittedSnapshot(ConfigurationDocumentSnapshot committedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(committedSnapshot);
        var load = SyncTopologyTomlAdapter.Load(committedSnapshot.Contents);
        if (!load.IsValid || load.Topology is null)
        {
            return load;
        }

        snapshot = committedSnapshot;
        baseline = load;
        Desired = load.Topology;
        renames = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
        kindChangeDecisions = new ReadOnlyDictionary<string, SyncTargetKindChangePolicy>(
            new Dictionary<string, SyncTargetKindChangePolicy>(StringComparer.Ordinal));
        HasPendingChanges = false;
        IsStale = false;
        return load;
    }

    internal void MarkStale() => IsStale = true;
}

// Compatibility entry point for callers compiled against the original spike name. New code should
// depend on SyncTopologyEditSession: it owns the complete staged Data Sync transaction.
public static class SyncTopologyPersistenceWorkspace
{
    public static SyncTopologyTomlLoadResult Load(
        ConfigurationDocumentSnapshot snapshot,
        out SyncTopologyEditSession? workspace,
        AtomicTomlStore? store = null) =>
        SyncTopologyEditSession.Load(snapshot, out workspace, store);
}
