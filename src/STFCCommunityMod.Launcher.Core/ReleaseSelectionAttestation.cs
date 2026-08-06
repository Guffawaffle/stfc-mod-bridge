using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core;

public sealed record ReleaseSelectionVerificationRequest(
    int SchemaVersion,
    string ManifestPath,
    string BundlePath,
    string ExpectedTag);

public sealed record ReleaseSelectionRekorEntry(
    string LogId,
    long LogIndex,
    DateTimeOffset IntegratedTime);

public sealed record ReleaseSelectionVerificationReceipt(
    int SchemaVersion,
    bool Verified,
    string VerificationMode,
    string Repository,
    string RepositoryId,
    string OwnerId,
    string Workflow,
    string SourceRef,
    string SourceCommit,
    string Event,
    string Runner,
    string StatementType,
    string PredicateType,
    string BuildType,
    string SubjectName,
    string ManifestSha256,
    string BundleSha256,
    int TrustEpoch,
    string TrustedRootSha256,
    string FulcioIssuer,
    string FulcioSan,
    IReadOnlyList<ReleaseSelectionRekorEntry> RekorEntries,
    IReadOnlyList<string> Checks);

internal static partial class ReleaseSelectionAttestationPolicy
{
    internal const int SchemaVersion = 1;
    internal const int MaximumRequestBytes = 8 * 1024;
    internal const int MaximumReceiptBytes = 64 * 1024;
    internal const int MaximumEvidenceBytes = 1024 * 1024;
    internal const string ManifestName = "stfc-mod-bridge-release-manifest.json";
    internal const string BundleName = "stfc-mod-bridge-release-selection-attestation.json";
    internal const string Repository = "Guffawaffle/stfc-mod-bridge";
    internal const string RepositoryId = "1320037274";
    internal const string OwnerId = "105761663";
    internal const string Workflow = ".github/workflows/release.yml";
    internal const string VerificationMode = "offline";
    internal const string Event = "push";
    internal const string Runner = "github-hosted";
    internal const string StatementType = "https://in-toto.io/Statement/v1";
    internal const string PredicateType = "https://slsa.dev/provenance/v1";
    internal const string BuildType = "https://actions.github.io/buildtypes/workflow/v1";
    internal const int TrustEpoch = 1;
    internal const string TrustedRootSha256 = "844a1c6de3986c9f02070266b25e0d1a2fa99ceccc89f6b9ad90aae47b62a16e";
    internal const string FulcioIssuer = "https://token.actions.githubusercontent.com";
    internal static readonly HashSet<string> AcceptedRekorLogIds =
    [
        "c0d23d6ad406973f9559f3ba2d1ca01f84147d8ffc5b8445c224f98b9591801d",
        "cf1199155bddd051268d1f16ac5c0c75c009f6fb5a63f4177f8e18d7051e3fa0",
    ];
    internal static readonly string[] RequiredChecks =
    [
        "bundle-signature",
        "manifest-digest",
        "fulcio-chain",
        "certificate-transparency",
        "rekor-inclusion",
        "repository",
        "workflow",
        "tag-ref",
        "source-commit",
        "event",
        "runner",
        "statement",
        "predicate",
        "single-subject",
        "embedded-trust-root",
    ];

