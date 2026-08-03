using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core;

public enum ModInstallationEvidenceState
{
    NoGameTarget,
    InvalidGameTarget,
    NotInstalled,
    ManualInstallation,
    ManagedVerified,
    ManagedChanged,
    RecoveryRequired,
    Unavailable,
}

public enum LauncherProviderCompatibilityState
{
    NotApplicable,
    MatchesSelectedProvider,
    DifferentProvider,
    Unattributed,
    Unknown,
}

public enum LauncherNativeEvidenceState
{
    NotApplicable,
    Unknown,
    Healthy,
    Degraded,
    Incompatible,
}

public enum ModUpdateEvidenceState
{
    NotApplicable,
    Unknown,
    UpToDate,
    UpdateAvailable,
}

public sealed record LauncherProviderHealthContext(
    string ProviderId,
    string ReleaseChannelId,
    string RuntimeDistributionId,
    bool CanMutate,
    string UnavailableReason);

public sealed record ModInstallationEvidence(
    ModInstallationEvidenceState State,
    bool IsGameRunning,
    string? InstalledVersion = null,
    string? InstalledProviderId = null,
    string? InstalledReleaseChannelId = null,
    string? InstalledRuntimeDistributionId = null,
    string? InstalledSha256 = null)
{
    public bool HasCompleteAttribution =>
        !string.IsNullOrWhiteSpace(InstalledProviderId)
        && !string.IsNullOrWhiteSpace(InstalledReleaseChannelId)
        && !string.IsNullOrWhiteSpace(InstalledRuntimeDistributionId);
}

public sealed record ModUpdateEvidence(
    ModUpdateEvidenceState State,
    DateTimeOffset ObservedAtUtc,
    string ProviderId,
    string ReleaseChannelId,
    string RuntimeDistributionId,
    string InstalledSha256,
    string? AvailableVersion = null);

public sealed record LauncherNativeHealthEvidence(
    LauncherNativeEvidenceState GameCompatibility,
    LauncherNativeEvidenceState RuntimeActivation,
    LauncherNativeEvidenceState NativeSupport)
{
    public static LauncherNativeHealthEvidence WithoutAuthoritativeContract(bool isGameRunning) => new(
        LauncherNativeEvidenceState.Unknown,
        isGameRunning ? LauncherNativeEvidenceState.Unknown : LauncherNativeEvidenceState.NotApplicable,
        isGameRunning ? LauncherNativeEvidenceState.Unknown : LauncherNativeEvidenceState.NotApplicable);
}

public interface ILauncherNativeHealthEvidenceSource
{
    LauncherNativeHealthEvidence Capture(ModInstallationEvidence installation);
}

public sealed class UnknownLauncherNativeHealthEvidenceSource : ILauncherNativeHealthEvidenceSource
{
    public LauncherNativeHealthEvidence Capture(ModInstallationEvidence installation) =>
        LauncherNativeHealthEvidence.WithoutAuthoritativeContract(installation.IsGameRunning);
}

public interface IModDeploymentStateReader
{
    ModDeploymentJournal? ReadJournal();

    ModInstalledArtifactState? ReadInstalledState();
}

public interface IModInstallationFileSystem
{
    bool FileExists(string path);

    string ComputeSha256(string path);
}

public interface IGameTargetHealthInspector
{
    bool IsValid(string gameDirectory);
}

public sealed class SystemGameTargetHealthInspector : IGameTargetHealthInspector
{
    public bool IsValid(string gameDirectory) => GameInstallValidator.Validate(gameDirectory).IsValid;
}

