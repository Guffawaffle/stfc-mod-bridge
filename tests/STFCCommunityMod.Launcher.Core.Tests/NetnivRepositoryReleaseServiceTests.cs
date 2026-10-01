using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class NetnivRepositoryReleaseServiceTests
{
    [TestMethod]
    public async Task NewStableReleaseDoesNotNeedABundledHashCertification()
    {
        using var api = new Api();
        using var http = new HttpClient(api);
        var service = Service(http);
        var release = await service.DiscoverLatestAsync("stable", new Version(0, 1, 0));
        Assert.AreEqual("1.1.9.0", release.Manifest.ReleaseVersion);
        Assert.IsNotNull(release.ModArtifact.RepositoryRelease);
        var download = await service.DownloadAsync(release.ModArtifact.DownloadUri, default);
        CollectionAssert.AreEqual(api.Latest.Payload, download.Contents);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(download.Contents)).ToLowerInvariant(), release.ModArtifact.Sha256);
    }

    [TestMethod]
    public async Task EarlierPreparationSurvivesNewerDiscoveryAndLocalVerificationMakesNoRequests()
    {
        using var api = new Api();
        using var http = new HttpClient(api);
        var service = Service(http);
        var first = await service.DiscoverLatestAsync("stable", new Version(0, 1, 0));
        api.Latest = new Release("1.1.10.0");
        api.Releases.Add(api.Latest.Tag, api.Latest);
        await service.DiscoverLatestAsync("stable", new Version(0, 1, 0));
        var retained = await service.DownloadAsync(first.ModArtifact.DownloadUri, default);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("1.1.9.0"), retained.Contents);
        using var directory = new TemporaryDirectory();
        var dll = Path.Combine(directory.Path, "version.dll");
        File.WriteAllBytes(dll, retained.Contents);
        var calls = api.Requests.Count;
        Assert.IsTrue(service.Verify(dll).IsTrusted);
        File.AppendAllText(dll, "changed");
        Assert.IsFalse(service.Verify(dll).IsTrusted);
        Assert.AreEqual(calls, api.Requests.Count, "Local verification must be passive.");
        _ = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => service.DownloadAsync(new Uri("https://example.invalid/version.dll"), default));
        Assert.AreEqual(calls, api.Requests.Count, "An unobserved URI must fail before network access.");
    }

    [TestMethod]
    public async Task RestartRepairsTheExactPersistedReleaseWithoutQueryingLatest()
    {
        using var api = new Api();
        using var http = new HttpClient(api);
        var first = await Service(http).DiscoverLatestAsync("stable", new Version(0, 1, 0));
        var artifact = first.ModArtifact;
        var receipt = new ModInstalledArtifactState(1, Path.GetFullPath("synthetic-game"), "version.dll",
            artifact.ExpectedVersion, artifact.Size, artifact.Sha256, DateTimeOffset.UtcNow, null,
            "netniv", "stable", "netniv.stfc-community-mod", ReleaseProductVersion: first.Manifest.Tag,
            RepositoryRelease: artifact.RepositoryRelease);
        receipt = JsonSerializer.Deserialize<ModInstalledArtifactState>(JsonSerializer.Serialize(receipt))!;
        api.Latest = new Release("1.1.10.0");
        api.Requests.Clear();
        var restarted = Service(http);
        var repaired = await restarted.DiscoverRecordedAsync(receipt, new Version(0, 1, 0), default);
        Assert.AreEqual(artifact, repaired.ModArtifact with { RepositoryRelease = artifact.RepositoryRelease });
        Assert.IsFalse(api.Requests.Any(path => path.EndsWith("/latest", StringComparison.Ordinal)));
        Assert.IsTrue(api.Requests.Any(path => path.EndsWith("/tags/v1.1.9.0", StringComparison.Ordinal)));
        var contents = await restarted.DownloadAsync(repaired.ModArtifact.DownloadUri, default);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("1.1.9.0"), contents.Contents);
    }

    [DataTestMethod]
    [DataRow("owner")]
    [DataRow("repository")]
    [DataRow("uri")]
    [DataRow("digest")]
    [DataRow("duplicate")]
    [DataRow("draft")]
    [DataRow("prerelease")]
    [DataRow("asset-size")]
    [DataRow("tag")]
    [DataRow("source")]
    public async Task InvalidReleaseEvidenceNeverRegistersAnArtifact(string fault)
    {
        using var api = new Api { Fault = fault };
        using var http = new HttpClient(api);
        var service = Service(http);
        _ = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => service.DiscoverLatestAsync("stable", new Version(0, 1, 0)));
        var calls = api.Requests.Count;
        _ = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => service.DownloadAsync(api.Latest.Uri, default));
        Assert.AreEqual(calls, api.Requests.Count);
    }

    [DataTestMethod]
    [DataRow("../version.dll")]
    [DataRow("Version.dll")]
    [DataRow("directory/version.dll")]
    [DataRow("extra")]
    [DataRow("duplicate")]
    public async Task ArchivesMustContainExactlyOneRootDll(string shape)
    {
        using var api = new Api();
        api.Latest = new Release("1.1.9.0", shape);
        api.Releases[api.Latest.Tag] = api.Latest;
        using var http = new HttpClient(api);
        _ = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => Service(http).DiscoverLatestAsync("stable", new Version(0, 1, 0)));
    }

    [TestMethod]
    public async Task DownloadRejectsChangedZipAndTagVersionMismatch()
    {
        using var api = new Api();
        using var http = new HttpClient(api);
        var service = Service(http);
        var first = await service.DiscoverLatestAsync("stable", new Version(0, 1, 0));
        api.Latest.Archive[0] ^= 1;
        _ = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => service.DownloadAsync(first.ModArtifact.DownloadUri, default));
        api.Latest = new Release("1.1.9.0");
        api.Latest.Payload[0] ^= 1;
        api.Latest.Rebuild();
        api.Releases[api.Latest.Tag] = api.Latest;
        _ = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => service.DiscoverLatestAsync("stable", new Version(0, 1, 0)));
    }

    [TestMethod]
    public void BundledNetnivPolicyIsAvailableWithoutAStaticReleaseCertificate()
    {
        var catalog = LauncherDistributionProviderTests.LoadFixtureCatalog();
        var netniv = catalog.GetProvider("netniv");
        var binding = LauncherProviderModBinding.Resolve(netniv, netniv.DefaultReleaseChannel);
        Assert.IsTrue(binding.IsAvailable, binding.UnavailableReason);
        Assert.AreEqual(LauncherProviderArtifactTrustKind.GitHubRepositoryRelease, binding.TrustKind);
        Assert.IsNull(binding.ReviewedCertification);
    }

    private static NetnivRepositoryReleaseService Service(HttpClient http) => new(http, new Reader());
    private sealed class Reader : IModArtifactVersionReader
    {
        public string? ReadVersion(string path) => File.ReadAllText(path);
    }
    private sealed class Release
    {
        public Release(string version, string shape = "version.dll")
        {
            Version = version;
            Shape = shape;
            Payload = Encoding.UTF8.GetBytes(version);
            Rebuild();
        }
        public string Version { get; }
        public string Shape { get; }
        public string Tag => "v" + Version;
        public Uri Uri => new("https://github.com/netniV/stfc-mod/releases/download/" + Tag + "/stfc-community-mod.zip");
        public byte[] Payload { get; }
        public byte[] Archive { get; private set; } = [];
        public string Digest { get; private set; } = "";
        public void Rebuild()
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                var name = Shape is "extra" or "duplicate" ? "version.dll" : Shape;
                using (var entry = zip.CreateEntry(name).Open()) entry.Write(Payload);
                if (Shape is "extra" or "duplicate")
                {
                    using var entry = zip.CreateEntry(Shape == "extra" ? "surprise.dll" : "version.dll").Open();
                    entry.Write(Payload);
                }
            }
            Archive = memory.ToArray();
            Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Archive)).ToLowerInvariant();
        }
    }
    private sealed class Api : HttpMessageHandler
    {
        public Api() => Releases.Add(Latest.Tag, Latest);
        public Release Latest { get; set; } = new("1.1.9.0");
        public Dictionary<string, Release> Releases { get; } = new();
        public List<string> Requests { get; } = [];
        public string? Fault { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            object result;
            if (request.RequestUri.Host == "github.com")
            {
                var release = Releases.Values.Single(item => item.Uri == request.RequestUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(release.Archive) });
            }
            if (path == "/repos/netniV/stfc-mod")
                result = new { id = Fault == "repository" ? 1L : 693298224, full_name = "netniV/stfc-mod", owner = new { id = Fault == "owner" ? 1L : 9052188 } };
            else if (path.Contains("/git/ref/tags/", StringComparison.Ordinal))
                result = new { @object = new { type = "commit", sha = Fault == "source" ? "bad" : new string('1', 40) } };
            else
            {
                var release = path.EndsWith("/latest", StringComparison.Ordinal) ? Latest
                    : Releases[Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..])];
                var asset = new { id = 11, name = "stfc-community-mod.zip", state = "uploaded",
                    size = Fault == "asset-size" ? 0 : release.Archive.LongLength,
                    digest = Fault == "digest" ? "sha256:wrong" : release.Digest,
                    browser_download_url = Fault == "uri" ? "https://github.com/other/repository/releases/download/v1.1.9.0/stfc-community-mod.zip" : release.Uri.ToString() };
                result = new { id = 22, tag_name = Fault == "tag" ? "../bad" : release.Tag,
                    draft = Fault == "draft", prerelease = Fault == "prerelease",
                    assets = Fault == "duplicate" ? new[] { asset, asset } : new[] { asset } };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result)) });
        }
    }
}
