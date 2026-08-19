using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed record ReviewedReleaseCertification(
    string ProviderId,
    string ChannelId,
    string RuntimeDistributionId,
    string Repository,
    string Tag,
    string ReleaseVersion,
    string SourceCommit,
    string AssetName,
    long AssetSize,
    string AssetSha256,
    string PayloadFileName,
    long PayloadSize,
    string PayloadSha256,
    string PayloadVersion,
    DateTimeOffset ObservedAtUtc,
    ReviewedRuntimeManifestCertification? RuntimeManifest = null)
{
    public Uri DownloadUri => new(
        $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(Tag)}/{Uri.EscapeDataString(AssetName)}");
}

public sealed record ReviewedRuntimeManifestCertification(
    string FileName,
    long Size,
    string Sha256);

public sealed class ReviewedReleaseCertificationCatalog
{
    private readonly Dictionary<(string ProviderId, string ChannelId), ReviewedReleaseCertification> certifications;

    public ReviewedReleaseCertificationCatalog(IEnumerable<ReviewedReleaseCertification> certifications)
    {
        ArgumentNullException.ThrowIfNull(certifications);
        this.certifications = new();
        foreach (var certification in certifications)
        {
            ArgumentNullException.ThrowIfNull(certification);
            if (!this.certifications.TryAdd((certification.ProviderId, certification.ChannelId), certification))
            {
                throw new InvalidDataException(
                    $"Reviewed release certification is duplicated for '{certification.ProviderId}/{certification.ChannelId}'.");
            }
        }
    }

    public static ReviewedReleaseCertificationCatalog Empty { get; } = new([]);

    public int Count => certifications.Count;

    public IReadOnlyCollection<ReviewedReleaseCertification> Certifications =>
        certifications.Values;

    public ReviewedReleaseCertification? Find(string providerId, string channelId) =>
        certifications.GetValueOrDefault((providerId, channelId));
}

public static class ReviewedReleaseCertificationCatalogLoader
{
    private const int SupportedSchemaVersion = 1;
    private const int MaximumCertifications = 16;
    private const long MaximumArtifactSize = 128L * 1024L * 1024L;
    private static readonly HashSet<string> RootProperties = ["schemaVersion", "certifications"];
    private static readonly HashSet<string> CertificationProperties =
    [
        "providerId", "channelId", "runtimeDistributionId", "repository", "tag", "releaseVersion",
        "sourceCommit", "assetName", "assetSize", "assetSha256", "payloadFileName", "payloadSize",
        "payloadSha256", "payloadVersion", "observedAtUtc", "runtimeManifest",
    ];
    private static readonly HashSet<string> RuntimeManifestProperties = ["fileName", "size", "sha256"];