public sealed class SystemModInstallationFileSystem : IModInstallationFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public sealed class ModInstallationInspector(
    IModDeploymentStateReader stateReader,
    IModInstallationFileSystem fileSystem,
    IGameTargetHealthInspector? gameTargetInspector = null)
{
    private readonly IGameTargetHealthInspector gameTargetInspector =
        gameTargetInspector ?? new SystemGameTargetHealthInspector();

    public ModInstallationEvidence Capture(string? gameDirectory, bool isGameRunning)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return new(ModInstallationEvidenceState.NoGameTarget, isGameRunning);
        }

        string normalizedGameDirectory;
        try
        {
            normalizedGameDirectory = Path.GetFullPath(gameDirectory);
            if (!gameTargetInspector.IsValid(normalizedGameDirectory))
            {
                return new(ModInstallationEvidenceState.InvalidGameTarget, isGameRunning);
            }
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            return new(ModInstallationEvidenceState.InvalidGameTarget, isGameRunning);
        }

        try
        {
            var journal = stateReader.ReadJournal();
            if (journal is not null
                && journal.Phase is not (ModDeploymentPhase.Committed
                    or ModDeploymentPhase.RolledBack
                    or ModDeploymentPhase.Failed))
            {
                return new(ModInstallationEvidenceState.RecoveryRequired, isGameRunning);
            }

            var installedState = stateReader.ReadInstalledState();
            var artifactPath = Path.Combine(normalizedGameDirectory, "version.dll");
            if (installedState is null)
            {
                return new(
                    fileSystem.FileExists(artifactPath)
                        ? ModInstallationEvidenceState.ManualInstallation
                        : ModInstallationEvidenceState.NotInstalled,
                    isGameRunning);
            }

            var verified = PathsEqual(installedState.GameDirectory, normalizedGameDirectory)
                && fileSystem.FileExists(artifactPath)
                && string.Equals(
                    fileSystem.ComputeSha256(artifactPath),
                    installedState.Sha256,
                    StringComparison.OrdinalIgnoreCase);
            return new(
                verified
                    ? ModInstallationEvidenceState.ManagedVerified
                    : ModInstallationEvidenceState.ManagedChanged,
                isGameRunning,
                installedState.Version,
                installedState.ProviderId,
                installedState.ReleaseChannelId,
                installedState.RuntimeDistributionId,
                installedState.Sha256);
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            return new(ModInstallationEvidenceState.Unavailable, isGameRunning);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsInspectionFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException;
}

public sealed record LauncherHealthSnapshot(
    ModInstallationEvidence Installation,
    LauncherProviderCompatibilityState ProviderCompatibility,
    ModUpdateEvidenceState UpdateAvailability,
    LauncherNativeEvidenceState GameCompatibility,
    LauncherNativeEvidenceState RuntimeActivation,
    LauncherNativeEvidenceState NativeSupport,
    IReadOnlyList<LauncherHealthDimension> Dimensions,
    ModManagementPresentation ModManagement);

public static class LauncherHealthResolver
{
    public static LauncherHealthSnapshot Resolve(
        ModInstallationEvidence installation,
        LauncherProviderHealthContext provider,
        ModUpdateEvidence? updateEvidence = null,
        LauncherNativeHealthEvidence? nativeHealth = null,
        DateTimeOffset? nowUtc = null,
        TimeSpan? maximumUpdateAge = null)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(provider);

        var providerCompatibility = ResolveProviderCompatibility(installation, provider);
        var updateAvailability = ResolveUpdateAvailability(
            installation,
            provider,
            providerCompatibility,
            updateEvidence,
            nowUtc ?? DateTimeOffset.UtcNow,
            maximumUpdateAge ?? TimeSpan.FromMinutes(30));
        nativeHealth ??= LauncherNativeHealthEvidence.WithoutAuthoritativeContract(installation.IsGameRunning);
        if (!installation.IsGameRunning)
        {
            nativeHealth = nativeHealth with
            {
                RuntimeActivation = LauncherNativeEvidenceState.NotApplicable,
                NativeSupport = LauncherNativeEvidenceState.NotApplicable,
            };
        }
        var dimensions = new List<LauncherHealthDimension>
        {
            ResolveInstallationDimension(installation),
            ResolveProviderDimension(providerCompatibility),
            ResolveUpdateDimension(updateAvailability, updateEvidence?.AvailableVersion),
            ResolveNativeDimension(
                LauncherHealthDimensionCategory.GameCompatibility,
                nativeHealth.GameCompatibility,
                "Game compatibility"),
            ResolveNativeDimension(
                LauncherHealthDimensionCategory.RuntimeActivation,
                nativeHealth.RuntimeActivation,
                "Runtime activation"),
            ResolveNativeDimension(
                LauncherHealthDimensionCategory.NativeSupport,
                nativeHealth.NativeSupport,
                "Native hook support"),
        };
        if (!provider.CanMutate)
        {
            dimensions.Add(new(
                LauncherHealthDimensionCategory.ProviderAvailability,
                LauncherHealthSeverity.Unknown,
                "Provider operations unavailable",
                "Verified local installation state remains usable; provider-bound mutations fail closed."));
        }

