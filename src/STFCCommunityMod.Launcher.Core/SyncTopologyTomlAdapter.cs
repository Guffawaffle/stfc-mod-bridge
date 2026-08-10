using System.Collections.ObjectModel;

namespace STFCCommunityMod.Launcher.Core;

public sealed record SyncTopologyTomlLoadResult(
    bool IsValid,
    SyncDesiredTopology? Topology,
    IReadOnlyList<SyncTopologyDiagnostic> Diagnostics,
    bool HasLegacyRootTarget,
    SparseTomlError? Error = null);

public static class SyncTopologyTomlAdapter
{
    private static readonly ReadOnlyDictionary<SyncDataKind, string> DataKindKeys =
        new(
            new Dictionary<SyncDataKind, string>
            {
                [SyncDataKind.Battlelogs] = "battlelogs",
                [SyncDataKind.BattlelogsRealtime] = "battlelogs_realtime",
                [SyncDataKind.Buffs] = "buffs",
                [SyncDataKind.Buildings] = "buildings",
                [SyncDataKind.Inventory] = "inventory",
                [SyncDataKind.Jobs] = "jobs",
                [SyncDataKind.Missions] = "missions",
                [SyncDataKind.Officer] = "officer",
                [SyncDataKind.Research] = "research",
                [SyncDataKind.Resources] = "resources",
                [SyncDataKind.Ships] = "ships",
                [SyncDataKind.Slots] = "slots",
                [SyncDataKind.Tech] = "tech",
                [SyncDataKind.Traits] = "traits",
                [SyncDataKind.FleetRuntime] = "fleet_runtime",
            });

    public static SyncGlobalDefaults NativeGlobalDefaults { get; } =
        new SyncGlobalDefaults(
            string.Empty,
            true,
            false,
            new Dictionary<SyncDataKind, bool>
            {
                [SyncDataKind.Battlelogs] = true,
                [SyncDataKind.BattlelogsRealtime] = false,
                [SyncDataKind.Buffs] = true,
                [SyncDataKind.Buildings] = true,
                [SyncDataKind.FleetRuntime] = false,
                [SyncDataKind.Inventory] = true,
                [SyncDataKind.Jobs] = true,
                [SyncDataKind.Missions] = true,
                [SyncDataKind.Officer] = true,
                [SyncDataKind.Research] = true,
                [SyncDataKind.Resources] = true,
                [SyncDataKind.Ships] = true,
                [SyncDataKind.Slots] = true,
                [SyncDataKind.Tech] = true,
                [SyncDataKind.Traits] = true,
            });

    public static IReadOnlyDictionary<SyncDataKind, string> DataKeys => DataKindKeys;

    public static SyncTopologyTomlLoadResult Load(
        byte[] contents,
        SyncGlobalDefaults? nativeDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var load = SparseTomlDocument.Load(contents, out var document);
        if (!load.IsValid || document is null)
        {
            return Invalid(load.Error);
        }

        var read = document.ReadOverrides();
        if (!read.IsValid || read.Overrides is null)
        {
            return Invalid(read.Error);
        }

        var diagnostics = new List<SyncTopologyDiagnostic>();
        var overrides = read.Overrides;
        var globals = ReadGlobals(overrides, nativeDefaults ?? NativeGlobalDefaults, diagnostics);
        var targets = new Dictionary<string, SyncTargetDraft>(StringComparer.Ordinal);

        ReadSidecar(overrides, targets, diagnostics);
        ReadExternalTargets(overrides, read.Tables ?? [], targets, diagnostics);
        var hasLegacyRootTarget = ReadLegacyRootTarget(overrides, targets, diagnostics);

        return new(
            true,
            new SyncDesiredTopology(globals, targets),
            diagnostics.AsReadOnly(),
            hasLegacyRootTarget);
    }

    internal static string DataKey(SyncDataKind kind) => DataKindKeys[kind];

    private static SyncGlobalDefaults ReadGlobals(
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        SyncGlobalDefaults defaults,
        List<SyncTopologyDiagnostic> diagnostics)
    {
        var proxy = ReadString(overrides, "sync.proxy", defaults.Proxy, diagnostics, null);
        var verifySsl = ReadBoolean(overrides, "sync.verify_ssl", defaults.VerifySsl, diagnostics, null);
        var unsafeTls = ReadBoolean(
            overrides,
            "sync.allow_unsafe_tls_without_certificate_validation",
            defaults.AllowUnsafeTlsWithoutCertificateValidation,
            diagnostics,
            null);
        var dataKinds = defaults.DataKinds.ToDictionary(item => item.Key, item => item.Value);
        foreach (var (kind, key) in DataKindKeys)
        {
            dataKinds[kind] = ReadBoolean(overrides, $"sync.{key}", dataKinds[kind], diagnostics, null);
        }

        return new(proxy, verifySsl, unsafeTls, dataKinds);
    }

