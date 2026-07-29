using System.Collections.ObjectModel;
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
        var hasBaselineOverride = baselineOverrides.TryGetValue(setting.Path, out var baseline);
        if ((hasBaselineOverride
             && AreEquivalentSettingValues(
                 setting,
                 baseline!.RenderedValue,
                 renderedTomlValue))
            || (!hasBaselineOverride
                && IsEquivalentToDefault(setting, renderedTomlValue)))
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
                LauncherTomlValue.TryReadInteger(renderedTomlValue, out _),
            LauncherConfigurationValueKind.Number =>
                LauncherTomlValue.TryReadNumber(renderedTomlValue, out _),
            LauncherConfigurationValueKind.String
                or LauncherConfigurationValueKind.Keybinding =>
                LauncherTomlValue.TryReadString(renderedTomlValue, out _),
            LauncherConfigurationValueKind.Enum =>
                LauncherTomlValue.TryReadString(renderedTomlValue, out var enumValue)
                && IsDeclaredEnumValue(setting, enumValue),
            LauncherConfigurationValueKind.Union =>
                setting.Control == LauncherConfigurationControl.NotificationPolicy
                && LauncherNotificationPolicyParser.Parse(setting, renderedTomlValue).IsValid,
            _ => false,
        };

        if (!valid)
        {
            return InvalidValue(
                setting,
                $"The value is not valid for {setting.ValueKind.ToString().ToLowerInvariant()} setting '{setting.Path}'.");
        }

        if (setting.ValueKind == LauncherConfigurationValueKind.String
            && LauncherTomlValue.TryReadString(renderedTomlValue, out var stringValue)
            && !LauncherConfigurationStringValue.TryNormalize(
                setting,
                stringValue,
                out _,
                out var stringValidationError))
        {
            return InvalidValue(setting, stringValidationError);
        }

        if (setting.NumericConstraints is { } numericConstraints)
        {
            if (!TryReadConstrainedNumber(setting, renderedTomlValue, out var number))
            {
                return InvalidValue(
                    setting,
                    $"The value is not valid for numeric setting '{setting.Path}'.");
            }

            if (!numericConstraints.Contains(number))
            {
                return InvalidValue(
                    setting,
                    $"The value for '{setting.Path}' must be {FormatNumericRange(setting)}.");
            }
        }

        return null;
    }

    private static bool TryReadConstrainedNumber(
        LauncherConfigurationSetting setting,
        string renderedTomlValue,
        out double value)
    {
        if (setting.ValueKind == LauncherConfigurationValueKind.Integer
            && LauncherTomlValue.TryReadInteger(renderedTomlValue, out var integer))
        {
            value = integer;
            return true;
        }

        return LauncherTomlValue.TryReadNumber(renderedTomlValue, out value);
    }

    private static string FormatNumericRange(LauncherConfigurationSetting setting)
    {
        var constraints = setting.NumericConstraints!;
        if (constraints.Minimum.HasValue && constraints.Maximum.HasValue)
        {
            return $"between {FormatConstraint(setting, constraints.Minimum.Value)} and "
                + $"{FormatConstraint(setting, constraints.Maximum.Value)}";
        }

        return constraints.Minimum.HasValue
            ? $"at least {FormatConstraint(setting, constraints.Minimum.Value)}"
            : $"at most {FormatConstraint(setting, constraints.Maximum!.Value)}";
    }

    private static string FormatConstraint(
        LauncherConfigurationSetting setting,
        double value) =>
        setting.ValueKind == LauncherConfigurationValueKind.Integer
            ? LauncherTomlValue.RenderInteger(checked((long)value))
            : LauncherTomlValue.RenderNumber(value);

    private static bool IsDeclaredEnumValue(
        LauncherConfigurationSetting setting,
        string value) =>
        setting.ValueTypeDefinition.TryGetProperty("values", out var values)
        && values.ValueKind == JsonValueKind.Array
        && values.EnumerateArray().Any(
            candidate =>
                candidate.ValueKind == JsonValueKind.String
                && string.Equals(candidate.GetString(), value, StringComparison.Ordinal));

    private static bool AreEquivalentSettingValues(
        LauncherConfigurationSetting setting,
        string first,
        string second)
    {
        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return true;
        }

        return setting.ValueKind switch
        {
            LauncherConfigurationValueKind.String
                or LauncherConfigurationValueKind.Keybinding
                or LauncherConfigurationValueKind.Enum =>
                LauncherTomlValue.TryReadString(first, out var firstValue)
                && LauncherTomlValue.TryReadString(second, out var secondValue)
                && string.Equals(firstValue, secondValue, StringComparison.Ordinal),
            LauncherConfigurationValueKind.Integer =>
                LauncherTomlValue.TryReadInteger(first, out var firstInteger)
                && LauncherTomlValue.TryReadInteger(second, out var secondInteger)
                && firstInteger == secondInteger,
            LauncherConfigurationValueKind.Number =>
                LauncherTomlValue.TryReadNumber(first, out var firstNumber)
                && LauncherTomlValue.TryReadNumber(second, out var secondNumber)
                && firstNumber.Equals(secondNumber),
            _ => false,
        };
    }

    private static bool IsEquivalentToDefault(
        LauncherConfigurationSetting setting,
        string renderedValue) =>
        setting.ValueKind switch
        {
            LauncherConfigurationValueKind.Boolean
                when setting.DefaultValue.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                renderedValue == (setting.DefaultValue.GetBoolean() ? "true" : "false"),
            LauncherConfigurationValueKind.String
                or LauncherConfigurationValueKind.Keybinding
                or LauncherConfigurationValueKind.Enum
                when setting.DefaultValue.ValueKind == JsonValueKind.String
                     && LauncherTomlValue.TryReadString(renderedValue, out var value) =>
                string.Equals(
                    value,
                    setting.DefaultValue.GetString(),
                    StringComparison.Ordinal),
            LauncherConfigurationValueKind.Integer
                when setting.DefaultValue.TryGetInt64(out var defaultInteger)
                     && LauncherTomlValue.TryReadInteger(renderedValue, out var integer) =>
                integer == defaultInteger,
            LauncherConfigurationValueKind.Number
                when setting.DefaultValue.TryGetDouble(out var defaultNumber)
                     && LauncherTomlValue.TryReadNumber(renderedValue, out var number) =>
                number.Equals(defaultNumber),
            _ => false,
        };

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
