using System.Text.Json;
using System.Text.Json.Serialization;

namespace STFCCommunityMod.Launcher.Core;

public enum ConfigurationEffectiveExportState
{
    Succeeded,
    Unavailable,
    Invalid,
}

public sealed record ConfigurationEffectiveExportEntry(
    string Path,
    string Origin,
    LauncherConfigurationSensitivity Sensitivity,
    LauncherConfigurationRuntimeStatus? RuntimeStatus,
    IReadOnlyList<string> FeatureGates,
    string RenderedTomlValue,
    bool CatalogKnown);

public sealed record ConfigurationEffectiveExportDocument(
    string Format,
    string Warning,
    string ProviderId,
    string ChannelId,
    string CatalogId,
    string CatalogVersion,
    string SourceRevisionSha256,
    IReadOnlyList<ConfigurationEffectiveExportEntry> Entries);

public sealed record ConfigurationEffectiveExportResult(
    ConfigurationEffectiveExportState State,
    ConfigurationEffectiveExportDocument? Document = null,
    string? Error = null)
{
    public bool IsSuccess => State == ConfigurationEffectiveExportState.Succeeded && Document is not null;
}

/// <summary>
/// Builds an intentionally unredacted local export of the effective values established by
/// the selected provider catalog. This is separate from support diagnostics by design.
/// </summary>
public sealed class ConfigurationEffectiveExportService
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    private const string ExportWarning =
        "This local file is intentionally unredacted and may contain credentials, private endpoints, paths, and other secrets. Do not attach it to support requests.";

    public static ConfigurationEffectiveExportResult Build(
        ConfigurationDocumentSnapshot snapshot,
        LauncherConfigurationDiagnosisEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(evidence);

        var catalog = evidence.Catalog;
        if (catalog is null)
        {
            return new(
                ConfigurationEffectiveExportState.Unavailable,
                Error: "The selected provider has no verified configuration catalog, so effective values cannot be established.");
        }

        var load = SparseTomlDocument.Load(snapshot.Contents, out var sparse);
        if (!load.IsValid || sparse is null)
        {
            return Invalid(load.Error?.Message ?? "The configuration document could not be read safely.");
        }

        var read = sparse.ReadOverrides();
        if (!read.IsValid || read.Overrides is null)
        {
            return Invalid(read.Error?.Message ?? "The configuration overrides could not be read safely.");
        }

        var entries = new List<ConfigurationEffectiveExportEntry>();
        var recognized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in catalog.Settings.Where(item => !item.IsTemplate))
        {
            if (!TryResolve(setting, read.Overrides, recognized, out var rendered, out var origin, out var error))
            {
                return Invalid(error!);
            }

            entries.Add(
                new(
                    setting.Path,
                    origin!,
                    setting.Sensitivity,
                    setting.RuntimeStatus,
                    setting.FeatureGates,
                    rendered!,
                    true));
        }

        entries.AddRange(
            read.Overrides.Values
                .Where(item => !recognized.Contains(item.CanonicalPath))
                .OrderBy(item => item.CanonicalPath, StringComparer.Ordinal)
                .Select(item => new ConfigurationEffectiveExportEntry(
                    item.CanonicalPath,
                    "preserved-unknown-override",
                    LauncherConfigurationSensitivity.Private,
                    null,
                    [],
                    item.RenderedValue,
                    false)));

        return new(
            ConfigurationEffectiveExportState.Succeeded,
            new(
                "stfc-mod-bridge-effective-configuration-v1",
                ExportWarning,
                evidence.ProviderId,
                evidence.ChannelId,
                catalog.Identity.CatalogId,
                catalog.Identity.CatalogVersion.ToString(),
                snapshot.Revision.Sha256,
                entries.AsReadOnly()));
    }

    public static async Task ExportAsync(
        ConfigurationEffectiveExportDocument document,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The effective-configuration export path has no parent directory.");
        Directory.CreateDirectory(directory);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            useAsync: true);
        await JsonSerializer.SerializeAsync(
            stream,
            document,
            ExportJsonOptions,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static bool TryResolve(
        LauncherConfigurationSetting setting,
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        HashSet<string> recognized,
        out string? rendered,
        out string? origin,
        out string? error)
    {
        rendered = null;
        origin = null;
        error = null;
        if (overrides.TryGetValue(setting.Path, out var canonical))
        {
            if (LauncherConfigurationEditSession.ValidateSettingValue(setting, canonical.RenderedValue) is not null)
            {
                error = $"Effective value for '{setting.Path}' cannot be established because its canonical override is invalid.";
                return false;
            }
            recognized.Add(canonical.CanonicalPath);
            foreach (var alias in setting.Aliases)
            {
                if (overrides.TryGetValue(alias.Path, out var shadowed))
                {
                    recognized.Add(shadowed.CanonicalPath);
                }
            }
            rendered = canonical.RenderedValue;
            origin = "canonical-override";
            return true;
        }

        var aliases = setting.Aliases
            .Select(alias => overrides.TryGetValue(alias.Path, out var value) ? value : null)
            .Where(value => value is not null)
            .Cast<SparseTomlOverride>()
            .ToArray();
        foreach (var alias in aliases)
        {
            recognized.Add(alias.CanonicalPath);
        }
        if (aliases.Length > 0)
        {
            var first = aliases[0];
            var validation = LauncherConfigurationEditSession.ValidateSettingValue(setting, first.RenderedValue);
            if (validation is not null
                || aliases.Skip(1).Any(alias =>
                    LauncherConfigurationEditSession.ValidateSettingValue(setting, alias.RenderedValue) is not null
                    || !LauncherConfigurationEditSession.AreEquivalentSettingValues(
                        setting,
                        first.RenderedValue,
                        alias.RenderedValue)))
            {
                error = $"Effective value for '{setting.Path}' is ambiguous because its compatibility aliases conflict or are invalid.";
                return false;
            }

            rendered = first.RenderedValue;
            origin = aliases.Length == 1
                ? $"compatibility-alias:{first.CanonicalPath}"
                : "equivalent-compatibility-aliases";
            return true;
        }

        if (!TryRenderDefault(setting.DefaultValue, out rendered))
        {
            error = $"Provider default for '{setting.Path}' cannot be represented in the effective export.";
            return false;
        }
        origin = "provider-default";
        return true;
    }

    private static bool TryRenderDefault(JsonElement value, out string rendered)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                rendered = "true";
                return true;
            case JsonValueKind.False:
                rendered = "false";
                return true;
            case JsonValueKind.String:
                rendered = LauncherTomlValue.RenderString(value.GetString() ?? string.Empty);
                return true;
            case JsonValueKind.Number:
                rendered = value.GetRawText();
                return true;
            case JsonValueKind.Array:
                var values = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    if (!TryRenderDefault(item, out var child))
                    {
                        rendered = string.Empty;
                        return false;
                    }
                    values.Add(child);
                }
                rendered = $"[{string.Join(", ", values)}]";
                return true;
            case JsonValueKind.Object:
                var fields = new List<string>();
                foreach (var property in value.EnumerateObject())
                {
                    if (!TryRenderDefault(property.Value, out var child))
                    {
                        rendered = string.Empty;
                        return false;
                    }
                    fields.Add($"{property.Name} = {child}");
                }
                rendered = $"{{ {string.Join(", ", fields)} }}";
                return true;
            default:
                rendered = string.Empty;
                return false;
        }
    }

    private static ConfigurationEffectiveExportResult Invalid(string error) =>
        new(ConfigurationEffectiveExportState.Invalid, Error: error);
}
