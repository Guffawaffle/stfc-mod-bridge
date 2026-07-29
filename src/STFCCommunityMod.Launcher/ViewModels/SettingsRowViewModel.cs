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
    private readonly SettingsActionCommand unbindKeybindingCommand;
    private readonly SettingsValueCommand<string> replaceKeybindingCommand;
    private readonly SettingsValueCommand<string> addKeybindingCommand;
    private SettingsValueState valueState;
    private LauncherNotificationPolicyParseResult? notificationPolicy;
    private string? keybindingConflictMessage;
    private string? numericDraft;
    private string? numericValidationError;
    private string? stringDraft;
    private string? stringValidationError;

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
        IsStringEditor = setting.Control == LauncherConfigurationControl.Scalar
            && setting.ValueKind == LauncherConfigurationValueKind.String;
        IsKeybindingEditor = setting.Control == LauncherConfigurationControl.Keybinding
            && setting.ValueKind == LauncherConfigurationValueKind.Keybinding;
        IsNotificationEditor = setting.Control == LauncherConfigurationControl.NotificationPolicy;
        IsSpecializedEditor =
            !IsBooleanEditor
            && !IsEnumEditor
            && !IsNumericEditor
            && !IsStringEditor
            && !IsKeybindingEditor
            && !IsNotificationEditor;
        CanEdit = editingAvailable
            && (IsBooleanEditor
                || IsEnumEditor
                || IsNumericEditor
                || IsStringEditor
                || IsKeybindingEditor
                || IsNotificationEditor);
        EnumOptions = ReadEnumOptions(setting);
        RefreshNotificationPolicy();

        removeOverrideCommand = new SettingsActionCommand(
            RemoveOverride,
            () => editingAvailable && HasOverride);
        unbindKeybindingCommand = new SettingsActionCommand(
            UnbindKeybinding,
            () => CanEdit && IsKeybindingEditor && !CurrentKeybinding().IsUnbound);
        replaceKeybindingCommand = new SettingsValueCommand<string>(
            ReplaceKeybinding,
            _ => CanEdit && IsKeybindingEditor);
        addKeybindingCommand = new SettingsValueCommand<string>(
            AddKeybinding,
            _ => CanEdit && IsKeybindingEditor);
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

    public bool IsStringEditor { get; }

    public bool IsKeybindingEditor { get; }

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

    public string StringText
    {
        get => stringDraft ?? ReadStringText();
        set
        {
            if (!CanEdit || string.Equals(value, StringText, StringComparison.Ordinal))
            {
                return;
            }

            stringDraft = value;
            if (!TryNormalizeStringValue(value, out var normalized, out var validationError))
            {
                stringValidationError = validationError;
                setInputValidity(Setting, false);
                NotifyStringStateChanged();
                return;
            }

            stringValidationError = null;
            if (stageValue(Setting, LauncherTomlValue.RenderString(normalized)))
            {
                stringDraft = null;
            }
            else
            {
                stringValidationError = "The value could not be staged. Review the settings status and try again.";
            }

            setInputValidity(Setting, stringValidationError is null);
            NotifyStringStateChanged();
        }
    }

    public bool StringNeedsAttention =>
        stringValidationError is not null
        || (IsStringEditor && HasOverride && !TryReadValidStringValue(valueState.EffectiveValue, out _));

    public string StringValidationMessage =>
        stringValidationError
        ?? (StringNeedsAttention
            ? $"The configured value is invalid. {ReadDefaultStringText()} is shown."
            : $"{StringInputHint}. Press Enter, Tab, or click elsewhere to stage the value.");

    public string StringInputHint =>
        ReadStringFormat() switch
        {
            "uri" => "HTTP or HTTPS URL · Empty uses the game default",
            "comma-separated-list" => "Comma-separated names · Empty disables filtering",
            _ => "Text value",
        };

    public string NotificationStateText =>
        notificationPolicy?.Policy.IsEnabled == true ? "On" : "Off";

    public string KeybindingDisplay => CurrentKeybinding().Display;

    public bool KeybindingNeedsAttention =>
        IsKeybindingEditor
        && (!TryReadEffectiveKeybinding(out var parsed) || !parsed.IsValid
            || keybindingConflictMessage is not null);

    public string KeybindingValidationMessage
    {
        get
        {
            if (keybindingConflictMessage is not null)
            {
                return keybindingConflictMessage;
            }

            return !TryReadEffectiveKeybinding(out var parsed) || !parsed.IsValid
                ? parsed.Error ?? "The configured shortcut is invalid; the runtime default is shown."
                : "Change replaces this binding. Add keeps this binding as an alternative.";
        }
    }

    public ICommand ReplaceKeybindingCommand => replaceKeybindingCommand;

    public ICommand AddKeybindingCommand => addKeybindingCommand;

    public ICommand UnbindKeybindingCommand => unbindKeybindingCommand;

    public bool NotificationSystem
    {
        get => notificationPolicy?.Policy.System == true;
        set
        {
            var policy = CurrentNotificationPolicy();
            if (value != policy.System)
            {
                StageNotificationPolicy(policy with { System = value });
            }
        }
    }

    public bool NotificationAudio
    {
        get => notificationPolicy?.Policy.Audio == true;
        set
        {
            var policy = CurrentNotificationPolicy();
            if (value != policy.Audio)
            {
                StageNotificationPolicy(policy with { Audio = value });
            }
        }
    }

    public string NotificationSound
    {
        get => CurrentNotificationPolicy().Sound;
        set
        {
            var policy = CurrentNotificationPolicy();
            if (!string.IsNullOrEmpty(value)
                && !string.Equals(value, policy.Sound, StringComparison.Ordinal)
                && NotificationSounds.Contains(value, StringComparer.Ordinal))
            {
                StageNotificationPolicy(policy with { Sound = value });
            }
        }
    }

    public IReadOnlyList<string> NotificationSounds =>
        IsNotificationEditor
            ? LauncherNotificationPolicyParser.ReadAllowedSounds(Setting)
            : [];

    public IReadOnlyList<SettingsEnumOption> NotificationSoundOptions =>
        NotificationSounds
            .Select(sound => new SettingsEnumOption(sound, FormatCategory(sound)))
            .ToArray();

    public bool CanSelectNotificationSound =>
        CanEdit && NotificationAudio && NotificationSounds.Count > 0;

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
            : "Toggle Windows or audio delivery and choose the in-game sound.";

    public string EditorLabel => $"{Control} · {ValueKind}";

    public string EditorAutomationName =>
        IsBooleanEditor
            ? $"{Title}, {EffectiveState}, {BooleanValue}"
            : IsEnumEditor
                ? $"{Title}, {EffectiveState}, {FormatCategory(EnumValue)}"
            : IsNumericEditor
                ? $"{Title}, {EffectiveState}, {NumericText}. {NumericValidationMessage}"
            : IsStringEditor
                ? $"{Title}, {EffectiveState}, {StringText}. {StringValidationMessage}"
            : IsKeybindingEditor
                ? $"{Title}, {EffectiveState}, {KeybindingDisplay}. {KeybindingValidationMessage}"
            : IsNotificationEditor
                ? $"{Title}, {NotificationStateText}, {NotificationDeliverySummary}. Use the inline delivery controls."
                : $"{Title} requires its dedicated {Control} editor.";

    public string SpecializedEditorMessage =>
        $"This {Control.ToLowerInvariant()} value is catalogued and awaits its typed editor.";

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
        stringDraft = null;
        stringValidationError = null;
        setInputValidity(Setting, true);
        RefreshNotificationPolicy();
        CanEdit = editingAvailable
            && (IsBooleanEditor
                || IsEnumEditor
                || IsNumericEditor
                || IsStringEditor
                || IsKeybindingEditor
                || IsNotificationEditor);
        removeOverrideCommand.RaiseCanExecuteChanged();
        unbindKeybindingCommand.RaiseCanExecuteChanged();
        replaceKeybindingCommand.RaiseCanExecuteChanged();
        addKeybindingCommand.RaiseCanExecuteChanged();
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
        NotifyStringStateChanged();
        NotifyKeybindingStateChanged();
        OnPropertyChanged(nameof(NotificationStateText));
        OnPropertyChanged(nameof(NotificationSystem));
        OnPropertyChanged(nameof(NotificationAudio));
        OnPropertyChanged(nameof(NotificationSound));
        OnPropertyChanged(nameof(NotificationSounds));
        OnPropertyChanged(nameof(NotificationSoundOptions));
        OnPropertyChanged(nameof(CanSelectNotificationSound));
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

    private LauncherNotificationPolicy CurrentNotificationPolicy() =>
        notificationPolicy?.Policy
        ?? LauncherNotificationPolicyParser.Parse(Setting, null).Policy;

    private void StageNotificationPolicy(LauncherNotificationPolicy policy)
    {
        if (!CanEdit || !IsNotificationEditor || !stageValue(Setting, policy.Render()))
        {
            return;
        }

        OnPropertyChanged(nameof(NotificationStateText));
        OnPropertyChanged(nameof(NotificationSystem));
        OnPropertyChanged(nameof(NotificationAudio));
        OnPropertyChanged(nameof(NotificationSound));
        OnPropertyChanged(nameof(CanSelectNotificationSound));
        OnPropertyChanged(nameof(NotificationDeliverySummary));
        OnPropertyChanged(nameof(NotificationNeedsAttention));
        OnPropertyChanged(nameof(NotificationPolicyHelp));
        OnPropertyChanged(nameof(EditorAutomationName));
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
            NotifyStringStateChanged();
            NotifyKeybindingStateChanged();
        }
    }

    internal LauncherKeybindingAssignment? ReadKeybindingAssignment()
    {
        if (!IsKeybindingEditor
            || !TryReadEffectiveKeybinding(out var binding)
            || !binding.IsValid)
        {
            return null;
        }

        return new(Setting, binding);
    }

    internal void SetKeybindingConflict(string? message)
    {
        if (string.Equals(keybindingConflictMessage, message, StringComparison.Ordinal))
        {
            return;
        }

        keybindingConflictMessage = message;
        setInputValidity(Setting, message is null);
        NotifyKeybindingStateChanged();
    }

    private void ReplaceKeybinding(string chord)
    {
        var parsed = LauncherKeybindingValue.Parse(chord);
        if (!CanEdit
            || !parsed.IsValid
            || parsed.IsUnbound
            || !stageValue(Setting, LauncherTomlValue.RenderString(parsed.Normalized)))
        {
            return;
        }

        NotifyKeybindingStateChanged();
    }

    private void AddKeybinding(string chord)
    {
        var captured = LauncherKeybindingValue.Parse(chord);
        if (!CanEdit || !captured.IsValid || captured.IsUnbound)
        {
            return;
        }

        var current = CurrentKeybinding();
        var combined = current.IsUnbound
            ? captured
            : LauncherKeybindingValue.Parse($"{current.Normalized}|{captured.Normalized}");
        if (combined.IsValid
            && stageValue(Setting, LauncherTomlValue.RenderString(combined.Normalized)))
        {
            NotifyKeybindingStateChanged();
        }
    }

    private void UnbindKeybinding()
    {
        if (CanEdit
            && stageValue(Setting, LauncherTomlValue.RenderString("NONE")))
        {
            NotifyKeybindingStateChanged();
        }
    }

    private LauncherKeybindingParseResult CurrentKeybinding()
    {
        if (TryReadEffectiveKeybinding(out var binding) && binding.IsValid)
        {
            return binding;
        }

        return LauncherKeybindingValue.Parse(Setting.DefaultValue.GetString()!);
    }

    private bool TryReadEffectiveKeybinding(
        out LauncherKeybindingParseResult binding)
    {
        binding = LauncherKeybindingValue.Parse("NONE");
        if (!IsKeybindingEditor || valueState.EffectiveValue is not string text)
        {
            return false;
        }

        if ((HasOverride || IsStaged)
            && LauncherTomlValue.TryReadString(text, out var parsedText))
        {
            text = parsedText;
        }

        binding = LauncherKeybindingValue.Parse(text);
        return true;
    }

    private void NotifyKeybindingStateChanged()
    {
        unbindKeybindingCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(KeybindingDisplay));
        OnPropertyChanged(nameof(KeybindingNeedsAttention));
        OnPropertyChanged(nameof(KeybindingValidationMessage));
        OnPropertyChanged(nameof(EditorAutomationName));
    }

    private string ReadStringText()
    {
        if (TryReadValidStringValue(valueState.EffectiveValue, out var value))
        {
            return value;
        }

        return ReadDefaultStringText();
    }

    private string ReadDefaultStringText() =>
        Setting.DefaultValue.ValueKind == JsonValueKind.String
            ? Setting.DefaultValue.GetString() ?? string.Empty
            : string.Empty;

    private bool TryReadValidStringValue(object? candidate, out string value)
    {
        if (candidate is not string text)
        {
            value = string.Empty;
            return false;
        }

        if (HasOverride)
        {
            if (!LauncherTomlValue.TryReadString(text, out value))
            {
                return false;
            }
        }
        else
        {
            value = text;
        }

        return LauncherConfigurationStringValue.TryNormalize(
            Setting,
            value,
            out value,
            out _);
    }

    private bool TryNormalizeStringValue(
        string value,
        out string normalized,
        out string validationError) =>
        LauncherConfigurationStringValue.TryNormalize(
            Setting,
            value,
            out normalized,
            out validationError);

    private string? ReadStringFormat() =>
        LauncherConfigurationStringValue.ReadFormat(Setting);

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

    private void NotifyStringStateChanged()
    {
        OnPropertyChanged(nameof(StringText));
        OnPropertyChanged(nameof(StringNeedsAttention));
        OnPropertyChanged(nameof(StringValidationMessage));
        OnPropertyChanged(nameof(StringInputHint));
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
