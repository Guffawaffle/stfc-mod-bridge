using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherConfigurationEntryState(
    object? DefaultValue,
    string? RenderedOverride,
    bool HasOverride,
    bool IsStaged,
    bool IsRemoval)
{
    public string? EffectiveRenderedValue =>
        IsRemoval ? null : RenderedOverride;
}

public sealed class LauncherConfigurationEditSession
{
    private readonly LauncherConfigurationCatalog catalog;
    private readonly ReadOnlyDictionary<string, LauncherConfigurationSetting> settingsByPath;
    private readonly Dictionary<string, StagedChange> stagedChanges =
        new(StringComparer.OrdinalIgnoreCase);
    private byte[] baselineContents;
    private IReadOnlyDictionary<string, SparseTomlOverride> baselineOverrides;

    private LauncherConfigurationEditSession(
        LauncherConfigurationCatalog catalog,
        byte[] baselineContents,
        IReadOnlyDictionary<string, SparseTomlOverride> baselineOverrides)
    {
        this.catalog = catalog;
        this.baselineContents = [.. baselineContents];
        this.baselineOverrides = baselineOverrides;
        settingsByPath = new ReadOnlyDictionary<string, LauncherConfigurationSetting>(
            catalog.Settings.ToDictionary(setting => setting.Path, StringComparer.OrdinalIgnoreCase));
    }

    public int PendingChangeCount => stagedChanges.Count;

    public bool HasPendingChanges => stagedChanges.Count > 0;

    public static SparseTomlEditResult Load(
        byte[] contents,
        LauncherConfigurationCatalog catalog,
        out LauncherConfigurationEditSession? session)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(catalog);
        session = null;

        var load = SparseTomlDocument.Load(contents, out var document);
        if (!load.IsValid || document is null)
        {
            return load;
        }

        var validation = document.ValidateForMutation();
        if (!validation.IsValid)
        {
            return validation;
        }

        var read = document.ReadOverrides();
        if (!read.IsValid || read.Overrides is null)
        {
            return SparseTomlEditResult.Invalid(
                read.Error
                ?? new SparseTomlError(
                    SparseTomlErrorCode.UnsupportedDocument,
                    "The configuration overrides could not be read."));
        }

