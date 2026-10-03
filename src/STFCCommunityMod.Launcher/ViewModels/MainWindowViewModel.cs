using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.Services;

namespace STFCCommunityMod.Launcher.ViewModels;

internal sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    internal static readonly TimeSpan RefreshActionStatusLifetime = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan ModActionStatusLifetime = TimeSpan.FromSeconds(3);

    private readonly LauncherEnvironmentProbe environmentProbe;
    private readonly IModManagementCoordinator modManagementCoordinator;
    private readonly GameLaunchHandoffCoordinator gameLaunchCoordinator;
    private readonly IGameProcessInspector gameProcessInspector;
    private readonly LauncherDiagnosticService diagnosticService;
    private Func<string?, LauncherConfigurationCatalog>? configurationCatalogResolver;
    private Func<string?, LauncherConfigurationDiagnosisEvidence>? configurationEvidenceProvider;

    internal LauncherConfigurationCatalog ConfigurationCatalog =>
        configurationCatalogResolver?.Invoke(ConfigurationGameDirectory)
        ?? throw new InvalidOperationException("Installed configuration resolution is unavailable.");

    internal LauncherConfigurationDiagnosisEvidence ConfigurationEvidence =>
        configurationEvidenceProvider?.Invoke(ConfigurationGameDirectory)
        ?? throw new InvalidOperationException("Installed configuration evidence is unavailable.");
    private readonly LauncherSelfUpdateService launcherSelfUpdateService;
    private readonly ILauncherReleaseDiscoveryClient releaseDiscoveryClient;
    private readonly IPackagedLauncherUpdateService packagedLauncherUpdateService;
    private readonly ILauncherUiPreferencesStore uiPreferencesStore;
    private readonly NativeLauncherProfilesStore profilesStore;
    private readonly LauncherDistributionProviderCatalog distributionProviderCatalog;
    private readonly LauncherFeatureRemediationCandidates? featureRemediationCandidates;
    private readonly string selectedModSourceMetadata;
    private readonly IDiagnosticFolderService diagnosticFolderService;
    private LauncherEnvironmentSnapshot snapshot;
    private LauncherHealthSnapshot localHealth;
    private HomeHealthProjection homeHealth;
    private LauncherHomePresentation presentation;
    private ModManagementPresentation modPresentation;
    private GameLaunchPresentation launchPresentation = null!;
    private GameLaunchPresentation primeLaunchChoice = null!;
    private GameLaunchPresentation scopelyLaunchChoice = null!;
    private string selectionFeedback = string.Empty;
    private readonly LauncherActionFeedbackChannels actionFeedback = new();
    private readonly HomeActionFeedbackArbiter homeFeedback;
    private LauncherLaunchTarget selectedLaunchTarget;
    private LauncherProfilesLoadResult profilesLoad;
    private IReadOnlyList<ProfileSession> profileSessions = [];
    private IReadOnlyList<LauncherProfileCard> profileCards = [];
    private long profileGeneration;
    private LauncherWorkspaceMode workspaceMode;
    private bool isModMutationInProgress;
    private string? modOperationDirectory;
    private LauncherDiagnosticPreview? diagnosticPreview;
    private string diagnosticActionStatus = string.Empty;
    private bool isRecoveryWorkspaceTransitionPending;
    private Func<LauncherActivationPlan>? currentActivationPlan;
    private Func<LauncherBattleFeatureSnapshot>? currentBattleFeatures;
    private readonly DispatcherTimer refreshActionStatusTimer;
    private readonly DispatcherTimer modActionStatusTimer;
    private bool isDisposed;

    private LauncherProviderAtomicSwitchCoordinator? providerSwitchCoordinator;

    internal LauncherProviderAtomicSwitchCoordinator? ProviderSwitchCoordinator
    {
        get => providerSwitchCoordinator;
        private set
        {
            providerSwitchCoordinator = value;
            NotifyModPresentationChanged();
        }
    }

    internal LauncherFeatureRemediationCoordinator? FeatureRemediationCoordinator { get; private set; }

    internal Func<GameLaunchPresentation, Task<bool>>? ConfirmLaunchOverrideAsync { get; set; }
    internal Func<LauncherProfile, IReadOnlyList<ProfileSession>, Task<ProfileSession?>>? SelectProfileSessionAsync { get; set; }

    private MainWindowViewModel(
        LauncherEnvironmentProbe environmentProbe,
        IModManagementCoordinator modManagementCoordinator,
        GameLaunchHandoffCoordinator gameLaunchCoordinator,
        LauncherDiagnosticService diagnosticService,
        LauncherSelfUpdateService launcherSelfUpdateService,
        ILauncherReleaseDiscoveryClient releaseDiscoveryClient,
        IPackagedLauncherUpdateService packagedLauncherUpdateService,
        ILauncherUiPreferencesStore uiPreferencesStore,
        NativeLauncherProfilesStore profilesStore,
        GameInstallationCoordinator gameInstallationCoordinator,
        LauncherDistributionProviderCatalog distributionProviderCatalog,
        LauncherFeatureRemediationCandidates? featureRemediationCandidates,
        string modSourceMetadata,
        IDiagnosticFolderService diagnosticFolderService,
        IGameProcessInspector gameProcessInspector)
    {
        this.environmentProbe = environmentProbe;
        this.modManagementCoordinator = modManagementCoordinator;
        this.gameLaunchCoordinator = gameLaunchCoordinator;
        this.gameProcessInspector = gameProcessInspector;
        this.diagnosticService = diagnosticService;
        this.launcherSelfUpdateService = launcherSelfUpdateService;
        this.releaseDiscoveryClient = releaseDiscoveryClient;
        this.packagedLauncherUpdateService = packagedLauncherUpdateService;
        this.uiPreferencesStore = uiPreferencesStore;
        this.profilesStore = profilesStore;
        this.distributionProviderCatalog = distributionProviderCatalog;
        this.featureRemediationCandidates = featureRemediationCandidates;
        selectedModSourceMetadata = modSourceMetadata;
        this.diagnosticFolderService = diagnosticFolderService;
        var preferences = uiPreferencesStore.Load();
        selectedLaunchTarget = preferences.LaunchTarget;
        workspaceMode = preferences.WorkspaceMode;
        profilesLoad = profilesStore.Load();
        homeFeedback = new(actionFeedback.Mod, actionFeedback.Launch);
        homeFeedback.PropertyChanged += HomeFeedback_PropertyChanged;
        refreshActionStatusTimer = new(DispatcherPriority.Background)
        {
            Interval = RefreshActionStatusLifetime,
        };
        refreshActionStatusTimer.Tick += RefreshActionStatusTimer_Tick;
        modActionStatusTimer = new(DispatcherPriority.Background)
        {
            Interval = ModActionStatusLifetime,
        };
        modActionStatusTimer.Tick += ModActionStatusTimer_Tick;
        snapshot = environmentProbe.Capture();
        RefreshSessionObservations();
        GameClient = new(gameInstallationCoordinator, () => GameClientDirectory,
            () => !actionFeedback.Mod.IsWorking && !actionFeedback.Launch.IsWorking && !isRecoveryWorkspaceTransitionPending,
            RefreshCore, () => ActiveLaunchProfile?.PreferredInstallationId);
        GameClient.SetTarget(GameClientDirectory, ActiveLaunchProfile?.PreferredInstallationId);
        GameClient.PropertyChanged += GameClient_PropertyChanged;
        presentation = CaptureSelectedInstallationPresentation();
        localHealth = modManagementCoordinator.CaptureHealth(
            SelectedGameDirectory,
            presentation.IsGameRunning);
        homeHealth = HomeHealthProjection.FromSnapshot(localHealth);
        modPresentation = localHealth.ModManagement;
        RefreshLaunchPresentations();
        actionFeedback.Refresh.PropertyChanged += RefreshActionState_PropertyChanged;
        actionFeedback.Mod.PropertyChanged += ModActionState_PropertyChanged;
        actionFeedback.Launch.PropertyChanged += LaunchActionState_PropertyChanged;
        actionFeedback.LauncherUpdate.PropertyChanged += LauncherUpdateActionState_PropertyChanged;
        RefreshCommand = new ObservableActionCommand(
            actionFeedback.Refresh,
            "Refresh accepted. Checking Mod Bridge status…",
            RefreshStatusAsync,
            exception => $"Mod Bridge status refresh failed: {exception.Message}");
        LaunchPrimaryCommand = new ObservableActionCommand(
            actionFeedback.Launch,
            "Launch accepted. Starting the selected target…",
            LaunchSelectedTargetAsync,
            exception => $"The selected launch target failed: {exception.Message}");
        SelectPrimeExecutableCommand = new RelayCommand(
            () => SelectLaunchTarget(LauncherLaunchTarget.PrimeExecutable));
        SelectScopelyLauncherCommand = new RelayCommand(
            () => SelectLaunchTarget(LauncherLaunchTarget.ScopelyLauncher));
        UpdateModActionAvailability();
        UpdateLaunchActionAvailability();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        refreshActionStatusTimer.Stop();
        refreshActionStatusTimer.Tick -= RefreshActionStatusTimer_Tick;
        modActionStatusTimer.Stop();
        modActionStatusTimer.Tick -= ModActionStatusTimer_Tick;
        actionFeedback.Refresh.PropertyChanged -= RefreshActionState_PropertyChanged;
        actionFeedback.Mod.PropertyChanged -= ModActionState_PropertyChanged;
        actionFeedback.Launch.PropertyChanged -= LaunchActionState_PropertyChanged;
        actionFeedback.LauncherUpdate.PropertyChanged -= LauncherUpdateActionState_PropertyChanged;
        homeFeedback.PropertyChanged -= HomeFeedback_PropertyChanged;
        GameClient.PropertyChanged -= GameClient_PropertyChanged;
        if (featureRemediationCandidates is not null)
        {
            ObserveDisposal(featureRemediationCandidates.DisposeAsync().AsTask());
        }
        GC.SuppressFinalize(this);
    }

    public GameInstallationViewModel GameClient { get; }

    public string GameSectionStatus => presentation.GameSectionStatus;

    public string GameFolderStatus => presentation.GameFolderStatus;

    public string GameFolderIcon => presentation.GameFolderIcon;

    public LauncherHomeTone GameFolderTone => presentation.GameFolderTone;

    public string GameFolderStatusAutomationName => presentation.GameFolderStatusAutomationName;

    public string GameFolderActionLabel => presentation.GameFolderActionLabel;

    public string GameFolderActionAutomationName => presentation.GameFolderActionAutomationName;

    public bool CanChangeGameFolder => !isDisposed;

    public bool CanChangeReleaseSource => !GameClient.IsMutationInProgress
        && ResolveModContextChangeAvailability(
            ModActionKind == ModManagementActionKind.Recover,
            isModMutationInProgress || isRecoveryWorkspaceTransitionPending,
            actionFeedback.Launch.IsWorking);

    public bool CanOpenSettingsWorkspace => !isDisposed;

    public string GameClientStatus => presentation.GameClientStatus;

    public string GameClientIcon => presentation.GameClientIcon;

    public LauncherHomeTone GameClientTone => presentation.GameClientTone;

    public string GameClientStatusAutomationName => presentation.GameClientStatusAutomationName;

    public bool IsGameRunning => presentation.IsGameRunning;

    public LauncherProviderCompatibilityState ModProviderCompatibility =>
        localHealth.ProviderCompatibility;

    public bool HasUnsafeModDeploymentTransaction =>
        !primeLaunchChoice.CanExecute
        && primeLaunchChoice.NextAction == LauncherLaunchRecoveryAction.RecoverModTransaction;

    public ReviewedRuntimeActivation? ReviewedRuntimeActivation =>
        localHealth.Installation.RuntimeActivation;

    public string ModProviderCompatibilityStatus => homeHealth.ProviderCompatibilityStatus;

    public ModUpdateEvidenceState ModUpdateAvailability => localHealth.UpdateAvailability;

    public string ModUpdateAvailabilityStatus => homeHealth.UpdateAvailabilityStatus;

    public LauncherNativeEvidenceState ModGameCompatibility => localHealth.GameCompatibility;

    public string ModGameCompatibilityStatus => homeHealth.GameCompatibilityStatus;

    public LauncherNativeEvidenceState ModRuntimeActivation => localHealth.RuntimeActivation;

    public string ModRuntimeActivationStatus => homeHealth.RuntimeActivationStatus;

    public LauncherNativeEvidenceState ModNativeSupport => localHealth.NativeSupport;

    public string ModNativeSupportStatus => homeHealth.NativeSupportStatus;

    public IReadOnlyList<LauncherHealthDimension> ModHealthDimensions => localHealth.Dimensions;

    public string ModStatus => HasIncompleteProviderSwitch
        ? "Provider switch recovery required"
        : homeHealth.InstallationStatus;

    public string ModSourceMetadata => ModSourceMetadataProjection.From(
        localHealth.Installation,
        distributionProviderCatalog,
        selectedModSourceMetadata);

    public string SelectedModReleaseSource => selectedModSourceMetadata;

    public LauncherHomeTone ModTone => HasIncompleteProviderSwitch
        ? LauncherHomeTone.Error
        : modPresentation.Tone;

    public string ModActionLabel => actionFeedback.Mod.IsWorking
        ? "Working…"
        : HasIncompleteProviderSwitch
            ? "Recover"
            : modPresentation.ActionLabel;

    public string ModActionAutomationName => actionFeedback.Mod.IsWorking
        ? $"{ModActionLabel}. {actionFeedback.Mod.AutomationAnnouncement}"
        : HasIncompleteProviderSwitch
            ? "Recover. Recover the incomplete provider switch."
            : $"{ModActionLabel}. {modPresentation.AutomationName}";

    public string ModActionHelpText => actionFeedback.Mod.IsWorking
        ? actionFeedback.Mod.AutomationAnnouncement
        : HasIncompleteProviderSwitch
            ? "Recover the incomplete provider switch before changing the mod or its release source."
            : modPresentation.AutomationName;

    public bool CanManageMod => !GameClient.IsMutationInProgress && actionFeedback.Mod.IsCommandAvailable
        && !actionFeedback.Launch.IsWorking
        && (!HasIncompleteProviderSwitch || !IsGameRunning);

    public ModManagementActionKind ModActionKind => HasIncompleteProviderSwitch
        ? ModManagementActionKind.Recover
        : modPresentation.ActionKind;

    public bool CanRecoverMod =>
        !GameClient.IsMutationInProgress && ModActionKind == ModManagementActionKind.Recover
        && actionFeedback.CanStartModMaintenance(
            HasIncompleteProviderSwitch ? !IsGameRunning : modPresentation.CanExecute,
            actionFeedback.Launch.IsWorking);

    public bool CanUninstallMod => ResolveUninstallAvailability(
        localHealth.Installation, modManagementCoordinator.ProviderId,
        HasIncompleteProviderSwitch || GameClient.IsMutationInProgress
            || actionFeedback.Mod.IsWorking || actionFeedback.Launch.IsWorking);

    public bool CanStopManagingMod =>
        !GameClient.IsMutationInProgress && !HasIncompleteProviderSwitch
        && SelectedGameDirectory is not null
        && localHealth.Installation.State is (
            ModInstallationEvidenceState.ManagedVerified
            or ModInstallationEvidenceState.ManagedChanged
            or ModInstallationEvidenceState.ManagedMissing)
        && !actionFeedback.Mod.IsWorking && !actionFeedback.Launch.IsWorking;

    public string DiagnosticRecoveryAvailability
    {
        get
        {
            var recoveryDirectory = IncompleteProviderSwitchGameDirectory;
            if (recoveryDirectory is not null && !SameInstallationOrUnknown(SelectedGameDirectory, recoveryDirectory))
                return $"Provider switch recovery belongs to {recoveryDirectory}. Select that installation to review recovery; the selected installation was not retargeted.";
            if (HasIncompleteProviderSwitch)
            {
                return $"Recovery target: {recoveryDirectory ?? "unavailable; inspect saved transaction details"}. "
                    + DescribeProviderSwitchRecoveryAvailability(IsGameRunning, IncompleteProviderSwitchIncludesArtifact);
            }
            return CanRecoverMod
                ? "Recovery is available for the detected incomplete transaction."
                : ModActionKind == ModManagementActionKind.Recover
                    ? modPresentation.AutomationName
                    : "No incomplete deployment transaction is available to recover.";
        }
    }

    public string DiagnosticRemovalAvailability => CanUninstallMod
        ? "Removal is available after confirmation."
        : IsGameRunning
            ? "Close Star Trek Fleet Command before removing the managed community mod."
            : "Removal is available only for a verified Mod Bridge-managed installation owned by the selected provider.";

    public string DiagnosticStopManagingAvailability => CanStopManagingMod
        ? "Stop managing is available after confirmation. It removes only this installation's ownership receipt and does not change game files."
        : HasIncompleteProviderSwitch
            ? "Recover the incomplete provider switch before changing ownership records."
            : "Stop managing is available only when the selected installation has a Mod Bridge ownership receipt.";

    public bool CanRetryCandidateRecovery =>
        featureRemediationCandidates is not null
        && !actionFeedback.Mod.IsWorking
        && !actionFeedback.Launch.IsWorking;

    public string DiagnosticCandidateRecoveryAvailability =>
        featureRemediationCandidates is null
            ? "Candidate recovery is unavailable because no reviewed release source is configured."
            : "Use Retry candidate recovery only after an interrupted reviewed download. It removes only exact launcher-owned candidate residue and does not change the game installation.";

    public string LaunchActionLabel => actionFeedback.Launch.IsWorking
        ? "Opening…"
        : ActiveLaunchProfile is { } profile
            ? NamedProfileActionLabel(profile, gameLaunchCoordinator.CaptureSessions(profile, profileSessions))
            : "Launch unavailable";

    public string LaunchActionAutomationName => actionFeedback.Launch.IsWorking
        ? actionFeedback.Launch.AutomationAnnouncement
        : ActiveLaunchProfile is { } profile
            ? $"{LaunchActionLabel}: {launchPresentation.AutomationName}"
            : launchPresentation.AutomationName;

    public string LaunchProfileStatus => profilesLoad.State == LauncherProfilesLoadState.Invalid
        ? "Shared profile catalog needs attention"
        : ActiveLaunchProfile is { } profile
            ? launchPresentation.CanExecute
                ? $"Launch profile: {profile.Name} ({profile.Id})"
                : $"Launch profile: {profile.Name} — {launchPresentation.Reason}"
            : profilesLoad.Snapshot?.SelectedProfileId is not null
                ? $"Selected profile needs attention: {profilesLoad.Error}"
                : "Launch profile: Default";

    private LauncherProfile? ActiveLaunchProfile => profilesLoad.State == LauncherProfilesLoadState.Invalid
        ? null : profilesLoad.Snapshot?.SelectedProfile;

    public LauncherProfile? SelectedProfile => ActiveLaunchProfile;
    public string SelectedProfileName => ActiveLaunchProfile?.Name ?? "Profile unavailable";
    public bool IsDefaultProfileSelected => ActiveLaunchProfile?.IsDefault == true;
    public IReadOnlyList<LauncherProfileCard> ProfileCards => profileCards;
    public string NextLaunchInstallationLabel => SelectedGameDirectory ?? "Select installation";
    public string WorkspaceMode => workspaceMode.ToString();

    public void SetWorkspaceMode(string mode)
    {
        if (!Enum.TryParse<LauncherWorkspaceMode>(mode, out var parsed)
            || !Enum.IsDefined(parsed) || parsed.ToString() != mode)
            throw new ArgumentException("Choose ShuttleBay or Engineering.", nameof(mode));
        if (workspaceMode == parsed) return;
        workspaceMode = parsed;
        try { uiPreferencesStore.Save(uiPreferencesStore.Load() with { WorkspaceMode = parsed }); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // View preference is best-effort; no profile, draft or runtime operation is performed.
        }
        OnPropertyChanged(nameof(WorkspaceMode));
    }

    private LauncherLaunchTarget EffectiveLaunchTarget => ActiveLaunchProfile is not null
        ? LauncherLaunchTarget.PrimeExecutable : selectedLaunchTarget;

    public bool CanLaunchGame => actionFeedback.Launch.IsCommandAvailable
        && !HasConflictingInstallationMutation;

    public LauncherLaunchTarget SelectedLaunchTarget => EffectiveLaunchTarget;

    public bool IsPrimeExecutableSelected => EffectiveLaunchTarget == LauncherLaunchTarget.PrimeExecutable;

    public bool IsScopelyLauncherSelected => EffectiveLaunchTarget == LauncherLaunchTarget.ScopelyLauncher;

    public string PrimeExecutableChoiceAutomationName => BuildChoiceAutomationName(
        LauncherLaunchTarget.PrimeExecutable,
        "Launch prime.exe");

    public string ScopelyLauncherChoiceAutomationName => BuildChoiceAutomationName(
        LauncherLaunchTarget.ScopelyLauncher,
        "Open Scopely launcher");

    public string PrimeExecutableChoiceStatus => BuildChoiceStatus(LauncherLaunchTarget.PrimeExecutable);

    public string ScopelyLauncherChoiceStatus => BuildChoiceStatus(LauncherLaunchTarget.ScopelyLauncher);

    public static bool CanOpenLaunchTargetMenu => false;

    public ICommand LaunchPrimaryCommand { get; }

    public ICommand SelectPrimeExecutableCommand { get; }

    public ICommand SelectScopelyLauncherCommand { get; }

    public string ModOperationFeedback => actionFeedback.Mod.StatusText;

    public bool HasModOperationFeedback => actionFeedback.Mod.HasStatus;

    public bool IsModOperationInProgress => actionFeedback.Mod.IsWorking;

    public bool IsLaunchInProgress => actionFeedback.Launch.IsWorking;

    public string HomeOperationFeedback => homeFeedback.Text;

    public bool HasHomeOperationFeedback => homeFeedback.HasFeedback;

    public bool CanDismissHomeOperationFeedback => homeFeedback.CanDismiss;

    public void DismissHomeOperationFeedback() => homeFeedback.Dismiss();

    public string? SelectedGameDirectory
    {
        get
        {
            var directory = ResolveProfileGameDirectory(profilesLoad, snapshot.SelectedGameDirectory,
                snapshot.ConfirmedGameInstallationDirectory);
            if (directory is null || ActiveLaunchProfile is not { PreferredInstallationId.Length: > 0 } profile)
                return directory;
            try
            {
                var registered = profilesStore.InstallationPaths(profile.PreferredInstallationId);
                return registered.State == "available" && GameDirectoryIdentity.SameLocation(registered.GameDirectory, directory)
                    ? directory : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException or ArgumentException
                or System.Runtime.InteropServices.ExternalException or TypeLoadException or BadImageFormatException
                or JsonException) { return null; }
        }
    }

    public string SelectionFeedback => selectionFeedback;

    public bool HasSelectionFeedback => !string.IsNullOrWhiteSpace(selectionFeedback);

    public ICommand RefreshCommand { get; }

    public string RefreshActionLabel => actionFeedback.Refresh.IsWorking ? "_Refreshing…" : "_Refresh status";

    public string RefreshActionAutomationName => actionFeedback.Refresh.IsWorking
        ? actionFeedback.Refresh.AutomationAnnouncement
        : "Refresh Mod Bridge status";

    public string RefreshActionStatus => actionFeedback.Refresh.StatusText;

    public bool HasRefreshActionStatus => actionFeedback.Refresh.HasStatus;

    public bool CanRefresh => actionFeedback.Refresh.IsCommandAvailable;

    public string LauncherUpdateActionLabel => actionFeedback.LauncherUpdate.IsWorking
        ? "Checking for Mod Bridge update…"
        : "Check Mod Bridge _update";

    public string LauncherUpdateActionAutomationName => DescribeLauncherUpdateActionAutomationName(
        actionFeedback.LauncherUpdate.IsWorking,
        actionFeedback.LauncherUpdate.AutomationAnnouncement);

    public string LauncherUpdateFeedback => actionFeedback.LauncherUpdate.StatusText;

    public bool CanCheckLauncherUpdate => actionFeedback.LauncherUpdate.IsCommandAvailable;

    public static bool IsPackagedInstallation => WindowsPackageIdentity.IsCurrentProcessPackaged;

    public IReadOnlyList<LauncherDiagnosticFact> DiagnosticChecks =>
        diagnosticPreview?.Document.Health ?? [];

    public string DiagnosticTechnicalReport => diagnosticPreview?.RedactedJson ?? string.Empty;

    public string DiagnosticSummary => diagnosticPreview?.RedactedSummary ?? string.Empty;

    public string DiagnosticActionStatus => diagnosticActionStatus;

    public bool HasDiagnosticActionStatus => !string.IsNullOrWhiteSpace(diagnosticActionStatus);

    public bool CanOpenGameFolder => SelectedGameDirectory is not null;

    public bool CanOpenLogsFolder => SelectedGameDirectory is not null;

    public string? InitialBrowseDirectory
    {
        get
        {
            var validCandidates = snapshot.Discovery.ValidCandidates;
            return SelectedGameDirectory
                ?? (validCandidates.Count > 0 ? validCandidates[0].GameDirectory : null);
        }
    }

    public LauncherProfile? SelectedConfigurationProfile => ActiveLaunchProfile;
    public string? ConfigurationGameDirectory => SelectedGameDirectory;
    private string? GameClientDirectory => ActiveLaunchProfile is { PreferredInstallationId.Length: > 0 } profile
        ? profile.GameDirectory : SelectedGameDirectory;
    public string ConfigurationTargetLabel => ActiveLaunchProfile is { } profile
        ? $"{(profile.IsDefault ? "Windows setup" : profile.Name)} · Settings for next launch on {NextLaunchInstallationLabel}"
        : "Selected profile is unavailable";
    public string? ConfigurationFilePath => ResolveConfigurationFilePath(ActiveLaunchProfile, SelectedGameDirectory);


    internal static string? ResolveProfileGameDirectory(LauncherProfilesLoadResult load,
        string? detectedDirectory, string? confirmedDirectory)
    {
        if (load.State == LauncherProfilesLoadState.Invalid || load.Snapshot?.SelectedProfile is not { } profile)
            return null;
        if (!string.IsNullOrEmpty(profile.PreferredInstallationId) && profile.InstallationState != "available")
            return null;
        return !string.IsNullOrWhiteSpace(profile.GameDirectory) ? profile.GameDirectory
            : profile.IsDefault ? detectedDirectory ?? confirmedDirectory : null;
    }

    internal static string? ResolveConfigurationFilePath(LauncherProfile? profile, string? installationDirectory) =>
        profile is null ? null
            : profile.IsDefault
                ? installationDirectory is null ? null : Path.Combine(installationDirectory, "community_patch_settings.toml")
                : string.IsNullOrWhiteSpace(profile.ConfigPath) ? null : profile.ConfigPath;

    internal static bool ResolveUninstallAvailability(ModInstallationEvidence installation,
        string providerId, bool conflictingOperation) =>
        !conflictingOperation && !installation.IsGameRunning
        && installation.State == ModInstallationEvidenceState.ManagedVerified
        && string.Equals(installation.InstalledProviderId, providerId, StringComparison.Ordinal);

    private bool HasConflictingInstallationMutation => HasConflictingInstallationMutationFor(SelectedGameDirectory);

    private bool HasConflictingInstallationMutationFor(string? directory) =>
        (GameClient.IsMutationInProgress && SameInstallationOrUnknown(directory, GameClient.OperationTarget))
        || (isModMutationInProgress && SameInstallationOrUnknown(directory, modOperationDirectory))
        || isRecoveryWorkspaceTransitionPending;

    internal static bool IsRecoveryForInstallation(string? configurationPath, string? selectedDirectory)
    {
        if (selectedDirectory is null || string.IsNullOrWhiteSpace(configurationPath)) return true;
        try
        {
            if (!Path.IsPathFullyQualified(configurationPath)
                || !string.Equals(Path.GetFileName(configurationPath), "community_patch_settings.toml", StringComparison.OrdinalIgnoreCase))
                return true;
            var directory = Path.GetDirectoryName(Path.GetFullPath(configurationPath));
            return SameInstallationOrUnknown(selectedDirectory, directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return true; }
    }

    private static bool SameInstallationOrUnknown(string? left, string? right) =>
        left is null || right is null || GameDirectoryIdentity.SameLocation(left, right);

    private LauncherHomePresentation CaptureSelectedInstallationPresentation()
    {
        var directory = SelectedGameDirectory;
        var processState = directory is null ? GameProcessInspectionState.NotRunning
            : gameProcessInspector.Inspect(directory);
        var selected = LauncherHomePresentation.FromSnapshot(snapshot with
        {
            SelectedGameDirectory = directory,
            IsGameRunning = processState != GameProcessInspectionState.NotRunning,
            GameProcessState = processState,
        });
        return selected with
        {
            GameFolderActionLabel = "Select installation",
            GameFolderActionAutomationName = "Select installation for the selected profile's next launch",
        };
    }

    public LauncherProviderSelection? ConfigurationRuntimeSelection
    {
        get
        {
            var installed = localHealth.Installation;
            if (installed.State != ModInstallationEvidenceState.ManagedVerified
                || installed.InstalledProviderId is not { } providerId
                || installed.InstalledReleaseChannelId is not { } channelId
                || !distributionProviderCatalog.TryGetProvider(providerId, out var provider)
                || provider is null || !provider.ReleaseChannels.ContainsKey(channelId)) return null;
            return new(providerId, channelId);
        }
    }

    public bool HasCommunityFeatures => ConfigurationRuntimeSelection is { } selection
        && distributionProviderCatalog.GetProvider(selection.ProviderId)
            .GetCapabilityStatus(LauncherProviderCapabilityIds.CommunityFeatures) == LauncherProviderCapabilityStatus.Supported;

    public async Task RefreshProfileSessionsAsync(CancellationToken cancellationToken = default)
    {
        var admittedGeneration = profileGeneration;
        try
        {
            var sessions = await profilesStore.SessionsAsync(cancellationToken);
            if (isDisposed || admittedGeneration != profileGeneration) return;
            profileSessions = sessions;
            RefreshLaunchPresentations();
            NotifyLaunchPresentationChanged();
            UpdateLaunchActionAvailability();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or ArgumentException
            or System.Runtime.InteropServices.ExternalException or TypeLoadException or BadImageFormatException
            or JsonException)
        {
            if (isDisposed || admittedGeneration != profileGeneration) return;
            profileSessions = [];
            RefreshLaunchPresentations();
            NotifyLaunchPresentationChanged();
            UpdateLaunchActionAvailability();
        }
    }

    private void RefreshSessionObservations()
    {
        try { profileSessions = profilesStore.Sessions(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or ArgumentException
            or System.Runtime.InteropServices.ExternalException or TypeLoadException or BadImageFormatException
            or JsonException) { profileSessions = []; }
    }

    internal static string NamedProfileActionLabel(LauncherProfile profile, IReadOnlyList<ProfileSession> sessions) =>
        sessions.Count > 1 ? $"Choose session for {profile.Name}"
            : sessions.Count == 1 ? $"Focus {profile.Name}" : $"Launch {profile.Name}";

    internal static LauncherProfileCard ProjectProfileCard(LauncherProfile profile, string? directory,
        GameLaunchPresentation choice, IReadOnlyList<ProfileSession> sessions, bool conflictingMutation) =>
        new(profile.Id, profile.Name,
            sessions.Count > 0 ? "Running"
                : choice.NextAction == LauncherLaunchRecoveryAction.CloseRunningGame ? "Running · inspect session"
                : choice.CanExecute ? "Ready" : "Needs setup",
            sessions.Count > 0 ? NamedProfileActionLabel(profile, sessions) : choice.NextAction switch
            {
                LauncherLaunchRecoveryAction.SetUpProfileSupport => $"Set up profile support for {profile.Name}",
                LauncherLaunchRecoveryAction.SelectGameFolder => $"Select installation for {profile.Name}",
                _ => NamedProfileActionLabel(profile, sessions),
            },
            sessions.Count > 0 || (choice.CanExecute && !conflictingMutation),
            profile.IsDefault, directory, sessions,
            sessions.Count > 0 ? "Focus the exact running session; its launch installation remains unchanged." : choice.Reason,
            choice.NextAction);

    private void RefreshProfileCards()
    {
        profileCards = profilesLoad.State == LauncherProfilesLoadState.Invalid ? []
            : (profilesLoad.Snapshot?.Profiles ?? []).Where(profile => profile.State == "active")
                .OrderByDescending(profile => profile.IsDefault).ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .Select(profile =>
                {
                    var directory = !string.IsNullOrWhiteSpace(profile.GameDirectory) ? profile.GameDirectory
                        : profile.IsDefault ? snapshot.SelectedGameDirectory ?? snapshot.ConfirmedGameInstallationDirectory : null;
                    var choice = gameLaunchCoordinator.CapturePresentation(directory,
                        LauncherLaunchTarget.PrimeExecutable, requiredProfile: profile);
                    var sessions = gameLaunchCoordinator.CaptureSessions(profile, profileSessions);
                    return ProjectProfileCard(profile, directory, choice, sessions,
                        HasConflictingInstallationMutationFor(directory));
                }).ToArray();
        OnPropertyChanged(nameof(ProfileCards));
    }

    public static MainWindowViewModel CreateDefault(
        HttpClient httpClient,
        LauncherDistributionProviderCatalog distributionProviderCatalog,
        LauncherDistributionProvider distributionProvider,
        LauncherProviderReleaseChannel releaseChannel,
        string? providerResolutionFailure = null,
        ILauncherUiPreferencesStore? uiPreferencesStore = null,
        ILauncherProviderSelectionStore? providerSelectionStore = null,
        LauncherConfigurationCatalog? configurationCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(distributionProviderCatalog);
        ArgumentNullException.ThrowIfNull(distributionProvider);
        ArgumentNullException.ThrowIfNull(releaseChannel);
        var installLayout = PerUserInstallLayout.FromCurrentUser();
        var sharedProfilesStore = new NativeLauncherProfilesStore(installLayout.StateDirectory);
        uiPreferencesStore ??= new JsonLauncherUiPreferencesStore(installLayout.StateDirectory);
        var currentLauncherVersion = CurrentLauncherVersion();
        var processInspector = new SystemGameProcessInspector();
        var installDiscovery = new GameInstallDiscovery(
            new JsonGameInstallSelectionStore(installLayout.StateDirectory),
            [
                OfficialLauncherSettingsCandidateProvider.FromCurrentUser(),
                BoundedGameInstallCandidateProvider.FromCurrentMachine(),
            ]);

        var knownArtifacts = BundledLauncherProviderCatalog.LoadKnownWindowsArtifacts(
            distributionProviderCatalog);
        var reviewedReleases = BundledLauncherProviderCatalog.LoadReviewedWindowsReleases(
            distributionProviderCatalog);
        var providerComponents = distributionProviderCatalog.Providers.Values.Select(provider =>
        {
            var providerChannel = string.Equals(provider.Id, distributionProvider.Id, StringComparison.Ordinal)
                ? releaseChannel
                : provider.DefaultReleaseChannel;
            var binding = LauncherProviderModBinding.Resolve(
                provider,
                providerChannel,
                reviewedReleases,
                string.Equals(provider.Id, distributionProvider.Id, StringComparison.Ordinal)
                    ? providerResolutionFailure
                    : null);
            var repositoryReleases = binding.IsAvailable
                && binding.TrustKind == LauncherProviderArtifactTrustKind.GitHubRepositoryRelease
                    ? new NetnivRepositoryReleaseService(httpClient) : null;
            IModArtifactAuthenticityVerifier artifactVerifier = binding.IsAvailable
                ? binding.TrustKind switch
                {
                    LauncherProviderArtifactTrustKind.AuthenticodePublisher =>
                        new WindowsAuthenticodeVerifier(
                            binding.WindowsPublisher!,
                            binding.WindowsArtifactSigningIdentityEku!),
                    LauncherProviderArtifactTrustKind.GitHubRepositoryRelease => repositoryReleases!,
                    LauncherProviderArtifactTrustKind.ReviewedExactHash =>
                        new ReviewedExactHashAuthenticityVerifier(binding.ReviewedCertification!),
                    _ => new FailClosedModArtifactAuthenticityVerifier("Unsupported artifact trust kind."),
                }
                : new FailClosedModArtifactAuthenticityVerifier(binding.UnavailableReason);
            IWindowsReleaseDiscoveryClient releaseClient = binding.IsAvailable
                ? binding.DiscoveryKind switch
                {
                    LauncherProviderReleaseDiscoveryKind.ReleaseManifest =>
                        binding.ReviewedCertification is null
                            ? new GitHubWindowsReleaseClient(
                                httpClient,
                                binding.Repository,
                                binding.ManifestAssetName!)
                            : new ManifestWithReviewedFallbackReleaseClient(
                                new GitHubWindowsReleaseClient(
                                    httpClient,
                                    binding.Repository,
                                    binding.ManifestAssetName!),
                                new ReviewedGitHubReleaseAssetClient(
                                    httpClient,
                                    binding.ReviewedCertification),
                                binding.ReviewedCertification),
                    LauncherProviderReleaseDiscoveryKind.GitHubReleaseAsset =>
                        repositoryReleases is not null ? repositoryReleases
                            : new ReviewedGitHubReleaseAssetClient(httpClient, binding.ReviewedCertification!),
                    _ => new UnavailableWindowsReleaseDiscoveryClient("Unsupported release discovery kind."),
                }
                : new UnavailableWindowsReleaseDiscoveryClient(binding.UnavailableReason);
            IModArtifactDownloader artifactDownloader = repositoryReleases is not null ? repositoryReleases
                : binding.IsAvailable
                && binding.ReviewedCertification is not null
                    ? binding.DiscoveryKind == LauncherProviderReleaseDiscoveryKind.ReleaseManifest
                        ? new ManifestWithReviewedFallbackArtifactDownloader(
                            httpClient,
                            binding.ReviewedCertification)
                        : new ReviewedZipModArtifactDownloader(
                            httpClient,
                            binding.ReviewedCertification)
                    : new HttpModArtifactDownloader(httpClient);
            var providerDeployment = new ModDeploymentService(
                installLayout.StateDirectory,
                artifactDownloader,
                new WindowsModArtifactVersionReader(provider.RuntimeDistributionId),
                artifactVerifier,
                gameDirectory =>
                    processInspector.Inspect(gameDirectory) != GameProcessInspectionState.NotRunning,
                new(binding.ProviderId, binding.ReleaseChannelId, provider.RuntimeDistributionId),
                reviewedCertification: binding.ReviewedCertification,
                reviewedCertifications: reviewedReleases.ReleaseEvidence,
                profilesStore: sharedProfilesStore);
            var candidateAcquirer = binding.IsAvailable
                && repositoryReleases is null
                && binding.ReviewedCertification is not null
                    ? new ReviewedModArtifactCandidateAcquirer(
                        installLayout.StateDirectory,
                        artifactDownloader,
                        new WindowsModArtifactVersionReader(provider.RuntimeDistributionId),
                        artifactVerifier,
                        new(
                            binding.ProviderId,
                            binding.ReleaseChannelId,
                            provider.RuntimeDistributionId),
                        binding.ReviewedCertification)
                    : null;
            var providerHealth = new LauncherHealthService(
                new ModInstallationInspector(
                    providerDeployment,
                    new SystemModInstallationFileSystem(),
                    provenanceResolver: new(
                        new WindowsModBinaryVersionMetadataReader(),
                        knownArtifacts),
                    reviewedCertification: binding.ReviewedCertification),
                new(
                    binding.ProviderId,
                    binding.ReleaseChannelId,
                    provider.RuntimeDistributionId,
                    binding.IsAvailable,
                    binding.UnavailableReason));
            var management = new ModManagementCoordinator(
                providerDeployment,
                releaseClient,
                currentLauncherVersion,
                binding.ReleaseChannelId,
                providerUnavailableReason: binding.UnavailableReason,
                healthService: providerHealth);
            return (
                Endpoint: new ModProviderManagementEndpoint(
                    provider.Id,
                    provider.RuntimeDistributionId,
                    management),
                SwitchEndpoint: new LauncherProviderSwitchEndpoint(provider.Id, management),
                Deployment: providerDeployment,
                CandidateEndpoint: candidateAcquirer is null
                    ? null
                    : new LauncherFeatureRemediationEndpoint(provider.Id, candidateAcquirer));
        }).ToArray();
        var providerEndpoints = providerComponents.Select(component => component.Endpoint).ToArray();
        var deploymentService = providerComponents.Single(component =>
            string.Equals(component.Endpoint.ProviderId, distributionProvider.Id, StringComparison.Ordinal)).Deployment;
        IModManagementCoordinator modManagementCoordinator = new ProviderAwareModManagementCoordinator(
            distributionProvider.Id,
            providerEndpoints);
        var candidateEndpoints = providerComponents
            .Select(component => component.CandidateEndpoint)
            .OfType<LauncherFeatureRemediationEndpoint>()
            .ToArray();
        var featureRemediationCandidates = candidateEndpoints.Length == 0
            ? null
            : new LauncherFeatureRemediationCandidates(candidateEndpoints);
        ILauncherReleaseDiscoveryClient launcherReleaseClient = new UnavailableLauncherReleaseDiscoveryClient(
            "Authenticated standalone update authorization remains disabled until release qualification is complete. "
            + "Use the signed MSIX/App Installer channel or a separately verified installer.");
        var officialLauncherService = WindowsOfficialLauncherService.FromCurrentUser();
        var launchCoordinator = new GameLaunchHandoffCoordinator(
            installLayout.StateDirectory,
            deploymentService,
            new WindowsGameExecutableLaunchService(),
            officialLauncherService,
            processInspector);
        var activeConfigurationSelection = new LauncherProviderSelection(
            distributionProvider.Id,
            releaseChannel.Id);
        var installedConfiguration = new LauncherInstalledConfigurationResolver(
            distributionProviderCatalog, reviewedReleases, deploymentService);
        LauncherConfigurationCatalog ResolveConfigurationCatalog(string? gameDirectory) =>
            installedConfiguration.ResolveCatalog(
                activeConfigurationSelection, gameDirectory, configurationCatalog);
        LauncherConfigurationDiagnosisEvidence ResolveConfigurationEvidence(string? gameDirectory) =>
            installedConfiguration.ResolveEvidence(
                activeConfigurationSelection, gameDirectory, configurationCatalog);

        var viewModel = new MainWindowViewModel(
            new LauncherEnvironmentProbe(
                processInspector,
                installLayout,
                installDiscovery),
            modManagementCoordinator,
            launchCoordinator,
            new LauncherDiagnosticService(
                deploymentService,
                officialLauncherService,
                processInspector,
                currentLauncherVersion.ToString(3),
                runtimeDistributionId: distributionProvider.RuntimeDistributionId,
                configurationEvidenceProvider: ResolveConfigurationEvidence),
            new LauncherSelfUpdateService(
                installLayout.StateDirectory,
                installLayout.ProgramDirectory,
                new HttpLauncherArchiveDownloader(httpClient),
                new WindowsAuthenticodeVerifier(
                    LauncherSelfUpdateAuthority.WindowsArtifactPublisher,
                    LauncherSelfUpdateAuthority.WindowsArtifactSigningIdentityEku),
                new WindowsLauncherArtifactIdentityReader()),
            launcherReleaseClient,
            new WindowsPackagedLauncherUpdateService(),
            uiPreferencesStore,
            sharedProfilesStore,
            new GameInstallationCoordinator(installLayout.StateDirectory),
            distributionProviderCatalog,
            featureRemediationCandidates,
            string.IsNullOrWhiteSpace(providerResolutionFailure)
                ? $"{distributionProvider.DisplayName} · {releaseChannel.DisplayName}"
                : "Source needs attention",
            new WindowsDiagnosticFolderService(),
            processInspector);
        providerSelectionStore ??= new JsonLauncherProviderSelectionStore(installLayout.StateDirectory);
        viewModel.configurationCatalogResolver = ResolveConfigurationCatalog;
        viewModel.configurationEvidenceProvider = ResolveConfigurationEvidence;
        viewModel.ProviderSwitchCoordinator = new(
            new LauncherProviderSourceSwitchService(
                distributionProviderCatalog,
                providerSelectionStore,
                installLayout.StateDirectory,
                selection => installedConfiguration.ResolveSwitchEvidence(
                    selection, activeConfigurationSelection,
                    viewModel.ConfigurationGameDirectory, configurationCatalog)),
            providerComponents.Select(component => component.SwitchEndpoint),
            installLayout.StateDirectory,
            profilesStore: sharedProfilesStore);
        return viewModel;
    }

    internal void ConfigureFeatureRemediation(
        Func<LauncherActivationPlan> currentPlan,
        Func<LauncherBattleFeatureSnapshot> battleFeatures)
    {
        ArgumentNullException.ThrowIfNull(currentPlan);
        ArgumentNullException.ThrowIfNull(battleFeatures);
        if (currentActivationPlan is not null)
        {
            throw new InvalidOperationException("Runtime feature composition is already configured for this provider session.");
        }
        currentActivationPlan = currentPlan;
        currentBattleFeatures = battleFeatures;
        if (ProviderSwitchCoordinator is null || featureRemediationCandidates is null)
        {
            return;
        }
        if (FeatureRemediationCoordinator is not null)
        {
            throw new InvalidOperationException("Feature remediation is already composed for this provider session.");
        }
        FeatureRemediationCoordinator = new(
            ProviderSwitchCoordinator,
            currentPlan,
            featureRemediationCandidates.Endpoints);
    }

    public void ConfirmManualSelection(string gameDirectory)
    {
        var candidate = environmentProbe.ConfirmManualSelection(gameDirectory);
        selectionFeedback = candidate.Validation.IsValid
            ? "Game folder saved."
            : candidate.Validation.Message;
        OnPropertyChanged(nameof(SelectionFeedback));
        OnPropertyChanged(nameof(HasSelectionFeedback));
    }

    public void Refresh()
    {
        RefreshCore();
    }

    private async Task<ObservableActionResult> RefreshStatusAsync()
    {
        var before = CaptureHomeState();
        await Task.Yield();
        RefreshCore();
        await GameClient.RefreshStatusAsync();
        await RefreshProfileSessionsAsync();
        var changed = before != CaptureHomeState();
        return changed
            ? ObservableActionResult.Changed("Mod Bridge status refreshed. The displayed status changed.")
            : ObservableActionResult.Unchanged("Mod Bridge status is up to date. No changes were found.");
    }

    private void RefreshCore()
    {
        profilesLoad = profilesStore.Load();
        profileGeneration++;
        snapshot = environmentProbe.Capture();
        RefreshSessionObservations();
        GameClient.SetTarget(GameClientDirectory, ActiveLaunchProfile?.PreferredInstallationId);
        presentation = CaptureSelectedInstallationPresentation();
        localHealth = modManagementCoordinator.CaptureHealth(
            SelectedGameDirectory,
            presentation.IsGameRunning);
        homeHealth = HomeHealthProjection.FromSnapshot(localHealth);
        modPresentation = localHealth.ModManagement;
        RefreshLaunchPresentations();
        OnPropertyChanged(nameof(LaunchProfileStatus));
        NotifyConfigurationTargetChanged();
        OnPropertyChanged(nameof(SelectedLaunchTarget));
        OnPropertyChanged(nameof(IsPrimeExecutableSelected));
        OnPropertyChanged(nameof(IsScopelyLauncherSelected));
        OnPropertyChanged(nameof(CanOpenLaunchTargetMenu));
        OnPropertyChanged(nameof(GameFolderStatus));
        OnPropertyChanged(nameof(GameSectionStatus));
        OnPropertyChanged(nameof(GameFolderIcon));
        OnPropertyChanged(nameof(GameFolderTone));
        OnPropertyChanged(nameof(GameFolderStatusAutomationName));
        OnPropertyChanged(nameof(GameFolderActionLabel));
        OnPropertyChanged(nameof(GameFolderActionAutomationName));
        OnPropertyChanged(nameof(GameClientStatus));
        OnPropertyChanged(nameof(GameClientIcon));
        OnPropertyChanged(nameof(GameClientTone));
        OnPropertyChanged(nameof(GameClientStatusAutomationName));
        OnPropertyChanged(nameof(IsGameRunning));
        OnPropertyChanged(nameof(ModProviderCompatibility));
        OnPropertyChanged(nameof(HasUnsafeModDeploymentTransaction));
        OnPropertyChanged(nameof(ReviewedRuntimeActivation));
        OnPropertyChanged(nameof(ModProviderCompatibilityStatus));
        OnPropertyChanged(nameof(ModUpdateAvailability));
        OnPropertyChanged(nameof(ModUpdateAvailabilityStatus));
        OnPropertyChanged(nameof(ModGameCompatibility));
        OnPropertyChanged(nameof(ModGameCompatibilityStatus));
        OnPropertyChanged(nameof(ModRuntimeActivation));
        OnPropertyChanged(nameof(ModRuntimeActivationStatus));
        OnPropertyChanged(nameof(ModNativeSupport));
        OnPropertyChanged(nameof(ModNativeSupportStatus));
        OnPropertyChanged(nameof(ModHealthDimensions));
        OnPropertyChanged(nameof(ModSourceMetadata));
        NotifyModPresentationChanged();
        NotifyLaunchPresentationChanged();
        OnPropertyChanged(nameof(InitialBrowseDirectory));
        OnPropertyChanged(nameof(ConfigurationFilePath));
        OnPropertyChanged(nameof(SelectedGameDirectory));
        UpdateModActionAvailability();
        UpdateLaunchActionAvailability();
    }

    public async Task<ModOperationPreparation?> PrepareModOperationAsync(
        CancellationToken cancellationToken = default)
    {
        var admittedDirectory = SelectedGameDirectory;
        var admittedInstallationId = ActiveLaunchProfile?.PreferredInstallationId;
        if (!CanManageMod || admittedDirectory is null)
        {
            return null;
        }

        if (!actionFeedback.Mod.TryBegin("Checking the selected source for the latest community mod…"))
        {
            return null;
        }
        try
        {
            var target = await profilesStore.ResolveOperationTargetAsync(admittedDirectory, admittedInstallationId, cancellationToken);
            admittedDirectory = target.GameDirectory;
            admittedInstallationId = target.Id;
            var preparation = await modManagementCoordinator.PrepareLatestAsync(
                admittedDirectory,
                IsGameRunning,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(admittedInstallationId))
                preparation = preparation with { InstallationId = admittedInstallationId };
            if (preparation.State is ModOperationPreparationState.UpToDate
                or ModOperationPreparationState.MutationBlocked)
            {
                actionFeedback.Mod.Complete(false, preparation.Message);
            }
            else
            {
                var action = preparation.IsAdoptionOnly
                    ? "ready for Mod Bridge management"
                    : preparation.ActionKind switch
                    {
                        ModManagementActionKind.Install => "ready to install",
                        ModManagementActionKind.Repair => "ready to repair",
                        _ => "ready to update",
                    };
                actionFeedback.Mod.Complete(
                    true,
                    $"Community mod {preparation.ReleaseVersion} is {action}.");
            }
            return preparation;
        }
        catch (OperationCanceledException)
        {
            actionFeedback.Mod.Cancel("The release check was canceled or timed out.");
            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException
                or System.Runtime.InteropServices.ExternalException
                or TypeLoadException
                or BadImageFormatException
                or JsonException)
        {
            actionFeedback.Mod.Fail($"Could not prepare the mod operation: {exception.Message}");
            return null;
        }
    }

    public async Task<ModDeploymentResult?> ExecuteModOperationAsync(
        ModOperationPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        IDisposable? admittedLease = null;
        try
        {
            admittedLease = AcquireRuntimeInstallationLease(preparation.GameDirectory, preparation.InstallationId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or ArgumentException
            or System.Runtime.InteropServices.ExternalException or TypeLoadException or BadImageFormatException
            or JsonException)
        {
            admittedLease?.Dispose();
            actionFeedback.Mod.Fail("The prepared installation could not be revalidated. Select it again before continuing.");
            return null;
        }
        using var installationLease = admittedLease;
        if (!actionFeedback.Mod.TryBegin(ModOperationAcceptedMessage(preparation)))
        {
            return null;
        }

        BeginModMutation(preparation.GameDirectory);
        try
        {
            var result = await modManagementCoordinator.ExecuteAsync(preparation, cancellationToken);
            if (preparation.IsAdoptionOnly && result.IsSuccess)
            {
                actionFeedback.Mod.CompleteTransient(
                    result.Changed,
                    ModOperationSucceededMessage(preparation));
            }
            else
            {
                actionFeedback.CompleteModDeployment(result);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            actionFeedback.Mod.Cancel("The mod operation was canceled.");
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or HttpRequestException)
        {
            actionFeedback.Mod.Fail($"The mod operation failed: {exception.Message}");
            return null;
        }
        finally
        {
            EndModMutation();
            Refresh();
        }
    }

    internal IDisposable AcquireRuntimeInstallationLease(string gameDirectory, string? installationId) =>
        string.IsNullOrWhiteSpace(installationId)
            ? profilesStore.AcquireInstallationLease(gameDirectory)
            : profilesStore.AcquireInstallationLease(gameDirectory, installationId);

    internal Task<RegisteredGameInstallation> ResolveRuntimeInstallationAsync(CancellationToken cancellationToken) =>
        profilesStore.ResolveOperationTargetAsync(SelectedGameDirectory
            ?? throw new InvalidOperationException("Select an installation before continuing."),
            SelectedProfile?.PreferredInstallationId, cancellationToken);

    internal static string ModOperationAcceptedMessage(ModOperationPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (preparation.IsAdoptionOnly)
        {
            return "Management accepted. Recording the current community mod with Mod Bridge…";
        }
        return preparation.ActionKind switch
        {
            ModManagementActionKind.Install => "Installation accepted. Installing the verified community mod…",
            ModManagementActionKind.Repair => "Repair accepted. Restoring the verified community mod…",
            _ => "Update accepted. Installing the verified community mod update…",
        };
    }

    internal static string ModOperationSucceededMessage(ModOperationPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return preparation.IsAdoptionOnly
            ? $"Mod Bridge now manages community mod {preparation.ReleaseVersion}. "
                + "The previously installed file was preserved for removal or recovery."
            : $"Community mod {preparation.ReleaseVersion} completed successfully.";
    }

    internal void ReportRecoveryCompletion(bool changed, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        actionFeedback.Mod.Complete(changed, $"Recovery completed. {message}");
    }

    internal void BeginRecoveryWorkspaceTransition()
    {
        if (isRecoveryWorkspaceTransitionPending)
        {
            return;
        }
        isRecoveryWorkspaceTransitionPending = true;
        NotifyModContextChangeAvailability();
    }

    internal void EndRecoveryWorkspaceTransition()
    {
        if (!isRecoveryWorkspaceTransitionPending)
        {
            return;
        }
        isRecoveryWorkspaceTransitionPending = false;
        NotifyModContextChangeAvailability();
    }

    private void NotifyModContextChangeAvailability()
    {
        OnPropertyChanged(nameof(CanChangeGameFolder));
        OnPropertyChanged(nameof(CanChangeReleaseSource));
        OnPropertyChanged(nameof(CanOpenSettingsWorkspace));
        UpdateLaunchActionAvailability();
        NotifyLaunchPresentationChanged();
        RefreshProfileCards();
    }

    private async Task<ObservableActionResult> LaunchSelectedTargetAsync()
    {
        var displayedProfile = ActiveLaunchProfile;
        var displayedDefaultDirectory = SelectedGameDirectory;
        var displayedTarget = EffectiveLaunchTarget;
        ReloadLaunchProfile();
        if (!launchPresentation.CanExecute || !SameDisplayedLaunch())
        {
            return ObservableActionResult.Failed("The launch selection changed. Review the button and try again.");
        }
        var allowUnverifiedProxy = false;
        if (launchPresentation.RequiresUserOverride)
        {
            if (ConfirmLaunchOverrideAsync is null
                || !await ConfirmLaunchOverrideAsync(launchPresentation))
            {
                return ObservableActionResult.Unchanged(
                    "Launch canceled. The unverified version.dll remains unchanged.");
            }
            allowUnverifiedProxy = true;
        }

        ReloadLaunchProfile();
        if (!launchPresentation.CanExecute || !SameDisplayedLaunch())
        {
            return ObservableActionResult.Failed("The launch profile changed or became unavailable. Review the selection and try again.");
        }
        var profile = ActiveLaunchProfile;
        if (profile is null)
            return ObservableActionResult.Failed("The selected profile is unavailable.");
        await RefreshProfileSessionsAsync();
        if (!SameDisplayedLaunch())
            return ObservableActionResult.Failed("The selected profile changed while its sessions were checked.");
        var sessions = gameLaunchCoordinator.CaptureSessions(profile, profileSessions);
        if (sessions.Count > 0)
        {
            var selectedSession = sessions.Count == 1 ? sessions[0]
                : SelectProfileSessionAsync is null ? null : await SelectProfileSessionAsync(profile, sessions);
            if (selectedSession is null)
                return ObservableActionResult.Unchanged("Focus canceled. No game was launched.");
            ReloadLaunchProfile();
            if (!SameDisplayedLaunch() || !sessions.Contains(selectedSession))
                return ObservableActionResult.Failed("The selected session or profile changed. Refresh and try again.");
            var focused = gameLaunchCoordinator.FocusSession(profile, selectedSession);
            RefreshCore();
            return ProjectLaunchResult(focused);
        }
        if (profile is { IsDefault: false })
        {
            var contract = LauncherProfileLaunchContract.Inspect(profile.GameDirectory, profile.Id);
            if (!contract.IsValid)
            {
                return ObservableActionResult.Failed(contract.Message);
            }
        }

        var result = await gameLaunchCoordinator.LaunchProfileAsync(profile, allowUnverifiedProxy,
            profile.IsDefault ? displayedDefaultDirectory : snapshot.SelectedGameDirectory);
        RefreshCore();
        return ProjectLaunchResult(result);

        bool SameDisplayedLaunch() =>
            ActiveLaunchProfile?.Id == displayedProfile?.Id
            && string.Equals(ActiveLaunchProfile?.GameDirectory, displayedProfile?.GameDirectory,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(SelectedGameDirectory, displayedDefaultDirectory,
                StringComparison.OrdinalIgnoreCase)
            && EffectiveLaunchTarget == displayedTarget;
    }

    internal static ObservableActionResult ProjectLaunchResult(GameLaunchHandoffResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.State switch
        {
            GameLaunchHandoffState.Completed when result.Changed => ObservableActionResult.Changed(result.Message),
            GameLaunchHandoffState.Completed => ObservableActionResult.Unchanged(result.Message),
            GameLaunchHandoffState.Failed => ObservableActionResult.Failed(result.Message),
            _ => ObservableActionResult.Unchanged(result.Message),
        };
    }

    public LauncherDiagnosticPreview BuildDiagnosticPreview()
    {
        var battleFeatures = currentBattleFeatures?.Invoke();
        diagnosticPreview = diagnosticService.BuildPreview(
            SelectedGameDirectory,
            localHealth,
            battleFeatures);
        OnPropertyChanged(nameof(DiagnosticChecks));
        OnPropertyChanged(nameof(DiagnosticTechnicalReport));
        OnPropertyChanged(nameof(DiagnosticSummary));
        OnPropertyChanged(nameof(CanOpenGameFolder));
        OnPropertyChanged(nameof(CanOpenLogsFolder));
        return diagnosticPreview;
    }

    public void OpenGameFolder() =>
        SetDiagnosticActionStatus(diagnosticFolderService.TryOpen(
            SelectedGameDirectory,
            out var message), message);

    public void OpenLogsFolder()
    {
        var directory = CanOpenLogsFolder ? SelectedGameDirectory : null;
        SetDiagnosticActionStatus(diagnosticFolderService.TryOpen(directory, out var message), message);
    }

    public void ReportDiagnosticAction(bool succeeded, string message) =>
        SetDiagnosticActionStatus(succeeded, message);

    private void SetDiagnosticActionStatus(bool succeeded, string message)
    {
        diagnosticActionStatus = succeeded ? message : $"Action unavailable. {message}";
        OnPropertyChanged(nameof(DiagnosticActionStatus));
        OnPropertyChanged(nameof(HasDiagnosticActionStatus));
    }

    public static Task ExportDiagnosticsAsync(
        LauncherDiagnosticPreview preview,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        LauncherDiagnosticService.ExportAsync(preview, outputPath, cancellationToken);

    public async Task<LauncherUpdatePreparation?> PrepareLauncherUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!actionFeedback.LauncherUpdate.TryBegin("Mod Bridge update check accepted. Checking for an update…"))
        {
            return null;
        }
        if (WindowsPackageIdentity.IsCurrentProcessPackaged)
        {
            actionFeedback.LauncherUpdate.Fail(
                "Packaged Mod Bridge updates must use the Windows App Installer availability check.");
            throw new InvalidOperationException(
                "Standalone update preparation is unavailable for an installed MSIX application.");
        }
        try
        {
            var discovery = await releaseDiscoveryClient.DiscoverLatestAsync(
                "stable",
                CurrentLauncherVersion(),
                cancellationToken);
            var preparation = await launcherSelfUpdateService.PrepareAsync(
                discovery,
                CurrentSourceCommit(),
                Environment.ProcessId,
                cancellationToken);
            actionFeedback.LauncherUpdate.Complete(
                preparation.State == LauncherUpdatePreparationState.Ready,
                preparation.Message);
            return preparation;
        }
        catch (OperationCanceledException)
        {
            actionFeedback.LauncherUpdate.Cancel("The Mod Bridge update check was canceled or timed out.");
            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            actionFeedback.LauncherUpdate.Fail($"The Mod Bridge update could not be prepared: {exception.Message}");
            return null;
        }
    }

    public static void StartLauncherUpdate(LauncherUpdatePreparation preparation) =>
        LauncherSelfUpdateService.StartUpdater(preparation);

    public async Task<PackagedLauncherUpdateCheck?> CheckPackagedLauncherUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsPackagedInstallation)
        {
            throw new InvalidOperationException("A packaged update check requires an installed MSIX application.");
        }
        if (!actionFeedback.LauncherUpdate.TryBegin(
                "Mod Bridge update check accepted. Asking Windows App Installer for current availability…"))
        {
            return null;
        }

        try
        {
            var result = await packagedLauncherUpdateService.CheckAsync(cancellationToken);
            actionFeedback.LauncherUpdate.Complete(result.CanOpenUpdateSource, result.Message);
            return result;
        }
        catch (OperationCanceledException)
        {
            actionFeedback.LauncherUpdate.Cancel("The Mod Bridge update check was canceled or timed out.");
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or UnauthorizedAccessException
                or System.Runtime.InteropServices.COMException)
        {
            actionFeedback.LauncherUpdate.Fail(
                $"Windows App Installer could not check for a Mod Bridge update: {exception.Message}");
            return null;
        }
    }

    public bool TryOpenPackagedLauncherUpdateSource(PackagedLauncherUpdateCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!check.CanOpenUpdateSource || check.AppInstallerUri is null)
        {
            return false;
        }

        try
        {
            packagedLauncherUpdateService.OpenUpdateSource(check.AppInstallerUri);
            actionFeedback.LauncherUpdate.Complete(
                true,
                "Windows was asked to open the official App Installer file in your default browser. "
                    + "Open the downloaded STFCModBridge.appinstaller file to continue; "
                    + "if no browser window appeared, choose Check Mod Bridge update again.");
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or ArgumentException
                or System.ComponentModel.Win32Exception)
        {
            actionFeedback.LauncherUpdate.Fail(
                $"The official Mod Bridge update download could not be opened in your default browser: "
                    + exception.Message);
            return false;
        }
    }

    private static string CurrentSourceCommit()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return LauncherReleaseIdentityParser.Parse(informational).SourceCommit ?? string.Empty;
    }

    private static Version CurrentLauncherVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version
        ?? throw new InvalidOperationException("The Mod Bridge assembly version is unavailable.");

    public async Task<ModDeploymentResult?> RecoverModAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRecoverMod)
        {
            return null;
        }
        return await ExecuteMaintenanceAsync(
            "Recovering the incomplete mod transaction…",
            async (_, token) =>
            {
                if (HasIncompleteProviderSwitch && ProviderSwitchCoordinator is not null)
                {
                    var recovery = await ProviderSwitchCoordinator.RecoverAsync(token).ConfigureAwait(false);
                    return new(
                        recovery.IsSuccess
                            ? ModDeploymentResultState.Succeeded
                            : ModDeploymentResultState.RecoveryRequired,
                        recovery.Message,
                        Changed: recovery.Changed);
                }
                return await modManagementCoordinator.RecoverAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<ReviewedCandidateRecoveryResult?> RetryCandidateRecoveryAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanRetryCandidateRecovery || featureRemediationCandidates is null)
        {
            return null;
        }
        if (!actionFeedback.Mod.TryBegin(
                "Candidate recovery accepted. Checking exact launcher-owned residue…"))
        {
            return null;
        }
        try
        {
            var result = await featureRemediationCandidates.RecoverAsync(cancellationToken);
            actionFeedback.Mod.Complete(result.CanAcquire, result.Message);
            SetDiagnosticActionStatus(result.CanAcquire, result.Message);
            return result;
        }
        catch (OperationCanceledException)
        {
            actionFeedback.Mod.Cancel("Candidate recovery was canceled; no game or provider state was changed.");
            SetDiagnosticActionStatus(
                false,
                "Candidate recovery was canceled; no game or provider state was changed.");
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            actionFeedback.Mod.Fail($"Candidate recovery could not finish: {exception.Message}");
            SetDiagnosticActionStatus(
                false,
                $"Candidate recovery could not finish: {exception.Message}");
            return null;
        }
    }

    private bool HasIncompleteProviderSwitch
    {
        get
        {
            try
            {
                var journal = ProviderSwitchCoordinator?.ReadJournal();
                return journal is not null
                    && journal.Phase is not (LauncherProviderAtomicSwitchPhase.Completed
                        or LauncherProviderAtomicSwitchPhase.RolledBack)
                    && IsRecoveryForInstallation(journal.Preview.ConfigurationPath, SelectedGameDirectory);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or JsonException)
            {
                return true;
            }
        }
    }

    public async Task<ModDeploymentResult?> UninstallModAsync(CancellationToken cancellationToken = default)
    {
        var admittedDirectory = SelectedGameDirectory;
        if (!CanUninstallMod || admittedDirectory is null)
        {
            return null;
        }
        return await ExecuteMaintenanceAsync(
            "Removing the Mod Bridge-managed community mod…",
            (directory, token) => modManagementCoordinator.UninstallAsync(directory!, token),
            cancellationToken, admittedDirectory, ActiveLaunchProfile?.PreferredInstallationId);
    }

    public async Task<ModDeploymentResult?> StopManagingModAsync(
        CancellationToken cancellationToken = default)
    {
        var admittedDirectory = SelectedGameDirectory;
        if (!CanStopManagingMod || admittedDirectory is null)
        {
            return null;
        }
        return await ExecuteMaintenanceAsync(
            "Detaching Mod Bridge ownership from the selected installation…",
            (_, token) => modManagementCoordinator.StopManagingAsync(admittedDirectory, token),
            cancellationToken);
    }

    private async Task<ModDeploymentResult?> ExecuteMaintenanceAsync(
        string progress,
        Func<string?, CancellationToken, Task<ModDeploymentResult>> operation,
        CancellationToken cancellationToken,
        string? operationDirectory = null,
        string? installationId = null)
    {
        if (!actionFeedback.Mod.TryBegin(progress))
        {
            return null;
        }
        BeginModMutation(operationDirectory);
        try
        {
            var resolved = operationDirectory is null ? null
                : await profilesStore.ResolveOperationTargetAsync(operationDirectory, installationId, cancellationToken);
            using var installationLease = resolved is null ? null
                : AcquireRuntimeInstallationLease(resolved.GameDirectory, resolved.Id);
            var result = await operation(resolved?.GameDirectory, cancellationToken);
            actionFeedback.CompleteModDeployment(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            actionFeedback.Mod.Cancel("The maintenance operation was canceled.");
            return null;
        }
        catch (LauncherProviderSwitchJournalException)
        {
            actionFeedback.Mod.Fail(
                "The saved recovery details are damaged, so Mod Bridge did not change any files. "
                    + "Do not retry recovery until those details are repaired. Open Verification & recovery guidance, "
                    + "export a diagnostic report, and share it when asking for help.");
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException or ArgumentException or TypeLoadException or BadImageFormatException
                or System.Runtime.InteropServices.ExternalException or JsonException)
        {
            actionFeedback.Mod.Fail($"The maintenance operation failed: {exception.Message}");
            return null;
        }
        finally
        {
            EndModMutation();
            Refresh();
        }
    }

    private void BeginModMutation(string? directory)
    {
        isModMutationInProgress = true;
        modOperationDirectory = directory;
        UpdateLaunchActionAvailability();
        NotifyLaunchPresentationChanged();
        RefreshProfileCards();
    }

    private void EndModMutation()
    {
        isModMutationInProgress = false;
        modOperationDirectory = null;
        UpdateLaunchActionAvailability();
        NotifyLaunchPresentationChanged();
        RefreshProfileCards();
    }

    private void NotifyModPresentationChanged()
    {
        OnPropertyChanged(nameof(ModStatus));
        OnPropertyChanged(nameof(ModTone));
        OnPropertyChanged(nameof(ModActionLabel));
        OnPropertyChanged(nameof(ModActionAutomationName));
        OnPropertyChanged(nameof(ModActionHelpText));
        OnPropertyChanged(nameof(CanManageMod));
        OnPropertyChanged(nameof(CanChangeGameFolder));
        OnPropertyChanged(nameof(CanChangeReleaseSource));
        OnPropertyChanged(nameof(CanOpenSettingsWorkspace));
        OnPropertyChanged(nameof(ModActionKind));
        OnPropertyChanged(nameof(CanRecoverMod));
        OnPropertyChanged(nameof(CanUninstallMod));
        OnPropertyChanged(nameof(CanStopManagingMod));
        OnPropertyChanged(nameof(DiagnosticRecoveryAvailability));
        OnPropertyChanged(nameof(DiagnosticRemovalAvailability));
        OnPropertyChanged(nameof(DiagnosticStopManagingAvailability));
        OnPropertyChanged(nameof(CanRetryCandidateRecovery));
        OnPropertyChanged(nameof(DiagnosticCandidateRecoveryAvailability));
        UpdateModActionAvailability();
    }

    internal static bool ResolveModActionAvailability(
        bool hasIncompleteProviderSwitch,
        bool isGameRunning,
        bool ordinaryActionCanExecute) =>
        hasIncompleteProviderSwitch
            ? !isGameRunning
            : ordinaryActionCanExecute;

    internal static bool ResolveModContextChangeAvailability(
        bool recoveryRequired,
        bool isModOperationInProgress,
        bool isLaunchInProgress) =>
        !isModOperationInProgress && !isLaunchInProgress;

    internal static string DescribeProviderSwitchRecoveryAvailability(
        bool isGameRunning,
        bool? includesArtifact)
    {
        if (isGameRunning)
        {
            return "Close Star Trek Fleet Command before recovering the incomplete provider switch.";
        }
        return includesArtifact switch
        {
            true => "Recovery is available for the incomplete provider switch. "
                + "version.dll, provider selection, and exact TOML bytes will be restored together.",
            false => "Recovery is available for the incomplete provider switch. "
                + "Provider selection and exact TOML bytes will be restored; no DLL change was part of this switch.",
            null => "Recovery is required for the incomplete provider switch. "
                + "Review its persisted transaction details before continuing.",
        };
    }

    internal static string DescribeLauncherUpdateActionAutomationName(
        bool isWorking,
        string automationAnnouncement) =>
        isWorking
            ? $"Checking for Mod Bridge update… {automationAnnouncement}"
            : "Check Mod Bridge update. Check for a Mod Bridge self-update.";

    internal bool? IncompleteProviderSwitchIncludesArtifact
    {
        get
        {
            try
            {
                var journal = ProviderSwitchCoordinator?.ReadJournal();
                return journal is not null
                    && journal.Phase is not (LauncherProviderAtomicSwitchPhase.Completed
                        or LauncherProviderAtomicSwitchPhase.RolledBack)
                    ? journal.TargetArtifact is not null
                    : null;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or JsonException)
            {
                return null;
            }
        }
    }

    internal LauncherProviderSelection? IncompleteProviderSwitchSourceSelection
    {
        get
        {
            try
            {
                var journal = ProviderSwitchCoordinator?.ReadJournal();
                return journal is not null
                    && journal.Phase is not (LauncherProviderAtomicSwitchPhase.Completed
                        or LauncherProviderAtomicSwitchPhase.RolledBack)
                    ? journal.Preview.Source
                    : null;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or JsonException)
            {
                return null;
            }
        }
    }

    internal string? IncompleteProviderSwitchGameDirectory
    {
        get
        {
            try
            {
                var journal = ProviderSwitchCoordinator?.ReadJournal();
                if (journal is null
                    || journal.Phase is LauncherProviderAtomicSwitchPhase.Completed
                        or LauncherProviderAtomicSwitchPhase.RolledBack
                    || string.IsNullOrWhiteSpace(journal.Preview.ConfigurationPath))
                {
                    return null;
                }
                return Path.GetDirectoryName(Path.GetFullPath(journal.Preview.ConfigurationPath));
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or JsonException
                    or ArgumentException
                    or NotSupportedException)
            {
                return null;
            }
        }
    }

    private void UpdateModActionAvailability()
    {
        var hasIncompleteProviderSwitch = HasIncompleteProviderSwitch;
        actionFeedback.Mod.SetAvailability(
            ResolveModActionAvailability(
                hasIncompleteProviderSwitch,
                IsGameRunning,
                modPresentation.CanExecute),
            hasIncompleteProviderSwitch
                ? "Close Star Trek Fleet Command before recovering the incomplete provider switch."
                : modPresentation.AutomationName);
    }

    private void UpdateLaunchActionAvailability() =>
        actionFeedback.Launch.SetAvailability(
            launchPresentation.CanExecute && !HasConflictingInstallationMutation,
            launchPresentation.AutomationName);

    private void SelectLaunchTarget(LauncherLaunchTarget target)
    {
        if (ActiveLaunchProfile is not null || profilesLoad.Snapshot?.SelectedProfileId is not null
            || profilesLoad.Snapshot is null)
        {
            return;
        }
        if (selectedLaunchTarget == target)
        {
            return;
        }

        selectedLaunchTarget = target;
        try
        {
            uiPreferencesStore.Save(uiPreferencesStore.Load() with { LaunchTarget = target });
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            // Launcher UI preferences are best-effort; selection remains valid for this session.
        }
        launchPresentation = GetLaunchChoice(selectedLaunchTarget);
        OnPropertyChanged(nameof(SelectedLaunchTarget));
        OnPropertyChanged(nameof(IsPrimeExecutableSelected));
        OnPropertyChanged(nameof(IsScopelyLauncherSelected));
        OnPropertyChanged(nameof(PrimeExecutableChoiceAutomationName));
        OnPropertyChanged(nameof(ScopelyLauncherChoiceAutomationName));
        OnPropertyChanged(nameof(PrimeExecutableChoiceStatus));
        OnPropertyChanged(nameof(ScopelyLauncherChoiceStatus));
        NotifyLaunchPresentationChanged();
        UpdateLaunchActionAvailability();
    }

    private string BuildChoiceAutomationName(LauncherLaunchTarget target, string label)
    {
        var choice = GetLaunchChoice(target);
        var selected = EffectiveLaunchTarget == target ? ", selected" : string.Empty;
        var availability = choice.RequiresUserOverride
            ? $", available after confirmation, {choice.Reason}"
            : choice.CanExecute
            ? $", available, {choice.Reason}"
            : $", unavailable, {choice.Reason}, {choice.NextActionLabel}";
        return $"{label}{selected}{availability}";
    }

    private string BuildChoiceStatus(LauncherLaunchTarget target)
    {
        var choice = GetLaunchChoice(target);
        return choice.RequiresUserOverride
            ? $"Warning · {choice.Reason} · Launch anyway requires confirmation"
            : choice.CanExecute
            ? choice.Reason
            : $"Unavailable · {choice.Reason} · {choice.NextActionLabel}";
    }

    private void RefreshLaunchPresentations()
    {
        var profile = ActiveLaunchProfile;
        primeLaunchChoice = gameLaunchCoordinator.CapturePresentation(
            SelectedGameDirectory, LauncherLaunchTarget.PrimeExecutable,
            localHealth.Installation, requiredProfile: profile);
        scopelyLaunchChoice = gameLaunchCoordinator.CapturePresentation(
            SelectedGameDirectory, LauncherLaunchTarget.ScopelyLauncher,
            localHealth.Installation);
        if (profile is null)
        {
            var reason = profilesLoad.Error ?? "The shared profile catalog could not be read.";
            primeLaunchChoice = BlockProfileLaunch(primeLaunchChoice, reason);
            scopelyLaunchChoice = BlockProfileLaunch(scopelyLaunchChoice, reason);
        }
        else
        {
            var sessions = gameLaunchCoordinator.CaptureSessions(profile, profileSessions);
            if (sessions.Count > 0)
                primeLaunchChoice = primeLaunchChoice with
                {
                    Status = "Running", Tone = LauncherHomeTone.Success,
                    ActionLabel = NamedProfileActionLabel(profile, sessions), CanExecute = true,
                    AutomationName = NamedProfileActionLabel(profile, sessions),
                    Reason = "Focus an exact running session; its launch installation remains unchanged.",
                    NextAction = LauncherLaunchRecoveryAction.None, RequiresUserOverride = false,
                };
        }
        launchPresentation = primeLaunchChoice;
        RefreshProfileCards();
    }

    internal void ReloadLaunchProfile()
    {
        RefreshCore();
        OnPropertyChanged(nameof(LaunchProfileStatus));
        NotifyConfigurationTargetChanged();
        OnPropertyChanged(nameof(SelectedLaunchTarget));
        OnPropertyChanged(nameof(IsPrimeExecutableSelected));
        OnPropertyChanged(nameof(IsScopelyLauncherSelected));
        OnPropertyChanged(nameof(CanOpenLaunchTargetMenu));
        NotifyLaunchPresentationChanged();
        UpdateLaunchActionAvailability();
    }

    private static GameLaunchPresentation BlockProfileLaunch(GameLaunchPresentation choice, string reason) =>
        choice with
        {
            Status = "Profile needs attention",
            Tone = LauncherHomeTone.Warning,
            CanExecute = false,
            AutomationName = $"Launch unavailable: {reason}",
            Reason = reason,
            NextAction = LauncherLaunchRecoveryAction.OpenDiagnostics,
            RequiresUserOverride = false,
        };

    private void NotifyConfigurationTargetChanged()
    {
        OnPropertyChanged(nameof(ConfigurationFilePath));
        OnPropertyChanged(nameof(ConfigurationGameDirectory));
        OnPropertyChanged(nameof(ConfigurationTargetLabel));
        OnPropertyChanged(nameof(ConfigurationRuntimeSelection));
        OnPropertyChanged(nameof(HasCommunityFeatures));
        OnPropertyChanged(nameof(SelectedConfigurationProfile));
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(SelectedProfileName));
        OnPropertyChanged(nameof(IsDefaultProfileSelected));
        OnPropertyChanged(nameof(NextLaunchInstallationLabel));
    }

    private GameLaunchPresentation GetLaunchChoice(LauncherLaunchTarget target) =>
        target == LauncherLaunchTarget.PrimeExecutable
            ? primeLaunchChoice
            : scopelyLaunchChoice;

    private HomeState CaptureHomeState() => new(
        snapshot.HealthCode,
        snapshot.IsGameRunning,
        SelectedGameDirectory,
        presentation.GameFolderStatus,
        presentation.GameClientStatus,
        modPresentation.Status,
        modPresentation.ActionKind,
        modPresentation.CanExecute,
        launchPresentation.ActionLabel,
        launchPresentation.CanExecute);

    private void RefreshActionState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        switch (e.PropertyName)
        {
            case nameof(ObservableActionState.IsWorking):
                OnPropertyChanged(nameof(RefreshActionLabel));
                UpdateRefreshActionStatusLifetime();
                break;
            case nameof(ObservableActionState.AutomationAnnouncement):
                OnPropertyChanged(nameof(RefreshActionAutomationName));
                break;
            case nameof(ObservableActionState.StatusText):
                OnPropertyChanged(nameof(RefreshActionStatus));
                UpdateRefreshActionStatusLifetime();
                break;
            case nameof(ObservableActionState.HasStatus):
                OnPropertyChanged(nameof(HasRefreshActionStatus));
                break;
            case nameof(ObservableActionState.IsCommandAvailable):
                OnPropertyChanged(nameof(CanRefresh));
                break;
        }
    }

    private void UpdateRefreshActionStatusLifetime()
    {
        refreshActionStatusTimer.Stop();
        if (actionFeedback.Refresh.HasStatus && !actionFeedback.Refresh.IsWorking)
        {
            refreshActionStatusTimer.Start();
        }
    }

    private void RefreshActionStatusTimer_Tick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        refreshActionStatusTimer.Stop();
        actionFeedback.Refresh.ClearStatus();
    }

    private void ModActionState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName == nameof(ObservableActionState.IsWorking)) GameClient.NotifyAvailability();
        switch (e.PropertyName)
        {
            case nameof(ObservableActionState.Status):
            case nameof(ObservableActionState.IsTransientFeedback):
                UpdateModActionStatusLifetime();
                break;
            case nameof(ObservableActionState.IsWorking):
                OnPropertyChanged(nameof(IsModOperationInProgress));
                OnPropertyChanged(nameof(ModActionLabel));
                OnPropertyChanged(nameof(ModActionAutomationName));
                OnPropertyChanged(nameof(ModActionHelpText));
                OnPropertyChanged(nameof(CanRecoverMod));
                OnPropertyChanged(nameof(CanUninstallMod));
                OnPropertyChanged(nameof(CanStopManagingMod));
                OnPropertyChanged(nameof(CanLaunchGame));
                OnPropertyChanged(nameof(CanRetryCandidateRecovery));
                OnPropertyChanged(nameof(CanChangeGameFolder));
                OnPropertyChanged(nameof(CanChangeReleaseSource));
                OnPropertyChanged(nameof(CanOpenSettingsWorkspace));
                break;
            case nameof(ObservableActionState.AutomationAnnouncement):
                OnPropertyChanged(nameof(ModActionAutomationName));
                OnPropertyChanged(nameof(ModActionHelpText));
                break;
            case nameof(ObservableActionState.StatusText):
                OnPropertyChanged(nameof(ModOperationFeedback));
                break;
            case nameof(ObservableActionState.HasStatus):
                OnPropertyChanged(nameof(HasModOperationFeedback));
                break;
            case nameof(ObservableActionState.IsCommandAvailable):
                OnPropertyChanged(nameof(CanManageMod));
                OnPropertyChanged(nameof(CanRecoverMod));
                OnPropertyChanged(nameof(CanUninstallMod));
                OnPropertyChanged(nameof(CanStopManagingMod));
                OnPropertyChanged(nameof(CanRetryCandidateRecovery));
                OnPropertyChanged(nameof(CanChangeGameFolder));
                OnPropertyChanged(nameof(CanChangeReleaseSource));
                OnPropertyChanged(nameof(CanOpenSettingsWorkspace));
                break;
        }
    }

    private void UpdateModActionStatusLifetime()
    {
        modActionStatusTimer.Stop();
        if (ShouldAutoClearModStatus(actionFeedback.Mod))
        {
            modActionStatusTimer.Start();
        }
    }

    private void ModActionStatusTimer_Tick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        modActionStatusTimer.Stop();
        actionFeedback.Mod.ClearStatus();
    }

    internal static bool ShouldAutoClearModStatus(ObservableActionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.IsTransientFeedback
            && state.Status is (
                ObservableActionStatus.CompletedChanged
                or ObservableActionStatus.CompletedUnchanged);
    }

    private void LauncherUpdateActionState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        switch (e.PropertyName)
        {
            case nameof(ObservableActionState.IsWorking):
                OnPropertyChanged(nameof(LauncherUpdateActionLabel));
                OnPropertyChanged(nameof(LauncherUpdateActionAutomationName));
                break;
            case nameof(ObservableActionState.AutomationAnnouncement):
                OnPropertyChanged(nameof(LauncherUpdateActionAutomationName));
                break;
            case nameof(ObservableActionState.StatusText):
                OnPropertyChanged(nameof(LauncherUpdateFeedback));
                break;
            case nameof(ObservableActionState.IsCommandAvailable):
                OnPropertyChanged(nameof(CanCheckLauncherUpdate));
                break;
        }
    }

    private void LaunchActionState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName == nameof(ObservableActionState.IsWorking)) GameClient.NotifyAvailability();
        switch (e.PropertyName)
        {
            case nameof(ObservableActionState.IsWorking):
                OnPropertyChanged(nameof(IsLaunchInProgress));
                OnPropertyChanged(nameof(LaunchActionLabel));
                OnPropertyChanged(nameof(CanManageMod));
                OnPropertyChanged(nameof(CanChangeGameFolder));
                OnPropertyChanged(nameof(CanChangeReleaseSource));
                OnPropertyChanged(nameof(CanRecoverMod));
                OnPropertyChanged(nameof(CanUninstallMod));
                OnPropertyChanged(nameof(CanStopManagingMod));
                OnPropertyChanged(nameof(CanRetryCandidateRecovery));
                break;
            case nameof(ObservableActionState.AutomationAnnouncement):
                OnPropertyChanged(nameof(LaunchActionAutomationName));
                break;
            case nameof(ObservableActionState.IsCommandAvailable):
                OnPropertyChanged(nameof(CanLaunchGame));
                break;
        }
    }

    private void GameClient_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(GameInstallationViewModel.IsWorking)
            or nameof(GameInstallationViewModel.IsMutationInProgress))) return;
        UpdateLaunchActionAvailability();
        RefreshProfileCards();
        NotifyModContextChangeAvailability();
        OnPropertyChanged(nameof(CanManageMod));
        OnPropertyChanged(nameof(CanRecoverMod));
        OnPropertyChanged(nameof(CanUninstallMod));
        OnPropertyChanged(nameof(CanStopManagingMod));
        OnPropertyChanged(nameof(CanLaunchGame));
    }

    private void HomeFeedback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName == nameof(HomeActionFeedbackArbiter.Text))
        {
            OnPropertyChanged(nameof(HomeOperationFeedback));
        }
        else if (e.PropertyName == nameof(HomeActionFeedbackArbiter.HasFeedback))
        {
            OnPropertyChanged(nameof(HasHomeOperationFeedback));
        }
        else if (e.PropertyName == nameof(HomeActionFeedbackArbiter.CanDismiss))
        {
            OnPropertyChanged(nameof(CanDismissHomeOperationFeedback));
        }
    }

    private static void ObserveDisposal(Task disposal)
    {
        _ = disposal.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void NotifyLaunchPresentationChanged()
    {
        OnPropertyChanged(nameof(LaunchActionLabel));
        OnPropertyChanged(nameof(LaunchActionAutomationName));
        OnPropertyChanged(nameof(CanLaunchGame));
        OnPropertyChanged(nameof(PrimeExecutableChoiceAutomationName));
        OnPropertyChanged(nameof(ScopelyLauncherChoiceAutomationName));
        OnPropertyChanged(nameof(PrimeExecutableChoiceStatus));
        OnPropertyChanged(nameof(ScopelyLauncherChoiceStatus));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed record HomeState(
        LauncherHealthCode HealthCode,
        bool IsGameRunning,
        string? SelectedGameDirectory,
        string GameFolderStatus,
        string GameClientStatus,
        string ModStatus,
        ModManagementActionKind ModActionKind,
        bool CanExecuteModAction,
        string LaunchActionLabel,
        bool CanExecuteLaunchAction);
}

internal sealed record LauncherProfileCard(string Id, string Name, string Status, string ActionLabel,
    bool CanLaunch, bool IsDefault, string? GameDirectory, IReadOnlyList<ProfileSession> RunningSessions,
    string Reason, LauncherLaunchRecoveryAction NextAction)
{
    public bool NeedsSetup => NextAction is (LauncherLaunchRecoveryAction.SetUpProfileSupport
        or LauncherLaunchRecoveryAction.SelectGameFolder) && RunningSessions.Count == 0;
    public bool CanAct => CanLaunch || NeedsSetup;
    public string SessionSummary => string.Join("; ", RunningSessions.Select(session =>
        $"Process {session.ProcessId} · {session.GameDirectory}"));
    public string ActionAutomationName => $"{ActionLabel}. {Status}. {Reason}";
}
