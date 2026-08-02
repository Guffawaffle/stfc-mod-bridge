using System.Text;
using System.Text.Json.Nodes;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class WindowsReleaseManifestTests
{
    private const string Repository = "Guffawaffle/stfc-mod";

    [TestMethod]
    public void ActiveStableManifestSelectsPinnedWindowsDll()
    {
        using var stream = JsonStream(Manifest());

        var manifest = WindowsReleaseManifestParser.Parse(stream);
        var artifact = WindowsReleaseSelectionPolicy.SelectModArtifact(
            manifest,
            "stable",
            new Version(0, 1, 0),
            Repository);

        Assert.AreEqual("2.1.0-guffa.8", manifest.ReleaseVersion);
        Assert.AreEqual("2.1.0.8", artifact.ExpectedVersion);
        Assert.AreEqual(123L, artifact.Size);
        Assert.AreEqual(new string('a', 64), artifact.Sha256);
        Assert.AreEqual(
            "https://github.com/Guffawaffle/stfc-mod/releases/download/v2.1.0-guffa.8/version.dll",
            artifact.DownloadUri.AbsoluteUri);
    }

    [DataTestMethod]
    [DataRow("2.1.0", "2.1.0.0")]
    [DataRow("2.1.0-guffa.8", "2.1.0.8")]
    [DataRow("2.1.0-guffa.rc9", "2.1.0.9")]
    [DataRow("2.1.0-rc.9", "2.1.0.9")]
    [DataRow("2.1.0.alpha.3", "2.1.0.3")]
    [DataRow("2.1.0.beta.4", "2.1.0.4")]
    public void ReleaseVersionsMapToNumericFileVersions(string releaseVersion, string expected)
    {
        Assert.AreEqual(expected, WindowsReleaseSelectionPolicy.DeriveEmbeddedFileVersion(releaseVersion));
    }

    [TestMethod]
    public void UnknownRootPropertyFailsClosed()
    {
        using var stream = JsonStream(Manifest().Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1, \"surprise\": true,",
            StringComparison.Ordinal));

        Assert.ThrowsException<InvalidDataException>(() => WindowsReleaseManifestParser.Parse(stream));
    }

    [DataTestMethod]
    [DataRow("\"schemaVersion\": 1", "\"schemaVersion\": 2")]
    [DataRow("\"schemaVersion\": 1", "\"schemaVersion\": true")]
    [DataRow("\"releaseVersion\": \"2.1.0-guffa.8\"", "\"releaseVersion\": \"2.1.0-guffa.7\"")]
    [DataRow("\"fileName\": \"version.dll\"", "\"fileName\": \"../version.dll\"")]
    [DataRow("\"sha256\": \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", "\"sha256\": \"NOPE\"")]
    public void InvalidManifestIdentityFailsClosed(string oldValue, string newValue)
    {
        using var stream = JsonStream(Manifest().Replace(oldValue, newValue, StringComparison.Ordinal));

        Assert.ThrowsException<InvalidDataException>(() => WindowsReleaseManifestParser.Parse(stream));
    }

    [DataTestMethod]
    [DataRow("\"releaseState\": \"active\"", "\"releaseState\": \"withdrawn\"", "stable", "0.1.0")]
    [DataRow("\"channel\": \"stable\"", "\"channel\": \"preview\"", "stable", "0.1.0")]
    [DataRow("\"minimumLauncherVersion\": \"0.1.0\"", "\"minimumLauncherVersion\": \"9.0.0\"", "stable", "0.1.0")]
    [DataRow("\"repository\": \"Guffawaffle/stfc-mod\"", "\"repository\": \"attacker/stfc-mod\"", "stable", "0.1.0")]
    [DataRow("\"scheme\": \"authenticode\"", "\"scheme\": \"none\"", "stable", "0.1.0")]
    public void IneligibleReleaseOrArtifactCannotBeSelected(
        string oldValue,
        string newValue,
        string selectedChannel,
        string launcherVersion)
    {
        using var stream = JsonStream(Manifest().Replace(oldValue, newValue, StringComparison.Ordinal));
        var manifest = WindowsReleaseManifestParser.Parse(stream);

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsReleaseSelectionPolicy.SelectModArtifact(
                manifest,
                selectedChannel,
                Version.Parse(launcherVersion),
                Repository));
    }

    [TestMethod]
    public void DirectDllCannotBorrowArchiveSignedFilesPolicy()
    {
        using var stream = JsonStream(Manifest().Replace(
            "\"scope\": \"artifact\"",
            "\"scope\": \"artifact\", \"signedFiles\": [\"version.dll\"]",
            StringComparison.Ordinal));
        var manifest = WindowsReleaseManifestParser.Parse(stream);

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsReleaseSelectionPolicy.SelectModArtifact(
                manifest,
                "stable",
                new Version(0, 1, 0),
                Repository));
    }

    [TestMethod]
    public void DuplicateArtifactIdentityFailsClosed()
    {
        var duplicate = JsonNode.Parse(Manifest())!.AsObject();
        var artifacts = duplicate["artifacts"]!.AsArray();
        artifacts.Add(artifacts[0]!.DeepClone());
        using var stream = JsonStream(duplicate.ToJsonString());

        Assert.ThrowsException<InvalidDataException>(() => WindowsReleaseManifestParser.Parse(stream));
    }

    private static MemoryStream JsonStream(string value) => new(Encoding.UTF8.GetBytes(value));

    private static string Manifest() => $$"""
        {
          "schemaVersion": 1,
          "releaseVersion": "2.1.0-guffa.8",
          "tag": "v2.1.0-guffa.8",
          "channel": "stable",
          "releaseState": "active",
          "minimumLauncherVersion": "0.1.0",
          "source": {
            "repository": "Guffawaffle/stfc-mod",
            "targetCommit": "0123456789abcdef0123456789abcdef01234567"
          },
          "manifestAuthenticity": {
            "scheme": "none"
          },
          "artifacts": [
        {{ArtifactJson()}}
          ]
        }
        """;

    private static string ArtifactJson() => """
            {
              "id": "windows-mod-dll-x64",
              "kind": "windows-mod",
              "platform": "windows",
              "architecture": "x64",
              "fileName": "version.dll",
              "mediaType": "application/vnd.microsoft.portable-executable",
              "size": 123,
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "authenticity": {
                "scheme": "authenticode",
                "scope": "artifact"
              }
            }
        """;
}
