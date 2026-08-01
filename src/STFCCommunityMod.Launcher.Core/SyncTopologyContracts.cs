using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace STFCCommunityMod.Launcher.Core;

public enum SyncTargetKind
{
    LocalSidecar,
    LegacyCommunity,
    MajelIngest,
}

public enum SyncDataKind
{
    Battlelogs,
    BattlelogsRealtime,
    Buffs,
    Buildings,
    Inventory,
    Jobs,
    Missions,
    Officer,
    Research,
    Resources,
    Ships,
    Slots,
    Tech,
    Traits,
    FleetRuntime,
}

public enum SyncValueProvenance
{
    Inherited,
    ExplicitValue,
    ExplicitFalse,
    ExplicitEmpty,
}

public enum SyncValueSource
{
    GlobalDefault,
    TargetTypeDefault,
    Target,
}

public enum SyncTopologyDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public readonly record struct SyncOverride<T>(bool IsExplicit, T Value)
{
    public SyncResolvedValue<T> Resolve(T inheritedValue, SyncValueSource inheritedSource)
    {
        if (!IsExplicit)
        {
            return new(inheritedValue, SyncValueProvenance.Inherited, inheritedSource);
        }

        var provenance = Value switch
        {
            false => SyncValueProvenance.ExplicitFalse,
            string { Length: 0 } => SyncValueProvenance.ExplicitEmpty,
            _ => SyncValueProvenance.ExplicitValue,
        };
        return new(Value, provenance, SyncValueSource.Target);
    }
}

public static class SyncOverride
{
    public static SyncOverride<T> Inherited<T>() => new(false, default!);

    public static SyncOverride<T> Explicit<T>(T value) => new(true, value);
}

public sealed record SyncResolvedValue<T>(
    T Value,
    SyncValueProvenance Provenance,
    SyncValueSource Source);

public sealed class SyncSecret : IEquatable<SyncSecret>
{
    private readonly string value;

    private SyncSecret(string value)
    {
        this.value = value;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(value);

    public static SyncSecret Missing { get; } = new(string.Empty);

    public static SyncSecret FromPlainText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value);
    }

    public bool Equals(SyncSecret? other) =>
        other is not null && string.Equals(value, other.value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SyncSecret other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(value);

    public override string ToString() => IsConfigured ? "[configured]" : "[missing]";

    internal string RevealForPersistence() => value;
}

public sealed record SyncTargetTypeDefinition(
    SyncTargetKind Kind,
    string PersistencePattern,
    string WireContract,
    int MaximumInstances,
    bool InheritsGlobalSync,
    bool RequiresUrl,
    bool RequiresToken,
    bool SupportsBattlelogEnrichment,
    bool SupportsFleetRuntimeMode,
    IReadOnlySet<SyncDataKind> SupportedDataKinds,
    string DefaultUrl);

public sealed record SyncTargetPreset(
    string Id,
    string DisplayName,
    SyncTargetKind TargetKind,
    string SuggestedIdentity);

public static class SyncTargetTypeCatalog
{
    private static readonly ReadOnlyDictionary<SyncTargetKind, SyncTargetTypeDefinition> Definitions =
        new(
            new Dictionary<SyncTargetKind, SyncTargetTypeDefinition>
            {
                [SyncTargetKind.LocalSidecar] = new(
                    SyncTargetKind.LocalSidecar,
                    "sidecar.sync",
                    "sidecar_local_ingest",
                    1,
                    false,
                    true,
                    true,
                    true,
                    true,
                    new[]
                    {
                        SyncDataKind.BattlelogsRealtime,
                        SyncDataKind.FleetRuntime,
                    }.ToFrozenSet(),
                    "http://127.0.0.1:43127/api/sidecar/ingest"),
                [SyncTargetKind.LegacyCommunity] = External(
                    SyncTargetKind.LegacyCommunity,
                    "legacy_sync_json"),
                [SyncTargetKind.MajelIngest] = External(
                    SyncTargetKind.MajelIngest,
                    "majel.ingest.v1"),
            });

    private static readonly ReadOnlyCollection<SyncTargetPreset> PresetDefinitions =
        Array.AsReadOnly(
        new SyncTargetPreset[]
        {
            new("spocks_club", "Spocks Club", SyncTargetKind.LegacyCommunity, "spocksclub"),
            new("next_spocks_club", "Next Spocks Club", SyncTargetKind.LegacyCommunity, "nextspocksclub"),
        });

    public static IReadOnlyDictionary<SyncTargetKind, SyncTargetTypeDefinition> All => Definitions;