    [GeneratedRegex("^v[0-9]+\\.[0-9]+\\.[0-9]+(?:-rc\\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalTagPattern();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    internal static partial Regex CommitPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    internal static partial Regex Sha256Pattern();

    internal static ReleaseSelectionVerificationRequest CreateRequest(
        string manifestPath,
        string bundlePath,
        string expectedTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTag);
        if (!Path.IsPathFullyQualified(manifestPath)
            || !Path.IsPathFullyQualified(bundlePath)
            || Path.GetFileName(manifestPath) != ManifestName
            || Path.GetFileName(bundlePath) != BundleName
            || string.Equals(Path.GetFullPath(manifestPath), Path.GetFullPath(bundlePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Release-selection evidence paths are invalid or ambiguously named.");
        }
        if (!CanonicalTagPattern().IsMatch(expectedTag))
        {
            throw new ArgumentException("The expected release tag is not canonical.", nameof(expectedTag));
        }
        return new(SchemaVersion, Path.GetFullPath(manifestPath), Path.GetFullPath(bundlePath), expectedTag);
    }
}

internal static class ReleaseSelectionVerificationReceiptParser
{
    private static readonly HashSet<string> RootProperties =
    [
        "schemaVersion", "verified", "verificationMode", "repository", "repositoryId", "ownerId", "workflow",
        "sourceRef", "sourceCommit", "event", "runner", "statementType", "predicateType", "buildType",
        "subjectName", "manifestSha256", "bundleSha256", "trustEpoch", "trustedRootSha256", "fulcioIssuer",
        "fulcioSan", "rekorEntries", "checks",
    ];
    private static readonly HashSet<string> RekorProperties = ["logId", "logIndex", "integratedTime"];

    internal static ReleaseSelectionVerificationReceipt Parse(
        ReadOnlySpan<byte> receiptBytes,
        ReleaseSelectionVerificationRequest request,
        string expectedManifestSha256,
        string expectedBundleSha256)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (receiptBytes.IsEmpty || receiptBytes.Length > ReleaseSelectionAttestationPolicy.MaximumReceiptBytes)
        {
            throw new InvalidDataException("The verifier receipt is empty or exceeds its 64-KiB limit.");
        }
        try
        {
            using var document = JsonDocument.Parse(receiptBytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = RequireObject(document.RootElement, "verifier receipt");
            RejectUnknown(root, RootProperties, "verifier receipt");
            var receipt = new ReleaseSelectionVerificationReceipt(
                ReadInt32(root, "schemaVersion"),
                ReadBoolean(root, "verified"),
                ReadString(root, "verificationMode"),
                ReadString(root, "repository"),
                ReadString(root, "repositoryId"),
                ReadString(root, "ownerId"),
                ReadString(root, "workflow"),
                ReadString(root, "sourceRef"),
                ReadString(root, "sourceCommit"),
                ReadString(root, "event"),
                ReadString(root, "runner"),
                ReadString(root, "statementType"),
                ReadString(root, "predicateType"),
                ReadString(root, "buildType"),
                ReadString(root, "subjectName"),
                ReadString(root, "manifestSha256"),
                ReadString(root, "bundleSha256"),
                ReadInt32(root, "trustEpoch"),
                ReadString(root, "trustedRootSha256"),
                ReadString(root, "fulcioIssuer"),
                ReadString(root, "fulcioSan"),
                ReadRekorEntries(root),
                ReadChecks(root));
            Validate(receipt, request, expectedManifestSha256, expectedBundleSha256);
            return receipt;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The verifier receipt is not valid closed-schema JSON.", exception);
        }
    }

    private static void Validate(
        ReleaseSelectionVerificationReceipt receipt,
        ReleaseSelectionVerificationRequest request,
        string expectedManifestSha256,
        string expectedBundleSha256)
    {
        var expectedRef = $"refs/tags/{request.ExpectedTag}";
        var expectedSan = $"https://github.com/{ReleaseSelectionAttestationPolicy.Repository}/"
            + $"{ReleaseSelectionAttestationPolicy.Workflow}@{expectedRef}";
        if (receipt.SchemaVersion != ReleaseSelectionAttestationPolicy.SchemaVersion
            || !receipt.Verified
            || receipt.VerificationMode != ReleaseSelectionAttestationPolicy.VerificationMode
            || receipt.Repository != ReleaseSelectionAttestationPolicy.Repository
            || receipt.RepositoryId != ReleaseSelectionAttestationPolicy.RepositoryId
            || receipt.OwnerId != ReleaseSelectionAttestationPolicy.OwnerId
            || receipt.Workflow != ReleaseSelectionAttestationPolicy.Workflow
            || receipt.SourceRef != expectedRef
            || receipt.Event != ReleaseSelectionAttestationPolicy.Event
            || receipt.Runner != ReleaseSelectionAttestationPolicy.Runner
            || receipt.StatementType != ReleaseSelectionAttestationPolicy.StatementType
            || receipt.PredicateType != ReleaseSelectionAttestationPolicy.PredicateType
            || receipt.BuildType != ReleaseSelectionAttestationPolicy.BuildType
            || receipt.SubjectName != ReleaseSelectionAttestationPolicy.ManifestName
            || receipt.TrustEpoch != ReleaseSelectionAttestationPolicy.TrustEpoch
            || receipt.TrustedRootSha256 != ReleaseSelectionAttestationPolicy.TrustedRootSha256
            || receipt.FulcioIssuer != ReleaseSelectionAttestationPolicy.FulcioIssuer
            || receipt.FulcioSan != expectedSan)
        {
            throw new InvalidDataException("The verifier receipt does not match the closed Mod Bridge identity policy.");
        }
        if (!ReleaseSelectionAttestationPolicy.CommitPattern().IsMatch(receipt.SourceCommit)
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(receipt.ManifestSha256)
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(receipt.BundleSha256)
            || !FixedTimeHexEquals(receipt.ManifestSha256, expectedManifestSha256)
            || !FixedTimeHexEquals(receipt.BundleSha256, expectedBundleSha256))
        {
            throw new InvalidDataException("The verifier receipt contains invalid or mismatched source and evidence digests.");
        }
        if (receipt.RekorEntries.Count != 1
            || receipt.RekorEntries[0].LogIndex < 0
            || receipt.RekorEntries[0].IntegratedTime.Offset != TimeSpan.Zero
            || receipt.RekorEntries[0].IntegratedTime < new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero)
            || !ReleaseSelectionAttestationPolicy.AcceptedRekorLogIds.Contains(receipt.RekorEntries[0].LogId))
        {
            throw new InvalidDataException("The verifier receipt must contain exactly one valid Rekor entry.");
        }
        if (!receipt.Checks.SequenceEqual(ReleaseSelectionAttestationPolicy.RequiredChecks, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The verifier receipt check set is incomplete or unexpected.");
        }
    }

    private static ReleaseSelectionRekorEntry[] ReadRekorEntries(JsonElement root)
    {
        var property = ReadProperty(root, "rekorEntries");
        if (property.ValueKind != JsonValueKind.Array || property.GetArrayLength() != 1)
        {
            throw new InvalidDataException("rekorEntries must contain exactly one entry.");
        }
        return property.EnumerateArray().Select(element =>
        {
            var entry = RequireObject(element, "Rekor entry");
            RejectUnknown(entry, RekorProperties, "Rekor entry");
            var timestampText = ReadString(entry, "integratedTime");
            if (!DateTimeOffset.TryParseExact(
                    timestampText,
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var timestamp))
            {
                throw new InvalidDataException("Rekor integratedTime must be whole-second UTC RFC 3339.");
            }
            return new ReleaseSelectionRekorEntry(ReadString(entry, "logId"), ReadInt64(entry, "logIndex"), timestamp);
        }).ToArray();
    }

    private static string[] ReadChecks(JsonElement root)
    {
        var property = ReadProperty(root, "checks");
        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("checks must be an array.");
        }
        return property.EnumerateArray().Select(element =>
            element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString())
                ? element.GetString()!
                : throw new InvalidDataException("checks must contain non-empty strings.")).ToArray();
    }

    private static bool FixedTimeHexEquals(string left, string right)
    {
        if (!ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(right))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    }

    private static JsonElement RequireObject(JsonElement element, string context) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw new InvalidDataException($"{context} must be an object.");

    private static JsonElement ReadProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException($"Verifier receipt is missing '{name}'.");

    private static string ReadString(JsonElement element, string name)
    {
        var value = ReadProperty(element, name);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Verifier receipt '{name}' must be a non-empty string.");
    }

    private static int ReadInt32(JsonElement element, string name) =>
        ReadProperty(element, name).TryGetInt32(out var value)
            ? value
            : throw new InvalidDataException($"Verifier receipt '{name}' must be an integer.");

    private static long ReadInt64(JsonElement element, string name) =>
        ReadProperty(element, name).TryGetInt64(out var value)
            ? value
            : throw new InvalidDataException($"Verifier receipt '{name}' must be an integer.");

    private static bool ReadBoolean(JsonElement element, string name)
    {
        var value = ReadProperty(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Verifier receipt '{name}' must be a boolean."),
        };
    }

    private static void RejectUnknown(JsonElement element, HashSet<string> allowed, string context)
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

internal static class ReleaseSelectionVerifierProcess
{
    private const int MaximumErrorCharacters = 8 * 1024;
    private const long MaximumHelperBytes = 64L * 1024L * 1024L;

    internal static async Task<ReleaseSelectionVerificationReceipt> VerifyAsync(
        string helperPath,
        ReleaseSelectionVerificationRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentNullException.ThrowIfNull(request);
        var validatedRequest = ReleaseSelectionAttestationPolicy.CreateRequest(
            request.ManifestPath,
            request.BundlePath,
            request.ExpectedTag);
        if (request.SchemaVersion != ReleaseSelectionAttestationPolicy.SchemaVersion)
        {
            throw new ArgumentException("The verifier request schema is unsupported.", nameof(request));
        }
        request = validatedRequest;
        if (!Path.IsPathFullyQualified(helperPath) || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentException("The verifier process boundary is invalid.");
        }
        var helperInfo = new FileInfo(helperPath);
        if (!helperInfo.Exists
            || helperInfo.Length is <= 0 or > MaximumHelperBytes
            || (helperInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("The verifier helper must be an existing regular file, not a reparse point.", helperPath);
        }
        var startInfo = new ProcessStartInfo(helperInfo.FullName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = helperInfo.DirectoryName!,
        };
        startInfo.Environment.Clear();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The release verifier helper could not be started.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = request.SchemaVersion,
                manifestPath = request.ManifestPath,
                bundlePath = request.BundlePath,
                expectedTag = request.ExpectedTag,
            });
            if (requestBytes.Length > ReleaseSelectionAttestationPolicy.MaximumRequestBytes)
            {
                throw new InvalidDataException("The release verifier request exceeds its 8-KiB limit.");
            }
            await process.StandardInput.BaseStream.WriteAsync(requestBytes, timeoutSource.Token);
            process.StandardInput.Close();
            var outputTask = ReadBoundedAsync(
                process.StandardOutput,
                ReleaseSelectionAttestationPolicy.MaximumReceiptBytes,
                timeoutSource.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, MaximumErrorCharacters, timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"Release verifier rejected the evidence: {error.Trim()}");
            }
            var manifestDigest = await HashFileAsync(request.ManifestPath, timeoutSource.Token);
            var bundleDigest = await HashFileAsync(request.BundlePath, timeoutSource.Token);
            return ReleaseSelectionVerificationReceiptParser.Parse(
                Encoding.UTF8.GetBytes(output),
                request,
                manifestDigest,
                bundleDigest);
        }
        catch
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The process exited between the state check and termination.
                }
            }
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximum, 4096));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return builder.ToString();
            }
            if (builder.Length + read > maximum)
            {
                throw new InvalidDataException("The verifier process output exceeded its bound.");
            }
            builder.Append(buffer, 0, read);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
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
            throw new InvalidDataException("Release-selection evidence is outside the accepted size bound.");
        }
        var expectedLength = stream.Length;
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        if (stream.Position != expectedLength || stream.Length != expectedLength)
        {
            throw new InvalidDataException("Release-selection evidence changed while it was hashed.");
        }
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