    private static void ReadSidecar(
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        Dictionary<string, SyncTargetDraft> targets,
        List<SyncTopologyDiagnostic> diagnostics)
    {
        const string prefix = "sidecar.sync.";
        if (!overrides.Keys.Any(path => path.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return;
        }

        var target = SyncTargetDraft.Create(
            SyncDesiredTopology.LocalSidecarIdentity,
            SyncTargetKind.LocalSidecar);
        var url = ReadString(overrides, prefix + "url", target.Url, diagnostics, target.Name);
        var token = ReadSecret(overrides, prefix + "token", diagnostics, target.Name);
        target = target
            .WithEnabled(ReadBoolean(overrides, prefix + "enabled", false, diagnostics, target.Name))
            .WithConnection(url, token)
            .WithLocalTransport(
                ReadLocalTransportOverride(overrides, prefix + "transport", diagnostics, target.Name),
                ReadStringOverride(overrides, prefix + "pipe_name", diagnostics, target.Name))
            .WithProxy(ReadStringOverride(overrides, prefix + "proxy", diagnostics, target.Name))
            .WithVerifySsl(ReadBooleanOverride(overrides, prefix + "verify_ssl", diagnostics, target.Name))
            .WithUnsafeTls(ReadBooleanOverride(
                overrides,
                prefix + "allow_unsafe_tls_without_certificate_validation",
                diagnostics,
                target.Name))
            .WithDataOverride(
                SyncDataKind.BattlelogsRealtime,
                ReadBooleanOverride(overrides, prefix + "battlelogs_realtime", diagnostics, target.Name))
            .WithDataOverride(
                SyncDataKind.FleetRuntime,
                ReadBooleanOverride(overrides, prefix + "fleet_runtime", diagnostics, target.Name))
            .WithBattlelogEnrichment(
                ReadBooleanOverride(overrides, prefix + "battlelog_enrichment", diagnostics, target.Name))
            .WithFleetRuntimeMode(
                ReadStringOverride(overrides, prefix + "fleet_runtime_mode", diagnostics, target.Name));
        targets.Add(target.Name, target);
    }

    private static void ReadExternalTargets(
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        IReadOnlyList<SparseTomlTable> tables,
        Dictionary<string, SyncTargetDraft> targets,
        List<SyncTopologyDiagnostic> diagnostics)
    {
        var names = overrides.Keys
            .Where(path => path.StartsWith("sync.targets.", StringComparison.Ordinal))
            .Select(path => path.Split('.'))
            .Where(parts => parts.Length >= 4)
            .Select(parts => parts[2])
            .Concat(
                tables
                    .Select(table => table.CanonicalPath.Split('.'))
                    .Where(parts => parts.Length >= 3
                        && parts[0] == "sync"
                        && parts[1] == "targets")
                    .Select(parts => parts[2]))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);
        foreach (var name in names)
        {
            var prefix = $"sync.targets.{name}.";
            var mode = ReadString(overrides, prefix + "mode", "legacy", diagnostics, name);
            var kind = mode switch
            {
                "majel" => SyncTargetKind.MajelIngest,
                "legacy" or "" => SyncTargetKind.LegacyCommunity,
                _ => SyncTargetKind.LegacyCommunity,
            };
            if (mode == "sidecar_broker")
            {
                diagnostics.Add(new(
                    "SYNC_TARGET_SIDECAR_NAMESPACE_INVALID",
                    SyncTopologyDiagnosticSeverity.Error,
                    "Sidecar broker mode is invalid under external sync targets; use the local Sidecar target.",
                    name,
                    "mode"));
            }
            else if (mode is not ("legacy" or "majel" or ""))
            {
                diagnostics.Add(new(
                    "SYNC_TARGET_MODE_INVALID",
                    SyncTopologyDiagnosticSeverity.Warning,
                    "The target mode is unknown and resolves as ordinary sync until explicitly corrected.",
                    name,
                    "mode"));
            }

            var target = SyncTargetDraft.Create(name, kind)
                .WithEnabled(true)
                .WithConnection(
                    ReadString(overrides, prefix + "url", string.Empty, diagnostics, name),
                    ReadSecret(overrides, prefix + "token", diagnostics, name))
                .WithProxy(ReadStringOverride(overrides, prefix + "proxy", diagnostics, name))
                .WithVerifySsl(ReadBooleanOverride(overrides, prefix + "verify_ssl", diagnostics, name))
                .WithUnsafeTls(ReadBooleanOverride(
                    overrides,
                    prefix + "allow_unsafe_tls_without_certificate_validation",
                    diagnostics,
                    name));
            foreach (var (dataKind, key) in DataKindKeys)
            {
                target = target.WithDataOverride(
                    dataKind,
                    ReadBooleanOverride(overrides, prefix + key, diagnostics, name));
            }

            targets[name] = target;
        }
    }

