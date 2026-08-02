using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public enum LauncherConfigurationSourceId
{
    Guffawaffle,
    Netniv,
}

public enum LauncherConfigurationControl
{
    Scalar,
    Keybinding,
    NotificationPolicy,
}

#pragma warning disable CA1720 // Members intentionally mirror the schema's public value-kind vocabulary.
public enum LauncherConfigurationValueKind
{
    Boolean,
    Enum,
    Integer,
    Keybinding,
    Number,
    String,
    Union,
}
#pragma warning restore CA1720

public enum LauncherConfigurationStability
{
    Stable,
    Advanced,
    Experimental,
    Internal,
}

public enum LauncherConfigurationPlatform
{
    Windows,
    Macos,
}

public enum LauncherConfigurationSensitivity
{
    Public,
    Private,
    Secret,
}

public enum LauncherConfigurationAliasStatus
{
    Compatibility,
    Deprecated,
}

public enum LauncherConfigurationAliasPrecedence
{
    CanonicalWins,
    CanonicalReplacesWholePolicy,
}

public sealed record LauncherConfigurationAlias(
    string Path,
    LauncherConfigurationAliasStatus Status,
    LauncherConfigurationAliasPrecedence Precedence,
    string? RemovalVersion);

public sealed record LauncherConfigurationProvenance(
    string RuntimePath,
    string? DefaultSource);

public sealed record LauncherConfigurationSource(
    LauncherConfigurationSourceId Id,
    string Repository)
{
    public string StableId =>
        Id switch
        {
            LauncherConfigurationSourceId.Guffawaffle => "guffawaffle",
            LauncherConfigurationSourceId.Netniv => "netniv",
            _ => throw new InvalidOperationException($"Configuration source '{Id}' has no stable provider ID."),
        };

    public string DisplayName =>
        Id switch
        {
            LauncherConfigurationSourceId.Guffawaffle => "Guffawaffle",
            LauncherConfigurationSourceId.Netniv => "NetniV",
            _ => Id.ToString(),
        };
}

public sealed record LauncherConfigurationNumericConstraints(
    double? Minimum,
    double? Maximum)
{
    public bool Contains(double value) =>
        double.IsFinite(value)
        && (!Minimum.HasValue || value >= Minimum.Value)
        && (!Maximum.HasValue || value <= Maximum.Value);
}

public sealed record LauncherConfigurationKeybindingMetadata(
    string TriggerMode,
    string InputPhase,
    string InputLayer,
    string ConflictGroup,
    string ActionCategory);

public sealed class LauncherConfigurationSetting
{
    internal LauncherConfigurationSetting(
        string path,
        string title,
        string description,
        string category,
        LauncherConfigurationControl control,
        LauncherConfigurationValueKind valueKind,
        JsonElement valueTypeDefinition,
        LauncherConfigurationNumericConstraints? numericConstraints,
        LauncherConfigurationKeybindingMetadata? keybindingMetadata,
        JsonElement defaultValue,
        LauncherConfigurationStability stability,
        IReadOnlyList<LauncherConfigurationPlatform> platforms,
        IReadOnlyList<LauncherConfigurationSourceId> sourceSupport,
        LauncherConfigurationSensitivity sensitivity,
        IReadOnlyList<LauncherConfigurationAlias> aliases,
        LauncherConfigurationProvenance provenance,
        LauncherConfigurationApplyBehavior applyBehavior,
        LauncherConfigurationPresentation presentation)
    {
        Path = path;
        Title = title;
        Description = description;
        Category = category;
        Control = control;
        ValueKind = valueKind;
        ValueTypeDefinition = valueTypeDefinition;
        NumericConstraints = numericConstraints;
        KeybindingMetadata = keybindingMetadata;
        DefaultValue = defaultValue;
        Stability = stability;
        Platforms = platforms;
        SourceSupport = sourceSupport;
        Sensitivity = sensitivity;
        Aliases = aliases;
        Provenance = provenance;
        ApplyBehavior = applyBehavior;
        Presentation = presentation;
        IsTemplate = path.Split('.').Contains("*", StringComparer.Ordinal);
    }

    public string Path { get; }

    /// <summary>
    /// Durable setting identity within the provider-owned catalog. Canonical paths
    /// are the runtime contract's stable IDs; display copy never participates in identity.
    /// </summary>
    public string StableId => Path;

    public string Title { get; }

    public string Description { get; }

    public string Category { get; }

