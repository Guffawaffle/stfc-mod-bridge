using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public enum SyncBooleanOverrideChoice
{
    UseGlobal,
    Enabled,
    Disabled,
}

public sealed record SyncOverrideChoice(SyncBooleanOverrideChoice Value, string Label);

public sealed record SyncFleetRuntimeModeOption(string Value, string Label);

public sealed class SyncWorkspaceViewModel : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<SyncOverrideChoice> OverrideChoices =
    [
        new(SyncBooleanOverrideChoice.UseGlobal, "Use inherited"),
        new(SyncBooleanOverrideChoice.Enabled, "On"),
        new(SyncBooleanOverrideChoice.Disabled, "Off"),
    ];

    private readonly Func<string?> configurationPathProvider;
    private readonly Func<bool> hasSiblingPendingChanges;
    private readonly IConfigurationRepository repository;
    private readonly SettingsActionCommand discardCommand;
    private readonly AsyncSettingsActionCommand saveCommand;
    private SyncTopologyPersistenceWorkspace? workspace;
    private string operationStatus = string.Empty;
    private string newTargetName = string.Empty;
    private bool migrateLegacyRoot;

    public SyncWorkspaceViewModel(
        Func<string?> configurationPathProvider,
        IConfigurationRepository repository,
        Func<bool>? hasSiblingPendingChanges = null)
    {
        this.configurationPathProvider =
            configurationPathProvider ?? throw new ArgumentNullException(nameof(configurationPathProvider));
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.hasSiblingPendingChanges = hasSiblingPendingChanges ?? (() => false);
        discardCommand = new(Discard, () => HasPendingChanges);
        saveCommand = new(SaveAsync, () => CanSave);
        AddSidecarCommand = new SettingsActionCommand(AddSidecar, () => CanAddSidecar);
        AddMajelCommand = new SettingsActionCommand(() => AddExternal(SyncTargetKind.MajelIngest));
        AddLegacyCommand = new SettingsActionCommand(() => AddExternal(SyncTargetKind.LegacyCommunity));
        AddSpocksClubCommand = new SettingsActionCommand(() => AddPreset("spocks_club"));
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? StateChanged;

    public event EventHandler? Committed;

    public ObservableCollection<SyncTargetCardViewModel> Targets { get; } = [];

    public ObservableCollection<SyncGlobalFeedViewModel> GlobalFeeds { get; } = [];

    internal static IReadOnlyList<SyncOverrideChoice> BooleanOverrideChoices => OverrideChoices;

    public ICommand AddSidecarCommand { get; }

    public ICommand AddMajelCommand { get; }

    public ICommand AddLegacyCommand { get; }

    public ICommand AddSpocksClubCommand { get; }

    public ICommand DiscardCommand => discardCommand;

    public ICommand SaveCommand => saveCommand;

    public bool IsConfigurationReady => workspace is not null;

    public bool HasPendingChanges => workspace?.HasPendingChanges ?? false;

    public bool IsStale => workspace?.IsStale ?? false;

    public bool IsCommittable =>
        workspace is not null
        && workspace.Desired.Resolve().IsCommittable
        && workspace.PreparePlan(MigrateLegacyRoot).IsValid;

    public bool HasLegacyRootTarget => workspace?.HasLegacyRootTarget ?? false;

    public bool MigrateLegacyRoot
    {
        get => migrateLegacyRoot;
        set
        {
            if (SetField(ref migrateLegacyRoot, value))
            {
                Rebuild();
            }
        }
    }

    public bool CanSave =>
        IsConfigurationReady
        && HasPendingChanges
        && IsCommittable
        && !IsStale
        && !hasSiblingPendingChanges();

    public bool CanAddSidecar =>
        workspace is not null
        && !workspace.Desired.Targets.Values.Any(target => target.Kind == SyncTargetKind.LocalSidecar);

    public string ConfigurationStatus => IsConfigurationReady
        ? "Changes are staged until Save. The running game keeps its startup topology until restart."
        : "Select a game folder with a supported configuration to set up sync.";

    public string PendingChangesText => HasPendingChanges ? "Unsaved sync changes" : "No unsaved sync changes";

    public string SaveAvailability => IsStale
        ? "The TOML changed outside the launcher. Reload before saving."
        : hasSiblingPendingChanges()
            ? "Save or discard the pending non-sync settings before saving sync setup."
        : !IsCommittable
            ? "Fix the target validation errors before saving."
            : CanSave
                ? "Save all staged sync changes atomically."
                : "Stage a valid sync change before saving.";

    public string OperationStatus
    {
        get => operationStatus;
        private set => SetField(ref operationStatus, value);
    }

    public string NewTargetName
    {
        get => newTargetName;
        set => SetField(ref newTargetName, value?.Trim() ?? string.Empty);
    }

    public string GlobalProxy
    {
        get => workspace?.Desired.GlobalDefaults.Proxy ?? string.Empty;
        set
        {
            if (workspace is null || value == GlobalProxy)
            {
                return;
            }

            Stage(workspace.Desired.WithGlobalDefaults(workspace.Desired.GlobalDefaults.WithProxy(value ?? string.Empty)));
        }
    }

    public bool GlobalVerifySsl
    {
        get => workspace?.Desired.GlobalDefaults.VerifySsl ?? true;
        set
        {
            if (workspace is null || value == GlobalVerifySsl)
            {
                return;
            }

            Stage(workspace.Desired.WithGlobalDefaults(workspace.Desired.GlobalDefaults.WithVerifySsl(value)));
        }
    }

    public bool GlobalUnsafeTls
    {
        get => workspace?.Desired.GlobalDefaults.AllowUnsafeTlsWithoutCertificateValidation ?? false;
        set
        {
            if (workspace is null || value == GlobalUnsafeTls)
            {
                return;
            }

            Stage(workspace.Desired.WithGlobalDefaults(workspace.Desired.GlobalDefaults.WithUnsafeTls(value)));
        }
    }

    public void Reload()
    {
        if (HasPendingChanges)
        {
            return;
        }

        workspace = null;
        var read = repository.Read(configurationPathProvider());
        if (!read.IsSuccess || read.Snapshot is null)
        {
            OperationStatus = read.State == ConfigurationRepositoryReadState.Invalid
                ? $"Sync setup is unavailable because the TOML is unsafe to edit: {read.ValidationError?.Message}"
                : read.State == ConfigurationRepositoryReadState.IoFailure
                    ? $"Sync setup is unavailable: {read.Error}"
                    : string.Empty;
            Rebuild();
            return;
        }

        var load = SyncTopologyPersistenceWorkspace.Load(read.Snapshot, out workspace);
        if (!load.IsValid || workspace is null)
        {
            OperationStatus = $"Sync setup is unavailable because the topology could not be loaded: {load.Error?.Message}";
        }
        else
        {
            OperationStatus = load.Diagnostics.FirstOrDefault(item => item.Severity == SyncTopologyDiagnosticSeverity.Error)?.Message
                ?? string.Empty;
        }

        Rebuild();
    }

    internal SyncTargetDraft? GetTarget(string name) =>
        workspace?.Desired.Targets.GetValueOrDefault(name);

    internal SyncResolvedTarget? GetResolvedTarget(string name) =>
        workspace?.Desired.Resolve().Targets.FirstOrDefault(target => target.Name == name);

    internal bool GetGlobalFeed(SyncDataKind kind) =>
        workspace?.Desired.GlobalDefaults.DataKinds.GetValueOrDefault(kind) ?? false;

    internal string GetGlobalProxy() => workspace?.Desired.GlobalDefaults.Proxy ?? string.Empty;

    internal bool GetGlobalVerifySsl() => workspace?.Desired.GlobalDefaults.VerifySsl ?? true;

    internal bool GetGlobalUnsafeTls() =>
        workspace?.Desired.GlobalDefaults.AllowUnsafeTlsWithoutCertificateValidation ?? false;

    internal IReadOnlyList<SyncTopologyDiagnostic> GetDiagnostics(string name) =>
        workspace?.Desired.Resolve().Diagnostics.Where(item => item.TargetName == name).ToArray() ?? [];

    internal void UpdateTarget(string name, Func<SyncTargetDraft, SyncTargetDraft> update)
    {
        if (workspace is null)
        {
            return;
        }

        Apply(workspace.Desired.UpdateTarget(name, update));
    }

    internal void RemoveTarget(string name)
    {
        if (workspace is not null)
        {
            Apply(workspace.Desired.RemoveTarget(name));
        }
    }

    internal void SetGlobalFeed(SyncDataKind kind, bool enabled)
    {
        if (workspace is null)
        {
            return;
        }

        Stage(workspace.Desired.WithGlobalDefaults(workspace.Desired.GlobalDefaults.WithDataKind(kind, enabled)));
    }

    private void AddSidecar()
    {
        if (workspace is not null)
        {
            Apply(workspace.Desired.AddTarget(SyncDesiredTopology.LocalSidecarIdentity, SyncTargetKind.LocalSidecar));
        }
    }

    private void AddPreset(string preset)
    {
        if (workspace is null)
        {
            return;
        }

        Apply(workspace.Desired.AddPreset(preset), enableExternal: true);
    }

    private void AddExternal(SyncTargetKind kind)
    {
        if (workspace is null)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(NewTargetName)
            ? kind == SyncTargetKind.MajelIngest ? "majel" : "community"
            : NewTargetName;
        Apply(workspace.Desired.AddTarget(name, kind), enableExternal: true);
    }

    private void Apply(SyncTopologyTransitionResult transition, bool enableExternal = false)
    {
        if (!transition.Succeeded)
        {
            OperationStatus = transition.Diagnostic?.Message ?? "The sync change could not be staged.";
            return;
        }

        var desired = transition.Topology;
        if (enableExternal)
        {
            var added = desired.Targets.Values.ExceptBy(workspace!.Desired.Targets.Keys, target => target.Name).Single();
            desired = desired.SetTargetEnabled(added.Name, true).Topology;
            NewTargetName = string.Empty;
        }

        Stage(desired);
    }

    private void Stage(SyncDesiredTopology desired)
    {
        workspace!.Stage(desired);
        OperationStatus = string.Empty;
        Rebuild();
    }

    private void Discard()
    {
        workspace?.Discard();
        OperationStatus = "Unsaved sync changes discarded.";
        Rebuild();
    }

    private async Task SaveAsync()
    {
        if (workspace is null)
        {
            return;
        }

        OperationStatus = "Saving sync setup…";
        var result = await workspace.CommitAsync(MigrateLegacyRoot);
        OperationStatus = result.State switch
        {
            AtomicTomlWriteState.Succeeded => "Sync setup saved. Restart the game to activate the new topology.",
            AtomicTomlWriteState.NoChange => "No sync changes were needed.",
            AtomicTomlWriteState.Conflict => "The TOML changed outside the launcher. External edits were preserved; reload before saving.",
            AtomicTomlWriteState.Invalid => $"Nothing was written: {result.ValidationError?.Message ?? FirstPlanDiagnostic(result)}",
            _ => $"Sync setup could not be saved: {result.Error}",
        };
        if (result.State is AtomicTomlWriteState.Succeeded or AtomicTomlWriteState.NoChange)
        {
            migrateLegacyRoot = false;
            Committed?.Invoke(this, EventArgs.Empty);
        }
        Rebuild();
    }

    private void Rebuild()
    {
        Targets.Clear();
        GlobalFeeds.Clear();
        if (workspace is not null)
        {
            foreach (var kind in Enum.GetValues<SyncDataKind>().Where(kind => kind != SyncDataKind.FleetRuntime))
            {
                GlobalFeeds.Add(new(this, kind));
            }

            foreach (var target in workspace.Desired.Targets.Values.OrderBy(target => target.Name, StringComparer.Ordinal))
            {
                Targets.Add(new(this, target.Name));
            }
        }

        OnPropertyChanged(string.Empty);
        discardCommand.RaiseCanExecuteChanged();
        saveCommand.RaiseCanExecuteChanged();
        (AddSidecarCommand as SettingsActionCommand)?.RaiseCanExecuteChanged();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string? FirstPlanDiagnostic(SyncTopologyPersistenceCommitResult result) =>
        result.Plan is { Diagnostics.Count: > 0 } plan ? plan.Diagnostics[0].Message : null;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}

public sealed class SyncGlobalFeedViewModel(SyncWorkspaceViewModel owner, SyncDataKind kind) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Label => SyncPresentation.FeedLabel(kind);
    public bool IsEnabled
    {
        get => owner.IsConfigurationReady && owner.GetGlobalFeed(kind);
        set
        {
            owner.SetGlobalFeed(kind, value);
            PropertyChanged?.Invoke(this, new(nameof(IsEnabled)));
        }
    }
}