    public static ReviewedReleaseCertificationCatalog Load(
        Stream stream,
        LauncherDistributionProviderCatalog providerCatalog)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(providerCatalog);
        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = RequireObject(document.RootElement, "reviewed release catalog");
            RejectUnknown(root, RootProperties, "reviewed release catalog");
            if (!root.TryGetProperty("schemaVersion", out var schema)
                || !schema.TryGetInt32(out var schemaVersion)
                || schemaVersion != SupportedSchemaVersion)
            {
                throw new InvalidDataException("Reviewed release catalog schema is unsupported.");
            }
            if (!root.TryGetProperty("certifications", out var values)
                || values.ValueKind != JsonValueKind.Array
                || values.GetArrayLength() > MaximumCertifications)
            {
                throw new InvalidDataException(
                    $"Reviewed release catalog must contain at most {MaximumCertifications} certifications.");
            }
            return new(values.EnumerateArray().Select(value => Read(value, providerCatalog)));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Reviewed release catalog is not valid JSON.", exception);
        }
    }

    private static ReviewedReleaseCertification Read(
        JsonElement element,
        LauncherDistributionProviderCatalog providerCatalog)
    {
        RequireObject(element, "reviewed release certification");
        RejectUnknown(element, CertificationProperties, "reviewed release certification");
        var providerId = ReadString(element, "providerId");
        if (!providerCatalog.TryGetProvider(providerId, out var provider) || provider is null)
        {
            throw new InvalidDataException($"Reviewed release references unknown provider '{providerId}'.");
        }
        var channelId = ReadString(element, "channelId");
        if (!provider.ReleaseChannels.TryGetValue(channelId, out var channel))
        {
            throw new InvalidDataException($"Reviewed release references unknown channel '{providerId}/{channelId}'.");
        }
        var runtimeDistributionId = ReadString(element, "runtimeDistributionId");
        var repository = ReadString(element, "repository");
        var assetName = ReadFileName(element, "assetName");
        var payloadFileName = ReadFileName(element, "payloadFileName");
        var tag = ReadString(element, "tag");
        var releaseVersion = ReadString(element, "releaseVersion");
        var sourceCommit = ReadString(element, "sourceCommit");
        var observedAt = ReadString(element, "observedAtUtc");
        if (!string.Equals(runtimeDistributionId, provider.RuntimeDistributionId, StringComparison.Ordinal)
            || !string.Equals(repository, channel.Repository, StringComparison.Ordinal)
            || (channel.DiscoveryKind == LauncherProviderReleaseDiscoveryKind.GitHubReleaseAsset
                && !string.Equals(assetName, channel.ArtifactAssetName, StringComparison.Ordinal))
            || !string.Equals(tag, $"v{releaseVersion}", StringComparison.Ordinal)
            || sourceCommit.Length != 40 || !sourceCommit.All(Uri.IsHexDigit)
            || !DateTimeOffset.TryParseExact(observedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var observedAtUtc)
            || observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"Reviewed release certification for '{providerId}/{channelId}' is inconsistent.");
        }
        var assetSize = ReadPositiveInt64(element, "assetSize");
        var payloadSize = ReadPositiveInt64(element, "payloadSize");
        if (assetSize > MaximumArtifactSize || payloadSize > MaximumArtifactSize)
        {
            throw new InvalidDataException("Reviewed release artifacts exceed the 128 MiB safety limit.");
        }
        var assetSha256 = ReadSha256(element, "assetSha256");
        var payloadSha256 = ReadSha256(element, "payloadSha256");
        ReviewedRuntimeManifestCertification? runtimeManifest = null;
        if (element.TryGetProperty("runtimeManifest", out var runtimeManifestElement))
        {
            RequireObject(runtimeManifestElement, "reviewed runtime manifest certification");
            RejectUnknown(
                runtimeManifestElement,
                RuntimeManifestProperties,
                "reviewed runtime manifest certification");
            var runtimeManifestSize = ReadPositiveInt64(runtimeManifestElement, "size");
            if (runtimeManifestSize > ArtifactBoundRuntimeManifestParser.MaximumManifestBytes)
            {
                throw new InvalidDataException("Reviewed runtime manifest exceeds its bounded safety limit.");
            }
            runtimeManifest = new(
                ReadFileName(runtimeManifestElement, "fileName"),
                runtimeManifestSize,
                ReadSha256(runtimeManifestElement, "sha256"));
            if (runtimeManifest.FileName != ArtifactBoundRuntimeManifestParser.ManagedFileName)
            {
                throw new InvalidDataException("Reviewed runtime manifest has an unsupported file name.");
            }
        }
        return new(
            providerId, channelId, runtimeDistributionId, repository, tag, releaseVersion,
            sourceCommit.ToLowerInvariant(), assetName, assetSize, assetSha256, payloadFileName,
            payloadSize, payloadSha256, ReadString(element, "payloadVersion"), observedAtUtc,
            runtimeManifest);
    }

    private static JsonElement RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{context} must be an object.");
        }
        return element;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"Reviewed release property '{propertyName}' must be a non-empty string.");
        }
        return property.GetString()!;
    }

    private static string ReadFileName(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
            ? value
            : throw new InvalidDataException($"Reviewed release property '{propertyName}' must be a file name.");
    }

    private static long ReadPositiveInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || !property.TryGetInt64(out var value) || value <= 0)
        {
            throw new InvalidDataException($"Reviewed release property '{propertyName}' must be a positive integer.");
        }
        return value;
    }

    private static string ReadSha256(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return value.Length == 64 && value.All(Uri.IsHexDigit)
            ? value.ToUpperInvariant()
            : throw new InvalidDataException($"Reviewed release property '{propertyName}' must be a SHA-256 digest.");
    }

    private static void RejectUnknown(JsonElement element, HashSet<string> allowed, string context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException($"{context} contains duplicate property '{property.Name}'.");
            }
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException($"{context} contains unknown property '{property.Name}'.");
            }
        }
    }
}