    public static IReadOnlyList<SyncTargetPreset> Presets => PresetDefinitions;

    public static SyncTargetTypeDefinition Get(SyncTargetKind kind) => Definitions[kind];

    public static SyncTargetPreset GetPreset(string id) =>
        PresetDefinitions.Single(preset => string.Equals(preset.Id, id, StringComparison.Ordinal));

    private static SyncTargetTypeDefinition External(SyncTargetKind kind, string wireContract)
    {
        return new(
            kind,
            "sync.targets.*",
            wireContract,
            int.MaxValue,
            true,
            true,
            true,
            false,
            false,
            Enum.GetValues<SyncDataKind>()
                .Where(value => value != SyncDataKind.FleetRuntime)
                .ToFrozenSet(),
            string.Empty);
    }
}

public sealed class SyncGlobalDefaults
{
    private readonly ReadOnlyDictionary<SyncDataKind, bool> dataKinds;

    public SyncGlobalDefaults(
        string proxy,
        bool verifySsl,
        bool allowUnsafeTlsWithoutCertificateValidation,
        IReadOnlyDictionary<SyncDataKind, bool>? dataKinds = null)
    {
        Proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        VerifySsl = verifySsl;
        AllowUnsafeTlsWithoutCertificateValidation = allowUnsafeTlsWithoutCertificateValidation;
        this.dataKinds = new(
            Enum.GetValues<SyncDataKind>()
                .ToDictionary(
                    kind => kind,
                    kind => dataKinds is not null && dataKinds.TryGetValue(kind, out var enabled) && enabled));
    }

    public string Proxy { get; }

    public bool VerifySsl { get; }

    public bool AllowUnsafeTlsWithoutCertificateValidation { get; }

    public IReadOnlyDictionary<SyncDataKind, bool> DataKinds => dataKinds;

    public SyncGlobalDefaults WithProxy(string proxy) =>
        new(proxy, VerifySsl, AllowUnsafeTlsWithoutCertificateValidation, dataKinds);

    public SyncGlobalDefaults WithVerifySsl(bool value) =>
        new(Proxy, value, AllowUnsafeTlsWithoutCertificateValidation, dataKinds);

    public SyncGlobalDefaults WithUnsafeTls(bool value) =>
        new(Proxy, VerifySsl, value, dataKinds);

    public SyncGlobalDefaults WithDataKind(SyncDataKind kind, bool enabled)
    {
        var changed = dataKinds.ToDictionary(item => item.Key, item => item.Value);
        changed[kind] = enabled;
        return new(Proxy, VerifySsl, AllowUnsafeTlsWithoutCertificateValidation, changed);
    }
}

public sealed class SyncTargetDraft
{
    private readonly ReadOnlyDictionary<SyncDataKind, SyncOverride<bool>> dataOverrides;

    public SyncTargetDraft(
        string name,
        SyncTargetKind kind,
        bool enabled,
        string url,
        SyncSecret token,
        SyncOverride<string> proxy,
        SyncOverride<bool> verifySsl,
        SyncOverride<bool> allowUnsafeTlsWithoutCertificateValidation,
        IReadOnlyDictionary<SyncDataKind, SyncOverride<bool>>? dataOverrides = null,
        SyncOverride<bool>? battlelogEnrichment = null,
        SyncOverride<string>? fleetRuntimeMode = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Kind = kind;
        Enabled = enabled;
        Url = url ?? throw new ArgumentNullException(nameof(url));
        Token = token ?? throw new ArgumentNullException(nameof(token));
        Proxy = proxy;
        VerifySsl = verifySsl;
        AllowUnsafeTlsWithoutCertificateValidation = allowUnsafeTlsWithoutCertificateValidation;
        this.dataOverrides = new(dataOverrides?.ToDictionary(item => item.Key, item => item.Value) ?? []);
        BattlelogEnrichment = battlelogEnrichment ?? SyncOverride.Inherited<bool>();
        FleetRuntimeMode = fleetRuntimeMode ?? SyncOverride.Inherited<string>();
    }

    public string Name { get; }

    public SyncTargetKind Kind { get; }

    public bool Enabled { get; }

    public string Url { get; }

    public SyncSecret Token { get; }

    public SyncOverride<string> Proxy { get; }

    public SyncOverride<bool> VerifySsl { get; }

    public SyncOverride<bool> AllowUnsafeTlsWithoutCertificateValidation { get; }

    public IReadOnlyDictionary<SyncDataKind, SyncOverride<bool>> DataOverrides => dataOverrides;

    public SyncOverride<bool> BattlelogEnrichment { get; }

