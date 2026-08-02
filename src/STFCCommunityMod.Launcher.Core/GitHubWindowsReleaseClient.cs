using System.Net;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed record WindowsReleaseDiscovery(
    WindowsReleaseManifest Manifest,
    ModReleaseArtifact ModArtifact,
    LauncherReleaseArtifact? LauncherArtifact = null);

public sealed record LauncherReleaseDiscovery(
    WindowsReleaseManifest Manifest,
    LauncherReleaseArtifact LauncherArtifact);

public interface IWindowsReleaseDiscoveryClient
{
    Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
        string channel,
        Version currentLauncherVersion,
        CancellationToken cancellationToken = default);
}

public interface ILauncherReleaseDiscoveryClient
{
    Task<LauncherReleaseDiscovery> DiscoverLatestAsync(
        string channel,
        Version currentLauncherVersion,
        CancellationToken cancellationToken = default);
}

public sealed class GitHubWindowsReleaseClient : IWindowsReleaseDiscoveryClient
{
    private readonly GitHubReleaseManifestClient manifestClient;
    private readonly string repository;

    public GitHubWindowsReleaseClient(
        HttpClient httpClient,
        string repository,
        string manifestFileName)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFileName);
        if (repository.Count(character => character == '/') != 1
            || repository.Any(character => !(char.IsLetterOrDigit(character)
                || character is '/' or '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "GitHub repository must use owner/name coordinates.",
                nameof(repository));
        }
        if (!string.Equals(Path.GetFileName(manifestFileName), manifestFileName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Release manifest asset must be a file name, not a path.",
                nameof(manifestFileName));
        }

        this.repository = repository;
        manifestClient = new(httpClient, repository, manifestFileName);
    }

    public async Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
        string channel,
        Version currentLauncherVersion,
        CancellationToken cancellationToken = default)
    {
        var manifests = await manifestClient.DiscoverCandidatesAsync(channel, cancellationToken);
        var manifest = WindowsReleaseSelectionPolicy.SelectHighestEligibleRelease(
            manifests,
            channel,
            currentLauncherVersion,
            repository);
        var artifact = WindowsReleaseSelectionPolicy.SelectModArtifact(
            manifest,
            channel,
            currentLauncherVersion,
            repository);
        var launcherArtifact = manifest.Artifacts.Any(candidate => candidate.Id == "windows-launcher-archive-x64")
            ? WindowsReleaseSelectionPolicy.SelectLauncherArtifact(manifest, channel, currentLauncherVersion, repository)
            : null;
        return new(manifest, artifact, launcherArtifact);
    }
}

public sealed class GitHubLauncherReleaseClient : ILauncherReleaseDiscoveryClient
{
    private readonly GitHubReleaseManifestClient manifestClient;
    private readonly string repository;

    public GitHubLauncherReleaseClient(HttpClient httpClient, string repository, string manifestFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        this.repository = repository;
        manifestClient = new(httpClient, repository, manifestFileName);
    }

    public async Task<LauncherReleaseDiscovery> DiscoverLatestAsync(
        string channel,
        Version currentLauncherVersion,
        CancellationToken cancellationToken = default)
    {
        var manifests = await manifestClient.DiscoverCandidatesAsync(channel, cancellationToken);
        var manifest = WindowsReleaseSelectionPolicy.SelectHighestEligibleRelease(
            manifests,
            channel,
            currentLauncherVersion,
            repository);
        var artifact = WindowsReleaseSelectionPolicy.SelectLauncherArtifact(
            manifest,
            channel,
            currentLauncherVersion,
            repository);
        var candidateVersion = Version.Parse(
            WindowsReleaseSelectionPolicy.DeriveEmbeddedFileVersion(artifact.ReleaseVersion));
        var installedVersion = new Version(
            currentLauncherVersion.Major,
            currentLauncherVersion.Minor,
            Math.Max(currentLauncherVersion.Build, 0),
            Math.Max(currentLauncherVersion.Revision, 0));
        if (candidateVersion <= installedVersion)
        {
            throw new InvalidDataException(
                $"No newer {channel} launcher release is eligible; {artifact.ReleaseVersion} does not advance "
                + $"the installed launcher {currentLauncherVersion}.");
        }
        return new(manifest, artifact);
    }
}

