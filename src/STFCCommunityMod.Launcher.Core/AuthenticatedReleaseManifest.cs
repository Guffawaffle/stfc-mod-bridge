using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core;

public sealed record AuthenticatedReleaseWithdrawal(
    string Kind,
    string Value,
    DateTimeOffset WithdrawnAt,
    string Reason);

public sealed record AuthenticatedWindowsReleaseManifest(
    int SchemaVersion,
    long ReleaseSequence,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string ReleaseVersion,
    string Tag,
    string Channel,
    string ReleaseState,
    Version MinimumLauncherVersion,
    WindowsReleaseSource Source,
    string ManifestAuthenticityScheme,
    IReadOnlyList<WindowsReleaseArtifact> Artifacts,
    IReadOnlyList<AuthenticatedReleaseWithdrawal> Withdrawals);

public sealed record AuthenticatedReleaseChannelState(
    int SchemaVersion,
    string Channel,
    long HighestReleaseSequence,
    string HighestReleaseVersion,
    string ManifestSha256,
    string BundleSha256,
    string SourceCommit,
    string Tag,
    int TrustEpoch,
    string TrustedRootSha256,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset LastObservedUtc,
    string VerificationMode,
    IReadOnlyList<AuthenticatedReleaseWithdrawal> Withdrawals);

public sealed record AuthenticatedReleaseAcceptance(
    AuthenticatedWindowsReleaseManifest Manifest,
    AuthenticatedReleaseChannelState State,
    DateTimeOffset EffectiveObservationUtc);

