using System.Net;
using System.Text;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class GitHubWindowsReleaseClientTests
{
    private const string Repository = "Guffawaffle/stfc-mod";
    private const string ManifestFileName = "stfc-community-mod-release-manifest.json";

    [TestMethod]
    public async Task StableDiscoverySkipsDraftPreviewAndManifestlessReleases()
    {
        var releases = $$"""
            [
              {{ReleaseJson("v9.0.0", draft: true, prerelease: false, includeManifest: true)}},
              {{ReleaseJson("v8.0.0-guffa.rc1", draft: false, prerelease: true, includeManifest: true)}},
              {{ReleaseJson("v2.1.0-guffa.7", draft: false, prerelease: false, includeManifest: false)}},
              {{ReleaseJson("v2.1.0-guffa.8", draft: false, prerelease: false, includeManifest: true)}}
            ]
            """;
        var handler = new RouteHandler(releases, Manifest("2.1.0-guffa.8"));
        using var client = new HttpClient(handler);
        var discoveryClient = CreateDiscoveryClient(client);

        var result = await discoveryClient.DiscoverLatestAsync(
            "stable",
            new Version(0, 1, 0));

        Assert.AreEqual("v2.1.0-guffa.8", result.Manifest.Tag);
        Assert.AreEqual("2.1.0.8", result.ModArtifact.ExpectedVersion);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.IsTrue(handler.Requests.All(request => request.UserAgent.Contains("STFC-Community-Mod-Launcher/0.1")));
    }

    [TestMethod]
    public async Task PreviewDiscoverySelectsOnlyPrerelease()
    {
        var releases = $$"""
            [
              {{ReleaseJson("v2.1.0-guffa.8", draft: false, prerelease: false, includeManifest: true)}},
              {{ReleaseJson("v2.1.0-guffa.rc9", draft: false, prerelease: true, includeManifest: true)}}
            ]
            """;
        var handler = new RouteHandler(releases, Manifest("2.1.0-guffa.rc9", "preview"));
        using var client = new HttpClient(handler);

        var result = await CreateDiscoveryClient(client).DiscoverLatestAsync(
            "preview",
            new Version(0, 1, 0));

        Assert.AreEqual("v2.1.0-guffa.rc9", result.Manifest.Tag);
        Assert.AreEqual("2.1.0.9", result.ModArtifact.ExpectedVersion);
    }

    [TestMethod]
    public async Task ArbitraryManifestAssetUrlIsNeverFollowed()
    {
        var release = ReleaseJson(
            "v2.1.0-guffa.8",
            draft: false,
            prerelease: false,
            includeManifest: true).Replace(
                "https://github.com/Guffawaffle/stfc-mod/releases/download/",
                "https://attacker.invalid/",
                StringComparison.Ordinal);
        var handler = new RouteHandler($"[{release}]", Manifest("2.1.0-guffa.8"));
        using var client = new HttpClient(handler);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            CreateDiscoveryClient(client).DiscoverLatestAsync(
                "stable",
                new Version(0, 1, 0)));

        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task GitHubAndManifestTagsMustMatch()
    {
        var handler = new RouteHandler(
            $"[{ReleaseJson("v2.1.0-guffa.8", false, false, true)}]",
            Manifest("2.1.0-guffa.7"));
        using var client = new HttpClient(handler);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            CreateDiscoveryClient(client).DiscoverLatestAsync(
                "stable",
                new Version(0, 1, 0)));
    }

    [TestMethod]
    public async Task NonSuccessGitHubResponseIsActionable()
    {
        var handler = new RouteHandler("[]", Manifest("2.1.0-guffa.8"))
        {
            ReleasesStatusCode = HttpStatusCode.Forbidden,
        };
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsExceptionAsync<HttpRequestException>(() =>
            CreateDiscoveryClient(client).DiscoverLatestAsync(
                "stable",
                new Version(0, 1, 0)));

        Assert.AreEqual(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    private static string ReleaseJson(
        string tag,
        bool draft,
        bool prerelease,
        bool includeManifest)
    {
        var asset = includeManifest
            ? $$"""
                {
                  "name": "stfc-community-mod-release-manifest.json",
                  "browser_download_url": "https://github.com/Guffawaffle/stfc-mod/releases/download/{{tag}}/stfc-community-mod-release-manifest.json"
                }
                """
            : string.Empty;
        return $$"""
            {
              "tag_name": "{{tag}}",
              "draft": {{draft.ToString().ToLowerInvariant()}},
              "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
              "assets": [{{asset}}]
            }
            """;
    }

    private static GitHubWindowsReleaseClient CreateDiscoveryClient(HttpClient client) =>
        new(client, Repository, ManifestFileName);

    private static string Manifest(string releaseVersion, string channel = "stable") => $$"""
        {
          "schemaVersion": 1,
          "releaseVersion": "{{releaseVersion}}",
          "tag": "v{{releaseVersion}}",
          "channel": "{{channel}}",
          "releaseState": "active",
          "minimumLauncherVersion": "0.1.0",
          "source": {
            "repository": "Guffawaffle/stfc-mod",
            "targetCommit": "0123456789abcdef0123456789abcdef01234567"
          },
          "manifestAuthenticity": { "scheme": "none" },
          "artifacts": [
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
          ]
        }
        """;

    private sealed class RouteHandler(string releasesJson, string manifestJson) : HttpMessageHandler
    {
        public HttpStatusCode ReleasesStatusCode { get; init; } = HttpStatusCode.OK;

        public List<(Uri Uri, string UserAgent)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!,
                request.Headers.UserAgent.ToString()));
            var isReleasesRequest = request.RequestUri!.Host == "api.github.com";
            var status = isReleasesRequest ? ReleasesStatusCode : HttpStatusCode.OK;
            var body = isReleasesRequest ? releasesJson : manifestJson;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
            });
        }
    }
}
