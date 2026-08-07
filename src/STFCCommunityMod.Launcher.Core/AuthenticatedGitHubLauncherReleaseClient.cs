using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core;

public static class AuthenticatedLauncherReleaseDiscovery
{
    public static ILauncherReleaseDiscoveryClient Create(
        HttpClient httpClient,
        string stateDirectory,
        string programDirectory,
        string expectedReleaseVerifierSha256)
    {
        var authenticityVerifier = new WindowsAuthenticodeVerifier(
            LauncherSelfUpdateAuthority.WindowsArtifactPublisher,
            LauncherSelfUpdateAuthority.WindowsArtifactSigningIdentityEku);
        return new AuthenticatedGitHubLauncherReleaseClient(
            httpClient,
            stateDirectory,
            new InstalledReleaseSelectionEvidenceVerifier(
                Path.Combine(programDirectory, ModBridgeProductIdentity.ReleaseVerifierExecutableName),
                expectedReleaseVerifierSha256,
                authenticityVerifier,
                TimeSpan.FromMinutes(2)),
            new WindowsCurrentUserReleaseEvidenceStorageSecurity());
    }
}

public sealed record AuthenticatedLauncherReleaseEvidence(
    string EvidenceDirectory,
    string ManifestPath,
    string BundlePath,
    string InstalledReleaseVersion,
    ReleaseSelectionVerificationReceipt Receipt,
    AuthenticatedReleaseAcceptance Acceptance)
{
    public string Summary =>
        $"Integrity: manifest and bundle digests verified. Producer origin: {Receipt.Repository}/"
        + $"{Receipt.Workflow} at {Receipt.SourceCommit}. Freshness: sequence "
        + $"{Acceptance.Manifest.ReleaseSequence}, valid through "
        + $"{Acceptance.Manifest.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC; no matching authenticated withdrawal. "
        + "Verification used the installed offline trust root; learning a later withdrawal requires a newer manifest. "
        + "Runtime lock: archive SHA-256, Authenticode, embedded source identity, and transactional replacement "
        + "must still pass. This evidence proves origin and byte integrity, not software safety.";
}

internal interface IReleaseSelectionEvidenceVerifier
{
    Task<ReleaseSelectionVerificationReceipt> VerifyAsync(
        ReleaseSelectionVerificationRequest request,
        CancellationToken cancellationToken);
}

internal interface IReleaseEvidenceStorageSecurity
{
    void SecureDirectory(string directory);
}

internal sealed class WindowsCurrentUserReleaseEvidenceStorageSecurity : IReleaseEvidenceStorageSecurity
{
    private readonly WindowsCurrentUserConfigurationBackupStorageSecurity storageSecurity = new();

    public void SecureDirectory(string directory) => storageSecurity.SecureDirectory(directory);
}

internal sealed class InstalledReleaseSelectionEvidenceVerifier : IReleaseSelectionEvidenceVerifier
{
    private const long MaximumHelperBytes = 64L * 1024L * 1024L;
    private readonly string helperPath;
    private readonly string expectedSha256;
    private readonly IModArtifactAuthenticityVerifier authenticityVerifier;
    private readonly TimeSpan timeout;

    internal InstalledReleaseSelectionEvidenceVerifier(
        string helperPath,
        string expectedSha256,
        IModArtifactAuthenticityVerifier authenticityVerifier,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentNullException.ThrowIfNull(authenticityVerifier);
        if (!Path.IsPathFullyQualified(helperPath)
            || Path.GetFileName(helperPath) != ModBridgeProductIdentity.ReleaseVerifierExecutableName
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(expectedSha256)
            || timeout <= TimeSpan.Zero
            || timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentException("The installed release-verifier identity is invalid.");
        }
        this.helperPath = Path.GetFullPath(helperPath);
        this.expectedSha256 = expectedSha256;
        this.authenticityVerifier = authenticityVerifier;
        this.timeout = timeout;
    }

