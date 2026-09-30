using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

/// <summary>Holds native profile-directory custody during every read or atomic configuration commit.</summary>
public sealed class ProfileConfigurationRepository(
    LauncherProfile profile,
    NativeLauncherProfilesStore profiles,
    Func<string?> selectedProfileId,
    IConfigurationRepository inner) : IConfigurationRepository
{
    public bool ProducesVerifiedBackupReceipt => inner.ProducesVerifiedBackupReceipt;

    public ConfigurationRepositoryReadResult Read(string? configurationPath)
    {
        try
        {
            using var lease = Admit(configurationPath);
            var read = inner.Read(configurationPath);
            return read.State == ConfigurationRepositoryReadState.NoConfigurationSelected
                ? new(ConfigurationRepositoryReadState.Succeeded,
                    new ConfigurationDocumentSnapshot(lease.Profile.ConfigPath, [], existed: false))
                : read;
        }
        catch (Exception exception) when (IsAdmissionFailure(exception))
        { return new(ConfigurationRepositoryReadState.IoFailure, Error: exception.Message); }
    }

    public async Task<ConfigurationRepositoryCommitResult> CommitAsync(ConfigurationCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var lease = Admit(request.Path);
            return await inner.CommitAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAdmissionFailure(exception))
        { return new(AtomicTomlWriteState.Conflict, Error: exception.Message); }
    }

    public async Task<ConfigurationRepositoryCommitResult> CommitDocumentAsync(ConfigurationDocumentCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var lease = Admit(request.Path);
            return await inner.CommitDocumentAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAdmissionFailure(exception))
        { return new(AtomicTomlWriteState.Conflict, Error: exception.Message); }
    }

    private ProfileCatalogLease Admit(string? configurationPath)
    {
        if (selectedProfileId() != profile.Id)
            throw new InvalidOperationException("The selected profile changed. Discard and reload before saving.");
        var lease = profiles.AcquireDataLease(profile.Id);
        try
        {
            if (selectedProfileId() != profile.Id || lease.Profile.Id != profile.Id
                || lease.Profile.State != "active" || string.IsNullOrWhiteSpace(configurationPath)
                || !string.Equals(Path.GetFullPath(configurationPath), Path.GetFullPath(lease.Profile.ConfigPath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("The profile configuration target changed or was archived. Nothing was written.");
            ProfileConfigurationFilesystemSafety.RejectLink(lease.Profile.Directory);
            ProfileConfigurationFilesystemSafety.RejectLink(lease.Profile.ConfigPath);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static bool IsAdmissionFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException;
}

/// <summary>Verified encrypted configuration backups travel inside the profile directory.</summary>
public sealed class ProfileConfigurationMutationBackup(LauncherProfile profile, string providerId) : IConfigurationMutationBackup
{
    public async ValueTask<ConfigurationBackupReceipt> BeforeReplaceAsync(string configurationPath,
        byte[] expectedContents, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows profile backups require DPAPI.");
        if (!string.Equals(Path.GetFullPath(configurationPath), Path.GetFullPath(profile.ConfigPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A profile backup must belong to the held profile configuration.");
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        ProfileConfigurationFilesystemSafety.RejectLink(profile.Directory);
        ProfileConfigurationFilesystemSafety.RejectLink(profile.ConfigPath);
        var backups = Path.Combine(profile.Directory, "backups");
        ProfileConfigurationFilesystemSafety.CreateOrdinaryDirectory(backups);
        var root = Path.Combine(backups, "configuration");
        ProfileConfigurationFilesystemSafety.CreateOrdinaryDirectory(root);
        var staging = Path.Combine(root, $".{id}.tmp");
        var destination = Path.Combine(root, id);
        System.IO.Directory.CreateDirectory(staging);
        var entropy = System.Text.Encoding.UTF8.GetBytes($"STFC Profiles configuration backup v1:{profile.Id}");
        var protectedBytes = ProtectedData.Protect(expectedContents, entropy, DataProtectionScope.CurrentUser);
        var receipt = new ConfigurationBackupReceipt(id, profile.Id, providerId, null,
            DateTimeOffset.UtcNow, ConfigurationDocumentRevision.FromContents(expectedContents).Sha256,
            "profile-configuration-save", null);
        var payloadPath = Path.Combine(staging, "config.bin");
        await WriteDurablyAsync(payloadPath, protectedBytes, cancellationToken).ConfigureAwait(false);
        await WriteDurablyAsync(Path.Combine(staging, "receipt.json"),
            JsonSerializer.SerializeToUtf8Bytes(receipt), cancellationToken).ConfigureAwait(false);
        var recovered = ProtectedData.Unprotect(await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false),
            entropy, DataProtectionScope.CurrentUser);
        if (!recovered.AsSpan().SequenceEqual(expectedContents))
            throw new InvalidDataException("The encrypted profile configuration backup could not be verified.");
        System.IO.Directory.Move(staging, destination);
        return receipt;
    }

    private static async Task WriteDurablyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }
}

internal static class ProfileConfigurationFilesystemSafety
{
    public static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Profile configuration and backup paths must not be filesystem links or reparse points.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    public static void CreateOrdinaryDirectory(string path)
    {
        RejectLink(path);
        System.IO.Directory.CreateDirectory(path);
        RejectLink(path);
    }
}
