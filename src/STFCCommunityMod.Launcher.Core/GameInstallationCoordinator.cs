namespace STFCCommunityMod.Launcher.Core;

/// <summary>One exact installation observed by the shared Profiles update coordinator.</summary>
public sealed record GameInstallationSnapshot(
    string GameDirectory, int? InstalledVersion, string State, string Phase,
    int? AvailableVersion = null, bool? UpdateAvailable = null,
    double? ProgressPercent = null, long? DownloadedBytes = null, long? DownloadBytes = null,
    long? ExtractedBytes = null, long? ExtractedTotalBytes = null,
    string? TransactionId = null, string? Message = null, bool RequiresRecovery = false,
    int? CompletedFiles = null, int? TotalFiles = null);

/// <summary>Bridge admission and presentation adapter; download, integrity, commit and recovery remain native.</summary>
public sealed class GameInstallationCoordinator(string stateDirectory, IProfileCatalogTransport? transport = null,
    string? catalogRoot = null)
{
    private readonly IProfileCatalogTransport transport = transport ?? new NativeProfileCatalogTransport();
    private readonly LauncherOperationLock operationLock = new(stateDirectory);

    public Task<ProfileCatalogResponse> ReadStatusAsync(string gameDirectory,
        CancellationToken cancellationToken = default) => ReadStatusAsync(gameDirectory, null, cancellationToken);

    public Task<ProfileCatalogResponse> ReadStatusAsync(string gameDirectory, string? installationId,
        CancellationToken cancellationToken = default) =>
        RequestAsync(new("installation-status", GameDirectory: Normalize(gameDirectory), InstallationId: installationId), cancellationToken);

    public Task<ProfileCatalogResponse> CheckAsync(string gameDirectory,
        CancellationToken cancellationToken = default) => CheckAsync(gameDirectory, null, cancellationToken);

    public Task<ProfileCatalogResponse> CheckAsync(string gameDirectory, string? installationId,
        CancellationToken cancellationToken = default) =>
        RequestAsync(new("check-game-update", GameDirectory: Normalize(gameDirectory), InstallationId: installationId), cancellationToken);

    public Task<ProfileCatalogResponse> UpdateAsync(string gameDirectory, int expectedVersion,
        IProgress<GameInstallationSnapshot>? progress = null, CancellationToken cancellationToken = default) =>
        UpdateAsync(gameDirectory, expectedVersion, null, progress, cancellationToken);

    public Task<ProfileCatalogResponse> UpdateAsync(string gameDirectory, int expectedVersion, string? installationId,
        IProgress<GameInstallationSnapshot>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedVersion);
        return MutateAsync(new("update-game", GameDirectory: Normalize(gameDirectory),
            ExpectedVersion: expectedVersion, InstallationId: installationId), progress, cancellationToken);
    }

    public Task<ProfileCatalogResponse> RecoverAsync(string gameDirectory,
        IProgress<GameInstallationSnapshot>? progress = null, CancellationToken cancellationToken = default) =>
        RecoverAsync(gameDirectory, null, progress, cancellationToken);

    public Task<ProfileCatalogResponse> RecoverAsync(string gameDirectory, string? installationId,
        IProgress<GameInstallationSnapshot>? progress = null, CancellationToken cancellationToken = default) =>
        MutateAsync(new("recover-game-update", GameDirectory: Normalize(gameDirectory), InstallationId: installationId), progress, cancellationToken);

    private async Task<ProfileCatalogResponse> MutateAsync(ProfileCatalogRequest request,
        IProgress<GameInstallationSnapshot>? progress, CancellationToken cancellationToken)
    {
        await using var lease = await operationLock.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null) return new(false, new("bridge-operation-busy",
            "Another Bridge launch, settings save, or installation operation is in progress. Nothing was changed."));
        cancellationToken.ThrowIfCancellationRequested();
        // Once admitted, a native transaction must finish or preserve its recovery journal.
        // Cancellation of the caller cannot abandon this lease while its worker still writes.
        var worker = RequestAsync(request, CancellationToken.None);
        while (!worker.IsCompleted)
        {
            if (await Task.WhenAny(worker, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false) == worker) break;
            try
            {
                var status = await ReadStatusAsync(request.GameDirectory!, request.InstallationId, CancellationToken.None).ConfigureAwait(false);
                if (status.Installation is { } installation) progress?.Report(installation);
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                // A failed observation must not cancel, retry, or release a running native mutation.
                // The worker's final result is still required and owns the retained journal.
            }
        }
        var result = await worker.ConfigureAwait(false);
        if (result.Installation is { } final) progress?.Report(final);
        return result;
    }

    private Task<ProfileCatalogResponse> RequestAsync(ProfileCatalogRequest request, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var response = transport.Request(request with { Root = catalogRoot });
            if (response.Installation is { } observed && !SameDirectory(request.GameDirectory!, observed.GameDirectory))
                throw new InvalidDataException("The shared updater reported a different installation. Review the target before continuing.");
            if (response.Ok && response.Installation is null)
                throw new InvalidDataException("The shared updater did not return installation evidence.");
            return response;
        }, cancellationToken);

    private static string Normalize(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        if (!Path.IsPathFullyQualified(gameDirectory)) throw new ArgumentException("Select an absolute game installation directory.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
    }

    public static bool SameDirectory(string left, string right) =>
        GameDirectoryIdentity.SameLocation(Normalize(left), Normalize(right));

    private static bool IsTransportFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException
        or System.Runtime.InteropServices.ExternalException or TypeLoadException or BadImageFormatException
        or System.Text.Json.JsonException;
}