    public LauncherConfigurationControl Control { get; }

    public LauncherConfigurationValueKind ValueKind { get; }

    /// <summary>
    /// The complete valueType object from the schema. This retains adapter-specific
    /// metadata such as enum values and notification policy variants.
    /// </summary>
    public JsonElement ValueTypeDefinition { get; }

    public LauncherConfigurationNumericConstraints? NumericConstraints { get; }

    public LauncherConfigurationKeybindingMetadata? KeybindingMetadata { get; }

    /// <summary>
    /// A detached copy of the JSON default. Callers may safely retain this value
    /// after the source stream and loader document have been disposed.
    /// </summary>
    public JsonElement DefaultValue { get; }

    public LauncherConfigurationStability Stability { get; }

    public IReadOnlyList<LauncherConfigurationPlatform> Platforms { get; }

    public IReadOnlyList<LauncherConfigurationSourceId> SourceSupport { get; }

    public LauncherConfigurationSensitivity Sensitivity { get; }

    public IReadOnlyList<LauncherConfigurationAlias> Aliases { get; }

    public LauncherConfigurationProvenance Provenance { get; }

    public LauncherConfigurationApplyBehavior ApplyBehavior { get; }

    /// <summary>
    /// The authoritative serialized apply token. Prefer <see cref="ApplyBehavior"/>
    /// for decisions and <see cref="Presentation"/> for player-facing copy.
    /// </summary>
    public string Apply => LauncherConfigurationPresentation.ApplyTokenFor(ApplyBehavior);

    public LauncherConfigurationPresentation Presentation { get; }

    /// <summary>
    /// True when the schema path contains a wildcard path segment that must be
    /// replaced with a concrete runtime identity before editing.
    /// </summary>
    public bool IsTemplate { get; }

    public bool IsPlayerFacing =>
        Sensitivity == LauncherConfigurationSensitivity.Public
        && Stability != LauncherConfigurationStability.Internal;

    /// <summary>
    /// True when this catalog entry can be presented as a concrete editor row.
    /// Player-facing templates remain catalogued but require session-specific
    /// instantiation before they become directly editable.
    /// </summary>
    public bool IsDirectlyEditable => IsPlayerFacing && !IsTemplate;
}

public sealed class LauncherConfigurationCatalog
{
    private readonly IReadOnlyList<LauncherConfigurationSetting> _visibleSettings;

    internal LauncherConfigurationCatalog(
        Version schemaVersion,
        LauncherConfigurationSource source,
        IReadOnlyList<LauncherConfigurationSetting> settings)
    {
        SchemaVersion = schemaVersion;
        Source = source;
        Settings = settings;
        _visibleSettings = Array.AsReadOnly(settings.Where(setting => setting.IsDirectlyEditable).ToArray());
        Categories = Array.AsReadOnly(
            _visibleSettings
                .Select(setting => setting.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public Version SchemaVersion { get; }

    public LauncherConfigurationSource Source { get; }

    public IReadOnlyList<LauncherConfigurationSetting> Settings { get; }

    public IReadOnlyList<LauncherConfigurationSetting> VisibleSettings => _visibleSettings;

    public IReadOnlyList<string> Categories { get; }

    public IReadOnlyList<LauncherConfigurationSetting> Search(
        string? query,
        string? category = null)
    {
        var normalizedQuery = query?.Trim();
        var normalizedCategory = category?.Trim();

        var matches = _visibleSettings.Where(
            setting =>
                (string.IsNullOrEmpty(normalizedCategory)
                 || string.Equals(setting.Category, normalizedCategory, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrEmpty(normalizedQuery)
                    || Contains(setting.Path, normalizedQuery)
                    || Contains(setting.Title, normalizedQuery)
                    || Contains(setting.Description, normalizedQuery)
                    || Contains(setting.Category, normalizedQuery)
                    || Contains(setting.Presentation.Label, normalizedQuery)
                    || Contains(setting.Presentation.Help, normalizedQuery)
                    || Contains(setting.Presentation.Group, normalizedQuery)
                    || setting.Presentation.SearchTerms.Any(
                        term => Contains(term, normalizedQuery))));

        return Array.AsReadOnly(matches.ToArray());
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}

public sealed class LauncherConfigurationSchemaException : Exception
{
    public LauncherConfigurationSchemaException()
    {
    }

    public LauncherConfigurationSchemaException(string message)
        : base(message)
    {
    }

    public LauncherConfigurationSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
