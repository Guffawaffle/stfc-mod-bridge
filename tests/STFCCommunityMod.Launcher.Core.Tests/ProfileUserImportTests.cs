using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ProfileUserImportTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string SourceSid = "S-1-5-21-101-202-303-1002";
    private const string DestinationSid = "S-1-5-21-101-202-303-1001";
    private static ProfileUserImportPlan Plan(bool elevation = true) =>
        new(SourceSid, "other-user", DestinationSid, "destination-user", "Main", "", elevation,
            elevation ? "Windows needs administrator approval to read this user’s saved STFC data." : "");

    [TestMethod]
    public async Task PreparingAnImportDoesNotPublishOrChangeTheLaunchSelection()
    {
        using var temporary = new TemporaryDirectory();
        var transport = new RecordingTransport(request => request.Operation switch
        {
            "paths" => new(true, Profile: Profile()),
            "prepare-user-import" => new(true, ImportPlan: Plan()),
            _ => throw new AssertFailedException("Unexpected operation: " + request.Operation),
        });
        var store = new NativeLauncherProfilesStore(temporary.Path, transport);
        await store.SelectAsync(Profile().Id);
        var saved = File.ReadAllBytes(Path.Combine(temporary.Path, "profile-ui-selection.json"));
        var prepared = await store.PrepareUserImportAsync(SourceSid, "Main", "");
        Assert.AreEqual(DestinationSid, prepared.DestinationUserSid);
        Assert.IsTrue(prepared.RequiresElevation);
        Assert.IsFalse(transport.Requests.Last().AllowElevation);
        CollectionAssert.AreEqual(saved, File.ReadAllBytes(Path.Combine(temporary.Path, "profile-ui-selection.json")));
        Assert.IsFalse(transport.Requests.Any(request => request.Operation is "create" or "import-user"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ImportBindsReviewedSourceAndDestinationWithoutWritingSelection(bool elevation)
    {
        using var temporary = new TemporaryDirectory();
        var transport = new RecordingTransport(_ => new(true, Profile: Profile()));
        var store = new NativeLauncherProfilesStore(temporary.Path, transport, temporary.Path);
        var imported = await store.ImportUserAsync(Plan(elevation));
        var request = transport.Requests.Single();
        Assert.AreEqual("import-user", request.Operation);
        Assert.AreEqual(SourceSid, request.SourceUserSid);
        Assert.AreEqual(DestinationSid, request.ExpectedDestinationSid);
        Assert.AreEqual(elevation, request.AllowElevation);
        Assert.AreEqual("Main", request.Name);
        Assert.AreEqual(temporary.Path, request.Root);
        Assert.AreEqual(Profile().Id, imported.Id);
        Assert.IsNull(store.LoadSelectedId());
        Assert.IsFalse(File.Exists(Path.Combine(temporary.Path, "profile-ui-selection.json")));
    }

    [TestMethod]
    public async Task CancelledWindowsPromptReturnsNativeExplanationWithoutPublishingUiState()
    {
        using var temporary = new TemporaryDirectory();
        var transport = new RecordingTransport(_ => new(false,
            Error: new("import_cancelled", "Windows approval was cancelled. You can try again.")));
        var store = new NativeLauncherProfilesStore(temporary.Path, transport);
        var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.ImportUserAsync(Plan()));
        StringAssert.Contains(failure.Message, "Windows approval was cancelled");
        Assert.AreEqual(1, transport.Requests.Count);
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(temporary.Path).Any());
    }

    [TestMethod]
    public async Task IncompleteOrMisdirectedPlansAreRejectedBeforeImport()
    {
        using var temporary = new TemporaryDirectory();
        ProfileUserImportPlan? supplied = null;
        var transport = new RecordingTransport(_ => new(true, ImportPlan: supplied));
        var store = new NativeLauncherProfilesStore(temporary.Path, transport);
        foreach (var plan in new[] { null, Plan() with { SourceUserSid = DestinationSid },
            Plan() with { DestinationUserSid = "" }, Plan() with { Reason = "" }, Plan() with { Name = "" } })
        {
            supplied = plan;
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.PrepareUserImportAsync(SourceSid, "Main", ""));
        }
        Assert.IsTrue(transport.Requests.All(request => request.Operation == "prepare-user-import"));
    }

    [TestMethod]
    public async Task SourcesRemainUserBasedWhenBothUsersHaveTheSameCommander()
    {
        using var temporary = new TemporaryDirectory();
        var transport = new RecordingTransport(_ => new(true,
            Users: [new(SourceSid, "other-user"), new(DestinationSid, "destination-user", true)],
            DestinationUser: new(DestinationSid, "destination-user", true)));
        var sources = await new NativeLauncherProfilesStore(temporary.Path, transport).ImportSourcesAsync();
        Assert.AreEqual(2, sources.Users.Count);
        Assert.AreEqual(DestinationSid, sources.DestinationUser.Sid);
        Assert.AreEqual("import-sources", transport.Requests.Single().Operation);
    }

    [TestMethod]
    public void ImportJsonUsesExplicitDestinationAndNativeElevationFields()
    {
        var json = JsonSerializer.Serialize(new ProfileCatalogRequest("import-user", SourceUserSid: SourceSid,
            ExpectedDestinationSid: DestinationSid, AllowElevation: true), JsonOptions);
        using var document = JsonDocument.Parse(json);
        Assert.AreEqual(SourceSid, document.RootElement.GetProperty("sourceUserSid").GetString());
        Assert.AreEqual(DestinationSid, document.RootElement.GetProperty("expectedDestinationSid").GetString());
        Assert.IsTrue(document.RootElement.GetProperty("allowElevation").GetBoolean());
    }

    private static LauncherProfile Profile() => new("0123456789abcdef0123456789abcdef", "Main", "");
    private sealed class RecordingTransport(Func<ProfileCatalogRequest, ProfileCatalogResponse> respond) : IProfileCatalogTransport
    {
        public List<ProfileCatalogRequest> Requests { get; } = [];
        public ProfileCatalogResponse Request(ProfileCatalogRequest request)
        {
            Requests.Add(request);
            return respond(request);
        }
    }
}