    private static bool ReadLegacyRootTarget(
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        Dictionary<string, SyncTargetDraft> targets,
        List<SyncTopologyDiagnostic> diagnostics)
    {
        var hasUrl = TryReadString(overrides, "sync.url", out var url, diagnostics, null);
        var hasToken = TryReadString(overrides, "sync.token", out var token, diagnostics, null);
        if (!hasUrl && !hasToken)
        {
            return false;
        }

        if (!hasUrl || !hasToken || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
        {
            diagnostics.Add(new(
                "SYNC_LEGACY_ROOT_INCOMPLETE",
                SyncTopologyDiagnosticSeverity.Warning,
                "Older root sync credentials are incomplete and do not create a destination."));
            return false;
        }

        if (targets.ContainsKey("default"))
        {
            diagnostics.Add(new(
                "SYNC_LEGACY_ROOT_CONFLICT",
                SyncTopologyDiagnosticSeverity.Error,
                "Older root sync credentials conflict with the named default destination.",
                "default"));
            return false;
        }

        var target = SyncTargetDraft.Create("default", SyncTargetKind.LegacyCommunity)
            .WithEnabled(true)
            .WithConnection(url, SyncSecret.FromPlainText(token));
        targets.Add(target.Name, target);
        diagnostics.Add(new(
            "SYNC_LEGACY_ROOT_CONVERTED",
            SyncTopologyDiagnosticSeverity.Info,
            "Older root sync credentials are represented as a virtual default destination without changing the source.",
            "default"));
        return true;
    }

    private static SyncOverride<string> ReadStringOverride(
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        string path,
        List<SyncTopologyDiagnostic> diagnostics,
        string? targetName) =>
        TryReadString(overrides, path, out var value, diagnostics, targetName)
            ? SyncOverride.Explicit(value)
            : SyncOverride.Inherited<string>();

    private static SyncOverride<SyncLocalTransport> ReadLocalTransportOverride(
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        string path,
        List<SyncTopologyDiagnostic> diagnostics,
        string targetName)
    {
        if (!overrides.ContainsKey(path))
        {
            return SyncOverride.Inherited<SyncLocalTransport>();
        }
        if (!TryReadString(overrides, path, out var value, diagnostics, targetName))
        {
            return SyncOverride.Inherited<SyncLocalTransport>();
        }
        var transport = value switch
        {
            "legacy_http" => SyncLocalTransport.LegacyHttp,
            "named_pipe" => SyncLocalTransport.NamedPipe,
            _ => (SyncLocalTransport?)null,
        };
        if (transport is null)
        {
            diagnostics.Add(new(
                "SYNC_LOCAL_TRANSPORT_INVALID",
                SyncTopologyDiagnosticSeverity.Error,
                "Local transport must be exactly legacy_http or named_pipe.",
                targetName,
                "transport"));
            return SyncOverride.Inherited<SyncLocalTransport>();
        }
        return SyncOverride.Explicit(transport.Value);
    }

    private static SyncOverride<bool> ReadBooleanOverride(
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        string path,
        List<SyncTopologyDiagnostic> diagnostics,
        string? targetName) =>
        TryReadBoolean(overrides, path, out var value, diagnostics, targetName)
            ? SyncOverride.Explicit(value)
            : SyncOverride.Inherited<bool>();

    private static SyncSecret ReadSecret(
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        string path,
        List<SyncTopologyDiagnostic> diagnostics,
        string targetName) =>
        TryReadString(overrides, path, out var value, diagnostics, targetName)
            ? SyncSecret.FromPlainText(value)
            : SyncSecret.Missing;

    private static string ReadString(
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        string path,
        string fallback,
        List<SyncTopologyDiagnostic> diagnostics,
        string? targetName) =>
        TryReadString(overrides, path, out var value, diagnostics, targetName) ? value : fallback;

    private static bool ReadBoolean(
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        string path,
        bool fallback,
        List<SyncTopologyDiagnostic> diagnostics,
        string? targetName) =>
        TryReadBoolean(overrides, path, out var value, diagnostics, targetName) ? value : fallback;

    private static bool TryReadString(
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        string path,
        out string value,
        List<SyncTopologyDiagnostic> diagnostics,
        string? targetName)
    {
        value = string.Empty;
        if (!overrides.TryGetValue(path, out var configured))
        {
            return false;
        }

        if (LauncherTomlValue.TryReadString(configured.RenderedValue, out value))
        {
            return true;
        }

        diagnostics.Add(InvalidValue(path, configured.LineNumber, targetName));
        return false;
    }

    private static bool TryReadBoolean(
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        string path,
        out bool value,
        List<SyncTopologyDiagnostic> diagnostics,
        string? targetName)
    {
        value = false;
        if (!overrides.TryGetValue(path, out var configured))
        {
            return false;
        }

        if (configured.RenderedValue is "true" or "false")
        {
            value = configured.RenderedValue == "true";
            return true;
        }

        diagnostics.Add(InvalidValue(path, configured.LineNumber, targetName));
        return false;
    }

    private static SyncTopologyDiagnostic InvalidValue(string path, int lineNumber, string? targetName) =>
        new(
            "SYNC_TOML_VALUE_INVALID",
            SyncTopologyDiagnosticSeverity.Error,
            $"'{path}' has an invalid value at line {lineNumber}.",
            targetName,
            path[(path.LastIndexOf('.') + 1)..]);

    private static SyncTopologyTomlLoadResult Invalid(SparseTomlError? error) =>
        new(false, null, [], false, error);
}
