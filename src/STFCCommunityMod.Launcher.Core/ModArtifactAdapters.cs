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

public sealed class WindowsAuthenticodeVerifier(
    string expectedPublisherSubject,
    string expectedArtifactSigningIdentityEku)
    : IModArtifactAuthenticityVerifier
{
    private static readonly Guid GenericVerifyV2Action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private static readonly string Rfc3161TimestampOid = "1.3.6.1.4.1.311.3.3.1";
    private static readonly string Rfc3161V21TimestampOid = "1.3.6.1.4.1.311.3.3.2";
    private static readonly string LegacyTimestampOid = "1.2.840.113549.1.9.6";
    private const uint MaximumSignatureCount = 32;
    private const uint MaximumUnauthenticatedAttributeCount = 128;
    private const uint MaximumTimestampTokenBytes = 1024 * 1024;
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckChain = 0x00000040;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;
    private const uint WssVerifySpecific = 0x00000001;
    private const uint WssGetSecondarySignatureCount = 0x00000002;
    private readonly X500DistinguishedName expectedPublisher = !string.IsNullOrWhiteSpace(expectedPublisherSubject)
        ? new X500DistinguishedName(expectedPublisherSubject)
        : throw new ArgumentException("Expected publisher subject is required.", nameof(expectedPublisherSubject));
    private readonly string expectedArtifactSigningIdentityEku = ValidateOid(
        expectedArtifactSigningIdentityEku,
        nameof(expectedArtifactSigningIdentityEku));

    public ModArtifactAuthenticityResult Verify(string artifactPath)
        => Verify(artifactPath, AuthenticodeRevocationMode.CachedOnly);

    /// <summary>
    /// Performs an Authenticode policy evaluation. Callers must use
    /// <see cref="AuthenticodeRevocationMode.OnlineRetrievalAllowed"/> only from an explicitly user-authorized flow.
    /// </summary>
    public ModArtifactAuthenticityResult Verify(
        string artifactPath,
        AuthenticodeRevocationMode revocationMode)
    {
        if (revocationMode is not AuthenticodeRevocationMode.CachedOnly
            and not AuthenticodeRevocationMode.OnlineRetrievalAllowed)
        {
            return new(false, "The Authenticode revocation mode is invalid.");
        }
        if (!OperatingSystem.IsWindows())
        {
            return new(false, "Authenticode verification is available only on Windows.");
        }
        if (!File.Exists(artifactPath))
        {
            return new(false, "The artifact does not exist.");
        }

        var evaluatedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            var inspections = InspectAllSignatures(artifactPath, revocationMode);
            return AuthenticodeTrustPolicy.Evaluate(
                revocationMode,
                evaluatedAtUtc,
                inspections.Select(ToPublicEvidence).ToArray());
        }
        catch (CryptographicException exception)
        {
            return new(false, $"Authenticode certificate inspection failed ({exception.HResult:X8}).");
        }
        catch (InvalidDataException exception)
        {
            return new(false, exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            return new(false, "Authenticode signer metadata is outside the supported bounds.");
        }
    }

    private List<NativeSignatureInspection> InspectAllSignatures(
        string artifactPath,
        AuthenticodeRevocationMode revocationMode)
    {
        var primary = InspectSignature(artifactPath, revocationMode, 0, getSecondarySignatureCount: true);
        if (primary.SecondarySignatureCount >= MaximumSignatureCount)
        {
            throw new InvalidDataException("The artifact contains too many Authenticode signatures.");
        }
        var signatures = new List<NativeSignatureInspection> { primary.Signature };
        for (uint index = 1; index <= primary.SecondarySignatureCount; index++)
        {
            signatures.Add(InspectSignature(artifactPath, revocationMode, index, false).Signature);
        }
        return signatures;
    }

    private SignatureInspectionResult InspectSignature(
        string artifactPath,
        AuthenticodeRevocationMode revocationMode,
        uint index,
        bool getSecondarySignatureCount)
    {
        var filePathPointer = Marshal.StringToCoTaskMemUni(Path.GetFullPath(artifactPath));
        var fileInfoPointer = IntPtr.Zero;
        var signatureSettingsPointer = IntPtr.Zero;
        var stateOpened = false;
        var trustData = default(WinTrustData);
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePathPointer,
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var signatureSettings = new WinTrustSignatureSettings
            {
                Size = (uint)Marshal.SizeOf<WinTrustSignatureSettings>(),
                Index = index,
                Flags = getSecondarySignatureCount ? WssGetSecondarySignatureCount : WssVerifySpecific,
            };
            signatureSettingsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustSignatureSettings>());
            Marshal.StructureToPtr(signatureSettings, signatureSettingsPointer, false);

            trustData = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeWholeChain,
                UnionChoice = WtdChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WtdStateActionVerify,
                ProviderFlags = WtdRevocationCheckChain
                    | (revocationMode == AuthenticodeRevocationMode.CachedOnly ? WtdCacheOnlyUrlRetrieval : 0u),
                UiContext = 0,
                SignatureSettings = signatureSettingsPointer,
            };
            var action = GenericVerifyV2Action;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
            stateOpened = trustData.StateData != IntPtr.Zero;
            signatureSettings = Marshal.PtrToStructure<WinTrustSignatureSettings>(signatureSettingsPointer);
            if (status != 0 || !stateOpened)
            {
                return new(
                    new NativeSignatureInspection(
                        index,
                        status,
                        false,
                        false,
                        false,
                        AuthenticodeTimestampKind.None,
                        null,
                        null),
                    signatureSettings.SecondarySignatureCount);
            }
            if (signatureSettings.VerifiedSignatureIndex != index)
            {
                throw new InvalidDataException("WinVerifyTrust evaluated an unexpected Authenticode signature index.");
            }

            var providerData = WTHelperProvDataFromStateData(trustData.StateData);
            var signerPointer = providerData == IntPtr.Zero
                ? IntPtr.Zero
                : WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
            if (signerPointer == IntPtr.Zero)
            {
                throw new InvalidDataException("WinVerifyTrust returned no signer evidence.");
            }

            var signer = Marshal.PtrToStructure<CryptProviderSigner>(signerPointer);
            if (signer.Error != 0)
            {
                throw new InvalidDataException("WinVerifyTrust returned failed signer evidence.");
            }
            if (signer.CertificateChainCount == 0 || signer.CertificateChain == IntPtr.Zero)
            {
                throw new InvalidDataException("WinVerifyTrust returned no signer certificate chain.");
            }
            var providerCertificate = Marshal.PtrToStructure<CryptProviderCertificate>(signer.CertificateChain);
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(providerCertificate.CertificateContext);
#pragma warning restore SYSLIB0057
            var publisherMatched = certificate.SubjectName.RawData.AsSpan().SequenceEqual(expectedPublisher.RawData);
            var hasCodeSigningEku = HasEku(certificate, "1.3.6.1.5.5.7.3.3");
            var durableIdentityMatched = HasEku(certificate, expectedArtifactSigningIdentityEku);
            var timestampKind = ReadTimestampKind(signer.SignerInfo);
            var verifyAsOfFileTime = signer.VerifyAsOf.ToInt64();
            DateTimeOffset? verifiedAsOf = verifyAsOfFileTime == 0
                ? null
                : DateTimeOffset.FromFileTime(verifyAsOfFileTime).ToUniversalTime();
            var identityHash = Convert.ToHexString(SHA256.HashData(certificate.SubjectName.RawData));
            return new(
                new NativeSignatureInspection(
                    index,
                    status,
                    publisherMatched,
                    hasCodeSigningEku,
                    durableIdentityMatched,
                    timestampKind,
                    verifiedAsOf,
                    identityHash),
                signatureSettings.SecondarySignatureCount);
        }
        finally
        {
            if (stateOpened)
            {
                trustData.StateAction = WtdStateActionClose;
                var action = GenericVerifyV2Action;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
            }
            if (signatureSettingsPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(signatureSettingsPointer);
            }
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }
            Marshal.FreeCoTaskMem(filePathPointer);
        }
    }

    private static bool HasEku(X509Certificate2 certificate, string expectedOid)
    {
        var extensions = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (extensions.Length != 1)
        {
            return false;
        }
        return extensions[0].EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == expectedOid);
    }

    private static AuthenticodeTimestampKind ReadTimestampKind(IntPtr signerInfoPointer)
    {
        if (signerInfoPointer == IntPtr.Zero)
        {
            return AuthenticodeTimestampKind.None;
        }
        var signerInfo = Marshal.PtrToStructure<CmsgSignerInfo>(signerInfoPointer);
        if (signerInfo.UnauthenticatedAttributes.Count > MaximumUnauthenticatedAttributeCount
            || signerInfo.UnauthenticatedAttributes.Count > 0
                && signerInfo.UnauthenticatedAttributes.Attributes == IntPtr.Zero)
        {
            throw new InvalidDataException("The Authenticode signer has invalid timestamp attributes.");
        }
        var foundLegacy = false;
        var rfc3161Count = 0;
        var attributeSize = Marshal.SizeOf<CryptAttribute>();
        for (var index = 0u; index < signerInfo.UnauthenticatedAttributes.Count; index++)
        {
            var pointer = IntPtr.Add(signerInfo.UnauthenticatedAttributes.Attributes, checked((int)index * attributeSize));
            var attribute = Marshal.PtrToStructure<CryptAttribute>(pointer);
            var oid = Marshal.PtrToStringAnsi(attribute.ObjectId);
            if (string.Equals(oid, Rfc3161TimestampOid, StringComparison.Ordinal)
                || string.Equals(oid, Rfc3161V21TimestampOid, StringComparison.Ordinal))
            {
                if (attribute.ValueCount != 1 || attribute.Values == IntPtr.Zero)
                {
                    throw new InvalidDataException("The RFC 3161 timestamp attribute has invalid values.");
                }
                var timestampToken = Marshal.PtrToStructure<CryptDataBlob>(attribute.Values);
                if (timestampToken.Size == 0
                    || timestampToken.Size > MaximumTimestampTokenBytes
                    || timestampToken.Data == IntPtr.Zero)
                {
                    throw new InvalidDataException("The RFC 3161 timestamp token is invalid or exceeds its size limit.");
                }
                rfc3161Count++;
            }
            foundLegacy |= string.Equals(oid, LegacyTimestampOid, StringComparison.Ordinal);
        }
        if (rfc3161Count > 1)
        {
            throw new InvalidDataException("The Authenticode signer has multiple RFC 3161 timestamp attributes.");
        }
        return rfc3161Count == 1
            ? AuthenticodeTimestampKind.Rfc3161
            : foundLegacy
                ? AuthenticodeTimestampKind.LegacyAuthenticode
                : AuthenticodeTimestampKind.None;
    }

    private static string ValidateOid(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Split('.').Length < 2
            || value.Split('.').Any(part => !uint.TryParse(part, out _)))
        {
            throw new ArgumentException("A valid Artifact Signing identity EKU OID is required.", parameterName);
        }
        return value;
    }

    private static AuthenticodeSignatureEvidence ToPublicEvidence(NativeSignatureInspection signature) => new(
        checked((int)signature.Index),
        signature.NativeStatus == 0,
        signature.PublisherMatched,
        signature.HasCodeSigningEku,
        signature.DurableIdentityMatched,
        signature.TimestampKind,
        signature.VerifiedAsOfUtc,
        signature.SignerIdentitySha256);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(
        IntPtr providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustSignatureSettings
    {
        public uint Size;
        public uint Index;
        public uint Flags;
        public uint SecondarySignatureCount;
        public uint VerifiedSignatureIndex;
        public IntPtr CryptoPolicy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        public uint Size;
        public NativeFileTime VerifyAsOf;
        public uint CertificateChainCount;
        public IntPtr CertificateChain;
        public uint SignerType;
        public IntPtr SignerInfo;
        public uint Error;
        public uint CounterSignerCount;
        public IntPtr CounterSigners;
        public IntPtr ChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;

        public readonly long ToInt64() => unchecked((long)(((ulong)High << 32) | Low));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificate
    {
        public uint Size;
        public IntPtr CertificateContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptDataBlob
    {
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAlgorithmIdentifier
    {
        public IntPtr ObjectId;
        public CryptDataBlob Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAttributes
    {
        public uint Count;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAttribute
    {
        public IntPtr ObjectId;
        public uint ValueCount;
        public IntPtr Values;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CmsgSignerInfo
    {
        public uint Version;
        public CryptDataBlob Issuer;
        public CryptDataBlob SerialNumber;
        public CryptAlgorithmIdentifier HashAlgorithm;
        public CryptAlgorithmIdentifier HashEncryptionAlgorithm;
        public CryptDataBlob EncryptedHash;
        public CryptAttributes AuthenticatedAttributes;
        public CryptAttributes UnauthenticatedAttributes;
    }

    private sealed record NativeSignatureInspection(
        uint Index,
        int NativeStatus,
        bool PublisherMatched,
        bool HasCodeSigningEku,
        bool DurableIdentityMatched,
        AuthenticodeTimestampKind TimestampKind,
        DateTimeOffset? VerifiedAsOfUtc,
        string? SignerIdentitySha256);

    private sealed record SignatureInspectionResult(
        NativeSignatureInspection Signature,
        uint SecondarySignatureCount);
}

internal static class AuthenticodeTrustPolicy
{
    internal static ModArtifactAuthenticityResult Evaluate(
        AuthenticodeRevocationMode revocationMode,
        DateTimeOffset evaluatedAtUtc,
        IReadOnlyList<AuthenticodeSignatureEvidence> signatures)
    {
        var evidence = new AuthenticodeVerificationEvidence(
            revocationMode,
            evaluatedAtUtc,
            "Not established; WinVerifyTrust evaluated the available revocation data.",
            signatures);
        if (signatures.Count == 0)
        {
            return new(false, "No Authenticode signature was found.", evidence);
        }
        if (signatures.Any(signature => !signature.TrustPolicyPassed))
        {
            return new(false, "WinVerifyTrust rejected one or more Authenticode signatures.", evidence);
        }
        if (signatures.Any(signature => !signature.PublisherMatched))
        {
            return new(false, "An Authenticode signature has an unexpected publisher identity.", evidence);
        }
        if (signatures.Any(signature => !signature.HasCodeSigningEku))
        {
            return new(false, "An Authenticode signer certificate does not contain the code-signing EKU.", evidence);
        }
        if (signatures.Any(signature => !signature.DurableIdentityMatched))
        {
            return new(false, "An Authenticode signer does not match the expected durable Artifact Signing identity.", evidence);
        }
        if (signatures.Any(signature => signature.TimestampKind != AuthenticodeTimestampKind.Rfc3161
            || signature.VerifiedAsOfUtc is null))
        {
            return new(false, "Every Authenticode signature must have a Windows-verified RFC 3161 timestamp.", evidence);
        }

        return new(
            true,
            $"Trusted {signatures.Count} Authenticode signature(s) for the expected publisher.",
            evidence);
    }
}
