using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STFCCommunityMod.Launcher.Core;

internal enum BattleLifecycleOperationKind
{
    FeatureActivation,
}

internal enum BattleLifecycleStage
{
    Prepared,
    Quiesced,
    BackupVerified,
    CommitStarted,
    CommitVerified,
    CleanupPending,
    Failed,
}

internal enum BattleLifecycleJournalState
{
    Absent,
    Readable,
    RecoverableSuccessor,
    RecoverableResidue,
    RecoveryFailed,
    Unavailable,
}

internal enum BattleLifecyclePreCommitRecoveryState
{
    NoOperation,
    Recovered,
    Blocked,
    Unavailable,
}

internal sealed record BattleLifecyclePreCommitRecoveryResult(
    BattleLifecyclePreCommitRecoveryState State,
    string Code);

internal sealed record BattleLifecycleFileIdentity(long ByteCount, string Sha256);

internal sealed record BattleLifecycleResourceTransition(
    string Role,
    string PrimaryRelativePath,
    BattleLifecycleFileIdentity? Before,
    string? CandidateRelativePath,
    BattleLifecycleFileIdentity? After,
    string? DisplacedRelativePath = null,
    string? BackupRelativePath = null);

internal sealed record BattleLifecycleCredentialBinding(
    long Generation,
    int ProtectedByteCount,
    string ProtectedSha256);

internal sealed record BattleLifecycleConfigurationBinding(
    string SourceRevisionSha256,
    string SourcePathSha256,
    long SourceByteCount,
    string SourceSha256,
    string CandidateRelativePath,
    long CandidateByteCount,
    string CandidateSha256,
    string MutationReceiptSha256,
    string? BackupId,
    string? BackupContentSha256);

internal sealed record BattleLifecycleFeatureTransition(
    string FeatureId,
    LauncherPlayerFeaturePreference Before,
    LauncherPlayerFeaturePreference After);

internal sealed record BattleLifecycleMarker(
    string OperationId,
    BattleLifecycleOperationKind OperationKind,
    string OwnerId,
    BattleLifecycleStage Stage,
    IReadOnlyList<string> AffectedFeatureIds,
    IReadOnlyList<BattleLifecycleResourceTransition> Resources,
    BattleLifecycleCredentialBinding? Credential,
    BattleLifecycleConfigurationBinding? Configuration,
    IReadOnlyList<BattleLifecycleFeatureTransition> FeatureTransitions,
    bool SharedTargetBefore,
    bool SharedTargetAfter,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string ImplementationVersion,
    bool MutationBlocked,
    bool SafeReadsAllowed);

internal sealed record BattleLifecycleJournalInspection(
    BattleLifecycleJournalState State,
    BattleLifecycleMarker? Marker,
    BattleLifecycleMarker? Successor,
    string Code);

internal interface IBattleLifecycleMarkerProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedBytes);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiBattleLifecycleMarkerProtector : IBattleLifecycleMarkerProtector
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("STFC Mod Bridge Battle recovery marker v1");

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedBytes) =>
        ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
}