public static partial class AuthenticatedReleaseManifestParser
{
    public const int MaximumManifestBytes = 1024 * 1024;
    private const int SupportedSchemaVersion = 2;
    private const int MaximumArtifacts = 16;
    private const int MaximumWithdrawals = 1024;
    private static readonly HashSet<string> RootProperties =
    [
        "schemaVersion",
        "releaseSequence",
        "issuedAt",
        "expiresAt",
        "releaseVersion",
        "tag",
        "channel",
        "releaseState",
        "minimumLauncherVersion",
        "source",
        "manifestAuthenticity",
        "artifacts",
        "withdrawals",
    ];
    private static readonly HashSet<string> SourceProperties = ["repository", "targetCommit"];
    private static readonly HashSet<string> ManifestAuthenticityProperties = ["scheme"];
    private static readonly HashSet<string> ArtifactProperties =
    [
        "id",
        "kind",
        "platform",
        "architecture",
        "fileName",
        "mediaType",
        "size",
        "sha256",
        "authenticity",
    ];
    private static readonly HashSet<string> ArtifactAuthenticityProperties = ["scheme", "scope", "signedFiles"];
    private static readonly HashSet<string> WithdrawalProperties = ["kind", "value", "withdrawnAt", "reason"];

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    public static AuthenticatedWindowsReleaseManifest Parse(Stream manifestStream)
    {
        ArgumentNullException.ThrowIfNull(manifestStream);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = manifestStream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > MaximumManifestBytes)
            {
                throw new InvalidDataException("The authenticated release manifest exceeds its 1-MiB limit.");
            }
            buffer.Write(chunk, 0, read);
        }
        return Parse(buffer.ToArray());
    }

    public static AuthenticatedWindowsReleaseManifest Parse(ReadOnlyMemory<byte> manifestBytes)
    {
        if (manifestBytes.IsEmpty || manifestBytes.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("The authenticated release manifest is empty or exceeds its 1-MiB limit.");
        }
        try
        {
            using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = RequireObject(document.RootElement, "authenticated release manifest");
            RejectUnknownOrDuplicate(root, RootProperties, "authenticated release manifest");
            var schemaVersion = ReadInt32(root, "schemaVersion", "authenticated release manifest");
            if (schemaVersion != SupportedSchemaVersion)
            {
                throw new InvalidDataException($"Authenticated release manifest schema {schemaVersion} is unsupported.");
            }

            var releaseSequence = ReadInt64(root, "releaseSequence", "authenticated release manifest");
            if (releaseSequence <= 0)
            {
                throw new InvalidDataException("releaseSequence must be a positive integer.");
            }
            var issuedAt = ReadTimestamp(root, "issuedAt", "authenticated release manifest");
            var expiresAt = ReadTimestamp(root, "expiresAt", "authenticated release manifest");
            var releaseVersion = ReadString(root, "releaseVersion", "authenticated release manifest");
            var tag = ReadString(root, "tag", "authenticated release manifest");
            if (tag != $"v{releaseVersion}")
            {
                throw new InvalidDataException("Authenticated release manifest tag and releaseVersion do not match.");
            }
            var channel = ReadString(root, "channel", "authenticated release manifest");
            if (channel is not ("stable" or "preview"))
            {
                throw new InvalidDataException($"Authenticated release channel '{channel}' is unsupported.");
            }
            var releaseState = ReadString(root, "releaseState", "authenticated release manifest");
            if (releaseState is not ("active" or "withdrawn"))
            {
                throw new InvalidDataException($"Authenticated release state '{releaseState}' is unsupported.");
            }
            var minimumLauncherText = ReadString(root, "minimumLauncherVersion", "authenticated release manifest");
            if (!Version.TryParse(minimumLauncherText, out var minimumLauncherVersion))
            {
                throw new InvalidDataException("minimumLauncherVersion is not a numeric version.");
            }

            var source = RequireObject(ReadProperty(root, "source", "authenticated release manifest"), "release source");
            RejectUnknownOrDuplicate(source, SourceProperties, "release source");
            var repository = ReadString(source, "repository", "release source");
            var targetCommit = ReadString(source, "targetCommit", "release source");
            if (!RepositoryPattern().IsMatch(repository) || !CommitPattern().IsMatch(targetCommit))
            {
                throw new InvalidDataException("Authenticated release source identity is invalid.");
            }

            var authenticity = RequireObject(
                ReadProperty(root, "manifestAuthenticity", "authenticated release manifest"),
                "manifest authenticity");
            RejectUnknownOrDuplicate(authenticity, ManifestAuthenticityProperties, "manifest authenticity");
            var authenticityScheme = ReadString(authenticity, "scheme", "manifest authenticity");

            var artifacts = ParseArray(root, "artifacts", 1, MaximumArtifacts, ParseArtifact);
            if (artifacts.Select(artifact => artifact.Id).Distinct(StringComparer.Ordinal).Count() != artifacts.Length
                || artifacts.Select(artifact => artifact.FileName).Distinct(StringComparer.Ordinal).Count() != artifacts.Length)
            {
                throw new InvalidDataException("Authenticated release artifact identities and file names must be unique.");
            }
            var withdrawals = ParseArray(root, "withdrawals", 0, MaximumWithdrawals, ParseWithdrawal);
            if (withdrawals
                .Select(withdrawal => $"{withdrawal.Kind}\0{withdrawal.Value}")
                .Distinct(StringComparer.Ordinal)
                .Count() != withdrawals.Length)
            {
                throw new InvalidDataException("Authenticated release withdrawal selectors must be unique.");
            }

            return new(
                schemaVersion,
                releaseSequence,
                issuedAt,
                expiresAt,
                releaseVersion,
                tag,
                channel,
                releaseState,
                minimumLauncherVersion,
                new(repository, targetCommit),
                authenticityScheme,
                artifacts,
                withdrawals);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The authenticated release manifest is not valid closed-schema JSON.", exception);
        }
    }

    private static WindowsReleaseArtifact ParseArtifact(JsonElement value)
    {
        var artifact = RequireObject(value, "authenticated release artifact");
        RejectUnknownOrDuplicate(artifact, ArtifactProperties, "authenticated release artifact");
        var id = ReadString(artifact, "id", "authenticated release artifact");
        var kind = ReadString(artifact, "kind", "authenticated release artifact");
        var platform = ReadString(artifact, "platform", "authenticated release artifact");
        var architecture = ReadString(artifact, "architecture", "authenticated release artifact");
        var fileName = ReadString(artifact, "fileName", "authenticated release artifact");
        var mediaType = ReadString(artifact, "mediaType", "authenticated release artifact");
        var size = ReadInt64(artifact, "size", "authenticated release artifact");
        var sha256 = ReadString(artifact, "sha256", "authenticated release artifact");
        if (Path.GetFileName(fileName) != fileName
            || fileName is "." or ".."
            || size <= 0
            || !Sha256Pattern().IsMatch(sha256))
        {
            throw new InvalidDataException($"Authenticated release artifact '{id}' has invalid file metadata.");
        }

        var authenticity = RequireObject(
            ReadProperty(artifact, "authenticity", "authenticated release artifact"),
            "artifact authenticity");
        RejectUnknownOrDuplicate(authenticity, ArtifactAuthenticityProperties, "artifact authenticity");
        var scheme = ReadString(authenticity, "scheme", "artifact authenticity");
        var scope = ReadString(authenticity, "scope", "artifact authenticity");
        var signedFiles = Array.Empty<string>();
        if (authenticity.TryGetProperty("signedFiles", out var signedFilesElement))
        {
            if (signedFilesElement.ValueKind != JsonValueKind.Array || signedFilesElement.GetArrayLength() > MaximumArtifacts)
            {
                throw new InvalidDataException("artifact authenticity signedFiles must be a bounded array.");
            }
            signedFiles = signedFilesElement.EnumerateArray().Select(element =>
            {
                if (element.ValueKind != JsonValueKind.String
                    || element.GetString() is not { Length: > 0 and <= 260 } signedFile
                    || string.IsNullOrWhiteSpace(signedFile)
                    || signedFile is "." or ".."
                    || Path.GetFileName(signedFile) != signedFile)
                {
                    throw new InvalidDataException("artifact authenticity signedFiles contains an invalid file name.");
                }
                return signedFile;
            }).ToArray();
            if (signedFiles.Distinct(StringComparer.Ordinal).Count() != signedFiles.Length)
            {
                throw new InvalidDataException("artifact authenticity signedFiles must be unique.");
            }
        }
        return new(id, kind, platform, architecture, fileName, mediaType, size, sha256, new(scheme, scope, signedFiles));
    }

    private static AuthenticatedReleaseWithdrawal ParseWithdrawal(JsonElement value)
    {
        var withdrawal = RequireObject(value, "authenticated release withdrawal");
        RejectUnknownOrDuplicate(withdrawal, WithdrawalProperties, "authenticated release withdrawal");
        var kind = ReadString(withdrawal, "kind", "authenticated release withdrawal");
        var selector = ReadString(withdrawal, "value", "authenticated release withdrawal");
        switch (kind)
        {
            case "release-sequence" when !long.TryParse(
                selector,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sequence) || sequence <= 0 || selector != sequence.ToString(CultureInfo.InvariantCulture):
                throw new InvalidDataException("A release-sequence withdrawal must contain a canonical positive integer value.");
            case "manifest-sha256" or "artifact-sha256" when !Sha256Pattern().IsMatch(selector):
                throw new InvalidDataException($"A {kind} withdrawal must contain a lowercase SHA-256 value.");
            case not ("release-sequence" or "manifest-sha256" or "artifact-sha256"):
                throw new InvalidDataException($"Authenticated release withdrawal kind '{kind}' is unsupported.");
        }
        var withdrawnAt = ReadTimestamp(withdrawal, "withdrawnAt", "authenticated release withdrawal");
        var reason = ReadString(withdrawal, "reason", "authenticated release withdrawal");
        if (reason is not ("security" or "integrity" or "operator-error" or "policy"))
        {
            throw new InvalidDataException($"Authenticated release withdrawal reason '{reason}' is unsupported.");
        }
        return new(kind, selector, withdrawnAt, reason);
    }

    private static T[] ParseArray<T>(
        JsonElement root,
        string name,
        int minimumCount,
        int maximumCount,
        Func<JsonElement, T> parse)
    {
        var value = ReadProperty(root, name, "authenticated release manifest");
        if (value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() < minimumCount
            || value.GetArrayLength() > maximumCount)
        {
            throw new InvalidDataException($"Authenticated release manifest {name} has an invalid item count.");
        }
        return value.EnumerateArray().Select(parse).ToArray();
    }

    private static DateTimeOffset ReadTimestamp(JsonElement parent, string name, string context)
    {
        var text = ReadString(parent, name, context);
        if (!DateTimeOffset.TryParseExact(
                text,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var timestamp)
            || timestamp.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"{context}.{name} must be whole-second UTC RFC 3339.");
        }
        return timestamp;
    }

    private static JsonElement RequireObject(JsonElement value, string context) =>
        value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidDataException($"{context} must be an object.");

    private static JsonElement ReadProperty(JsonElement parent, string name, string context) =>
        parent.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException($"{context} is missing '{name}'.");

    private static string ReadString(JsonElement parent, string name, string context)
    {
        var value = ReadProperty(parent, name, context);
        return value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 and <= 512 } result
            && !string.IsNullOrWhiteSpace(result)
                ? result
                : throw new InvalidDataException($"{context}.{name} must be a bounded non-empty string.");
    }

    private static int ReadInt32(JsonElement parent, string name, string context)
    {
        var value = ReadProperty(parent, name, context);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : throw new InvalidDataException($"{context}.{name} must be an integer.");
    }

    private static long ReadInt64(JsonElement parent, string name, string context)
    {
        var value = ReadProperty(parent, name, context);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result
            : throw new InvalidDataException($"{context}.{name} must be an integer.");
    }

    private static void RejectUnknownOrDuplicate(JsonElement element, HashSet<string> allowed, string context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException($"{context} contains unknown property '{property.Name}'.");
            }
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException($"{context} contains duplicate property '{property.Name}'.");
            }
        }
    }
}

