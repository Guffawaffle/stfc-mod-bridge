using System.IO;
using System.Reflection;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public sealed class LauncherAboutViewModel
{
    private const string ProductRepository =
        "https://github.com/Guffawaffle/stfc-mod-bridge";

    public LauncherAboutViewModel(
        LauncherAboutCatalog content,
        LauncherConfigurationCatalog configurationCatalog,
        LauncherSettingsActivationDiagnostics diagnostics,
        Action<Uri>? openExternalUri,
        Action? openDataFolder = null,
        Action? manageApplication = null,
        Action? openReleaseSecurityGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(configurationCatalog);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var assembly = typeof(LauncherAboutViewModel).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        ProductName = ModBridgeProductIdentity.ProductName;
        Descriptor = ModBridgeProductIdentity.Descriptor;
        Description = ModBridgeProductIdentity.Description;
        Version = LauncherInstalledProduct.DisplayVersion(assembly);
        BuildProvenance = string.IsNullOrWhiteSpace(informationalVersion)
            ? "No informational build identity is embedded."
            : informationalVersion;
        Provider = diagnostics.ProviderDisplayName
            ?? configurationCatalog.Source.DisplayName;
        ProviderId = diagnostics.ProviderId
            ?? configurationCatalog.Source.StableId;
        DetectedRuntime = diagnostics.DetectedRuntime;
        ReleaseChannel = diagnostics.ReleaseChannelDisplayName
            ?? "Not reported";
        ReleaseRepository = diagnostics.ReleaseRepository
            ?? configurationCatalog.Source.Repository;
        RuntimeRepositoryUrl = BuildGitHubRepositoryUrl(ReleaseRepository);
        RepositoryUrl = ProductRepository;
        ReleasesUrl = $"{ProductRepository}/releases";
        ProductLicenseUrl = $"{ProductRepository}/blob/main/LICENSE";
        var installLayout = PerUserInstallLayout.FromCurrentUser();
        IsPackagedInstallation = WindowsPackageIdentity.IsCurrentProcessPackaged;
        InstallationKind = IsPackagedInstallation ? "Windows MSIX package" : "Standalone copy";
        ApplicationManagementDescription = IsPackagedInstallation
            ? "Windows owns Mod Bridge package updates and uninstall. Local data remains outside the package, and removing the app never removes the installed Community Mod or its game configuration."
            : "This copy is running standalone, so Windows does not install, update, or uninstall it. Remove its application folder to remove this copy. Local data remains separate, and removing Mod Bridge never removes the installed Community Mod or its game configuration.";
        ProgramDirectory = WindowsPackageIdentity.CurrentInstallDirectory ??
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        DataDirectory = installLayout.StateDirectory;
        Contributors = content.Contributors;
        Acknowledgements = content.Acknowledgements;
        ThirdPartyNotices = content.ThirdPartyNotices;
        GameAcknowledgement = content.GameAcknowledgement;
        NoticeCoverageStatus = content.NoticeCoverageStatus;
        LegalReviewStatus = content.LegalReviewStatus;
        OpenExternalLinkCommand = new ExternalUriCommand(openExternalUri);
        OpenDataFolderCommand = new SettingsActionCommand(
            () => openDataFolder?.Invoke(),
            () => openDataFolder is not null);
        ManageApplicationCommand = new SettingsActionCommand(
            () => manageApplication?.Invoke(),
            () => IsPackagedInstallation && manageApplication is not null);
        OpenReleaseSecurityGuidanceCommand = new SettingsActionCommand(
            () => openReleaseSecurityGuidance?.Invoke(),
            () => openReleaseSecurityGuidance is not null);
    }

    public string ProductName { get; }

    public string Descriptor { get; }

    public string Description { get; }

    public string Version { get; }

    public string BuildProvenance { get; }

    public string Provider { get; }

    public string ProviderId { get; }

    public string DetectedRuntime { get; }

    public string ReleaseChannel { get; }

    public string ReleaseRepository { get; }

    public string? RuntimeRepositoryUrl { get; }

    public string RepositoryUrl { get; }

    public string ReleasesUrl { get; }

    public string ProductLicenseUrl { get; }

    public string ProgramDirectory { get; }

    public string DataDirectory { get; }

    public bool IsPackagedInstallation { get; }

    public string InstallationKind { get; }

    public string ApplicationManagementDescription { get; }

    public IReadOnlyList<LauncherContributor> Contributors { get; }

    public IReadOnlyList<LauncherAcknowledgement> Acknowledgements { get; }

    public IReadOnlyList<LauncherThirdPartyNotice> ThirdPartyNotices { get; }

    public string GameAcknowledgement { get; }

    public string NoticeCoverageStatus { get; }

    public string LegalReviewStatus { get; }

    public ICommand OpenExternalLinkCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public ICommand ManageApplicationCommand { get; }

    public ICommand OpenReleaseSecurityGuidanceCommand { get; }

    private static string? BuildGitHubRepositoryUrl(string repository)
    {
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && parts.All(part => part.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
                ? $"https://github.com/{parts[0]}/{parts[1]}"
                : null;
    }

    private sealed class ExternalUriCommand(Action<Uri>? openExternalUri) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) =>
            openExternalUri is not null
            && TryParseHttpsUri(parameter, out _);

        public void Execute(object? parameter)
        {
            if (openExternalUri is not null
                && TryParseHttpsUri(parameter, out var uri))
            {
                openExternalUri(uri);
            }
        }

        private static bool TryParseHttpsUri(object? parameter, out Uri uri)
        {
            var parsed = parameter as Uri;
            if (parsed is null
                && parameter is string text)
            {
                _ = Uri.TryCreate(text, UriKind.Absolute, out parsed);
            }

            uri = parsed ?? new Uri("https://invalid.invalid");
            return parsed is not null
                && string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }
    }
}
