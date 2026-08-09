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

    private readonly LauncherEnvironmentProbe environmentProbe;
    private readonly IModManagementCoordinator modManagementCoordinator;
    private readonly GameLaunchHandoffCoordinator gameLaunchCoordinator;
    private readonly LauncherDiagnosticService diagnosticService;
    private readonly LauncherSelfUpdateService launcherSelfUpdateService;
    private readonly ILauncherReleaseDiscoveryClient releaseDiscoveryClient;
    private readonly ILauncherUiPreferencesStore uiPreferencesStore;
    private readonly LauncherDistributionProviderCatalog distributionProviderCatalog;
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
    private readonly DispatcherTimer refreshActionStatusTimer;
    private bool isDisposed;

    internal LauncherProviderAtomicSwitchCoordinator? ProviderSwitchCoordinator { get; private set; }

    private MainWindowViewModel(
        LauncherEnvironmentProbe environmentProbe,
        IModManagementCoordinator modManagementCoordinator,
        GameLaunchHandoffCoordinator gameLaunchCoordinator,
        LauncherDiagnosticService diagnosticService,
        LauncherSelfUpdateService launcherSelfUpdateService,
        ILauncherReleaseDiscoveryClient releaseDiscoveryClient,
        ILauncherUiPreferencesStore uiPreferencesStore,
        LauncherDistributionProviderCatalog distributionProviderCatalog,
        string modSourceMetadata,
        IDiagnosticFolderService diagnosticFolderService)
    {
        this.environmentProbe = environmentProbe;
        this.modManagementCoordinator = modManagementCoordinator;
        this.gameLaunchCoordinator = gameLaunchCoordinator;
        this.diagnosticService = diagnosticService;
        this.launcherSelfUpdateService = launcherSelfUpdateService;
        this.releaseDiscoveryClient = releaseDiscoveryClient;
        this.uiPreferencesStore = uiPreferencesStore;
        this.distributionProviderCatalog = distributionProviderCatalog;
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
        actionFeedback.Refresh.PropertyChanged -= RefreshActionState_PropertyChanged;
        actionFeedback.Mod.PropertyChanged -= ModActionState_PropertyChanged;
        actionFeedback.Launch.PropertyChanged -= LaunchActionState_PropertyChanged;
        actionFeedback.LauncherUpdate.PropertyChanged -= LauncherUpdateActionState_PropertyChanged;
        homeFeedback.PropertyChanged -= HomeFeedback_PropertyChanged;
        GC.SuppressFinalize(this);
    }

    public string GameSectionStatus => presentation.GameSectionStatus;

    public string GameFolderStatus => presentation.GameFolderStatus;

    public string GameFolderIcon => presentation.GameFolderIcon;

    public LauncherHomeTone GameFolderTone => presentation.GameFolderTone;

    public string GameFolderStatusAutomationName => presentation.GameFolderStatusAutomationName;

    public string GameFolderActionLabel => presentation.GameFolderActionLabel;

    public string GameFolderActionAutomationName => presentation.GameFolderActionAutomationName;

    public string GameClientStatus => presentation.GameClientStatus;

    public string GameClientIcon => presentation.GameClientIcon;

    public LauncherHomeTone GameClientTone => presentation.GameClientTone;

    public string GameClientStatusAutomationName => presentation.GameClientStatusAutomationName;

    public bool IsGameRunning => presentation.IsGameRunning;

    public LauncherProviderCompatibilityState ModProviderCompatibility =>
        localHealth.ProviderCompatibility;

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

    public LauncherHomeTone ModTone => HasIncompleteProviderSwitch
        ? LauncherHomeTone.Error
        : modPresentation.Tone;

    public string ModActionLabel => actionFeedback.Mod.IsWorking
        ? "Working…"
        : HasIncompleteProviderSwitch
            ? "Recover"
            : modPresentation.ActionLabel;

    public string ModActionAutomationName => actionFeedback.Mod.IsWorking
        ? actionFeedback.Mod.AutomationAnnouncement
        : HasIncompleteProviderSwitch
            ? "Recover the incomplete provider switch"
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

    public string DiagnosticRecoveryAvailability => CanRecoverMod
        ? HasIncompleteProviderSwitch
            ? "Recovery is available for the incomplete provider switch. DLL, provider selection, and TOML state will be restored together."
            : "Recovery is available for the detected incomplete transaction."
        : ModActionKind == ModManagementActionKind.Recover
            ? modPresentation.AutomationName
            : "No incomplete deployment transaction is available to recover.";

    public string DiagnosticRemovalAvailability => CanUninstallMod
        ? "Removal is available after confirmation."
        : IsGameRunning
            ? "Close Star Trek Fleet Command before removing the managed community mod."
            : "Removal is available only for a verified Mod Bridge-managed installation owned by the selected provider.";

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

    public string LauncherUpdateActionAutomationName => actionFeedback.LauncherUpdate.IsWorking
        ? actionFeedback.LauncherUpdate.AutomationAnnouncement
        : "Check for a Mod Bridge self-update";

    public string LauncherUpdateFeedback => actionFeedback.LauncherUpdate.StatusText;

    public bool CanCheckLauncherUpdate => actionFeedback.LauncherUpdate.IsCommandAvailable;

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
        ILauncherProviderSelectionStore? providerSelectionStore = null)
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
                reviewedCertification: binding.ReviewedCertification);
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
                Deployment: providerDeployment);
        }).ToArray();
        var providerEndpoints = providerComponents.Select(component => component.Endpoint).ToArray();
        var deploymentService = providerComponents.Single(component =>
            string.Equals(component.Endpoint.ProviderId, distributionProvider.Id, StringComparison.Ordinal)).Deployment;
        IModManagementCoordinator modManagementCoordinator = new ProviderAwareModManagementCoordinator(
            distributionProvider.Id,
            providerEndpoints);
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
            configurationEvidence = distributionProvider.GetCapabilityStatus(
                    LauncherProviderCapabilityIds.ConfigurationCatalog)
                    == LauncherProviderCapabilityStatus.Supported
                ? LauncherConfigurationDiagnosisEvidence.Supported(
                    distributionProvider.Id,
                    releaseChannel.Id,
                    BundledLauncherProviderCatalog.LoadConfigurationCatalog(distributionProvider))
                : LauncherConfigurationDiagnosisEvidence.Unavailable(
                    distributionProvider.Id,
                    releaseChannel.Id,
                    distributionProvider.GetCapabilityStatus(
                        LauncherProviderCapabilityIds.ConfigurationCatalog));
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
            uiPreferencesStore,
            distributionProviderCatalog,
            string.IsNullOrWhiteSpace(providerResolutionFailure)
                ? $"{distributionProvider.DisplayName} · {releaseChannel.DisplayName}"
                : "Source needs attention",
            new WindowsDiagnosticFolderService());
        providerSelectionStore ??= new JsonLauncherProviderSelectionStore(installLayout.StateDirectory);
        viewModel.ProviderSwitchCoordinator = new(
            new LauncherProviderSourceSwitchService(
                distributionProviderCatalog,
                providerSelectionStore,
                installLayout.StateDirectory),
            providerComponents.Select(component => component.SwitchEndpoint),
            installLayout.StateDirectory);
        return viewModel;
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

        if (!actionFeedback.Mod.TryBegin("Release check accepted. Checking the selected release…"))
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
                actionFeedback.Mod.Complete(
                    true,
                    $"Community mod {preparation.ReleaseVersion} is ready for confirmation.");
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
        if (!actionFeedback.Mod.TryBegin("Installation accepted. Installing the verified community mod…"))
        {
            return null;
        }

        try
        {
            var result = await modManagementCoordinator.ExecuteAsync(preparation, cancellationToken);
            actionFeedback.CompleteModDeployment(result);
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

    private async Task<ObservableActionResult> LaunchSelectedTargetAsync()
    {
        var result = await gameLaunchCoordinator.LaunchAsync(
            snapshot.SelectedGameDirectory,
            selectedLaunchTarget);
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
        diagnosticPreview = diagnosticService.BuildPreview(
            snapshot.SelectedGameDirectory,
            localHealth);
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
            actionFeedback.LauncherUpdate.Complete(
                false,
                "Windows App Installer manages Mod Bridge updates and checks the signed package channel when the app starts.");
            return null;
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
            modManagementCoordinator.UninstallAsync,
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
        OnPropertyChanged(nameof(CanManageMod));
        OnPropertyChanged(nameof(ModActionKind));
        OnPropertyChanged(nameof(CanRecoverMod));
        OnPropertyChanged(nameof(CanUninstallMod));
        OnPropertyChanged(nameof(DiagnosticRecoveryAvailability));
        OnPropertyChanged(nameof(DiagnosticRemovalAvailability));
        UpdateModActionAvailability();
    }

    private void UpdateModActionAvailability() =>
        actionFeedback.Mod.SetAvailability(modPresentation.CanExecute, modPresentation.AutomationName);

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
        var availability = choice.CanExecute
            ? $", available, {choice.Reason}"
            : $", unavailable, {choice.Reason}, {choice.NextActionLabel}";
        return $"{label}{selected}{availability}";
    }

    private string BuildChoiceStatus(LauncherLaunchTarget target)
    {
        var choice = GetLaunchChoice(target);
        return choice.CanExecute
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
            case nameof(ObservableActionState.IsWorking):
                OnPropertyChanged(nameof(IsModOperationInProgress));
                OnPropertyChanged(nameof(ModActionLabel));
                OnPropertyChanged(nameof(CanRecoverMod));
                OnPropertyChanged(nameof(CanUninstallMod));
                OnPropertyChanged(nameof(CanLaunchGame));
                break;
            case nameof(ObservableActionState.AutomationAnnouncement):
                OnPropertyChanged(nameof(ModActionAutomationName));
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
                break;
        }
    }

    private void LauncherUpdateActionState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        switch (e.PropertyName)
        {
            case nameof(ObservableActionState.IsWorking):
                OnPropertyChanged(nameof(LauncherUpdateActionLabel));
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
                OnPropertyChanged(nameof(CanRecoverMod));
                OnPropertyChanged(nameof(CanUninstallMod));
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
