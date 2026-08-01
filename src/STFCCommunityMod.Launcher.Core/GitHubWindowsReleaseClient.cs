using System.Net;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed record WindowsReleaseDiscovery(
    WindowsReleaseManifest Manifest,
    ModReleaseArtifact ModArtifact);

public interface IWindowsReleaseDiscoveryClient
{
    Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
        string channel,
        Version currentLauncherVersion,
        CancellationToken cancellationToken = default);
}

public sealed class GitHubWindowsReleaseClient(HttpClient httpClient) : IWindowsReleaseDiscoveryClient
{
    private const string Repository = "Guffawaffle/stfc-mod";
    private const string ManifestFileName = "stfc-community-mod-release-manifest.json";
    private const int MaximumReleaseResponseBytes = 2 * 1024 * 1024;
    private const int MaximumManifestBytes = 1024 * 1024;
    private static readonly Uri ReleasesUri = new(
        $"https://api.github.com/repos/{Repository}/releases?per_page=30");

    public async Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
        string channel,
        Version currentLauncherVersion,
        CancellationToken cancellationToken = default)
    {
        if (channel is not ("stable" or "preview"))
        {
            throw new ArgumentException("Release channel must be stable or preview.", nameof(channel));
        }
        ArgumentNullException.ThrowIfNull(currentLauncherVersion);

        using var releasesRequest = CreateRequest(ReleasesUri);
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

            var artifact = WindowsReleaseSelectionPolicy.SelectModArtifact(
                manifest,
                channel,
                currentLauncherVersion);
            return new(manifest, artifact);
        }

        throw new InvalidDataException(
            $"No {channel} GitHub release contains a supported Windows release manifest.");
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

    private static bool TrySelectManifestAsset(
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
            $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{ManifestFileName}");
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object
                || !TryReadString(asset, "name", out var name)
                || !TryReadString(asset, "browser_download_url", out var downloadUrl)
                || name != ManifestFileName
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
