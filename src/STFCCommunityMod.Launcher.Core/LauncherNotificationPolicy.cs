using System.Text;
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

        var fields = SplitFields(trimmed[1..^1]);
        if (fields is null)
        {
            return Invalid(defaultPolicy, "The notification policy inline table is malformed.");
        }

        var system = defaultPolicy.System;
        var audio = defaultPolicy.Audio;
        var sound = defaultPolicy.Sound;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var equals = FindEquals(field);
            if (equals <= 0)
            {
                return Invalid(defaultPolicy, "A notification policy field is missing '='.");
            }

            var key = field[..equals].Trim();
            var value = field[(equals + 1)..].Trim();
            if (!seen.Add(key))
            {
                return Invalid(defaultPolicy, $"Notification policy field '{key}' is assigned more than once.");
            }

            switch (key)
            {
                case "system":
                    if (!TryParseBoolean(value, out system))
                    {
                        return Invalid(defaultPolicy, "Notification policy field 'system' must be true or false.");
                    }

                    break;
                case "audio":
                    if (!TryParseBoolean(value, out audio))
                    {
                        return Invalid(defaultPolicy, "Notification policy field 'audio' must be true or false.");
                    }

                    break;
                case "sound":
                    if (!TryParseString(value, out sound)
                        || !ReadAllowedSounds(setting).Contains(sound, StringComparer.Ordinal))
                    {
                        return Invalid(defaultPolicy, $"Notification policy sound '{sound}' is not supported.");
                    }

                    break;
                default:
                    return Invalid(defaultPolicy, $"Unknown notification policy field '{key}'.");
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

    private static List<string>? SplitFields(string value)
    {
        var fields = new List<string>();
        var start = 0;
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (quote == '"' && !escaped && character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (!escaped && character == quote)
                {
                    quote = '\0';
                }

                escaped = false;
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == ',')
            {
                fields.Add(value[start..index].Trim());
                start = index + 1;
            }
        }

        if (quote != '\0')
        {
            return null;
        }

        var final = value[start..].Trim();
        if (final.Length > 0)
        {
            fields.Add(final);
        }

        return fields.Any(string.IsNullOrWhiteSpace) ? null : fields;
    }

    private static int FindEquals(string value)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (quote == '"' && !escaped && character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (!escaped && character == quote)
                {
                    quote = '\0';
                }

                escaped = false;
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '=')
            {
                return index;
            }
        }

        return -1;
    }

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

    private static bool TryParseString(string value, out string parsed)
    {
        parsed = string.Empty;
        if (value.Length < 2)
        {
            return false;
        }

        if (value[0] == '\'' && value[^1] == '\'')
        {
            parsed = value[1..^1];
            return !parsed.Contains('\'');
        }

        if (value[0] != '"' || value[^1] != '"')
        {
            return false;
        }

        try
        {
            parsed = JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static LauncherNotificationPolicyParseResult Invalid(
        LauncherNotificationPolicy fallback,
        string error) =>
        new(false, fallback, error);
}
