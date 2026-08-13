using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherConfigurationCatalogApplicability(
    string ProviderId,
    string TrackId,
    string ReleaseVersion,
    string SourceCommit);

public static class LauncherConfigurationSchemaSetLoader
{
    private const int SupportedSchemaVersion = 1;
    private const int MaximumSettings = 512;
    private const int MaximumRevisions = 16;
    private const string SupportedSchemaId = "stfc-mod-bridge.versioned-config-schema-set";

    private sealed record ReviewedPresentationProfile(
        string SettingsLayoutId,
        IReadOnlyDictionary<string, JsonObject> Settings);

    public static bool IsSchemaSet(ReadOnlyMemory<byte> contents)
    {
        try
        {
            using var document = JsonDocument.Parse(contents);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaId", out var schemaId)
                && schemaId.ValueKind == JsonValueKind.String
                && string.Equals(schemaId.GetString(), SupportedSchemaId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static LauncherConfigurationCatalog Load(
        Stream stream,
        LauncherConfigurationCatalogApplicability applicability)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(applicability);
        ValidateApplicability(applicability);

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
            return Load(document.RootElement, applicability);
        }
        catch (LauncherConfigurationSchemaException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new LauncherConfigurationSchemaException(
                "The versioned configuration schema set is not valid JSON.",
                exception);
        }
    }

    private static LauncherConfigurationCatalog Load(
        JsonElement root,
        LauncherConfigurationCatalogApplicability applicability)
    {
        RequireObject(root, "schema set");
        RejectUnknown(
            root,
            "schema set",
            "schemaVersion",
            "schemaId",
            "provider",
            "featureGateSets",
            "settings",
            "presentation",
            "revisions");
        if (ReadInt32(root, "schemaVersion", "schema set") != SupportedSchemaVersion)
        {
            throw Invalid("The versioned configuration schema set version is unsupported.");
        }
        if (!string.Equals(ReadString(root, "schemaId", "schema set"), SupportedSchemaId, StringComparison.Ordinal))
        {
            throw Invalid("The versioned configuration schema set ID is unsupported.");
        }

        var provider = ReadObject(root, "provider", "schema set");
        RejectUnknown(provider, "schema-set provider", "id", "repository");
        var providerId = ReadString(provider, "id", "schema-set provider");
        if (!string.Equals(providerId, applicability.ProviderId, StringComparison.Ordinal))
        {
            throw Invalid(
                $"Configuration catalog provider '{providerId}' does not match requested provider "
                + $"'{applicability.ProviderId}'.");
        }
        var repository = ReadString(provider, "repository", "schema-set provider");

        var featureGateSets = ReadFeatureGateSets(
            ReadObject(root, "featureGateSets", "schema set"));
        var settingsElement = ReadArray(root, "settings", "schema set");
        if (settingsElement.GetArrayLength() is 0 or > MaximumSettings)
        {
            throw Invalid($"A schema set must contain between 1 and {MaximumSettings} shared settings.");
        }
        var settings = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var settingElement in settingsElement.EnumerateArray())
        {
            var setting = ReadCompactSetting(settingElement, featureGateSets);
            var path = setting["path"]!.GetValue<string>();
            if (!settings.TryAdd(path, setting))
            {
                throw Invalid($"Shared setting '{path}' is duplicated.");
            }
        }
        var presentation = root.TryGetProperty("presentation", out var presentationElement)
            ? ReadPresentationProfile(presentationElement, settings)
            : null;

        var revisions = ReadArray(root, "revisions", "schema set");
        if (revisions.GetArrayLength() is 0 or > MaximumRevisions)
        {
            throw Invalid($"A schema set must contain between 1 and {MaximumRevisions} revisions.");
        }
        var matches = revisions.EnumerateArray()
            .Where(revision => RevisionMatches(revision, applicability))
            .ToArray();
        if (matches.Length != 1)
        {
            throw Invalid(
                $"No unique reviewed configuration catalog applies to {applicability.ProviderId}/"
                + $"{applicability.TrackId} {applicability.ReleaseVersion} at {applicability.SourceCommit}.");
        }
        var selected = matches[0];
        RequireObject(selected, "catalog revision");
        RejectUnknown(
            selected,
            "catalog revision",
            "catalogId",
            "catalogVersion",
            "trackId",
            "releaseVersion",
            "sourceCommit",
            "removeSettings",
            "presentationSettingRemovals",
            "settingOverrides");

        ApplyRemovals(settings, selected);
        ApplyOverrides(settings, selected);
        presentation = ApplyPresentationRemovals(presentation, selected);
        ValidateMaterializedPresentation(settings, presentation);

        var legacyRoot = new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["schemaId"] = "stfc-community-mod.config-schema",
            ["source"] = new JsonObject
            {
                ["id"] = providerId,
                ["repository"] = repository,
            },
            ["identity"] = new JsonObject
            {
                ["catalogId"] = ReadString(selected, "catalogId", "catalog revision"),
                ["catalogVersion"] = ReadString(selected, "catalogVersion", "catalog revision"),
                ["trackId"] = applicability.TrackId,
                ["releaseVersion"] = applicability.ReleaseVersion,
                ["sourceCommit"] = applicability.SourceCommit.ToLowerInvariant(),
            },
            ["settings"] = new JsonArray(
                settings.Values
                    .OrderBy(setting => setting["path"]!.GetValue<string>(), StringComparer.Ordinal)
                    .Select(setting => ExpandSetting(
                        setting,
                        providerId,
                        presentation?.Settings.GetValueOrDefault(
                            setting["path"]!.GetValue<string>())))
                    .ToArray()),
        };

