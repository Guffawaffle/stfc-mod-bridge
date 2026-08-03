using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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

public sealed class WindowsModArtifactVersionReader(
    string expectedRuntimeDistributionId,
    IModBinaryVersionMetadataReader? metadataReader = null) : IModArtifactVersionReader
{
    private readonly string expectedRuntimeDistributionId = !string.IsNullOrWhiteSpace(expectedRuntimeDistributionId)
        ? expectedRuntimeDistributionId
        : throw new ArgumentException(
            "Expected runtime distribution identity is required.",
            nameof(expectedRuntimeDistributionId));
    private readonly IModBinaryVersionMetadataReader metadataReader =
        metadataReader ?? new WindowsModBinaryVersionMetadataReader();

    public string? ReadVersion(string artifactPath)
    {
        var metadata = metadataReader.Read(artifactPath);
        var identity = ModBuildIdentityCommentParser.Parse(metadata.Comments);
        if (identity.State == ModBuildIdentityParseState.Malformed)
        {
            throw new InvalidDataException(identity.Detail);
        }
        if (identity.Identity is not null
            && !string.Equals(
                identity.Identity.DistributionId,
                expectedRuntimeDistributionId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The artifact declares runtime distribution '{identity.Identity.DistributionId}', "
                + $"not '{expectedRuntimeDistributionId}'.");
        }
        return metadata.FileVersion;
    }
}

public sealed class WindowsAuthenticodeVerifier(string expectedPublisher) : IModArtifactAuthenticityVerifier
{
    private static readonly Guid GenericVerifyV2Action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private readonly string expectedPublisher = !string.IsNullOrWhiteSpace(expectedPublisher)
        ? expectedPublisher
        : throw new ArgumentException("Expected publisher is required.", nameof(expectedPublisher));

    public ModArtifactAuthenticityResult Verify(string artifactPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(false, "Authenticode verification is available only on Windows.");
        }
        if (!File.Exists(artifactPath))
        {
            return new(false, "The artifact does not exist.");
        }

        var filePathPointer = Marshal.StringToCoTaskMemUni(Path.GetFullPath(artifactPath));
        var fileInfoPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePathPointer,
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 1,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 0,
                ProviderFlags = 0x00000080,
                UiContext = 0,
            };
            var action = GenericVerifyV2Action;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
            if (status != 0)
            {
                return new(false, $"WinVerifyTrust rejected the artifact (0x{status:X8}).");
            }

#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(artifactPath));
#pragma warning restore SYSLIB0057
            var publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
            return string.Equals(publisher, expectedPublisher, StringComparison.OrdinalIgnoreCase)
                ? new(true, $"Trusted Authenticode publisher: {publisher}.")
                : new(false, $"The Authenticode publisher '{publisher}' is not the expected publisher.");
        }
        catch (CryptographicException exception)
        {
            return new(false, exception.Message);
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }
            Marshal.FreeCoTaskMem(filePathPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