public sealed class ReviewedGitHubReleaseAssetClient(
    HttpClient httpClient,
    ReviewedReleaseCertification certification) : IWindowsReleaseDiscoveryClient
{
    private const int MaximumResponseBytes = 1024 * 1024;

    public async Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
        string channel,
        Version currentLauncherVersion,
        CancellationToken cancellationToken = default)
    {
        _ = currentLauncherVersion;
        if (!string.Equals(channel, certification.ChannelId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Requested release channel does not match the reviewed certification.");
        }
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{certification.Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("STFC-Mod-Bridge/0.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"GitHub latest release request returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("GitHub latest release response is too large.");
        }
        var bytes = await ReadBoundedAsync(response.Content, cancellationToken);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        var tag = ReadString(root, "tag_name");
        if (!string.Equals(tag, certification.Tag, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The latest {certification.ProviderId} release '{tag}' is not yet in the launcher-reviewed allowlist; installation fails closed.");
        }
        if (ReadBoolean(root, "draft") || ReadBoolean(root, "prerelease"))
        {
            throw new InvalidDataException("The reviewed stable release is unexpectedly draft or prerelease.");
        }
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub latest release has no asset list.");
        }
        var matches = assets.EnumerateArray()
            .Where(asset => string.Equals(ReadString(asset, "name"), certification.AssetName, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("GitHub latest release does not contain exactly one reviewed Windows asset.");
        }
        var selected = matches[0];
        var size = ReadInt64(selected, "size");
        var digest = ReadString(selected, "digest");
        var downloadUri = new Uri(ReadString(selected, "browser_download_url"));
        if (size != certification.AssetSize
            || !string.Equals(digest, $"sha256:{certification.AssetSha256}", StringComparison.OrdinalIgnoreCase)
            || downloadUri != certification.DownloadUri)
        {
            throw new InvalidDataException("GitHub latest release asset does not match the reviewed certification.");
        }
        ModRuntimeManifestArtifact? runtimeManifest = null;
        if (certification.RuntimeManifest is not null)
        {
            var manifestMatches = assets.EnumerateArray()
                .Where(asset => string.Equals(
                    ReadString(asset, "name"),
                    certification.RuntimeManifest.FileName,
                    StringComparison.Ordinal))
                .ToArray();
            if (manifestMatches.Length != 1)
            {
                throw new InvalidDataException(
                    "GitHub latest release does not contain exactly one reviewed runtime-manifest asset.");
            }
            var manifestAsset = manifestMatches[0];
            var manifestUri = RuntimeManifestUri(certification);
            if (ReadInt64(manifestAsset, "size") != certification.RuntimeManifest.Size
                || !string.Equals(
                    ReadString(manifestAsset, "digest"),
                    $"sha256:{certification.RuntimeManifest.Sha256}",
                    StringComparison.OrdinalIgnoreCase)
                || new Uri(ReadString(manifestAsset, "browser_download_url")) != manifestUri)
            {
                throw new InvalidDataException(
                    "GitHub latest release runtime manifest does not match the reviewed certification.");
            }
            runtimeManifest = new(
                manifestUri,
                certification.RuntimeManifest.FileName,
                certification.RuntimeManifest.Size,
                certification.RuntimeManifest.Sha256,
                certification.SourceCommit,
                certification.Repository,
                certification.Tag);
        }
        var manifest = new WindowsReleaseManifest(
            1,
            certification.ReleaseVersion,
            certification.Tag,
            certification.ChannelId,
            "active",
            new Version(0, 1, 0),
            new(certification.Repository, certification.SourceCommit),
            "launcher-reviewed-exact-hash",
            []);
        return new(
            manifest,
            new(
                certification.DownloadUri,
                certification.PayloadFileName,
                certification.PayloadSize,
                certification.PayloadSha256,
                certification.PayloadVersion,
                runtimeManifest));
    }

    internal static Uri RuntimeManifestUri(ReviewedReleaseCertification certification) =>
        new(
            $"https://github.com/{certification.Repository}/releases/download/"
            + $"{Uri.EscapeDataString(certification.Tag)}/"
            + Uri.EscapeDataString(certification.RuntimeManifest!.FileName));

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                return destination.ToArray();
            }
            if (destination.Length + count > MaximumResponseBytes)
            {
                throw new InvalidDataException("GitHub latest release response is too large.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new InvalidDataException($"GitHub release property '{name}' is missing or invalid.");

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"GitHub release property '{name}' is missing or invalid.");

    private static long ReadInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result)
            ? result
            : throw new InvalidDataException($"GitHub release property '{name}' is missing or invalid.");
}

public sealed class ReviewedZipModArtifactDownloader(
    HttpClient httpClient,
    ReviewedReleaseCertification certification) : IModArtifactDownloader
{
    private readonly HttpModArtifactDownloader downloader = new(httpClient);
    private readonly HttpModArtifactDownloader runtimeManifestDownloader = new(
        httpClient,
        ArtifactBoundRuntimeManifestParser.MaximumManifestBytes);

    public async Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (certification.RuntimeManifest is not null
            && uri == ReviewedGitHubReleaseAssetClient.RuntimeManifestUri(certification))
        {
            var manifest = await runtimeManifestDownloader.DownloadAsync(uri, cancellationToken);
            if (manifest.StatusCode == HttpStatusCode.OK
                && (manifest.DeclaredContentLength is not null
                        && manifest.DeclaredContentLength != certification.RuntimeManifest.Size
                    || manifest.Contents.LongLength != certification.RuntimeManifest.Size
                    || !CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(manifest.Contents),
                        Convert.FromHexString(certification.RuntimeManifest.Sha256))))
            {
                throw new InvalidDataException(
                    "Downloaded runtime manifest does not match the reviewed release certification.");
            }
            return manifest;
        }
        if (uri != certification.DownloadUri)
        {
            throw new InvalidDataException("Download URI is outside the reviewed release certification.");
        }
        var archiveDownload = await downloader.DownloadAsync(uri, cancellationToken);
        if (archiveDownload.StatusCode != HttpStatusCode.OK)
        {
            return archiveDownload;
        }
        if (archiveDownload.Contents.LongLength != certification.AssetSize
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(archiveDownload.Contents),
                Convert.FromHexString(certification.AssetSha256)))
        {
            throw new InvalidDataException("Downloaded archive does not match the reviewed release certification.");
        }
        using var archive = new ZipArchive(
            new MemoryStream(archiveDownload.Contents, writable: false),
            ZipArchiveMode.Read);
        var expectedFileNames = certification.RuntimeManifest is null
            ? new HashSet<string>([certification.PayloadFileName], StringComparer.Ordinal)
            : new HashSet<string>(
                [certification.PayloadFileName, certification.RuntimeManifest.FileName],
                StringComparer.Ordinal);
        var files = archive.Entries.ToArray();
        if (files.Length != expectedFileNames.Count
            || files.Any(entry =>
                string.IsNullOrEmpty(entry.Name)
                || !string.Equals(entry.FullName, entry.Name, StringComparison.Ordinal))
            || !expectedFileNames.SetEquals(files.Select(entry => entry.FullName)))
        {
            throw new InvalidDataException(
                "Reviewed release archive does not contain exactly the certified root payloads.");
        }
        var payloadEntry = files.Single(entry =>
            string.Equals(entry.FullName, certification.PayloadFileName, StringComparison.Ordinal));
        var payload = await ReadCertifiedEntryAsync(
            payloadEntry,
            certification.PayloadSize,
            certification.PayloadSha256,
            "DLL",
            cancellationToken).ConfigureAwait(false);
        if (certification.RuntimeManifest is not null)
        {
            var runtimeManifestEntry = files.Single(entry => string.Equals(
                entry.FullName,
                certification.RuntimeManifest.FileName,
                StringComparison.Ordinal));
            _ = await ReadCertifiedEntryAsync(
                runtimeManifestEntry,
                certification.RuntimeManifest.Size,
                certification.RuntimeManifest.Sha256,
                "runtime manifest",
                cancellationToken).ConfigureAwait(false);
        }
        return new(HttpStatusCode.OK, payload, payload.LongLength);
    }

    private static async Task<byte[]> ReadCertifiedEntryAsync(
        ZipArchiveEntry entry,
        long expectedSize,
        string expectedSha256,
        string description,
        CancellationToken cancellationToken)
    {
        if (entry.Length != expectedSize || expectedSize > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Reviewed release archive {description} does not match its certified size.");
        }
        await using var source = entry.Open();
        using var destination = new MemoryStream((int)expectedSize);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        var contents = destination.ToArray();
        if (contents.LongLength != expectedSize
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(contents),
                Convert.FromHexString(expectedSha256)))
        {
            throw new InvalidDataException(
                $"Reviewed release archive {description} does not match its certified bytes.");
        }
        return contents;
    }
}

