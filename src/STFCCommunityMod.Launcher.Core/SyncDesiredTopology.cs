using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core;

public sealed class SyncDesiredTopology
{
    public const string LocalSidecarIdentity = "local-sidecar";

    private static readonly Regex TargetNamePattern = new(
        "^[A-Za-z0-9_-]+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly ReadOnlyDictionary<string, SyncTargetDraft> targets;

    public SyncDesiredTopology(
        SyncGlobalDefaults globalDefaults,
        IReadOnlyDictionary<string, SyncTargetDraft>? targets = null)
    {
        GlobalDefaults = globalDefaults ?? throw new ArgumentNullException(nameof(globalDefaults));
        this.targets = new(
            targets?.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal) ??
            new Dictionary<string, SyncTargetDraft>(StringComparer.Ordinal));
    }

    public SyncGlobalDefaults GlobalDefaults { get; }

    public IReadOnlyDictionary<string, SyncTargetDraft> Targets => targets;

    public static SyncDesiredTopology Empty { get; } =
        new(new SyncGlobalDefaults(string.Empty, true, false));

    public SyncDesiredTopology WithGlobalDefaults(SyncGlobalDefaults value) => new(value, targets);

    public SyncTopologyTransitionResult AddTarget(string name, SyncTargetKind kind)
    {
        var identity = kind == SyncTargetKind.LocalSidecar ? LocalSidecarIdentity : name;
        var nameDiagnostic = ValidateIdentity(identity, kind);
        if (nameDiagnostic is not null)
        {
            return Failed(nameDiagnostic);
        }

        if (targets.ContainsKey(identity))
        {
            return Failed(Error("SYNC_TARGET_NAME_DUPLICATE", identity, "identity", "A sync target already uses this name."));
        }

        var definition = SyncTargetTypeCatalog.Get(kind);
        if (targets.Values.Count(target => target.Kind == kind) >= definition.MaximumInstances)
        {
            return Failed(Error("SYNC_TARGET_CARDINALITY", identity, "kind", "This target kind has reached its instance limit."));
        }

        var changed = CopyTargets();
        changed.Add(identity, SyncTargetDraft.Create(identity, kind));
        return Succeeded(new(GlobalDefaults, changed));
    }

    public SyncTopologyTransitionResult AddPreset(string presetId)
    {
        var preset = SyncTargetTypeCatalog.GetPreset(presetId);
        return AddTarget(preset.SuggestedIdentity, preset.TargetKind);
    }

    public SyncTopologyTransitionResult UpdateTarget(
        string name,
        Func<SyncTargetDraft, SyncTargetDraft> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!targets.TryGetValue(name, out var current))
        {
            return Failed(Error("SYNC_TARGET_NOT_FOUND", name, null, "The sync target no longer exists."));
        }

        var updated = update(current);
        if (!string.Equals(updated.Name, current.Name, StringComparison.Ordinal))
        {
            return Failed(Error(
                "SYNC_TARGET_IDENTITY_TRANSITION_REQUIRED",
                name,
                "identity",
                "Use the rename transition to change a target identity."));
        }

