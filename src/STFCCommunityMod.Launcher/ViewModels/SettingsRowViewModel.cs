using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public sealed class SettingsRowViewModel : INotifyPropertyChanged
{
    private readonly Func<LauncherConfigurationSetting, string, bool> stageValue;
    private readonly Func<LauncherConfigurationSetting, bool> stageRemove;
    private readonly Action<LauncherConfigurationSetting, bool> setInputValidity;
    private readonly SettingsActionCommand removeOverrideCommand;
    private SettingsValueState valueState;
    private LauncherNotificationPolicyParseResult? notificationPolicy;
    private string? numericDraft;
    private string? numericValidationError;

    internal SettingsRowViewModel(
        LauncherConfigurationSetting setting,
        SettingsValueState valueState,
        bool editingAvailable,
        Func<LauncherConfigurationSetting, string, bool> stageValue,
        Func<LauncherConfigurationSetting, bool> stageRemove,
        Action<LauncherConfigurationSetting, bool> setInputValidity)
    {
        Setting = setting;
        this.valueState = valueState;
        this.stageValue = stageValue;
        this.stageRemove = stageRemove;
        this.setInputValidity = setInputValidity;

        Path = setting.Path;
        Title = string.IsNullOrWhiteSpace(setting.Title) ? setting.Path : setting.Title;
        Description = string.IsNullOrWhiteSpace(setting.Description)
            ? "No description is available for this setting."
            : setting.Description;
        Category = FormatCategory(setting.Category);
        Control = FormatMetadata(setting.Control);
        ValueKind = FormatMetadata(setting.ValueKind);
        DefaultValue = FormatValue(setting.DefaultValue);
        ApplyState = string.IsNullOrWhiteSpace(valueState.ApplyState)
            ? "Apply behavior is not available."
            : valueState.ApplyState;
        Stability = FormatMetadata(setting.Stability);
        Platforms = FormatMetadata(setting.Platforms);
        SourceSupport = FormatMetadata(setting.SourceSupport);
        IsBooleanEditor = setting.Control == LauncherConfigurationControl.Scalar
            && setting.ValueKind == LauncherConfigurationValueKind.Boolean;
        IsEnumEditor = setting.Control == LauncherConfigurationControl.Scalar
            && setting.ValueKind == LauncherConfigurationValueKind.Enum;
        IsNumericEditor = setting.Control == LauncherConfigurationControl.Scalar
            && setting.ValueKind
                is LauncherConfigurationValueKind.Integer
                or LauncherConfigurationValueKind.Number;
        IsNotificationEditor = setting.Control == LauncherConfigurationControl.NotificationPolicy;
        IsSpecializedEditor =
            !IsBooleanEditor && !IsEnumEditor && !IsNumericEditor && !IsNotificationEditor;
        CanEdit = editingAvailable && (IsBooleanEditor || IsEnumEditor || IsNumericEditor);
        EnumOptions = ReadEnumOptions(setting);
        RefreshNotificationPolicy();

        removeOverrideCommand = new SettingsActionCommand(
            RemoveOverride,
            () => editingAvailable && HasOverride);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LauncherConfigurationSetting Setting { get; }

    public string Path { get; }

    public string Title { get; }

    public string Description { get; }

    public string Category { get; }

    public string Control { get; }

    public string ValueKind { get; }

    public string DefaultValue { get; }

    public string ApplyState { get; }

    public string Stability { get; }

    public string Platforms { get; }

    public string SourceSupport { get; }

    public bool IsBooleanEditor { get; }

    public bool IsEnumEditor { get; }

    public bool IsNumericEditor { get; }

    public bool IsNotificationEditor { get; }

    public bool IsSpecializedEditor { get; }

    public bool CanEdit { get; private set; }

    public IReadOnlyList<SettingsEnumOption> EnumOptions { get; }

    public bool HasOverride => valueState.HasOverride;

    public bool IsStaged => valueState.IsStaged;

    public bool IsRemoval => valueState.IsRemoval;

    public string EffectiveState =>
        IsStaged
            ? IsRemoval ? "Will use default" : "Unsaved"
            : HasOverride ? "Override" : "Default";

    public string EffectiveValue => FormatValue(valueState.EffectiveValue ?? Setting.DefaultValue);

    public bool BooleanValue
    {
        get => ReadBooleanValue();
        set
        {
            if (!CanEdit || value == ReadBooleanValue())
            {
                return;
            }

            if (stageValue(Setting, value ? "true" : "false"))
            {
                OnPropertyChanged();
            }
        }
    }

    public string BooleanStateText => BooleanValue ? "On" : "Off";

    public string EnumValue
    {
        get => ReadEnumValue();
        set
        {
            var currentValue = ReadEnumValue();
            if (!CanEdit
                || string.IsNullOrEmpty(value)
                || string.Equals(value, currentValue, StringComparison.Ordinal))
            {
                return;
            }

            if (stageValue(Setting, LauncherTomlValue.RenderString(value)))
            {
                OnPropertyChanged();
            }
        }
    }

    public bool EnumNeedsAttention =>
        IsEnumEditor
        && HasOverride
        && !TryReadValidEnumValue(valueState.EffectiveValue, out _);

    public string EnumValidationMessage =>
        EnumNeedsAttention
            ? $"The configured value is invalid. {FormatCategory(EnumValue)} is the runtime default."
            : $"Choose one of {EnumOptions.Count} supported values.";

    public string NumericText
    {
        get => numericDraft ?? ReadNumericText();
        set
        {
            if (!CanEdit || string.Equals(value, NumericText, StringComparison.Ordinal))
            {
                return;
            }

            numericDraft = value;
            if (!TryNormalizeNumericValue(value, out var rendered, out var validationError))
            {
                numericValidationError = validationError;
                setInputValidity(Setting, false);
                NotifyNumericStateChanged();
                return;
            }

            numericValidationError = null;
            if (stageValue(Setting, rendered))
            {
                numericDraft = null;
            }
            else
            {
                numericValidationError = "The value could not be staged. Review the settings status and try again.";
            }

            setInputValidity(Setting, numericValidationError is null);
            NotifyNumericStateChanged();
        }
    }

    public bool NumericNeedsAttention =>
        numericValidationError is not null
        || (IsNumericEditor
            && HasOverride
            && !TryReadValidNumericValue(valueState.EffectiveValue, out _));

    public string NumericValidationMessage =>
        numericValidationError
        ?? (NumericNeedsAttention
            ? $"The configured value is invalid or outside its supported range. {ReadDefaultNumericText()} is shown."
            : $"{NumericConstraintText}. Press Enter, Tab, or click elsewhere to stage the value.");

    public string NumericConstraintText
    {
        get
        {
            var valueType = Setting.ValueKind == LauncherConfigurationValueKind.Integer
                ? "Whole number"
                : "Decimal number";
            return Setting.NumericConstraints switch
            {
                { Minimum: not null, Maximum: not null } constraints =>
                    $"{valueType} · {FormatNumber(constraints.Minimum.Value)}–{FormatNumber(constraints.Maximum.Value)}",
                { Minimum: not null } constraints =>
                    $"{valueType} · Minimum {FormatNumber(constraints.Minimum.Value)}",
                { Maximum: not null } constraints =>
                    $"{valueType} · Maximum {FormatNumber(constraints.Maximum.Value)}",
                _ => valueType,
            };
        }
    }

    public string NotificationStateText =>
        notificationPolicy?.Policy.IsEnabled == true ? "On" : "Off";

    public string NotificationDeliverySummary
    {
        get
        {
            if (notificationPolicy is null)
            {
                return "Policy unavailable";
            }

            if (!notificationPolicy.IsValid)
            {
                return "Invalid value · Using event default";
            }

            if (!notificationPolicy.Policy.IsEnabled)
            {
                return "No delivery";
            }

            var delivery = new List<string>();
            if (notificationPolicy.Policy.System)
            {
                delivery.Add("System");
            }

            if (notificationPolicy.Policy.Audio)
            {
                delivery.Add($"{FormatCategory(notificationPolicy.Policy.Sound)} sound");
            }

            return string.Join(" · ", delivery);
        }
    }

    public bool NotificationNeedsAttention =>
        notificationPolicy is { IsValid: false };

    public string NotificationPolicyHelp =>
        notificationPolicy is { IsValid: false }
            ? notificationPolicy.Error ?? "The canonical policy is invalid; the event default is shown."
            : "Notification delivery is review-only until the dedicated policy editor is connected.";

    public string EditorLabel => $"{Control} · {ValueKind}";

    public string EditorAutomationName =>
        IsBooleanEditor
            ? $"{Title}, {EffectiveState}, {BooleanValue}"
            : IsEnumEditor
                ? $"{Title}, {EffectiveState}, {FormatCategory(EnumValue)}"
            : IsNumericEditor
                ? $"{Title}, {EffectiveState}, {NumericText}. {NumericValidationMessage}"
            : IsNotificationEditor
                ? $"{Title}, {NotificationStateText}, {NotificationDeliverySummary}. Review only."
                : $"{Title} requires its dedicated {Control} editor.";

    public string SpecializedEditorMessage =>
        Setting.Control switch
        {
            LauncherConfigurationControl.NotificationPolicy =>
                "System, audio, and sound delivery editor follows in the Notifications adapter.",
            LauncherConfigurationControl.Keybinding =>
                "Key capture and conflict detection follow in the Hotkeys adapter.",
            _ => "This value type is catalogued and awaits its typed editor.",
        };

    public ICommand RemoveOverrideCommand => removeOverrideCommand;

    public bool CanRemoveOverride => removeOverrideCommand.CanExecute(null);

    public string RemoveOverrideAvailability =>
        HasOverride
            ? CanRemoveOverride
                ? $"Remove the override for {Title} and use its runtime default."
                : "Override removal requires a valid writable configuration."
            : "This setting already uses its runtime default.";

    internal bool Matches(string searchText)
    {
        return Contains(Path, searchText)
            || Contains(Title, searchText)
            || Contains(Description, searchText)
            || Contains(Category, searchText)
            || Contains(Control, searchText)
            || Contains(ValueKind, searchText);
    }

    internal void UpdateState(SettingsValueState state, bool editingAvailable)
    {
        valueState = state;
        numericDraft = null;
        numericValidationError = null;
        setInputValidity(Setting, true);
        RefreshNotificationPolicy();
        CanEdit = editingAvailable && (IsBooleanEditor || IsEnumEditor || IsNumericEditor);
        removeOverrideCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(HasOverride));
        OnPropertyChanged(nameof(IsStaged));
        OnPropertyChanged(nameof(IsRemoval));
        OnPropertyChanged(nameof(EffectiveState));
        OnPropertyChanged(nameof(EffectiveValue));
        OnPropertyChanged(nameof(BooleanValue));
        OnPropertyChanged(nameof(BooleanStateText));
        OnPropertyChanged(nameof(EnumValue));
        OnPropertyChanged(nameof(EnumNeedsAttention));
        OnPropertyChanged(nameof(EnumValidationMessage));
        NotifyNumericStateChanged();
        OnPropertyChanged(nameof(NotificationStateText));
        OnPropertyChanged(nameof(NotificationDeliverySummary));
        OnPropertyChanged(nameof(NotificationNeedsAttention));
        OnPropertyChanged(nameof(NotificationPolicyHelp));
        OnPropertyChanged(nameof(EditorAutomationName));
        OnPropertyChanged(nameof(CanRemoveOverride));
        OnPropertyChanged(nameof(RemoveOverrideAvailability));
    }

    private bool ReadBooleanValue()
    {
        if (valueState.EffectiveValue is bool boolean)
        {
            return boolean;
        }

        if (valueState.EffectiveValue is string text
            && bool.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return Setting.DefaultValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            && Setting.DefaultValue.GetBoolean();
    }

    private string ReadEnumValue()
    {
        if (TryReadValidEnumValue(valueState.EffectiveValue, out var value))
        {
            return value;
        }

        var defaultValue = Setting.DefaultValue.ValueKind == JsonValueKind.String
            ? Setting.DefaultValue.GetString()
            : null;
        return defaultValue is not null
            && EnumOptions.Any(option => option.Value == defaultValue)
                ? defaultValue
                : EnumOptions.Count > 0 ? EnumOptions[0].Value : string.Empty;
    }

    private bool TryReadValidEnumValue(object? candidate, out string value)
    {
        value = candidate as string ?? string.Empty;
        if (value.Length >= 2
            && (value[0] == '\'' || value[0] == '"'))
        {
            if (!LauncherTomlValue.TryReadString(value, out var parsed))
            {
                value = string.Empty;
            }
            else
            {
                value = parsed;
            }
        }

        var parsedValue = value;
        return EnumOptions.Any(option => option.Value == parsedValue);
    }

    private void RefreshNotificationPolicy()
    {
        notificationPolicy = IsNotificationEditor
            ? LauncherNotificationPolicyParser.Parse(
                Setting,
                valueState.HasOverride ? valueState.EffectiveValue as string : null)
            : null;
    }

    private void RemoveOverride()
    {
        if (stageRemove(Setting))
        {
            OnPropertyChanged(nameof(BooleanValue));
            OnPropertyChanged(nameof(BooleanStateText));
            OnPropertyChanged(nameof(EnumValue));
            OnPropertyChanged(nameof(EnumNeedsAttention));
            OnPropertyChanged(nameof(EnumValidationMessage));
            NotifyNumericStateChanged();
        }
    }

    private string ReadNumericText()
    {
        if (TryReadValidNumericValue(valueState.EffectiveValue, out var rendered))
        {
            return rendered;
        }

        return ReadDefaultNumericText();
    }

    private string ReadDefaultNumericText()
    {
        if (Setting.ValueKind == LauncherConfigurationValueKind.Integer
            && Setting.DefaultValue.TryGetInt64(out var integer))
        {
            return LauncherTomlValue.RenderInteger(integer);
        }

        return Setting.DefaultValue.TryGetDouble(out var number)
            ? LauncherTomlValue.RenderNumber(number)
            : string.Empty;
    }

    private bool TryReadValidNumericValue(
        object? candidate,
        out string rendered)
    {
        rendered = string.Empty;
        switch (Setting.ValueKind)
        {
            case LauncherConfigurationValueKind.Integer:
                if (!TryReadInteger(candidate, out var integer)
                    || Setting.NumericConstraints is { } integerConstraints
                    && !integerConstraints.Contains(integer))
                {
                    return false;
                }

                rendered = LauncherTomlValue.RenderInteger(integer);
                return true;

            case LauncherConfigurationValueKind.Number:
                if (!TryReadNumber(candidate, out var number)
                    || Setting.NumericConstraints is { } numberConstraints
                    && !numberConstraints.Contains(number))
                {
                    return false;
                }

                rendered = LauncherTomlValue.RenderNumber(number);
                return true;

            default:
                return false;
        }
    }

    private bool TryNormalizeNumericValue(
        string value,
        out string rendered,
        out string validationError)
    {
        rendered = string.Empty;
        validationError = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            validationError = "Enter a value.";
            return false;
        }

        if (!TryReadValidNumericValue(value, out rendered))
        {
            validationError = $"Enter a valid {NumericConstraintText.ToLower(CultureInfo.CurrentCulture)}.";
            return false;
        }

        return true;
    }

    private static bool TryReadInteger(object? candidate, out long value)
    {
        value = default;
        if (candidate is long integer)
        {
            value = integer;
            return true;
        }

        if (candidate is int integer32)
        {
            value = integer32;
            return true;
        }

        return candidate is string text
            && LauncherTomlValue.TryReadInteger(text, out value);
    }

    private static bool TryReadNumber(object? candidate, out double value)
    {
        value = default;
        if (candidate is double number)
        {
            value = number;
            return double.IsFinite(value);
        }

        if (candidate is float single)
        {
            value = single;
            return float.IsFinite(single);
        }

        if (candidate is long integer)
        {
            value = integer;
            return true;
        }

        return candidate is string text
            && LauncherTomlValue.TryReadNumber(text, out value);
    }

    private void NotifyNumericStateChanged()
    {
        OnPropertyChanged(nameof(NumericText));
        OnPropertyChanged(nameof(NumericNeedsAttention));
        OnPropertyChanged(nameof(NumericValidationMessage));
        OnPropertyChanged(nameof(NumericConstraintText));
        OnPropertyChanged(nameof(EditorAutomationName));
    }

    private static string FormatNumber(double value) =>
        value.ToString("G", CultureInfo.CurrentCulture);

    private static SettingsEnumOption[] ReadEnumOptions(
        LauncherConfigurationSetting setting)
    {
        if (setting.ValueKind != LauncherConfigurationValueKind.Enum
            || !setting.ValueTypeDefinition.TryGetProperty("values", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .Select(value => new SettingsEnumOption(value!, FormatCategory(value!)))
            .ToArray();
    }

    private static bool Contains(string candidate, string searchText) =>
        candidate.Contains(searchText, StringComparison.OrdinalIgnoreCase);

    private static string FormatMetadata(object? value)
    {
        var formatted = FormatValue(value);
        return formatted == "Not specified" ? "Unspecified" : formatted;
    }

    private static string FormatCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "Other";
        }

        var words = category.Replace('_', ' ').Replace('-', ' ');
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(words);
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "Not specified",
            JsonElement element => FormatJsonValue(element),
            bool booleanValue => booleanValue ? "true" : "false",
            string stringValue when string.IsNullOrEmpty(stringValue) => "(empty)",
            string stringValue => stringValue,
            IEnumerable values => string.Join(", ", values.Cast<object?>().Select(FormatValue)),
            IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture) ?? "Not specified",
            _ => value.ToString() ?? "Not specified",
        };
    }

    private static string FormatJsonValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String => value.GetString() ?? "(empty)",
            JsonValueKind.Number => value.GetRawText(),
            _ => value.GetRawText(),
        };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record SettingsEnumOption(
    string Value,
    string Label);