public sealed class ManifestWithReviewedFallbackReleaseClient(
    IWindowsReleaseDiscoveryClient manifestClient,
    IWindowsReleaseDiscoveryClient reviewedFallback,
    ReviewedReleaseCertification? reviewedCertification = null) : IWindowsReleaseDiscoveryClient
{
    public async Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
        string channel,
        Version currentLauncherVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var discovery = await manifestClient.DiscoverLatestAsync(
                channel,
                currentLauncherVersion,
                cancellationToken).ConfigureAwait(false);
            if (reviewedCertification?.RuntimeManifest is null
                || !WindowsReleaseSelectionPolicy.MatchesReviewedReleaseArtifact(
                    discovery.Manifest,
                    discovery.ModArtifact,
                    reviewedCertification))
            {
                return discovery;
            }
            var runtimeManifest = WindowsReleaseSelectionPolicy.SelectReviewedRuntimeManifestArtifact(
                discovery.Manifest,
                discovery.ModArtifact,
                reviewedCertification);
            return discovery with
            {
                ModArtifact = discovery.ModArtifact with
                {
                    ExpectedVersion = reviewedCertification.PayloadVersion,
                    RuntimeManifest = runtimeManifest,
                },
            };
        }
        catch (InvalidDataException exception) when (
            ReleaseManifestFallbackPolicy.IsMissingManifest(exception))
        {
            var fallback = await reviewedFallback.DiscoverLatestAsync(
                channel,
                currentLauncherVersion,
                cancellationToken).ConfigureAwait(false);
            return reviewedCertification is null
                ? fallback
                : fallback with
                {
                    ModArtifact = fallback.ModArtifact with
                    {
                        ExpectedProductVersion = reviewedCertification.Tag,
                    },
                };
        }
    }
}

