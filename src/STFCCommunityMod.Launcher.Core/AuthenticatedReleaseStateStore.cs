using System.Text.Json;
using System.Text.Json.Serialization;

namespace STFCCommunityMod.Launcher.Core;

public sealed class AuthenticatedReleaseStateStore
{
    private const int DocumentSchemaVersion = 1;
    private const int MaximumStateBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly string statePath;
    private readonly string backupPath;
    private readonly string lockPath;

    public AuthenticatedReleaseStateStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        var root = Path.GetFullPath(stateDirectory);
        statePath = Path.Combine(root, "authenticated-release-state.v1.json");
        backupPath = Path.Combine(root, "authenticated-release-state.v1.previous.json");
        lockPath = Path.Combine(root, ".authenticated-release-state.lock");
    }

    public AuthenticatedReleaseChannelState? Load(string channel)
    {
        ValidateChannel(channel);
        return LoadDocument()?.Channels.SingleOrDefault(state => state.Channel == channel);
    }

    public void Advance(AuthenticatedReleaseChannelState candidate)
    {
        AuthenticatedReleaseManifestPolicy.ValidateState(candidate);
        var directory = Path.GetDirectoryName(statePath)!;
        Directory.CreateDirectory(directory);
        using var stateLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);

        var document = LoadDocument() ?? new StateDocument(DocumentSchemaVersion, []);
        var existing = document.Channels.SingleOrDefault(state => state.Channel == candidate.Channel);
        if (existing is not null)
        {
            ValidateAdvance(existing, candidate);
        }
        var channels = document.Channels
            .Where(state => state.Channel != candidate.Channel)
            .Append(candidate)
            .OrderBy(state => state.Channel, StringComparer.Ordinal)
            .ToArray();
        WriteAtomically(new StateDocument(DocumentSchemaVersion, channels));
    }

    private StateDocument? LoadDocument()
    {
        if (!File.Exists(statePath))
        {
            if (File.Exists(backupPath))
            {
                throw new InvalidDataException(
                    "The authenticated release state primary is missing while recovery evidence remains.");
            }
            return null;
        }
        var info = new FileInfo(statePath);
        if (info.Length is <= 0 or > MaximumStateBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The authenticated release state file is empty, oversized, or not a regular file.");
        }
        try
        {
            byte[] bytes;
            using (var stream = new FileStream(
                statePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan))
            {
                if (stream.Length is <= 0 or > MaximumStateBytes)
                {
                    throw new InvalidDataException("The authenticated release state file changed outside its size bound.");
                }
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                if (stream.Length != bytes.Length)
                {
                    throw new InvalidDataException("The authenticated release state file changed while it was read.");
                }
            }
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            RejectDuplicateProperties(json.RootElement, "authenticated release state");
            var document = JsonSerializer.Deserialize<StateDocument>(bytes, SerializerOptions)
                ?? throw new InvalidDataException("The authenticated release state file is empty.");
            if (document.SchemaVersion != DocumentSchemaVersion
                || document.Channels is null
                || document.Channels.Length > 2
                || document.Channels.Select(state => state.Channel).Distinct(StringComparer.Ordinal).Count()
                    != document.Channels.Length)
            {
                throw new InvalidDataException("The authenticated release state document identity is invalid.");
            }
            foreach (var state in document.Channels)
            {
                AuthenticatedReleaseManifestPolicy.ValidateState(state);
            }
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The authenticated release state file is malformed or unsupported.", exception);
        }
    }

    private void WriteAtomically(StateDocument document)
    {
        var directory = Path.GetDirectoryName(statePath)!;
        var temporaryPath = Path.Combine(directory, $".authenticated-release-state.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, SerializerOptions);
                stream.Flush(true);
                if (stream.Length > MaximumStateBytes)
                {
                    throw new InvalidDataException("The authenticated release state exceeds its 1-MiB limit.");
                }
            }
            if (File.Exists(statePath))
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                File.Replace(temporaryPath, statePath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, statePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateAdvance(
        AuthenticatedReleaseChannelState existing,
        AuthenticatedReleaseChannelState candidate)
    {
        if (candidate.HighestReleaseSequence < existing.HighestReleaseSequence
            || candidate.TrustEpoch < existing.TrustEpoch
            || candidate.LastObservedUtc < existing.LastObservedUtc
            || candidate.FirstObservedUtc < existing.FirstObservedUtc
            || AuthenticatedReleaseManifestPolicy.CompareReleaseVersions(
                candidate.HighestReleaseVersion,
                existing.HighestReleaseVersion) < 0)
        {
            throw new InvalidDataException("The authenticated release state update would lower a monotonic floor.");
        }
        if (candidate.HighestReleaseSequence == existing.HighestReleaseSequence
            && (candidate.HighestReleaseVersion != existing.HighestReleaseVersion
                || candidate.Tag != existing.Tag
                || candidate.SourceCommit != existing.SourceCommit
                || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                    candidate.ManifestSha256,
                    existing.ManifestSha256)
                || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                    candidate.BundleSha256,
                    existing.BundleSha256)
                || candidate.TrustEpoch != existing.TrustEpoch
                || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                    candidate.TrustedRootSha256,
                    existing.TrustedRootSha256)
                || candidate.FirstObservedUtc != existing.FirstObservedUtc))
        {
            throw new InvalidDataException("An accepted release sequence cannot be rebound to different evidence.");
        }
        if (candidate.HighestReleaseSequence > existing.HighestReleaseSequence
            && AuthenticatedReleaseManifestPolicy.CompareReleaseVersions(
                candidate.HighestReleaseVersion,
                existing.HighestReleaseVersion) <= 0)
        {
            throw new InvalidDataException("A higher release-state sequence must advance the semantic version.");
        }
        foreach (var priorWithdrawal in existing.Withdrawals)
        {
            if (!candidate.Withdrawals.Contains(priorWithdrawal))
            {
                throw new InvalidDataException("Authenticated withdrawal state is additive and cannot be removed.");
            }
        }
    }

    private static void RejectDuplicateProperties(JsonElement element, string context)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException($"{context} contains duplicate property '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value, context);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, context);
            }
        }
    }

    private static void ValidateChannel(string channel)
    {
        if (channel is not ("stable" or "preview"))
        {
            throw new ArgumentException("Authenticated release state channels are stable or preview.", nameof(channel));
        }
    }

    private sealed record StateDocument(int SchemaVersion, AuthenticatedReleaseChannelState[] Channels);
}
