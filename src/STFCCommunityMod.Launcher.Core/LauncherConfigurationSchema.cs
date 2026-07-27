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

public sealed record LauncherConfigurationSource(
    LauncherConfigurationSourceId Id,
    string Repository)
{
    public string DisplayName =>
        Id switch
        {
            LauncherConfigurationSourceId.Guffawaffle => "Guffawaffle",
            LauncherConfigurationSourceId.Netniv => "NetniV",
            _ => Id.ToString(),
        };
}

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
        JsonElement defaultValue,
        LauncherConfigurationStability stability,
        IReadOnlyList<LauncherConfigurationPlatform> platforms,
        IReadOnlyList<LauncherConfigurationSourceId> sourceSupport,
        LauncherConfigurationSensitivity sensitivity,
        string apply)
    {
        Path = path;
        Title = title;
        Description = description;
        Category = category;
        Control = control;
        ValueKind = valueKind;
        ValueTypeDefinition = valueTypeDefinition;
        DefaultValue = defaultValue;
        Stability = stability;
        Platforms = platforms;
        SourceSupport = sourceSupport;
        Sensitivity = sensitivity;
        Apply = apply;
        IsTemplate = path.Split('.').Contains("*", StringComparer.Ordinal);
    }

    public string Path { get; }

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

    /// <summary>
    /// A detached copy of the JSON default. Callers may safely retain this value
    /// after the source stream and loader document have been disposed.
    /// </summary>
    public JsonElement DefaultValue { get; }

    public LauncherConfigurationStability Stability { get; }

    public IReadOnlyList<LauncherConfigurationPlatform> Platforms { get; }

    public IReadOnlyList<LauncherConfigurationSourceId> SourceSupport { get; }

    public LauncherConfigurationSensitivity Sensitivity { get; }

    public string Apply { get; }

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
                    || Contains(setting.Category, normalizedQuery)));

        return Array.AsReadOnly(matches.ToArray());
    }

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);
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
