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

public enum SyncTargetKindChangePolicy
{
    PreserveCompatibleOverrides,
    ResetOverrides,
}

public enum SyncAuthenticationCapability
{
    OpaqueToken,
}

public enum SyncEndpointPolicy
{
    LoopbackOnly,
    NonLoopback,
}

public enum SyncTargetExposurePolicy
{
    Hidden,
    ExistingConfigurationOnly,
    Creatable,
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

    public override string ToString() => IsExplicit ? "[explicit]" : "[inherited]";
}

public static class SyncOverride
{
    public static SyncOverride<T> Inherited<T>() => new(false, default!);

    public static SyncOverride<T> Explicit<T>(T value) => new(true, value);
}

public sealed record SyncResolvedValue<T>(
    T Value,
    SyncValueProvenance Provenance,
    SyncValueSource Source)
{
    public override string ToString() => $"[{Provenance} from {Source}]";
}

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
    SyncTargetExposurePolicy ExposurePolicy,
    string Id,
    string DisplayName,
    string Description,
    string CapabilitySummary,
    string PersistencePattern,
    string WireContract,
    int MaximumInstances,
    bool SupportsDisabledState,
    string? FixedIdentity,
    bool InheritsGlobalSync,
    bool RequiresUrl,
    bool RequiresToken,
    bool SupportsBattlelogEnrichment,
    bool SupportsFleetRuntimeMode,
    bool SupportsProxy,
    bool SupportsTls,
    SyncEndpointPolicy EndpointPolicy,
    SyncAuthenticationCapability Authentication,
    IReadOnlyList<SyncConnectionFieldDefinition> ConnectionFields,
    IReadOnlySet<SyncDataKind> SupportedDataKinds,
    string DefaultUrl,
    string TypeSpecificContent);

public sealed record SyncConnectionFieldDefinition(
    string Id,
    string DisplayName,
    string Description,
    bool IsRequired,
    bool IsSecret);

public sealed record SyncFeedDefinition(
    SyncDataKind Kind,
    string Id,
    string DisplayName,
    string Description);

public sealed record SyncTargetPreset(
    string Id,
    string DisplayName,
    SyncTargetKind TargetKind,
    string SuggestedIdentity,
    string Description,
    string DefaultUrl,
    IReadOnlyDictionary<SyncDataKind, bool> FeedDefaults)
{
    public IReadOnlySet<SyncDataKind> SupportedDataKinds { get; } =
        FeedDefaults.Keys.ToFrozenSet();
}

public static class SyncTargetTypeCatalog
{
    private static readonly IReadOnlyList<SyncConnectionFieldDefinition> EndpointAndTokenFields =
    [
        new("endpoint", "Endpoint", "Destination ingest endpoint.", true, false),
        new("token", "Token", "Opaque authentication token. Saved values are never displayed.", true, true),
    ];

    private static readonly ReadOnlyDictionary<SyncDataKind, SyncFeedDefinition> FeedDefinitions =
        new(
            new Dictionary<SyncDataKind, SyncFeedDefinition>
            {
                [SyncDataKind.Battlelogs] = Feed(SyncDataKind.Battlelogs, "battlelogs", "Battlelogs"),
                [SyncDataKind.BattlelogsRealtime] = Feed(SyncDataKind.BattlelogsRealtime, "battlelogs_realtime", "Realtime battlelogs"),
                [SyncDataKind.Buffs] = Feed(SyncDataKind.Buffs, "buffs", "Buffs"),
                [SyncDataKind.Buildings] = Feed(SyncDataKind.Buildings, "buildings", "Buildings"),
                [SyncDataKind.Inventory] = Feed(SyncDataKind.Inventory, "inventory", "Inventory"),
                [SyncDataKind.Jobs] = Feed(SyncDataKind.Jobs, "jobs", "Jobs"),
                [SyncDataKind.Missions] = Feed(SyncDataKind.Missions, "missions", "Missions"),
                [SyncDataKind.Officer] = Feed(SyncDataKind.Officer, "officer", "Officers"),
                [SyncDataKind.Research] = Feed(SyncDataKind.Research, "research", "Research"),
                [SyncDataKind.Resources] = Feed(SyncDataKind.Resources, "resources", "Resources"),
                [SyncDataKind.Ships] = Feed(SyncDataKind.Ships, "ships", "Ships"),
                [SyncDataKind.Slots] = Feed(SyncDataKind.Slots, "slots", "Slots"),
                [SyncDataKind.Tech] = Feed(SyncDataKind.Tech, "tech", "Tech"),
                [SyncDataKind.Traits] = Feed(SyncDataKind.Traits, "traits", "Traits"),
                [SyncDataKind.FleetRuntime] = Feed(SyncDataKind.FleetRuntime, "fleet_runtime", "Fleet runtime"),
            });

