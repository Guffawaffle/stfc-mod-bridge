namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class BattleRuntimeClientAuthorizationTests
{
    private const string EvidenceSha256 =
        "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";
    private static readonly DateTimeOffset ProcessStartUtc =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ExactSelectedGameProcessProducesOneEvidenceBoundReceipt()
    {
        var gameRoot = GameRoot();
        var target = Path.Combine(gameRoot, "prime.exe");
        var provider = Provider(
            new(8123, ProcessStartUtc, target),
            new(9000, ProcessStartUtc, Path.Combine(Path.GetTempPath(), "other", "prime.exe")));

        var result = provider.Discover(gameRoot, EvidenceSha256.ToUpperInvariant());

        Assert.AreEqual(BattleRuntimeClientReceiptState.Ready, result.State);
        Assert.AreEqual("battle-runtime-client-ready", result.Code);
        Assert.IsNotNull(result.Receipt);
        Assert.AreEqual((uint)8123, result.Receipt.ProcessId);
        Assert.AreEqual(ProcessStartUtc, result.Receipt.ProcessStartUtc);
        Assert.AreEqual(Path.GetFullPath(target), result.Receipt.ExecutablePath);
        Assert.AreEqual(EvidenceSha256, result.Receipt.RuntimeEvidenceSha256);
        Assert.IsNotNull(result.CreateAuthorizer());
    }

    [TestMethod]
    public void NoExactSelectedGameProcessIsAbsentAndCreatesNoDirectory()
    {
        var gameRoot = Path.Combine(Path.GetTempPath(), $"missing-battle-game-{Guid.NewGuid():N}");
        var provider = Provider();

        var result = provider.Discover(gameRoot, EvidenceSha256);

        Assert.AreEqual(BattleRuntimeClientReceiptState.Absent, result.State);
        Assert.AreEqual("battle-runtime-client-absent", result.Code);
        Assert.IsNull(result.Receipt);
        Assert.IsFalse(Directory.Exists(gameRoot));
        Assert.ThrowsException<InvalidOperationException>(() => result.CreateAuthorizer());
    }

    [TestMethod]
    public void MultipleExactProcessesFailClosedAsAmbiguous()
    {
        var gameRoot = GameRoot();
        var target = Path.Combine(gameRoot, "prime.exe");
        var provider = Provider(
            new(8123, ProcessStartUtc, target),
            new(8124, ProcessStartUtc.AddSeconds(1), target));

        var result = provider.Discover(gameRoot, EvidenceSha256);

        Assert.AreEqual(BattleRuntimeClientReceiptState.Ambiguous, result.State);
        Assert.AreEqual("battle-runtime-client-ambiguous", result.Code);
        Assert.IsNull(result.Receipt);
    }

    [TestMethod]
    public void AnyUninspectablePrimeProcessBlocksExactAuthorization()
    {
        var gameRoot = GameRoot();
        var provider = Provider(
            new(8123, ProcessStartUtc, Path.Combine(gameRoot, "prime.exe")),
            new(9000, DateTimeOffset.UnixEpoch, null, IsInspectable: false));

        var result = provider.Discover(gameRoot, EvidenceSha256);

        Assert.AreEqual(BattleRuntimeClientReceiptState.Unavailable, result.State);
        Assert.AreEqual("battle-runtime-client-unavailable", result.Code);
        Assert.IsNull(result.Receipt);
    }

    [TestMethod]
    public void DuplicateProcessIdentityFailsClosed()
    {
        var gameRoot = GameRoot();
        var target = Path.Combine(gameRoot, "prime.exe");
        var provider = Provider(
            new(8123, ProcessStartUtc, target),
            new(8123, ProcessStartUtc, target));

        Assert.AreEqual(
            BattleRuntimeClientReceiptState.Unavailable,
            provider.Discover(gameRoot, EvidenceSha256).State);
    }

    [TestMethod]
    public void NonUtcProcessStartFailsClosed()
    {
        var gameRoot = GameRoot();
        var provider = Provider(
            new BattleRuntimeClientProcessObservation(
                8123,
                ProcessStartUtc.ToOffset(TimeSpan.FromHours(1)),
                Path.Combine(gameRoot, "prime.exe")));

        Assert.AreEqual(
            BattleRuntimeClientReceiptState.Unavailable,
            provider.Discover(gameRoot, EvidenceSha256).State);
    }

    [TestMethod]
    public void RelativeObservedExecutablePathFailsClosed()
    {
        var provider = Provider(
            new BattleRuntimeClientProcessObservation(8123, ProcessStartUtc, "prime.exe"));

        Assert.AreEqual(
            BattleRuntimeClientReceiptState.Unavailable,
            provider.Discover(GameRoot(), EvidenceSha256).State);
        Assert.ThrowsException<ArgumentException>(() => new BattleNamedPipeAuthorizedProcess(
            8123,
            ProcessStartUtc,
            "prime.exe",
            EvidenceSha256));
    }

    [TestMethod]
    public void ObservationCardinalityIsBounded()
    {
        var gameRoot = GameRoot();
        var observations = Enumerable.Range(1, 65)
            .Select(index => new BattleRuntimeClientProcessObservation(
                8000 + index,
                ProcessStartUtc,
                Path.Combine(Path.GetTempPath(), $"other-{index}", "prime.exe")))
            .ToArray();
        var provider = new SystemBattleRuntimeClientReceiptProvider(() => observations);

        Assert.AreEqual(
            BattleRuntimeClientReceiptState.Unavailable,
            provider.Discover(gameRoot, EvidenceSha256).State);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProcessCaptureFailureReturnsBoundedUnavailableState(bool accessFailure)
    {
        var provider = new SystemBattleRuntimeClientReceiptProvider(
            () => throw (accessFailure
                ? new UnauthorizedAccessException("denied")
                : new System.ComponentModel.Win32Exception(5)));

        var result = provider.Discover(GameRoot(), EvidenceSha256);

        Assert.AreEqual(BattleRuntimeClientReceiptState.Unavailable, result.State);
        Assert.AreEqual("battle-runtime-client-unavailable", result.Code);
    }

    [TestMethod]
    public void InvalidRuntimeEvidenceRejectsBeforeProcessCapture()
    {
        var captureCalls = 0;
        var provider = new SystemBattleRuntimeClientReceiptProvider(() =>
        {
            captureCalls++;
            return [];
        });

        Assert.ThrowsException<ArgumentException>(() => provider.Discover(GameRoot(), "not-a-sha"));
        Assert.AreEqual(0, captureCalls);
    }

    private static SystemBattleRuntimeClientReceiptProvider Provider(
        params BattleRuntimeClientProcessObservation[] observations) =>
        new(() => observations);

    private static string GameRoot() =>
        Path.Combine(Path.GetTempPath(), "reviewed-game", "default", "game");
}
