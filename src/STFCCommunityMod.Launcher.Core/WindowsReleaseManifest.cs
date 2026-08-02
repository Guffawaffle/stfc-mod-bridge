using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core;

public sealed record WindowsReleaseSource(string Repository, string TargetCommit);

public sealed record WindowsArtifactAuthenticity(
    string Scheme,
    string Scope,
    IReadOnlyList<string> SignedFiles);

public sealed record WindowsReleaseArtifact(
    string Id,
    string Kind,
    string Platform,
    string Architecture,
    string FileName,
    string MediaType,
    long Size,
    string Sha256,
    WindowsArtifactAuthenticity Authenticity);

public sealed record WindowsReleaseManifest(
    int SchemaVersion,
    string ReleaseVersion,
    string Tag,
    string Channel,
    string ReleaseState,
    Version MinimumLauncherVersion,
    WindowsReleaseSource Source,
    string ManifestAuthenticityScheme,
    IReadOnlyList<WindowsReleaseArtifact> Artifacts);

public static partial class WindowsReleaseManifestParser
{
    private const int SupportedSchemaVersion = 1;
    private static readonly HashSet<string> RootProperties =
    [
        "schemaVersion",
        "releaseVersion",
        "tag",
        "channel",
        "releaseState",
        "minimumLauncherVersion",
        "source",
        "manifestAuthenticity",
        "artifacts",
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

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    public static WindowsReleaseManifest Parse(Stream manifestStream)
    {
        ArgumentNullException.ThrowIfNull(manifestStream);
        try
        {
            using var document = JsonDocument.Parse(manifestStream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = RequireObject(document.RootElement, "release manifest");
            RejectUnknownProperties(root, RootProperties, "release manifest");

            var schemaVersion = ReadInt32(root, "schemaVersion", "release manifest");
            if (schemaVersion != SupportedSchemaVersion)
            {
                throw new InvalidDataException($"Release manifest schema {schemaVersion} is unsupported.");
            }

            var releaseVersion = ReadString(root, "releaseVersion", "release manifest");
            var tag = ReadString(root, "tag", "release manifest");
            if (!string.Equals(tag, $"v{releaseVersion}", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Release manifest tag and releaseVersion do not match.");
            }

            var channel = ReadString(root, "channel", "release manifest");
            if (channel is not ("stable" or "preview"))
            {
                throw new InvalidDataException($"Release channel '{channel}' is unsupported.");
            }

            var releaseState = ReadString(root, "releaseState", "release manifest");
            if (releaseState is not ("active" or "withdrawn"))
            {
                throw new InvalidDataException($"Release state '{releaseState}' is unsupported.");
            }

            var minimumLauncherText = ReadString(root, "minimumLauncherVersion", "release manifest");
            if (!Version.TryParse(minimumLauncherText, out var minimumLauncherVersion))
            {
                throw new InvalidDataException("minimumLauncherVersion is not a numeric version.");
            }

            var sourceElement = RequireObject(ReadProperty(root, "source", "release manifest"), "release source");
            RejectUnknownProperties(sourceElement, SourceProperties, "release source");
            var repository = ReadString(sourceElement, "repository", "release source");
            var targetCommit = ReadString(sourceElement, "targetCommit", "release source");
            if (!RepositoryPattern().IsMatch(repository) || !CommitPattern().IsMatch(targetCommit))
            {
                throw new InvalidDataException("Release source identity is invalid.");
            }

            var manifestAuthenticity = RequireObject(
                ReadProperty(root, "manifestAuthenticity", "release manifest"),
                "manifest authenticity");
            RejectUnknownProperties(
                manifestAuthenticity,
                ManifestAuthenticityProperties,
                "manifest authenticity");
            var manifestAuthenticityScheme = ReadString(
                manifestAuthenticity,
                "scheme",
                "manifest authenticity");
            if (manifestAuthenticityScheme != "none")
            {
                throw new InvalidDataException(
                    $"Manifest authenticity scheme '{manifestAuthenticityScheme}' requires a newer consumer.");
            }

            var artifactArray = ReadProperty(root, "artifacts", "release manifest");
            if (artifactArray.ValueKind != JsonValueKind.Array || artifactArray.GetArrayLength() == 0)
            {
                throw new InvalidDataException("Release manifest artifacts must be a non-empty array.");
            }
            var artifacts = artifactArray.EnumerateArray().Select(ParseArtifact).ToArray();
            if (artifacts.Select(artifact => artifact.Id).Distinct(StringComparer.Ordinal).Count() != artifacts.Length)
            {
                throw new InvalidDataException("Release artifact IDs must be unique.");
            }

            return new(
                schemaVersion,
                releaseVersion,
                tag,
                channel,
                releaseState,
                minimumLauncherVersion,
                new(repository, targetCommit),
                manifestAuthenticityScheme,
                artifacts);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The release manifest is not valid JSON.", exception);
        }
    }

    private static WindowsReleaseArtifact ParseArtifact(JsonElement value)
    {
        var artifact = RequireObject(value, "release artifact");
        RejectUnknownProperties(artifact, ArtifactProperties, "release artifact");
        var id = ReadString(artifact, "id", "release artifact");
        var kind = ReadString(artifact, "kind", "release artifact");
        var platform = ReadString(artifact, "platform", "release artifact");
        var architecture = ReadString(artifact, "architecture", "release artifact");
        var fileName = ReadString(artifact, "fileName", "release artifact");
        var mediaType = ReadString(artifact, "mediaType", "release artifact");
        var size = ReadInt64(artifact, "size", "release artifact");
        var sha256 = ReadString(artifact, "sha256", "release artifact");
        if (Path.GetFileName(fileName) != fileName || fileName is "." or ".." || size <= 0 || !Sha256Pattern().IsMatch(sha256))
        {
            throw new InvalidDataException($"Release artifact '{id}' has invalid file metadata.");
        }

        var authenticityElement = RequireObject(
            ReadProperty(artifact, "authenticity", "release artifact"),
            "artifact authenticity");
        RejectUnknownProperties(
            authenticityElement,
            ArtifactAuthenticityProperties,
            "artifact authenticity");
        var scheme = ReadString(authenticityElement, "scheme", "artifact authenticity");
        var scope = ReadString(authenticityElement, "scope", "artifact authenticity");
        var signedFiles = Array.Empty<string>();
        if (authenticityElement.TryGetProperty("signedFiles", out var signedFilesElement))
        {
            if (signedFilesElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("artifact authenticity signedFiles must be an array.");
            }
            signedFiles = signedFilesElement.EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.String
                    ? element.GetString()!
                    : throw new InvalidDataException("artifact authenticity signedFiles must contain strings."))
                .ToArray();
        }

        return new(
            id,
            kind,
            platform,
            architecture,
            fileName,
            mediaType,
            size,
            sha256,
            new(scheme, scope, signedFiles));
    }

    private static JsonElement RequireObject(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{context} must be an object.");
        }
        return value;
    }

    private static JsonElement ReadProperty(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException($"{context} is missing '{name}'.");
        }
        return value;
    }

