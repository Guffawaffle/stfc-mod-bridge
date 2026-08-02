using System.Collections.ObjectModel;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherNotificationDefinition(
    LauncherConfigurationSetting Setting,
    LauncherNotificationPolicy DefaultPolicy,
    string InvalidValueBehavior,
    bool HasCompleteProviderMetadata)
{
    public string Id => Setting.Path;

    public IReadOnlyList<string> Sounds =>
        LauncherNotificationPolicyParser.ReadAllowedSounds(Setting);

    public IReadOnlyList<LauncherConfigurationAlias> Aliases => Setting.Aliases;

    public LauncherConfigurationProvenance Provenance => Setting.Provenance;
}

/// <summary>
/// Provider-resolved notification surface. This catalog is derived exclusively
/// from configuration-schema metadata and is independent from the user's sparse TOML.
/// </summary>
public sealed class LauncherNotificationCatalog
{
    private LauncherNotificationCatalog(
        IReadOnlyList<LauncherNotificationDefinition> events,
        IReadOnlyDictionary<string, LauncherNotificationDefinition> entriesByPath)
    {
        Events = events;
        EntriesByPath = entriesByPath;
    }

    public IReadOnlyList<LauncherNotificationDefinition> Events { get; }

    /// <summary>
    /// Canonical and declared compatibility paths, compared case-insensitively.
    /// </summary>
    public IReadOnlyDictionary<string, LauncherNotificationDefinition> EntriesByPath { get; }

    internal static LauncherNotificationCatalog Create(
        IReadOnlyList<LauncherConfigurationSetting> settings)
    {
        var events = new List<LauncherNotificationDefinition>();
        var entriesByPath = new Dictionary<string, LauncherNotificationDefinition>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var setting in settings.Where(
                     candidate => candidate.Control == LauncherConfigurationControl.NotificationPolicy))
        {
            var invalidValueBehavior = ReadOptionalStringMetadata(
                setting,
                "invalidValueBehavior") ?? "unknown";
            if (invalidValueBehavior != "unknown"
                && !string.Equals(
                    invalidValueBehavior,
                    "warn-and-use-event-default",
                    StringComparison.Ordinal))
            {
                throw new LauncherConfigurationSchemaException(
                    $"Notification '{setting.Path}' declares unsupported invalid-value behavior "
                    + $"'{invalidValueBehavior}'.");
            }

            var defaultPolicy = LauncherNotificationPolicyParser.Parse(setting, null).Policy;
            var hasExpandedDefault = ValidateExpandedDefault(setting, defaultPolicy);
            var hasCompleteProviderMetadata =
                hasExpandedDefault
                && invalidValueBehavior != "unknown"
                && !string.Equals(setting.Provenance.RuntimePath, "unknown", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(setting.Provenance.DefaultSource)
                && !string.Equals(setting.Provenance.DefaultSource, "unknown", StringComparison.Ordinal);
            var definition = new LauncherNotificationDefinition(
                setting,
                defaultPolicy,
                invalidValueBehavior,
                hasCompleteProviderMetadata);
            AddPath(entriesByPath, setting.Path, definition, "canonical");
            foreach (var alias in setting.Aliases)
            {
                if (alias.Status != LauncherConfigurationAliasStatus.Deprecated
                    || alias.Precedence != LauncherConfigurationAliasPrecedence.CanonicalReplacesWholePolicy
                    || string.IsNullOrWhiteSpace(alias.RemovalVersion))
                {
                    throw new LauncherConfigurationSchemaException(
                        $"Notification alias '{alias.Path}' does not declare the supported "
                        + "deprecated whole-policy precedence contract.");
                }

                AddPath(entriesByPath, alias.Path, definition, "alias");
            }

            events.Add(definition);
        }

        return new(
            events.AsReadOnly(),
            new ReadOnlyDictionary<string, LauncherNotificationDefinition>(entriesByPath));
    }

    private static bool ValidateExpandedDefault(
        LauncherConfigurationSetting setting,
        LauncherNotificationPolicy parsedDefault)
    {
        if (!setting.SchemaMetadata.TryGetProperty("expandedDefault", out var expanded))
        {
            return false;
        }

        if (expanded.ValueKind != JsonValueKind.Object
            || !expanded.TryGetProperty("system", out var system)
            || system.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !expanded.TryGetProperty("audio", out var audio)
            || audio.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !expanded.TryGetProperty("sound", out var sound)
            || sound.ValueKind != JsonValueKind.String)
        {
            throw new LauncherConfigurationSchemaException(
                $"Notification '{setting.Path}' must declare a complete expandedDefault policy.");
        }

        if (system.GetBoolean() != parsedDefault.System
            || audio.GetBoolean() != parsedDefault.Audio
            || !string.Equals(sound.GetString(), parsedDefault.Sound, StringComparison.Ordinal))
        {
            throw new LauncherConfigurationSchemaException(
                $"Notification '{setting.Path}' expandedDefault does not match its runtime default metadata.");
        }

        return true;
    }

    private static string? ReadOptionalStringMetadata(
        LauncherConfigurationSetting setting,
        string propertyName)
    {
        if (!setting.SchemaMetadata.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new LauncherConfigurationSchemaException(
                $"Notification '{setting.Path}' declares invalid '{propertyName}'.");
        }

        return value.GetString()!;
    }

    private static void AddPath(
        IDictionary<string, LauncherNotificationDefinition> entries,
        string path,
        LauncherNotificationDefinition definition,
        string kind)
    {
        if (!entries.TryAdd(path, definition))
        {
            throw new LauncherConfigurationSchemaException(
                $"Notification {kind} path '{path}' is assigned more than once.");
        }
    }
}
