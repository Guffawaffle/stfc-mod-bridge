using System.Reflection;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core;

internal static class ReleaseSelectionTrustedRoot
{
    private const string ResourceName = "STFCCommunityMod.Launcher.Core.ReleaseSelectionTrustedRoot.v1.json";

    internal static byte[] GetNormalizedBytes()
    {
        using var stream = typeof(ReleaseSelectionTrustedRoot).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The approved release-selection trust root is not embedded.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length < 2 || bytes[^1] != (byte)'\n' || bytes.AsSpan(0, bytes.Length - 1).Contains((byte)'\n'))
        {
            throw new InvalidDataException("The embedded release-selection trust root is not canonical.");
        }
        var normalized = bytes[..^1];
        var digest = Convert.ToHexString(SHA256.HashData(normalized)).ToLowerInvariant();
        if (!AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
            digest,
            ReleaseSelectionAttestationPolicy.TrustedRootSha256))
        {
            throw new InvalidDataException("The embedded release-selection trust root digest is invalid.");
        }
        return normalized;
    }
}
