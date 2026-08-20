using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ReviewedReleaseCertificationTests
{
    [TestMethod]
    public void BundledCertificationIsBoundToNetnivStableCoordinates()
    {
        using var stream = File.OpenRead(FixturePath("reviewed-windows-releases.v1.json"));
        var catalog = ReviewedReleaseCertificationCatalogLoader.Load(
            stream,
            LauncherDistributionProviderTests.LoadFixtureCatalog());

        var certification = catalog.Find("netniv", "stable");

        Assert.IsNotNull(certification);
        Assert.AreEqual("v1.1.6.0", certification.Tag);
        Assert.AreEqual("e80a303a9949c89100b6e59b8a5e5cc2271e7144", certification.SourceCommit);
        Assert.AreEqual(9448048, certification.AssetSize);
        Assert.AreEqual("9FDEA8CF4DD25D90A58EEE82952627D97A1B409B899F246C7594D5DC367D20B9", certification.AssetSha256);
        Assert.AreEqual(17920000, certification.PayloadSize);
        Assert.AreEqual("6B4C201D70AF8A00380AF3C07211051C571256640621063FC219A66785BFE4D9", certification.PayloadSha256);
        Assert.AreEqual("1.1.6.0", certification.PayloadVersion);
        Assert.IsNull(certification.RuntimeManifest);
        var historical = catalog.ReleaseEvidence.Single(candidate =>
            candidate.ProviderId == "netniv" && candidate.Tag == "v1.1.4");
        Assert.AreEqual(19630080, historical.PayloadSize);
        Assert.AreEqual(
            "020C975FD2391DF1814897B9D5F03A55443F99367EA6ACC4065AF7E240D9547A",
            historical.PayloadSha256);
        Assert.AreEqual(3, catalog.ReleaseEvidence.Count);
    }

    [TestMethod]
    public async Task ExactLatestReleaseProducesCertifiedDllDeploymentMetadata()
    {
        var certification = Certification();
        var handler = new StaticHandler(JsonResponse(certification));
        var client = new ReviewedGitHubReleaseAssetClient(new(handler), certification);

        var result = await client.DiscoverLatestAsync("stable", new Version(0, 1, 0));

        Assert.AreEqual(certification.Tag, result.Manifest.Tag);
        Assert.AreEqual(certification.PayloadSize, result.ModArtifact.Size);
        Assert.AreEqual(certification.PayloadSha256, result.ModArtifact.Sha256);
        Assert.AreEqual(certification.PayloadVersion, result.ModArtifact.ExpectedVersion);
        Assert.IsNull(result.ModArtifact.RuntimeManifest);
    }

    [TestMethod]
    public async Task ExactLatestReleaseDiscoversSeparatelyCertifiedRuntimeManifest()
    {
        var manifest = Encoding.UTF8.GetBytes("reviewed runtime manifest");
        var certification = Certification() with
        {
            RuntimeManifest = new(
                ArtifactBoundRuntimeManifestParser.ManagedFileName,
                manifest.LongLength,
                Convert.ToHexString(SHA256.HashData(manifest))),
        };
        var client = new ReviewedGitHubReleaseAssetClient(
            new(new StaticHandler(JsonResponse(certification))),
            certification);

        var result = await client.DiscoverLatestAsync("stable", new Version(0, 1, 0));

        var selected = result.ModArtifact.RuntimeManifest;
        Assert.IsNotNull(selected);
        Assert.AreEqual(certification.SourceCommit, selected.ExpectedSourceRevision);
        Assert.AreEqual(certification.Repository, selected.ExpectedRepository);
        Assert.AreEqual(certification.Tag, selected.ExpectedTag);
        Assert.AreEqual(certification.RuntimeManifest.Sha256, selected.Sha256);
    }

    [TestMethod]
    public async Task CertifiedRuntimeManifestMustBePresentExactlyOnce()
    {
        var certification = Certification() with
        {
            RuntimeManifest = new(
                ArtifactBoundRuntimeManifestParser.ManagedFileName,
                12,
                new string('A', 64)),
        };
        var response = JsonResponse(certification, includeRuntimeManifest: false);
        var client = new ReviewedGitHubReleaseAssetClient(
            new(new StaticHandler(response)),
            certification);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => client.DiscoverLatestAsync("stable", new Version(0, 1, 0)));
    }

    [TestMethod]
    public async Task UnreviewedNewLatestReleaseFailsClosed()
    {
        var certification = Certification();
        var response = JsonResponse(certification).Replace("v1.1.6.0", "v1.1.7.0", StringComparison.Ordinal);
        var client = new ReviewedGitHubReleaseAssetClient(new(new StaticHandler(response)), certification);

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => client.DiscoverLatestAsync("stable", new Version(0, 1, 0)));

        StringAssert.Contains(exception.Message, "not yet in the launcher-reviewed allowlist");
    }

    [TestMethod]
    public async Task MutatedLatestReleaseDigestFailsClosed()
    {
        var certification = Certification();
        var response = JsonResponse(certification).Replace(
            certification.AssetSha256,
            new string('0', 64),
            StringComparison.Ordinal);
        var client = new ReviewedGitHubReleaseAssetClient(new(new StaticHandler(response)), certification);

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => client.DiscoverLatestAsync("stable", new Version(0, 1, 0)));

        StringAssert.Contains(exception.Message, "does not match the reviewed certification");
    }

    [TestMethod]
    public async Task ReviewedZipDownloaderExtractsOnlyCertifiedDll()
    {
        var payload = Encoding.UTF8.GetBytes("reviewed dll bytes");
        var archive = CreateArchive("version.dll", payload);
        var certification = Certification(archive, payload);
        var downloader = new ReviewedZipModArtifactDownloader(
            new(new ByteHandler(archive)),
            certification);

        var result = await downloader.DownloadAsync(certification.DownloadUri, CancellationToken.None);

        CollectionAssert.AreEqual(payload, result.Contents);
        Assert.AreEqual(payload.Length, result.DeclaredContentLength);
    }

    [TestMethod]
    public async Task ReviewedZipDownloaderRejectsAdditionalArchiveEntry()
    {
        var payload = Encoding.UTF8.GetBytes("reviewed dll bytes");
        var archive = CreateArchive("version.dll", payload, ("surprise.txt", Encoding.UTF8.GetBytes("no")));
        var certification = Certification(archive, payload);
        var downloader = new ReviewedZipModArtifactDownloader(
            new(new ByteHandler(archive)),
            certification);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => downloader.DownloadAsync(certification.DownloadUri, CancellationToken.None));
    }

    [TestMethod]
    public async Task ReviewedZipDownloaderRejectsArchiveHashMutation()
    {
        var payload = Encoding.UTF8.GetBytes("reviewed dll bytes");
        var archive = CreateArchive("version.dll", payload);
        var certification = Certification(archive, payload);
        var mutatedArchive = (byte[])archive.Clone();
        mutatedArchive[^1] ^= 0x01;
        var downloader = new ReviewedZipModArtifactDownloader(
            new(new ByteHandler(mutatedArchive)),
            certification);

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => downloader.DownloadAsync(certification.DownloadUri, CancellationToken.None));

        StringAssert.Contains(exception.Message, "does not match the reviewed release certification");
    }

    [TestMethod]
    public async Task ReviewedZipDownloaderRoutesOnlyCertifiedCompanionWithoutSecondArchiveDownload()
    {
        var payload = Encoding.UTF8.GetBytes("reviewed dll bytes");
        var runtimeManifest = Encoding.UTF8.GetBytes("reviewed runtime manifest");
        var archive = CreateArchive(
            "version.dll",
            payload,
            (ArtifactBoundRuntimeManifestParser.ManagedFileName, runtimeManifest));
        var certification = Certification(archive, payload) with
        {
            RuntimeManifest = new(
                ArtifactBoundRuntimeManifestParser.ManagedFileName,
                runtimeManifest.LongLength,
                Convert.ToHexString(SHA256.HashData(runtimeManifest))),
        };
        var manifestUri = ReviewedGitHubReleaseAssetClient.RuntimeManifestUri(certification);
        var handler = new RoutingByteHandler(new Dictionary<Uri, byte[]>
        {
            [certification.DownloadUri] = archive,
            [manifestUri] = runtimeManifest,
        });
        var downloader = new ReviewedZipModArtifactDownloader(new(handler), certification);

        var dll = await downloader.DownloadAsync(certification.DownloadUri, CancellationToken.None);
        var companion = await downloader.DownloadAsync(manifestUri, CancellationToken.None);

        CollectionAssert.AreEqual(payload, dll.Contents);
        CollectionAssert.AreEqual(runtimeManifest, companion.Contents);
        Assert.AreEqual(1, handler.Requests.Count(uri => uri == certification.DownloadUri));
        Assert.AreEqual(1, handler.Requests.Count(uri => uri == manifestUri));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => downloader.DownloadAsync(
            new Uri("https://example.invalid/runtime.json"),
            CancellationToken.None));
    }

    [TestMethod]
    public async Task ReviewedZipDownloaderRejectsMissingCertifiedRuntimeManifestSidecar()
    {
        var payload = Encoding.UTF8.GetBytes("reviewed dll bytes");
        var runtimeManifest = Encoding.UTF8.GetBytes("reviewed runtime manifest");
        var archive = CreateArchive("version.dll", payload);
        var certification = Certification(archive, payload) with
        {
            RuntimeManifest = new(
                ArtifactBoundRuntimeManifestParser.ManagedFileName,
                runtimeManifest.LongLength,
                Convert.ToHexString(SHA256.HashData(runtimeManifest))),
        };
        var downloader = new ReviewedZipModArtifactDownloader(
            new(new ByteHandler(archive)),
            certification);

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(certification.DownloadUri, CancellationToken.None));

        StringAssert.Contains(exception.Message, "exactly the certified root payloads");
    }

    [TestMethod]
    public async Task ReviewedZipDownloaderRejectsChangedCertifiedRuntimeManifestSidecar()
    {
        var payload = Encoding.UTF8.GetBytes("reviewed dll bytes");
        var runtimeManifest = Encoding.UTF8.GetBytes("reviewed runtime manifest");
        var changedRuntimeManifest = (byte[])runtimeManifest.Clone();
        changedRuntimeManifest[0] ^= 0x01;
        var archive = CreateArchive(
            "version.dll",
            payload,
            (ArtifactBoundRuntimeManifestParser.ManagedFileName, changedRuntimeManifest));
        var certification = Certification(archive, payload) with
        {
            RuntimeManifest = new(
                ArtifactBoundRuntimeManifestParser.ManagedFileName,
                runtimeManifest.LongLength,
                Convert.ToHexString(SHA256.HashData(runtimeManifest))),
        };
        var downloader = new ReviewedZipModArtifactDownloader(
            new(new ByteHandler(archive)),
            certification);

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(certification.DownloadUri, CancellationToken.None));

        StringAssert.Contains(exception.Message, "runtime manifest does not match its certified bytes");
    }

    [TestMethod]
    public async Task ReviewedZipDownloaderRejectsNestedCertifiedRuntimeManifestSidecar()
    {
        var payload = Encoding.UTF8.GetBytes("reviewed dll bytes");
        var runtimeManifest = Encoding.UTF8.GetBytes("reviewed runtime manifest");
        var archive = CreateArchive(
            "version.dll",
            payload,
            ($"nested/{ArtifactBoundRuntimeManifestParser.ManagedFileName}", runtimeManifest));
        var certification = Certification(archive, payload) with
        {
            RuntimeManifest = new(
                ArtifactBoundRuntimeManifestParser.ManagedFileName,
                runtimeManifest.LongLength,
                Convert.ToHexString(SHA256.HashData(runtimeManifest))),
        };
        var downloader = new ReviewedZipModArtifactDownloader(
            new(new ByteHandler(archive)),
            certification);

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(certification.DownloadUri, CancellationToken.None));

        StringAssert.Contains(exception.Message, "exactly the certified root payloads");
    }

    [TestMethod]
    public async Task ReviewedZipDownloaderRejectsChangedCertifiedCompanion()
    {
        var payload = Encoding.UTF8.GetBytes("reviewed dll bytes");
        var runtimeManifest = Encoding.UTF8.GetBytes("reviewed runtime manifest");
        var changedManifest = (byte[])runtimeManifest.Clone();
        changedManifest[0] ^= 0x01;
        var archive = CreateArchive("version.dll", payload);
        var certification = Certification(archive, payload) with
        {
            RuntimeManifest = new(
                ArtifactBoundRuntimeManifestParser.ManagedFileName,
                runtimeManifest.LongLength,
                Convert.ToHexString(SHA256.HashData(runtimeManifest))),
        };
        var manifestUri = ReviewedGitHubReleaseAssetClient.RuntimeManifestUri(certification);
        var downloader = new ReviewedZipModArtifactDownloader(
            new(new RoutingByteHandler(new Dictionary<Uri, byte[]>
            {
                [manifestUri] = changedManifest,
            })),
            certification);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(manifestUri, CancellationToken.None));
    }

    [TestMethod]
    public async Task MissingManifestUsesReviewedFallback()
    {
        var certification = Certification();
        var expected = Discovery(certification);
        var fallback = new RecordingDiscoveryClient(expected);
        var client = new ManifestWithReviewedFallbackReleaseClient(
            new RecordingDiscoveryClient(
                ReleaseManifestFallbackPolicy.MissingManifest("No manifest asset.")),
            fallback,
            certification);

        var result = await client.DiscoverLatestAsync("stable", new Version(0, 1, 0));

        Assert.AreEqual(1, fallback.CallCount);
        Assert.AreEqual(certification.PayloadSha256, result.ModArtifact.Sha256);
        Assert.AreEqual(certification.Tag, result.ModArtifact.ExpectedProductVersion);
    }

    [TestMethod]
    public async Task InvalidManifestDoesNotDowngradeToReviewedFallback()
    {
        var fallback = new RecordingDiscoveryClient(Discovery(Certification()));
        var client = new ManifestWithReviewedFallbackReleaseClient(
            new RecordingDiscoveryClient(
                new InvalidDataException("Manifest signature is invalid.")),
            fallback);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => client.DiscoverLatestAsync("stable", new Version(0, 1, 0)));

        Assert.AreEqual(0, fallback.CallCount);
    }

    [TestMethod]
    public async Task ExactReviewedManifestUsesCertifiedPayloadVersion()
    {
        var runtimeManifest = new ReviewedRuntimeManifestCertification(
            ArtifactBoundRuntimeManifestParser.ManagedFileName,
            123,
            new string('A', 64));
        var certification = Certification() with
        {
            Tag = "v1.1.4-guffa.9",
            ReleaseVersion = "1.1.4-guffa.9",
            PayloadVersion = "1.1.4.0",
            RuntimeManifest = runtimeManifest,
        };
        var discovery = ReviewedManifestDiscovery(certification, "1.1.4.9");
        var client = new ManifestWithReviewedFallbackReleaseClient(
            new RecordingDiscoveryClient(discovery),
            new RecordingDiscoveryClient(Discovery(certification)),
            certification);

        var result = await client.DiscoverLatestAsync("stable", new Version(0, 1, 0));

        Assert.AreEqual(certification.PayloadVersion, result.ModArtifact.ExpectedVersion);
        Assert.IsNotNull(result.ModArtifact.RuntimeManifest);
        Assert.AreEqual(runtimeManifest.Sha256, result.ModArtifact.RuntimeManifest.Sha256);
    }

    [TestMethod]
    public async Task NewSignedManifestReleaseIsNotPinnedToReviewedRuntimePair()
    {
        var certification = Certification() with
        {
            Tag = "v1.1.4-guffa.9",
            ReleaseVersion = "1.1.4-guffa.9",
            PayloadVersion = "1.1.4.0",
            RuntimeManifest = new(
                ArtifactBoundRuntimeManifestParser.ManagedFileName,
                123,
                new string('A', 64)),
        };
        var discovery = ReviewedManifestDiscovery(certification, "1.1.4.9") with
        {
            Manifest = ReviewedManifestDiscovery(certification, "1.1.4.9").Manifest with
            {
                Source = new(certification.Repository, new string('f', 40)),
            },
        };
        var fallback = new RecordingDiscoveryClient(Discovery(certification));
        var client = new ManifestWithReviewedFallbackReleaseClient(
            new RecordingDiscoveryClient(discovery),
            fallback,
            certification);

        var result = await client.DiscoverLatestAsync("stable", new Version(0, 1, 0));

        Assert.AreEqual(0, fallback.CallCount);
        Assert.AreEqual("1.1.4.9", result.ModArtifact.ExpectedVersion);
        Assert.IsNull(result.ModArtifact.RuntimeManifest);
    }

    [TestMethod]
    public async Task RuntimeManifestRouteUsesItsDedicatedBoundedDownloader()
    {
        var bytes = new byte[ArtifactBoundRuntimeManifestParser.MaximumManifestBytes + 1];
        var certification = Certification() with
        {
            RuntimeManifest = new(
                ArtifactBoundRuntimeManifestParser.ManagedFileName,
                12,
                new string('A', 64)),
        };
        var downloader = new ManifestWithReviewedFallbackArtifactDownloader(
            new(new ByteHandler(bytes)),
            certification);
        var uri = new Uri(
            $"https://github.com/{certification.Repository}/releases/download/"
            + $"{certification.Tag}/{ArtifactBoundRuntimeManifestParser.ManagedFileName}");

        var result = await downloader.DownloadAsync(uri, CancellationToken.None);

        Assert.AreEqual(bytes.LongLength, result.DeclaredContentLength);
        Assert.AreEqual(0, result.Contents.Length);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(new Uri("https://example.invalid/file.json"), CancellationToken.None));
    }

    [TestMethod]
    public async Task NewSignedReleaseDllUsesTheManifestRepositoryDownloadBoundary()
    {
        var bytes = "new signed release"u8.ToArray();
        var certification = Certification();
        var downloader = new ManifestWithReviewedFallbackArtifactDownloader(
            new(new ByteHandler(bytes)),
            certification);
        var permitted = new Uri(
            $"https://github.com/{certification.Repository}/releases/download/v2.0.0/version.dll");

        var result = await downloader.DownloadAsync(permitted, CancellationToken.None);

        CollectionAssert.AreEqual(bytes, result.Contents);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(
                new Uri("https://github.com/other/repository/releases/download/v2.0.0/version.dll"),
                CancellationToken.None));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(
                new Uri($"https://github.com/{certification.Repository}/releases/download/v2.0.0/other.dll"),
                CancellationToken.None));
    }

    private static ReviewedReleaseCertification Certification(byte[]? archive = null, byte[]? payload = null)
    {
        payload ??= Encoding.UTF8.GetBytes("reviewed dll bytes");
        archive ??= CreateArchive("version.dll", payload);
        return new(
            "netniv",
            "stable",
            "netniv.stfc-community-mod",
            "netniV/stfc-mod",
            "v1.1.6.0",
            "1.1.6.0",
            "e80a303a9949c89100b6e59b8a5e5cc2271e7144",
            "stfc-community-mod.zip",
            archive.LongLength,
            Convert.ToHexString(SHA256.HashData(archive)),
            "version.dll",
            payload.LongLength,
            Convert.ToHexString(SHA256.HashData(payload)),
            "1.1.6.0",
            DateTimeOffset.Parse("2026-08-20T10:24:33Z", CultureInfo.InvariantCulture));
    }

    private static string JsonResponse(
        ReviewedReleaseCertification certification,
        bool includeRuntimeManifest = true)
    {
        var runtimeAsset = certification.RuntimeManifest is not null && includeRuntimeManifest
            ? $$"""
            ,{
              "name": "{{certification.RuntimeManifest.FileName}}",
              "size": {{certification.RuntimeManifest.Size}},
              "digest": "sha256:{{certification.RuntimeManifest.Sha256}}",
              "browser_download_url": "{{ReviewedGitHubReleaseAssetClient.RuntimeManifestUri(certification)}}"
            }
            """
            : string.Empty;
        return $$"""
        {
          "tag_name": "{{certification.Tag}}",
          "draft": false,
          "prerelease": false,
          "assets": [{
            "name": "{{certification.AssetName}}",
            "size": {{certification.AssetSize}},
            "digest": "sha256:{{certification.AssetSha256}}",
            "browser_download_url": "{{certification.DownloadUri}}"
          }{{runtimeAsset}}]
        }
        """;
    }

    private static WindowsReleaseDiscovery Discovery(
        ReviewedReleaseCertification certification) =>
        new(
            new(
                1,
                certification.ReleaseVersion,
                certification.Tag,
                certification.ChannelId,
                "active",
                new Version(0, 1, 0),
                new(certification.Repository, certification.SourceCommit),
                "launcher-reviewed-exact-hash",
                []),
            new(
                certification.DownloadUri,
                certification.PayloadFileName,
                certification.PayloadSize,
                certification.PayloadSha256,
                certification.PayloadVersion));

    private static WindowsReleaseDiscovery ReviewedManifestDiscovery(
        ReviewedReleaseCertification certification,
        string derivedPayloadVersion)
    {
        var runtimeManifest = certification.RuntimeManifest
            ?? throw new AssertFailedException("Test certification requires a runtime manifest.");
        var dllUri = new Uri(
            $"https://github.com/{certification.Repository}/releases/download/"
            + $"{certification.Tag}/{certification.PayloadFileName}");
        return new(
            new(
                1,
                certification.ReleaseVersion,
                certification.Tag,
                certification.ChannelId,
                "active",
                new Version(0, 1, 0),
                new(certification.Repository, certification.SourceCommit),
                "none",
                [
                    new(
                        "windows-mod-runtime-manifest-x64",
                        "windows-mod-runtime-manifest",
                        "windows",
                        "x64",
                        runtimeManifest.FileName,
                        "application/json",
                        runtimeManifest.Size,
                        runtimeManifest.Sha256,
                        new("none", "none", [])),
                ]),
            new(
                dllUri,
                certification.PayloadFileName,
                certification.PayloadSize,
                certification.PayloadSha256,
                derivedPayloadVersion));
    }

    private static byte[] CreateArchive(string name, byte[] contents, params (string Name, byte[] Contents)[] extras)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, name, contents);
            foreach (var extra in extras)
            {
                WriteEntry(archive, extra.Name, extra.Contents);
            }
        }
        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] contents)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var output = entry.Open();
        output.Write(contents);
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Providers", fileName);

    private sealed class StaticHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ByteHandler(byte[] contents) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(contents),
            });
    }

    private sealed class RoutingByteHandler(IReadOnlyDictionary<Uri, byte[]> contents) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(contents[uri]),
            });
        }
    }

    private sealed class RecordingDiscoveryClient : IWindowsReleaseDiscoveryClient
    {
        private readonly WindowsReleaseDiscovery? result;
        private readonly Exception? exception;

        public RecordingDiscoveryClient(WindowsReleaseDiscovery result) =>
            this.result = result;

        public RecordingDiscoveryClient(Exception exception) =>
            this.exception = exception;

        public int CallCount { get; private set; }

        public Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
            string channel,
            Version currentLauncherVersion,
            CancellationToken cancellationToken = default)
        {
            _ = channel;
            _ = currentLauncherVersion;
            _ = cancellationToken;
            ++CallCount;
            return exception is null
                ? Task.FromResult(result!)
                : Task.FromException<WindowsReleaseDiscovery>(exception);
        }
    }
}