internal sealed class GitHubReleaseManifestClient
{
    private const int MaximumReleaseResponseBytes = 2 * 1024 * 1024;
    private const int MaximumManifestBytes = 1024 * 1024;
    private readonly HttpClient httpClient;
    private readonly string repository;
    private readonly string manifestFileName;
    private readonly Uri releasesUri;

    public GitHubReleaseManifestClient(HttpClient httpClient, string repository, string manifestFileName)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFileName);
        if (repository.Count(character => character == '/') != 1
            || repository.Any(character => !(char.IsLetterOrDigit(character)
                || character is '/' or '-' or '_' or '.')))
        {
            throw new ArgumentException("GitHub repository must use owner/name coordinates.", nameof(repository));
        }
        if (!string.Equals(Path.GetFileName(manifestFileName), manifestFileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Release manifest asset must be a file name, not a path.", nameof(manifestFileName));
        }

        this.httpClient = httpClient;
        this.repository = repository;
        this.manifestFileName = manifestFileName;
        releasesUri = new($"https://api.github.com/repos/{repository}/releases?per_page=30");
    }

    public async Task<IReadOnlyList<WindowsReleaseManifest>> DiscoverCandidatesAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        if (channel is not ("stable" or "preview"))
        {
            throw new ArgumentException("Release channel must be stable or preview.", nameof(channel));
        }

        using var releasesRequest = CreateRequest(releasesUri);
        using var releasesResponse = await httpClient.SendAsync(
            releasesRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var releasesBytes = await ReadBoundedAsync(
            releasesResponse,
            MaximumReleaseResponseBytes,
            "GitHub releases",
            cancellationToken);
        using var releasesDocument = JsonDocument.Parse(releasesBytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        if (releasesDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub releases response must be an array.");
        }

        var manifests = new List<WindowsReleaseManifest>();
        foreach (var release in releasesDocument.RootElement.EnumerateArray())
        {
            if (!TrySelectManifestAsset(release, channel, out var tag, out var manifestUri))
            {
                continue;
            }

            using var manifestRequest = CreateRequest(manifestUri);
            using var manifestResponse = await httpClient.SendAsync(
                manifestRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var manifestBytes = await ReadBoundedAsync(
                manifestResponse,
                MaximumManifestBytes,
                "release manifest",
                cancellationToken);
            using var manifestStream = new MemoryStream(manifestBytes, writable: false);
            var manifest = WindowsReleaseManifestParser.Parse(manifestStream);
            if (!string.Equals(manifest.Tag, tag, StringComparison.Ordinal))
            {
                throw new InvalidDataException("GitHub release tag and release manifest tag do not match.");
            }
            manifests.Add(manifest);
        }

        return manifests.Count > 0
            ? manifests
            : throw new InvalidDataException(
                $"No {channel} GitHub release contains the required manifest asset.");
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Release discovery permits HTTPS endpoints only.");
        }
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("STFC-Community-Mod-Launcher/0.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        string context,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"{context} request returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException($"{context} exceeds the {maximumBytes}-byte limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                return destination.ToArray();
            }
            if (destination.Length + count > maximumBytes)
            {
                throw new InvalidDataException($"{context} exceeds the {maximumBytes}-byte limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    private bool TrySelectManifestAsset(
        JsonElement release,
        string channel,
        out string tag,
        out Uri manifestUri)
    {
        tag = string.Empty;
        manifestUri = null!;
        if (release.ValueKind != JsonValueKind.Object
            || !TryReadBoolean(release, "draft", out var draft)
            || !TryReadBoolean(release, "prerelease", out var prerelease)
            || draft
            || (channel == "stable" ? prerelease : !prerelease)
            || !TryReadString(release, "tag_name", out tag)
            || !release.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var expectedUri = new Uri(
            $"https://github.com/{repository}/releases/download/{Uri.EscapeDataString(tag)}/{manifestFileName}");
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object
                || !TryReadString(asset, "name", out var name)
                || !TryReadString(asset, "browser_download_url", out var downloadUrl)
                || name != manifestFileName
                || !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var candidateUri)
                || candidateUri != expectedUri)
            {
                continue;
            }
            manifestUri = expectedUri;
            return true;
        }
        return false;
    }

    private static bool TryReadBoolean(JsonElement parent, string name, out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(name, out var element)
            || (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False))
        {
            return false;
        }
        value = element.GetBoolean();
        return true;
    }

    private static bool TryReadString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}