    private static readonly ReadOnlyDictionary<SyncTargetKind, SyncTargetTypeDefinition> Definitions =
        new(
            new Dictionary<SyncTargetKind, SyncTargetTypeDefinition>
            {
                [SyncTargetKind.LocalSidecar] = new(
                    SyncTargetKind.LocalSidecar,
                    SyncTargetExposurePolicy.ExistingConfigurationOnly,
                    "sidecar",
                    "Sidecar",
                    "Sends realtime battle and fleet-runtime data to a local companion process.",
                    "2 feeds · local endpoint · token authentication",
                    "sidecar.sync",
                    "sidecar_local_ingest",
                    1,
                    true,
                    "local-sidecar",
                    false,
                    true,
                    true,
                    true,
                    true,
                    true,
                    true,
                    SyncEndpointPolicy.LoopbackOnly,
                    SyncAuthenticationCapability.OpaqueToken,
                    EndpointAndTokenFields,
                    new[]
                    {
                        SyncDataKind.BattlelogsRealtime,
                        SyncDataKind.FleetRuntime,
                    }.ToFrozenSet(),
                    "http://127.0.0.1:43127/api/sidecar/ingest",
                    "Supports battle-log enrichment and fleet-runtime delivery modes."),
                [SyncTargetKind.LegacyCommunity] = External(
                    SyncTargetKind.LegacyCommunity,
                    SyncTargetExposurePolicy.Creatable,
                    "legacy",
                    "Sync",
                    "Sends the standard sync payloads to a remote service.",
                    "14 feeds · remote endpoint · token authentication",
                    "legacy_sync_json"),
                [SyncTargetKind.MajelIngest] = External(
                    SyncTargetKind.MajelIngest,
                    SyncTargetExposurePolicy.Hidden,
                    "majel",
                    "Sync (advanced)",
                    "Advanced TOML-only wrapper around the standard sync mechanism.",
                    "14 feeds · remote endpoint · token authentication",
                    "majel.ingest.v1"),
            });

    private static readonly ReadOnlyCollection<SyncTargetPreset> PresetDefinitions =
        Array.AsReadOnly(
        new SyncTargetPreset[]
        {
            new("spocks_club", "Spock's Club", SyncTargetKind.LegacyCommunity, "spocksclub",
                "Prefills the canonical Spock's Club endpoint and its documented feed defaults.",
                "https://spocks.club/sync/ingress/",
                PresetFeeds(
                    (SyncDataKind.Resources, true),
                    (SyncDataKind.Battlelogs, false),
                    (SyncDataKind.Officer, true),
                    (SyncDataKind.Missions, false),
                    (SyncDataKind.Research, true),
                    (SyncDataKind.Tech, false),
                    (SyncDataKind.Traits, false),
                    (SyncDataKind.Buildings, true),
                    (SyncDataKind.Ships, false))),
            new("next_spocks_club", "Next Spock's Club", SyncTargetKind.LegacyCommunity, "spocksclub-next",
                "Prefills the canonical Next Spock's Club endpoint and its documented feed defaults.",
                "https://next.spocks.club/sync/ingress/",
                PresetFeeds(
                    (SyncDataKind.Battlelogs, false),
                    (SyncDataKind.Buffs, true),
                    (SyncDataKind.Buildings, true),
                    (SyncDataKind.Inventory, true),
                    (SyncDataKind.Jobs, false),
                    (SyncDataKind.Missions, true),
                    (SyncDataKind.Officer, true),
                    (SyncDataKind.Research, true),
                    (SyncDataKind.Resources, true),
                    (SyncDataKind.Ships, true),
                    (SyncDataKind.Slots, true),
                    (SyncDataKind.Tech, true),
                    (SyncDataKind.Traits, true))),
        });

    public static IReadOnlyDictionary<SyncTargetKind, SyncTargetTypeDefinition> All => Definitions;

    public static IReadOnlyList<SyncTargetPreset> Presets => PresetDefinitions;

    public static IReadOnlyDictionary<SyncDataKind, SyncFeedDefinition> Feeds => FeedDefinitions;

    public static SyncTargetTypeDefinition Get(SyncTargetKind kind) => Definitions[kind];

