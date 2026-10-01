using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed record NetnivRepositoryReleaseObservation(
    long ReleaseId, long AssetId, string Tag, string SourceCommit, Uri DownloadUri,
    long ArchiveSize, string ArchiveSha256, long PayloadSize, string PayloadSha256,
    string FileVersion, DateTimeOffset ObservedAtUtc);

public interface IRecordedModReleaseDiscoveryClient
{
    Task<WindowsReleaseDiscovery> DiscoverRecordedAsync(
        ModInstalledArtifactState receipt, Version launcherVersion, CancellationToken cancellationToken);
}

/// <summary>
/// NetniV repository release authority is an explicit provider policy. GitHub's
/// HTTPS API selects the asset; the ZIP and inner DLL remain exact byte bindings.
/// This service never grants Bridge self-update or runtime-manifest authority.
/// </summary>
public sealed class NetnivRepositoryReleaseService : IWindowsReleaseDiscoveryClient,
    IRecordedModReleaseDiscoveryClient, IModArtifactDownloader, IModArtifactAuthenticityVerifier
{
    internal const string Repository = "netniV/stfc-mod";
    internal const long RepositoryId = 693298224;
    internal const long OwnerId = 9052188;
    internal const string AssetName = "stfc-community-mod.zip";
    internal const long MaximumBytes = 128L * 1024 * 1024;
    private readonly HttpClient httpClient;
    private readonly IModArtifactVersionReader versionReader;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<Uri, NetnivRepositoryReleaseObservation> observations = new();

    public NetnivRepositoryReleaseService(HttpClient httpClient,
        IModArtifactVersionReader? versionReader = null, TimeProvider? timeProvider = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.versionReader = versionReader ?? new WindowsModArtifactVersionReader("netniv.stfc-community-mod");
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<WindowsReleaseDiscovery> DiscoverLatestAsync(string channel,
        Version currentLauncherVersion, CancellationToken cancellationToken = default)
    {
        if (channel != "stable") throw new InvalidDataException("NetniV repository releases require the stable channel.");
        return DiscoverAsync("latest", currentLauncherVersion, null, cancellationToken);
    }

    public Task<WindowsReleaseDiscovery> DiscoverRecordedAsync(ModInstalledArtifactState receipt,
        Version launcherVersion, CancellationToken cancellationToken)
    {
        if (receipt.ProviderId != "netniv" || receipt.ReleaseChannelId != "stable"
            || receipt.RuntimeDistributionId != "netniv.stfc-community-mod")
            throw new InvalidDataException("The repair receipt does not belong to NetniV stable.");
        var tag = receipt.RepositoryRelease?.Tag ?? receipt.ReleaseProductVersion ?? "v" + receipt.Version;
        ValidateTag(tag);
        return DiscoverAsync("tags/" + Uri.EscapeDataString(tag), launcherVersion, receipt, cancellationToken);
    }

    private async Task<WindowsReleaseDiscovery> DiscoverAsync(string selector, Version launcherVersion,
        ModInstalledArtifactState? recorded, CancellationToken cancellationToken)
    {
        using var repository = await GetJsonAsync("", cancellationToken).ConfigureAwait(false);
        var repo = repository.RootElement;
        if (Number(repo, "id") != RepositoryId || Number(Property(repo, "owner", JsonValueKind.Object), "id") != OwnerId
            || !string.Equals(Text(repo, "full_name"), Repository, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NetniV repository ownership does not match the configured release authority.");
        using var metadata = await GetJsonAsync("/releases/" + selector, cancellationToken).ConfigureAwait(false);
        var release = metadata.RootElement;
        if (Flag(release, "draft") || Flag(release, "prerelease"))
            throw new InvalidDataException("NetniV stable cannot select a draft or prerelease.");
        var tag = Text(release, "tag_name");
        var version = ValidateTag(tag);
        if (recorded is not null && tag != (recorded.RepositoryRelease?.Tag ?? recorded.ReleaseProductVersion ?? "v" + recorded.Version))
            throw new InvalidDataException("The recorded release tag changed during repair discovery.");
        var expectedUri = AssetUri(tag);
        var matches = Property(release, "assets", JsonValueKind.Array).EnumerateArray()
            .Where(asset => Text(asset, "name") == AssetName).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The release must contain exactly one NetniV Windows ZIP.");
        var asset = matches[0];
        var assetSize = Number(asset, "size");
        var assetId = Number(asset, "id");
        var releaseId = Number(release, "id");
        var digest = Text(asset, "digest");
        if (assetId <= 0 || releaseId <= 0 || assetSize is <= 0 or > MaximumBytes
            || Text(asset, "state") != "uploaded" || !Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var assetUri) || assetUri != expectedUri
            || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsSha256(digest[7..]))
            throw new InvalidDataException("The NetniV release ZIP identity is missing or inconsistent.");
        var sourceCommit = await ResolveTagCommitAsync(tag, cancellationToken).ConfigureAwait(false);
        var bytes = await DownloadZipAsync(expectedUri, assetSize, digest[7..], cancellationToken).ConfigureAwait(false);
        var payload = ExtractPayload(bytes);
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var actualVersion = ReadPayloadVersion(payload);
        if (!string.Equals(actualVersion, version, StringComparison.Ordinal))
            throw new InvalidDataException("The NetniV release tag and embedded Windows DLL version differ.");
        var observation = new NetnivRepositoryReleaseObservation(releaseId, assetId, tag, sourceCommit,
            expectedUri, assetSize, digest[7..].ToLowerInvariant(), payload.LongLength, payloadHash,
            actualVersion, timeProvider.GetUtcNow());
        ValidateObservation(observation);
        if (recorded is not null && (recorded.Size != observation.PayloadSize
            || !string.Equals(recorded.Sha256, observation.PayloadSha256, StringComparison.OrdinalIgnoreCase)
            || recorded.Version != observation.FileVersion
            || recorded.RepositoryRelease is not null && !SameRelease(recorded.RepositoryRelease, observation)))
            throw new InvalidDataException("The recorded NetniV release no longer has its exact retained identity.");
        var retained = observations.GetOrAdd(expectedUri, observation);
        if (!SameRelease(retained, observation))
            throw new InvalidDataException("A previously reviewed release asset changed. Its earlier preparation was preserved.");
        return new(new WindowsReleaseManifest(1, version, tag, "stable", "active", new Version(0, 1, 0),
            new(Repository, sourceCommit), "github-repository-release", []),
            new(expectedUri, "version.dll", retained.PayloadSize, retained.PayloadSha256, retained.FileVersion,
                RepositoryRelease: retained));
    }

    public async Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!observations.TryGetValue(uri, out var observation))
            throw new InvalidDataException("The download has no retained NetniV release observation; review the release first.");
        var zip = await DownloadZipAsync(uri, observation.ArchiveSize, observation.ArchiveSha256,
            cancellationToken).ConfigureAwait(false);
        var payload = ExtractPayload(zip);
        if (payload.LongLength != observation.PayloadSize || !string.Equals(
                Convert.ToHexString(SHA256.HashData(payload)), observation.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded DLL changed after release review.");
        return new(HttpStatusCode.OK, payload, payload.LongLength);
    }

    public ModArtifactAuthenticityResult Verify(string artifactPath)
    {
        using var stream = new FileStream(CandidateFileNative.OpenSharedExactReadNoFollow(artifactPath), FileAccess.Read);
        if (stream.Length is <= 0 or > MaximumBytes) return new(false, "The staged DLL exceeds its size bound.");
        var digest = Convert.ToHexString(SHA256.HashData(stream));
        var trusted = observations.Values.Any(observation => observation.PayloadSize == stream.Length
            && string.Equals(observation.PayloadSha256, digest, StringComparison.OrdinalIgnoreCase));
        return new(trusted, trusted ? "The DLL matches the exact observed NetniV repository release."
            : "The DLL has no verified repository-release observation.");
    }

    private async Task<JsonDocument> GetJsonAsync(string suffix, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Repository + suffix);
        request.Headers.UserAgent.ParseAdd("STFC-Mod-Bridge/0.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedAsync(response.Content, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException exception) { throw new InvalidDataException("GitHub release metadata is invalid JSON.", exception); }
    }

    private async Task<string> ResolveTagCommitAsync(string tag, CancellationToken cancellationToken)
    {
        using var reference = await GetJsonAsync("/git/ref/tags/" + Uri.EscapeDataString(tag), cancellationToken).ConfigureAwait(false);
        var current = Property(reference.RootElement, "object", JsonValueKind.Object).Clone();
        for (var depth = 0; depth < 4; depth++)
        {
            var sha = Text(current, "sha");
            if (sha.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new InvalidDataException("The release tag has no valid commit.");
            if (Text(current, "type") == "commit") return sha.ToLowerInvariant();
            if (Text(current, "type") != "tag") throw new InvalidDataException("The release tag does not resolve to a commit.");
            using var annotated = await GetJsonAsync("/git/tags/" + sha, cancellationToken).ConfigureAwait(false);
            current = Property(annotated.RootElement, "object", JsonValueKind.Object).Clone();
        }
        throw new InvalidDataException("The release tag exceeds the annotated-tag depth bound.");
    }

    private async Task<byte[]> DownloadZipAsync(Uri uri, long expectedSize, string expectedHash,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedAsync(response.Content, MaximumBytes, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength != expectedSize || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),
                expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded NetniV ZIP does not match its observed release size and SHA-256.");
        return bytes;
    }

    private static byte[] ExtractPayload(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        if (zip.Entries.Count != 1 || zip.Entries[0].FullName != "version.dll"
            || zip.Entries[0].Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("The NetniV Windows ZIP must contain only the bounded root version.dll.");
        var entry = zip.Entries[0];
        using var source = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer)) != 0)
        {
            if (output.Length + read > entry.Length || output.Length + read > MaximumBytes)
                throw new InvalidDataException("The ZIP payload exceeds its declared or permitted size.");
            output.Write(buffer, 0, read);
        }
        if (output.Length != entry.Length) throw new InvalidDataException("The ZIP payload is truncated.");
        return output.ToArray();
    }

    private string ReadPayloadVersion(byte[] payload)
    {
        var path = Path.Combine(Path.GetTempPath(), "bridge-netniv-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(payload);
                file.Flush();
            }
            using var retained = new FileStream(CandidateFileNative.OpenSharedExactReadNoFollow(path), FileAccess.Read);
            return versionReader.ReadVersion(path) ?? throw new InvalidDataException("The release DLL has no readable Windows version.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long maximum,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("The release response exceeds its bound.");
        using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("The release response exceeds its bound.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    internal static void ValidateObservation(NetnivRepositoryReleaseObservation observation)
    {
        ValidateTag(observation.Tag);
        if (observation.ReleaseId <= 0 || observation.AssetId <= 0 || observation.DownloadUri != AssetUri(observation.Tag)
            || observation.ArchiveSize is <= 0 or > MaximumBytes || observation.PayloadSize is <= 0 or > MaximumBytes
            || !IsSha256(observation.ArchiveSha256) || !IsSha256(observation.PayloadSha256)
            || observation.SourceCommit is not { Length: 40 } || !observation.SourceCommit.All(Uri.IsHexDigit)
            || observation.FileVersion != ValidateTag(observation.Tag) || observation.ObservedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("The persisted NetniV repository-release observation is invalid.");
    }
    internal static bool SameRelease(NetnivRepositoryReleaseObservation first, NetnivRepositoryReleaseObservation second) =>
        first with { ObservedAtUtc = second.ObservedAtUtc } == second;
    private static Uri AssetUri(string tag) => new("https://github.com/" + Repository + "/releases/download/" + tag + "/" + AssetName);
    private static string ValidateTag(string tag)
    {
        if (tag is not { Length: > 1 and <= 160 } || tag[0] != 'v'
            || !Version.TryParse(tag[1..], out var version) || version.Revision < 0 || "v" + version != tag)
            throw new InvalidDataException("The NetniV stable release tag must be a canonical four-part version.");
        return version.ToString();
    }
    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static JsonElement Property(JsonElement value, string name, JsonValueKind kind) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == kind
            ? item : throw new InvalidDataException("GitHub release metadata is missing or invalid: " + name + ".");
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item)
        && item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()) ? item.GetString()!
        : throw new InvalidDataException("GitHub release metadata is missing " + name + ".");
    private static long Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item)
        && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) ? number : throw new InvalidDataException("GitHub release metadata is missing " + name + ".");
    private static bool Flag(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item)
        && item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean()
        : throw new InvalidDataException("GitHub release metadata is missing " + name + ".");
}
