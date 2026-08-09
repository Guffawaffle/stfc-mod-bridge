using System.Collections.Frozen;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class LauncherWorkspaceCompositionTests
{
    private const string BattleHomeId = "test.home.battle-bridge";
    private const string BattleFeatureId = "test.feature.battle-home";
    private const string BattleCapabilityId = "test.capability.battle-home.v1";
    private const string BattleImplementationId = "test.native-battle-home";

    [TestMethod]
    public void IneligibleOptionalHomePerformsNoFactoryIoOrServiceStartup()
    {
        using var directory = new TemporaryDirectory();
        var markerPath = Path.Combine(directory.Path, "battle-started.txt");
        var services = new TestServices();
        var plan = ResolveBattlePlan([]);
        using var registry = CreateRegistry(
            services,
            () => plan,
            serviceScope =>
            {
                serviceScope.StartCount++;
                File.WriteAllText(markerPath, "unexpected Battle startup");
                return new TestWorkspace(BattleHomeId, serviceScope);
            });

        var activation = registry.Activate(BattleHomeId);

        Assert.AreEqual(LauncherWorkspaceActivationState.FeatureUnavailable, activation.State);
        Assert.IsNull(activation.Workspace);
        Assert.IsNotNull(activation.FeatureDecision);
        Assert.IsFalse(activation.FeatureDecision.IsActive);
        Assert.AreEqual(0, services.StartCount);
        Assert.IsFalse(File.Exists(markerPath));
    }

    [TestMethod]
    public void ActiveHomesReceiveTheSameSharedSettingsAndDiagnosticsContracts()
    {
        var services = new TestServices();
        var plan = ResolveBattlePlan([BattleCapabilityId]);
        var battleFactoryCalls = 0;
        using var registry = CreateRegistry(
            services,
            () => plan,
            serviceScope =>
            {
                battleFactoryCalls++;
                return new TestWorkspace(BattleHomeId, serviceScope);
            });

        var modHome = registry.Activate(LauncherHomeWorkspaceIds.ModBridge);
        var battleHome = registry.Activate(BattleHomeId);
        var repeatedBattleHome = registry.Activate(BattleHomeId);

        Assert.IsTrue(modHome.IsActive);
        Assert.IsTrue(battleHome.IsActive);
        Assert.AreSame(services, modHome.Workspace!.SharedServices);
        Assert.AreSame(services, battleHome.Workspace!.SharedServices);
        Assert.AreSame(
            modHome.Workspace.SharedServices.Settings,
            battleHome.Workspace.SharedServices.Settings);
        Assert.AreSame(
            modHome.Workspace.SharedServices.Diagnostics,
            battleHome.Workspace.SharedServices.Diagnostics);
        Assert.AreSame(battleHome.Workspace, repeatedBattleHome.Workspace);
        Assert.AreEqual(1, battleFactoryCalls);
    }

    [TestMethod]
    public void ActiveFeatureWithDifferentImplementationDoesNotConstructWorkspace()
    {
        var services = new TestServices();
        var plan = ResolveBattlePlan([BattleCapabilityId]);
        var factoryCalls = 0;
        using var registry = new LauncherWorkspaceRegistry<TestServices, TestWorkspace>(
            services,
            () => plan,
            [
                new(
                    BattleHomeId,
                    BattleFeatureId,
                    "different-native-home",
                    serviceScope =>
                    {
                        factoryCalls++;
                        return new TestWorkspace(BattleHomeId, serviceScope);
                    }),
            ]);

        var activation = registry.Activate(BattleHomeId);

        Assert.AreEqual(LauncherWorkspaceActivationState.ImplementationMismatch, activation.State);
        Assert.IsNull(activation.Workspace);
        Assert.AreEqual(BattleImplementationId, activation.FeatureDecision?.SelectedImplementation);
        Assert.AreEqual(0, factoryCalls);
    }

    [TestMethod]
    public void BaseRegistrationHasNoOptionalFactoryOrActivationSideEffects()
    {
        var services = new TestServices();
        var planRequests = 0;
        using var registry = new LauncherWorkspaceRegistry<TestServices, TestWorkspace>(
            services,
            () =>
            {
                planRequests++;
                return ResolveBattlePlan([]);
            },
            [
                new(
                    LauncherHomeWorkspaceIds.ModBridge,
                    null,
                    null,
                    serviceScope => new TestWorkspace(
                        LauncherHomeWorkspaceIds.ModBridge,
                        serviceScope)),
            ]);

        var activation = registry.Activate(LauncherHomeWorkspaceIds.ModBridge);

        Assert.IsTrue(activation.IsActive);
        Assert.AreSame(services, activation.Workspace?.SharedServices);
        Assert.AreEqual(0, planRequests, "The ungated base Home must not evaluate optional feature policy.");
        Assert.AreEqual(0, services.StartCount);
    }

    [TestMethod]
    public void DuplicateRegistrationsFailBeforeAnyWorkspaceFactoryRuns()
    {
        var factoryCalls = 0;
        var registration = new LauncherWorkspaceRegistration<TestServices, TestWorkspace>(
            BattleHomeId,
            null,
            null,
            services =>
            {
                factoryCalls++;
                return new TestWorkspace(BattleHomeId, services);
            });

        _ = Assert.ThrowsException<ArgumentException>(
            () => new LauncherWorkspaceRegistry<TestServices, TestWorkspace>(
                new(),
                () => ResolveBattlePlan([]),
                [registration, registration]));

        Assert.AreEqual(0, factoryCalls);
    }

    [TestMethod]
    public void FactoryCannotReplaceTheRegisteredSharedServiceScope()
    {
        var services = new TestServices();
        var foreignWorkspace = new TestWorkspace(BattleHomeId, new());
        using var registry = new LauncherWorkspaceRegistry<TestServices, TestWorkspace>(
            services,
            () => ResolveBattlePlan([BattleCapabilityId]),
            [
                new(
                    BattleHomeId,
                    BattleFeatureId,
                    BattleImplementationId,
                    _ => foreignWorkspace),
            ]);

        _ = Assert.ThrowsException<InvalidOperationException>(
            () => registry.Activate(BattleHomeId));

        Assert.AreEqual(1, foreignWorkspace.DisposeCount);
    }

    [TestMethod]
    public void CapabilityLossDisposesActivatedOptionalHomeExactlyOnceAndKeepsBaseHome()
    {
        var services = new TestServices();
        var plan = ResolveBattlePlan([BattleCapabilityId]);
        using var registry = CreateRegistry(
            services,
            () => plan,
            serviceScope => new TestWorkspace(BattleHomeId, serviceScope));
        var baseHome = registry.Activate(LauncherHomeWorkspaceIds.ModBridge).Workspace!;
        var battleHome = registry.Activate(BattleHomeId).Workspace!;

        plan = ResolveBattlePlan([]);
        var retired = registry.RevalidateActivated();
        var repeated = registry.RevalidateActivated();

        Assert.AreEqual(1, retired);
        Assert.AreEqual(0, repeated);
        Assert.AreEqual(1, battleHome.DisposeCount);
        Assert.AreEqual(0, baseHome.DisposeCount);
        Assert.AreSame(
            baseHome,
            registry.Activate(LauncherHomeWorkspaceIds.ModBridge).Workspace);
        Assert.AreEqual(
            LauncherWorkspaceActivationState.FeatureUnavailable,
            registry.Activate(BattleHomeId).State);
    }

    [TestMethod]
    public void ImplementationChangeDisposesActivatedOptionalHomeExactlyOnceAndKeepsBaseHome()
    {
        var services = new TestServices();
        var plan = ResolveBattlePlan([BattleCapabilityId]);
        using var registry = CreateRegistry(
            services,
            () => plan,
            serviceScope => new TestWorkspace(BattleHomeId, serviceScope));
        var baseHome = registry.Activate(LauncherHomeWorkspaceIds.ModBridge).Workspace!;
        var battleHome = registry.Activate(BattleHomeId).Workspace!;

        plan = ResolveBattlePlan(
            [BattleCapabilityId],
            activeImplementationId: "test.different-native-battle-home");
        var retired = registry.RevalidateActivated();
        var repeated = registry.RevalidateActivated();

        Assert.AreEqual(1, retired);
        Assert.AreEqual(0, repeated);
        Assert.AreEqual(1, battleHome.DisposeCount);
        Assert.AreEqual(0, baseHome.DisposeCount);
        Assert.AreSame(
            baseHome,
            registry.Activate(LauncherHomeWorkspaceIds.ModBridge).Workspace);
        Assert.AreEqual(
            LauncherWorkspaceActivationState.ImplementationMismatch,
            registry.Activate(BattleHomeId).State);
    }

    [TestMethod]
    public void WorkspaceFactoryCannotReenterActivation()
    {
        var services = new TestServices();
        LauncherWorkspaceRegistry<TestServices, TestWorkspace>? registry = null;
        registry = new(
            services,
            () => ResolveBattlePlan([BattleCapabilityId]),
            [
                new(
                    BattleHomeId,
                    BattleFeatureId,
                    BattleImplementationId,
                    serviceScope =>
                    {
                        _ = serviceScope;
                        _ = registry!.Activate(BattleHomeId);
                        return new TestWorkspace(BattleHomeId, services);
                    }),
            ]);
        using (registry)
        {
            var exception = Assert.ThrowsException<InvalidOperationException>(
                () => registry.Activate(BattleHomeId));

            StringAssert.Contains(exception.Message, "cannot activate another workspace");
        }
    }

    [TestMethod]
    public void WorkspaceFactoryCannotCreateAnActivationCycle()
    {
        const string secondWorkspaceId = "test.home.second";
        var services = new TestServices();
        LauncherWorkspaceRegistry<TestServices, TestWorkspace>? registry = null;
        registry = new(
            services,
            () => ResolveBattlePlan([BattleCapabilityId]),
            [
                new(
                    BattleHomeId,
                    null,
                    null,
                    serviceScope =>
                    {
                        _ = serviceScope;
                        _ = registry!.Activate(secondWorkspaceId);
                        return new TestWorkspace(BattleHomeId, services);
                    }),
                new(
                    secondWorkspaceId,
                    null,
                    null,
                    serviceScope => new TestWorkspace(secondWorkspaceId, serviceScope)),
            ]);
        using (registry)
        {
            var exception = Assert.ThrowsException<InvalidOperationException>(
                () => registry.Activate(BattleHomeId));

            StringAssert.Contains(exception.Message, "cannot activate another workspace");
        }
    }

    [TestMethod]
    public void DisposalDuringFactoryConstructionDisposesProductAndRejectsInsertion()
    {
        var services = new TestServices();
        TestWorkspace? constructed = null;
        LauncherWorkspaceRegistry<TestServices, TestWorkspace>? registry = null;
        registry = new(
            services,
            () => ResolveBattlePlan([BattleCapabilityId]),
            [
                new(
                    BattleHomeId,
                    BattleFeatureId,
                    BattleImplementationId,
                    serviceScope =>
                    {
                        constructed = new(BattleHomeId, serviceScope);
                        registry!.Dispose();
                        return constructed;
                    }),
            ]);

        _ = Assert.ThrowsException<ObjectDisposedException>(
            () => registry.Activate(BattleHomeId));

        Assert.IsNotNull(constructed);
        Assert.AreEqual(1, constructed.DisposeCount);
        _ = Assert.ThrowsException<ObjectDisposedException>(
            () => registry.Activate(BattleHomeId));
    }

    private static LauncherWorkspaceRegistry<TestServices, TestWorkspace> CreateRegistry(
        TestServices services,
        Func<LauncherActivationPlan> plan,
        Func<TestServices, TestWorkspace> battleFactory) =>
        new(
            services,
            plan,
            [
                new(
                    LauncherHomeWorkspaceIds.ModBridge,
                    null,
                    null,
                    serviceScope => new TestWorkspace(
                        LauncherHomeWorkspaceIds.ModBridge,
                        serviceScope)),
                new(
                    BattleHomeId,
                    BattleFeatureId,
                    BattleImplementationId,
                    battleFactory),
            ]);

    private static LauncherActivationPlan ResolveBattlePlan(
        IEnumerable<string> capabilities,
        string activeImplementationId = BattleImplementationId)
    {
        var profile = new LauncherRuntimeProfile(
            "test.runtime",
            new Version(1, 0),
            "test-revision",
            null,
            capabilities,
            [new("test", "synthetic workspace-composition evidence")]);
        var feature = new LauncherFeatureDefinition(
            BattleFeatureId,
            LauncherFeatureKind.CompatibilityGate,
            LauncherFeatureActivationMode.StartupLatched,
            new[] { BattleCapabilityId }.ToFrozenSet(StringComparer.Ordinal),
            Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal),
            LauncherFeatureDefault.EnabledWhenEligible,
            activeImplementationId,
            "test.mod-bridge-home-fallback");
        return LauncherFeatureResolver.Resolve(
            profile,
            [feature],
            catalogSource: new("test.workspace-catalog", "1"));
    }

    private sealed class TestServices
    {
        public object Settings { get; } = new();

        public object Diagnostics { get; } = new();

        public int StartCount { get; set; }
    }

    private sealed class TestWorkspace(string id, TestServices services) :
        ILauncherWorkspace<TestServices>
    {
        public string Id { get; } = id;

        public TestServices SharedServices { get; } = services;

        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "stfc-workspace-composition-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