        return new(
            installation,
            providerCompatibility,
            updateAvailability,
            nativeHealth.GameCompatibility,
            nativeHealth.RuntimeActivation,
            nativeHealth.NativeSupport,
            dimensions,
            ResolveManagement(
                installation,
                provider,
                providerCompatibility,
                updateAvailability,
                nativeHealth));
    }

    private static LauncherProviderCompatibilityState ResolveProviderCompatibility(
        ModInstallationEvidence installation,
        LauncherProviderHealthContext provider)
    {
        if (installation.State is ModInstallationEvidenceState.NoGameTarget
            or ModInstallationEvidenceState.InvalidGameTarget
            or ModInstallationEvidenceState.NotInstalled)
        {
            return LauncherProviderCompatibilityState.NotApplicable;
        }
        if (installation.State == ModInstallationEvidenceState.ManualInstallation)
        {
            return LauncherProviderCompatibilityState.Unattributed;
        }
        if (installation.State != ModInstallationEvidenceState.ManagedVerified)
        {
            return LauncherProviderCompatibilityState.Unknown;
        }
        if (!installation.HasCompleteAttribution)
        {
            return LauncherProviderCompatibilityState.Unattributed;
        }
        return string.Equals(installation.InstalledProviderId, provider.ProviderId, StringComparison.Ordinal)
            && string.Equals(
                installation.InstalledReleaseChannelId,
                provider.ReleaseChannelId,
                StringComparison.Ordinal)
            && string.Equals(
                installation.InstalledRuntimeDistributionId,
                provider.RuntimeDistributionId,
                StringComparison.Ordinal)
            ? LauncherProviderCompatibilityState.MatchesSelectedProvider
            : LauncherProviderCompatibilityState.DifferentProvider;
    }

    private static ModUpdateEvidenceState ResolveUpdateAvailability(
        ModInstallationEvidence installation,
        LauncherProviderHealthContext provider,
        LauncherProviderCompatibilityState providerCompatibility,
        ModUpdateEvidence? evidence,
        DateTimeOffset nowUtc,
        TimeSpan maximumAge)
    {
        if (installation.State != ModInstallationEvidenceState.ManagedVerified)
        {
            return ModUpdateEvidenceState.NotApplicable;
        }
        if (providerCompatibility != LauncherProviderCompatibilityState.MatchesSelectedProvider
            || evidence is null
            || evidence.State is not (ModUpdateEvidenceState.UpToDate or ModUpdateEvidenceState.UpdateAvailable)
            || maximumAge < TimeSpan.Zero
            || evidence.ObservedAtUtc > nowUtc
            || nowUtc - evidence.ObservedAtUtc > maximumAge
            || !string.Equals(evidence.ProviderId, provider.ProviderId, StringComparison.Ordinal)
            || !string.Equals(evidence.ReleaseChannelId, provider.ReleaseChannelId, StringComparison.Ordinal)
            || !string.Equals(evidence.RuntimeDistributionId, provider.RuntimeDistributionId, StringComparison.Ordinal)
            || !string.Equals(evidence.InstalledSha256, installation.InstalledSha256, StringComparison.OrdinalIgnoreCase))
        {
            return ModUpdateEvidenceState.Unknown;
        }
        return evidence.State;
    }

    private static LauncherHealthDimension ResolveInstallationDimension(ModInstallationEvidence installation) =>
        installation.State switch
        {
            ModInstallationEvidenceState.ManagedVerified => new(
                LauncherHealthDimensionCategory.ModInstallation,
                LauncherHealthSeverity.Healthy,
                "Mod Bridge-managed installation verified",
                "The installed artifact matches Mod Bridge-owned state."),
            ModInstallationEvidenceState.ManualInstallation => new(
                LauncherHealthDimensionCategory.ModInstallation,
                LauncherHealthSeverity.Informational,
                "Manual installation detected",
                "The artifact may remain runnable, but Mod Bridge does not claim managed integrity."),
            ModInstallationEvidenceState.ManagedChanged => new(
                LauncherHealthDimensionCategory.ModInstallation,
                LauncherHealthSeverity.ActionRequired,
                "Managed installation changed",
                "Repair is required before direct managed operation."),
            ModInstallationEvidenceState.RecoveryRequired => new(
                LauncherHealthDimensionCategory.ModInstallation,
                LauncherHealthSeverity.ActionRequired,
                "Deployment recovery required",
                "An interrupted Mod Bridge transaction must be recovered."),
            ModInstallationEvidenceState.Unavailable => new(
                LauncherHealthDimensionCategory.ModInstallation,
                LauncherHealthSeverity.Unknown,
                "Installation health unavailable",
                "Mod Bridge-owned state could not be validated safely."),
            ModInstallationEvidenceState.NotInstalled => new(
                LauncherHealthDimensionCategory.ModInstallation,
                LauncherHealthSeverity.Informational,
                "Community mod not installed",
                "No version.dll was found in the confirmed game target."),
            _ => new(
                LauncherHealthDimensionCategory.ModInstallation,
                LauncherHealthSeverity.Informational,
                "Installation target unavailable",
                "Confirm a valid game installation before managing the community mod."),
        };

    private static LauncherHealthDimension ResolveProviderDimension(
        LauncherProviderCompatibilityState compatibility) => compatibility switch
        {
            LauncherProviderCompatibilityState.MatchesSelectedProvider => new(
                LauncherHealthDimensionCategory.ProviderCompatibility,
                LauncherHealthSeverity.Healthy,
                "Installed provider matches selection",
                "Stable provider and runtime-distribution identities match."),
            LauncherProviderCompatibilityState.DifferentProvider => new(
                LauncherHealthDimensionCategory.ProviderCompatibility,
                LauncherHealthSeverity.ActionRequired,
                "Installed provider differs from selection",
                "Review provider migration before replacing the installed artifact."),
            LauncherProviderCompatibilityState.Unattributed => UnknownDimension(
                LauncherHealthDimensionCategory.ProviderCompatibility,
                "Installed provider unknown",
                "Older managed state and manual installations are never attributed by guesswork."),
            _ => UnknownDimension(
                LauncherHealthDimensionCategory.ProviderCompatibility,
                "Provider compatibility not established",
                "No attributed installed artifact is available for comparison."),
        };

    private static LauncherHealthDimension ResolveUpdateDimension(
        ModUpdateEvidenceState state,
        string? availableVersion) => state switch
        {
            ModUpdateEvidenceState.UpToDate => new(
                LauncherHealthDimensionCategory.UpdateAvailability,
                LauncherHealthSeverity.Healthy,
                "Installed release is current",
                "The latest identity-bound provider observation matches the installed artifact."),
            ModUpdateEvidenceState.UpdateAvailable => new(
                LauncherHealthDimensionCategory.UpdateAvailability,
                LauncherHealthSeverity.ActionRequired,
                "Community mod update available",
                string.IsNullOrWhiteSpace(availableVersion)
                    ? "A newer trusted provider artifact is available."
                    : $"Community mod {availableVersion} is available."),
            ModUpdateEvidenceState.NotApplicable => new(
                LauncherHealthDimensionCategory.UpdateAvailability,
                LauncherHealthSeverity.Informational,
                "Update availability not applicable",
                "A verified, attributed managed installation is required before comparing releases."),
            _ => UnknownDimension(
                LauncherHealthDimensionCategory.UpdateAvailability,
                "Update availability unknown",
                "No current identity-bound provider observation is available; local health does not depend on network discovery."),
        };

    private static LauncherHealthDimension ResolveNativeDimension(
        LauncherHealthDimensionCategory category,
        LauncherNativeEvidenceState state,
        string label) => state switch
        {
            LauncherNativeEvidenceState.Healthy => new(
                category,
                LauncherHealthSeverity.Healthy,
                $"{label} healthy",
                "Authoritative identity-bound evidence reports a healthy state."),
            LauncherNativeEvidenceState.Degraded => new(
                category,
                LauncherHealthSeverity.ActionRequired,
                $"{label} degraded",
                "Authoritative identity-bound evidence reports degraded operation."),
            LauncherNativeEvidenceState.Incompatible => new(
                category,
                LauncherHealthSeverity.ActionRequired,
                $"{label} incompatible",
                "Authoritative identity-bound evidence reports an incompatible state."),
            LauncherNativeEvidenceState.NotApplicable => new(
                category,
                LauncherHealthSeverity.Informational,
                $"{label} not applicable",
                "Live runtime evidence is not applicable while the game is not running."),
            _ => UnknownDimension(
                category,
                $"{label} unknown",
                category == LauncherHealthDimensionCategory.GameCompatibility
                    ? "No identity-bound game compatibility contract is available."
                    : "DLL, log, or process presence is not evidence that native hooks loaded successfully."),
        };

    private static ModManagementPresentation ResolveManagement(
        ModInstallationEvidence installation,
        LauncherProviderHealthContext provider,
        LauncherProviderCompatibilityState providerCompatibility,
        ModUpdateEvidenceState updateAvailability,
        LauncherNativeHealthEvidence nativeHealth)
    {
        var canMutate = !installation.IsGameRunning && provider.CanMutate;
        var providerReason = provider.CanMutate
            ? string.Empty
            : string.IsNullOrWhiteSpace(provider.UnavailableReason)
                ? "Provider-bound operations are unavailable."
                : provider.UnavailableReason;
        return installation.State switch
        {
            ModInstallationEvidenceState.NoGameTarget => new(
                "Select a game folder",
                LauncherHomeTone.Warning,
                "Install",
                ModManagementActionKind.None,
                false,
                "Community mod unavailable until a game folder is selected"),
            ModInstallationEvidenceState.InvalidGameTarget => RepairRequired(
                "The confirmed game installation is no longer valid.",
                canExecute: false),
            ModInstallationEvidenceState.RecoveryRequired => new(
                "Recovery required",
                LauncherHomeTone.Error,
                "Recover",
                ModManagementActionKind.Recover,
                !installation.IsGameRunning,
                installation.IsGameRunning
                    ? "Close Star Trek Fleet Command before recovering the community mod transaction."
                    : "Community mod transaction recovery is required"),
            ModInstallationEvidenceState.Unavailable => new(
                "Health unknown",
                LauncherHomeTone.Warning,
                "Unavailable",
                ModManagementActionKind.None,
                false,
                "The community mod deployment state could not be validated safely. Open Diagnostics."),
            ModInstallationEvidenceState.ManagedChanged => RepairRequired(
                "The installed artifact no longer matches Mod Bridge-managed state.",
                canMutate,
                providerReason),
            ModInstallationEvidenceState.ManualInstallation => new(
                "Manual installation detected",
                LauncherHomeTone.Warning,
                provider.CanMutate ? "Update" : "Unavailable",
                provider.CanMutate ? ModManagementActionKind.AdoptAndInstall : ModManagementActionKind.None,
                canMutate,
                provider.CanMutate
                    ? installation.IsGameRunning
                        ? "Close Star Trek Fleet Command before adopting the existing community mod."
                        : "Adopt the existing community mod and install the selected release"
                    : providerReason),
            ModInstallationEvidenceState.NotInstalled => new(
                "Not installed",
                LauncherHomeTone.Neutral,
                provider.CanMutate ? "Install" : "Unavailable",
                provider.CanMutate ? ModManagementActionKind.Install : ModManagementActionKind.None,
                canMutate,
                provider.CanMutate
                    ? installation.IsGameRunning
                        ? "Close Star Trek Fleet Command before installing the community mod."
                        : "Install the community mod"
                    : providerReason),
            ModInstallationEvidenceState.ManagedVerified
                when providerCompatibility == LauncherProviderCompatibilityState.DifferentProvider => new(
                    $"Installed {installation.InstalledVersion}",
                    LauncherHomeTone.Warning,
                    "Unavailable",
                    ModManagementActionKind.None,
                    false,
                    "The installed mod belongs to a different provider. Review provider migration before updating."),
            ModInstallationEvidenceState.ManagedVerified
                when nativeHealth.GameCompatibility == LauncherNativeEvidenceState.Incompatible => new(
                    "Incompatible",
                    LauncherHomeTone.Error,
                    "Unavailable",
                    ModManagementActionKind.None,
                    false,
                    "The installed community mod is incompatible with this game build. Open Diagnostics for details."),
            ModInstallationEvidenceState.ManagedVerified
                when installation.IsGameRunning
                    && (nativeHealth.RuntimeActivation == LauncherNativeEvidenceState.Degraded
                        || nativeHealth.NativeSupport == LauncherNativeEvidenceState.Degraded) => new(
                    "Running degraded",
                    LauncherHomeTone.Warning,
                    "Unavailable",
                    ModManagementActionKind.None,
                    false,
                    "Authoritative runtime evidence reports degraded community mod operation. Open Diagnostics."),
            ModInstallationEvidenceState.ManagedVerified
                when installation.IsGameRunning
                    && nativeHealth.RuntimeActivation == LauncherNativeEvidenceState.Healthy
                    && nativeHealth.NativeSupport == LauncherNativeEvidenceState.Healthy => new(
                    "Running healthy",
                    LauncherHomeTone.Success,
                    "Unavailable",
                    ModManagementActionKind.None,
                    false,
                    "Authoritative runtime evidence reports healthy community mod operation."),
            ModInstallationEvidenceState.ManagedVerified when installation.IsGameRunning => new(
                "Running health unknown",
                LauncherHomeTone.Warning,
                "Unavailable",
                ModManagementActionKind.None,
                false,
                "The game is running, but no authoritative live hook-health evidence is available."),
            ModInstallationEvidenceState.ManagedVerified
                when updateAvailability == ModUpdateEvidenceState.UpdateAvailable => new(
                    "Update available",
                    LauncherHomeTone.Warning,
                    provider.CanMutate ? "Update" : "Unavailable",
                    provider.CanMutate ? ModManagementActionKind.CheckForUpdate : ModManagementActionKind.None,
                    provider.CanMutate,
                    provider.CanMutate
                        ? "Install the observed community mod update."
                        : providerReason),
            ModInstallationEvidenceState.ManagedVerified => new(
                $"Installed {installation.InstalledVersion}",
                LauncherHomeTone.Success,
                provider.CanMutate ? "Check for updates" : "Unavailable",
                provider.CanMutate ? ModManagementActionKind.CheckForUpdate : ModManagementActionKind.None,
                canMutate,
                provider.CanMutate
                    ? installation.IsGameRunning
                        ? "Close Star Trek Fleet Command before changing the community mod."
                        : $"Check for a community mod update; installed version {installation.InstalledVersion}"
                    : providerReason),
            _ => throw new ArgumentOutOfRangeException(nameof(installation)),
        };
    }

    private static ModManagementPresentation RepairRequired(
        string detail,
        bool canExecute,
        string providerReason = "") => new(
            "Repair required",
            LauncherHomeTone.Error,
            canExecute ? "Repair" : "Unavailable",
            canExecute ? ModManagementActionKind.Repair : ModManagementActionKind.None,
            canExecute,
            string.IsNullOrWhiteSpace(providerReason) ? detail : providerReason);

    private static LauncherHealthDimension UnknownDimension(
        LauncherHealthDimensionCategory category,
        string title,
        string detail) => new(category, LauncherHealthSeverity.Unknown, title, detail);
}

