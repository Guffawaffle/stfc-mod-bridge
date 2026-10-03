using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherNotificationPolicy(
    bool System,
    bool Audio,
    string Sound)
{
    public bool IsEnabled => System || Audio;

    public string Render()
    {
        if (!System && !Audio)
        {
            return "false";
        }

        if (System && !Audio)
        {
            return "true";
        }

        return $"{{ system = {RenderBoolean(System)}, audio = {RenderBoolean(Audio)}, sound = {JsonSerializer.Serialize(Sound)} }}";
    }

    private static string RenderBoolean(bool value) => value ? "true" : "false";
}

public sealed record LauncherNotificationPolicyParseResult(
    bool IsValid,
    LauncherNotificationPolicy Policy,
    string? Error = null);

public static class LauncherNotificationPolicyParser
{
    public static LauncherNotificationPolicyParseResult Parse(
        LauncherConfigurationSetting setting,
        string? renderedValue)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (setting.Control != LauncherConfigurationControl.NotificationPolicy)
        {
            throw new ArgumentException(
                $"'{setting.Path}' is not a notification policy.",
                nameof(setting));
        }

        var defaultPolicy = ReadDefaultPolicy(setting);
        if (string.IsNullOrWhiteSpace(renderedValue))
        {
            return new(true, defaultPolicy);
        }

        var trimmed = renderedValue.Trim();
        if (trimmed == "false")
        {
            return new(true, defaultPolicy with { System = false, Audio = false });
        }

        if (trimmed == "true")
        {
            return new(true, defaultPolicy with { System = true, Audio = false });
        }

        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
        {
            return Invalid(defaultPolicy, "The notification policy must be false, true, or an inline table.");
        }

        var read = TomlNativeRuntime.Request(new("read", "policy = " + trimmed + "\n"));
        if (!read.Ok || read.Overrides is null || read.Tables is null
            || read.Tables.Any(item => item.Path.Length != 1 || item.Path[0] != "policy")
            || !read.Overrides.Any(item => item.Path.Length == 1 && item.Path[0] == "policy")
            || read.Overrides.Any(item => item.Path.Length == 0 || item.Path[0] != "policy"))
            return Invalid(defaultPolicy, "The notification policy inline table is malformed.");
        var system = defaultPolicy.System;
        var audio = defaultPolicy.Audio;
        var sound = defaultPolicy.Sound;
        foreach (var field in read.Overrides.Where(item => item.Path.Length > 1))
        {
            if (field.Path.Length != 2)
                return Invalid(defaultPolicy, "Notification policy fields cannot contain nested values.");
            switch (field.Path[1])
            {
                case "system":
                    if (!TryParseBoolean(field.SemanticValue, out system))
                        return Invalid(defaultPolicy, "Notification policy field 'system' must be true or false.");
                    break;
                case "audio":
                    if (!TryParseBoolean(field.SemanticValue, out audio))
                        return Invalid(defaultPolicy, "Notification policy field 'audio' must be true or false.");
                    break;
                case "sound":
                    if (!LauncherTomlValue.TryReadString(field.Value, out sound)
                        || !ReadAllowedSounds(setting).Contains(sound, StringComparer.Ordinal))
                        return Invalid(defaultPolicy, "The notification policy sound is not supported.");
                    break;
                default:
                    return Invalid(defaultPolicy, "The notification policy contains an unknown field.");
            }
        }
        return new(true, new(system, audio, sound));
    }

    public static IReadOnlyList<string> ReadAllowedSounds(
        LauncherConfigurationSetting setting)
    {
        var sound = ReadObjectField(setting, "sound");
        if (sound.ValueKind != JsonValueKind.Object
            || !sound.TryGetProperty("values", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return Array.AsReadOnly(
            values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .ToArray());
    }

    private static LauncherNotificationPolicy ReadDefaultPolicy(
        LauncherConfigurationSetting setting)
    {
        var system = ReadBooleanFieldDefault(setting, "system");
        var audio = ReadBooleanFieldDefault(setting, "audio");
        var soundElement = ReadObjectField(setting, "sound");
        var sound = soundElement.ValueKind == JsonValueKind.Object
            && soundElement.TryGetProperty("default", out var soundDefault)
            && soundDefault.ValueKind == JsonValueKind.String
                ? soundDefault.GetString()!
                : "default";

        return setting.DefaultValue.ValueKind switch
        {
            JsonValueKind.True => new(true, false, sound),
            JsonValueKind.False => new(false, false, sound),
            JsonValueKind.Object => new(
                ReadBooleanProperty(setting.DefaultValue, "system", system),
                ReadBooleanProperty(setting.DefaultValue, "audio", audio),
                ReadStringProperty(setting.DefaultValue, "sound", sound)),
            _ => new(system, audio, sound),
        };
    }

    private static bool ReadBooleanFieldDefault(
        LauncherConfigurationSetting setting,
        string fieldName)
    {
        var field = ReadObjectField(setting, fieldName);
        return field.ValueKind == JsonValueKind.Object
            && field.TryGetProperty("default", out var defaultValue)
            && defaultValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            && defaultValue.GetBoolean();
    }

    private static JsonElement ReadObjectField(
        LauncherConfigurationSetting setting,
        string fieldName)
    {
        if (!setting.ValueTypeDefinition.TryGetProperty("variants", out var variants)
            || variants.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        foreach (var variant in variants.EnumerateArray())
        {
            if (variant.ValueKind == JsonValueKind.Object
                && variant.TryGetProperty("kind", out var kind)
                && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "object"
                && variant.TryGetProperty("fields", out var fields)
                && fields.ValueKind == JsonValueKind.Object
                && fields.TryGetProperty(fieldName, out var field))
            {
                return field;
            }
        }

        return default;
    }

    private static bool ReadBooleanProperty(
        JsonElement value,
        string propertyName,
        bool fallback) =>
        value.TryGetProperty(propertyName, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : fallback;

    private static string ReadStringProperty(
        JsonElement value,
        string propertyName,
        string fallback) =>
        value.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;

    private static bool TryParseBoolean(string value, out bool parsed)
    {
        if (value == "true")
        {
            parsed = true;
            return true;
        }

        if (value == "false")
        {
            parsed = false;
            return true;
        }

        parsed = false;
        return false;
    }

    private static LauncherNotificationPolicyParseResult Invalid(
        LauncherNotificationPolicy fallback,
        string error) =>
        new(false, fallback, error);
}