public sealed class SyncTargetCardViewModel : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<SyncFleetRuntimeModeOption> FleetRuntimeModes =
    [
        new(string.Empty, "Use target default (Normal)"),
        new("normal", "Normal"),
        new("request_only", "Requests only"),
        new("snapshot_only", "Snapshots only"),
        new("enqueue_no_transport", "Queue without transport"),
    ];

    private readonly SyncWorkspaceViewModel owner;
    private readonly string name;
    private string replacementToken = string.Empty;

    internal SyncTargetCardViewModel(SyncWorkspaceViewModel owner, string name)
    {
        this.owner = owner;
        this.name = name;
        RemoveCommand = new SettingsActionCommand(() => owner.RemoveTarget(name));
        UseInheritedProxyCommand = new SettingsActionCommand(
            () => owner.UpdateTarget(name, target => target.WithProxy(SyncOverride.Inherited<string>())));
        ClearTokenCommand = new SettingsActionCommand(ClearToken);
        ReplaceTokenCommand = new SettingsActionCommand(ReplaceToken, () => !string.IsNullOrWhiteSpace(replacementToken));
        Feeds = SyncTargetTypeCatalog.Get(Draft.Kind).SupportedDataKinds
            .OrderBy(SyncPresentation.FeedLabel, StringComparer.Ordinal)
            .Select(kind => new SyncTargetFeedViewModel(owner, name, kind))
            .ToArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private SyncTargetDraft Draft => owner.GetTarget(name)!;
    public string Name => name;
    public string KindLabel => SyncPresentation.KindLabel(Draft.Kind);
    public string WireContract => SyncTargetTypeCatalog.Get(Draft.Kind).WireContract;
    public bool CanDisable => Draft.Kind == SyncTargetKind.LocalSidecar;
    public bool ShowSidecarControls => Draft.Kind == SyncTargetKind.LocalSidecar;
    public bool IsEnabled
    {
        get => Draft.Enabled;
        set { owner.UpdateTarget(name, target => target.WithEnabled(value)); NotifyAll(); }
    }
    public string Url
    {
        get => Draft.Url;
        set { owner.UpdateTarget(name, target => target.WithConnection(value ?? string.Empty, target.Token)); NotifyAll(); }
    }
    public string TokenStatus => Draft.Token.IsConfigured ? "Saved token configured" : "No token configured";
    public string ProxyText
    {
        get => Draft.Proxy.IsExplicit ? Draft.Proxy.Value : string.Empty;
        set { owner.UpdateTarget(name, target => target.WithProxy(SyncOverride.Explicit(value ?? string.Empty))); NotifyAll(); }
    }
    public string ProxySummary
    {
        get
        {
            if (!Draft.Proxy.IsExplicit)
            {
                var inherited = SyncTargetTypeCatalog.Get(Draft.Kind).InheritsGlobalSync ? owner.GetGlobalProxy() : string.Empty;
                return $"Inherited: {(string.IsNullOrEmpty(inherited) ? "no proxy" : inherited)}";
            }

            return string.IsNullOrEmpty(Draft.Proxy.Value) ? "Explicitly cleared" : "Target override";
        }
    }
    public SyncBooleanOverrideChoice VerifySslChoice
    {
        get => ToChoice(Draft.VerifySsl);
        set { owner.UpdateTarget(name, target => target.WithVerifySsl(FromChoice(value))); NotifyAll(); }
    }
    public SyncBooleanOverrideChoice UnsafeTlsChoice
    {
        get => ToChoice(Draft.AllowUnsafeTlsWithoutCertificateValidation);
        set { owner.UpdateTarget(name, target => target.WithUnsafeTls(FromChoice(value))); NotifyAll(); }
    }
    public SyncBooleanOverrideChoice BattlelogEnrichmentChoice
    {
        get => ToChoice(Draft.BattlelogEnrichment);
        set { owner.UpdateTarget(name, target => target.WithBattlelogEnrichment(FromChoice(value))); NotifyAll(); }
    }
    public string FleetRuntimeModeChoice
    {
        get => Draft.FleetRuntimeMode.IsExplicit ? Draft.FleetRuntimeMode.Value : string.Empty;
        set
        {
            owner.UpdateTarget(
                name,
                target => target.WithFleetRuntimeMode(
                    string.IsNullOrEmpty(value) ? SyncOverride.Inherited<string>() : SyncOverride.Explicit(value)));
            NotifyAll();
        }
    }
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "WPF binds this property through each target card instance.")]
    public IReadOnlyList<SyncOverrideChoice> BooleanChoices => SyncWorkspaceViewModel.BooleanOverrideChoices;

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "WPF binds this property through each target card instance.")]
    public IReadOnlyList<SyncFleetRuntimeModeOption> FleetRuntimeModeChoices => FleetRuntimeModes;
    public string ValidationSummary
    {
        get
        {
            var errors = owner.GetDiagnostics(name).Where(item => item.Severity == SyncTopologyDiagnosticSeverity.Error).ToArray();
            return errors.Length == 0 ? "Ready" : string.Join(" ", errors.Select(item => item.Message));
        }
    }
    public bool HasValidationError => owner.GetDiagnostics(name).Any(item => item.Severity == SyncTopologyDiagnosticSeverity.Error);
    public string EffectiveFeeds => string.Join(", ", Feeds.Where(feed => feed.EffectiveEnabled).Select(feed => feed.Label).DefaultIfEmpty("None"));
    public IReadOnlyList<SyncTargetFeedViewModel> Feeds { get; }
    public ICommand RemoveCommand { get; }
    public ICommand UseInheritedProxyCommand { get; }
    public ICommand ClearTokenCommand { get; }
    public ICommand ReplaceTokenCommand { get; }

    public void SetReplacementToken(string value)
    {
        replacementToken = value ?? string.Empty;
        (ReplaceTokenCommand as SettingsActionCommand)?.RaiseCanExecuteChanged();
    }

    private void ReplaceToken()
    {
        owner.UpdateTarget(name, target => target.WithConnection(target.Url, SyncSecret.FromPlainText(replacementToken)));
        replacementToken = string.Empty;
        NotifyAll();
    }

    private void ClearToken()
    {
        owner.UpdateTarget(name, target => target.WithConnection(target.Url, SyncSecret.Missing));
        replacementToken = string.Empty;
        NotifyAll();
    }

    private void NotifyAll() => PropertyChanged?.Invoke(this, new(string.Empty));

    private static SyncBooleanOverrideChoice ToChoice(SyncOverride<bool> value) =>
        !value.IsExplicit
            ? SyncBooleanOverrideChoice.UseGlobal
            : value.Value ? SyncBooleanOverrideChoice.Enabled : SyncBooleanOverrideChoice.Disabled;

    private static SyncOverride<bool> FromChoice(SyncBooleanOverrideChoice value) => value switch
    {
        SyncBooleanOverrideChoice.Enabled => SyncOverride.Explicit(true),
        SyncBooleanOverrideChoice.Disabled => SyncOverride.Explicit(false),
        _ => SyncOverride.Inherited<bool>(),
    };
}

