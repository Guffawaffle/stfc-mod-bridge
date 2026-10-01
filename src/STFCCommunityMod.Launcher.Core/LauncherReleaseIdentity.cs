using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherReleaseIdentity(
    string? SourceCommit, string? ReleaseVerifierSha256,
    string? ProfilesNativeSha256 = null, string? TomlNativeSha256 = null)
{
    public bool HasReleaseVerifierPairing =>
        ReleaseVerifierSha256 is not null
        && !ReleaseVerifierSha256.Equals(new string('0', 64), StringComparison.Ordinal);
}

public static partial class LauncherReleaseIdentityParser
{
    [GeneratedRegex(
        "\\+commit\\.(?<commit>unknown|[0-9a-f]{40})\\.verifier\\.(?<verifier>[0-9a-f]{64})(?:\\.profiles\\.(?<profiles>[0-9a-f]{64})(?:\\.toml\\.(?<toml>[0-9a-f]{64}))?)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();

    public static LauncherReleaseIdentity Parse(string? productVersion)
    {
        if (string.IsNullOrWhiteSpace(productVersion))
        {
            return new(null, null);
        }
        var match = IdentityPattern().Match(productVersion.Trim());
        if (!match.Success)
        {
            return new(null, null);
        }
        var commit = match.Groups["commit"].Value;
        return new(
            commit == "unknown" ? null : commit,
            match.Groups["verifier"].Value,
            match.Groups["profiles"].Success ? match.Groups["profiles"].Value : null,
            match.Groups["toml"].Success ? match.Groups["toml"].Value : null);
    }
}
