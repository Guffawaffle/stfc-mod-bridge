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
        Assert.AreEqual("v1.1.4", certification.Tag);
        Assert.AreEqual("d912611fa1eca49fc54f363bdf8377dfebf8def0", certification.SourceCommit);
        Assert.AreEqual("EDC67ED72E4C942B08AB81D92D23B416F80E250CE5DB151FC4B7781C174D468C", certification.AssetSha256);
        Assert.AreEqual("020C975FD2391DF1814897B9D5F03A55443F99367EA6ACC4065AF7E240D9547A", certification.PayloadSha256);
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
    }

    [TestMethod]
    public async Task UnreviewedNewLatestReleaseFailsClosed()
    {
        var certification = Certification();
        var response = JsonResponse(certification).Replace("v1.1.4", "v1.1.5", StringComparison.Ordinal);
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

    private static ReviewedReleaseCertification Certification(byte[]? archive = null, byte[]? payload = null)
    {
        payload ??= Encoding.UTF8.GetBytes("reviewed dll bytes");
        archive ??= CreateArchive("version.dll", payload);
        return new(
            "netniv",
            "stable",
            "netniv.stfc-community-mod",
            "netniV/stfc-mod",
            "v1.1.4",
            "1.1.4",
            "d912611fa1eca49fc54f363bdf8377dfebf8def0",
            "stfc-community-mod.zip",
            archive.LongLength,
            Convert.ToHexString(SHA256.HashData(archive)),
            "version.dll",
            payload.LongLength,
            Convert.ToHexString(SHA256.HashData(payload)),
            "1.1.4.0",
            DateTimeOffset.Parse("2026-07-19T15:55:25Z", CultureInfo.InvariantCulture));
    }

    private static string JsonResponse(ReviewedReleaseCertification certification) =>
        $$"""
        {
          "tag_name": "{{certification.Tag}}",
          "draft": false,
          "prerelease": false,
          "assets": [{
            "name": "{{certification.AssetName}}",
            "size": {{certification.AssetSize}},
            "digest": "sha256:{{certification.AssetSha256}}",
            "browser_download_url": "{{certification.DownloadUri}}"
          }]
        }
        """;

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
}