public sealed class SyncTargetFeedViewModel(
    SyncWorkspaceViewModel owner,
    string targetName,
    SyncDataKind kind) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Label => SyncPresentation.FeedLabel(kind);
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "WPF binds this property through each feed row instance.")]
    public IReadOnlyList<SyncOverrideChoice> Choices => SyncWorkspaceViewModel.BooleanOverrideChoices;
    public SyncBooleanOverrideChoice Choice
    {
        get
        {
            var target = owner.GetTarget(targetName)!;
            var configured = target.DataOverrides.GetValueOrDefault(kind, SyncOverride.Inherited<bool>());
            return !configured.IsExplicit
                ? SyncBooleanOverrideChoice.UseGlobal
                : configured.Value ? SyncBooleanOverrideChoice.Enabled : SyncBooleanOverrideChoice.Disabled;
        }
        set
        {
            var configured = value switch
            {
                SyncBooleanOverrideChoice.Enabled => SyncOverride.Explicit(true),
                SyncBooleanOverrideChoice.Disabled => SyncOverride.Explicit(false),
                _ => SyncOverride.Inherited<bool>(),
            };
            owner.UpdateTarget(targetName, target => target.WithDataOverride(kind, configured));
            PropertyChanged?.Invoke(this, new(string.Empty));
        }
    }
    public bool EffectiveEnabled
    {
        get
        {
            var target = owner.GetTarget(targetName)!;
            var configured = target.DataOverrides.GetValueOrDefault(kind, SyncOverride.Inherited<bool>());
            var inherited = SyncTargetTypeCatalog.Get(target.Kind).InheritsGlobalSync
                && owner.GetGlobalFeed(kind);
            return configured.IsExplicit ? configured.Value : inherited;
        }
    }
    public string EffectiveSummary => $"Effective: {(EffectiveEnabled ? "On" : "Off")} · {(Choice == SyncBooleanOverrideChoice.UseGlobal ? "inherited" : "target override")}";
}

internal static class SyncPresentation
{
    public static string KindLabel(SyncTargetKind kind) => kind switch
    {
        SyncTargetKind.LocalSidecar => "Local Sidecar",
        SyncTargetKind.MajelIngest => "Majel",
        _ => "Community / legacy",
    };

    public static string FeedLabel(SyncDataKind kind) => string.Concat(
        kind.ToString().SelectMany((character, index) => index > 0 && char.IsUpper(character) ? new[] { ' ', character } : new[] { character }));
}