public sealed class ManifestWithReviewedFallbackArtifactDownloader : IModArtifactDownloader
{
    private readonly ReviewedReleaseCertification certification;
    private readonly HttpModArtifactDownloader dllDownloader;
    private readonly HttpModArtifactDownloader runtimeManifestDownloader;
    private readonly ReviewedZipModArtifactDownloader reviewedDownloader;

    public ManifestWithReviewedFallbackArtifactDownloader(
        HttpClient httpClient,
        ReviewedReleaseCertification certification)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.certification = certification ?? throw new ArgumentNullException(nameof(certification));
        dllDownloader = new(httpClient);
        runtimeManifestDownloader = new(
            httpClient,
            ArtifactBoundRuntimeManifestParser.MaximumManifestBytes);
        reviewedDownloader = new(httpClient, certification);
    }

    public Task<ModArtifactDownload> DownloadAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri == certification.DownloadUri)
        {
            return reviewedDownloader.DownloadAsync(uri, cancellationToken);
        }
        if (uri == RuntimeManifestUri())
        {
            return runtimeManifestDownloader.DownloadAsync(uri, cancellationToken);
        }
        if (IsRepositoryDllUri(uri))
        {
            return dllDownloader.DownloadAsync(uri, cancellationToken);
        }
        throw new InvalidDataException("Artifact URI is outside the reviewed repository release boundary.");
    }

    private Uri? RuntimeManifestUri() => certification.RuntimeManifest is null
        ? null
        : new Uri(
            $"https://github.com/{certification.Repository}/releases/download/"
            + $"{Uri.EscapeDataString(certification.Tag)}/"
            + Uri.EscapeDataString(certification.RuntimeManifest.FileName));

    private bool IsRepositoryDllUri(Uri uri)
    {
        var repository = certification.Repository.Split('/');
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && segments.Length == 6
            && string.Equals(segments[0], repository[0], StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[1], repository[1], StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[2], "releases", StringComparison.Ordinal)
            && string.Equals(segments[3], "download", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(segments[4])
            && string.Equals(segments[5], "version.dll", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ReviewedExactHashAuthenticityVerifier(
    ReviewedReleaseCertification certification) : IModArtifactAuthenticityVerifier
{
    public ModArtifactAuthenticityResult Verify(string artifactPath)
    {
        if (!File.Exists(artifactPath))
        {
            return new(false, "The reviewed artifact does not exist.");
        }
        using var stream = new FileStream(
            CandidateFileNative.OpenSharedExactReadNoFollow(artifactPath),
            FileAccess.Read);
        var sha256 = Convert.ToHexString(SHA256.HashData(stream));
        return stream.Length == certification.PayloadSize
            && string.Equals(sha256, certification.PayloadSha256, StringComparison.OrdinalIgnoreCase)
                ? new(true, "The DLL matches the launcher-reviewed exact SHA-256 allowlist.")
                : new(false, "The DLL does not match the launcher-reviewed exact SHA-256 allowlist.");
    }
}
