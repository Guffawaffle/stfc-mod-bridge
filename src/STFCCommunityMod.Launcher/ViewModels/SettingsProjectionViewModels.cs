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
