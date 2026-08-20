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
    private readonly LauncherDiagnosticService diagnosticService;
    private readonly LauncherSelfUpdateService launcherSelfUpdateService;
    private readonly ILauncherReleaseDiscoveryClient releaseDiscoveryClient;
    private readonly IPackagedLauncherUpdateService packagedLauncherUpdateService;
    private readonly ILauncherUiPreferencesStore uiPreferencesStore;
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

    private MainWindowViewModel(
        LauncherEnvironmentProbe environmentProbe,
        IModManagementCoordinator modManagementCoordinator,
        GameLaunchHandoffCoordinator gameLaunchCoordinator,
        LauncherDiagnosticService diagnosticService,
        LauncherSelfUpdateService launcherSelfUpdateService,
        ILauncherReleaseDiscoveryClient releaseDiscoveryClient,
        IPackagedLauncherUpdateService packagedLauncherUpdateService,
        ILauncherUiPreferencesStore uiPreferencesStore,
        LauncherDistributionProviderCatalog distributionProviderCatalog,
        LauncherFeatureRemediationCandidates? featureRemediationCandidates,
        string modSourceMetadata,
        IDiagnosticFolderService diagnosticFolderService)
    {
        this.environmentProbe = environmentProbe;
        this.modManagementCoordinator = modManagementCoordinator;
        this.gameLaunchCoordinator = gameLaunchCoordinator;
        this.diagnosticService = diagnosticService;
        this.launcherSelfUpdateService = launcherSelfUpdateService;
        this.releaseDiscoveryClient = releaseDiscoveryClient;
        this.packagedLauncherUpdateService = packagedLauncherUpdateService;
        this.uiPreferencesStore = uiPreferencesStore;
        this.distributionProviderCatalog = distributionProviderCatalog;
        this.featureRemediationCandidates = featureRemediationCandidates;
        selectedModSourceMetadata = modSourceMetadata;
        this.diagnosticFolderService = diagnosticFolderService;
        selectedLaunchTarget = uiPreferencesStore.Load().LaunchTarget;
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
        presentation = LauncherHomePresentation.FromSnapshot(snapshot);
        localHealth = modManagementCoordinator.CaptureHealth(
            snapshot.SelectedGameDirectory,
            snapshot.IsGameRunning);
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
        if (featureRemediationCandidates is not null)
        {
            ObserveDisposal(featureRemediationCandidates.DisposeAsync().AsTask());
        }
        GC.SuppressFinalize(this);
    }

    public string GameSectionStatus => presentation.GameSectionStatus;

    public string GameFolderStatus => presentation.GameFolderStatus;

    public string GameFolderIcon => presentation.GameFolderIcon;

    public LauncherHomeTone GameFolderTone => presentation.GameFolderTone;

    public string GameFolderStatusAutomationName => presentation.GameFolderStatusAutomationName;

    public string GameFolderActionLabel => presentation.GameFolderActionLabel;

    public string GameFolderActionAutomationName => presentation.GameFolderActionAutomationName;

    public bool CanChangeGameFolder => ResolveModContextChangeAvailability(
        ModActionKind == ModManagementActionKind.Recover,
        actionFeedback.Mod.IsWorking || isRecoveryWorkspaceTransitionPending,
        actionFeedback.Launch.IsWorking);

    public bool CanChangeReleaseSource => ResolveModContextChangeAvailability(
        ModActionKind == ModManagementActionKind.Recover,
        actionFeedback.Mod.IsWorking || isRecoveryWorkspaceTransitionPending,
        actionFeedback.Launch.IsWorking);

    public bool CanOpenSettingsWorkspace =>
        !actionFeedback.Mod.IsWorking
        && !isRecoveryWorkspaceTransitionPending;

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

    public bool CanManageMod => actionFeedback.Mod.IsCommandAvailable
        && !actionFeedback.Launch.IsWorking
        && (!HasIncompleteProviderSwitch || !IsGameRunning);

    public ModManagementActionKind ModActionKind => HasIncompleteProviderSwitch
        ? ModManagementActionKind.Recover
        : modPresentation.ActionKind;

    public bool CanRecoverMod =>
        ModActionKind == ModManagementActionKind.Recover
        && actionFeedback.CanStartModMaintenance(
            HasIncompleteProviderSwitch ? !IsGameRunning : modPresentation.CanExecute,
            actionFeedback.Launch.IsWorking);

    public bool CanUninstallMod =>
        !HasIncompleteProviderSwitch
        && !IsGameRunning
        && modPresentation.ActionKind == ModManagementActionKind.CheckForUpdate
        && actionFeedback.CanStartModMaintenance(modPresentation.CanExecute, actionFeedback.Launch.IsWorking);

    public bool CanStopManagingMod =>
        !HasIncompleteProviderSwitch
        && SelectedGameDirectory is not null
        && localHealth.Installation.State is (
            ModInstallationEvidenceState.ManagedVerified
            or ModInstallationEvidenceState.ManagedChanged
            or ModInstallationEvidenceState.ManagedMissing)
        && actionFeedback.CanStartModMaintenance(
            externallyAvailable: true,
            actionFeedback.Launch.IsWorking);

    public string DiagnosticRecoveryAvailability
    {
        get
        {
            if (HasIncompleteProviderSwitch)
            {
                return DescribeProviderSwitchRecoveryAvailability(
                    IsGameRunning,
                    IncompleteProviderSwitchIncludesArtifact);
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

    public string LaunchActionLabel => actionFeedback.Launch.IsWorking ? "Opening…" : launchPresentation.ActionLabel;

    public string LaunchActionAutomationName => actionFeedback.Launch.IsWorking
        ? actionFeedback.Launch.AutomationAnnouncement
        : launchPresentation.AutomationName;

    public bool CanLaunchGame => actionFeedback.Launch.IsCommandAvailable && !actionFeedback.Mod.IsWorking;

    public LauncherLaunchTarget SelectedLaunchTarget => selectedLaunchTarget;

    public bool IsPrimeExecutableSelected => selectedLaunchTarget == LauncherLaunchTarget.PrimeExecutable;

    public bool IsScopelyLauncherSelected => selectedLaunchTarget == LauncherLaunchTarget.ScopelyLauncher;

    public string PrimeExecutableChoiceAutomationName => BuildChoiceAutomationName(
        LauncherLaunchTarget.PrimeExecutable,
        "Launch prime.exe");

    public string ScopelyLauncherChoiceAutomationName => BuildChoiceAutomationName(
        LauncherLaunchTarget.ScopelyLauncher,
        "Open Scopely launcher");

    public string PrimeExecutableChoiceStatus => BuildChoiceStatus(LauncherLaunchTarget.PrimeExecutable);

    public string ScopelyLauncherChoiceStatus => BuildChoiceStatus(LauncherLaunchTarget.ScopelyLauncher);

    public bool CanOpenLaunchTargetMenu => Enum.IsDefined(selectedLaunchTarget);

    public ICommand LaunchPrimaryCommand { get; }

    public ICommand SelectPrimeExecutableCommand { get; }

    public ICommand SelectScopelyLauncherCommand { get; }

    public string ModOperationFeedback => actionFeedback.Mod.StatusText;

    public bool HasModOperationFeedback => actionFeedback.Mod.HasStatus;

    public bool IsModOperationInProgress => actionFeedback.Mod.IsWorking;

    public bool IsLaunchInProgress => actionFeedback.Launch.IsWorking;

    public string HomeOperationFeedback => homeFeedback.Text;

    public bool HasHomeOperationFeedback => homeFeedback.HasFeedback;

    public string? SelectedGameDirectory => snapshot.SelectedGameDirectory;

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

    public bool CanOpenGameFolder => snapshot.SelectedGameDirectory is not null;

    public bool CanOpenLogsFolder => snapshot.SelectedGameDirectory is not null;

    public string? InitialBrowseDirectory
    {
        get
        {
            var validCandidates = snapshot.Discovery.ValidCandidates;
            return snapshot.SelectedGameDirectory
                ?? (validCandidates.Count > 0 ? validCandidates[0].GameDirectory : null);
        }
    }

    public string? ConfigurationFilePath =>
        snapshot.SelectedGameDirectory is null
            ? null
            : Path.Combine(snapshot.SelectedGameDirectory, "community_patch_settings.toml");

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
            IModArtifactAuthenticityVerifier artifactVerifier = binding.IsAvailable
                ? binding.TrustKind switch
                {
                    LauncherProviderArtifactTrustKind.AuthenticodePublisher =>
                        new WindowsAuthenticodeVerifier(
                            binding.WindowsPublisher!,
                            binding.WindowsArtifactSigningIdentityEku!),
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
                        new ReviewedGitHubReleaseAssetClient(httpClient, binding.ReviewedCertification!),
                    _ => new UnavailableWindowsReleaseDiscoveryClient("Unsupported release discovery kind."),
                }
                : new UnavailableWindowsReleaseDiscoveryClient(binding.UnavailableReason);
            IModArtifactDownloader artifactDownloader = binding.IsAvailable
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
                reviewedCertifications: reviewedReleases.Certifications);
            var candidateAcquirer = binding.IsAvailable
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
        LauncherConfigurationDiagnosisEvidence configurationEvidence;
        try
        {
            var configurationCapabilityStatus = distributionProvider.GetCapabilityStatus(
                LauncherProviderCapabilityIds.ConfigurationCatalog);
            if (configurationCapabilityStatus != LauncherProviderCapabilityStatus.Supported)
            {
                configurationEvidence = LauncherConfigurationDiagnosisEvidence.Unavailable(
                    distributionProvider.Id,
                    releaseChannel.Id,
                    configurationCapabilityStatus);
            }
            else
            {
                var resolvedConfigurationCatalog = configurationCatalog
                    ?? BundledLauncherProviderCatalog.LoadConfigurationCatalog(distributionProvider);
                var catalogMatchesChannel = string.Equals(
                        resolvedConfigurationCatalog.Identity.TrackId,
                        releaseChannel.Id,
                        StringComparison.Ordinal)
                    || (string.Equals(
                            resolvedConfigurationCatalog.Identity.TrackId,
                            "unversioned",
                            StringComparison.Ordinal)
                        && string.Equals(
                            releaseChannel.Id,
                            distributionProvider.DefaultReleaseChannelId,
                            StringComparison.Ordinal));
                configurationEvidence = catalogMatchesChannel
                    ? LauncherConfigurationDiagnosisEvidence.Supported(
                        distributionProvider.Id,
                        releaseChannel.Id,
                        resolvedConfigurationCatalog)
                    : LauncherConfigurationDiagnosisEvidence.Unavailable(
                        distributionProvider.Id,
                        releaseChannel.Id,
                        LauncherProviderCapabilityStatus.Unknown);
            }
        }
        catch (LauncherConfigurationSchemaException)
        {
            configurationEvidence = LauncherConfigurationDiagnosisEvidence.Unavailable(
                distributionProvider.Id,
                releaseChannel.Id,
                LauncherProviderCapabilityStatus.Unknown);
        }

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
                configurationEvidence: configurationEvidence,
                runtimeDistributionId: distributionProvider.RuntimeDistributionId),
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
            distributionProviderCatalog,
            featureRemediationCandidates,
            string.IsNullOrWhiteSpace(providerResolutionFailure)
                ? $"{distributionProvider.DisplayName} · {releaseChannel.DisplayName}"
                : "Source needs attention",
            new WindowsDiagnosticFolderService());
        providerSelectionStore ??= new JsonLauncherProviderSelectionStore(installLayout.StateDirectory);
        var activeConfigurationSelection = new LauncherProviderSelection(
            distributionProvider.Id,
            releaseChannel.Id);
        viewModel.ProviderSwitchCoordinator = new(
            new LauncherProviderSourceSwitchService(
                distributionProviderCatalog,
                providerSelectionStore,
                installLayout.StateDirectory,
                selection => selection == activeConfigurationSelection
                    ? configurationEvidence
                    : BundledLauncherProviderCatalog.LoadConfigurationDiagnosisEvidence(
                        distributionProviderCatalog,
                        reviewedReleases,
                        selection)),
            providerComponents.Select(component => component.SwitchEndpoint),
            installLayout.StateDirectory);
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
        var changed = before != CaptureHomeState();
        return changed
            ? ObservableActionResult.Changed("Mod Bridge status refreshed. The displayed status changed.")
            : ObservableActionResult.Unchanged("Mod Bridge status is up to date. No changes were found.");
    }

    private void RefreshCore()
    {
        snapshot = environmentProbe.Capture();
        presentation = LauncherHomePresentation.FromSnapshot(snapshot);
        localHealth = modManagementCoordinator.CaptureHealth(
            snapshot.SelectedGameDirectory,
            snapshot.IsGameRunning);
        homeHealth = HomeHealthProjection.FromSnapshot(localHealth);
        modPresentation = localHealth.ModManagement;
        RefreshLaunchPresentations();
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
        if (!CanManageMod || snapshot.SelectedGameDirectory is null)
        {
            return null;
        }

        if (!actionFeedback.Mod.TryBegin("Checking the selected source for the latest community mod…"))
        {
            return null;
        }
        try
        {
            var preparation = await modManagementCoordinator.PrepareLatestAsync(
                snapshot.SelectedGameDirectory,
                snapshot.IsGameRunning,
                cancellationToken);
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
                or UnauthorizedAccessException)
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
        if (!actionFeedback.Mod.TryBegin(ModOperationAcceptedMessage(preparation)))
        {
            return null;
        }

        try
        {
            var result = await modManagementCoordinator.ExecuteAsync(preparation, cancellationToken);
            if (preparation.IsAdoptionOnly && result.IsSuccess)
            {
                actionFeedback.Mod.Complete(
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
            Refresh();
        }
    }

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
    }

    private async Task<ObservableActionResult> LaunchSelectedTargetAsync()
    {
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

        var result = await gameLaunchCoordinator.LaunchAsync(
            snapshot.SelectedGameDirectory,
            selectedLaunchTarget,
            allowUnverifiedProxy);
        RefreshCore();
        return ProjectLaunchResult(result);
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
            snapshot.SelectedGameDirectory,
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
            snapshot.SelectedGameDirectory,
            out var message), message);

    public void OpenLogsFolder()
    {
        var directory = CanOpenLogsFolder ? snapshot.SelectedGameDirectory : null;
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
            async token =>
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
                        or LauncherProviderAtomicSwitchPhase.RolledBack);
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
        if (!CanUninstallMod)
        {
            return null;
        }
        return await ExecuteMaintenanceAsync(
            "Removing the Mod Bridge-managed community mod…",
            token => modManagementCoordinator.UninstallAsync(SelectedGameDirectory!, token),
            cancellationToken);
    }

    public async Task<ModDeploymentResult?> StopManagingModAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanStopManagingMod)
        {
            return null;
        }
        return await ExecuteMaintenanceAsync(
            "Detaching Mod Bridge ownership from the selected installation…",
            token => modManagementCoordinator.StopManagingAsync(SelectedGameDirectory!, token),
            cancellationToken);
    }

    private async Task<ModDeploymentResult?> ExecuteMaintenanceAsync(
        string progress,
        Func<CancellationToken, Task<ModDeploymentResult>> operation,
        CancellationToken cancellationToken)
    {
        if (!actionFeedback.Mod.TryBegin(progress))
        {
            return null;
        }
        try
        {
            var result = await operation(cancellationToken);
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
                or UnauthorizedAccessException)
        {
            actionFeedback.Mod.Fail($"The maintenance operation failed: {exception.Message}");
            return null;
        }
        finally
        {
            Refresh();
        }
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
        !recoveryRequired
        && !isModOperationInProgress
        && !isLaunchInProgress;

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
            launchPresentation.CanExecute && !actionFeedback.Mod.IsWorking,
            launchPresentation.AutomationName);

    private void SelectLaunchTarget(LauncherLaunchTarget target)
    {
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
        var selected = selectedLaunchTarget == target ? ", selected" : string.Empty;
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
        primeLaunchChoice = gameLaunchCoordinator.CapturePresentation(
            snapshot.SelectedGameDirectory,
            LauncherLaunchTarget.PrimeExecutable,
            localHealth.Installation);
        scopelyLaunchChoice = gameLaunchCoordinator.CapturePresentation(
            snapshot.SelectedGameDirectory,
            LauncherLaunchTarget.ScopelyLauncher,
            localHealth.Installation);
        launchPresentation = GetLaunchChoice(selectedLaunchTarget);
    }

    private GameLaunchPresentation GetLaunchChoice(LauncherLaunchTarget target) =>
        target == LauncherLaunchTarget.PrimeExecutable
            ? primeLaunchChoice
            : scopelyLaunchChoice;

    private HomeState CaptureHomeState() => new(
        snapshot.HealthCode,
        snapshot.IsGameRunning,
        snapshot.SelectedGameDirectory,
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