public static partial class AuthenticatedReleaseManifestPolicy
{
    public const int StateSchemaVersion = 1;
    public const string AuthenticityScheme = "github-sigstore-build-provenance-v1";
    public static readonly TimeSpan StableValidity = TimeSpan.FromDays(45);
    public static readonly TimeSpan PreviewValidity = TimeSpan.FromDays(14);
    public static readonly TimeSpan MaximumSigningDelay = TimeSpan.FromHours(1);
    public static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaterialClockRollback = TimeSpan.FromHours(24);
    private const long MaximumArtifactBytes = 512L * 1024L * 1024L;

    [GeneratedRegex(
        "^(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)(?:-rc\\.(?<rc>[1-9][0-9]*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionPattern();

    public static AuthenticatedReleaseAcceptance Evaluate(
        AuthenticatedWindowsReleaseManifest manifest,
        ReleaseSelectionVerificationReceipt receipt,
        string installedReleaseVersion,
        DateTimeOffset localUtcNow,
        AuthenticatedReleaseChannelState? previous = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(installedReleaseVersion);
        if (localUtcNow.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The release policy clock must be supplied as UTC.", nameof(localUtcNow));
        }
        var candidateVersion = ParseVersion(manifest.ReleaseVersion);
        var installedVersion = ParseVersion(installedReleaseVersion);
        if (!receipt.Verified
            || receipt.RekorEntries.Count != 1
            || receipt.RekorEntries[0].IntegratedTime.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Authenticated release policy requires one verified UTC transparency entry.");
        }
        ValidateManifestIdentity(manifest, receipt, candidateVersion);
        ValidateArtifacts(manifest.Artifacts);
        ValidateWithdrawals(manifest);
        if (new Version(installedVersion.Major, installedVersion.Minor, installedVersion.Patch)
            < manifest.MinimumLauncherVersion)
        {
            throw new InvalidDataException("The installed Mod Bridge does not satisfy minimumLauncherVersion.");
        }

        var rekorTime = receipt.RekorEntries.Single().IntegratedTime;
        var validity = manifest.Channel == "stable" ? StableValidity : PreviewValidity;
        if (manifest.IssuedAt > rekorTime + AllowedClockSkew
            || rekorTime - manifest.IssuedAt > MaximumSigningDelay
            || manifest.ExpiresAt <= manifest.IssuedAt
            || manifest.ExpiresAt - manifest.IssuedAt > validity
            || rekorTime > manifest.ExpiresAt + AllowedClockSkew)
        {
            throw new InvalidDataException("The authenticated release timing or signing-delay policy is invalid.");
        }
        if (candidateVersion.CompareTo(installedVersion) <= 0)
        {
            throw new InvalidDataException("The authenticated release does not advance the installed Mod Bridge version.");
        }
        if (previous is not null)
        {
            ValidatePreviousState(previous, manifest, receipt, candidateVersion, localUtcNow);
        }

        var effectiveObservation = Max(localUtcNow, rekorTime, previous?.LastObservedUtc ?? DateTimeOffset.MinValue);
        if (effectiveObservation > manifest.ExpiresAt + AllowedClockSkew)
        {
            throw new InvalidDataException("The authenticated release manifest is expired or frozen.");
        }
        if (IsWithdrawn(manifest.Withdrawals, manifest.ReleaseSequence, receipt.ManifestSha256, manifest.Artifacts))
        {
            throw new InvalidDataException("The authenticated release withdraws its own selected identity.");
        }

        var sameRelease = previous is not null && previous.HighestReleaseSequence == manifest.ReleaseSequence;
        var state = new AuthenticatedReleaseChannelState(
            StateSchemaVersion,
            manifest.Channel,
            manifest.ReleaseSequence,
            manifest.ReleaseVersion,
            receipt.ManifestSha256,
            receipt.BundleSha256,
            manifest.Source.TargetCommit,
            manifest.Tag,
            receipt.TrustEpoch,
            receipt.TrustedRootSha256,
            sameRelease ? previous!.FirstObservedUtc : effectiveObservation,
            effectiveObservation,
            receipt.VerificationMode,
            manifest.Withdrawals);
        return new(manifest, state, effectiveObservation);
    }

    public static bool IsWithdrawn(
        IReadOnlyList<AuthenticatedReleaseWithdrawal> withdrawals,
        long releaseSequence,
        string manifestSha256,
        IEnumerable<WindowsReleaseArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(withdrawals);
        ArgumentNullException.ThrowIfNull(artifacts);
        var sequence = releaseSequence.ToString(CultureInfo.InvariantCulture);
        var artifactDigests = artifacts.Select(artifact => artifact.Sha256).ToArray();
        return withdrawals.Any(withdrawal =>
            (withdrawal.Kind == "release-sequence" && withdrawal.Value == sequence)
            || (withdrawal.Kind == "manifest-sha256" && FixedTimeHexEquals(withdrawal.Value, manifestSha256))
            || (withdrawal.Kind == "artifact-sha256"
                && artifactDigests.Any(digest => FixedTimeHexEquals(withdrawal.Value, digest))));
    }

    private static void ValidateManifestIdentity(
        AuthenticatedWindowsReleaseManifest manifest,
        ReleaseSelectionVerificationReceipt receipt,
        ParsedReleaseVersion version)
    {
        var expectedChannel = version.ReleaseCandidate is null ? "stable" : "preview";
        if (receipt.SchemaVersion != ReleaseSelectionAttestationPolicy.SchemaVersion
            || receipt.VerificationMode != ReleaseSelectionAttestationPolicy.VerificationMode
            || receipt.Repository != ReleaseSelectionAttestationPolicy.Repository
            || receipt.RepositoryId != ReleaseSelectionAttestationPolicy.RepositoryId
            || receipt.OwnerId != ReleaseSelectionAttestationPolicy.OwnerId
            || receipt.Workflow != ReleaseSelectionAttestationPolicy.Workflow
            || receipt.Event != ReleaseSelectionAttestationPolicy.Event
            || receipt.Runner != ReleaseSelectionAttestationPolicy.Runner
            || receipt.StatementType != ReleaseSelectionAttestationPolicy.StatementType
            || receipt.PredicateType != ReleaseSelectionAttestationPolicy.PredicateType
            || receipt.BuildType != ReleaseSelectionAttestationPolicy.BuildType
            || receipt.SubjectName != ReleaseSelectionAttestationPolicy.ManifestName
            || receipt.TrustEpoch != ReleaseSelectionAttestationPolicy.TrustEpoch
            || !FixedTimeHexEquals(
                receipt.TrustedRootSha256,
                ReleaseSelectionAttestationPolicy.TrustedRootSha256)
            || receipt.FulcioIssuer != ReleaseSelectionAttestationPolicy.FulcioIssuer
            || !ReleaseSelectionAttestationPolicy.AcceptedRekorLogIds.Contains(receipt.RekorEntries[0].LogId)
            || !receipt.Checks.SequenceEqual(ReleaseSelectionAttestationPolicy.RequiredChecks, StringComparer.Ordinal)
            || manifest.SchemaVersion != 2
            || manifest.ReleaseSequence <= 0
            || manifest.Channel != expectedChannel
            || manifest.ReleaseState != "active"
            || manifest.Tag != $"v{manifest.ReleaseVersion}"
            || manifest.Source.Repository != ReleaseSelectionAttestationPolicy.Repository
            || manifest.Source.TargetCommit != receipt.SourceCommit
            || manifest.ManifestAuthenticityScheme != AuthenticityScheme
            || receipt.SourceRef != $"refs/tags/{manifest.Tag}"
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(receipt.ManifestSha256)
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(receipt.BundleSha256))
        {
            throw new InvalidDataException("The authenticated release manifest disagrees with verified producer evidence.");
        }
    }

    private static void ValidateArtifacts(IReadOnlyList<WindowsReleaseArtifact> artifacts)
    {
        if (artifacts.Count != 2)
        {
            throw new InvalidDataException("Authenticated Mod Bridge releases require exactly two artifact roles.");
        }
        var archive = artifacts.SingleOrDefault(artifact => artifact.Id == "windows-mod-bridge-archive-x64");
        var package = artifacts.SingleOrDefault(artifact => artifact.Id == "windows-mod-bridge-msix-x64");
        if (archive is null
            || archive.Kind != "windows-mod-bridge"
            || archive.Platform != "windows"
            || archive.Architecture != "x64"
            || archive.FileName != ModBridgeProductIdentity.UpdateArchiveName
            || archive.MediaType != "application/zip"
            || archive.Size > MaximumArtifactBytes
            || archive.Authenticity.Scheme != "authenticode"
            || archive.Authenticity.Scope != "contents"
            || !archive.Authenticity.SignedFiles.SequenceEqual(
                [
                    ModBridgeProductIdentity.ExecutableName,
                    ModBridgeProductIdentity.ReleaseVerifierExecutableName,
                    ModBridgeProductIdentity.UpdaterExecutableName,
                ],
                StringComparer.Ordinal)
            || package is null
            || package.Kind != "windows-mod-bridge-package"
            || package.Platform != "windows"
            || package.Architecture != "x64"
            || package.FileName != "STFCModBridge.msix"
            || package.MediaType != "application/msix"
            || package.Size > MaximumArtifactBytes
            || package.Authenticity.Scheme != "authenticode"
            || package.Authenticity.Scope != "artifact"
            || package.Authenticity.SignedFiles.Count != 0)
        {
            throw new InvalidDataException("The authenticated Mod Bridge artifact contract is invalid.");
        }
    }

    private static void ValidateWithdrawals(AuthenticatedWindowsReleaseManifest manifest)
    {
        var ordered = manifest.Withdrawals
            .OrderBy(withdrawal => withdrawal.Kind, StringComparer.Ordinal)
            .ThenBy(withdrawal => withdrawal.Value, StringComparer.Ordinal)
            .ToArray();
        if (!manifest.Withdrawals.SequenceEqual(ordered))
        {
            throw new InvalidDataException("Authenticated release withdrawals must use canonical selector order.");
        }
        foreach (var withdrawal in manifest.Withdrawals)
        {
            ValidateWithdrawal(withdrawal);
            if (withdrawal.WithdrawnAt > manifest.IssuedAt + AllowedClockSkew)
            {
                throw new InvalidDataException("A withdrawal cannot postdate the manifest that publishes it.");
            }
        }
    }

    private static void ValidatePreviousState(
        AuthenticatedReleaseChannelState previous,
        AuthenticatedWindowsReleaseManifest manifest,
        ReleaseSelectionVerificationReceipt receipt,
        ParsedReleaseVersion candidateVersion,
        DateTimeOffset localUtcNow)
    {
        ValidateState(previous);
        if (previous.Channel != manifest.Channel)
        {
            throw new InvalidDataException("Authenticated release state is scoped to a different channel.");
        }
        if (localUtcNow + MaterialClockRollback < previous.LastObservedUtc)
        {
            throw new InvalidDataException("The local clock materially precedes the authenticated observation floor.");
        }
        if (manifest.ReleaseSequence < previous.HighestReleaseSequence
            || receipt.TrustEpoch < previous.TrustEpoch)
        {
            throw new InvalidDataException("The authenticated release sequence or trust epoch is a rollback.");
        }
        if (receipt.TrustEpoch == previous.TrustEpoch
            && !FixedTimeHexEquals(receipt.TrustedRootSha256, previous.TrustedRootSha256))
        {
            throw new InvalidDataException("The authenticated trust-root digest changed without an epoch advance.");
        }
        var previousVersion = ParseVersion(previous.HighestReleaseVersion);
        if (candidateVersion.CompareTo(previousVersion) < 0)
        {
            throw new InvalidDataException("The authenticated release version is below the persisted channel floor.");
        }
        if (manifest.ReleaseSequence == previous.HighestReleaseSequence)
        {
            if (candidateVersion.CompareTo(previousVersion) != 0
                || manifest.Tag != previous.Tag
                || manifest.Source.TargetCommit != previous.SourceCommit
                || !FixedTimeHexEquals(receipt.ManifestSha256, previous.ManifestSha256)
                || !FixedTimeHexEquals(receipt.BundleSha256, previous.BundleSha256)
                || receipt.TrustEpoch != previous.TrustEpoch
                || !FixedTimeHexEquals(receipt.TrustedRootSha256, previous.TrustedRootSha256)
                || !manifest.Withdrawals.SequenceEqual(previous.Withdrawals))
            {
                throw new InvalidDataException("An accepted release sequence was replayed with different evidence.");
            }
        }
        else if (candidateVersion.CompareTo(previousVersion) <= 0)
        {
            throw new InvalidDataException("A higher release sequence must also advance the release version.");
        }
        foreach (var priorWithdrawal in previous.Withdrawals)
        {
            if (!manifest.Withdrawals.Contains(priorWithdrawal))
            {
                throw new InvalidDataException("Authenticated withdrawal entries are additive and cannot disappear.");
            }
        }
    }

    internal static void ValidateState(AuthenticatedReleaseChannelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != StateSchemaVersion
            || state.Channel is not ("stable" or "preview")
            || state.HighestReleaseSequence <= 0
            || state.Tag != $"v{state.HighestReleaseVersion}"
            || !ReleaseSelectionAttestationPolicy.CommitPattern().IsMatch(state.SourceCommit)
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(state.ManifestSha256)
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(state.BundleSha256)
            || state.TrustEpoch <= 0
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(state.TrustedRootSha256)
            || state.FirstObservedUtc.Offset != TimeSpan.Zero
            || state.LastObservedUtc.Offset != TimeSpan.Zero
            || state.LastObservedUtc < state.FirstObservedUtc
            || state.VerificationMode != ReleaseSelectionAttestationPolicy.VerificationMode
            || state.Withdrawals is null)
        {
            throw new InvalidDataException("The authenticated release channel state is invalid.");
        }
        var stateVersion = ParseVersion(state.HighestReleaseVersion);
        if ((stateVersion.ReleaseCandidate is null ? "stable" : "preview") != state.Channel)
        {
            throw new InvalidDataException("The authenticated release state channel and version disagree.");
        }
        var ordered = state.Withdrawals
            .OrderBy(withdrawal => withdrawal.Kind, StringComparer.Ordinal)
            .ThenBy(withdrawal => withdrawal.Value, StringComparer.Ordinal)
            .ToArray();
        if (!state.Withdrawals.SequenceEqual(ordered))
        {
            throw new InvalidDataException("Authenticated release state withdrawals are not canonically ordered.");
        }
        var selectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var withdrawal in state.Withdrawals)
        {
            ValidateWithdrawal(withdrawal);
            if (!selectors.Add($"{withdrawal.Kind}\0{withdrawal.Value}"))
            {
                throw new InvalidDataException("Authenticated release state contains duplicate withdrawal selectors.");
            }
        }
    }