    public SyncOverride<string> FleetRuntimeMode { get; }

    public static SyncTargetDraft Create(string name, SyncTargetKind kind)
    {
        var definition = SyncTargetTypeCatalog.Get(kind);
        return new(
            kind == SyncTargetKind.LocalSidecar ? SyncDesiredTopology.LocalSidecarIdentity : name,
            kind,
            false,
            definition.DefaultUrl,
            SyncSecret.Missing,
            SyncOverride.Inherited<string>(),
            SyncOverride.Inherited<bool>(),
            SyncOverride.Inherited<bool>());
    }

    public SyncTargetDraft WithIdentity(string name) => Copy(name: name);

    public SyncTargetDraft WithKind(SyncTargetKind kind) => Copy(kind: kind);

    public SyncTargetDraft WithEnabled(bool enabled) => Copy(enabled: enabled);

    public SyncTargetDraft WithConnection(string url, SyncSecret token) => Copy(url: url, token: token);

    public SyncTargetDraft WithProxy(SyncOverride<string> value) => Copy(proxy: value);

    public SyncTargetDraft WithVerifySsl(SyncOverride<bool> value) => Copy(verifySsl: value);

    public SyncTargetDraft WithUnsafeTls(SyncOverride<bool> value) =>
        Copy(allowUnsafeTlsWithoutCertificateValidation: value);

    public SyncTargetDraft WithBattlelogEnrichment(SyncOverride<bool> value) =>
        Copy(battlelogEnrichment: value);

    public SyncTargetDraft WithFleetRuntimeMode(SyncOverride<string> value) =>
        Copy(fleetRuntimeMode: value);

    public SyncTargetDraft WithDataOverride(SyncDataKind kind, SyncOverride<bool> value)
    {
        var changed = dataOverrides.ToDictionary(item => item.Key, item => item.Value);
        if (value.IsExplicit)
        {
            changed[kind] = value;
        }
        else
        {
            changed.Remove(kind);
        }

        return Copy(dataOverrides: changed);
    }

    public SyncTargetDraft WithoutSecret() => Copy(token: SyncSecret.Missing);

    private SyncTargetDraft Copy(
        string? name = null,
        SyncTargetKind? kind = null,
        bool? enabled = null,
        string? url = null,
        SyncSecret? token = null,
        SyncOverride<string>? proxy = null,
        SyncOverride<bool>? verifySsl = null,
        SyncOverride<bool>? allowUnsafeTlsWithoutCertificateValidation = null,
        IReadOnlyDictionary<SyncDataKind, SyncOverride<bool>>? dataOverrides = null,
        SyncOverride<bool>? battlelogEnrichment = null,
        SyncOverride<string>? fleetRuntimeMode = null) =>
        new(
            name ?? Name,
            kind ?? Kind,
            enabled ?? Enabled,
            url ?? Url,
            token ?? Token,
            proxy ?? Proxy,
            verifySsl ?? VerifySsl,
            allowUnsafeTlsWithoutCertificateValidation ?? AllowUnsafeTlsWithoutCertificateValidation,
            dataOverrides ?? this.dataOverrides,
            battlelogEnrichment ?? BattlelogEnrichment,
            fleetRuntimeMode ?? FleetRuntimeMode);
}

public sealed record SyncTopologyDiagnostic(
    string Code,
    SyncTopologyDiagnosticSeverity Severity,
    string Message,
    string? TargetName = null,
    string? Field = null);

public sealed record SyncResolvedTarget(
    string Name,
    SyncTargetKind Kind,
    bool Enabled,
    string Url,
    bool CredentialsConfigured,
    SyncResolvedValue<string> Proxy,
    SyncResolvedValue<bool> VerifySsl,
    SyncResolvedValue<bool> AllowUnsafeTlsWithoutCertificateValidation,
    IReadOnlyDictionary<SyncDataKind, SyncResolvedValue<bool>> DataKinds,
    SyncResolvedValue<bool>? BattlelogEnrichment,
    SyncResolvedValue<string>? FleetRuntimeMode);

public sealed record SyncResolvedTopology(
    IReadOnlyList<SyncResolvedTarget> Targets,
    IReadOnlyList<SyncTopologyDiagnostic> Diagnostics)
{
    public bool IsCommittable => Diagnostics.All(item => item.Severity != SyncTopologyDiagnosticSeverity.Error);
}

public sealed record SyncTopologyTransitionResult(
    bool Succeeded,
    SyncDesiredTopology Topology,
    SyncTopologyDiagnostic? Diagnostic = null);
