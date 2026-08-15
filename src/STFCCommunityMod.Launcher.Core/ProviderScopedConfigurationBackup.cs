using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace STFCCommunityMod.Launcher.Core;

public sealed record ConfigurationBackupRequest(
    string GameDirectory,
    string ProviderId,
    string ConfigurationPath,
    byte[] Contents,
    string Reason,
    string? TargetProviderId = null,
    string? ReleaseIdentity = null,
    string? PinnedBackupId = null);

public sealed record ConfigurationBackupReceipt(
    string BackupId,
    string InstallationId,
    string ProviderId,
    string? TargetProviderId,
    DateTimeOffset CreatedAtUtc,
    string ContentSha256,
    string Reason,
    string? ReleaseIdentity,
    bool WasRestored = false,
    DateTimeOffset? RestoredAtUtc = null,
    string? RestoreTransactionId = null);

internal sealed record ConfigurationBackupManifest(
    int SchemaVersion,
    string BackupId,
    string InstallationId,
    string ProviderId,
    string? TargetProviderId,
    string ConfigurationFileName,
    string ConfigurationIdentitySha256,
    DateTimeOffset CreatedAtUtc,
    string ContentSha256,
    long ContentLength,
    string ProtectionScheme,
    string Reason,
    string? ReleaseIdentity,
    bool WasRestored,
    DateTimeOffset? RestoredAtUtc = null,
    string? RestoreTransactionId = null);

public interface IConfigurationBackupProtector
{
    string SchemeId { get; }

    byte[] Protect(byte[] contents);

    byte[] Unprotect(byte[] protectedContents);
}

public interface IConfigurationBackupStorageSecurity
{
    void SecureDirectory(string directory);
}

public interface IConfigurationMutationBackup
{
    ValueTask<ConfigurationBackupReceipt> BeforeReplaceAsync(
        string configurationPath,
        byte[] expectedContents,
        CancellationToken cancellationToken);
}

internal enum ProviderConfigurationBackupCheckpoint
{
    PayloadDurable,
    ManifestDurable,
    Published,
    BeforePruneDelete,
    AfterPruneDelete,
}

