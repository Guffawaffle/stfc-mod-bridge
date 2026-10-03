using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class NetnivRepositoryReleaseAdditionalTests
{
    [DataTestMethod]
    [DataRow("asset-id")]
    [DataRow("source-commit")]
    public async Task SameTagIdentityConflictReachesRetentionCheckAndPreservesEarlierDownload(string fault)
    {
        using var api = new Api();
        using var http = new HttpClient(api);
        var reader = new Reader();
        var service = new NetnivRepositoryReleaseService(http, reader);
        var first = await service.DiscoverLatestAsync("stable", new Version(0, 1, 0));
        var originalArchive = api.Archive.ToArray();
        if (fault == "asset-id") api.AssetId++;
        else api.SourceCommit = new string('2', 40);

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => service.DiscoverLatestAsync("stable", new Version(0, 1, 0)));

        // Valid bytes, tag and version ensure this is not an earlier validation failure.
        StringAssert.Contains(exception.Message, "earlier preparation was preserved");
        Assert.AreEqual(2, reader.Reads);
        CollectionAssert.AreEqual(originalArchive, api.Archive);
        var earlier = await service.DownloadAsync(first.ModArtifact.DownloadUri, default);
        CollectionAssert.AreEqual(api.Payload, earlier.Contents);
        Assert.AreEqual(first.ModArtifact.Sha256,
            Convert.ToHexString(SHA256.HashData(earlier.Contents)).ToLowerInvariant());
        Assert.AreEqual(11L, first.ModArtifact.RepositoryRelease!.AssetId);
        Assert.AreEqual(new string('1', 40), first.ModArtifact.RepositoryRelease.SourceCommit);
    }

    [DataTestMethod]
    [DataRow("archive")]
    [DataRow("source-commit")]
    public async Task RestartedRecordedRepairRejectsChangedArchiveOrCommitWithoutRegisteringCandidate(string fault)
    {
        using var api = new Api();
        using var http = new HttpClient(api);
        var reader = new Reader();
        var first = await new NetnivRepositoryReleaseService(http, reader)
            .DiscoverLatestAsync("stable", new Version(0, 1, 0));
        var receipt = new ModInstalledArtifactState(1, Path.GetFullPath("synthetic-game"), "version.dll",
            first.ModArtifact.ExpectedVersion, first.ModArtifact.Size, first.ModArtifact.Sha256,
            DateTimeOffset.UtcNow, null, "netniv", "stable", "netniv.stfc-community-mod",
            RepositoryRelease: first.ModArtifact.RepositoryRelease);
        receipt = JsonSerializer.Deserialize<ModInstalledArtifactState>(JsonSerializer.Serialize(receipt))!;
        if (fault == "archive") api.RepackWithDifferentTimestamp();
        else api.SourceCommit = new string('2', 40);
        api.Paths.Clear();
        var restarted = new NetnivRepositoryReleaseService(http, reader);

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => restarted.DiscoverRecordedAsync(receipt, new Version(0, 1, 0), default));

        // The repacked archive has matching current API size/hash and unchanged
        // payload/version. Only its persisted release observation should refuse it.
        StringAssert.Contains(exception.Message, "exact retained identity");
        Assert.AreEqual(2, reader.Reads);
        Assert.IsTrue(api.Paths.Contains("/repos/netniV/stfc-mod/releases/tags/" + Api.Tag));
        Assert.IsFalse(api.Paths.Any(path => path.EndsWith("/latest", StringComparison.Ordinal)));
        var calls = api.Paths.Count;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => restarted.DownloadAsync(first.ModArtifact.DownloadUri, default));
        Assert.AreEqual(calls, api.Paths.Count, "Rejected repair must not register download authority.");
    }

    [DataTestMethod]
    [DataRow("metadata")]
    [DataRow("archive")]
    public async Task OversizedDeclaredResponseIsRejectedBeforeItsBodyIsOpened(string target)
    {
        var limit = target == "metadata" ? 1024L * 1024 : 128L * 1024 * 1024;
        using var content = new HeaderOnlyOversizedContent(limit + 1);
        using var api = new Api
        {
            OverridePath = target == "metadata" ? "/repos/netniV/stfc-mod/releases/latest" : Api.ArchiveUri.AbsolutePath,
            OverrideContent = content,
        };
        using var http = new HttpClient(api);
        var reader = new Reader();
        var service = new NetnivRepositoryReleaseService(http, reader);

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => service.DiscoverLatestAsync("stable", new Version(0, 1, 0)));

        StringAssert.Contains(exception.Message, "response exceeds its bound");
        Assert.AreEqual(1, api.OverrideResponses);
        Assert.AreEqual(0, content.BodyOpens, "No huge body should be generated or buffered.");
        Assert.AreEqual(0, reader.Reads);
        var calls = api.Paths.Count;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => service.DownloadAsync(Api.ArchiveUri, default));
        Assert.AreEqual(calls, api.Paths.Count);
    }

    [TestMethod]
    public async Task MetadataWithNoDeclaredLengthStopsAtBoundInsteadOfReadingLogicalHugeBody()
    {
        const long maximum = 1024 * 1024;
        using var content = new GeneratedContent(long.MaxValue);
        using var api = new Api
        {
            OverridePath = "/repos/netniV/stfc-mod/releases/latest",
            OverrideContent = content,
        };
        using var http = new HttpClient(api);
        var service = new NetnivRepositoryReleaseService(http, new Reader());

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => service.DiscoverLatestAsync("stable", new Version(0, 1, 0)));

        StringAssert.Contains(exception.Message, "response exceeds its bound");
        Assert.IsTrue(content.Source.BytesRead > maximum);
        Assert.IsTrue(content.Source.BytesRead <= maximum + 81920,
            "The logical long.MaxValue body must be stopped at the bounded read, not materialized.");
        var calls = api.Paths.Count;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => service.DownloadAsync(Api.ArchiveUri, default));
        Assert.AreEqual(calls, api.Paths.Count);
    }

    [DataTestMethod]
    [DataRow("missing-owner")]
    [DataRow("assets-not-array")]
    [DataRow("missing-tag-object")]
    [DataRow("id-wrong-kind")]
    [DataRow("malformed-url")]
    public async Task MalformedStructuralMetadataIsCleanlyRejectedWithoutRegisteringDownload(string fault)
    {
        using var api = new Api { StructuralFault = fault };
        using var http = new HttpClient(api);
        var service = new NetnivRepositoryReleaseService(http, new Reader());

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => service.DiscoverLatestAsync("stable", new Version(0, 1, 0)));

        Assert.IsFalse(api.Paths.Contains(Api.ArchiveUri.AbsolutePath));
        var calls = api.Paths.Count;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => service.DownloadAsync(Api.ArchiveUri, default));
        Assert.AreEqual(calls, api.Paths.Count);
    }

    private sealed class Reader : IModArtifactVersionReader
    {
        public int Reads { get; private set; }
        public string? ReadVersion(string path) { Reads++; return File.ReadAllText(path); }
    }

    private sealed class Api : HttpMessageHandler
    {
        public const string Version = "1.1.99.0";
        public const string Tag = "v" + Version;
        public static Uri ArchiveUri => new("https://github.com/netniV/stfc-mod/releases/download/" + Tag + "/stfc-community-mod.zip");
        public byte[] Payload { get; } = Encoding.UTF8.GetBytes(Version);
        public byte[] Archive { get; private set; }
        public long AssetId { get; set; } = 11;
        public string SourceCommit { get; set; } = new('1', 40);
        public List<string> Paths { get; } = [];
        public string? OverridePath { get; init; }
        public HttpContent? OverrideContent { get; init; }
        public int OverrideResponses { get; private set; }
        public string? StructuralFault { get; init; }
        public Api() => Archive = Pack(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        public void RepackWithDifferentTimestamp() => Archive = Pack(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));
        private byte[] Pack(DateTimeOffset timestamp)
        {
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                var entry = archive.CreateEntry("version.dll");
                entry.LastWriteTime = timestamp;
                using var output = entry.Open();
                output.Write(Payload);
            }
            return memory.ToArray();
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            Paths.Add(path);
            if (path == OverridePath)
            {
                OverrideResponses++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = OverrideContent! });
            }
            if (uri == ArchiveUri)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) });
            object model;
            if (uri.Host != "api.github.com") throw new InvalidOperationException("Unexpected request host.");
            if (path == "/repos/netniV/stfc-mod")
                model = new { id = 693298224L, full_name = "netniV/stfc-mod", owner = new { id = 9052188L } };
            else if (path == "/repos/netniV/stfc-mod/git/ref/tags/" + Tag)
                model = new { @object = new { type = "commit", sha = SourceCommit } };
            else if (path is "/repos/netniV/stfc-mod/releases/latest" || path == "/repos/netniV/stfc-mod/releases/tags/" + Tag)
                model = new { id = 22L, tag_name = Tag, draft = false, prerelease = false,
                    assets = new[] { new { id = AssetId, name = "stfc-community-mod.zip", state = "uploaded",
                        size = Archive.LongLength, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Archive)).ToLowerInvariant(),
                        browser_download_url = ArchiveUri.ToString() } } };
            else throw new InvalidOperationException("Unexpected request path: " + path);
            var json = JsonSerializer.SerializeToNode(model)!.AsObject();
            if (path == "/repos/netniV/stfc-mod" && StructuralFault == "missing-owner") json.Remove("owner");
            if (path == "/repos/netniV/stfc-mod" && StructuralFault == "id-wrong-kind") json["id"] = "not-a-number";
            if (path.Contains("/git/ref/tags/", StringComparison.Ordinal) && StructuralFault == "missing-tag-object") json.Remove("object");
            if (path.EndsWith("/latest", StringComparison.Ordinal) && StructuralFault == "assets-not-array") json["assets"] = new JsonObject();
            if (path.EndsWith("/latest", StringComparison.Ordinal) && StructuralFault == "malformed-url") json["assets"]![0]!["browser_download_url"] = "https://[";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString()) });
        }
    }

    private sealed class HeaderOnlyOversizedContent : HttpContent
    {
        private readonly long length;
        public int BodyOpens { get; private set; }
        public HeaderOnlyOversizedContent(long length) { this.length = length; Headers.ContentLength = length; }
        protected override bool TryComputeLength(out long computed) { computed = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { BodyOpens++; throw new InvalidOperationException("Oversized body must not be materialized."); }
        protected override Task<Stream> CreateContentReadStreamAsync()
        { BodyOpens++; throw new InvalidOperationException("Oversized body must not be opened."); }
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();
    }

    private sealed class GeneratedContent(long logicalLength) : HttpContent
    {
        public CountingGeneratedStream Source { get; } = new(logicalLength);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Use streaming access.");
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(Source);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();
    }

    private sealed class CountingGeneratedStream(long logicalLength) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, logicalLength - BytesRead);
            buffer.AsSpan(offset, read).Fill((byte)' ');
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, logicalLength - BytesRead);
            buffer.Span[..read].Fill((byte)' ');
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