        var changed = CopyTargets();
        changed[name] = updated;
        return Succeeded(new(GlobalDefaults, changed));
    }

    public SyncTopologyTransitionResult RenameTarget(string currentName, string newName)
    {
        if (!targets.TryGetValue(currentName, out var target))
        {
            return Failed(Error("SYNC_TARGET_NOT_FOUND", currentName, null, "The sync target no longer exists."));
        }

        if (target.Kind == SyncTargetKind.LocalSidecar)
        {
            return Failed(Error(
                "SYNC_SIDECAR_IDENTITY_FIXED",
                currentName,
                "identity",
                "The local Sidecar identity is fixed."));
        }

        var nameDiagnostic = ValidateIdentity(newName, target.Kind);
        if (nameDiagnostic is not null)
        {
            return Failed(nameDiagnostic);
        }

        if (targets.ContainsKey(newName))
        {
            return Failed(Error("SYNC_TARGET_NAME_DUPLICATE", newName, "identity", "A sync target already uses this name."));
        }

        var changed = CopyTargets();
        changed.Remove(currentName);
        changed.Add(newName, target.WithIdentity(newName));
        return Succeeded(new(GlobalDefaults, changed));
    }

    public SyncTopologyTransitionResult DuplicateTarget(string sourceName, string newName)
    {
        if (!targets.TryGetValue(sourceName, out var source))
        {
            return Failed(Error("SYNC_TARGET_NOT_FOUND", sourceName, null, "The sync target no longer exists."));
        }

        if (source.Kind == SyncTargetKind.LocalSidecar)
        {
            return Failed(Error(
                "SYNC_TARGET_CARDINALITY",
                sourceName,
                "kind",
                "The singleton local Sidecar target cannot be duplicated."));
        }

        var nameDiagnostic = ValidateIdentity(newName, source.Kind);
        if (nameDiagnostic is not null)
        {
            return Failed(nameDiagnostic);
        }

        if (targets.ContainsKey(newName))
        {
            return Failed(Error("SYNC_TARGET_NAME_DUPLICATE", newName, "identity", "A sync target already uses this name."));
        }

        var duplicate = source
            .WithIdentity(newName)
            .WithEnabled(false)
            .WithoutSecret();
        var changed = CopyTargets();
        changed.Add(newName, duplicate);
        return Succeeded(new(GlobalDefaults, changed));
    }

    public SyncTopologyTransitionResult ChangeTargetKind(
        string name,
        SyncTargetKind newKind,
        SyncTargetKindChangePolicy policy = SyncTargetKindChangePolicy.PreserveCompatibleOverrides)
    {
        if (!targets.TryGetValue(name, out var target))
        {
            return Failed(Error("SYNC_TARGET_NOT_FOUND", name, null, "The sync target no longer exists."));
        }

        if (target.Kind == newKind)
        {
            return Succeeded(this);
        }

        if (target.Kind == SyncTargetKind.LocalSidecar || newKind == SyncTargetKind.LocalSidecar)
        {
            return Failed(Error(
                "SYNC_KIND_CHANGE_UNSUPPORTED",
                name,
                "kind",
                "Local Sidecar cannot be converted to or from an external target."));
        }

        var supported = SyncTargetTypeCatalog.Get(newKind).SupportedDataKinds;
        var changedTarget = target.WithKind(newKind);
        if (policy == SyncTargetKindChangePolicy.ResetOverrides)
        {
            changedTarget = changedTarget.WithoutOverrides();
        }

        foreach (var unsupported in changedTarget.DataOverrides.Keys.Where(kind => !supported.Contains(kind)).ToArray())
        {
            changedTarget = changedTarget.WithDataOverride(unsupported, SyncOverride.Inherited<bool>());
        }

        var changed = CopyTargets();
        changed[name] = changedTarget;
        return Succeeded(new(GlobalDefaults, changed));
    }

    public SyncTopologyTransitionResult SetTargetEnabled(string name, bool enabled) =>
        UpdateTarget(name, target => target.WithEnabled(enabled));

    public SyncTopologyTransitionResult RemoveTarget(string name)
    {
        if (!targets.ContainsKey(name))
        {
            return Failed(Error("SYNC_TARGET_NOT_FOUND", name, null, "The sync target no longer exists."));
        }

        var changed = CopyTargets();
        changed.Remove(name);
        return Succeeded(new(GlobalDefaults, changed));
    }

    public SyncResolvedTopology Resolve() => SyncTopologyResolver.Resolve(this);

    internal static SyncTopologyDiagnostic? ValidateIdentity(string name, SyncTargetKind kind)
    {
        if (kind == SyncTargetKind.LocalSidecar)
        {
            return string.Equals(name, LocalSidecarIdentity, StringComparison.Ordinal)
                ? null
                : Error("SYNC_SIDECAR_IDENTITY_FIXED", name, "identity", "The local Sidecar identity is fixed.");
        }

        if (name.Length is < 1 or > 64 || !TargetNamePattern.IsMatch(name))
        {
            return Error(
                "SYNC_TARGET_NAME_INVALID",
                name,
                "identity",
                "Target names must contain 1-64 ASCII letters, numbers, underscores, or hyphens.");
        }

        return string.Equals(name, "sidecar", StringComparison.OrdinalIgnoreCase)
            ? Error(
                "SYNC_TARGET_SIDECAR_NAMESPACE_INVALID",
                name,
                "identity",
                "External targets cannot use the reserved Sidecar identity.")
            : null;
    }

    private Dictionary<string, SyncTargetDraft> CopyTargets() =>
        targets.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    private SyncTopologyTransitionResult Failed(SyncTopologyDiagnostic diagnostic) => new(false, this, diagnostic);

    private static SyncTopologyTransitionResult Succeeded(SyncDesiredTopology topology) => new(true, topology);

    internal static SyncTopologyDiagnostic Error(string code, string? targetName, string? field, string message) =>
        new(code, SyncTopologyDiagnosticSeverity.Error, message, targetName, field);
}

