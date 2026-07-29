using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public sealed class SettingsRowViewModel :
    SettingsListItemViewModel,
    INotifyPropertyChanged
{
    private readonly Func<LauncherConfigurationSetting, string, bool> stageValue;
    private readonly Func<LauncherConfigurationSetting, bool> stageRemove;
    private readonly Func<LauncherConfigurationSetting, bool> revertDraft;
    private readonly Action<LauncherConfigurationSetting, bool> setInputValidity;
    private readonly SettingsEditorDraftStore editorDraftStore;
    private readonly SettingsActionCommand useDefaultCommand;
    private readonly SettingsActionCommand revertDraftCommand;
    private readonly SettingsValueCommand<string> addKeybindingCommand;
    private readonly SettingsValueCommand<string> removeKeybindingCommand;
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
        Func<LauncherConfigurationSetting, bool> revertDraft,
        Action<LauncherConfigurationSetting, bool> setInputValidity,
        SettingsEditorDraftStore editorDraftStore)
    {
        Setting = setting;
        this.valueState = valueState;
        this.stageValue = stageValue;
        this.stageRemove = stageRemove;
        this.revertDraft = revertDraft;
        this.setInputValidity = setInputValidity;
        this.editorDraftStore =
            editorDraftStore ?? throw new ArgumentNullException(nameof(editorDraftStore));

        Path = setting.Path;
        Title = setting.Presentation.Label;
        Description = setting.Presentation.Help ?? string.Empty;
        FamilyMemberLabel = setting.Presentation.Family?.MemberLabel ?? string.Empty;
        IsCompactBindingFamily = string.Equals(
            setting.Presentation.Family?.PresentationHint,
            "compact-binding-list",
            StringComparison.Ordinal);
        Unit = setting.Presentation.Unit ?? string.Empty;
        ApplyTiming = setting.Presentation.ApplyTiming;
        AccessibleName = setting.Presentation.AccessibleName;
        AccessibleHelp = setting.Presentation.AccessibleHelp;
        Control = FormatMetadata(setting.Control);
        ValueKind = FormatMetadata(setting.ValueKind);
        DefaultValue = FormatValue(setting.DefaultValue);
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
        RestoreEditorDraft();

        useDefaultCommand = new SettingsActionCommand(
            UseDefault,
            () => CanEdit && DraftHasOverride);
        revertDraftCommand = new SettingsActionCommand(
            RevertDraft,
            () => CanEdit && IsDirty);
        addKeybindingCommand = new SettingsValueCommand<string>(
            AddKeybinding,
            _ => CanEdit && IsKeybindingEditor);
        removeKeybindingCommand = new SettingsValueCommand<string>(
            RemoveKeybinding,
            _ => CanEdit && IsKeybindingEditor);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LauncherConfigurationSetting Setting { get; }

    public string Path { get; }

    public string Title { get; }

    public string Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public string FamilyMemberLabel { get; }

    public bool IsCompactBindingFamily { get; }

    public string DisplayTitle =>
        IsCompactBindingFamily ? FamilyMemberLabel : Title;

    public string Unit { get; }

    public bool HasUnit => !string.IsNullOrWhiteSpace(Unit);

    public string ApplyTiming { get; }

    public string AccessibleName { get; }

    public string AccessibleHelp { get; }

    public string Control { get; }

    public string ValueKind { get; }

    public string DefaultValue { get; }

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

    public bool SavedHasOverride => valueState.SavedHasOverride;

    public bool DraftHasOverride => valueState.DraftHasOverride;

    public bool IsDirty => valueState.IsDirty;

    public bool IsCustom => DraftHasOverride;

    public bool HasOverflowActions => IsKeybindingEditor || DraftHasOverride;

    public bool IsExperimental =>
        Setting.Stability == LauncherConfigurationStability.Experimental;

    public string EffectiveState =>
        IsDirty
            ? DraftHasOverride ? "Unsaved custom value" : "Unsaved default"
            : DraftHasOverride ? "Custom" : "Default";

    public string EffectiveValue => FormatValue(valueState.DraftValue ?? Setting.DefaultValue);

    public string SavedValueText =>
        FormatStateValue(valueState.SavedValue, SavedHasOverride);

    public string RevertDraftAvailability =>
        IsDirty
            ? $"Revert unsaved change — saved: {SavedValueText}"
            : "This row matches its saved state.";

    public string RevertDraftAutomationName =>
        $"Revert {Title} to saved value {SavedValueText}";

    public string RevertDraftAutomationHelp =>
        $"Restores both the saved value and its saved {(SavedHasOverride ? "custom override" : "default")} state.";

    public string UseDefaultLabel => $"Use default: {DefaultValueText}";

    public string UseDefaultAvailability =>
        DraftHasOverride
            ? $"Remove the explicit override and use the application default {DefaultValueText}."
            : $"This setting already uses the application default {DefaultValueText}.";

    public string DefaultValueText
    {
        get
        {
            if (!IsKeybindingEditor)
            {
                return DefaultValue;
            }

            var parsed = LauncherKeybindingValue.Parse(DefaultValue);
            return parsed.IsValid ? parsed.Display : DefaultValue;
        }
    }

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
        && DraftHasOverride
        && !TryReadValidEnumValue(valueState.DraftValue, out _);

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
                editorDraftStore.Set(Path, value, validationError);
                setInputValidity(Setting, false);
                NotifyNumericStateChanged();
                return;
            }

            numericValidationError = null;
            editorDraftStore.Remove(Path);
            if (stageValue(Setting, rendered))
            {
                numericDraft = null;
            }
            else
            {
                numericValidationError = "The value could not be staged. Review the settings status and try again.";
                editorDraftStore.Set(Path, value, numericValidationError);
            }

            setInputValidity(Setting, numericValidationError is null);
            NotifyNumericStateChanged();
        }
    }

    public bool NumericNeedsAttention =>
        numericValidationError is not null
        || (IsNumericEditor
            && DraftHasOverride
            && !TryReadValidNumericValue(valueState.DraftValue, out _));

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
                editorDraftStore.Set(Path, value, validationError);
                setInputValidity(Setting, false);
                NotifyStringStateChanged();
                return;
            }

            stringValidationError = null;
            editorDraftStore.Remove(Path);
            if (stageValue(Setting, LauncherTomlValue.RenderString(normalized)))
            {
                stringDraft = null;
            }
            else
            {
                stringValidationError = "The value could not be staged. Review the settings status and try again.";
                editorDraftStore.Set(Path, value, stringValidationError);
            }

            setInputValidity(Setting, stringValidationError is null);
            NotifyStringStateChanged();
        }
    }

    public bool StringNeedsAttention =>
        stringValidationError is not null
        || (IsStringEditor && DraftHasOverride && !TryReadValidStringValue(valueState.DraftValue, out _));

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

    public string KeybindingDisplay => CurrentKeybinding().Display;

    public IReadOnlyList<SettingsKeybindingChord> KeybindingChords =>
        CurrentKeybinding().Chords
            .Select(chord => new SettingsKeybindingChord(chord.Canonical, chord.Display, Title))
            .ToArray();

    public bool IsKeybindingUnbound => CurrentKeybinding().IsUnbound;

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
                : "Add another shortcut or remove individual alternatives.";
        }
    }

    public ICommand AddKeybindingCommand => addKeybindingCommand;

    public ICommand RemoveKeybindingCommand => removeKeybindingCommand;

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
            .Select(sound => new SettingsEnumOption(sound, FormatCategory(sound), string.Empty))
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
                ? $"{Title}, {EffectiveState}, {NotificationDeliverySummary}. Use the system and sound delivery controls."
                : $"{Title} requires its dedicated {Control} editor.";

    public string SpecializedEditorMessage =>
        $"This {Control.ToLowerInvariant()} value is catalogued and awaits its typed editor.";

    public ICommand UseDefaultCommand => useDefaultCommand;

    public ICommand RevertDraftCommand => revertDraftCommand;

    internal void UpdateState(SettingsValueState state, bool editingAvailable)
    {
        valueState = state;
        RestoreEditorDraft();
        RefreshNotificationPolicy();
        CanEdit = editingAvailable
            && (IsBooleanEditor
                || IsEnumEditor
                || IsNumericEditor
                || IsStringEditor
                || IsKeybindingEditor
                || IsNotificationEditor);
        useDefaultCommand.RaiseCanExecuteChanged();
        revertDraftCommand.RaiseCanExecuteChanged();
        addKeybindingCommand.RaiseCanExecuteChanged();
        removeKeybindingCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(SavedHasOverride));
        OnPropertyChanged(nameof(DraftHasOverride));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(HasOverflowActions));
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
        OnPropertyChanged(nameof(SavedValueText));
        OnPropertyChanged(nameof(RevertDraftAvailability));
        OnPropertyChanged(nameof(RevertDraftAutomationName));
        OnPropertyChanged(nameof(RevertDraftAutomationHelp));
        OnPropertyChanged(nameof(UseDefaultLabel));
        OnPropertyChanged(nameof(UseDefaultAvailability));
    }

    private bool ReadBooleanValue()
    {
        if (valueState.DraftValue is bool boolean)
        {
            return boolean;
        }

        if (valueState.DraftValue is string text
            && bool.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return Setting.DefaultValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            && Setting.DefaultValue.GetBoolean();
    }

    private string ReadEnumValue()
    {
        if (TryReadValidEnumValue(valueState.DraftValue, out var value))
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
                valueState.DraftHasOverride ? valueState.DraftValue as string : null)
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

        OnPropertyChanged(nameof(NotificationSystem));
        OnPropertyChanged(nameof(NotificationAudio));
        OnPropertyChanged(nameof(NotificationSound));
        OnPropertyChanged(nameof(CanSelectNotificationSound));
        OnPropertyChanged(nameof(NotificationDeliverySummary));
        OnPropertyChanged(nameof(NotificationNeedsAttention));
        OnPropertyChanged(nameof(NotificationPolicyHelp));
        OnPropertyChanged(nameof(EditorAutomationName));
    }

    private void UseDefault()
    {
        if (RunWithClearedEditorDraft(() => stageRemove(Setting)))
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

    private void RevertDraft()
    {
        if (RunWithClearedEditorDraft(() => revertDraft(Setting)))
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

    private bool RunWithClearedEditorDraft(Func<bool> operation)
    {
        var hadDraft = editorDraftStore.TryGet(Path, out var draft);
        editorDraftStore.Remove(Path);
        if (operation())
        {
            setInputValidity(Setting, true);
            return true;
        }

        if (hadDraft && draft is not null)
        {
            editorDraftStore.Set(Path, draft.RawText, draft.ParseIssue);
            RestoreEditorDraft();
        }

        return false;
    }

    internal void SetKeybindingConflict(string? message)
    {
        if (string.Equals(keybindingConflictMessage, message, StringComparison.Ordinal))
        {
            return;
        }

        keybindingConflictMessage = message;
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

    private void RemoveKeybinding(string canonicalChord)
    {
        if (!CanEdit)
        {
            return;
        }

        var current = CurrentKeybinding();
        var remaining = current.Chords
            .Where(chord => !string.Equals(chord.Canonical, canonicalChord, StringComparison.Ordinal))
            .Select(chord => chord.Canonical)
            .ToArray();
        if (remaining.Length == current.Chords.Count)
        {
            return;
        }

        var normalized = remaining.Length == 0
            ? "NONE"
            : string.Join('|', remaining);
        if (stageValue(Setting, LauncherTomlValue.RenderString(normalized)))
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
        if (!IsKeybindingEditor || valueState.DraftValue is not string text)
        {
            return false;
        }

        if (DraftHasOverride
            && LauncherTomlValue.TryReadString(text, out var parsedText))
        {
            text = parsedText;
        }

        binding = LauncherKeybindingValue.Parse(text);
        return true;
    }

    private void NotifyKeybindingStateChanged()
    {
        OnPropertyChanged(nameof(KeybindingDisplay));
        OnPropertyChanged(nameof(KeybindingChords));
        OnPropertyChanged(nameof(IsKeybindingUnbound));
        OnPropertyChanged(nameof(KeybindingNeedsAttention));
        OnPropertyChanged(nameof(KeybindingValidationMessage));
        OnPropertyChanged(nameof(EditorAutomationName));
    }

    private string ReadStringText()
    {
        if (TryReadValidStringValue(valueState.DraftValue, out var value))
        {
            return value;
        }

        return ReadDefaultStringText();
    }

    private string ReadDefaultStringText() =>
        Setting.DefaultValue.ValueKind == JsonValueKind.String
            ? Setting.DefaultValue.GetString() ?? string.Empty
            : string.Empty;

    private void RestoreEditorDraft()
    {
        numericDraft = null;
        numericValidationError = null;
        stringDraft = null;
        stringValidationError = null;
        if (editorDraftStore.TryGet(Path, out var draft) && draft is not null)
        {
            if (IsNumericEditor)
            {
                numericDraft = draft.RawText;
                numericValidationError = draft.ParseIssue;
            }
            else if (IsStringEditor)
            {
                stringDraft = draft.RawText;
                stringValidationError = draft.ParseIssue;
            }
        }

        setInputValidity(
            Setting,
            numericValidationError is null && stringValidationError is null);
    }

    private bool TryReadValidStringValue(object? candidate, out string value)
    {
        if (candidate is not string text)
        {
            value = string.Empty;
            return false;
        }

        if (DraftHasOverride)
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
        if (TryReadValidNumericValue(valueState.DraftValue, out var rendered))
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

        var presentationOptions = setting.Presentation.EnumOptions
            .ToDictionary(option => option.Value, StringComparer.Ordinal);
        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .Select(value =>
            {
                var rawValue = value!;
                return presentationOptions.TryGetValue(rawValue, out var option)
                    ? new SettingsEnumOption(
                        rawValue,
                        option.Label,
                        option.Help ?? string.Empty)
                    : new SettingsEnumOption(rawValue, FormatCategory(rawValue), string.Empty);
            })
            .ToArray();
    }

    private static string FormatMetadata(object? value)
    {
        var formatted = FormatValue(value);
        return formatted == "Not specified" ? "Unspecified" : formatted;
    }

    private string FormatStateValue(object? value, bool hasOverride)
    {
        if (!hasOverride)
        {
            return DefaultValue;
        }

        if (value is not string rendered)
        {
            return FormatValue(value);
        }

        if (IsBooleanEditor && bool.TryParse(rendered, out var boolean))
        {
            return boolean ? "On" : "Off";
        }

        if ((IsEnumEditor || IsStringEditor || IsKeybindingEditor)
            && LauncherTomlValue.TryReadString(rendered, out var text))
        {
            return IsEnumEditor ? FormatCategory(text) : text;
        }

        if (IsNotificationEditor)
        {
            var parsed = LauncherNotificationPolicyParser.Parse(Setting, rendered);
            if (parsed.IsValid)
            {
                var channels = new List<string>();
                if (parsed.Policy.System)
                {
                    channels.Add("system");
                }

                if (parsed.Policy.Audio)
                {
                    channels.Add($"{FormatCategory(parsed.Policy.Sound)} sound");
                }

                return channels.Count == 0 ? "Off" : string.Join(" and ", channels);
            }
        }

        return rendered;
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
    string Label,
    string Help)
{
    public bool HasHelp => !string.IsNullOrWhiteSpace(Help);
}

public sealed record SettingsKeybindingChord(
    string Canonical,
    string Display,
    string OwnerTitle)
{
    public string RemoveAutomationName => $"Remove {Display} from {OwnerTitle}";
}