    public static SyncTargetPreset GetPreset(string id) =>
        PresetDefinitions.Single(preset => string.Equals(preset.Id, id, StringComparison.Ordinal));

    public static SyncFeedDefinition GetFeed(SyncDataKind kind) => FeedDefinitions[kind];

    public static IReadOnlyList<SyncTargetPreset> GetPresets(SyncTargetKind kind) =>
        PresetDefinitions.Where(preset => preset.TargetKind == kind).ToArray();

    public static SyncTargetPreset? FindPresetByUrl(string url) =>
        PresetDefinitions.FirstOrDefault(preset =>
            string.Equals(
                NormalizeEndpoint(preset.DefaultUrl),
                NormalizeEndpoint(url),
                StringComparison.OrdinalIgnoreCase));

    private static SyncTargetTypeDefinition External(
        SyncTargetKind kind,
        SyncTargetExposurePolicy exposurePolicy,
        string id,
        string displayName,
        string description,
        string capabilitySummary,
        string wireContract)
    {
        return new(
            kind,
            exposurePolicy,
            id,
            displayName,
            description,
            capabilitySummary,
            "sync.targets.*",
            wireContract,
            int.MaxValue,
            false,
            null,
            true,
            true,
            true,
            false,
            false,
            true,
            true,
            SyncEndpointPolicy.NonLoopback,
            SyncAuthenticationCapability.OpaqueToken,
            EndpointAndTokenFields,
            Enum.GetValues<SyncDataKind>()
                .Where(value => value != SyncDataKind.FleetRuntime)
                .ToFrozenSet(),
            string.Empty,
            "No additional adapter-specific fields are established by the current implementation.");
    }

    private static SyncFeedDefinition Feed(SyncDataKind kind, string id, string displayName) =>
        new(kind, id, displayName, $"Synchronize {displayName.ToLowerInvariant()} data.");

    private static FrozenDictionary<SyncDataKind, bool> PresetFeeds(
        params (SyncDataKind Kind, bool Enabled)[] values) =>
        values.ToFrozenDictionary(value => value.Kind, value => value.Enabled);

    private static string NormalizeEndpoint(string value) =>
        (value ?? string.Empty).Trim().TrimEnd('/');
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

    public SyncTargetDraft WithoutOverrides() =>
        Copy(
            proxy: SyncOverride.Inherited<string>(),
            verifySsl: SyncOverride.Inherited<bool>(),
            allowUnsafeTlsWithoutCertificateValidation: SyncOverride.Inherited<bool>(),
            dataOverrides: new Dictionary<SyncDataKind, SyncOverride<bool>>(),
            battlelogEnrichment: SyncOverride.Inherited<bool>(),
            fleetRuntimeMode: SyncOverride.Inherited<string>());

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

public sealed class SyncResolvedTarget(
    string name,
    SyncTargetKind kind,
    bool enabled,
    string url,
    bool credentialsConfigured,
    SyncResolvedValue<string> proxy,
    SyncResolvedValue<bool> verifySsl,
    SyncResolvedValue<bool> allowUnsafeTlsWithoutCertificateValidation,
    IReadOnlyDictionary<SyncDataKind, SyncResolvedValue<bool>> dataKinds,
    SyncResolvedValue<bool>? battlelogEnrichment,
    SyncResolvedValue<string>? fleetRuntimeMode)
{
    public string Name { get; } = name;

    public SyncTargetKind Kind { get; } = kind;

    public bool Enabled { get; } = enabled;

    public string Url { get; } = url;

    public bool CredentialsConfigured { get; } = credentialsConfigured;

    public SyncResolvedValue<string> Proxy { get; } = proxy;

    public SyncResolvedValue<bool> VerifySsl { get; } = verifySsl;

    public SyncResolvedValue<bool> AllowUnsafeTlsWithoutCertificateValidation { get; } =
        allowUnsafeTlsWithoutCertificateValidation;

    public IReadOnlyDictionary<SyncDataKind, SyncResolvedValue<bool>> DataKinds { get; } = dataKinds;

    public SyncResolvedValue<bool>? BattlelogEnrichment { get; } = battlelogEnrichment;

    public SyncResolvedValue<string>? FleetRuntimeMode { get; } = fleetRuntimeMode;

    public override string ToString() =>
        $"{Name} ({Kind}, {(Enabled ? "enabled" : "disabled")}, credentials {(CredentialsConfigured ? "configured" : "missing")})";
}

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