public static class SyncTopologyResolver
{
    public static SyncResolvedTopology Resolve(SyncDesiredTopology desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var diagnostics = new List<SyncTopologyDiagnostic>();
        var targets = new List<SyncResolvedTarget>();
        var invalidCardinality = desired.Targets.Values
            .GroupBy(target => target.Kind)
            .Where(group => group.Count() > SyncTargetTypeCatalog.Get(group.Key).MaximumInstances)
            .Select(group => group.Key)
            .ToHashSet();
        foreach (var kind in invalidCardinality)
        {
            diagnostics.Add(SyncDesiredTopology.Error(
                "SYNC_TARGET_CARDINALITY",
                null,
                "kind",
                $"Target kind '{kind}' exceeds its instance limit."));
        }

        foreach (var target in desired.Targets.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (invalidCardinality.Contains(target.Kind))
            {
                continue;
            }

            var targetDiagnostics = ValidateTarget(desired.GlobalDefaults, target);
            diagnostics.AddRange(targetDiagnostics);
            if (targetDiagnostics.Any(item => item.Severity == SyncTopologyDiagnosticSeverity.Error))
            {
                continue;
            }

            targets.Add(ResolveTarget(desired.GlobalDefaults, target));
        }

        return new(targets.AsReadOnly(), diagnostics.AsReadOnly());
    }

    private static List<SyncTopologyDiagnostic> ValidateTarget(
        SyncGlobalDefaults globals,
        SyncTargetDraft target)
    {
        var diagnostics = new List<SyncTopologyDiagnostic>();
        var identity = SyncDesiredTopology.ValidateIdentity(target.Name, target.Kind);
        if (identity is not null)
        {
            diagnostics.Add(identity);
        }

        var definition = SyncTargetTypeCatalog.Get(target.Kind);
        if (target.Enabled
            && definition.RequiresUrl
            && !TryValidateEndpoint(target.Url, target.Kind, out var endpointCode, out var endpointMessage))
        {
            diagnostics.Add(SyncDesiredTopology.Error(endpointCode, target.Name, "url", endpointMessage));
        }

        if (target.Enabled && definition.RequiresToken && !target.Token.IsConfigured)
        {
            diagnostics.Add(SyncDesiredTopology.Error(
                "SYNC_CREDENTIALS_INCOMPLETE",
                target.Name,
                "token",
                "This target requires a configured token."));
        }

        foreach (var (kind, value) in target.DataOverrides)
        {
            if (value.IsExplicit && !definition.SupportedDataKinds.Contains(kind))
            {
                diagnostics.Add(SyncDesiredTopology.Error(
                    "SYNC_CAPABILITY_UNSUPPORTED",
                    target.Name,
                    kind.ToString(),
                    "This data capability is not supported by the selected target kind."));
            }
        }

        if (target.BattlelogEnrichment.IsExplicit && !definition.SupportsBattlelogEnrichment)
        {
            diagnostics.Add(SyncDesiredTopology.Error(
                "SYNC_CAPABILITY_UNSUPPORTED",
                target.Name,
                "battlelog_enrichment",
                "Battle-log enrichment is supported only by the local Sidecar target."));
        }

        if (target.FleetRuntimeMode.IsExplicit && !definition.SupportsFleetRuntimeMode)
        {
            diagnostics.Add(SyncDesiredTopology.Error(
                "SYNC_CAPABILITY_UNSUPPORTED",
                target.Name,
                "fleet_runtime_mode",
                "Fleet runtime mode is supported only by the local Sidecar target."));
        }
        else if (target.FleetRuntimeMode.IsExplicit && !IsFleetRuntimeModeSupported(target.FleetRuntimeMode.Value))
        {
            diagnostics.Add(SyncDesiredTopology.Error(
                "SYNC_FLEET_RUNTIME_MODE_INVALID",
                target.Name,
                "fleet_runtime_mode",
                "Fleet runtime mode must be normal, request_only, snapshot_only, or enqueue_no_transport."));
        }

        var verifySsl = ResolveVerifySsl(target, definition, globals).Value;
        var unsafeTls = ResolveUnsafeTls(target, definition, globals).Value;
        if (!verifySsl && !unsafeTls)
        {
            diagnostics.Add(SyncDesiredTopology.Error(
                "SYNC_UNSAFE_TLS_PAIR_REQUIRED",
                target.Name,
                "verify_ssl",
                "Disabling TLS verification requires the explicit unsafe-TLS acknowledgement."));
        }

        return diagnostics;
    }