    private static ParsedReleaseVersion ParseVersion(string value)
    {
        var match = ReleaseVersionPattern().Match(value);
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            throw new InvalidDataException($"Release version '{value}' is not canonical Mod Bridge SemVer.");
        }
        int? releaseCandidate = null;
        if (match.Groups["rc"].Success)
        {
            if (!int.TryParse(match.Groups["rc"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var rc)
                || rc is < 1 or > 65534)
            {
                throw new InvalidDataException("Release-candidate numbers must be between 1 and 65534.");
            }
            releaseCandidate = rc;
        }
        return new(major, minor, patch, releaseCandidate);
    }

    internal static int CompareReleaseVersions(string left, string right) =>
        ParseVersion(left).CompareTo(ParseVersion(right));

    internal static bool FixedTimeDigestEquals(string left, string right) => FixedTimeHexEquals(left, right);

    private static bool FixedTimeHexEquals(string left, string right)
    {
        if (!ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(left)
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(right))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    }

    private static void ValidateWithdrawal(AuthenticatedReleaseWithdrawal withdrawal)
    {
        ArgumentNullException.ThrowIfNull(withdrawal);
        var valueIsValid = withdrawal.Kind switch
        {
            "release-sequence" => long.TryParse(
                withdrawal.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sequence)
                && sequence > 0
                && withdrawal.Value == sequence.ToString(CultureInfo.InvariantCulture),
            "manifest-sha256" or "artifact-sha256" =>
                ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(withdrawal.Value),
            _ => false,
        };
        if (!valueIsValid
            || withdrawal.WithdrawnAt.Offset != TimeSpan.Zero
            || withdrawal.Reason is not ("security" or "integrity" or "operator-error" or "policy"))
        {
            throw new InvalidDataException("Authenticated release withdrawal state is invalid.");
        }
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second, DateTimeOffset third) =>
        new[] { first, second, third }.Max();

    private readonly record struct ParsedReleaseVersion(int Major, int Minor, int Patch, int? ReleaseCandidate)
        : IComparable<ParsedReleaseVersion>
    {
        public int CompareTo(ParsedReleaseVersion other)
        {
            var comparison = Major.CompareTo(other.Major);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = Minor.CompareTo(other.Minor);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = Patch.CompareTo(other.Patch);
            if (comparison != 0)
            {
                return comparison;
            }
            if (ReleaseCandidate is null)
            {
                return other.ReleaseCandidate is null ? 0 : 1;
            }
            return other.ReleaseCandidate is null ? -1 : ReleaseCandidate.Value.CompareTo(other.ReleaseCandidate.Value);
        }
    }
}
