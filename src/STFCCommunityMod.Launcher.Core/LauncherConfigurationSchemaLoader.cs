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
        ValidateStringFormat(valueType, valueKind, path);
        var numericConstraints = ReadNumericConstraints(element, valueKind, path);
        var keybindingMetadata = ReadKeybindingMetadata(valueType, valueKind, path);

        var defaultValue = ReadRequiredProperty(element, "default", context);
        ValidateDefault(defaultValue, valueKind, valueType, path);
        ValidateDefaultConstraints(defaultValue, numericConstraints, path);
        ValidateKeybindingDefault(defaultValue, keybindingMetadata, path);

        var stability = ParseStability(ReadRequiredString(element, "stability", context), path);
        var sensitivity = ParseSensitivity(ReadRequiredString(element, "sensitivity", context), path);
        var applyBehavior = ParseApplyBehavior(
            ReadRequiredString(element, "apply", context),
            path);
        var presentation = ReadPresentation(
            ReadRequiredProperty(element, "presentation", context),
            control,
            valueKind,
            valueType,
            applyBehavior,
            path);
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
            numericConstraints,
            keybindingMetadata,
            defaultValue.Clone(),
            stability,
            platforms,
            sourceSupport,
            sensitivity,
            applyBehavior,
            presentation);
    }

    private static LauncherConfigurationPresentation ReadPresentation(
        JsonElement element,
        LauncherConfigurationControl control,
        LauncherConfigurationValueKind valueKind,
        JsonElement valueType,
        LauncherConfigurationApplyBehavior applyBehavior,
        string path)
    {
        var context = $"presentation for '{path}'";
        RequireKind(element, JsonValueKind.Object, context);
        RejectUnknownProperties(
            element,
            context,
            "label",
            "help",
            "group",
            "searchTerms",
            "enumOptions",
            "unit",
            "editorWidth",
            "applyTiming",
            "accessibleName",
            "accessibleHelp");

        var label = ReadRequiredString(element, "label", context);
        var help = ReadOptionalString(element, "help", context);
        var group = ReadRequiredString(element, "group", context);
        var searchTerms = ReadDistinctStrings(
            ReadRequiredProperty(element, "searchTerms", context),
            $"{context}.searchTerms");
        if (!searchTerms.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            throw Invalid($"{context}.searchTerms must include the canonical setting path.");
        }

        var enumOptions = ReadPresentationEnumOptions(
            element,
            valueKind,
            valueType,
            context);
        var unit = ReadOptionalString(element, "unit", context);
        if (unit is not null
            && (control != LauncherConfigurationControl.Scalar
                || valueKind is not LauncherConfigurationValueKind.Integer
                    and not LauncherConfigurationValueKind.Number))
        {
            throw Invalid($"{context}.unit is only valid for numeric scalar settings.");
        }

        var editorWidth = ParseEditorWidth(
            ReadRequiredString(element, "editorWidth", context),
            path);
        var applyTiming = ReadRequiredString(element, "applyTiming", context);
        var expectedApplyTiming = LauncherConfigurationPresentation.ApplyTimingFor(applyBehavior);
        if (!string.Equals(applyTiming, expectedApplyTiming, StringComparison.Ordinal))
        {
            throw Invalid(
                $"{context}.applyTiming must be '{expectedApplyTiming}' for apply behavior "
                + $"'{LauncherConfigurationPresentation.ApplyTokenFor(applyBehavior)}'.");
        }

        return new(
            label,
            help,
            group,
            searchTerms,
            enumOptions,
            unit,
            editorWidth,
            applyTiming,
            ReadRequiredString(element, "accessibleName", context),
            ReadRequiredString(element, "accessibleHelp", context));
    }

    private static ReadOnlyCollection<LauncherConfigurationPresentationOption> ReadPresentationEnumOptions(
        JsonElement presentation,
        LauncherConfigurationValueKind valueKind,
        JsonElement valueType,
        string context)
    {
        if (!presentation.TryGetProperty("enumOptions", out var element))
        {
            return Array.AsReadOnly(Array.Empty<LauncherConfigurationPresentationOption>());
        }

        if (valueKind != LauncherConfigurationValueKind.Enum)
        {
            throw Invalid($"{context}.enumOptions is only valid for enum settings.");
        }

        RequireKind(element, JsonValueKind.Array, $"{context}.enumOptions");
        var options = new List<LauncherConfigurationPresentationOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in element.EnumerateArray())
        {
            RequireKind(option, JsonValueKind.Object, $"{context}.enumOptions item");
            RejectUnknownProperties(
                option,
                $"{context}.enumOptions item",
                "value",
                "label",
                "help");
            var value = ReadRequiredString(option, "value", $"{context}.enumOptions item");
            if (!seen.Add(value))
            {
                throw Invalid($"{context}.enumOptions contains duplicate value '{value}'.");
            }

            options.Add(
                new(
                    value,
                    ReadRequiredString(option, "label", $"{context}.enumOptions item"),
                    ReadOptionalString(option, "help", $"{context}.enumOptions item")));
        }

        var declaredValues = valueType.GetProperty("values")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        if (options.Count != declaredValues.Length
            || declaredValues.Any(value => !seen.Contains(value)))
        {
            throw Invalid($"{context}.enumOptions must describe every declared enum value exactly once.");
        }

        return options.AsReadOnly();
    }

    private static LauncherConfigurationKeybindingMetadata? ReadKeybindingMetadata(
        JsonElement valueType,
        LauncherConfigurationValueKind valueKind,
        string path)
    {
        if (valueKind != LauncherConfigurationValueKind.Keybinding)
        {
            return null;
        }

        if (!valueType.TryGetProperty("multiple", out var multiple)
            || multiple.ValueKind is not JsonValueKind.True)
        {
            throw Invalid($"Keybinding setting '{path}' must support multiple alternatives.");
        }

        var unbound = ReadRequiredString(valueType, "unbound", $"keybinding setting '{path}'");
        if (!string.Equals(unbound, "NONE", StringComparison.Ordinal))
        {
            throw Invalid($"Keybinding setting '{path}' declares unsupported unbound value '{unbound}'.");
        }

        var triggerMode = ReadRequiredString(valueType, "triggerMode", $"keybinding setting '{path}'");
        if (triggerMode is not ("Down" or "Pressed"))
        {
            throw Invalid($"Keybinding setting '{path}' declares unsupported trigger mode '{triggerMode}'.");
        }

        return new(
            triggerMode,
            ReadRequiredString(valueType, "inputPhase", $"keybinding setting '{path}'"),
            ReadRequiredString(valueType, "inputLayer", $"keybinding setting '{path}'"),
            ReadRequiredString(valueType, "conflictGroup", $"keybinding setting '{path}'"),
            ReadRequiredString(valueType, "actionCategory", $"keybinding setting '{path}'"));
    }

    private static void ValidateKeybindingDefault(
        JsonElement defaultValue,
        LauncherConfigurationKeybindingMetadata? metadata,
        string path)
    {
        if (metadata is null)
        {
            return;
        }

        var parsed = LauncherKeybindingValue.Parse(defaultValue.GetString()!);
        if (!parsed.IsValid)
        {
            throw Invalid($"Keybinding setting '{path}' has an invalid default: {parsed.Error}");
        }
    }

    private static void ValidateStringFormat(
        JsonElement valueType,
        LauncherConfigurationValueKind valueKind,
        string path)
    {
        if (!valueType.TryGetProperty("format", out var format))
        {
            return;
        }

        if (valueKind != LauncherConfigurationValueKind.String
            || format.ValueKind != JsonValueKind.String
            || format.GetString() is not ("uri" or "comma-separated-list"))
        {
            throw Invalid($"Setting '{path}' declares an unsupported string format.");
        }
    }

    private static LauncherConfigurationNumericConstraints? ReadNumericConstraints(
        JsonElement setting,
        LauncherConfigurationValueKind valueKind,
        string path)
    {
        if (!setting.TryGetProperty("constraints", out var constraints))
        {
            return null;
        }

        if (valueKind is not LauncherConfigurationValueKind.Integer
            and not LauncherConfigurationValueKind.Number)
        {
            throw Invalid($"Setting '{path}' declares numeric constraints for a non-numeric value.");
        }

        RequireKind(constraints, JsonValueKind.Object, $"constraints for '{path}'");
        var minimum = ReadOptionalFiniteNumber(constraints, "minimum", path);
        var maximum = ReadOptionalFiniteNumber(constraints, "maximum", path);
        if (!minimum.HasValue && !maximum.HasValue)
        {
            throw Invalid($"Setting '{path}' has an empty numeric constraints object.");
        }

        if (valueKind == LauncherConfigurationValueKind.Integer
            && ((minimum.HasValue
                 && (minimum.Value != Math.Truncate(minimum.Value)
                     || minimum.Value < long.MinValue
                     || minimum.Value > long.MaxValue))
                || (maximum.HasValue
                    && (maximum.Value != Math.Truncate(maximum.Value)
                        || maximum.Value < long.MinValue
                        || maximum.Value > long.MaxValue))))
        {
            throw Invalid($"Integer setting '{path}' must use signed 64-bit integer constraints.");
        }

        if (minimum > maximum)
        {
            throw Invalid($"Setting '{path}' has a minimum greater than its maximum.");
        }

        return new(minimum, maximum);
    }

    private static double? ReadOptionalFiniteNumber(
        JsonElement constraints,
        string propertyName,
        string path)
    {
        if (!constraints.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number))
        {
            throw Invalid($"Constraint '{propertyName}' for '{path}' must be a finite number.");
        }

        return number;
    }

    private static void ValidateDefaultConstraints(
        JsonElement defaultValue,
        LauncherConfigurationNumericConstraints? constraints,
        string path)
    {
        if (constraints is null)
        {
            return;
        }

        var value = defaultValue.GetDouble();
        if (!constraints.Contains(value))
        {
            throw Invalid($"Default for setting '{path}' falls outside its numeric constraints.");
        }
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

    private static LauncherConfigurationApplyBehavior ParseApplyBehavior(
        string value,
        string path) =>
        value switch
        {
            "live" => LauncherConfigurationApplyBehavior.Live,
            "next-session" => LauncherConfigurationApplyBehavior.NextSession,
            "restart-required" => LauncherConfigurationApplyBehavior.RestartRequired,
            _ => throw Invalid($"Setting '{path}' uses unsupported apply behavior '{value}'."),
        };

    private static LauncherConfigurationEditorWidth ParseEditorWidth(
        string value,
        string path) =>
        value switch
        {
            "compact" => LauncherConfigurationEditorWidth.Compact,
            "standard" => LauncherConfigurationEditorWidth.Standard,
            "wide" => LauncherConfigurationEditorWidth.Wide,
            _ => throw Invalid($"Setting '{path}' uses unsupported presentation editor width '{value}'."),
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

    private static string? ReadOptionalString(
        JsonElement parent,
        string propertyName,
        string context)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid($"{context}.{propertyName} must be a non-empty string when present.");
        }

        return value.GetString();
    }

    private static ReadOnlyCollection<string> ReadDistinctStrings(
        JsonElement element,
        string context)
    {
        RequireKind(element, JsonValueKind.Array, context);
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var elementValue in element.EnumerateArray())
        {
            var value = ReadArrayString(elementValue, context);
            if (!seen.Add(value))
            {
                throw Invalid($"{context} contains duplicate value '{value}'.");
            }
            values.Add(value);
        }

        if (values.Count == 0)
        {
            throw Invalid($"{context} must contain at least one value.");
        }

        return Array.AsReadOnly(values.ToArray());
    }

    private static void RejectUnknownProperties(
        JsonElement element,
        string context,
        params string[] supportedProperties)
    {
        var supported = new HashSet<string>(supportedProperties, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!supported.Contains(property.Name))
            {
                throw Invalid($"{context} contains unsupported property '{property.Name}'.");
            }
        }
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