public sealed class ProviderScopedConfigurationMutationBackup(
    ProviderScopedConfigurationBackupStore store,
    string providerId,
    string? releaseIdentity = null,
    string reason = "configuration-save",
    string? pinnedBackupId = null) : IConfigurationMutationBackup
{
    private readonly ProviderScopedConfigurationBackupStore store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly string providerId = string.IsNullOrWhiteSpace(providerId)
        ? throw new ArgumentException("A stable provider ID is required.", nameof(providerId))
        : providerId;
    private readonly string reason = string.IsNullOrWhiteSpace(reason)
        ? throw new ArgumentException("A configuration backup reason is required.", nameof(reason))
        : reason;

    public async ValueTask<ConfigurationBackupReceipt> BeforeReplaceAsync(
        string configurationPath,
        byte[] expectedContents,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentNullException.ThrowIfNull(expectedContents);
        var fullConfigurationPath = Path.GetFullPath(configurationPath);
        var gameDirectory = Path.GetDirectoryName(fullConfigurationPath)
            ?? throw new InvalidDataException("The configuration path has no game directory.");
        return await store.CreateAsync(
            new(
                gameDirectory,
                providerId,
                fullConfigurationPath,
                expectedContents,
                reason,
                ReleaseIdentity: releaseIdentity,
                PinnedBackupId: pinnedBackupId),
            cancellationToken).ConfigureAwait(false);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiConfigurationBackupProtector : IConfigurationBackupProtector
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("STFC Mod Bridge configuration backup v1");

    public string SchemeId => "windows-dpapi-current-user-v1";

    public byte[] Protect(byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return ProtectedData.Protect(contents, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedContents)
    {
        ArgumentNullException.ThrowIfNull(protectedContents);
        return ProtectedData.Unprotect(protectedContents, Entropy, DataProtectionScope.CurrentUser);
    }
}

public sealed class WindowsCurrentUserConfigurationBackupStorageSecurity
    : IConfigurationBackupStorageSecurity
{
    public void SecureDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Protected configuration backups require Windows access controls.");
        }

        Directory.CreateDirectory(directory);
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        const FileSystemRights rights = FileSystemRights.FullControl;
        const InheritanceFlags inheritance =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new(
            currentUser,
            rights,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new(
            system,
            rights,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
}

public sealed class ProviderScopedConfigurationBackupStore
{
    public const int DefaultRetentionCount = 5;
    private const int SchemaVersion = 2;
    private const int LegacySchemaVersion = 1;
    private const long MaximumConfigurationBytes = 8 * 1024 * 1024;
    private const string ManifestFileName = "manifest.json";
    private const string PayloadFileName = "configuration.protected";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PartitionGates = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string backupRoot;
    private readonly IConfigurationBackupProtector protector;
    private readonly IConfigurationBackupStorageSecurity storageSecurity;
    private readonly TimeProvider timeProvider;
    private readonly int retentionCount;
    private readonly Func<ProviderConfigurationBackupCheckpoint, CancellationToken, ValueTask>?
        checkpoint;

    public ProviderScopedConfigurationBackupStore(
        string stateDirectory,
        IConfigurationBackupProtector? protector = null,
        IConfigurationBackupStorageSecurity? storageSecurity = null,
        TimeProvider? timeProvider = null,
        int retentionCount = DefaultRetentionCount)
        : this(
            stateDirectory,
            protector,
            storageSecurity,
            timeProvider,
            retentionCount,
            checkpoint: null)
    {
    }

    internal ProviderScopedConfigurationBackupStore(
        string stateDirectory,
        IConfigurationBackupProtector? protector,
        IConfigurationBackupStorageSecurity? storageSecurity,
        TimeProvider? timeProvider,
        int retentionCount,
        Func<ProviderConfigurationBackupCheckpoint, CancellationToken, ValueTask>? checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionCount, 1);

        backupRoot = Path.Combine(Path.GetFullPath(stateDirectory), "configuration-backups");
        this.protector = protector ?? CreateDefaultProtector();
        this.storageSecurity = storageSecurity
            ?? new WindowsCurrentUserConfigurationBackupStorageSecurity();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.retentionCount = retentionCount;
        this.checkpoint = checkpoint;
    }

    public async Task<ConfigurationBackupReceipt> CreateAsync(
        ConfigurationBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateStableId(request.ProviderId, nameof(request.ProviderId));
        if (request.TargetProviderId is not null)
        {
            ValidateStableId(request.TargetProviderId, nameof(request.TargetProviderId));
        }
        if (request.PinnedBackupId is not null)
        {
            ValidateBackupId(request.PinnedBackupId);
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);
        ArgumentNullException.ThrowIfNull(request.Contents);
        if (request.Contents.LongLength > MaximumConfigurationBytes)
        {
            throw new InvalidDataException(
                $"Configuration exceeds the {MaximumConfigurationBytes}-byte backup limit.");
        }

        var validation = GameInstallValidator.Validate(request.GameDirectory);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(validation.Message);
        }
        var expectedConfigurationPath = Path.Combine(
            validation.GameDirectory,
            "community_patch_settings.toml");
        var configurationPath = Path.GetFullPath(request.ConfigurationPath);
        if (!PathEquals(expectedConfigurationPath, configurationPath))
        {
            throw new InvalidDataException(
                "The configuration backup must belong to the validated game installation.");
        }

        var installationId = ComputeInstallationId(validation.GameDirectory);
        var partition = Path.Combine(backupRoot, installationId, request.ProviderId);
        var gate = PartitionGates.GetOrAdd(partition, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            storageSecurity.SecureDirectory(backupRoot);
            storageSecurity.SecureDirectory(Path.Combine(backupRoot, installationId));
            storageSecurity.SecureDirectory(partition);

            var backupId = Guid.NewGuid().ToString("N");
            var temporaryDirectory = Path.Combine(partition, $".{backupId}.tmp");
            var completedDirectory = Path.Combine(partition, backupId);
            try
            {
                storageSecurity.SecureDirectory(temporaryDirectory);
                var protectedContents = protector.Protect(request.Contents);
                await WriteDurablyAsync(
                    Path.Combine(temporaryDirectory, PayloadFileName),
                    protectedContents,
                    cancellationToken).ConfigureAwait(false);
                await ObserveCheckpointAsync(
                    ProviderConfigurationBackupCheckpoint.PayloadDurable,
                    cancellationToken).ConfigureAwait(false);

                var verified = protector.Unprotect(
                    await File.ReadAllBytesAsync(
                        Path.Combine(temporaryDirectory, PayloadFileName),
                        cancellationToken).ConfigureAwait(false));
                if (!verified.AsSpan().SequenceEqual(request.Contents))
                {
                    throw new InvalidDataException(
                        "Protected configuration backup verification failed.");
                }

                var createdAtUtc = timeProvider.GetUtcNow();
                var contentSha256 = Hash(request.Contents);
                var manifest = new ConfigurationBackupManifest(
                    SchemaVersion,
                    backupId,
                    installationId,
                    request.ProviderId,
                    request.TargetProviderId,
                    Path.GetFileName(configurationPath),
                    Hash(Encoding.UTF8.GetBytes(NormalizePath(configurationPath))),
                    createdAtUtc,
                    contentSha256,
                    request.Contents.LongLength,
                    protector.SchemeId,
                    request.Reason,
                    request.ReleaseIdentity,
                    WasRestored: false,
                    RestoredAtUtc: null,
                    RestoreTransactionId: null);
                await WriteDurablyAsync(
                    Path.Combine(temporaryDirectory, ManifestFileName),
                    JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions),
                    cancellationToken).ConfigureAwait(false);
                await ObserveCheckpointAsync(
                    ProviderConfigurationBackupCheckpoint.ManifestDurable,
                    cancellationToken).ConfigureAwait(false);
                ValidateCompletedBackup(temporaryDirectory, manifest);
                Directory.Move(temporaryDirectory, completedDirectory);
                await ObserveCheckpointAsync(
                    ProviderConfigurationBackupCheckpoint.Published,
                    cancellationToken).ConfigureAwait(false);

                await PruneAsync(
                    partition,
                    installationId,
                    request.ProviderId,
                    backupId,
                    request.PinnedBackupId,
                    cancellationToken).ConfigureAwait(false);
                return ToReceipt(manifest);
            }
            catch
            {
                TryDeleteDirectory(temporaryDirectory);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public IReadOnlyList<ConfigurationBackupReceipt> List(
        string gameDirectory,
        string providerId)
    {
        ValidateStableId(providerId, nameof(providerId));
        var validation = GameInstallValidator.Validate(gameDirectory);
        if (!validation.IsValid)
        {
            return [];
        }
        var installationId = ComputeInstallationId(validation.GameDirectory);
        var partition = Path.Combine(backupRoot, installationId, providerId);
        return ReadManifests(partition, installationId, providerId)
            .Select(ToReceipt)
            .OrderByDescending(receipt => receipt.CreatedAtUtc)
            .ThenByDescending(receipt => receipt.BackupId, StringComparer.Ordinal)
            .ToArray();
    }

    public byte[] Read(
        string gameDirectory,
        string providerId,
        string backupId)
    {
        ValidateStableId(providerId, nameof(providerId));
        ValidateBackupId(backupId);
        var validation = GameInstallValidator.Validate(gameDirectory);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(validation.Message);
        }
        var installationId = ComputeInstallationId(validation.GameDirectory);
        var directory = Path.Combine(backupRoot, installationId, providerId, backupId);
        var manifest = ReadManifest(directory);
        if (!IsSupportedSchema(manifest.SchemaVersion)
            || !string.Equals(manifest.BackupId, backupId, StringComparison.Ordinal)
            || !string.Equals(manifest.InstallationId, installationId, StringComparison.Ordinal)
            || !string.Equals(manifest.ProviderId, providerId, StringComparison.Ordinal)
            || !string.Equals(manifest.ProtectionScheme, protector.SchemeId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Configuration backup identity does not match its partition.");
        }

        var contents = protector.Unprotect(
            File.ReadAllBytes(Path.Combine(directory, PayloadFileName)));
        if (contents.LongLength != manifest.ContentLength
            || !string.Equals(Hash(contents), manifest.ContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Configuration backup payload verification failed.");
        }
        return contents;
    }

    public async Task<ConfigurationBackupReceipt> MarkRestoredAsync(
        string gameDirectory,
        string providerId,
        string backupId,
        string restoreTransactionId,
        CancellationToken cancellationToken = default)
    {
        ValidateStableId(providerId, nameof(providerId));
        ValidateBackupId(backupId);
        if (!Guid.TryParseExact(restoreTransactionId, "N", out _))
        {
            throw new ArgumentException(
                "Restore transaction IDs must be 32 hexadecimal characters.",
                nameof(restoreTransactionId));
        }
        var validation = GameInstallValidator.Validate(gameDirectory);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(validation.Message);
        }
        var installationId = ComputeInstallationId(validation.GameDirectory);
        var partition = Path.Combine(backupRoot, installationId, providerId);
        var gate = PartitionGates.GetOrAdd(partition, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(partition, backupId);
            var manifest = ReadManifest(directory);
            ValidateManifestIdentity(manifest, backupId, installationId, providerId);
            ValidateCompletedBackup(directory, manifest);
            if (manifest.WasRestored
                && string.Equals(
                    manifest.RestoreTransactionId,
                    restoreTransactionId,
                    StringComparison.Ordinal))
            {
                return ToReceipt(manifest);
            }

            var updated = manifest with
            {
                SchemaVersion = SchemaVersion,
                WasRestored = true,
                RestoredAtUtc = timeProvider.GetUtcNow(),
                RestoreTransactionId = restoreTransactionId,
            };
            await WriteManifestAtomicallyAsync(directory, updated, cancellationToken)
                .ConfigureAwait(false);
            ValidateCompletedBackup(directory, updated);
            return ToReceipt(updated);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static string ComputeInstallationId(string gameDirectory) =>
        Hash(Encoding.UTF8.GetBytes(NormalizePath(gameDirectory))).ToLowerInvariant();

    private void ValidateCompletedBackup(
        string directory,
        ConfigurationBackupManifest expectedManifest)
    {
        var manifest = ReadManifest(directory);
        if (manifest != expectedManifest)
        {
            throw new InvalidDataException("Configuration backup manifest verification failed.");
        }
        if (!string.Equals(manifest.ProtectionScheme, protector.SchemeId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Configuration backup protection scheme changed unexpectedly.");
        }
        var protectedContents = File.ReadAllBytes(Path.Combine(directory, PayloadFileName));
        var contents = protector.Unprotect(protectedContents);
        if (contents.LongLength != manifest.ContentLength
            || !string.Equals(Hash(contents), manifest.ContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Configuration backup payload verification failed.");
        }
    }

    private async Task PruneAsync(
        string partition,
        string installationId,
        string providerId,
        string newlyCreatedBackupId,
        string? pinnedBackupId,
        CancellationToken cancellationToken)
    {
        var completed = ReadManifests(partition, installationId, providerId)
            .OrderByDescending(manifest => manifest.CreatedAtUtc)
            .ThenByDescending(manifest => manifest.BackupId, StringComparer.Ordinal)
            .ToArray();
        var retained = completed
            .Take(retentionCount)
            .Select(manifest => manifest.BackupId)
            .ToHashSet(StringComparer.Ordinal);
        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            newlyCreatedBackupId,
        };
        if (pinnedBackupId is not null
            && completed.Any(manifest => string.Equals(
                manifest.BackupId,
                pinnedBackupId,
                StringComparison.Ordinal)))
        {
            required.Add(pinnedBackupId);
        }
        retained.UnionWith(required);
        var minimumRetained = Math.Max(retentionCount, required.Count);
        foreach (var candidate in completed.Reverse())
        {
            if (retained.Count <= minimumRetained)
            {
                break;
            }
            if (!required.Contains(candidate.BackupId))
            {
                retained.Remove(candidate.BackupId);
            }
        }
        foreach (var manifest in completed)
        {
            if (retained.Contains(manifest.BackupId))
            {
                continue;
            }
            await ObserveCheckpointAsync(
                ProviderConfigurationBackupCheckpoint.BeforePruneDelete,
                cancellationToken).ConfigureAwait(false);
            TryDeleteDirectory(Path.Combine(partition, manifest.BackupId));
            await ObserveCheckpointAsync(
                ProviderConfigurationBackupCheckpoint.AfterPruneDelete,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask ObserveCheckpointAsync(
        ProviderConfigurationBackupCheckpoint current,
        CancellationToken cancellationToken) =>
        checkpoint?.Invoke(current, cancellationToken) ?? ValueTask.CompletedTask;

    private static List<ConfigurationBackupManifest> ReadManifests(
        string partition,
        string installationId,
        string providerId)
    {
        if (!Directory.Exists(partition))
        {
            return [];
        }

        var manifests = new List<ConfigurationBackupManifest>();
        foreach (var directory in Directory.EnumerateDirectories(partition))
        {
            var directoryName = Path.GetFileName(directory);
            if (directoryName.Length > 0 && directoryName[0] == '.')
            {
                continue;
            }
            try
            {
                var manifest = ReadManifest(directory);
                if (IsSupportedSchema(manifest.SchemaVersion)
                    && string.Equals(manifest.InstallationId, installationId, StringComparison.Ordinal)
                    && string.Equals(manifest.ProviderId, providerId, StringComparison.Ordinal)
                    && string.Equals(
                        Path.GetFileName(directory),
                        manifest.BackupId,
                        StringComparison.Ordinal))
                {
                    manifests.Add(manifest);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or JsonException)
            {
                // Invalid entries are retained for recovery inspection and never count as verified backups.
            }
        }
        return manifests;
    }

    private static ConfigurationBackupManifest ReadManifest(string directory) =>
        JsonSerializer.Deserialize<ConfigurationBackupManifest>(
            File.ReadAllBytes(Path.Combine(directory, ManifestFileName)),
            JsonOptions)
        ?? throw new InvalidDataException("Configuration backup manifest is empty.");

    private static ConfigurationBackupReceipt ToReceipt(ConfigurationBackupManifest manifest) =>
        new(
            manifest.BackupId,
            manifest.InstallationId,
            manifest.ProviderId,
            manifest.TargetProviderId,
            manifest.CreatedAtUtc,
            manifest.ContentSha256,
            manifest.Reason,
            manifest.ReleaseIdentity,
            manifest.WasRestored,
            manifest.RestoredAtUtc,
            manifest.RestoreTransactionId);

    private static bool IsSupportedSchema(int schemaVersion) =>
        schemaVersion is LegacySchemaVersion or SchemaVersion;

    private void ValidateManifestIdentity(
        ConfigurationBackupManifest manifest,
        string backupId,
        string installationId,
        string providerId)
    {
        if (!IsSupportedSchema(manifest.SchemaVersion)
            || !string.Equals(manifest.BackupId, backupId, StringComparison.Ordinal)
            || !string.Equals(manifest.InstallationId, installationId, StringComparison.Ordinal)
            || !string.Equals(manifest.ProviderId, providerId, StringComparison.Ordinal)
            || !string.Equals(manifest.ProtectionScheme, protector.SchemeId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Configuration backup identity does not match its partition.");
        }
    }

    private static async Task WriteManifestAtomicallyAsync(
        string directory,
        ConfigurationBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, ManifestFileName);
        var temporaryPath = Path.Combine(directory, $".manifest.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteDurablyAsync(
                temporaryPath,
                JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            var written = JsonSerializer.Deserialize<ConfigurationBackupManifest>(
                await File.ReadAllBytesAsync(temporaryPath, cancellationToken).ConfigureAwait(false),
                JsonOptions);
            if (written != manifest)
            {
                throw new InvalidDataException(
                    "Configuration backup restore receipt verification failed.");
            }
            File.Replace(temporaryPath, manifestPath, null, ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task WriteDurablyAsync(
        string path,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateStableId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 64
            || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
            || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException(
                "Provider IDs must be stable lowercase-compatible identifiers.",
                parameterName);
        }
    }

    private static void ValidateBackupId(string backupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        if (backupId.Length != 32 || backupId.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Backup IDs must be 32 hexadecimal characters.", nameof(backupId));
        }
    }

    private static string NormalizePath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.Ordinal);

    private static string Hash(byte[] contents) =>
        Convert.ToHexString(SHA256.HashData(contents));

    private static WindowsDpapiConfigurationBackupProtector CreateDefaultProtector()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Protected configuration backups require Windows DPAPI.");
        }
        return new WindowsDpapiConfigurationBackupProtector();
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A completed backup is authoritative before pruning. Residue is retained safely.
        }
    }
}
