using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class SyncDesiredTopologyTests
{
    [TestMethod]
    public void CatalogSeparatesConcreteKindsFromProviderPresets()
    {
        Assert.AreEqual(3, SyncTargetTypeCatalog.All.Count);
        Assert.AreEqual("sidecar.sync", SyncTargetTypeCatalog.Get(SyncTargetKind.LocalSidecar).PersistencePattern);
        Assert.AreEqual("sidecar_local_ingest", SyncTargetTypeCatalog.Get(SyncTargetKind.LocalSidecar).WireContract);
        Assert.IsFalse(SyncTargetTypeCatalog.Get(SyncTargetKind.LocalSidecar).InheritsGlobalSync);
        Assert.AreEqual(
            SyncTargetExposurePolicy.ExistingConfigurationOnly,
            SyncTargetTypeCatalog.Get(SyncTargetKind.LocalSidecar).ExposurePolicy);
        Assert.AreEqual("sync.targets.*", SyncTargetTypeCatalog.Get(SyncTargetKind.MajelIngest).PersistencePattern);
        Assert.AreEqual("majel.ingest.v1", SyncTargetTypeCatalog.Get(SyncTargetKind.MajelIngest).WireContract);
        Assert.AreEqual(
            SyncTargetExposurePolicy.Hidden,
            SyncTargetTypeCatalog.Get(SyncTargetKind.MajelIngest).ExposurePolicy);
        Assert.AreEqual("Sync", SyncTargetTypeCatalog.Get(SyncTargetKind.LegacyCommunity).DisplayName);

        var spocks = SyncTargetTypeCatalog.GetPreset("spocks_club");
        Assert.AreEqual(SyncTargetKind.LegacyCommunity, spocks.TargetKind);
        Assert.AreEqual("spocksclub", spocks.SuggestedIdentity);
        Assert.AreEqual("https://spocks.club/sync/ingress/", spocks.DefaultUrl);
        AssertPresetFeeds(
            spocks,
            (SyncDataKind.Resources, true),
            (SyncDataKind.Battlelogs, false),
            (SyncDataKind.Officer, true),
            (SyncDataKind.Missions, false),
            (SyncDataKind.Research, true),
            (SyncDataKind.Tech, false),
            (SyncDataKind.Traits, false),
            (SyncDataKind.Buildings, true),
            (SyncDataKind.Ships, false));

        var next = SyncTargetTypeCatalog.GetPreset("next_spocks_club");
        Assert.AreEqual("spocksclub-next", next.SuggestedIdentity);
        Assert.AreEqual("https://next.spocks.club/sync/ingress/", next.DefaultUrl);
        AssertPresetFeeds(
            next,
            (SyncDataKind.Battlelogs, false),
            (SyncDataKind.Buffs, true),
            (SyncDataKind.Buildings, true),
            (SyncDataKind.Inventory, true),
            (SyncDataKind.Jobs, false),
            (SyncDataKind.Missions, true),
            (SyncDataKind.Officer, true),
            (SyncDataKind.Research, true),
            (SyncDataKind.Resources, true),
            (SyncDataKind.Ships, true),
            (SyncDataKind.Slots, true),
            (SyncDataKind.Tech, true),
            (SyncDataKind.Traits, true));
        Assert.AreSame(next, SyncTargetTypeCatalog.FindPresetByUrl("https://next.spocks.club/sync/ingress"));
    }

    private static void AssertPresetFeeds(
        SyncTargetPreset preset,
        params (SyncDataKind Kind, bool Enabled)[] expected)
    {
        Assert.AreEqual(expected.Length, preset.FeedDefaults.Count);
        CollectionAssert.AreEquivalent(
            expected.Select(item => item.Kind).ToArray(),
            preset.SupportedDataKinds.ToArray());
        foreach (var (kind, enabled) in expected)
        {
            Assert.AreEqual(enabled, preset.FeedDefaults[kind], $"Unexpected {preset.Id} default for {kind}.");
        }
    }

    [TestMethod]
    public void OverridePresenceParticipatesInEqualityAndResolutionProvenance()
    {
        var inherited = SyncOverride.Inherited<bool>();
        var explicitFalse = SyncOverride.Explicit(false);

        Assert.AreNotEqual(inherited, explicitFalse);
        Assert.AreEqual(
            new SyncResolvedValue<bool>(false, SyncValueProvenance.Inherited, SyncValueSource.GlobalDefault),
            inherited.Resolve(false, SyncValueSource.GlobalDefault));
        Assert.AreEqual(
            new SyncResolvedValue<bool>(false, SyncValueProvenance.ExplicitFalse, SyncValueSource.Target),
            explicitFalse.Resolve(true, SyncValueSource.GlobalDefault));

        var explicitEmpty = SyncOverride.Explicit(string.Empty).Resolve(
            "http://proxy.example.invalid:8080",
            SyncValueSource.GlobalDefault);
        Assert.AreEqual(SyncValueProvenance.ExplicitEmpty, explicitEmpty.Provenance);
        Assert.AreEqual(string.Empty, explicitEmpty.Value);
    }

    [TestMethod]
    public void MixedTopologyResolvesCapabilitiesAndProvenance()
    {
        var globals = new SyncGlobalDefaults(
                "http://proxy.example.invalid:8080",
                true,
                false)
            .WithDataKind(SyncDataKind.Battlelogs, true);
        var topology = new SyncDesiredTopology(globals);
        topology = AddConfigured(
            topology,
            "ignored-for-sidecar",
            SyncTargetKind.LocalSidecar,
            "http://127.0.0.1:43127/api/sidecar/ingest",
            target => target
                .WithProxy(SyncOverride.Explicit(string.Empty))
                .WithDataOverride(SyncDataKind.BattlelogsRealtime, SyncOverride.Explicit(true))
                .WithBattlelogEnrichment(SyncOverride.Explicit(true))
                .WithFleetRuntimeMode(SyncOverride.Explicit("snapshot_only")));
        topology = AddConfigured(
            topology,
            "community",
            SyncTargetKind.LegacyCommunity,
            "https://community.example.invalid/sync");
        topology = AddConfigured(
            topology,
            "majel",
            SyncTargetKind.MajelIngest,
            "https://majel.example.invalid/api/ingest/events",
            target => target.WithDataOverride(SyncDataKind.Battlelogs, SyncOverride.Explicit(false)));

        var resolved = topology.Resolve();

        Assert.IsTrue(resolved.IsCommittable);
        Assert.AreEqual(3, resolved.Targets.Count);
        var sidecar = resolved.Targets.Single(target => target.Kind == SyncTargetKind.LocalSidecar);
        Assert.AreEqual(string.Empty, sidecar.Proxy.Value);
        Assert.AreEqual(SyncValueProvenance.ExplicitEmpty, sidecar.Proxy.Provenance);
        Assert.AreEqual(SyncValueSource.Target, sidecar.Proxy.Source);
        Assert.AreEqual(
            SyncValueSource.TargetTypeDefault,
            sidecar.DataKinds[SyncDataKind.FleetRuntime].Source);
        Assert.IsTrue(sidecar.BattlelogEnrichment!.Value);
        Assert.AreEqual("snapshot_only", sidecar.FleetRuntimeMode!.Value);

        var community = resolved.Targets.Single(target => target.Name == "community");
        Assert.AreEqual(globals.Proxy, community.Proxy.Value);
        Assert.AreEqual(SyncValueProvenance.Inherited, community.Proxy.Provenance);
        Assert.AreEqual(SyncValueSource.GlobalDefault, community.Proxy.Source);
        Assert.IsTrue(community.DataKinds[SyncDataKind.Battlelogs].Value);

        var majel = resolved.Targets.Single(target => target.Name == "majel");
        Assert.IsFalse(majel.DataKinds[SyncDataKind.Battlelogs].Value);
        Assert.AreEqual(
            SyncValueProvenance.ExplicitFalse,
            majel.DataKinds[SyncDataKind.Battlelogs].Provenance);
    }

    [TestMethod]
    public void GlobalChangesFlowOnlyToCompatibleInheritedFields()
    {
        var topology = new SyncDesiredTopology(new SyncGlobalDefaults("proxy-one", true, false));
        topology = AddConfigured(
            topology,
            "inherited",
            SyncTargetKind.LegacyCommunity,
            "https://community.example.invalid/inherited");
        topology = AddConfigured(
            topology,
            "explicit",
            SyncTargetKind.LegacyCommunity,
            "https://community.example.invalid/explicit",
            target => target.WithProxy(SyncOverride.Explicit("target-proxy")));
        topology = AddConfigured(
            topology,
            "sidecar",
            SyncTargetKind.LocalSidecar,
            "http://localhost:43127/api/sidecar/ingest");

        topology = topology.WithGlobalDefaults(topology.GlobalDefaults.WithProxy("proxy-two"));
        var resolved = topology.Resolve();

        Assert.AreEqual("proxy-two", resolved.Targets.Single(target => target.Name == "inherited").Proxy.Value);
        Assert.AreEqual("target-proxy", resolved.Targets.Single(target => target.Name == "explicit").Proxy.Value);
        Assert.AreEqual(
            string.Empty,
            resolved.Targets.Single(target => target.Kind == SyncTargetKind.LocalSidecar).Proxy.Value);
    }

    [TestMethod]
    public void SidecarDoesNotInheritExternalProxyOrUnsafeTlsPolicy()
    {
        var topology = new SyncDesiredTopology(
            new SyncGlobalDefaults("http://external-proxy.example.invalid:8080", false, true));
        topology = AddConfigured(
            topology,
            "sidecar",
            SyncTargetKind.LocalSidecar,
            "http://127.0.0.1:43127/api/sidecar/ingest");
        topology = AddConfigured(
            topology,
            "external",
            SyncTargetKind.LegacyCommunity,
            "https://community.example.invalid/sync");

        var resolved = topology.Resolve();
        var sidecar = resolved.Targets.Single(target => target.Kind == SyncTargetKind.LocalSidecar);
        var external = resolved.Targets.Single(target => target.Kind == SyncTargetKind.LegacyCommunity);

        Assert.AreEqual(string.Empty, sidecar.Proxy.Value);
        Assert.IsTrue(sidecar.VerifySsl.Value);
        Assert.IsFalse(sidecar.AllowUnsafeTlsWithoutCertificateValidation.Value);
        Assert.AreEqual(SyncValueSource.TargetTypeDefault, sidecar.VerifySsl.Source);
        Assert.AreEqual("http://external-proxy.example.invalid:8080", external.Proxy.Value);
        Assert.IsFalse(external.VerifySsl.Value);
        Assert.IsTrue(external.AllowUnsafeTlsWithoutCertificateValidation.Value);
        Assert.AreEqual(SyncValueSource.GlobalDefault, external.VerifySsl.Source);
    }

    [TestMethod]
    public void LifecycleTransitionsAreImmutableAndDuplicateClearsCredentials()
    {
        var topology = SyncDesiredTopology.Empty;
        var added = topology.AddPreset("spocks_club");
        Assert.IsTrue(added.Succeeded);
        Assert.AreEqual(0, topology.Targets.Count);
        topology = added.Topology;
        topology = RequireSuccess(
            topology.UpdateTarget(
                "spocksclub",
                target => target
                    .WithConnection(
                        "https://community.example.invalid/sync",
                        SyncSecret.FromPlainText("secret-one"))
                    .WithEnabled(true)));

        topology = RequireSuccess(topology.RenameTarget("spocksclub", "primary"));
        topology = RequireSuccess(topology.DuplicateTarget("primary", "backup"));
        var duplicate = topology.Targets["backup"];
        Assert.IsFalse(duplicate.Enabled);
        Assert.IsFalse(duplicate.Token.IsConfigured);
        Assert.AreEqual(topology.Targets["primary"].Url, duplicate.Url);

        topology = RequireSuccess(topology.ChangeTargetKind("backup", SyncTargetKind.MajelIngest));
        Assert.AreEqual(SyncTargetKind.MajelIngest, topology.Targets["backup"].Kind);
        topology = RequireSuccess(topology.SetTargetEnabled("backup", true));
        Assert.IsTrue(topology.Targets["backup"].Enabled);
        topology = RequireSuccess(topology.RemoveTarget("backup"));
        Assert.IsFalse(topology.Targets.ContainsKey("backup"));
    }

    [TestMethod]
    public void KindChangeResetPolicyClearsTargetOverridesButKeepsConnection()
    {
        var topology = AddConfigured(
            SyncDesiredTopology.Empty,
            "community",
            SyncTargetKind.LegacyCommunity,
            "https://community.example.invalid/sync",
            target => target
                .WithProxy(SyncOverride.Explicit("target-proxy"))
                .WithDataOverride(SyncDataKind.Jobs, SyncOverride.Explicit(false)));
        var original = topology.Targets["community"];

        topology = RequireSuccess(
            topology.ChangeTargetKind(
                "community",
                SyncTargetKind.MajelIngest,
                SyncTargetKindChangePolicy.ResetOverrides));
        var changed = topology.Targets["community"];

        Assert.AreEqual(SyncTargetKind.MajelIngest, changed.Kind);
        Assert.AreEqual(original.Url, changed.Url);
        Assert.AreEqual(original.Token, changed.Token);
        Assert.IsFalse(changed.Proxy.IsExplicit);
        Assert.AreEqual(0, changed.DataOverrides.Count);
    }

    [TestMethod]
    public void SidecarCardinalityIdentityAndDuplicationAreGuarded()
    {
        var topology = RequireSuccess(
            SyncDesiredTopology.Empty.AddTarget("anything", SyncTargetKind.LocalSidecar));

        Assert.AreEqual(SyncDesiredTopology.LocalSidecarIdentity, topology.Targets.Keys.Single());
        Assert.IsFalse(topology.AddTarget("second", SyncTargetKind.LocalSidecar).Succeeded);
        Assert.IsFalse(topology.RenameTarget(SyncDesiredTopology.LocalSidecarIdentity, "other").Succeeded);
        Assert.IsFalse(topology.DuplicateTarget(SyncDesiredTopology.LocalSidecarIdentity, "other").Succeeded);
        Assert.IsFalse(
            topology.ChangeTargetKind(
                SyncDesiredTopology.LocalSidecarIdentity,
                SyncTargetKind.LegacyCommunity).Succeeded);
    }

    [TestMethod]
    public void ResolverRejectsCardinalityViolationsFromUntrustedConstruction()
    {
        var sidecar = SyncTargetDraft.Create("ignored", SyncTargetKind.LocalSidecar)
            .WithConnection(
                "http://127.0.0.1:43127/api/sidecar/ingest",
                SyncSecret.FromPlainText("fixture-sidecar"));
        var topology = new SyncDesiredTopology(
            new SyncGlobalDefaults(string.Empty, true, false),
            new Dictionary<string, SyncTargetDraft>
            {
                ["first"] = sidecar,
                ["second"] = sidecar,
            });

        var resolved = topology.Resolve();

        Assert.IsFalse(resolved.IsCommittable);
        Assert.AreEqual(0, resolved.Targets.Count);
        Assert.IsTrue(resolved.Diagnostics.Any(item => item.Code == "SYNC_TARGET_CARDINALITY"));
    }

    [TestMethod]
    public void InvalidNamesBlockConstructionWhileRuntimeConnectionProblemsAreAdvisory()
    {
        Assert.IsFalse(SyncDesiredTopology.Empty.AddTarget("bad.target", SyncTargetKind.LegacyCommunity).Succeeded);
        Assert.IsFalse(SyncDesiredTopology.Empty.AddTarget("sidecar", SyncTargetKind.LegacyCommunity).Succeeded);

        var external = RequireSuccess(
            SyncDesiredTopology.Empty.AddTarget("external", SyncTargetKind.LegacyCommunity));
        external = RequireSuccess(
            external.UpdateTarget(
                "external",
                target => target.WithConnection(
                    "http://127.0.0.1:43127/api/sidecar/ingest",
                    SyncSecret.FromPlainText("hidden-value")).WithEnabled(true)));
        var externalResolved = external.Resolve();
        Assert.IsTrue(externalResolved.IsCommittable);
        Assert.IsTrue(externalResolved.Diagnostics.Any(item =>
            item.Code == "SYNC_LOOPBACK_TARGET_INVALID"
            && item.Severity == SyncTopologyDiagnosticSeverity.Warning));

        var embeddedCredentials = RequireSuccess(
            SyncDesiredTopology.Empty.AddTarget("embedded", SyncTargetKind.LegacyCommunity));
        embeddedCredentials = RequireSuccess(
            embeddedCredentials.UpdateTarget(
                "embedded",
                target => target.WithConnection(
                    "https://username:password@community.example.invalid/sync",
                    SyncSecret.FromPlainText("hidden-value")).WithEnabled(true)));
        Assert.IsTrue(
            embeddedCredentials.Resolve().Diagnostics.Any(
                item => item.Code == "SYNC_ENDPOINT_EMBEDDED_CREDENTIALS"
                    && item.Severity == SyncTopologyDiagnosticSeverity.Warning));

        var missing = RequireSuccess(
            SyncDesiredTopology.Empty.AddTarget("missing", SyncTargetKind.MajelIngest));
        missing = RequireSuccess(
            missing.UpdateTarget(
                "missing",
                target => target.WithConnection(
                    "https://majel.example.invalid/api/ingest/events",
                    SyncSecret.Missing).WithEnabled(true)));
        Assert.IsTrue(missing.Resolve().IsCommittable);
        Assert.IsTrue(missing.Resolve().Diagnostics.Any(item =>
            item.Code == "SYNC_CREDENTIALS_INCOMPLETE"
            && item.Severity == SyncTopologyDiagnosticSeverity.Warning));

        var sidecar = RequireSuccess(
            SyncDesiredTopology.Empty.AddTarget("sidecar", SyncTargetKind.LocalSidecar));
        sidecar = RequireSuccess(
            sidecar.UpdateTarget(
                SyncDesiredTopology.LocalSidecarIdentity,
                target => target.WithConnection(
                    "https://sidecar.example.invalid/ingest",
                    SyncSecret.FromPlainText("hidden-value")).WithEnabled(true)));
        Assert.IsTrue(sidecar.Resolve().IsCommittable);
        Assert.IsTrue(sidecar.Resolve().Diagnostics.Any(item =>
            item.Code == "SYNC_SIDECAR_ENDPOINT_NOT_LOOPBACK"
            && item.Severity == SyncTopologyDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void UnsafeTlsStillBlocksWhileUnsupportedCapabilitiesRemainAdvisory()
    {
        var topology = AddConfigured(
            SyncDesiredTopology.Empty,
            "external",
            SyncTargetKind.LegacyCommunity,
            "https://community.example.invalid/sync",
            target => target
                .WithDataOverride(SyncDataKind.FleetRuntime, SyncOverride.Explicit(true))
                .WithBattlelogEnrichment(SyncOverride.Explicit(true))
                .WithVerifySsl(SyncOverride.Explicit(false)));

        var blocked = topology.Resolve();
        Assert.IsFalse(blocked.IsCommittable);
        var unsupported = blocked.Diagnostics.First(item => item.Code == "SYNC_CAPABILITY_UNSUPPORTED");
        StringAssert.Contains(unsupported.Message, "Fleet runtime");
        StringAssert.Contains(unsupported.Message, "Sync target type");
        Assert.AreEqual(SyncTopologyDiagnosticSeverity.Warning, unsupported.Severity);
        Assert.IsTrue(blocked.Diagnostics.Any(item => item.Code == "SYNC_UNSAFE_TLS_PAIR_REQUIRED"));

        topology = RequireSuccess(
            topology.UpdateTarget(
                "external",
                target => target
                    .WithDataOverride(SyncDataKind.FleetRuntime, SyncOverride.Inherited<bool>())
                    .WithBattlelogEnrichment(SyncOverride.Inherited<bool>())
                    .WithUnsafeTls(SyncOverride.Explicit(true))));
        Assert.IsTrue(topology.Resolve().IsCommittable);
    }

    [TestMethod]
    public void UnsupportedCapabilitiesAreAdvisoryWhenTheTopologyIsOtherwiseValid()
    {
        var topology = AddConfigured(
            SyncDesiredTopology.Empty,
            "external",
            SyncTargetKind.LegacyCommunity,
            "https://community.example.invalid/sync",
            target => target
                .WithDataOverride(SyncDataKind.FleetRuntime, SyncOverride.Explicit(true))
                .WithBattlelogEnrichment(SyncOverride.Explicit(true)));

        var resolved = topology.Resolve();

        Assert.IsTrue(resolved.IsCommittable);
        Assert.IsTrue(resolved.Diagnostics
            .Where(item => item.Code == "SYNC_CAPABILITY_UNSUPPORTED")
            .All(item => item.Severity == SyncTopologyDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void InvalidSidecarRuntimeModeIsAdvisory()
    {
        var topology = AddConfigured(
            SyncDesiredTopology.Empty,
            "sidecar",
            SyncTargetKind.LocalSidecar,
            "http://127.0.0.1:43127/api/sidecar/ingest",
            target => target.WithFleetRuntimeMode(SyncOverride.Explicit("surprise")));

        var resolved = topology.Resolve();

        Assert.IsTrue(resolved.IsCommittable);
        Assert.IsTrue(resolved.Diagnostics.Any(item =>
            item.Code == "SYNC_FLEET_RUNTIME_MODE_INVALID"
            && item.Severity == SyncTopologyDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void SecretsRemainOpaqueInDisplayAndDiagnostics()
    {
        const string secretValue = "do-not-display-this-token";
        var secret = SyncSecret.FromPlainText(secretValue);
        Assert.AreEqual("[configured]", secret.ToString());
        Assert.AreEqual(secret, SyncSecret.FromPlainText(secretValue));

        var topology = RequireSuccess(
            SyncDesiredTopology.Empty.AddTarget("external", SyncTargetKind.LegacyCommunity));
        topology = RequireSuccess(
            topology.UpdateTarget(
                "external",
                target => target.WithConnection("not-a-url", secret).WithEnabled(true)));

        var diagnostics = topology.Resolve().Diagnostics;
        Assert.IsTrue(diagnostics.Count > 0);
        Assert.IsFalse(diagnostics.Any(item => item.Message.Contains(secretValue, StringComparison.Ordinal)));
        Assert.IsFalse(diagnostics.Any(item => item.ToString().Contains(secretValue, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ProxyUserInfoDoesNotAppearInDomainDisplayStrings()
    {
        const string proxy = "http://proxy-user:proxy-password@example.invalid:8080";
        var topology = AddConfigured(
            SyncDesiredTopology.Empty,
            "community",
            SyncTargetKind.LegacyCommunity,
            "https://community.example.invalid/sync",
            target => target.WithProxy(SyncOverride.Explicit(proxy)));
        var draft = topology.Targets["community"];
        var resolved = topology.Resolve().Targets.Single();

        Assert.IsFalse(draft.Proxy.ToString().Contains("proxy-password", StringComparison.Ordinal));
        Assert.IsFalse(resolved.Proxy.ToString().Contains("proxy-password", StringComparison.Ordinal));
        Assert.IsFalse(resolved.ToString().Contains("proxy-password", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StartupRuntimeTopologyDoesNotChangeWithDesiredEdits()
    {
        var desired = AddConfigured(
            new SyncDesiredTopology(new SyncGlobalDefaults("proxy-one", true, false)),
            "community",
            SyncTargetKind.LegacyCommunity,
            "https://community.example.invalid/sync");
        var workspace = SyncTopologyWorkspace.Begin(desired);
        var startup = workspace.StartupRuntime;

        var changedDesired = desired.WithGlobalDefaults(desired.GlobalDefaults.WithProxy("proxy-two"));
        workspace = workspace.Apply(new(true, changedDesired));

        Assert.AreSame(startup, workspace.StartupRuntime);
        Assert.AreEqual("proxy-one", workspace.StartupRuntime.Targets.Single().Proxy.Value);
        Assert.AreEqual("proxy-two", workspace.Preview.Targets.Single().Proxy.Value);
    }

    private static SyncDesiredTopology AddConfigured(
        SyncDesiredTopology topology,
        string name,
        SyncTargetKind kind,
        string url,
        Func<SyncTargetDraft, SyncTargetDraft>? configure = null)
    {
        topology = RequireSuccess(topology.AddTarget(name, kind));
        var identity = kind == SyncTargetKind.LocalSidecar
            ? SyncDesiredTopology.LocalSidecarIdentity
            : name;
        return RequireSuccess(
            topology.UpdateTarget(
                identity,
                target => (configure?.Invoke(target) ?? target)
                    .WithConnection(url, SyncSecret.FromPlainText($"fixture-{identity}"))
                    .WithEnabled(true)));
    }

    private static SyncDesiredTopology RequireSuccess(SyncTopologyTransitionResult result)
    {
        Assert.IsTrue(result.Succeeded, result.Diagnostic?.Message);
        return result.Topology;
    }
}
