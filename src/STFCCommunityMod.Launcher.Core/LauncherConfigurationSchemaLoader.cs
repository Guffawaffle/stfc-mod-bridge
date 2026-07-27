using System.Text.Json;
using System.Collections.ObjectModel;

namespace STFCCommunityMod.Launcher.Core;

public static class LauncherConfigurationSchemaLoader
{
    private const string SupportedSchemaId = "stfc-community-mod.config-schema";
    private static readonly Version SupportedSchemaVersion = new(1, 0, 0);

    public static LauncherConfigurationCatalog LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static LauncherConfigurationCatalog Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead)
        {
            throw new ArgumentException("The schema stream must be readable.", nameof(stream));
        }

        try
        {
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });

            return ParseCatalog(document.RootElement);
        }
        catch (LauncherConfigurationSchemaException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new LauncherConfigurationSchemaException(
                "The launcher configuration schema is not valid JSON.",
                exception);
        }
    }

    private static LauncherConfigurationCatalog ParseCatalog(JsonElement root)
    {
        RequireKind(root, JsonValueKind.Object, "schema root");

        var schemaId = ReadRequiredString(root, "schemaId", "schema root");
        if (!string.Equals(schemaId, SupportedSchemaId, StringComparison.Ordinal))
        {
            throw Invalid($"Unsupported schema id '{schemaId}'.");
        }

        var schemaVersionText = ReadRequiredString(root, "schemaVersion", "schema root");
        if (!Version.TryParse(schemaVersionText, out var schemaVersion)
            || schemaVersion != SupportedSchemaVersion)
        {
            throw Invalid(
                $"Unsupported schema version '{schemaVersionText}'. "
                + $"This launcher supports {SupportedSchemaVersion}.");
        }

        var sourceElement = ReadRequiredProperty(root, "source", "schema root");
        RequireKind(sourceElement, JsonValueKind.Object, "source");
        var sourceId = ParseSourceId(ReadRequiredString(sourceElement, "id", "source"));
        var repository = ReadRequiredString(sourceElement, "repository", "source");
        var source = new LauncherConfigurationSource(sourceId, repository);

        var settingsElement = ReadRequiredProperty(root, "settings", "schema root");
        RequireKind(settingsElement, JsonValueKind.Array, "settings");

        var settings = new List<LauncherConfigurationSetting>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var settingElement in settingsElement.EnumerateArray())
        {
            var setting = ParseSetting(settingElement, index, sourceId);
            if (!paths.Add(setting.Path))
            {
                throw Invalid($"Duplicate setting path '{setting.Path}'.");
            }

            settings.Add(setting);
            ++index;
        }

        return new LauncherConfigurationCatalog(
            schemaVersion,
            source,
            Array.AsReadOnly(settings.ToArray()));
    }

    private static LauncherConfigurationSetting ParseSetting(
        JsonElement element,
        int index,
        LauncherConfigurationSourceId schemaSource)
    {
        var context = $"settings[{index}]";
        RequireKind(element, JsonValueKind.Object, context);

        var path = ReadRequiredString(element, "path", context);
        var title = ReadRequiredString(element, "title", context);
        var description = ReadRequiredString(element, "description", context);
        var category = ReadRequiredString(element, "category", context);
        var control = ParseControl(ReadRequiredString(element, "control", context), path);

        var valueType = ReadRequiredProperty(element, "valueType", context);
        RequireKind(valueType, JsonValueKind.Object, $"{context}.valueType");
        var valueKind = ParseValueKind(
            ReadRequiredString(valueType, "kind", $"{context}.valueType"),
            path);
        ValidateControlAndValueKind(control, valueKind, path);

        var defaultValue = ReadRequiredProperty(element, "default", context);
        ValidateDefault(defaultValue, valueKind, valueType, path);

        var stability = ParseStability(ReadRequiredString(element, "stability", context), path);
        var sensitivity = ParseSensitivity(ReadRequiredString(element, "sensitivity", context), path);
        var apply = ReadRequiredString(element, "apply", context);
        var platforms = ReadPlatforms(ReadRequiredProperty(element, "platforms", context), path);
        var sourceSupport = ReadSourceSupport(
            ReadRequiredProperty(element, "sourceSupport", context),
            path);

        if (!sourceSupport.Contains(schemaSource))
        {
            throw Invalid(
                $"Setting '{path}' does not declare support for schema source "
                + $"'{FormatSourceId(schemaSource)}'.");
        }

        return new LauncherConfigurationSetting(
            path,
            title,
            description,
            category,
            control,
            valueKind,
            valueType.Clone(),
            defaultValue.Clone(),
            stability,
            platforms,
            sourceSupport,
            sensitivity,
            apply);
    }

    private static ReadOnlyCollection<LauncherConfigurationPlatform> ReadPlatforms(
        JsonElement element,
        string path)
    {
        RequireKind(element, JsonValueKind.Array, $"platforms for '{path}'");
        var values = element
            .EnumerateArray()
            .Select(
                value =>
                    ParsePlatform(
                        ReadArrayString(value, $"platforms for '{path}'"),
                        path))
            .Distinct()
            .ToArray();

        if (values.Length == 0)
        {
            throw Invalid($"Setting '{path}' must support at least one platform.");
        }

        return Array.AsReadOnly(values);
    }

    private static ReadOnlyCollection<LauncherConfigurationSourceId> ReadSourceSupport(
        JsonElement element,
        string path)
    {
        RequireKind(element, JsonValueKind.Array, $"sourceSupport for '{path}'");
        var values = element
            .EnumerateArray()
            .Select(
                value =>
                    ParseSourceId(
                        ReadArrayString(value, $"sourceSupport for '{path}'")))
            .Distinct()
            .ToArray();

        if (values.Length == 0)
        {
            throw Invalid($"Setting '{path}' must support at least one source.");
        }

        return Array.AsReadOnly(values);
    }

    private static void ValidateControlAndValueKind(
        LauncherConfigurationControl control,
        LauncherConfigurationValueKind valueKind,
        string path)
    {
        var valid = control switch
        {
            LauncherConfigurationControl.Scalar =>
                valueKind is LauncherConfigurationValueKind.Boolean
                    or LauncherConfigurationValueKind.Enum
                    or LauncherConfigurationValueKind.Integer
                    or LauncherConfigurationValueKind.Number
                    or LauncherConfigurationValueKind.String,
            LauncherConfigurationControl.Keybinding =>
                valueKind == LauncherConfigurationValueKind.Keybinding,
            LauncherConfigurationControl.NotificationPolicy =>
                valueKind == LauncherConfigurationValueKind.Union,
            _ => false,
        };

        if (!valid)
        {
            throw Invalid(
                $"Setting '{path}' uses incompatible control '{control}' "
                + $"and value kind '{valueKind}'.");
        }
    }

    private static void ValidateDefault(
        JsonElement defaultValue,
        LauncherConfigurationValueKind valueKind,
        JsonElement valueType,
        string path)
    {
        var valid = valueKind switch
        {
            LauncherConfigurationValueKind.Boolean =>
                defaultValue.ValueKind is JsonValueKind.True or JsonValueKind.False,
            LauncherConfigurationValueKind.Enum =>
                defaultValue.ValueKind == JsonValueKind.String
                && IsDeclaredEnumValue(defaultValue.GetString()!, valueType),
            LauncherConfigurationValueKind.Integer =>
                defaultValue.ValueKind == JsonValueKind.Number && defaultValue.TryGetInt64(out _),
            LauncherConfigurationValueKind.Number =>
                defaultValue.ValueKind == JsonValueKind.Number,
            LauncherConfigurationValueKind.String or LauncherConfigurationValueKind.Keybinding =>
                defaultValue.ValueKind == JsonValueKind.String,
            LauncherConfigurationValueKind.Union =>
                defaultValue.ValueKind
                    is JsonValueKind.True
                    or JsonValueKind.False
                    or JsonValueKind.Object,
            _ => false,
        };

        if (!valid)
        {
            throw Invalid(
                $"Setting '{path}' has a default incompatible with value kind '{valueKind}'.");
        }
    }

    private static bool IsDeclaredEnumValue(string defaultValue, JsonElement valueType)
    {
        if (!valueType.TryGetProperty("values", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return values.EnumerateArray().Any(
            value =>
                value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), defaultValue, StringComparison.Ordinal));
    }

    private static LauncherConfigurationSourceId ParseSourceId(string value) =>
        value switch
        {
            "guffawaffle" => LauncherConfigurationSourceId.Guffawaffle,
            "netniv" => LauncherConfigurationSourceId.Netniv,
            _ => throw Invalid($"Unsupported configuration source '{value}'."),
        };

    private static string FormatSourceId(LauncherConfigurationSourceId value) =>
        value switch
        {
            LauncherConfigurationSourceId.Guffawaffle => "guffawaffle",
            LauncherConfigurationSourceId.Netniv => "netniv",
            _ => throw Invalid($"Unsupported configuration source enum '{value}'."),
        };

    private static LauncherConfigurationControl ParseControl(string value, string path) =>
        value switch
        {
            "scalar" => LauncherConfigurationControl.Scalar,
            "keybinding" => LauncherConfigurationControl.Keybinding,
            "notification-policy" => LauncherConfigurationControl.NotificationPolicy,
            _ => throw Invalid($"Setting '{path}' uses unsupported control '{value}'."),
        };

    private static LauncherConfigurationValueKind ParseValueKind(string value, string path) =>
        value switch
        {
            "boolean" => LauncherConfigurationValueKind.Boolean,
            "enum" => LauncherConfigurationValueKind.Enum,
            "integer" => LauncherConfigurationValueKind.Integer,
            "keybinding" => LauncherConfigurationValueKind.Keybinding,
            "number" => LauncherConfigurationValueKind.Number,
            "string" => LauncherConfigurationValueKind.String,
            "union" => LauncherConfigurationValueKind.Union,
            _ => throw Invalid($"Setting '{path}' uses unsupported value kind '{value}'."),
        };

    private static LauncherConfigurationStability ParseStability(string value, string path) =>
        value switch
        {
            "stable" => LauncherConfigurationStability.Stable,
            "advanced" => LauncherConfigurationStability.Advanced,
            "experimental" => LauncherConfigurationStability.Experimental,
            "internal" => LauncherConfigurationStability.Internal,
            _ => throw Invalid($"Setting '{path}' uses unsupported stability '{value}'."),
        };

    private static LauncherConfigurationSensitivity ParseSensitivity(string value, string path) =>
        value switch
        {
            "public" => LauncherConfigurationSensitivity.Public,
            "private" => LauncherConfigurationSensitivity.Private,
            "secret" => LauncherConfigurationSensitivity.Secret,
            _ => throw Invalid($"Setting '{path}' uses unsupported sensitivity '{value}'."),
        };

    private static LauncherConfigurationPlatform ParsePlatform(string value, string path) =>
        value switch
        {
            "windows" => LauncherConfigurationPlatform.Windows,
            "macos" => LauncherConfigurationPlatform.Macos,
            _ => throw Invalid($"Setting '{path}' uses unsupported platform '{value}'."),
        };

    private static JsonElement ReadRequiredProperty(
        JsonElement parent,
        string propertyName,
        string context)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            throw Invalid($"{context} is missing required property '{propertyName}'.");
        }

        return value;
    }

    private static string ReadRequiredString(
        JsonElement parent,
        string propertyName,
        string context)
    {
        var value = ReadRequiredProperty(parent, propertyName, context);
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid($"{context}.{propertyName} must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static string ReadArrayString(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid($"{context} entries must be non-empty strings.");
        }

        return value.GetString()!;
    }

    private static void RequireKind(
        JsonElement value,
        JsonValueKind expected,
        string context)
    {
        if (value.ValueKind != expected)
        {
            throw Invalid($"{context} must be a JSON {expected.ToString().ToLowerInvariant()}.");
        }
    }

    private static LauncherConfigurationSchemaException Invalid(string message) => new(message);
}