        using var legacyStream = new MemoryStream(
            Encoding.UTF8.GetBytes(legacyRoot.ToJsonString()));
        var catalog = LauncherConfigurationSchemaLoader.Load(legacyStream);
        return new LauncherConfigurationCatalog(
            catalog.SchemaVersion,
            catalog.Source,
            catalog.Settings,
            catalog.Identity,
            presentation?.SettingsLayoutId);
    }

    private static ReviewedPresentationProfile ReadPresentationProfile(
        JsonElement element,
        Dictionary<string, JsonObject> sharedSettings)
    {
        RequireObject(element, "schema-set presentation");
        RejectUnknown(
            element,
            "schema-set presentation",
            "settingsLayout",
            "settings");
        var settingsLayoutId = ReadString(
            element,
            "settingsLayout",
            "schema-set presentation");
        if (settingsLayoutId is not (
            LauncherFeatureImplementations.PrincipalCatalogSettingsLayout
            or LauncherFeatureImplementations.AlphabeticalSettingsLayout))
        {
            throw Invalid(
                $"Schema-set presentation selects unsupported Settings layout "
                + $"'{settingsLayoutId}'.");
        }

        var entries = ReadArray(element, "settings", "schema-set presentation");
        if (entries.GetArrayLength() is 0 or > MaximumSettings)
        {
            throw Invalid(
                $"Schema-set presentation must contain between 1 and {MaximumSettings} settings.");
        }

        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            RequireObject(entry, "presentation setting");
            RejectUnknown(
                entry,
                "presentation setting",
                "path",
                "label",
                "help",
                "group",
                "searchTerms",
                "unit",
                "family");
            var path = ReadString(entry, "path", "presentation setting");
            if (!sharedSettings.TryGetValue(path, out var setting))
            {
                throw Invalid($"Presentation setting '{path}' is not in the shared catalog.");
            }
            if (!IsDirectlyEditable(setting))
            {
                throw Invalid($"Presentation setting '{path}' is not directly player-editable.");
            }
            _ = ReadString(entry, "label", $"presentation setting '{path}'");
            _ = ReadString(entry, "help", $"presentation setting '{path}'");
            _ = ReadString(entry, "group", $"presentation setting '{path}'");
            if (!result.TryAdd(path, JsonNode.Parse(entry.GetRawText())!.AsObject()))
            {
                throw Invalid($"Presentation setting '{path}' is duplicated.");
            }
        }

        return new(settingsLayoutId, result);
    }

    private static void ValidateMaterializedPresentation(
        Dictionary<string, JsonObject> settings,
        ReviewedPresentationProfile? presentation)
    {
        if (presentation is null)
        {
            return;
        }

        var directlyEditable = settings.Values
            .Where(IsDirectlyEditable)
            .Select(setting => setting["path"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        var stale = presentation.Settings.Keys
            .Where(path => !directlyEditable.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (stale.Length > 0)
        {
            throw Invalid(
                "Reviewed presentation contains settings that are not materialized as directly player-editable: "
                + string.Join(", ", stale));
        }

        var missing = directlyEditable
            .Where(path => !presentation.Settings.ContainsKey(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw Invalid(
                "Reviewed presentation is missing directly player-editable settings: "
                + string.Join(", ", missing));
        }
    }

    private static ReviewedPresentationProfile? ApplyPresentationRemovals(
        ReviewedPresentationProfile? presentation,
        JsonElement revision)
    {
        var removals = ReadArray(
            revision,
            "presentationSettingRemovals",
            "catalog revision");
        if (presentation is null)
        {
            if (removals.GetArrayLength() > 0)
            {
                throw Invalid(
                    "Catalog revision presentation removals require a schema-set presentation profile.");
            }
            return null;
        }

        var settings = presentation.Settings.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var removal in removals.EnumerateArray())
        {
            if (removal.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(removal.GetString()))
            {
                throw Invalid(
                    "Catalog revision presentation removals must be non-empty setting paths.");
            }
            var path = removal.GetString()!;
            if (!seen.Add(path) || !settings.Remove(path))
            {
                throw Invalid(
                    $"Catalog revision presentation removal '{path}' is duplicated or unknown.");
            }
        }

        return new(presentation.SettingsLayoutId, settings);
    }

    private static bool IsDirectlyEditable(JsonObject setting)
    {
        var path = setting["path"]?.GetValue<string>() ?? string.Empty;
        var sensitivity = setting["sensitivity"]?.GetValue<string>();
        var stability = setting["stability"]?.GetValue<string>();
        var runtimeStatus = setting["runtimeStatus"]?.GetValue<string>();
        return !path.Split('.').Contains("*", StringComparer.Ordinal)
            && string.Equals(sensitivity, "public", StringComparison.Ordinal)
            && !string.Equals(stability, "internal", StringComparison.Ordinal)
            && runtimeStatus is "live" or "conditional";
    }

    private static Dictionary<string, JsonArray> ReadFeatureGateSets(JsonElement element)
    {
        var result = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name)
                || property.Value.ValueKind != JsonValueKind.Array)
            {
                throw Invalid("Feature-gate sets must map stable IDs to string arrays.");
            }
            var gates = new JsonArray();
            foreach (var gate in property.Value.EnumerateArray())
            {
                if (gate.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(gate.GetString()))
                {
                    throw Invalid($"Feature-gate set '{property.Name}' contains an invalid gate.");
                }
                gates.Add(gate.GetString());
            }
            if (!result.TryAdd(property.Name, gates))
            {
                throw Invalid($"Feature-gate set '{property.Name}' is duplicated.");
            }
        }
        return result;
    }

    private static JsonObject ReadCompactSetting(
        JsonElement element,
        Dictionary<string, JsonArray> featureGateSets)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 9)
        {
            throw Invalid("Each compact setting must contain exactly nine fields.");
        }
        var fields = element.EnumerateArray().ToArray();
        var path = ReadArrayString(fields[0], "setting path");
        var kind = ReadArrayString(fields[1], $"kind for '{path}'");
        var runtimeStatus = ReadArrayString(fields[3], $"runtime status for '{path}'");
        var platformCode = ReadArrayString(fields[4], $"platforms for '{path}'");
        var sensitivity = ReadArrayString(fields[5], $"sensitivity for '{path}'");
        var stability = ReadArrayString(fields[6], $"stability for '{path}'");
        var gateSetId = ReadArrayString(fields[7], $"feature-gate set for '{path}'");
        if (!featureGateSets.TryGetValue(gateSetId, out var gates))
        {
            throw Invalid($"Setting '{path}' references unknown feature-gate set '{gateSetId}'.");
        }
        if (fields[8].ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"Aliases for '{path}' must be an array.");
        }
        var aliases = new JsonArray();
        foreach (var alias in fields[8].EnumerateArray())
        {
            aliases.Add(ReadArrayString(alias, $"aliases for '{path}'"));
        }
        var platforms = platformCode switch
        {
            "all" => new JsonArray("windows", "macos"),
            "windows" => new JsonArray("windows"),
            _ => throw Invalid($"Setting '{path}' has unsupported platform code '{platformCode}'."),
        };
        return new JsonObject
        {
            ["path"] = path,
            ["kind"] = kind,
            ["default"] = JsonNode.Parse(fields[2].GetRawText()),
            ["description"] = $"NetniV runtime contract for '{path}'.",
            ["runtimeStatus"] = runtimeStatus,
            ["platforms"] = platforms,
            ["sensitivity"] = sensitivity,
            ["stability"] = stability,
            ["featureGates"] = gates.DeepClone(),
            ["aliases"] = aliases,
            ["defaultSource"] = $"reviewed-provider-catalog:{path}",
        };
    }

    private static void ApplyRemovals(
        Dictionary<string, JsonObject> settings,
        JsonElement revision)
    {
        var removals = ReadArray(revision, "removeSettings", "catalog revision");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var removal in removals.EnumerateArray())
        {
            if (removal.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(removal.GetString()))
            {
                throw Invalid("Catalog revision removals must be non-empty setting paths.");
            }
            var path = removal.GetString()!;
            if (!seen.Add(path) || !settings.Remove(path))
            {
                throw Invalid($"Catalog revision removal '{path}' is duplicated or unknown.");
            }
        }
    }

    private static void ApplyOverrides(
        Dictionary<string, JsonObject> settings,
        JsonElement revision)
    {
        var overrides = ReadArray(revision, "settingOverrides", "catalog revision");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var overrideElement in overrides.EnumerateArray())
        {
            RequireObject(overrideElement, "setting override");
            var path = ReadString(overrideElement, "path", "setting override");
            if (!seen.Add(path) || !settings.TryGetValue(path, out var setting))
            {
                throw Invalid($"Catalog revision override '{path}' is duplicated or unknown.");
            }
            foreach (var property in overrideElement.EnumerateObject())
            {
                if (property.NameEquals("path"))
                {
                    continue;
                }
                if (property.Name is not (
                    "description"
                    or "presentationHelp"
                    or "runtimeStatus"
                    or "featureGates"
                    or "default"))
                {
                    throw Invalid($"Setting override '{path}' contains unsupported property '{property.Name}'.");
                }
                if (property.NameEquals("presentationHelp")
                    && (property.Value.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(property.Value.GetString())))
                {
                    throw Invalid(
                        $"Setting override '{path}' presentationHelp must be a non-empty string.");
                }
                setting[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }
    }

    private static JsonObject ExpandSetting(
        JsonObject compact,
        string providerId,
        JsonObject? reviewedPresentation)
    {
        var path = compact["path"]?.GetValue<string>()
            ?? throw Invalid("A compact setting is missing its path.");
        var kind = compact["kind"]?.GetValue<string>()
            ?? throw Invalid($"Setting '{path}' is missing its value kind.");
        var runtimeDescription = compact["description"]?.GetValue<string>()
            ?? throw Invalid($"Setting '{path}' is missing its description.");
        var label = reviewedPresentation?["label"]?.GetValue<string>() ?? Label(path);
        var description = compact["presentationHelp"]?.GetValue<string>()
            ?? reviewedPresentation?["help"]?.GetValue<string>()
            ?? runtimeDescription;
        var category = path.Split('.')[0];
        var group = reviewedPresentation?["group"]?.GetValue<string>() ?? Label(category);
        var apply = compact["apply"]?.GetValue<string>() ?? "next-session";
        var runtimeStatus = compact["runtimeStatus"]?.GetValue<string>() ?? "live";
        var featureGates = compact["featureGates"]?.DeepClone() ?? new JsonArray();
        var platforms = compact["platforms"]?.DeepClone()
            ?? new JsonArray("windows", "macos");
        var sensitivity = compact["sensitivity"]?.GetValue<string>() ?? "public";
        var stability = compact["stability"]?.GetValue<string>() ?? "stable";
        var defaultValue = compact["default"]?.DeepClone()
            ?? throw Invalid($"Setting '{path}' is missing its runtime default.");
        var aliases = ExpandAliases(compact["aliases"] as JsonArray);
        var valueType = new JsonObject { ["kind"] = kind };
        if (kind == "keybinding")
        {
            valueType["multiple"] = true;
            valueType["unbound"] = "NONE";
            valueType["triggerMode"] = "Down";
            valueType["inputPhase"] = "legacy-input";
            valueType["inputLayer"] = "netniv-hotkeys";
            valueType["conflictGroup"] = "netniv-shortcuts";
            valueType["actionCategory"] = category;
        }

        var applyTiming = apply switch
        {
            "live" => "Immediate",
            "restart-required" => "Restart required",
            _ => "Next launch",
        };
        var searchTerms = BuildSearchTerms(
            path,
            label,
            category,
            group,
            reviewedPresentation?["searchTerms"] as JsonArray);
        var presentation = new JsonObject
        {
            ["label"] = label,
            ["help"] = description,
            ["group"] = group,
            ["searchTerms"] = searchTerms,
            ["editorWidth"] = kind is "string" or "keybinding" ? "wide"
                : kind is "integer" or "number" ? "compact"
                : "standard",
            ["applyTiming"] = applyTiming,
            ["accessibleName"] = label,
            ["accessibleHelp"] = $"{description.TrimEnd().TrimEnd('.')} Applies: {applyTiming}.",
        };
        if (reviewedPresentation?["unit"] is { } unit)
        {
            presentation["unit"] = unit.DeepClone();
        }
        if (reviewedPresentation?["family"] is { } family)
        {
            presentation["family"] = family.DeepClone();
        }

        return new JsonObject
        {
            ["path"] = path,
            ["title"] = label,
            ["description"] = description,
            ["category"] = category,
            ["control"] = kind == "keybinding" ? "keybinding" : "scalar",
            ["valueType"] = valueType,
            ["default"] = defaultValue,
            ["platforms"] = platforms,
            ["apply"] = apply,
            ["sensitivity"] = sensitivity,
            ["stability"] = stability,
            ["sourceSupport"] = new JsonArray(providerId),
            ["aliases"] = aliases,
            ["provenance"] = new JsonObject
            {
                ["runtimePath"] = path,
                ["defaultSource"] = compact["defaultSource"]?.GetValue<string>()
                    ?? $"netniv/{path}",
            },
            ["runtimeStatus"] = runtimeStatus,
            ["featureGates"] = featureGates,
            ["presentation"] = presentation,
        };
    }

    private static JsonArray BuildSearchTerms(
        string path,
        string label,
        string category,
        string group,
        JsonArray? reviewedTerms)
    {
        var result = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in new[] { path, label, category, group })
        {
            if (seen.Add(term))
            {
                result.Add(term);
            }
        }
        foreach (var node in reviewedTerms ?? [])
        {
            if (node is not JsonValue value
                || !value.TryGetValue<string>(out var term))
            {
                throw Invalid($"Presentation search terms for '{path}' must be strings.");
            }
            if (string.IsNullOrWhiteSpace(term))
            {
                throw Invalid($"Presentation search terms for '{path}' must not be empty.");
            }
            if (seen.Add(term))
            {
                result.Add(term);
            }
        }
        return result;
    }

    private static JsonArray ExpandAliases(JsonArray? compactAliases)
    {
        var aliases = new JsonArray();
        foreach (var node in compactAliases ?? [])
        {
            var path = node?.GetValue<string>()
                ?? throw Invalid("Setting aliases must be non-empty paths.");
            aliases.Add(
                new JsonObject
                {
                    ["path"] = path,
                    ["status"] = "deprecated",
                    ["precedence"] = "canonical-wins",
                });
        }
        return aliases;
    }

    private static bool RevisionMatches(
        JsonElement revision,
        LauncherConfigurationCatalogApplicability applicability)
    {
        RequireObject(revision, "catalog revision");
        return string.Equals(ReadString(revision, "trackId", "catalog revision"), applicability.TrackId, StringComparison.Ordinal)
            && string.Equals(ReadString(revision, "releaseVersion", "catalog revision"), applicability.ReleaseVersion, StringComparison.Ordinal)
            && string.Equals(ReadString(revision, "sourceCommit", "catalog revision"), applicability.SourceCommit, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateApplicability(LauncherConfigurationCatalogApplicability applicability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicability.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicability.TrackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicability.ReleaseVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicability.SourceCommit);
        if (applicability.SourceCommit.Length != 40
            || applicability.SourceCommit.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Catalog applicability requires a full hexadecimal Git commit SHA.",
                nameof(applicability));
        }
    }

    private static string Label(string value)
    {
        var leaf = value[(value.LastIndexOf('.') + 1)..].Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(leaf);
    }

    private static string ReadArrayString(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid($"{context} must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static JsonElement ReadObject(JsonElement parent, string property, string context)
    {
        var value = ReadRequired(parent, property, context);
        RequireObject(value, $"{context}.{property}");
        return value;
    }

    private static JsonElement ReadArray(JsonElement parent, string property, string context)
    {
        var value = ReadRequired(parent, property, context);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{context}.{property} must be an array.");
        }
        return value;
    }

    private static string ReadString(JsonElement parent, string property, string context)
    {
        var value = ReadRequired(parent, property, context);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid($"{context}.{property} must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static int ReadInt32(JsonElement parent, string property, string context)
    {
        var value = ReadRequired(parent, property, context);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw Invalid($"{context}.{property} must be an integer.");
        }
        return result;
    }

    private static JsonElement ReadRequired(JsonElement parent, string property, string context) =>
        parent.TryGetProperty(property, out var value)
            ? value
            : throw Invalid($"{context} is missing required property '{property}'.");

    private static void RequireObject(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{context} must be an object.");
        }
    }

    private static void RejectUnknown(JsonElement value, string context, params string[] supported)
    {
        var names = new HashSet<string>(supported, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Contains(property.Name))
            {
                throw Invalid($"{context} contains unsupported property '{property.Name}'.");
            }
        }
    }

    private static LauncherConfigurationSchemaException Invalid(string message) => new(message);
}