internal static class BattleLifecycleMarkerCodec
{
    internal const string Schema = "stfc.battle-lifecycle-operation.v1";
    internal const string FileName = "active-operation-v1.dpapi";
    internal const string NextFileName = "active-operation-v1.dpapi.next";
    internal const int MaximumProtectedBytes = 64 * 1024;
    private const int MaximumResources = 8;
    private static readonly string[] FeatureIds =
    [
        LauncherFeatureIds.BattleCollection,
        LauncherFeatureIds.FleetCollection,
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    public static byte[] Protect(
        BattleLifecycleMarker marker,
        IBattleLifecycleMarkerProtector protector)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(protector);
        Validate(marker);
        var plaintext = Serialize(marker);
        try
        {
            var protectedBytes = protector.Protect(plaintext)
                ?? throw new InvalidDataException("The Battle lifecycle protector returned no result.");
            if (ReferenceEquals(protectedBytes, plaintext))
            {
                protectedBytes = protectedBytes.ToArray();
            }
            if (protectedBytes.Length is <= 0 or > MaximumProtectedBytes)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                throw Invalid();
            }
            return protectedBytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static BattleLifecycleMarker Unprotect(
        byte[] protectedBytes,
        IBattleLifecycleMarkerProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        ArgumentNullException.ThrowIfNull(protector);
        if (protectedBytes.Length is <= 0 or > MaximumProtectedBytes)
        {
            throw Invalid();
        }

        byte[]? plaintext = null;
        try
        {
            plaintext = protector.Unprotect(protectedBytes)
                ?? throw new InvalidDataException("The Battle lifecycle protector returned no plaintext.");
            RejectDuplicateProperties(plaintext);
            MarkerDocument document;
            try
            {
                document = JsonSerializer.Deserialize<MarkerDocument>(plaintext, JsonOptions) ?? throw Invalid();
            }
            catch (JsonException exception)
            {
                throw Invalid(exception);
            }
            var marker = FromDocument(document);
            Validate(marker);
            var canonical = Serialize(marker);
            try
            {
                if (!plaintext.AsSpan().SequenceEqual(canonical))
                {
                    throw Invalid();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
            return marker;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public static void ValidateSuccessor(
        BattleLifecycleMarker current,
        BattleLifecycleMarker successor)
    {
        Validate(current);
        Validate(successor);
        if (current.Stage is BattleLifecycleStage.CleanupPending or BattleLifecycleStage.Failed)
        {
            throw Invalid();
        }
        var expected = current.Stage switch
        {
            BattleLifecycleStage.Prepared => BattleLifecycleStage.Quiesced,
            BattleLifecycleStage.Quiesced => BattleLifecycleStage.BackupVerified,
            BattleLifecycleStage.BackupVerified => BattleLifecycleStage.CommitStarted,
            BattleLifecycleStage.CommitStarted => BattleLifecycleStage.CommitVerified,
            BattleLifecycleStage.CommitVerified => BattleLifecycleStage.CleanupPending,
            _ => throw Invalid(),
        };
        if (successor.Stage != expected && successor.Stage != BattleLifecycleStage.Failed)
        {
            throw Invalid();
        }
        if (current.OperationId != successor.OperationId
            || current.OperationKind != successor.OperationKind
            || current.OwnerId != successor.OwnerId
            || current.StartedAtUtc != successor.StartedAtUtc
            || current.ImplementationVersion != successor.ImplementationVersion
            || current.MutationBlocked != successor.MutationBlocked
            || current.SafeReadsAllowed != successor.SafeReadsAllowed
            || current.SharedTargetBefore != successor.SharedTargetBefore
            || current.SharedTargetAfter != successor.SharedTargetAfter
            || current.UpdatedAtUtc > successor.UpdatedAtUtc
            || !current.AffectedFeatureIds.SequenceEqual(successor.AffectedFeatureIds, StringComparer.Ordinal)
            || !current.FeatureTransitions.SequenceEqual(successor.FeatureTransitions))
        {
            throw Invalid();
        }

        var successorResources = successor.Resources.ToDictionary(item => item.Role, StringComparer.Ordinal);
        foreach (var resource in current.Resources)
        {
            if (!successorResources.TryGetValue(resource.Role, out var retained) || retained != resource)
            {
                throw Invalid();
            }
        }
        if (successor.Resources.Count != current.Resources.Count)
        {
            throw Invalid();
        }
        if (current.Credential != successor.Credential
            || !ConfigurationSuccessorIsValid(current.Configuration, successor.Configuration, successor.Stage))
        {
            throw Invalid();
        }
    }

    internal static bool AreEquivalent(
        BattleLifecycleMarker left,
        BattleLifecycleMarker right)
    {
        Validate(left);
        Validate(right);
        return Serialize(left).AsSpan().SequenceEqual(Serialize(right));
    }

    private static byte[] Serialize(BattleLifecycleMarker marker) =>
        JsonSerializer.SerializeToUtf8Bytes(ToDocument(marker), JsonOptions);

    private static MarkerDocument ToDocument(BattleLifecycleMarker marker) => new(
        Schema,
        marker.OperationId,
        OperationKind(marker.OperationKind),
        marker.OwnerId,
        Stage(marker.Stage),
        marker.AffectedFeatureIds.ToArray(),
        marker.Resources.Select(resource => new ResourceDocument(
            resource.Role,
            resource.PrimaryRelativePath,
            Identity(resource.Before),
            resource.CandidateRelativePath,
            Identity(resource.After),
            resource.DisplacedRelativePath,
            resource.BackupRelativePath)).ToArray(),
        marker.Credential is null ? null : new(
            marker.Credential.Generation,
            marker.Credential.ProtectedByteCount,
            marker.Credential.ProtectedSha256),
        marker.Configuration is null ? null : new(
            marker.Configuration.SourceRevisionSha256,
            marker.Configuration.SourcePathSha256,
            marker.Configuration.SourceByteCount,
            marker.Configuration.SourceSha256,
            marker.Configuration.CandidateRelativePath,
            marker.Configuration.CandidateByteCount,
            marker.Configuration.CandidateSha256,
            marker.Configuration.MutationReceiptSha256,
            marker.Configuration.BackupId,
            marker.Configuration.BackupContentSha256),
        marker.FeatureTransitions.Select(feature => new FeatureDocument(
            feature.FeatureId,
            Preference(feature.Before),
            Preference(feature.After))).ToArray(),
        marker.SharedTargetBefore,
        marker.SharedTargetAfter,
        FormatTimestamp(marker.StartedAtUtc),
        FormatTimestamp(marker.UpdatedAtUtc),
        marker.ImplementationVersion,
        marker.MutationBlocked,
        marker.SafeReadsAllowed);

    private static BattleLifecycleMarker FromDocument(MarkerDocument value)
    {
        if (value.Schema != Schema
            || value.OperationId is null
            || value.OperationKind is null
            || value.OwnerId is null
            || value.Stage is null
            || value.AffectedFeatureIds is null
            || value.Resources is null
            || value.FeatureTransitions is null
            || value.StartedAtUtc is null
            || value.UpdatedAtUtc is null
            || value.ImplementationVersion is null)
        {
            throw Invalid();
        }
        return new(
            value.OperationId,
            ParseOperationKind(value.OperationKind),
            value.OwnerId,
            ParseStage(value.Stage),
            value.AffectedFeatureIds,
            value.Resources.Select(resource => resource is null
                ? throw Invalid()
                : new BattleLifecycleResourceTransition(
                    resource.Role ?? throw Invalid(),
                    resource.PrimaryRelativePath ?? throw Invalid(),
                    FromIdentity(resource.Before),
                    resource.CandidateRelativePath,
                    FromIdentity(resource.After),
                    resource.DisplacedRelativePath,
                    resource.BackupRelativePath)).ToArray(),
            value.Credential is null ? null : new(
                value.Credential.Generation,
                value.Credential.ProtectedByteCount,
                value.Credential.ProtectedSha256 ?? throw Invalid()),
            value.Configuration is null ? null : new(
                value.Configuration.SourceRevisionSha256 ?? throw Invalid(),
                value.Configuration.SourcePathSha256 ?? throw Invalid(),
                value.Configuration.SourceByteCount,
                value.Configuration.SourceSha256 ?? throw Invalid(),
                value.Configuration.CandidateRelativePath ?? throw Invalid(),
                value.Configuration.CandidateByteCount,
                value.Configuration.CandidateSha256 ?? throw Invalid(),
                value.Configuration.MutationReceiptSha256 ?? throw Invalid(),
                value.Configuration.BackupId,
                value.Configuration.BackupContentSha256),
            value.FeatureTransitions.Select(feature => feature is null
                ? throw Invalid()
                : new BattleLifecycleFeatureTransition(
                    feature.FeatureId ?? throw Invalid(),
                    ParsePreference(feature.Before ?? throw Invalid()),
                    ParsePreference(feature.After ?? throw Invalid()))).ToArray(),
            value.SharedTargetBefore,
            value.SharedTargetAfter,
            ParseTimestamp(value.StartedAtUtc),
            ParseTimestamp(value.UpdatedAtUtc),
            value.ImplementationVersion,
            value.MutationBlocked,
            value.SafeReadsAllowed);
    }

    private static void Validate(BattleLifecycleMarker marker)
    {
        if (!IsLowerHex(marker.OperationId, 32)
            || !IsLowerHex(marker.OwnerId, 32)
            || !Enum.IsDefined(marker.OperationKind)
            || !Enum.IsDefined(marker.Stage)
            || marker.OperationKind != BattleLifecycleOperationKind.FeatureActivation
            || marker.AffectedFeatureIds.Count is <= 0 or > 2
            || !IsStrictlyOrdered(marker.AffectedFeatureIds)
            || marker.AffectedFeatureIds.Any(feature => !FeatureIds.Contains(feature, StringComparer.Ordinal))
            || marker.Resources.Count is <= 0 or > MaximumResources
            || !IsStrictlyOrdered(marker.Resources.Select(resource => resource.Role))
            || marker.FeatureTransitions.Count != FeatureIds.Length
            || !marker.FeatureTransitions.Select(feature => feature.FeatureId).SequenceEqual(FeatureIds)
            || marker.StartedAtUtc.Offset != TimeSpan.Zero
            || marker.UpdatedAtUtc.Offset != TimeSpan.Zero
            || marker.UpdatedAtUtc < marker.StartedAtUtc
            || !IsSafeToken(marker.ImplementationVersion, 1, 64)
            || !marker.MutationBlocked
            || !marker.SafeReadsAllowed)
        {
            throw Invalid();
        }

        foreach (var feature in marker.FeatureTransitions)
        {
            var affected = marker.AffectedFeatureIds.Contains(feature.FeatureId, StringComparer.Ordinal);
            if (!Enum.IsDefined(feature.Before)
                || !Enum.IsDefined(feature.After)
                || affected && feature.After != LauncherPlayerFeaturePreference.Enabled
                || !affected && feature.Before != feature.After)
            {
                throw Invalid();
            }
        }
        if (marker.SharedTargetBefore != marker.FeatureTransitions.Any(
                feature => feature.Before == LauncherPlayerFeaturePreference.Enabled)
            || marker.SharedTargetAfter != marker.FeatureTransitions.Any(
                feature => feature.After == LauncherPlayerFeaturePreference.Enabled))
        {
            throw Invalid();
        }

        foreach (var resource in marker.Resources)
        {
            ValidateResource(marker.OperationId, resource);
        }
        var ownedPaths = marker.Resources
            .SelectMany(resource => new[]
            {
                resource.CandidateRelativePath,
                resource.DisplacedRelativePath,
                resource.BackupRelativePath,
            })
            .Where(path => path is not null)
            .Cast<string>()
            .ToList();
        if (marker.Configuration is not null)
        {
            ownedPaths.Add(marker.Configuration.CandidateRelativePath);
        }
        if (ownedPaths.Count != ownedPaths.Distinct(StringComparer.Ordinal).Count())
        {
            throw Invalid();
        }
        if (!marker.Resources.Any(resource => resource.Role == "runtime-lock" && resource.After is not null))
        {
            throw Invalid();
        }
        if (marker.Credential is not null)
        {
            ValidateIdentity(new(marker.Credential.ProtectedByteCount, marker.Credential.ProtectedSha256));
            if (marker.Credential.Generation <= 0
                || !marker.Resources.Any(resource => resource.Role == "ingest-credential"
                    && resource.After == new BattleLifecycleFileIdentity(
                        marker.Credential.ProtectedByteCount,
                        marker.Credential.ProtectedSha256)))
            {
                throw Invalid();
            }
        }
        if (marker.Configuration is not null)
        {
            if (marker.Configuration.SourceByteCount < 0
                || !IsLowerHex(marker.Configuration.SourceRevisionSha256, 64)
                || !IsLowerHex(marker.Configuration.SourcePathSha256, 64)
                || !IsLowerHex(marker.Configuration.SourceSha256, 64)
                || marker.Configuration.SourceRevisionSha256 != marker.Configuration.SourceSha256
                || !IsSafeRelativePath(marker.Configuration.CandidateRelativePath)
                || !marker.Configuration.CandidateRelativePath.StartsWith(
                    $"battle/recovery/{marker.OperationId}/candidate/",
                    StringComparison.Ordinal)
                || marker.Configuration.CandidateByteCount is < 0 or > 8 * 1024 * 1024
                || !IsLowerHex(marker.Configuration.CandidateSha256, 64)
                || !IsLowerHex(marker.Configuration.MutationReceiptSha256, 64)
                || (marker.Configuration.BackupId is null) !=
                    (marker.Configuration.BackupContentSha256 is null)
                || marker.Configuration.BackupId is not null
                    && !IsSafeToken(marker.Configuration.BackupId, 1, 96)
                || marker.Configuration.BackupContentSha256 is not null
                    && !IsLowerHex(marker.Configuration.BackupContentSha256, 64)
                || marker.Stage < BattleLifecycleStage.BackupVerified
                    && marker.Configuration.BackupId is not null
                || marker.Stage >= BattleLifecycleStage.BackupVerified
                    && marker.Stage != BattleLifecycleStage.Failed
                    && marker.Configuration.BackupId is null)
            {
                throw Invalid();
            }
        }
    }

    private static void ValidateResource(
        string operationId,
        BattleLifecycleResourceTransition resource)
    {
        var expectedPrimary = resource.Role switch
        {
            "runtime-lock" => "battle/runtime.lock",
            "ingest-credential" => $"battle/{BattleIngestCredentialCodec.FileName}",
            "battle-store" => "battle/battle-store-v1.sqlite3",
            _ => throw Invalid(),
        };
        if (resource.PrimaryRelativePath != expectedPrimary
            || !IsSafeRelativePath(resource.PrimaryRelativePath)
            || resource.Before is not null && !IsValidIdentity(resource.Before)
            || resource.After is not null && !IsValidIdentity(resource.After))
        {
            throw Invalid();
        }
        foreach (var path in new[]
                 {
                     resource.CandidateRelativePath,
                     resource.DisplacedRelativePath,
                     resource.BackupRelativePath,
                 }.Where(path => path is not null))
        {
            if (!IsSafeRelativePath(path!)
                || !path!.StartsWith($"battle/recovery/{operationId}/", StringComparison.Ordinal))
            {
                throw Invalid();
            }
        }
        if (resource.Role == "runtime-lock" && resource.CandidateRelativePath is not null)
        {
            throw Invalid();
        }
    }

    private static bool IsValidIdentity(BattleLifecycleFileIdentity identity)
    {
        try
        {
            ValidateIdentity(identity);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool ConfigurationSuccessorIsValid(
        BattleLifecycleConfigurationBinding? current,
        BattleLifecycleConfigurationBinding? successor,
        BattleLifecycleStage successorStage)
    {
        if (current is null || successor is null)
        {
            return current is null && successor is null;
        }
        if (current.SourceRevisionSha256 != successor.SourceRevisionSha256
            || current.SourcePathSha256 != successor.SourcePathSha256
            || current.SourceByteCount != successor.SourceByteCount
            || current.SourceSha256 != successor.SourceSha256
            || current.CandidateRelativePath != successor.CandidateRelativePath
            || current.CandidateByteCount != successor.CandidateByteCount
            || current.CandidateSha256 != successor.CandidateSha256
            || current.MutationReceiptSha256 != successor.MutationReceiptSha256)
        {
            return false;
        }
        if (current.BackupId is not null)
        {
            return current.BackupId == successor.BackupId
                && current.BackupContentSha256 == successor.BackupContentSha256;
        }
        return successorStage == BattleLifecycleStage.BackupVerified
            ? successor.BackupId is not null && successor.BackupContentSha256 is not null
            : successor.BackupId is null && successor.BackupContentSha256 is null;
    }

    private static void ValidateIdentity(BattleLifecycleFileIdentity identity)
    {
        if (identity.ByteCount < 0 || !IsLowerHex(identity.Sha256, 64))
        {
            throw Invalid();
        }
    }

    private static bool IsSafeRelativePath(string value) =>
        value.Length is > 0 and <= 240
        && value.StartsWith("battle/", StringComparison.Ordinal)
        && !value.Contains('\\')
        && !Path.IsPathRooted(value)
        && value.Split('/').All(segment =>
            segment.Length is > 0 and <= 96
            && segment is not "." and not ".."
            && segment.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_'));

    private static bool IsStrictlyOrdered(IEnumerable<string> values)
    {
        string? previous = null;
        foreach (var value in values)
        {
            if (previous is not null && string.CompareOrdinal(previous, value) >= 0)
            {
                return false;
            }
            previous = value;
        }
        return true;
    }

    private static bool IsSafeToken(string value, int minimum, int maximum) =>
        value.Length >= minimum
        && value.Length <= maximum
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_');

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static FileIdentityDocument? Identity(BattleLifecycleFileIdentity? identity) =>
        identity is null ? null : new(identity.ByteCount, identity.Sha256);

    private static BattleLifecycleFileIdentity? FromIdentity(FileIdentityDocument? identity) =>
        identity is null ? null : new(identity.ByteCount, identity.Sha256 ?? throw Invalid());

    private static string OperationKind(BattleLifecycleOperationKind value) => value switch
    {
        BattleLifecycleOperationKind.FeatureActivation => "feature-activation",
        _ => throw Invalid(),
    };

    private static BattleLifecycleOperationKind ParseOperationKind(string value) => value switch
    {
        "feature-activation" => BattleLifecycleOperationKind.FeatureActivation,
        _ => throw Invalid(),
    };

    private static string Stage(BattleLifecycleStage value) => value switch
    {
        BattleLifecycleStage.Prepared => "prepared",
        BattleLifecycleStage.Quiesced => "quiesced",
        BattleLifecycleStage.BackupVerified => "backup-verified",
        BattleLifecycleStage.CommitStarted => "commit-started",
        BattleLifecycleStage.CommitVerified => "commit-verified",
        BattleLifecycleStage.CleanupPending => "cleanup-pending",
        BattleLifecycleStage.Failed => "failed",
        _ => throw Invalid(),
    };

    private static BattleLifecycleStage ParseStage(string value) => value switch
    {
        "prepared" => BattleLifecycleStage.Prepared,
        "quiesced" => BattleLifecycleStage.Quiesced,
        "backup-verified" => BattleLifecycleStage.BackupVerified,
        "commit-started" => BattleLifecycleStage.CommitStarted,
        "commit-verified" => BattleLifecycleStage.CommitVerified,
        "cleanup-pending" => BattleLifecycleStage.CleanupPending,
        "failed" => BattleLifecycleStage.Failed,
        _ => throw Invalid(),
    };

    private static string Preference(LauncherPlayerFeaturePreference value) => value switch
    {
        LauncherPlayerFeaturePreference.Unset => "unset",
        LauncherPlayerFeaturePreference.Enabled => "enabled",
        LauncherPlayerFeaturePreference.Disabled => "disabled",
        _ => throw Invalid(),
    };

    private static LauncherPlayerFeaturePreference ParsePreference(string value) => value switch
    {
        "unset" => LauncherPlayerFeaturePreference.Unset,
        "enabled" => LauncherPlayerFeaturePreference.Enabled,
        "disabled" => LauncherPlayerFeaturePreference.Disabled,
        _ => throw Invalid(),
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero
            || FormatTimestamp(parsed) != value)
        {
            throw Invalid();
        }
        return parsed;
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 12,
        });
        var objects = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    objects.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (objects.Count == 0) throw Invalid();
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (objects.Count == 0
                        || objects.Peek() is not { } names
                        || !names.Add(reader.GetString() ?? throw Invalid()))
                    {
                        throw Invalid();
                    }
                    break;
            }
        }
        if (objects.Count != 0)
        {
            throw Invalid();
        }
    }

    private static InvalidDataException Invalid(Exception? inner = null) =>
        new("The Battle lifecycle marker is invalid.", inner);

    private sealed record MarkerDocument(
        string? Schema,
        string? OperationId,
        string? OperationKind,
        string? OwnerId,
        string? Stage,
        string[]? AffectedFeatureIds,
        ResourceDocument?[]? Resources,
        CredentialDocument? Credential,
        ConfigurationDocument? Configuration,
        FeatureDocument?[]? FeatureTransitions,
        bool SharedTargetBefore,
        bool SharedTargetAfter,
        string? StartedAtUtc,
        string? UpdatedAtUtc,
        string? ImplementationVersion,
        bool MutationBlocked,
        bool SafeReadsAllowed);

    private sealed record ResourceDocument(
        string? Role,
        string? PrimaryRelativePath,
        FileIdentityDocument? Before,
        string? CandidateRelativePath,
        FileIdentityDocument? After,
        string? DisplacedRelativePath,
        string? BackupRelativePath);

    private sealed record FileIdentityDocument(long ByteCount, string? Sha256);

    private sealed record CredentialDocument(
        long Generation,
        int ProtectedByteCount,
        string? ProtectedSha256);

    private sealed record ConfigurationDocument(
        string? SourceRevisionSha256,
        string? SourcePathSha256,
        long SourceByteCount,
        string? SourceSha256,
        string? CandidateRelativePath,
        long CandidateByteCount,
        string? CandidateSha256,
        string? MutationReceiptSha256,
        string? BackupId,
        string? BackupContentSha256);

    private sealed record FeatureDocument(string? FeatureId, string? Before, string? After);
}

internal sealed class BattleLifecycleJournalStore
{
    private readonly string stateRoot;
    private readonly string battleRoot;
    private readonly string recoveryRoot;
    private readonly IBattleLifecycleMarkerProtector protector;
    private readonly IConfigurationBackupStorageSecurity storageSecurity;
    private readonly Func<BattleLifecycleMarker, ValueTask>? beforeReplace;

    public BattleLifecycleJournalStore(
        string stateRoot,
        IBattleLifecycleMarkerProtector protector,
        IConfigurationBackupStorageSecurity? storageSecurity = null,
        Func<BattleLifecycleMarker, ValueTask>? beforeReplace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        this.stateRoot = Path.GetFullPath(stateRoot);
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.storageSecurity = storageSecurity
            ?? new WindowsCurrentUserConfigurationBackupStorageSecurity();
        this.beforeReplace = beforeReplace;
        battleRoot = Path.Combine(this.stateRoot, "battle");
        recoveryRoot = Path.Combine(battleRoot, "recovery");
        MarkerPath = Path.Combine(recoveryRoot, BattleLifecycleMarkerCodec.FileName);
    }

    public string MarkerPath { get; }

    internal string StateRoot => stateRoot;

    public BattleLifecycleJournalInspection Inspect()
    {
        try
        {
            if (!Directory.Exists(battleRoot))
            {
                return new(BattleLifecycleJournalState.Absent, null, null, "battle-operation-absent");
            }
            using var battleHandle = OpenDirectoryNoFollow(battleRoot);
            if (!Directory.Exists(recoveryRoot))
            {
                return new(BattleLifecycleJournalState.Absent, null, null, "battle-operation-absent");
            }
            using var recoveryHandle = OpenDirectoryNoFollow(recoveryRoot);
            var rootEntries = Directory.EnumerateFileSystemEntries(recoveryRoot).Take(4).ToArray();
            if (!rootEntries.Any(path => Path.GetFileName(path) == BattleLifecycleMarkerCodec.FileName))
            {
                return rootEntries.Length == 0
                    ? new(BattleLifecycleJournalState.Absent, null, null, "battle-operation-absent")
                    : new(BattleLifecycleJournalState.RecoveryFailed, null, null, "battle-operation-marker-missing");
            }
            var marker = Read(MarkerPath);
            var expectedCandidates = ExpectedCandidates(marker);
            var operationDirectory = Path.Combine(recoveryRoot, marker.OperationId);
            if (rootEntries.Any(path =>
                    Path.GetFileName(path) != BattleLifecycleMarkerCodec.FileName
                    && !PathEquals(path, operationDirectory)))
            {
                return new(BattleLifecycleJournalState.RecoveryFailed, marker, null, "battle-operation-successor-ambiguous");
            }
            var expectedNext = NextPath(marker.OperationId);
            if (!Directory.Exists(operationDirectory))
            {
                return expectedCandidates.Count == 0
                    ? new(BattleLifecycleJournalState.Readable, marker, null, "battle-operation-readable")
                    : new(
                        BattleLifecycleJournalState.RecoverableResidue,
                        marker,
                        null,
                        "battle-operation-candidate-missing");
            }
            using var operationHandle = OpenDirectoryNoFollow(operationDirectory);
            var candidateDirectory = Path.GetDirectoryName(expectedNext)!;
            var operationEntries = Directory.EnumerateFileSystemEntries(operationDirectory).Take(2).ToArray();
            if (operationEntries.Length == 0)
            {
                return new(
                    BattleLifecycleJournalState.RecoverableResidue,
                    marker,
                    null,
                    "battle-operation-empty-residue");
            }
            if (operationEntries.Length != 1 || !PathEquals(operationEntries[0], candidateDirectory)
                || !Directory.Exists(candidateDirectory))
            {
                return new(BattleLifecycleJournalState.RecoveryFailed, marker, null, "battle-operation-successor-ambiguous");
            }
            using var candidateHandle = OpenDirectoryNoFollow(candidateDirectory);
            var candidateEntries = Directory.EnumerateFileSystemEntries(candidateDirectory)
                .Take(expectedCandidates.Count + 2)
                .ToArray();
            if (candidateEntries.Length == 0)
            {
                return new(
                    BattleLifecycleJournalState.RecoverableResidue,
                    marker,
                    null,
                    "battle-operation-empty-residue");
            }
            var allowedPaths = expectedCandidates.Keys.Append(expectedNext).ToArray();
            if (candidateEntries.Any(entry => !allowedPaths.Any(allowed => PathEquals(entry, allowed))))
            {
                return new(BattleLifecycleJournalState.RecoveryFailed, marker, null, "battle-operation-successor-ambiguous");
            }
            var missingCandidate = false;
            foreach (var (candidatePath, expectedIdentity) in expectedCandidates)
            {
                if (!candidateEntries.Any(entry => PathEquals(entry, candidatePath)))
                {
                    missingCandidate = true;
                    continue;
                }
                if (!MatchesIdentity(candidatePath, expectedIdentity))
                {
                    return new(
                        BattleLifecycleJournalState.RecoveryFailed,
                        marker,
                        null,
                        "battle-operation-candidate-mismatch");
                }
            }
            var hasSuccessor = candidateEntries.Any(entry => PathEquals(entry, expectedNext));
            if (hasSuccessor)
            {
                if (missingCandidate)
                {
                    return new(
                        BattleLifecycleJournalState.RecoveryFailed,
                        marker,
                        null,
                        "battle-operation-candidate-missing");
                }
                var successor = Read(expectedNext);
                BattleLifecycleMarkerCodec.ValidateSuccessor(marker, successor);
                return new(
                    BattleLifecycleJournalState.RecoverableSuccessor,
                    marker,
                    successor,
                    "battle-operation-successor-recoverable");
            }
            return missingCandidate
                ? new(
                    BattleLifecycleJournalState.RecoverableResidue,
                    marker,
                    null,
                    "battle-operation-candidate-missing")
                : new(BattleLifecycleJournalState.Readable, marker, null, "battle-operation-readable");
        }
        catch (Exception exception) when (
            exception is InvalidDataException or CryptographicException or JsonException or ArgumentException)
        {
            return new(BattleLifecycleJournalState.RecoveryFailed, null, null, "battle-operation-invalid");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(BattleLifecycleJournalState.Unavailable, null, null, "battle-operation-unavailable");
        }
    }

    public async Task CreatePreparedAsync(
        LauncherOperationLease operationLease,
        BattleLifecycleMarker marker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationLease);
        ArgumentNullException.ThrowIfNull(marker);
        using var operationScope = operationLease.RetainFor(stateRoot);
        if (marker.Stage != BattleLifecycleStage.Prepared)
        {
            throw new InvalidOperationException("The first Battle lifecycle marker must be prepared.");
        }
        if (Inspect().State != BattleLifecycleJournalState.Absent)
        {
            throw new InvalidOperationException("A Battle lifecycle marker already requires attention.");
        }

        var protectedBytes = BattleLifecycleMarkerCodec.Protect(marker, protector);
        try
        {
            storageSecurity.SecureDirectory(battleRoot);
            using var battleHandle = OpenDirectoryNoFollow(battleRoot);
            storageSecurity.SecureDirectory(recoveryRoot);
            using var recoveryHandle = OpenDirectoryNoFollow(recoveryRoot);
            await WriteCreateNewDurablyAsync(MarkerPath, protectedBytes, cancellationToken).ConfigureAwait(false);
            var inspection = Inspect();
            if (inspection.State is not (
                    BattleLifecycleJournalState.Readable
                    or BattleLifecycleJournalState.RecoverableResidue)
                || inspection.Marker is null
                || !BattleLifecycleMarkerCodec.AreEquivalent(inspection.Marker, marker))
            {
                throw new InvalidDataException("The prepared Battle lifecycle marker did not verify.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public async Task AdvanceAsync(
        LauncherOperationLease operationLease,
        BattleLifecycleMarker successor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationLease);
        ArgumentNullException.ThrowIfNull(successor);
        using var operationScope = operationLease.RetainFor(stateRoot);
        var inspection = Inspect();
        if (inspection.State != BattleLifecycleJournalState.Readable || inspection.Marker is null)
        {
            throw new InvalidOperationException("The Battle lifecycle marker is not ready to advance.");
        }
        BattleLifecycleMarkerCodec.ValidateSuccessor(inspection.Marker, successor);
        var candidateDirectory = Path.GetDirectoryName(NextPath(successor.OperationId))!;
        var operationDirectory = Directory.GetParent(candidateDirectory)!.FullName;
        using var battleHandle = OpenDirectoryNoFollow(battleRoot);
        using var recoveryHandle = OpenDirectoryNoFollow(recoveryRoot);
        storageSecurity.SecureDirectory(operationDirectory);
        using var operationHandle = OpenDirectoryNoFollow(operationDirectory);
        storageSecurity.SecureDirectory(candidateDirectory);
        using var candidateHandle = OpenDirectoryNoFollow(candidateDirectory);
        var protectedBytes = BattleLifecycleMarkerCodec.Protect(successor, protector);
        var nextPath = NextPath(successor.OperationId);
        try
        {
            await WriteCreateNewDurablyAsync(nextPath, protectedBytes, cancellationToken).ConfigureAwait(false);
            if (!BattleLifecycleMarkerCodec.AreEquivalent(Read(nextPath), successor))
            {
                throw new InvalidDataException("The Battle lifecycle successor did not verify.");
            }
            if (beforeReplace is not null)
            {
                await beforeReplace(successor).ConfigureAwait(false);
            }
            candidateHandle.Dispose();
            operationHandle.Dispose();
            recoveryHandle.Dispose();
            battleHandle.Dispose();
            File.Replace(nextPath, MarkerPath, null, ignoreMetadataErrors: true);
            using var verificationBattleHandle = OpenDirectoryNoFollow(battleRoot);
            using var verificationHandle = OpenDirectoryNoFollow(recoveryRoot);
            if (!BattleLifecycleMarkerCodec.AreEquivalent(Read(MarkerPath), successor))
            {
                throw new InvalidDataException("The advanced Battle lifecycle marker did not verify.");
            }
            verificationHandle.Dispose();
            if (!Directory.EnumerateFileSystemEntries(candidateDirectory).Any())
            {
                Directory.Delete(candidateDirectory, recursive: false);
                if (!Directory.EnumerateFileSystemEntries(operationDirectory).Any())
                {
                    Directory.Delete(operationDirectory, recursive: false);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public Task<BattleLifecycleJournalInspection> RecoverAsync(
        LauncherOperationLease operationLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationLease);
        using var operationScope = operationLease.RetainFor(stateRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var inspection = Inspect();
        if (inspection.State is BattleLifecycleJournalState.Absent or BattleLifecycleJournalState.Readable)
        {
            return Task.FromResult(inspection);
        }
        if (inspection.Marker is null
            || inspection.State is not (
                BattleLifecycleJournalState.RecoverableSuccessor
                or BattleLifecycleJournalState.RecoverableResidue))
        {
            throw new InvalidOperationException("The Battle lifecycle marker requires manual recovery.");
        }

        var operationDirectory = Path.Combine(recoveryRoot, inspection.Marker.OperationId);
        var candidateDirectory = Path.Combine(operationDirectory, "candidate");
        if (inspection.State == BattleLifecycleJournalState.RecoverableResidue)
        {
            using var battleHandle = OpenDirectoryNoFollow(battleRoot);
            using var recoveryHandle = OpenDirectoryNoFollow(recoveryRoot);
            using var operationHandle = OpenDirectoryNoFollow(operationDirectory);
            IDisposable? candidateHandle = Directory.Exists(candidateDirectory)
                ? OpenDirectoryNoFollow(candidateDirectory)
                : null;
            if (candidateHandle is not null
                && Directory.EnumerateFileSystemEntries(candidateDirectory).Any()
                || Directory.EnumerateFileSystemEntries(operationDirectory)
                    .Any(path => !PathEquals(path, candidateDirectory)))
            {
                candidateHandle?.Dispose();
                throw new InvalidOperationException("The Battle lifecycle residue changed during recovery.");
            }
            candidateHandle?.Dispose();
            operationHandle.Dispose();
            recoveryHandle.Dispose();
            battleHandle.Dispose();
            if (Directory.Exists(candidateDirectory))
            {
                Directory.Delete(candidateDirectory, recursive: false);
            }
            Directory.Delete(operationDirectory, recursive: false);
            return Task.FromResult(Inspect());
        }

        var successor = inspection.Successor!;
        var nextPath = NextPath(inspection.Marker.OperationId);
        using (var battleHandle = OpenDirectoryNoFollow(battleRoot))
        using (var recoveryHandle = OpenDirectoryNoFollow(recoveryRoot))
        using (var operationHandle = OpenDirectoryNoFollow(operationDirectory))
        using (var candidateHandle = OpenDirectoryNoFollow(candidateDirectory))
        {
            var current = Read(MarkerPath);
            var next = Read(nextPath);
            if (!BattleLifecycleMarkerCodec.AreEquivalent(current, inspection.Marker)
                || !BattleLifecycleMarkerCodec.AreEquivalent(next, successor))
            {
                throw new InvalidOperationException("The Battle lifecycle successor changed during recovery.");
            }
            BattleLifecycleMarkerCodec.ValidateSuccessor(current, next);
        }
        File.Replace(nextPath, MarkerPath, null, ignoreMetadataErrors: true);
        using (var battleHandle = OpenDirectoryNoFollow(battleRoot))
        using (var recoveryHandle = OpenDirectoryNoFollow(recoveryRoot))
        {
            if (!BattleLifecycleMarkerCodec.AreEquivalent(Read(MarkerPath), successor))
            {
                throw new InvalidDataException("The recovered Battle lifecycle marker did not verify.");
            }
        }
        if (!Directory.EnumerateFileSystemEntries(candidateDirectory).Any())
        {
            Directory.Delete(candidateDirectory, recursive: false);
            Directory.Delete(operationDirectory, recursive: false);
        }
        return Task.FromResult(Inspect());
    }

    internal async Task WritePreparedCandidatesAsync(
        LauncherOperationLease operationLease,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationLease);
        ArgumentNullException.ThrowIfNull(candidates);
        using var operationScope = operationLease.RetainFor(stateRoot);
        var inspection = Inspect();
        if (inspection.Marker is not { Stage: BattleLifecycleStage.Prepared } marker
            || inspection.State is not (
                BattleLifecycleJournalState.RecoverableResidue
                or BattleLifecycleJournalState.Readable))
        {
            throw new InvalidOperationException("The prepared Battle marker is not ready for candidate writes.");
        }
        var expected = ExpectedCandidates(marker);
        var supplied = candidates.ToDictionary(
            item => ResolveRelativePath(item.Key),
            item => item.Value,
            PathComparer());
        if (expected.Count == 0
            || expected.Count != supplied.Count
            || expected.Any(item => !supplied.TryGetValue(item.Key, out var bytes)
                || Identity(bytes.Span) != item.Value))
        {
            throw new InvalidOperationException("The supplied Battle candidates do not match the prepared marker.");
        }

        var operationDirectory = Path.Combine(recoveryRoot, marker.OperationId);
        var candidateDirectory = Path.Combine(operationDirectory, "candidate");
        using var battleHandle = OpenDirectoryNoFollow(battleRoot);
        using var recoveryHandle = OpenDirectoryNoFollow(recoveryRoot);
        storageSecurity.SecureDirectory(operationDirectory);
        using var operationHandle = OpenDirectoryNoFollow(operationDirectory);
        storageSecurity.SecureDirectory(candidateDirectory);
        using var candidateHandle = OpenDirectoryNoFollow(candidateDirectory);
        foreach (var (candidatePath, bytes) in supplied.OrderBy(item => item.Key, PathComparer()))
        {
            await WriteCreateNewDurablyAsync(candidatePath, bytes, cancellationToken)
                .ConfigureAwait(false);
            if (!MatchesIdentity(candidatePath, expected[candidatePath]))
            {
                throw new InvalidDataException("The prepared Battle candidate did not verify.");
            }
        }
        var complete = Inspect();
        if (complete.State != BattleLifecycleJournalState.Readable
            || complete.Marker is null
            || !BattleLifecycleMarkerCodec.AreEquivalent(complete.Marker, marker))
        {
            throw new InvalidDataException("The prepared Battle candidates did not verify as a complete set.");
        }
    }

    public Task<BattleLifecyclePreCommitRecoveryResult> RollbackPreparedAsync(
        LauncherOperationLease operationLease,
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationLease);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        using var operationScope = operationLease.RetainFor(stateRoot);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inspection = Inspect();
            if (inspection.State == BattleLifecycleJournalState.Absent)
            {
                return Task.FromResult(new BattleLifecyclePreCommitRecoveryResult(
                    BattleLifecyclePreCommitRecoveryState.NoOperation,
                    "battle-precommit-absent"));
            }
            if (inspection.Marker is not { } marker
                || marker.Stage is not (
                    BattleLifecycleStage.Prepared
                    or BattleLifecycleStage.Quiesced
                    or BattleLifecycleStage.BackupVerified)
                || inspection.Successor is not null
                || inspection.State == BattleLifecycleJournalState.Unavailable
                || inspection.State == BattleLifecycleJournalState.RecoveryFailed
                    && inspection.Code != "battle-operation-candidate-mismatch")
            {
                return Task.FromResult(Blocked());
            }
            ValidateSourceConfiguration(marker, configurationPath);
            var expectedCandidates = ExpectedCandidates(marker);
            var operationDirectory = Path.Combine(recoveryRoot, marker.OperationId);
            var candidateDirectory = Path.Combine(operationDirectory, "candidate");
            var inventory = PreflightPreparedInventory(
                expectedCandidates,
                operationDirectory,
                candidateDirectory);

            var candidateStreams = inventory.CandidatePaths
                .OrderBy(path => path, PathComparer())
                .Select(OpenLockedDeleteNoFollow)
                .ToArray();
            FileStream? runtimeStream = null;
            FileStream? markerStream = null;
            try
            {
                var runtimePath = Path.Combine(battleRoot, BattleRuntimeLockCodec.FileName);
                if (inventory.RuntimePresent)
                {
                    runtimeStream = OpenLockedDeleteNoFollow(runtimePath);
                }
                markerStream = OpenLockedDeleteNoFollow(MarkerPath);
                var current = Read(markerStream);
                if (!BattleLifecycleMarkerCodec.AreEquivalent(marker, current))
                {
                    return Task.FromResult(Blocked());
                }

                foreach (var stream in candidateStreams)
                {
                    MarkDeleteOnClose(stream.SafeFileHandle);
                }
                foreach (var stream in candidateStreams)
                {
                    stream.Dispose();
                }
                if (runtimeStream is not null)
                {
                    MarkDeleteOnClose(runtimeStream.SafeFileHandle);
                    runtimeStream.Dispose();
                    runtimeStream = null;
                }
                DeleteEmptyOwnedDirectory(candidateDirectory);
                DeleteEmptyOwnedDirectory(operationDirectory);
                MarkDeleteOnClose(markerStream.SafeFileHandle);
                markerStream.Dispose();
                markerStream = null;
                DeleteEmptyOwnedDirectory(recoveryRoot);
                DeleteEmptyOwnedDirectory(battleRoot);
                var final = Inspect();
                return Task.FromResult(final.State == BattleLifecycleJournalState.Absent
                    ? new BattleLifecyclePreCommitRecoveryResult(
                        BattleLifecyclePreCommitRecoveryState.Recovered,
                        "battle-precommit-recovered")
                    : Blocked());
            }
            finally
            {
                foreach (var stream in candidateStreams)
                {
                    stream.Dispose();
                }
                runtimeStream?.Dispose();
                markerStream?.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or CryptographicException or JsonException or ArgumentException)
        {
            return Task.FromResult(Blocked());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return Task.FromResult(new BattleLifecyclePreCommitRecoveryResult(
                BattleLifecyclePreCommitRecoveryState.Unavailable,
                "battle-precommit-unavailable"));
        }
    }

    private BattleLifecycleMarker Read(string path)
    {
        using var stream = OpenLockedReadNoFollow(path);
        if (stream.Length is <= 0 or > BattleLifecycleMarkerCodec.MaximumProtectedBytes)
        {
            throw new InvalidDataException("The Battle lifecycle marker is outside its size bound.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        try
        {
            return BattleLifecycleMarkerCodec.Unprotect(bytes, protector);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private BattleLifecycleMarker Read(FileStream stream)
    {
        if (stream.Length is <= 0 or > BattleLifecycleMarkerCodec.MaximumProtectedBytes)
        {
            throw new InvalidDataException("The Battle lifecycle marker is outside its size bound.");
        }
        stream.Position = 0;
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        try
        {
            return BattleLifecycleMarkerCodec.Unprotect(bytes, protector);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private string NextPath(string operationId) => Path.Combine(
        recoveryRoot,
        operationId,
        "candidate",
        BattleLifecycleMarkerCodec.NextFileName);

    private Dictionary<string, BattleLifecycleFileIdentity> ExpectedCandidates(
        BattleLifecycleMarker marker)
    {
        var expected = new Dictionary<string, BattleLifecycleFileIdentity>(PathComparer());
        foreach (var resource in marker.Resources.Where(resource => resource.CandidateRelativePath is not null))
        {
            if (resource.After is null
                || !expected.TryAdd(ResolveRelativePath(resource.CandidateRelativePath!), resource.After))
            {
                throw new InvalidDataException("The Battle lifecycle candidate inventory is invalid.");
            }
        }
        if (marker.Configuration is not null
            && !expected.TryAdd(
                ResolveRelativePath(marker.Configuration.CandidateRelativePath),
                new(
                    marker.Configuration.CandidateByteCount,
                    marker.Configuration.CandidateSha256)))
        {
            throw new InvalidDataException("The Battle lifecycle configuration candidate is duplicated.");
        }
        return expected;
    }

    private string ResolveRelativePath(string relativePath)
    {
        var resolved = Path.GetFullPath(Path.Combine(stateRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(
                stateRoot + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Battle lifecycle path escapes the state root.");
        }
        return resolved;
    }

    private static bool MatchesIdentity(string path, BattleLifecycleFileIdentity expected)
    {
        using var stream = OpenLockedReadNoFollow(path);
        return stream.Length == expected.ByteCount
            && string.Equals(
                Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
                expected.Sha256,
                StringComparison.Ordinal);
    }

    private static BattleLifecycleFileIdentity Identity(ReadOnlySpan<byte> bytes) =>
        new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    internal static string PathIdentity(string path) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))
        .ToLowerInvariant();

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static void ValidateSourceConfiguration(BattleLifecycleMarker marker, string configurationPath)
    {
        if (marker.Configuration is not { } configuration)
        {
            throw new InvalidDataException("The prepared Battle operation has no configuration binding.");
        }
        var canonicalPath = Path.GetFullPath(configurationPath);
        if (!string.Equals(
                PathIdentity(canonicalPath),
                configuration.SourcePathSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Battle source configuration path changed before rollback.");
        }
        using var stream = OpenLockedReadNoFollow(canonicalPath);
        if (stream.Length != configuration.SourceByteCount
            || stream.Length is < 0 or > 8 * 1024 * 1024
            || !string.Equals(
                Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
                configuration.SourceSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Battle source configuration changed before rollback.");
        }
    }

    private PreparedInventory PreflightPreparedInventory(
        IReadOnlyDictionary<string, BattleLifecycleFileIdentity> expectedCandidates,
        string operationDirectory,
        string candidateDirectory)
    {
        using var battleHandle = OpenDirectoryNoFollow(battleRoot);
        using var recoveryHandle = OpenDirectoryNoFollow(recoveryRoot);
        var runtimePath = Path.Combine(battleRoot, BattleRuntimeLockCodec.FileName);
        var battleEntries = Directory.EnumerateFileSystemEntries(battleRoot).Take(4).ToArray();
        if (battleEntries.Any(path =>
                !PathEquals(path, recoveryRoot) && !PathEquals(path, runtimePath)))
        {
            throw new InvalidDataException("The Battle directory contains an unowned pre-commit entry.");
        }
        if (File.Exists(Path.Combine(battleRoot, BattleIngestCredentialCodec.FileName)))
        {
            throw new InvalidDataException("The authoritative Battle credential changed before commit.");
        }
        var recoveryEntries = Directory.EnumerateFileSystemEntries(recoveryRoot).Take(4).ToArray();
        if (recoveryEntries.Any(path =>
                !PathEquals(path, MarkerPath) && !PathEquals(path, operationDirectory)))
        {
            throw new InvalidDataException("The Battle recovery directory contains an unowned entry.");
        }
        if (!recoveryEntries.Any(path => PathEquals(path, operationDirectory)))
        {
            return new([], battleEntries.Any(path => PathEquals(path, runtimePath)));
        }
        using var operationHandle = OpenDirectoryNoFollow(operationDirectory);
        var operationEntries = Directory.EnumerateFileSystemEntries(operationDirectory).Take(3).ToArray();
        if (operationEntries.Any(path =>
                !PathEquals(path, candidateDirectory)))
        {
            throw new InvalidDataException("The Battle operation directory contains an unowned entry.");
        }
        if (!operationEntries.Any(path => PathEquals(path, candidateDirectory)))
        {
            return new([], battleEntries.Any(path => PathEquals(path, runtimePath)));
        }
        using var candidateHandle = OpenDirectoryNoFollow(candidateDirectory);
        var candidateEntries = Directory.EnumerateFileSystemEntries(candidateDirectory)
            .Take(expectedCandidates.Count + 1)
            .ToArray();
        if (candidateEntries.Any(path =>
                !expectedCandidates.Keys.Any(expected => PathEquals(path, expected))))
        {
            throw new InvalidDataException("The Battle candidate directory contains an unowned entry.");
        }
        return new(
            candidateEntries,
            battleEntries.Any(path => PathEquals(path, runtimePath)));
    }

    private static FileStream OpenLockedDeleteNoFollow(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        return new(
            CandidateFileNative.OpenRecoveryReadDeleteNoFollow(path),
            FileAccess.Read,
            81920,
            isAsync: false);
    }

    private static void MarkDeleteOnClose(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Battle lifecycle exact cleanup requires Windows handles.");
        }
        if (!CandidateFileNative.TryMarkDeleteOnClose(handle))
        {
            throw new Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                "The exact Battle lifecycle file could not be marked for deletion.");
        }
    }

    private static void DeleteEmptyOwnedDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new InvalidDataException("The Battle lifecycle directory is not empty after exact cleanup.");
        }
        if (!OperatingSystem.IsWindows())
        {
            Directory.Delete(path, recursive: false);
            return;
        }
        using var handle = CandidateFileNative.OpenRecoveryDirectoryReadDeleteNoFollow(path);
        MarkDeleteOnClose(handle);
    }

    private static BattleLifecyclePreCommitRecoveryResult Blocked() => new(
        BattleLifecyclePreCommitRecoveryState.Blocked,
        "battle-precommit-blocked");

    private sealed record PreparedInventory(
        IReadOnlyList<string> CandidatePaths,
        bool RuntimePresent);

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task WriteCreateNewDurablyAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static FileStream OpenLockedReadNoFollow(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        return new(CandidateFileNative.OpenRecoveryReadNoFollow(path), FileAccess.Read, 81920, isAsync: false);
    }

    private static IDisposable OpenDirectoryNoFollow(string path) => OperatingSystem.IsWindows()
        ? CandidateFileNative.OpenRecoveryDirectoryReadNoFollow(path)
        : NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