public sealed class LauncherHealthService
{
    private static readonly TimeSpan MaximumUpdateAge = TimeSpan.FromMinutes(30);
    private readonly ModInstallationInspector inspector;
    private readonly LauncherProviderHealthContext provider;
    private readonly ILauncherNativeHealthEvidenceSource nativeHealthSource;
    private readonly TimeProvider timeProvider;
    private readonly object observationLock = new();
    private ModUpdateEvidence? updateEvidence;

    public LauncherHealthService(
        ModInstallationInspector inspector,
        LauncherProviderHealthContext provider,
        ILauncherNativeHealthEvidenceSource? nativeHealthSource = null,
        TimeProvider? timeProvider = null)
    {
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.nativeHealthSource = nativeHealthSource ?? new UnknownLauncherNativeHealthEvidenceSource();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public LauncherHealthSnapshot Capture(string? gameDirectory, bool isGameRunning)
    {
        var installation = inspector.Capture(gameDirectory, isGameRunning);
        ModUpdateEvidence? observation;
        lock (observationLock)
        {
            observation = updateEvidence;
        }
        return LauncherHealthResolver.Resolve(
            installation,
            provider,
            observation,
            nativeHealthSource.Capture(installation),
            timeProvider.GetUtcNow(),
            MaximumUpdateAge);
    }

    public void RecordUpdateObservation(
        string gameDirectory,
        bool isGameRunning,
        WindowsReleaseDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        var installation = inspector.Capture(gameDirectory, isGameRunning);
        if (installation.State != ModInstallationEvidenceState.ManagedVerified
            || !installation.HasCompleteAttribution
            || string.IsNullOrWhiteSpace(installation.InstalledSha256))
        {
            return;
        }
        var observation = new ModUpdateEvidence(
            string.Equals(
                installation.InstalledSha256,
                discovery.ModArtifact.Sha256,
                StringComparison.OrdinalIgnoreCase)
                ? ModUpdateEvidenceState.UpToDate
                : ModUpdateEvidenceState.UpdateAvailable,
            timeProvider.GetUtcNow(),
            provider.ProviderId,
            provider.ReleaseChannelId,
            provider.RuntimeDistributionId,
            installation.InstalledSha256,
            discovery.Manifest.ReleaseVersion);
        lock (observationLock)
        {
            updateEvidence = observation;
        }
    }
}
