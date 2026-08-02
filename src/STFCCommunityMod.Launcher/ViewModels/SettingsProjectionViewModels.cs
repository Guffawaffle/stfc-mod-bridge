using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace STFCCommunityMod.Launcher.ViewModels;

public abstract class SettingsListItemViewModel;

public sealed class SettingsGroupHeaderViewModel(string label) :
    SettingsListItemViewModel
{
    public string Label { get; } = label;
}

public sealed class SettingsFamilyHeaderViewModel(
    string id,
    string label,
    string description) :
    SettingsListItemViewModel
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public string Description { get; } = description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

public sealed class AdvancedPatchEditingGateViewModel : SettingsListItemViewModel
{
    public const string Warning =
        "Patch changes can alter runtime behavior, and invalid combinations may make the mod or game unstable. "
        + "Prefer ordinary settings when they are available. Changes remain staged until you save them.";

    public AdvancedPatchEditingGateViewModel(
        bool isUnlocked,
        IReadOnlyList<AdvancedPatchSummaryItemViewModel> summaries,
        System.Windows.Input.ICommand enableCommand,
        System.Windows.Input.ICommand lockCommand)
    {
        IsUnlocked = isUnlocked;
        Summaries = summaries;
        EnableCommand = enableCommand;
        LockCommand = lockCommand;
        WarningText = Warning;
    }

    public bool IsUnlocked { get; }

    public bool IsLocked => !IsUnlocked;

    public string StateTitle => IsUnlocked ? "Patch editing enabled" : "Patch editing locked";

    public string StateDescription => IsUnlocked
        ? "Patch controls are available for this launcher session. Lock them again when you are finished editing."
        : "Review the current effective values below. A deliberate acknowledgement is required before editor controls are created.";

    public string StateAutomationName =>
        $"{StateTitle}. {Summaries.Count} provider-supported patch settings.";

    public string WarningText { get; }

    public IReadOnlyList<AdvancedPatchSummaryItemViewModel> Summaries { get; }

    public int SettingCount => Summaries.Count;

    public System.Windows.Input.ICommand EnableCommand { get; }

    public System.Windows.Input.ICommand LockCommand { get; }
}

public sealed record AdvancedPatchSummaryItemViewModel(
    string Title,
    string EffectiveValue,
    bool IsDirty)
{
    public string StateText => IsDirty ? $"{EffectiveValue} · Unsaved" : EffectiveValue;

    public string AutomationName => $"{Title}, effective value {StateText}";
}

public sealed record SettingsProjectionSnapshot(
    int Revision,
    int ConstructedRowCount,
    int GroupHeaderCount,
    int FamilyHeaderCount,
    IReadOnlyList<string> ConstructedSettingPaths);

internal sealed record SettingsEditorDraft(
    string RawText,
    string ParseIssue);

internal sealed class SettingsEditorDraftStore
{
    private readonly Dictionary<string, SettingsEditorDraft> drafts =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string fieldId, out SettingsEditorDraft? draft) =>
        drafts.TryGetValue(fieldId, out draft);

    public void Set(string fieldId, string rawText, string parseIssue)
    {
        drafts[fieldId] = new(rawText, parseIssue);
    }

    public bool Remove(string fieldId) =>
        drafts.Remove(fieldId);

    public void Clear() =>
        drafts.Clear();
}

internal sealed class SettingsProjectionCollection :
    ObservableCollection<SettingsListItemViewModel>
{
    public void ReplaceAll(IEnumerable<SettingsListItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Reset));
    }
}