    private static SyncResolvedTarget ResolveTarget(SyncGlobalDefaults globals, SyncTargetDraft target)
    {
        var definition = SyncTargetTypeCatalog.Get(target.Kind);
        var dataKinds = new ReadOnlyDictionary<SyncDataKind, SyncResolvedValue<bool>>(
            definition.SupportedDataKinds.ToDictionary(
                kind => kind,
                kind => ResolveDataKind(globals, target, definition, kind)));
        return new(
            target.Name,
            target.Kind,
            target.Enabled,
            target.Url,
            target.Token.IsConfigured,
            ResolveProxy(globals, target, definition),
            ResolveVerifySsl(target, definition, globals),
            ResolveUnsafeTls(target, definition, globals),
            dataKinds,
            definition.SupportsBattlelogEnrichment
                ? target.BattlelogEnrichment.Resolve(false, SyncValueSource.TargetTypeDefault)
                : null,
            definition.SupportsFleetRuntimeMode
                ? target.FleetRuntimeMode.Resolve("normal", SyncValueSource.TargetTypeDefault)
                : null);
    }

    private static SyncResolvedValue<string> ResolveProxy(
        SyncGlobalDefaults globals,
        SyncTargetDraft target,
        SyncTargetTypeDefinition definition) =>
        definition.InheritsGlobalSync
            ? target.Proxy.Resolve(globals.Proxy, SyncValueSource.GlobalDefault)
            : target.Proxy.Resolve(string.Empty, SyncValueSource.TargetTypeDefault);

    private static SyncResolvedValue<bool> ResolveVerifySsl(
        SyncTargetDraft target,
        SyncTargetTypeDefinition definition,
        SyncGlobalDefaults? globals = null) =>
        definition.InheritsGlobalSync
            ? target.VerifySsl.Resolve(globals?.VerifySsl ?? true, SyncValueSource.GlobalDefault)
            : target.VerifySsl.Resolve(true, SyncValueSource.TargetTypeDefault);

    private static SyncResolvedValue<bool> ResolveUnsafeTls(
        SyncTargetDraft target,
        SyncTargetTypeDefinition definition,
        SyncGlobalDefaults? globals = null) =>
        definition.InheritsGlobalSync
            ? target.AllowUnsafeTlsWithoutCertificateValidation.Resolve(
                globals?.AllowUnsafeTlsWithoutCertificateValidation ?? false,
                SyncValueSource.GlobalDefault)
            : target.AllowUnsafeTlsWithoutCertificateValidation.Resolve(false, SyncValueSource.TargetTypeDefault);

    private static SyncResolvedValue<bool> ResolveDataKind(
        SyncGlobalDefaults globals,
        SyncTargetDraft target,
        SyncTargetTypeDefinition definition,
        SyncDataKind kind)
    {
        var value = target.DataOverrides.TryGetValue(kind, out var configured)
            ? configured
            : SyncOverride.Inherited<bool>();
        return definition.InheritsGlobalSync
            ? value.Resolve(globals.DataKinds[kind], SyncValueSource.GlobalDefault)
            : value.Resolve(false, SyncValueSource.TargetTypeDefault);
    }

    private static bool TryValidateEndpoint(
        string value,
        SyncTargetKind kind,
        out string code,
        out string message)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host))
        {
            code = "SYNC_ENDPOINT_INVALID";
            message = "The target endpoint must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            code = "SYNC_ENDPOINT_EMBEDDED_CREDENTIALS";
            message = "The target endpoint cannot contain embedded credentials; use the token field.";
            return false;
        }

        if (kind == SyncTargetKind.LocalSidecar && !uri.IsLoopback)
        {
            code = "SYNC_SIDECAR_ENDPOINT_NOT_LOOPBACK";
            message = "The local Sidecar endpoint must use a loopback host.";
            return false;
        }

        if (kind != SyncTargetKind.LocalSidecar && uri.IsLoopback)
        {
            code = "SYNC_LOOPBACK_TARGET_INVALID";
            message = "Loopback Sidecar endpoints belong to the local Sidecar target.";
            return false;
        }

        code = string.Empty;
        message = string.Empty;
        return true;
    }

    private static bool IsFleetRuntimeModeSupported(string value) =>
        value is "normal" or "request_only" or "snapshot_only" or "enqueue_no_transport";
}

public sealed record SyncTopologyWorkspace(
    SyncDesiredTopology Desired,
    SyncResolvedTopology StartupRuntime)
{
    public static SyncTopologyWorkspace Begin(SyncDesiredTopology desired) => new(desired, desired.Resolve());

    public SyncTopologyWorkspace Apply(SyncTopologyTransitionResult transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return transition.Succeeded ? new(transition.Topology, StartupRuntime) : this;
    }

    public SyncResolvedTopology Preview => Desired.Resolve();
}
