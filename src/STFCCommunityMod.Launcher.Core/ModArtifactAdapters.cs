using System.Diagnostics;

namespace STFCCommunityMod.Launcher.Core;

public sealed class HttpModArtifactDownloader(
    HttpClient httpClient,
    long maximumDownloadSize = 128L * 1024L * 1024L) : IModArtifactDownloader
{
    private readonly long maximumDownloadSize = maximumDownloadSize > 0
        ? maximumDownloadSize
        : throw new ArgumentOutOfRangeException(nameof(maximumDownloadSize));

    public async Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > maximumDownloadSize)
        {
            return new(response.StatusCode, [], declaredLength);
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }
            if (destination.Length + count > maximumDownloadSize)
            {
                return new(response.StatusCode, [], declaredLength);
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return new(response.StatusCode, destination.ToArray(), declaredLength);
    }
}

public sealed class WindowsModArtifactVersionReader : IModArtifactVersionReader
{
    public string? ReadVersion(string artifactPath) =>
        FileVersionInfo.GetVersionInfo(artifactPath).FileVersion;
}