    private static string ReadString(JsonElement parent, string name, string context)
    {
        var value = ReadProperty(parent, name, context);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{context}.{name} must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static int ReadInt32(JsonElement parent, string name, string context)
    {
        var value = ReadProperty(parent, name, context);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"{context}.{name} must be an integer.");
        }
        return result;
    }

    private static long ReadInt64(JsonElement parent, string name, string context)
    {
        var value = ReadProperty(parent, name, context);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException($"{context}.{name} must be an integer.");
        }
        return result;
    }

    private static void RejectUnknownProperties(
        JsonElement element,
        HashSet<string> allowed,
        string context)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException($"{context} contains unknown property '{property.Name}'.");
            }
        }
    }
}

public static partial class WindowsReleaseSelectionPolicy
{
    private const string ModArtifactId = "windows-mod-dll-x64";
    private const long MaximumModArtifactSize = 128L * 1024L * 1024L;
    private const string LauncherArtifactId = "windows-launcher-archive-x64";
    private const long MaximumLauncherArtifactSize = 512L * 1024L * 1024L;

    [GeneratedRegex(
        "^(?<major>\\d+)\\.(?<minor>\\d+)\\.(?<revision>\\d+)(?:(?:-guffa\\.(?:rc)?(?<patch>\\d+))|(?:\\.(?:alpha|beta)\\.(?<patch>\\d+))|(?:-rc\\.(?<patch>\\d+)))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionPattern();

    public static ModReleaseArtifact SelectModArtifact(
        WindowsReleaseManifest manifest,
        string selectedChannel,
        Version currentLauncherVersion,
        string expectedRepository)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(currentLauncherVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRepository);
        if (manifest.ReleaseState != "active")
        {
            throw new InvalidDataException("The selected release is withdrawn and cannot be newly installed.");
        }
        if (!string.Equals(manifest.Channel, selectedChannel, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The selected release does not belong to the configured channel.");
        }
        if (currentLauncherVersion < manifest.MinimumLauncherVersion)
        {
            throw new InvalidDataException(
                $"Launcher {manifest.MinimumLauncherVersion} or newer is required for this release.");
        }
        if (!string.Equals(
                manifest.Source.Repository,
                expectedRepository,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The release manifest belongs to an unexpected repository.");
        }

        var matches = manifest.Artifacts.Where(artifact => artifact.Id == ModArtifactId).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("The release must contain exactly one Windows mod DLL artifact.");
        }
        var artifact = matches[0];
        if (artifact.Kind != "windows-mod"
            || artifact.Platform != "windows"
            || artifact.Architecture != "x64"
            || !string.Equals(artifact.FileName, "version.dll", StringComparison.OrdinalIgnoreCase)
            || artifact.MediaType != "application/vnd.microsoft.portable-executable"
            || artifact.Size > MaximumModArtifactSize
            || artifact.Authenticity.Scheme != "authenticode"
            || artifact.Authenticity.Scope != "artifact"
            || artifact.Authenticity.SignedFiles.Count != 0)
        {
            throw new InvalidDataException("The Windows mod artifact contract is invalid or unsupported.");
        }

        var expectedFileVersion = DeriveEmbeddedFileVersion(manifest.ReleaseVersion);
        var downloadUri = new Uri(
            $"https://github.com/{expectedRepository}/releases/download/{Uri.EscapeDataString(manifest.Tag)}/{Uri.EscapeDataString(artifact.FileName)}");
        return new(
            downloadUri,
            artifact.FileName,
            artifact.Size,
            artifact.Sha256,
            expectedFileVersion);
    }

    public static LauncherReleaseArtifact SelectLauncherArtifact(
        WindowsReleaseManifest manifest,
        string selectedChannel,
        Version currentLauncherVersion,
        string expectedRepository)
    {
        ValidateRelease(
            manifest,
            selectedChannel,
            currentLauncherVersion,
            expectedRepository);
        var matches = manifest.Artifacts.Where(artifact => artifact.Id == LauncherArtifactId).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("The release must contain exactly one Windows launcher archive.");
        }
        var artifact = matches[0];
        if (artifact.Kind != "windows-launcher"
            || artifact.Platform != "windows"
            || artifact.Architecture != "x64"
            || artifact.FileName != "stfc-community-mod-launcher-win-x64.zip"
            || artifact.MediaType != "application/zip"
            || artifact.Size > MaximumLauncherArtifactSize
            || artifact.Authenticity.Scheme != "authenticode"
            || artifact.Authenticity.Scope != "contents"
            || artifact.Authenticity.SignedFiles.Count != 2
            || artifact.Authenticity.SignedFiles[0] != "STFCCommunityMod.Launcher.exe"
            || artifact.Authenticity.SignedFiles[1] != "STFCCommunityMod.Launcher.Updater.exe")
        {
            throw new InvalidDataException("The Windows launcher artifact contract is invalid or unsupported.");
        }
        return new(
            new Uri($"https://github.com/{expectedRepository}/releases/download/{Uri.EscapeDataString(manifest.Tag)}/{artifact.FileName}"),
            artifact.FileName,
            artifact.Size,
            artifact.Sha256,
            manifest.ReleaseVersion,
            manifest.Source.TargetCommit);
    }

    private static void ValidateRelease(
        WindowsReleaseManifest manifest,
        string selectedChannel,
        Version currentLauncherVersion,
        string expectedRepository)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(currentLauncherVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRepository);
        if (manifest.ReleaseState != "active"
            || !string.Equals(manifest.Channel, selectedChannel, StringComparison.Ordinal)
            || currentLauncherVersion < manifest.MinimumLauncherVersion
            || !string.Equals(
                manifest.Source.Repository,
                expectedRepository,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The release is not eligible for this launcher channel.");
        }
    }

    public static string DeriveEmbeddedFileVersion(string releaseVersion)
    {
        var match = ReleaseVersionPattern().Match(releaseVersion);
        if (!match.Success)
        {
            throw new InvalidDataException($"Release version '{releaseVersion}' cannot map to a Windows file version.");
        }
        var patch = match.Groups["patch"].Success ? match.Groups["patch"].Value : "0";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["revision"].Value}.{patch}");
    }
}
