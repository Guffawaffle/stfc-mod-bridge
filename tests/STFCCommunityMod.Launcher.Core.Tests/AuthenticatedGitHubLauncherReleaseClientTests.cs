using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class AuthenticatedGitHubLauncherReleaseClientTests
{
    private const string Repository = "Guffawaffle/stfc-mod-bridge";
    private const string ManifestName = "stfc-mod-bridge-release-manifest.json";
    private const string BundleName = "stfc-mod-bridge-release-selection-attestation.json";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private static readonly DateTimeOffset LocalNow = Utc(2026, 8, 6, 10, 5);
    private static readonly DateTimeOffset RekorTime = Utc(2026, 8, 6, 10, 0);

    [TestMethod]
    public async Task ValidEvidenceUsesDerivedUrlsAndAdvancesAuthenticatedState()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var handler = new AuthenticatedRouteHandler(Releases(browserHost: "attacker.invalid"), Manifest(), Bundle());
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, temporaryDirectory.Path);

        var result = await client.DiscoverLatestAsync("stable", new Version(0, 1, 0));

        Assert.AreEqual("0.2.0", result.LauncherArtifact.ReleaseVersion);
        Assert.AreEqual(
            $"https://github.com/{Repository}/releases/download/v0.2.0/{ManifestName}",
            handler.Requests[1].AbsoluteUri);
        Assert.AreEqual(
            $"https://github.com/{Repository}/releases/download/v0.2.0/{BundleName}",
            handler.Requests[2].AbsoluteUri);
        Assert.IsNotNull(result.Authentication);
        StringAssert.Contains(result.Authentication.Summary, "proves origin and byte integrity, not software safety");
        var state = new AuthenticatedReleaseStateStore(temporaryDirectory.Path).Load("stable");
        Assert.IsNotNull(state);
        Assert.AreEqual(42L, state.HighestReleaseSequence);
    }

    [TestMethod]
    public async Task WrongAuthorityAndPostVerificationSubstitutionFailClosedWithoutStateAdvance()
    {
        foreach (var mutate in new Action<FakeVerifier>[]
                 {
                     verifier => verifier.ReceiptMutation = receipt => receipt with { RepositoryId = "1" },
                     verifier => verifier.MutateManifestAfterVerification = true,
                 })
        {
            using var temporaryDirectory = new TemporaryDirectory();
            var healthyArtifact = Path.Combine(temporaryDirectory.Path, "healthy-version.dll");
            File.WriteAllText(healthyArtifact, "keep-installed");
            var handler = new AuthenticatedRouteHandler(Releases(), Manifest(), Bundle());
            using var httpClient = new HttpClient(handler);
            var verifier = new FakeVerifier();
            mutate(verifier);
            var client = CreateClient(httpClient, temporaryDirectory.Path, verifier);

            await Assert.ThrowsExceptionAsync<InvalidDataException>(
                () => client.DiscoverLatestAsync("stable", new Version(0, 1, 0)));

            Assert.IsNull(new AuthenticatedReleaseStateStore(temporaryDirectory.Path).Load("stable"));
            Assert.AreEqual("keep-installed", File.ReadAllText(healthyArtifact));
            var evidenceRoot = Path.Combine(temporaryDirectory.Path, "release-authentication");
            Assert.IsTrue(!Directory.Exists(evidenceRoot) || Directory.GetDirectories(evidenceRoot).Length == 0);
        }
    }

    [TestMethod]
    public async Task StaleOrUnavailableEvidenceLeavesNoAcceptedRelease()
    {
        foreach (var scenario in new[] { "stale", "missing-bundle" })
        {
            using var temporaryDirectory = new TemporaryDirectory();
            var handler = new AuthenticatedRouteHandler(
                Releases(),
                scenario == "stale" ? Manifest(expiresAt: "2026-08-05T09:30:00Z") : Manifest(),
                Bundle())
            {
                BundleStatusCode = scenario == "missing-bundle" ? HttpStatusCode.NotFound : HttpStatusCode.OK,
            };
            using var httpClient = new HttpClient(handler);

            await Assert.ThrowsExceptionAsync<InvalidDataException>(
                () => CreateClient(httpClient, temporaryDirectory.Path)
                    .DiscoverLatestAsync("stable", new Version(0, 1, 0)));

            Assert.IsNull(new AuthenticatedReleaseStateStore(temporaryDirectory.Path).Load("stable"));
        }
    }

    [TestMethod]
    public async Task UnsupportedEvidenceRedirectIsRejectedBeforeItsBodyIsRead()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var handler = new AuthenticatedRouteHandler(Releases(), Manifest(), Bundle())
        {
            FinalManifestUri = new("https://attacker.invalid/substituted-manifest"),
        };
        using var httpClient = new HttpClient(handler);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => CreateClient(httpClient, temporaryDirectory.Path)
                .DiscoverLatestAsync("stable", new Version(0, 1, 0)));

        Assert.IsNull(new AuthenticatedReleaseStateStore(temporaryDirectory.Path).Load("stable"));
    }

    [TestMethod]
    public async Task InstalledVerifierDigestMismatchFailsBeforeSignatureOrProcessExecution()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var helperPath = Path.Combine(
            temporaryDirectory.Path,
            ModBridgeProductIdentity.ReleaseVerifierExecutableName);
        File.WriteAllBytes(helperPath, [1, 2, 3]);
        var authenticity = new CountingAuthenticityVerifier();
        var verifier = new InstalledReleaseSelectionEvidenceVerifier(
            helperPath,
            new string('0', 64),
            authenticity,
            TimeSpan.FromSeconds(5));
        var request = ReleaseSelectionAttestationPolicy.CreateRequest(
            Path.Combine(temporaryDirectory.Path, ManifestName),
            Path.Combine(temporaryDirectory.Path, BundleName),
            "v0.2.0");

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => verifier.VerifyAsync(request, CancellationToken.None));

        Assert.AreEqual(0, authenticity.CallCount);
    }

    [TestMethod]
    public async Task LegacyManifestCannotEnterAuthenticatedStandaloneDiscovery()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var legacy = Manifest().Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1", StringComparison.Ordinal);
        var handler = new AuthenticatedRouteHandler(Releases(), legacy, Bundle());
        using var httpClient = new HttpClient(handler);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => CreateClient(httpClient, temporaryDirectory.Path)
                .DiscoverLatestAsync("stable", new Version(0, 1, 0)));
    }

    [DataTestMethod]
    [DataRow(43L, true)]
    [DataRow(41L, false)]
    public async Task AuthenticatedCandidateSequenceMustAgreeWithSemanticVersionOrder(
        long newerVersionSequence,
        bool succeeds)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var releases = ReleasesFor("v0.2.0", "v0.3.0");
        var manifests = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v0.2.0"] = Manifest("0.2.0", 42),
            ["v0.3.0"] = Manifest("0.3.0", newerVersionSequence),
        };
        var handler = new AuthenticatedRouteHandler(releases, manifests, Bundle());
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, temporaryDirectory.Path);

        if (succeeds)
        {
            var result = await client.DiscoverLatestAsync("stable", new Version(0, 1, 0));
            Assert.AreEqual("0.3.0", result.LauncherArtifact.ReleaseVersion);
            Assert.AreEqual(43L, result.Authentication!.Acceptance.Manifest.ReleaseSequence);
        }
        else
        {
            await Assert.ThrowsExceptionAsync<InvalidDataException>(
                () => client.DiscoverLatestAsync("stable", new Version(0, 1, 0)));
            Assert.IsNull(new AuthenticatedReleaseStateStore(temporaryDirectory.Path).Load("stable"));
        }
    }

    [DataTestMethod]
    [DataRow("v0.2.0", true)]
    [DataRow("v0.2.0-rc.1", false)]
    [DataRow("v0.2.0-rc.01", true)]
    [DataRow("release-0.2.0", false)]
    public async Task DiscoveryRequiresCanonicalTagAndMatchingPrereleaseMetadata(
        string tag,
        bool prerelease)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var handler = new AuthenticatedRouteHandler(Releases(tag: tag, prerelease: prerelease), Manifest(), Bundle());
        using var httpClient = new HttpClient(handler);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => CreateClient(httpClient, temporaryDirectory.Path)
                .DiscoverLatestAsync("stable", new Version(0, 1, 0)));
    }

    private static AuthenticatedGitHubLauncherReleaseClient CreateClient(
        HttpClient httpClient,
        string stateDirectory,
        FakeVerifier? verifier = null) =>
        new(httpClient, stateDirectory, verifier ?? new FakeVerifier(), new FakeStorageSecurity(), () => LocalNow);

    private static string Releases(
        string tag = "v0.2.0",
        bool prerelease = false,
        string browserHost = "github.com") => $$"""
        [{
          "tag_name": "{{tag}}",
          "draft": false,
          "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
          "assets": [
            {
              "name": "{{ManifestName}}",
              "browser_download_url": "https://{{browserHost}}/arbitrary/manifest"
            },
            {
              "name": "{{BundleName}}",
              "browser_download_url": "https://{{browserHost}}/arbitrary/bundle"
            }
          ]
        }]
        """;

    private static string ReleasesFor(params string[] tags) =>
        "[" + string.Join(",", tags.Select(tag => $$"""
        {
          "tag_name": "{{tag}}",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "{{ManifestName}}" },
            { "name": "{{BundleName}}" }
          ]
        }
        """)) + "]";

    private static string Manifest(
        string releaseVersion = "0.2.0",
        long releaseSequence = 42,
        string expiresAt = "2026-09-20T09:30:00Z") => $$"""
        {
          "schemaVersion": 2,
          "releaseSequence": {{releaseSequence}},
          "issuedAt": "2026-08-06T09:30:00Z",
          "expiresAt": "{{expiresAt}}",
          "releaseVersion": "{{releaseVersion}}",
          "tag": "v{{releaseVersion}}",
          "channel": "stable",
          "releaseState": "active",
          "minimumLauncherVersion": "0.1.0",
          "source": { "repository": "{{Repository}}", "targetCommit": "{{Commit}}" },
          "manifestAuthenticity": { "scheme": "github-sigstore-build-provenance-v1" },
          "artifacts": [
            {
              "id": "windows-mod-bridge-archive-x64",
              "kind": "windows-mod-bridge",
              "platform": "windows",
              "architecture": "x64",
              "fileName": "stfc-mod-bridge-win-x64.zip",
              "mediaType": "application/zip",
              "size": 123,
              "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
              "authenticity": {
                "scheme": "authenticode",
                "scope": "contents",
                "signedFiles": ["STFCModBridge.exe", "STFCModBridge.ReleaseVerifier.exe", "STFCModBridge.Updater.exe"]
              }
            },
            {
              "id": "windows-mod-bridge-msix-x64",
              "kind": "windows-mod-bridge-package",
              "platform": "windows",
              "architecture": "x64",
              "fileName": "STFCModBridge.msix",
              "mediaType": "application/msix",
              "size": 456,
              "sha256": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
              "authenticity": { "scheme": "authenticode", "scope": "artifact" }
            }
          ],
          "withdrawals": []
        }
        """;

    private static byte[] Bundle() => Encoding.UTF8.GetBytes("fixture-bundle");

    private sealed class FakeVerifier : IReleaseSelectionEvidenceVerifier
    {
        public Func<ReleaseSelectionVerificationReceipt, ReleaseSelectionVerificationReceipt>? ReceiptMutation
        {
            get;
            set;
        }

        public bool MutateManifestAfterVerification { get; set; }

        public Task<ReleaseSelectionVerificationReceipt> VerifyAsync(
            ReleaseSelectionVerificationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestDigest = Digest(File.ReadAllBytes(request.ManifestPath));
            var bundleDigest = Digest(File.ReadAllBytes(request.BundlePath));
            var receipt = new ReleaseSelectionVerificationReceipt(
                1,
                true,
                "offline",
                Repository,
                "1320037274",
                "105761663",
                ".github/workflows/release.yml",
                $"refs/tags/{request.ExpectedTag}",
                Commit,
                "push",
                "github-hosted",
                "https://in-toto.io/Statement/v1",
                "https://slsa.dev/provenance/v1",
                "https://actions.github.io/buildtypes/workflow/v1",
                ManifestName,
                manifestDigest,
                bundleDigest,
                1,
                "844a1c6de3986c9f02070266b25e0d1a2fa99ceccc89f6b9ad90aae47b62a16e",
                "https://token.actions.githubusercontent.com",
                $"https://github.com/{Repository}/.github/workflows/release.yml@refs/tags/{request.ExpectedTag}",
                [new("c0d23d6ad406973f9559f3ba2d1ca01f84147d8ffc5b8445c224f98b9591801d", 1, RekorTime)],
                ReleaseSelectionAttestationPolicy.RequiredChecks);
            if (MutateManifestAfterVerification)
            {
                File.AppendAllText(request.ManifestPath, " ");
            }
            return Task.FromResult(ReceiptMutation?.Invoke(receipt) ?? receipt);
        }
    }

    private sealed class FakeStorageSecurity : IReleaseEvidenceStorageSecurity
    {
        public void SecureDirectory(string directory) => Directory.CreateDirectory(directory);
    }

    private sealed class CountingAuthenticityVerifier : IModArtifactAuthenticityVerifier
    {
        public int CallCount { get; private set; }

        public ModArtifactAuthenticityResult Verify(string artifactPath)
        {
            _ = artifactPath;
            CallCount++;
            return new(true, "trusted");
        }
    }

    private sealed class AuthenticatedRouteHandler(
        string releases,
        string? manifest,
        byte[] bundle) : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string>? manifestsByTag;

        public AuthenticatedRouteHandler(
            string releases,
            IReadOnlyDictionary<string, string> manifestsByTag,
            byte[] bundle) : this(releases, (string?)null, bundle)
        {
            this.manifestsByTag = manifestsByTag;
        }

        public HttpStatusCode BundleStatusCode { get; init; } = HttpStatusCode.OK;

        public Uri? FinalManifestUri { get; init; }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            var response = request.RequestUri!.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releases, Encoding.UTF8, "application/json"),
                }
                : request.RequestUri.AbsolutePath.EndsWith(BundleName, StringComparison.Ordinal)
                    ? new HttpResponseMessage(BundleStatusCode) { Content = new ByteArrayContent(bundle) }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            manifest ?? manifestsByTag!.Single(pair =>
                                request.RequestUri.AbsolutePath.Contains(
                                    $"/{pair.Key}/",
                                    StringComparison.Ordinal)).Value,
                            Encoding.UTF8,
                            "application/json"),
                    };
            response.RequestMessage = FinalManifestUri is not null
                && request.RequestUri.AbsolutePath.EndsWith(ManifestName, StringComparison.Ordinal)
                    ? new HttpRequestMessage(HttpMethod.Get, FinalManifestUri)
                    : request;
            return Task.FromResult(response);
        }
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