        session = new(catalog, contents, read.Overrides);
        return SparseTomlEditResult.Unchanged([.. contents]);
    }

    public LauncherConfigurationEntryState GetState(LauncherConfigurationSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        EnsureKnownSetting(setting.Path);

        baselineOverrides.TryGetValue(setting.Path, out var baseline);
        if (!stagedChanges.TryGetValue(setting.Path, out var staged))
        {
            return new(
                ConvertJsonValue(setting.DefaultValue),
                baseline?.RenderedValue,
                baseline is not null,
                false,
                false);
        }

        return new(
            ConvertJsonValue(setting.DefaultValue),
            staged.IsRemoval ? null : staged.RenderedValue,
            !staged.IsRemoval,
            true,
            staged.IsRemoval);
    }

    public SparseTomlEditResult StageSet(
        LauncherConfigurationSetting setting,
        string renderedTomlValue)
    {
        ArgumentNullException.ThrowIfNull(setting);
        EnsureEditableSetting(setting);

        var valueError = ValidateSettingValue(setting, renderedTomlValue);
        if (valueError is not null)
        {
            return SparseTomlEditResult.Invalid(valueError);
        }

        var hadPreviousChange = stagedChanges.TryGetValue(setting.Path, out var previousChange);
        if (baselineOverrides.TryGetValue(setting.Path, out var baseline)
            && string.Equals(
                baseline.RenderedValue,
                renderedTomlValue,
                StringComparison.Ordinal))
        {
            stagedChanges.Remove(setting.Path);
        }
        else
        {
            stagedChanges[setting.Path] = new(renderedTomlValue, false);
        }

        var draft = BuildDraft();
        if (!draft.IsValid)
        {
            RestorePreviousChange(setting.Path, hadPreviousChange, previousChange);
        }

        return draft;
    }

    public SparseTomlEditResult StageRemove(LauncherConfigurationSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        EnsureEditableSetting(setting);

        var hadPreviousChange = stagedChanges.TryGetValue(setting.Path, out var previousChange);
        if (!baselineOverrides.ContainsKey(setting.Path))
        {
            stagedChanges.Remove(setting.Path);
        }
        else
        {
            stagedChanges[setting.Path] = new(null, true);
        }

        var draft = BuildDraft();
        if (!draft.IsValid)
        {
            RestorePreviousChange(setting.Path, hadPreviousChange, previousChange);
        }

        return draft;
    }

    public void Discard() => stagedChanges.Clear();

    public SparseTomlEditResult BuildDraft()
    {
        var contents = baselineContents;
        var changed = false;
        foreach (var (path, staged) in stagedChanges)
        {
            var load = SparseTomlDocument.Load(contents, out var document);
            if (!load.IsValid || document is null)
            {
                return load;
            }

            var edit = staged.IsRemoval
                ? document.RemoveOverride(path)
                : document.SetOverride(path, staged.RenderedValue!);
            if (!edit.IsValid || edit.Contents is null)
            {
                return edit;
            }

            contents = edit.Contents;
            changed |= edit.Changed;
        }

        return changed
            ? SparseTomlEditResult.Updated(contents)
            : SparseTomlEditResult.Unchanged([.. baselineContents]);
    }

    public async Task<AtomicTomlWriteResult> SaveAsync(
        string? configurationPath,
        AtomicTomlStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var draft = BuildDraft();
        if (!draft.IsValid || draft.Contents is null)
        {
            return new(
                AtomicTomlWriteState.Invalid,
                ValidationError: draft.Error);
        }

        if (!draft.Changed)
        {
            stagedChanges.Clear();
            return new(AtomicTomlWriteState.NoChange);
        }

        var result = await store.SaveDocumentAsync(
            configurationPath,
            baselineContents,
            draft.Contents,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result;
        }

        var load = SparseTomlDocument.Load(draft.Contents, out var savedDocument);
        var read = savedDocument?.ReadOverrides();
        if (!load.IsValid || savedDocument is null || read is null || !read.IsValid || read.Overrides is null)
        {
            return new(
                AtomicTomlWriteState.Invalid,
                ValidationError: load.Error ?? read?.Error);
        }

        baselineContents = [.. draft.Contents];
        baselineOverrides = read.Overrides;
        stagedChanges.Clear();
        return result;
    }

    private void EnsureKnownSetting(string path)
    {
        if (!settingsByPath.ContainsKey(path))
        {
            throw new ArgumentException(
                $"'{path}' is not part of the active configuration schema.",
                nameof(path));
        }
    }

    private void RestorePreviousChange(
        string path,
        bool hadPreviousChange,
        StagedChange? previousChange)
    {
        if (hadPreviousChange)
        {
            stagedChanges[path] = previousChange!;
        }
        else
        {
            stagedChanges.Remove(path);
        }
    }

    private void EnsureEditableSetting(LauncherConfigurationSetting setting)
    {
        EnsureKnownSetting(setting.Path);
        if (!setting.IsDirectlyEditable)
        {
            throw new InvalidOperationException(
                $"'{setting.Path}' must be instantiated and player-facing before it can be edited.");
        }
    }

    private static SparseTomlError? ValidateSettingValue(
        LauncherConfigurationSetting setting,
        string renderedTomlValue)
    {
        if (string.IsNullOrWhiteSpace(renderedTomlValue))
        {
            return InvalidValue(setting, "A setting value cannot be empty.");
        }

        var valid = setting.ValueKind switch
        {
            LauncherConfigurationValueKind.Boolean =>
                renderedTomlValue is "true" or "false",
            LauncherConfigurationValueKind.Integer =>
                long.TryParse(
                    renderedTomlValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out _),
            LauncherConfigurationValueKind.Number =>
                double.TryParse(
                    renderedTomlValue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var number)
                && !double.IsInfinity(number)
                && !double.IsNaN(number),
            LauncherConfigurationValueKind.String
                or LauncherConfigurationValueKind.Keybinding =>
                IsQuotedTomlString(renderedTomlValue),
            LauncherConfigurationValueKind.Enum =>
                TryReadQuotedTomlString(renderedTomlValue, out var enumValue)
                && IsDeclaredEnumValue(setting, enumValue),
            LauncherConfigurationValueKind.Union =>
                setting.Control == LauncherConfigurationControl.NotificationPolicy
                && LauncherNotificationPolicyParser.Parse(setting, renderedTomlValue).IsValid,
            _ => false,
        };

        return valid
            ? null
            : InvalidValue(
                setting,
                $"The value is not valid for {setting.ValueKind.ToString().ToLowerInvariant()} setting '{setting.Path}'.");
    }

    private static bool IsDeclaredEnumValue(
        LauncherConfigurationSetting setting,
        string value) =>
        setting.ValueTypeDefinition.TryGetProperty("values", out var values)
        && values.ValueKind == JsonValueKind.Array
        && values.EnumerateArray().Any(
            candidate =>
                candidate.ValueKind == JsonValueKind.String
                && string.Equals(candidate.GetString(), value, StringComparison.Ordinal));

    private static bool IsQuotedTomlString(string value) =>
        TryReadQuotedTomlString(value, out _);

    private static bool TryReadQuotedTomlString(string value, out string parsed)
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

    private static object? ConvertJsonValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.Null => null,
            _ => value.GetRawText(),
        };

    private static SparseTomlError InvalidValue(
        LauncherConfigurationSetting setting,
        string message) =>
        new(SparseTomlErrorCode.InvalidValue, message);

    private sealed record StagedChange(
        string? RenderedValue,
        bool IsRemoval);
}