    public async Task<ReleaseSelectionVerificationReceipt> VerifyAsync(
        ReleaseSelectionVerificationRequest request,
        CancellationToken cancellationToken)
    {
        await using var helperLock = new FileStream(
            helperPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (helperLock.Length is <= 0 or > MaximumHelperBytes
            || (File.GetAttributes(helperPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The installed release verifier is empty, oversized, or not a regular file.");
        }
        var expectedLength = helperLock.Length;
        var digest = await SHA256.HashDataAsync(helperLock, cancellationToken);
        if (helperLock.Position != expectedLength
            || helperLock.Length != expectedLength
            || !CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(expectedSha256)))
        {
            throw new InvalidDataException("The installed release verifier does not match the launcher-paired digest.");
        }
        var authenticity = authenticityVerifier.Verify(helperPath);
        if (!authenticity.IsTrusted)
        {
            throw new InvalidDataException(
                $"The installed release-verifier signature is not trusted: {authenticity.Message}");
        }
        return await ReleaseSelectionVerifierProcess.VerifyAsync(
            helperPath,
            request,
            timeout,
            cancellationToken);
    }
}

internal sealed partial class AuthenticatedGitHubLauncherReleaseClient : ILauncherReleaseDiscoveryClient
{
    private const int MaximumReleaseResponseBytes = 2 * 1024 * 1024;
    private const int MaximumReleaseCount = 30;
    private readonly HttpClient httpClient;
    private readonly string stateDirectory;
    private readonly IReleaseSelectionEvidenceVerifier verifier;
    private readonly IReleaseEvidenceStorageSecurity storageSecurity;
    private readonly AuthenticatedReleaseStateStore stateStore;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Uri releasesUri = new(
        $"https://api.github.com/repos/{ReleaseSelectionAttestationPolicy.Repository}/releases?per_page=30");

    [GeneratedRegex("^v(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)(?:-rc\\.(?:[1-9][0-9]*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalTagPattern();

    internal AuthenticatedGitHubLauncherReleaseClient(
        HttpClient httpClient,
        string stateDirectory,
        IReleaseSelectionEvidenceVerifier verifier,
        IReleaseEvidenceStorageSecurity storageSecurity,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(storageSecurity);
        this.httpClient = httpClient;
        this.stateDirectory = Path.GetFullPath(stateDirectory);
        this.verifier = verifier;
        this.storageSecurity = storageSecurity;
        stateStore = new(this.stateDirectory);
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<LauncherReleaseDiscovery> DiscoverLatestAsync(
        string channel,
        Version currentLauncherVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentLauncherVersion);
        if (channel is not ("stable" or "preview"))
        {
            throw new ArgumentException("Release channel must be stable or preview.", nameof(channel));
        }

        var candidates = await DiscoverCandidatesAsync(channel, cancellationToken);
        var previous = stateStore.Load(channel);
        var installedReleaseVersion = ToReleaseVersion(currentLauncherVersion);
        var accepted = new List<(WindowsReleaseManifest Projection, LauncherReleaseArtifact Artifact,
            AuthenticatedLauncherReleaseEvidence Evidence)>();
        var failures = new List<Exception>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? evidenceDirectory = null;
            try
            {
                evidenceDirectory = CreateEvidenceDirectory();
                var manifestPath = Path.Combine(evidenceDirectory, ReleaseSelectionAttestationPolicy.ManifestName);
                var bundlePath = Path.Combine(evidenceDirectory, ReleaseSelectionAttestationPolicy.BundleName);
                await DownloadEvidenceAsync(candidate.ManifestUri, manifestPath, "release manifest", cancellationToken);
                await DownloadEvidenceAsync(candidate.BundleUri, bundlePath, "release-selection bundle", cancellationToken);

                var request = ReleaseSelectionAttestationPolicy.CreateRequest(
                    manifestPath,
                    bundlePath,
                    candidate.Tag);
                var receipt = await verifier.VerifyAsync(request, cancellationToken);
                var manifestBytes = await ReadAndBindEvidenceAsync(
                    manifestPath,
                    receipt.ManifestSha256,
                    "release manifest",
                    cancellationToken);
                _ = await ReadAndBindEvidenceAsync(
                    bundlePath,
                    receipt.BundleSha256,
                    "release-selection bundle",
                    cancellationToken);
                var manifest = AuthenticatedReleaseManifestParser.Parse(manifestBytes);
                var acceptance = AuthenticatedReleaseManifestPolicy.Evaluate(
                    manifest,
                    receipt,
                    installedReleaseVersion,
                    utcNow(),
                    previous);
                if (manifest.Tag != candidate.Tag || manifest.Channel != channel)
                {
                    throw new InvalidDataException(
                        "Authenticated release evidence disagrees with the discovered tag or requested channel.");
                }

                var projection = Project(manifest);
                var artifact = WindowsReleaseSelectionPolicy.SelectLauncherArtifact(
                    projection,
                    channel,
                    currentLauncherVersion,
                    ReleaseSelectionAttestationPolicy.Repository);
                accepted.Add((
                    projection,
                    artifact,
                    new(
                        evidenceDirectory,
                        manifestPath,
                        bundlePath,
                        installedReleaseVersion,
                        receipt,
                        acceptance)));
                evidenceDirectory = null;
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                    or InvalidDataException
                    or IOException
                    or UnauthorizedAccessException)
            {
                failures.Add(exception);
            }
            finally
            {
                if (evidenceDirectory is not null)
                {
                    DeleteEvidenceDirectory(evidenceDirectory);
                }
            }
        }

        if (accepted.Count == 0)
        {
            throw new InvalidDataException(
                $"No authenticated {channel} Mod Bridge release could be established.",
                failures.FirstOrDefault());
        }
        try
        {
            var ordered = accepted
                .OrderBy(candidate => candidate.Evidence.Acceptance.Manifest.ReleaseSequence)
                .ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previousCandidate = ordered[index - 1].Evidence.Acceptance.Manifest;
                var currentCandidate = ordered[index].Evidence.Acceptance.Manifest;
                if (currentCandidate.ReleaseSequence == previousCandidate.ReleaseSequence
                    || AuthenticatedReleaseManifestPolicy.CompareReleaseVersions(
                        currentCandidate.ReleaseVersion,
                        previousCandidate.ReleaseVersion) <= 0
                    || currentCandidate.IssuedAt < previousCandidate.IssuedAt
                    || previousCandidate.Withdrawals.Any(withdrawal =>
                        !currentCandidate.Withdrawals.Contains(withdrawal)))
                {
                    throw new InvalidDataException(
                        "Authenticated candidate sequence, version, issue time, or withdrawal history disagree.");
                }
            }
            var selected = ordered[^1];
            foreach (var rejected in accepted.Where(candidate => !ReferenceEquals(candidate.Evidence, selected.Evidence)))
            {
                DeleteEvidenceDirectory(rejected.Evidence.EvidenceDirectory);
            }
            stateStore.Advance(selected.Evidence.Acceptance.State);
            return new(selected.Projection, selected.Artifact, selected.Evidence);
        }
        catch
        {
            foreach (var candidate in accepted)
            {
                DeleteEvidenceDirectory(candidate.Evidence.EvidenceDirectory);
            }
            throw;
        }
    }

    private async Task<IReadOnlyList<DiscoveryCandidate>> DiscoverCandidatesAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(releasesUri);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        ValidateResponseEndpoint(response, releasesUri, allowReleaseAssetRedirect: false);
        var bytes = await ReadBoundedResponseAsync(
            response,
            MaximumReleaseResponseBytes,
            "GitHub releases",
            cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() > MaximumReleaseCount)
            {
                throw new InvalidDataException("GitHub releases must be a bounded array.");
            }
            RejectDuplicateProperties(document.RootElement);
            var candidates = new List<DiscoveryCandidate>();
            var tags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object
                    || !TryReadBoolean(release, "draft", out var draft)
                    || !TryReadBoolean(release, "prerelease", out var prerelease)
                    || !TryReadString(release, "tag_name", out var tag)
                    || draft)
                {
                    continue;
                }
                if (!CanonicalTagPattern().IsMatch(tag))
                {
                    continue;
                }
                var tagChannel = tag.Contains("-rc.", StringComparison.Ordinal) ? "preview" : "stable";
                if ((prerelease ? "preview" : "stable") != tagChannel)
                {
                    continue;
                }
                if (tagChannel != channel)
                {
                    continue;
                }
                if (!tags.Add(tag))
                {
                    throw new InvalidDataException("GitHub release discovery returned a duplicate canonical tag.");
                }
                if (!HasExactlyOneAsset(release, ReleaseSelectionAttestationPolicy.ManifestName)
                    || !HasExactlyOneAsset(release, ReleaseSelectionAttestationPolicy.BundleName))
                {
                    continue;
                }
                var prefix = $"https://github.com/{ReleaseSelectionAttestationPolicy.Repository}/releases/download/"
                    + $"{Uri.EscapeDataString(tag)}/";
                candidates.Add(new(
                    tag,
                    new(prefix + ReleaseSelectionAttestationPolicy.ManifestName),
                    new(prefix + ReleaseSelectionAttestationPolicy.BundleName)));
            }
            return candidates.Count > 0
                ? candidates
                : throw new InvalidDataException(
                    $"No {channel} GitHub release advertises both fixed authenticated evidence assets.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("GitHub releases are not valid bounded JSON.", exception);
        }
    }

    private async Task DownloadEvidenceAsync(
        Uri uri,
        string path,
        string context,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(uri);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        ValidateResponseEndpoint(response, uri, allowReleaseAssetRedirect: true);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"{context} request returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
        if (response.Content.Headers.ContentLength > ReleaseSelectionAttestationPolicy.MaximumEvidenceBytes)
        {
            throw new InvalidDataException($"{context} exceeds the 1-MiB limit.");
        }
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }
            if (destination.Length + count > ReleaseSelectionAttestationPolicy.MaximumEvidenceBytes)
            {
                throw new InvalidDataException($"{context} exceeds the 1-MiB limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
        if (destination.Length == 0)
        {
            throw new InvalidDataException($"{context} is empty.");
        }
    }

    private static async Task<byte[]> ReadAndBindEvidenceAsync(
        string path,
        string expectedSha256,
        string context,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > ReleaseSelectionAttestationPolicy.MaximumEvidenceBytes)
        {
            throw new InvalidDataException($"{context} is outside the accepted size bound.");
        }
        var length = checked((int)stream.Length);
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.Length != bytes.Length
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(expectedSha256)
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes),
                Convert.FromHexString(expectedSha256)))
        {
            throw new InvalidDataException($"{context} changed or disagrees with the verifier receipt.");
        }
        return bytes;
    }

    private string CreateEvidenceDirectory()
    {
        var root = Path.Combine(stateDirectory, "release-authentication");
        storageSecurity.SecureDirectory(root);
        LauncherFilesystemSafety.RejectReparsePoints(root, "authenticated release evidence");
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        storageSecurity.SecureDirectory(directory);
        LauncherFilesystemSafety.RejectReparsePoints(directory, "authenticated release evidence");
        return directory;
    }

    private static void DeleteEvidenceDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        LauncherFilesystemSafety.RejectReparsePoints(directory, "authenticated release evidence cleanup");
        Directory.Delete(directory, recursive: true);
    }

    private static WindowsReleaseManifest Project(AuthenticatedWindowsReleaseManifest manifest) => new(
        manifest.SchemaVersion,
        manifest.ReleaseVersion,
        manifest.Tag,
        manifest.Channel,
        manifest.ReleaseState,
        manifest.MinimumLauncherVersion,
        manifest.Source,
        manifest.ManifestAuthenticityScheme,
        manifest.Artifacts);

    private static string ToReleaseVersion(Version version)
    {
        var core = $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        return version.Revision > 0 ? $"{core}-rc.{version.Revision}" : core;
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Authenticated release discovery permits HTTPS endpoints only.");
        }
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("STFC-Mod-Bridge/0.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static void ValidateResponseEndpoint(
        HttpResponseMessage response,
        Uri requestedUri,
        bool allowReleaseAssetRedirect)
    {
        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("Authenticated release response has no final request identity.");
        var allowedRedirect = allowReleaseAssetRedirect
            && finalUri.Scheme == Uri.UriSchemeHttps
            && finalUri.Host == "release-assets.githubusercontent.com";
        if (finalUri != requestedUri && !allowedRedirect)
        {
            throw new InvalidDataException("Authenticated release discovery followed an unsupported redirect.");
        }
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(
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
        var buffer = new byte[64 * 1024];
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

    private static bool HasExactlyOneAsset(JsonElement release, string expectedName)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        var count = 0;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind == JsonValueKind.Object
                && TryReadString(asset, "name", out var name)
                && name == expectedName)
            {
                count++;
            }
        }
        return count == 1;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"GitHub release discovery contains duplicate property '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static bool TryReadBoolean(JsonElement parent, string name, out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(name, out var element)
            || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = element.GetBoolean();
        return true;
    }

    private static bool TryReadString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.String
            || element.GetString() is not { Length: > 0 and <= 512 } result
            || string.IsNullOrWhiteSpace(result))
        {
            return false;
        }
        value = result;
        return true;
    }

    private sealed record DiscoveryCandidate(string Tag, Uri ManifestUri, Uri BundleUri);
}
