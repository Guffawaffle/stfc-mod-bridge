using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ProfileCatalogPackageQualificationTests
{
    private const string Package = "Guffawaffle.STFCModBridge_0.1.0.22_x64__a1b2c3d4e5f6g";
    private const string Build = "0.1.0+commit." + "1111111111111111111111111111111111111111"
        + ".verifier." + "2222222222222222222222222222222222222222222222222222222222222222"
        + ".profiles." + "3333333333333333333333333333333333333333333333333333333333333333";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void UnrelatedStartupArgumentsDoNotEnterQualification()
    {
        Assert.IsFalse(ProfileCatalogPackageQualification.TryRun([], out var code));
        Assert.AreEqual(0, code);
    }

    [TestMethod]
    public void ExactArgumentShapeRejectsInvalidNonceModeExtraArgumentAndRelativePath()
    {
        var nonce = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), $"stfc-mod-bridge-profiles-qualification-{nonce}");
        string[][] invalid =
        [
            [ProfileCatalogPackageQualification.Argument],
            [ProfileCatalogPackageQualification.Argument, "other", nonce, path],
            [ProfileCatalogPackageQualification.Argument, "prepare", nonce.ToUpperInvariant(), path],
            [ProfileCatalogPackageQualification.Argument, "prepare", nonce, path, "extra"],
            [ProfileCatalogPackageQualification.Argument, "prepare", nonce, "relative"],
            [ProfileCatalogPackageQualification.Argument, "prepare", Guid.NewGuid().ToString("N"), path],
            [ProfileCatalogPackageQualification.Argument, "prepare", nonce, Path.Combine(path, "..", Path.GetFileName(path))],
        ];
        foreach (var arguments in invalid)
        {
            Assert.IsTrue(ProfileCatalogPackageQualification.TryRun(arguments, out var code));
            Assert.AreEqual(1, code);
        }
        Assert.AreEqual(path, ProfileCatalogPackageQualification.ParseArguments(
            [ProfileCatalogPackageQualification.Argument, "prepare", nonce, path]).Fixture);
    }

    [TestMethod]
    public async Task StandaloneAndMsixStagesRequireTheirActualIdentityBeforeCatalogAccess()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() =>
            ProfileCatalogPackageQualification.RunAsync(fixture.Options("prepare"), fixture.Host(Package)));
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() =>
            ProfileCatalogPackageQualification.RunAsync(fixture.Options("msix"), fixture.Host(null)));
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() =>
            ProfileCatalogPackageQualification.RunAsync(fixture.Options("msix"), fixture.Host("other_0.1.0.22_x64__a1b2c3d4e5f6g")));
        Assert.AreEqual(0, fixture.Transport.Requests.Count);
        Assert.IsFalse(Directory.Exists(fixture.Path));
    }

    [TestMethod]
    public async Task PrepareRefusesAnExistingFixtureWithoutAccessingTheCatalog()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Path);
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "unrelated"), "retain");
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() => fixture.Run("prepare"));
        Assert.AreEqual(0, fixture.Transport.Requests.Count);
        Assert.AreEqual("retain", File.ReadAllText(System.IO.Path.Combine(fixture.Path, "unrelated")));
    }

    [TestMethod]
    public async Task CrossHostFixtureProvesSharedEditsAndBothNativeLeasesThenDeletesOnlyItsProfile()
    {
        using var fixture = new Fixture();
        await fixture.Run("prepare");
        var original = fixture.Transport.Profile!;
        Assert.IsTrue(LauncherProfiles.ValidId(original.Id));
        Assert.AreEqual("Bridge proof " + fixture.Nonce, original.Name);
        Assert.IsFalse(File.Exists(original.ConfigPath));
        var held = fixture.Run("msix", Package);
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(fixture.Path, ProfileCatalogPackageQualification.ReadyFile)));
        Assert.IsTrue(fixture.Transport.DataHeld);
        Assert.IsTrue(fixture.Transport.InstallationHeld);
        await fixture.Run("verify");
        await held;
        Assert.IsFalse(fixture.Transport.DataHeld);
        Assert.IsFalse(fixture.Transport.InstallationHeld);
        Assert.AreEqual("Bridge MSIX " + fixture.Nonce, fixture.Transport.Profile!.Name);
        StringAssert.Contains(File.ReadAllText(fixture.Transport.Profile.ConfigPath), fixture.Nonce);
        Assert.AreEqual(1, fixture.Transport.BusyArchiveCount);
        Assert.AreEqual(1, fixture.Transport.BusyUpdateCount);
        Assert.IsTrue(fixture.Transport.Requests.All(request => request.Root is null));
        Assert.AreEqual(267, fixture.Transport.Requests.Single(request => request.Operation == "update-game").ExpectedVersion);
        await fixture.Run("cleanup");
        Assert.IsNull(fixture.Transport.Profile);
        Assert.AreEqual(1, fixture.Transport.Deletes);
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(fixture.Path, "cleanup-passed.json")));
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(fixture.Path, ProfileCatalogPackageQualification.ReceiptFile)));
    }

    [TestMethod]
    public async Task ChangedDefaultCatalogOrCandidateBuildFailsBeforeProfileMutation()
    {
        using var fixture = new Fixture();
        await fixture.Run("prepare");
        fixture.Transport.ReportedRoot += "-different";
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() => fixture.Run("msix", Package));
        fixture.Transport.ReportedRoot = fixture.Transport.Root;
        var changedBuild = fixture.Host(Package) with { BuildIdentity = Build + ".different" };
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() =>
            ProfileCatalogPackageQualification.RunAsync(fixture.Options("msix"), changedBuild));
        Assert.AreEqual(0, fixture.Transport.Edits);
    }

    [TestMethod]
    public async Task ReceiptCannotNameAnotherIdOrAnotherNonceForCleanup()
    {
        using var fixture = new Fixture();
        await fixture.Run("prepare");
        var receipt = fixture.Receipt();
        fixture.WriteReceipt(receipt with { Nonce = Guid.NewGuid().ToString("N") });
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() => fixture.Run("cleanup"));
        fixture.WriteReceipt(receipt with { Id = new string('f', 32) });
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() => fixture.Run("cleanup"));
        Assert.AreEqual(0, fixture.Transport.Deletes);
        Assert.AreEqual(0, fixture.Transport.SuccessfulArchives);
    }

    [TestMethod]
    public async Task StaleRevisionAndExternalProfileDataAreRetained()
    {
        using var fixture = new Fixture();
        await fixture.Run("prepare");
        var profile = fixture.Transport.Profile!;
        fixture.Transport.Profile = profile with { Revision = new string('a', 64) };
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() => fixture.Run("cleanup"));
        fixture.Transport.Profile = profile;
        File.WriteAllText(System.IO.Path.Combine(profile.Directory, "player_prefs.bin"), "external account data");
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() => fixture.Run("cleanup"));
        Assert.AreEqual("external account data", File.ReadAllText(System.IO.Path.Combine(profile.Directory, "player_prefs.bin")));
        Assert.AreEqual(0, fixture.Transport.SuccessfulArchives);
        Assert.AreEqual(0, fixture.Transport.Deletes);
    }

    [TestMethod]
    public async Task ModifiedSyntheticConfigRefusesCleanupAndRetainsReceipt()
    {
        using var fixture = new Fixture();
        await fixture.Run("prepare");
        var held = fixture.Run("msix", Package);
        await fixture.Run("verify");
        await held;
        File.AppendAllText(fixture.Transport.Profile!.ConfigPath, "# external edit\n");
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() => fixture.Run("cleanup"));
        Assert.AreEqual(0, fixture.Transport.SuccessfulArchives);
        Assert.AreEqual(0, fixture.Transport.Deletes);
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(fixture.Path, ProfileCatalogPackageQualification.ReceiptFile)));
    }

    [TestMethod]
    public async Task MissingNativeTransportIsAnExplicitFailureWithoutCreatingAFallbackCatalog()
    {
        using var fixture = new Fixture();
        fixture.Transport.TransportFailure = new DllNotFoundException("native fixture deliberately unavailable");
        var failure = await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() => fixture.Run("prepare"));
        Assert.AreEqual("catalog-location", failure.Stage);
        Assert.AreEqual(0, fixture.Transport.Creates);
        Assert.IsFalse(Directory.Exists(fixture.Transport.Root));
        Assert.IsTrue(Directory.Exists(fixture.Path));
    }

    [TestMethod]
    public async Task MissingBusyExclusionFailsBeforeVerificationMarkerAndStillReleasesLeases()
    {
        using var fixture = new Fixture();
        await fixture.Run("prepare");
        fixture.Transport.ArchiveError = "stale_revision";
        var held = fixture.Run("msix", Package);
        var failure = await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() => fixture.Run("verify"));
        Assert.AreEqual("profile-data-lease-exclusion", failure.Stage);
        Assert.IsFalse(File.Exists(System.IO.Path.Combine(fixture.Path, ProfileCatalogPackageQualification.VerifiedFile)));
        // Give the holding process a deliberately unbound marker to exercise bounded failure without a 15-second test delay.
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, ProfileCatalogPackageQualification.VerifiedFile), "{}");
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(async () => await held);
        Assert.IsFalse(fixture.Transport.DataHeld);
        Assert.IsFalse(fixture.Transport.InstallationHeld);
        Assert.AreEqual(0, fixture.Transport.Deletes);
        Assert.IsFalse(File.Exists(System.IO.Path.Combine(fixture.Path, ProfileCatalogPackageQualification.ReleasedFile)));
    }

    [TestMethod]
    public async Task UnexpectedGameAssemblyRefusesAnyUpdaterAttempt()
    {
        using var fixture = new Fixture();
        await fixture.Run("prepare");
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "game", "GameAssembly.dll"), "unexpected");
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() => fixture.Run("msix", Package));
        Assert.AreEqual(0, fixture.Transport.Edits);
        Assert.IsFalse(fixture.Transport.Requests.Any(request => request.Operation == "update-game"));
    }

    [TestMethod]
    public async Task FixtureMustBeDirectlyUnderCandidateOutputAndOutsidePhysicalAppData()
    {
        using var fixture = new Fixture();
        var wrongOutput = fixture.Host(null) with { StandaloneOutputRoot = System.IO.Path.GetDirectoryName(fixture.OutputRoot) };
        var failure = await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() =>
            ProfileCatalogPackageQualification.RunAsync(fixture.Options("prepare"), wrongOutput));
        Assert.AreEqual("fixture-boundary", failure.Stage);
        string[] physicalAppData = [fixture.OutputRoot, fixture.OutputRoot + "-roaming", fixture.OutputRoot + "-low"];
        var underAppData = fixture.Host(null) with { PhysicalAppDataDirectories = physicalAppData };
        failure = await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() =>
            ProfileCatalogPackageQualification.RunAsync(fixture.Options("prepare"), underAppData));
        Assert.AreEqual("fixture-boundary", failure.Stage);
        Assert.AreEqual(0, fixture.Transport.Requests.Count);
        Assert.IsFalse(Directory.Exists(fixture.Path));
    }

    [TestMethod]
    public async Task PhysicalMetadataHandleMismatchRefusesPackagedEditsAndRetainsReceipt()
    {
        using var fixture = new Fixture();
        await fixture.Run("prepare");
        fixture.RedirectPhysicalPath = true;
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() => fixture.Run("msix", Package));
        Assert.AreEqual(0, fixture.Transport.Edits);
        Assert.AreEqual(0, fixture.Transport.Deletes);
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(fixture.Path, ProfileCatalogPackageQualification.ReceiptFile)));
    }

    [TestMethod]
    public async Task PhysicalConfigHandleMismatchRefusesCleanupEvenWhenPathStringsAndBytesMatch()
    {
        using var fixture = new Fixture();
        await fixture.Run("prepare");
        var held = fixture.Run("msix", Package);
        await fixture.Run("verify");
        await held;
        var host = fixture.Host(null) with
        {
            PhysicalFileReader = path => new(path.EndsWith("config.toml", StringComparison.Ordinal)
                ? path + "-LocalCache" : path, File.ReadAllBytes(path)),
        };
        await Assert.ThrowsExceptionAsync<ProfileCatalogPackageQualification.QualificationException>(() =>
            ProfileCatalogPackageQualification.RunAsync(fixture.Options("cleanup"), host));
        Assert.AreEqual(0, fixture.Transport.SuccessfulArchives);
        Assert.AreEqual(0, fixture.Transport.Deletes);
    }
    private sealed class Fixture : IDisposable
    {
        internal string Nonce { get; } = Guid.NewGuid().ToString("N");
        internal string Path { get; }
        internal string OutputRoot { get; }
        internal bool RedirectPhysicalPath { get; set; }
        internal FakeTransport Transport { get; }
        internal Fixture()
        {
            OutputRoot = System.IO.Path.Combine(AppContext.BaseDirectory, "profiles-package-tests", Nonce);
            Directory.CreateDirectory(OutputRoot);
            Path = System.IO.Path.Combine(OutputRoot, $"stfc-mod-bridge-profiles-qualification-{Nonce}");
            Transport = new FakeTransport(System.IO.Path.Combine(OutputRoot, "fake-appdata", "Local", "STFC Profiles"));
        }
        internal ProfileCatalogPackageQualification.Options Options(string mode) =>
            ProfileCatalogPackageQualification.ParseArguments([ProfileCatalogPackageQualification.Argument, mode, Nonce, Path]);
        internal ProfileCatalogPackageQualification.Host Host(string? package) =>
            new(Transport, Transport, Transport, () => package, Build, TimeSpan.FromSeconds(15), OutputRoot,
                [System.IO.Path.Combine(OutputRoot, "fake-appdata", "Local"),
                 System.IO.Path.Combine(OutputRoot, "fake-appdata", "Roaming"),
                 System.IO.Path.Combine(OutputRoot, "fake-appdata", "LocalLow"),
                 System.IO.Path.Combine(OutputRoot, "fake-appdata")],
                path => new(RedirectPhysicalPath ? path + "-redirected" : path, File.ReadAllBytes(path)));
        internal Task Run(string mode, string? package = null) => ProfileCatalogPackageQualification.RunAsync(Options(mode), Host(package));
        internal ProfileCatalogPackageQualification.Receipt Receipt() => JsonSerializer.Deserialize<ProfileCatalogPackageQualification.Receipt>(
            File.ReadAllBytes(System.IO.Path.Combine(Path, ProfileCatalogPackageQualification.ReceiptFile)), JsonOptions)!;
        internal void WriteReceipt(ProfileCatalogPackageQualification.Receipt receipt) => File.WriteAllBytes(
            System.IO.Path.Combine(Path, ProfileCatalogPackageQualification.ReceiptFile), JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions));
        public void Dispose()
        {
            // These paths were created exclusively by this test seam; never a real default catalog.
            if (Directory.Exists(OutputRoot)) Directory.Delete(OutputRoot, recursive: true);
        }
    }

    private sealed class FakeTransport(string root) : IProfileCatalogTransport, IProfileCatalogLeaseTransport, IProfileInstallationLeaseTransport
    {
        internal string Root { get; } = root;
        internal string ReportedRoot { get; set; } = root;
        internal LauncherProfile? Profile { get; set; }
        internal bool DataHeld { get; private set; }
        internal bool InstallationHeld { get; private set; }
        internal int Creates { get; private set; }
        internal int Edits { get; private set; }
        internal int Deletes { get; private set; }
        internal int SuccessfulArchives { get; private set; }
        internal int BusyArchiveCount { get; private set; }
        internal int BusyUpdateCount { get; private set; }
        internal string ArchiveError { get; set; } = "busy";
        internal Exception? TransportFailure { get; set; }
        internal List<ProfileCatalogRequest> Requests { get; } = [];
        public ProfileCatalogResponse Request(ProfileCatalogRequest request)
        {
            Requests.Add(request);
            Assert.IsNull(request.Root, "Package qualification must use the native default catalog.");
            if (TransportFailure is not null) throw TransportFailure;
            switch (request.Operation)
            {
                case "catalog-location": return new(true, CatalogRoot: ReportedRoot);
                case "create":
                    Creates++;
                    var id = new string('1', 32);
                    var directory = System.IO.Path.Combine(Root, "profiles", id);
                    Directory.CreateDirectory(System.IO.Path.Combine(directory, "logs"));
                    Profile = new(id, request.Name!, request.GameDirectory!, directory,
                        System.IO.Path.Combine(directory, "config.toml"), System.IO.Path.Combine(directory, "logs", "Player.log"), Revision(1));
                    WriteMetadata();
                    return new(true, Profile: Profile);
                case "paths":
                    return Profile is not null && Profile.Id == request.Id && request.Archived == (Profile.State == "archived")
                        ? new(true, Profile: Profile) : new(false, new("not_found", "No exact synthetic profile."));
                case "edit":
                    Assert.AreEqual(Profile!.Revision, request.ExpectedRevision);
                    Edits++;
                    Profile = Profile with { Name = request.Name!, Revision = Revision(2) };
                    WriteMetadata();
                    return new(true, Profile: Profile);
                case "archive":
                    if (DataHeld) { BusyArchiveCount++; return new(false, new(ArchiveError, "Synthetic data lease.")); }
                    Assert.AreEqual(Profile!.Revision, request.ExpectedRevision);
                    SuccessfulArchives++;
                    var archived = System.IO.Path.Combine(Root, "archives", Profile.Id);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(archived)!);
                    Directory.Move(Profile.Directory, archived);
                    Profile = Profile with { State = "archived", Revision = Revision(3), Directory = archived,
                        ConfigPath = System.IO.Path.Combine(archived, "config.toml"), LogPath = System.IO.Path.Combine(archived, "logs", "Player.log") };
                    WriteMetadata();
                    return new(true, Profile: Profile);
                case "delete":
                    Assert.IsTrue(request.Archived && request.Permanent);
                    Assert.AreEqual(Profile!.Revision, request.ExpectedRevision);
                    Assert.AreEqual(Profile.Id, request.Id);
                    Directory.Delete(Profile.Directory, recursive: true);
                    Profile = null;
                    Deletes++;
                    return new(true);
                case "update-game":
                    if (!InstallationHeld) throw new InvalidOperationException("Missing lease exclusion reached fake updater.");
                    BusyUpdateCount++;
                    return new(false, new("busy", "Synthetic installation lease."));
                default: throw new InvalidOperationException("Unexpected fixture operation: " + request.Operation);
            }
        }
        public ProfileCatalogLease AcquireDataLease(ProfileCatalogRequest request)
        {
            Assert.IsNull(request.Root);
            Assert.AreEqual(Profile!.Id, request.Id);
            DataHeld = true;
            return new(Profile, new CallbackLease(() => DataHeld = false));
        }
        public IDisposable AcquireInstallationLease(ProfileCatalogRequest request)
        {
            Assert.IsNull(request.Root);
            Assert.AreEqual(Profile!.GameDirectory, request.GameDirectory);
            InstallationHeld = true;
            return new CallbackLease(() => InstallationHeld = false);
        }
        private void WriteMetadata() => File.WriteAllText(System.IO.Path.Combine(Profile!.Directory, "metadata.json"),
            JsonSerializer.Serialize(new { name = Profile.Name, gameDirectory = Profile.GameDirectory }));
        private static string Revision(int value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("revision" + value))).ToLowerInvariant();
    }
    private sealed class CallbackLease(Action release) : IDisposable { public void Dispose() => release(); }
}
