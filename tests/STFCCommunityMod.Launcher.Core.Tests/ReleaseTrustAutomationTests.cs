using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed partial class ReleaseTrustAutomationTests
{
    [TestMethod]
    public void WorkflowPassesGitHubIdentityThroughEnvironmentInsteadOfInlinePowerShell()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "release.yml"));
        var sensitiveExpressions = new[]
        {
            "${{ github.ref_name }}",
            "${{ github.sha }}",
            "${{ github.repository }}",
            "${{ github.run_number }}",
        };
        foreach (var line in workflow.Split('\n'))
        {
            if (!sensitiveExpressions.Any(line.Contains))
            {
                continue;
            }

            var trimmed = line.TrimStart();
            Assert.IsTrue(
                trimmed.StartsWith("RELEASE_TAG: ", StringComparison.Ordinal)
                || trimmed.StartsWith("SOURCE_REVISION_ID: ", StringComparison.Ordinal)
                || trimmed.StartsWith("TARGET_COMMIT: ", StringComparison.Ordinal)
                || trimmed.StartsWith("RELEASE_REPOSITORY: ", StringComparison.Ordinal)
                || trimmed.StartsWith("RELEASE_SEQUENCE: ", StringComparison.Ordinal),
                $"Sensitive GitHub context is interpolated outside a step environment binding: {line}");
        }

        const string hostileButGitValidTag = "v1.2.3');throw('injected')";
        Assert.IsFalse(ReleaseTagPattern().IsMatch(hostileButGitValidTag));
        StringAssert.Contains(
            workflow,
            "if ($env:RELEASE_TAG -notmatch '^v(?<version>\\d+\\.\\d+\\.\\d+(?:-rc\\.\\d+)?)$')");
    }

    [TestMethod]
    public void WorkflowsPinActionsAndSeparateSigningFromReleasePublicationAuthority()
    {
        foreach (var name in new[] { "ci.yml", "release.yml", "publish-update-channel.yml" })
        {
            var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", name));
            var uses = UsesPattern().Matches(workflow).Select(match => match.Groups[1].Value).ToArray();
            Assert.IsTrue(uses.Length > 0, $"{name} must invoke reviewed actions.");
            Assert.IsTrue(
                uses.All(reference => FullCommitPattern().IsMatch(reference)),
                $"{name} contains an action that is not pinned to a full commit: {string.Join(", ", uses)}");
            Assert.IsFalse(workflow.Contains("windows-latest", StringComparison.Ordinal));
            StringAssert.Contains(workflow, "runs-on: windows-2022");

            foreach (var line in workflow.Split('\n').Where(line => line.TrimStart().StartsWith("- uses: ", StringComparison.Ordinal)))
            {
                StringAssert.Matches(
                    line,
                    new Regex(@"@[0-9a-f]{40}\s+#\s+v\d+\s*$", RegexOptions.CultureInvariant));
            }
        }

        var releaseWorkflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "release.yml"));
        StringAssert.Contains(releaseWorkflow, "sign:");
        StringAssert.Contains(releaseWorkflow, "id-token: write");
        StringAssert.Contains(releaseWorkflow, "stage:");
        StringAssert.Contains(releaseWorkflow, "contents: write");
        Assert.AreEqual(1, Regex.Matches(releaseWorkflow, "id-token: write", RegexOptions.CultureInvariant).Count);
        Assert.AreEqual(1, Regex.Matches(releaseWorkflow, "attestations: write", RegexOptions.CultureInvariant).Count);
        Assert.AreEqual(1, Regex.Matches(releaseWorkflow, "contents: write", RegexOptions.CultureInvariant).Count);
        Assert.AreEqual(3, Regex.Matches(releaseWorkflow, "persist-credentials: false", RegexOptions.CultureInvariant).Count);
        Assert.AreEqual(3, Regex.Matches(releaseWorkflow, "submodules: false", RegexOptions.CultureInvariant).Count);
        Assert.IsFalse(File.Exists(Path.Combine(RepositoryRoot(), ".gitmodules")));

        var publicationWorkflow = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            ".github",
            "workflows",
            "publish-update-channel.yml")).ReplaceLineEndings("\n");
        StringAssert.Contains(publicationWorkflow, "release:\n    types: [published]");
        StringAssert.Contains(publicationWorkflow, "environment:\n      name: windows-release");
        StringAssert.Contains(publicationWorkflow, "attestations: read");
        StringAssert.Contains(publicationWorkflow, "contents: read");
        StringAssert.Contains(publicationWorkflow, "id-token: write");
        StringAssert.Contains(publicationWorkflow, "group: appinstaller-publish");
        StringAssert.Contains(publicationWorkflow, "cancel-in-progress: false");
        Assert.AreEqual(1, Regex.Matches(publicationWorkflow, "id-token: write", RegexOptions.CultureInvariant).Count);
        Assert.AreEqual(0, Regex.Matches(publicationWorkflow, "contents: write", RegexOptions.CultureInvariant).Count);
        Assert.IsFalse(publicationWorkflow.Contains("pull_request_target", StringComparison.Ordinal));
        Assert.IsFalse(publicationWorkflow.Contains("credentials_json", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DotNetDependenciesAndToolchainAreLockedForAutomation()
    {
        var root = RepositoryRoot();
        var globalJson = File.ReadAllText(Path.Combine(root, "global.json"));
        StringAssert.Contains(globalJson, "\"version\": \"8.0.423\"");
        StringAssert.Contains(globalJson, "\"rollForward\": \"disable\"");

        var buildProperties = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        StringAssert.Contains(buildProperties, "<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>");
        var projectRoots = new[] { Path.Combine(root, "src"), Path.Combine(root, "tests") };
        foreach (var project in projectRoots.SelectMany(
                     projectRoot => Directory.EnumerateFiles(projectRoot, "*.csproj", SearchOption.AllDirectories))
                 .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            Assert.IsTrue(
                File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json")),
                $"Missing lock file for {project}.");
        }

        var toolManifest = File.ReadAllText(Path.Combine(root, ".config", "dotnet-tools.json"));
        StringAssert.Contains(toolManifest, "\"microsoft.sbom.dotnettool\"");
        StringAssert.Contains(toolManifest, "\"version\": \"4.1.5\"");

        foreach (var workflowName in new[] { "ci.yml", "release.yml" })
        {
            var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", workflowName));
            foreach (var line in workflow.Split('\n').Where(line => line.Contains("dotnet restore", StringComparison.Ordinal)))
            {
                StringAssert.Contains(line, "--locked-mode");
            }
        }

        foreach (var scriptName in new[] { "publish.ps1" })
        {
            var script = File.ReadAllText(Path.Combine(root, "scripts", scriptName));
            StringAssert.Contains(script, "-p:RestoreLockedMode=true");
        }
    }

    [TestMethod]
    public void ReleaseSecurityGatesBindTheSignedVerifierBeforeSigningThePairedPayload()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        var identity = workflow.IndexOf("- name: Resolve and validate tag identity", StringComparison.Ordinal);
        var build = workflow.IndexOf("- name: Build unsigned Mod Bridge payload", StringComparison.Ordinal);
        var transferToSigning = workflow.IndexOf("- name: Upload unsigned Mod Bridge payload", StringComparison.Ordinal);
        var oidc = workflow.IndexOf("- name: Azure login with GitHub OIDC", StringComparison.Ordinal);
        var verifierSigning = workflow.IndexOf("- name: Sign release verifier first", StringComparison.Ordinal);
        var pairedBuild = workflow.IndexOf(
            "- name: Rebuild launcher and updater against final signed verifier",
            StringComparison.Ordinal);
        var verifierSbom = workflow.IndexOf(
            "- name: Regenerate verifier SBOM from final signed bytes",
            StringComparison.Ordinal);
        var security = workflow.IndexOf(
            "- name: Run final pre-signing security gates",
            StringComparison.Ordinal);
        var pairedSigning = workflow.IndexOf(
            "- name: Sign paired Mod Bridge launcher and updater",
            StringComparison.Ordinal);
        var finalPayloadSbom = workflow.IndexOf(
            "- name: Generate payload SBOM from final signed inner bytes",
            StringComparison.Ordinal);

        Assert.IsTrue(identity >= 0);
        Assert.IsTrue(build > identity);
        Assert.IsTrue(transferToSigning > build);
        Assert.IsTrue(oidc > transferToSigning);
        Assert.IsTrue(verifierSigning > oidc);
        Assert.IsTrue(pairedBuild > verifierSigning);
        Assert.IsTrue(verifierSbom > pairedBuild);
        Assert.IsTrue(security > verifierSbom);
        Assert.IsTrue(pairedSigning > security);
        Assert.IsTrue(finalPayloadSbom > pairedSigning);
        StringAssert.Contains(
            workflow,
            "files: ${{ github.workspace }}\\artifacts\\win-x64\\app\\STFCModBridge.ReleaseVerifier.exe");
        StringAssert.Contains(workflow, "-ReleaseVerifierPath $retained");
        StringAssert.Contains(workflow, "generate-release-verifier-sbom.ps1");
        StringAssert.Contains(workflow, "generate-payload-sbom.ps1");
        StringAssert.Contains(workflow, "STFCModBridge.ReleaseVerifier.spdx.json");
        StringAssert.Contains(workflow, "git merge-base --is-ancestor $tagCommit refs/remotes/origin/main");
        Assert.IsTrue(
            Regex.Matches(workflow, "stfc-mod-bridge-sbom.spdx.json", RegexOptions.CultureInvariant).Count >= 5,
            "The final payload SBOM must cross attestation, signed transfer, verification, and draft-staging boundaries.");

        var script = File.ReadAllText(Path.Combine(root, "scripts", "run-release-security-gates.ps1"));
        StringAssert.Contains(script, "--vulnerable --include-transitive --format json --output-version 1");
        StringAssert.Contains(script, "Get-MpComputerStatus");
        StringAssert.Contains(script, "-DisableRemediation");
        var sbomScript = File.ReadAllText(Path.Combine(root, "scripts", "generate-payload-sbom.ps1"));
        StringAssert.Contains(sbomScript, "dotnet tool restore");
        StringAssert.Contains(sbomScript, "SPDX-2.2");
    }

    [TestMethod]
    public void ReleaseInspectionUsesReviewedStableIdentityAndAllSignaturePolicy()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "release.yml"));
        var inspection = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "inspect-package.ps1"));

        StringAssert.Contains(inspection, "signtool.exe");
        StringAssert.Contains(inspection, "verify /pa /all");
        StringAssert.Contains(
            inspection,
            "CN=Joseph Gustavson, O=Joseph Gustavson, L=Dousman, S=Wisconsin, C=US, PostalCode=53118");
        StringAssert.Contains(
            inspection,
            "1.3.6.1.4.1.311.97.664386437.910814316.510550690.722133748");
        StringAssert.Contains(inspection, "1.3.6.1.5.5.7.3.3");
        StringAssert.Contains(inspection, "Assert-LauncherVerifierPairing");
        StringAssert.Contains(inspection, "STFCModBridge.ReleaseVerifier.exe");
        StringAssert.Contains(workflow, "-ExpectedSourceRevisionId \"$env:SOURCE_REVISION_ID\"");
        StringAssert.Contains(workflow, "Verify signed payload with the runtime Authenticode policy");
        StringAssert.Contains(workflow, "STFC_MOD_BRIDGE_SIGNED_RELEASE_ROOT");
        StringAssert.Contains(
            workflow,
            "AuthenticodeTrustPolicyTests.OptedInSignedReleasePayloadSatisfiesRuntimePolicy");
        Assert.IsFalse(
            inspection.Contains("X509NameType]::SimpleName", StringComparison.Ordinal),
            "Release inspection must not reduce publisher identity to a display name.");
        Assert.IsFalse(
            workflow.Contains("WIN_PUBLISHER_NAME", StringComparison.Ordinal),
            "The reviewed publisher identity belongs in versioned policy, not a mutable repository variable.");
    }

    [TestMethod]
    public void AuthenticatedStandaloneUpdateCompositionRemainsDisabledPendingQualification()
    {
        var root = RepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher",
            "ViewModels",
            "MainWindowViewModel.cs"));
        var factory = File.ReadAllText(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher.Core",
            "AuthenticatedGitHubLauncherReleaseClient.cs"));

        StringAssert.Contains(composition, "Authenticated standalone update authorization remains disabled");
        Assert.IsFalse(
            composition.Contains("AuthenticatedLauncherReleaseDiscovery.Create(", StringComparison.Ordinal),
            "Issue #97 must not activate standalone authorization before the release-qualification gate.");
        StringAssert.Contains(factory, "public static class AuthenticatedLauncherReleaseDiscovery");
    }

    [TestMethod]
    public void UpdaterReverifiesAndProtectsRecoveryBeforeLauncherPreservingReplacement()
    {
        var updater = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher.Updater",
            "Program.cs"));
        var retain = updater.IndexOf("LoadAndRetain(", StringComparison.Ordinal);
        var parentExit = updater.IndexOf("WaitForExitAsync()", StringComparison.Ordinal);
        var preSwap = updater.IndexOf("VerifyImmediatelyBeforeSwapAsync(runtimePlan)", StringComparison.Ordinal);
        var backup = updater.IndexOf("LauncherUpdatePayloadTransaction.CreateBackup(", StringComparison.Ordinal);
        var journal = updater.IndexOf("LauncherUpdateRecoveryJournalStore.Create(", StringComparison.Ordinal);
        var replace = updater.IndexOf("LauncherUpdatePayloadTransaction.InstallPreservingLauncher(", StringComparison.Ordinal);
        var postMove = updater.IndexOf("VerifyPayload(plan.TargetDirectory, plan.Files)", StringComparison.Ordinal);
        var launch = updater.IndexOf(
            "LauncherVerifiedExecutable.Start(installedLauncher, updatedStartInfo)",
            StringComparison.Ordinal);

        Assert.IsTrue(retain >= 0);
        Assert.IsTrue(parentExit > retain);
        Assert.IsTrue(preSwap > parentExit);
        Assert.IsTrue(backup > preSwap);
        Assert.IsTrue(journal > backup);
        Assert.IsTrue(replace > journal);
        Assert.IsTrue(postMove > replace);
        Assert.IsTrue(launch > postMove);
    }

    [TestMethod]
    public void ReleaseWorkflowAttestsFinalSubjectsAndReverifiesThemBeforeDraftStaging()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "release.yml"));
        var manifestTime = workflow.IndexOf(
            "- name: Resolve authenticated manifest issue time",
            StringComparison.Ordinal);
        var manifest = workflow.IndexOf(
            "- name: Generate repository-owned Mod Bridge manifest",
            StringComparison.Ordinal);
        var attestation = workflow.IndexOf("- name: Attest final signed release subjects", StringComparison.Ordinal);
        var releaseSelectionAttestation = workflow.IndexOf(
            "- name: Attest release-selection manifest only",
            StringComparison.Ordinal);
        var transfer = workflow.IndexOf("- name: Upload signed release assets", StringComparison.Ordinal);
        var stagingVerification = workflow.IndexOf(
            "- name: Verify attested release subjects before draft staging",
            StringComparison.Ordinal);
        var releaseSelectionVerification = workflow.IndexOf(
            "- name: Verify manifest-only release-selection attestation",
            StringComparison.Ordinal);
        var staging = workflow.IndexOf(
            "- name: Stage App Installer and machine-consumed release inputs as a draft",
            StringComparison.Ordinal);

        Assert.IsTrue(manifestTime >= 0, "The protected signing job must resolve the manifest issue time.");
        Assert.IsTrue(manifest > manifestTime, "Manifest time must be captured immediately before generation.");
        StringAssert.Contains(workflow, "RELEASE_SEQUENCE: ${{ github.run_number }}");
        StringAssert.Contains(workflow, "release_sequence: ${{ steps.release.outputs.release_sequence }}");
        StringAssert.Contains(workflow, "RELEASE_ISSUED_AT: ${{ steps.manifest-time.outputs.issued_at }}");
        StringAssert.Contains(workflow, "-ReleaseSequence $env:RELEASE_SEQUENCE");
        StringAssert.Contains(workflow, "-IssuedAtUtc \"$env:RELEASE_ISSUED_AT\"");
        Assert.IsTrue(attestation > manifest, "Attestation must occur after final manifest generation.");
        Assert.IsTrue(
            releaseSelectionAttestation > attestation,
            "The manifest-only authority bundle must supplement, not replace, broad release evidence.");
        Assert.IsTrue(
            transfer > releaseSelectionAttestation,
            "Only attested subjects may enter the staging transfer.");
        Assert.IsTrue(
            stagingVerification > transfer,
            "The staging job must verify transferred subjects after download.");
        Assert.IsTrue(
            releaseSelectionVerification > stagingVerification,
            "The dedicated manifest policy must run after broad subject verification.");
        Assert.IsTrue(
            staging > releaseSelectionVerification,
            "Both attestation verification policies must precede draft staging.");
        StringAssert.Contains(workflow, "\"--draft\"");

        var operations = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "docs",
            "windows-launcher",
            "RELEASE_SECURITY_OPERATIONS.md"));
        StringAssert.Contains(operations, "exercise and record the release epic's applicable canary matrix");
        StringAssert.Contains(operations, "**Closed-alpha approved**");
        StringAssert.Contains(operations, "**Public canary —");
        StringAssert.Contains(operations, "enumerate every open check");
        StringAssert.Contains(operations, "--draft=false");

        StringAssert.Matches(
            workflow,
            new Regex(
                @"uses:\s+actions/attest@[0-9a-f]{40}\s+#\s+v4",
                RegexOptions.CultureInvariant));
        Assert.AreEqual(
            2,
            Regex.Matches(
                workflow,
                @"uses:\s+actions/attest@508db95dd578ae2727ebd6217d5ba78e4fbda05d\s+#\s+v4",
                RegexOptions.CultureInvariant).Count,
            "Both release attestations must use the same reviewed immutable action revision.");
        foreach (var subject in new[]
                 {
                     "artifacts/win-x64/app/STFCModBridge.exe",
                     "artifacts/win-x64/app/STFCModBridge.ReleaseVerifier.exe",
                     "artifacts/win-x64/app/STFCModBridge.Updater.exe",
                     "artifacts/win-x64/package/STFCModBridge.msix",
                     "artifacts/win-x64/package/STFCModBridge.appinstaller",
                     "artifacts/win-x64/stfc-mod-bridge-win-x64.zip",
                     "artifacts/win-x64/stfc-mod-bridge-release-manifest.json",
                     "artifacts/win-x64/stfc-mod-bridge-sbom.spdx.json",
                     "artifacts/win-x64/release-verifier/STFCModBridge.ReleaseVerifier.spdx.json",
                 })
        {
            StringAssert.Contains(workflow, subject, $"Missing attested release subject: {subject}");
        }

        StringAssert.Contains(workflow, "stfc-mod-bridge-release-attestation.json");
        StringAssert.Contains(workflow, "stfc-mod-bridge-release-selection-attestation.json");
        StringAssert.Contains(workflow, "verify-release-selection-attestation.ps1");
        StringAssert.Contains(workflow, "--format json");
        StringAssert.Contains(workflow, "ref: ${{ needs.build.outputs.commit }}");
        StringAssert.Contains(workflow, "gh attestation verify");
        StringAssert.Contains(workflow, "--signer-workflow");
        StringAssert.Contains(workflow, "--source-digest");
        StringAssert.Contains(workflow, "--source-ref");
        StringAssert.Contains(workflow, "--deny-self-hosted-runners");
        StringAssert.Contains(workflow, "--bundle $bundle");
        Assert.IsFalse(
            workflow.Contains("pull_request_target", StringComparison.Ordinal),
            "Untrusted pull-request code must not enter the release-attestation authority boundary.");
    }

    [TestMethod]
    public async Task ReleaseManifestV2ProducerIsDeterministicForExplicitInputs()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var output = temporaryDirectory.CreateDirectory("release");
        var package = Directory.CreateDirectory(Path.Combine(output, "package")).FullName;
        await File.WriteAllTextAsync(Path.Combine(output, "stfc-mod-bridge-win-x64.zip"), "archive-bytes");
        await File.WriteAllTextAsync(Path.Combine(package, "STFCModBridge.msix"), "package-bytes");
        var withdrawals = Path.Combine(output, "withdrawals.jsonl");
        await File.WriteAllTextAsync(
            withdrawals,
            "{\"schemaVersion\":1,\"channel\":\"stable\",\"kind\":\"artifact-sha256\","
            + "\"value\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\","
            + "\"withdrawnAt\":\"2026-08-05T00:00:00Z\",\"reason\":\"security\"}\n");
        var first = Path.Combine(output, "manifest-one.json");
        var second = Path.Combine(output, "manifest-two.json");

        await RunManifestProducerAsync(output, withdrawals, first);
        await RunManifestProducerAsync(output, withdrawals, second);

        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
        await using var stream = File.OpenRead(first);
        var manifest = AuthenticatedReleaseManifestParser.Parse(stream);
        Assert.AreEqual(2, manifest.SchemaVersion);
        Assert.AreEqual(42L, manifest.ReleaseSequence);
        Assert.AreEqual("github-sigstore-build-provenance-v1", manifest.ManifestAuthenticityScheme);
        Assert.AreEqual(TimeSpan.FromDays(45), manifest.ExpiresAt - manifest.IssuedAt);
        Assert.AreEqual(1, manifest.Withdrawals.Count);
    }

    [TestMethod]
    public async Task ReleaseManifestV2ProducerRejectsMalformedOrDuplicateLedgerEntries()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var output = temporaryDirectory.CreateDirectory("release");
        var package = Directory.CreateDirectory(Path.Combine(output, "package")).FullName;
        await File.WriteAllTextAsync(Path.Combine(output, "stfc-mod-bridge-win-x64.zip"), "archive-bytes");
        await File.WriteAllTextAsync(Path.Combine(package, "STFCModBridge.msix"), "package-bytes");
        var withdrawals = Path.Combine(output, "withdrawals.jsonl");
        var entry = "{\"schemaVersion\":1,\"channel\":\"stable\",\"kind\":\"release-sequence\","
            + "\"value\":\"41\",\"withdrawnAt\":\"2026-08-05T00:00:00Z\",\"reason\":\"security\"}";

        await File.WriteAllTextAsync(withdrawals, entry[..^1] + ",\"surprise\":true}\n");
        var unknown = await RunManifestProducerAsync(
            output,
            withdrawals,
            Path.Combine(output, "unknown.json"),
            expectSuccess: false);
        StringAssert.Contains(unknown, "unknown property");

        await File.WriteAllTextAsync(withdrawals, entry + "\n" + entry + "\n");
        var duplicate = await RunManifestProducerAsync(
            output,
            withdrawals,
            Path.Combine(output, "duplicate.json"),
            expectSuccess: false);
        StringAssert.Contains(duplicate, "selectors must be unique");
    }

    [TestMethod]
    public void PreReleaseDocumentationKeepsInstallAndReportingBoundariesExplicit()
    {
        var root = RepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var testing = File.ReadAllText(Path.Combine(root, "TESTING.md"));
        var security = File.ReadAllText(Path.Combine(root, "SECURITY.md"));
        var issueTemplateRoot = Path.Combine(root, ".github", "ISSUE_TEMPLATE");
        var bugReport = File.ReadAllText(Path.Combine(issueTemplateRoot, "bug-report.yml"));
        var usabilityReport = File.ReadAllText(Path.Combine(issueTemplateRoot, "usability-feedback.yml"));

        foreach (var document in new[] { readme, testing })
        {
            StringAssert.Contains(document, "STFCModBridge.appinstaller");
            StringAssert.Contains(document, "v0.1.0-rc.3");
            StringAssert.Contains(document, "rejected");
            StringAssert.Contains(document, "Public canary");
        }

        StringAssert.Contains(readme, "releases/tag/v0.1.0-rc.4");
        StringAssert.Contains(testing, "Closed-alpha approved");
        StringAssert.Contains(testing, "machine-consumed release inputs");
        StringAssert.Contains(testing, "never uploaded");
        StringAssert.Contains(security, "Public canary");
        StringAssert.Contains(security, "/security/advisories/new");
        StringAssert.Contains(bugReport, "Do not include tokens");
        StringAssert.Contains(usabilityReport, "Do not include credentials");
    }

    [TestMethod]
    public void MsixOwnsInstallUpdateAndUninstallWhileUserStateRemainsExternal()
    {
        var root = RepositoryRoot();
        var manifest = File.ReadAllText(Path.Combine(root, "packaging", "windows", "AppxManifest.xml.in"));
        var descriptor = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "windows",
            "STFCModBridge.appinstaller.xml.in"));
        var application = File.ReadAllText(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher",
            "App.xaml.cs"));
        var settings = File.ReadAllText(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher",
            "Views",
            "SettingsView.xaml"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        StringAssert.Contains(manifest, "Name=\"Guffawaffle.STFCModBridge\"");
        StringAssert.Contains(manifest, "uap10:RuntimeBehavior=\"win32App\"");
        StringAssert.Contains(manifest, "uap10:TrustLevel=\"mediumIL\"");
        StringAssert.Contains(manifest, "<uap10:Content Enforcement=\"on\" />");
        StringAssert.Contains(manifest, "<rescap:Capability Name=\"runFullTrust\" />");
        StringAssert.Contains(descriptor, "HoursBetweenUpdateChecks=\"0\"");
        StringAssert.Contains(descriptor, "ShowPrompt=\"true\"");
        StringAssert.Contains(descriptor, "UpdateBlocksActivation=\"true\"");
        Assert.IsFalse(application.Contains("--uninstall", StringComparison.Ordinal));
        StringAssert.Contains(settings, "About.ManageApplicationCommand");
        StringAssert.Contains(settings, "Open Windows Installed Apps");
        Assert.IsFalse(File.Exists(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher.Setup",
            "STFCCommunityMod.Launcher.Setup.csproj")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "scripts", "uninstall-launcher.ps1")));
        StringAssert.Contains(readme, "%LOCALAPPDATA%\\STFC Mod Bridge");
        StringAssert.Contains(readme, "external local data");
    }

    [TestMethod]
    public void PublishedReleaseUsesKeylessGcsPublicationAndImmutablePackagePaths()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "publish-update-channel.yml"));
        var publisher = File.ReadAllText(Path.Combine(root, "scripts", "publish-appinstaller-gcs.ps1"));

        StringAssert.Contains(workflow, "google-github-actions/auth@");
        StringAssert.Contains(workflow, "workload_identity_provider:");
        StringAssert.Contains(workflow, "service_account:");
        StringAssert.Contains(workflow, "gh attestation verify");
        StringAssert.Contains(publisher, "--if-generation-match=0");
        StringAssert.Contains(publisher, "application/msix");
        StringAssert.Contains(publisher, "application/appinstaller");
        StringAssert.Contains(publisher, "bytes=0-1023");
        StringAssert.Contains(publisher, "refusing a channel downgrade");
    }

    [TestMethod]
    public void MsixVersionMappingReservesTheHighestRevisionForStable()
    {
        var builder = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "build-msix.ps1"));

        StringAssert.Contains(builder, "else { 65535 }");
        StringAssert.Contains(builder, "$revision -gt 65534");
        StringAssert.Contains(builder, "revision 65535 is reserved for the stable package");
    }

    [TestMethod]
    public void UpdaterAcquiresSharedLeaseBeforeWaitingOrReplacingFiles()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher.Updater",
            "Program.cs"));
        var lease = source.IndexOf(
            "new LauncherOperationLock(plan.StateRoot).TryAcquireAsync()",
            StringComparison.Ordinal);
        var wait = source.IndexOf("WaitForExitAsync()", StringComparison.Ordinal);
        var replace = source.IndexOf("LauncherUpdatePayloadTransaction.CreateBackup(", StringComparison.Ordinal);

        Assert.IsTrue(lease >= 0, "Updater must acquire the launcher operation lease.");
        Assert.IsTrue(lease < wait, "Updater must acquire the lease before waiting for its parent.");
        Assert.IsTrue(lease < replace, "Updater must acquire the lease before replacing the installation.");
    }

    [TestMethod]
    public void StartupRecoveryAcquiresSharedLeaseAndHandsOffToExternalUpdater()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher",
            "App.xaml.cs"));
        var lease = source.IndexOf("new LauncherOperationLock(layout.StateDirectory)", StringComparison.Ordinal);
        var inspect = source.IndexOf("LauncherUpdateRecovery.InspectBeforeStartup(", StringComparison.Ordinal);
        var handoff = source.IndexOf("--recover-journal", StringComparison.Ordinal);
        var shutdown = source.IndexOf("Shutdown();", handoff, StringComparison.Ordinal);

        Assert.IsTrue(lease >= 0, "Startup recovery must acquire the shared operation lease.");
        Assert.IsTrue(inspect > lease, "Recovery inspection must occur under the shared lease.");
        Assert.IsTrue(handoff > inspect, "Recovery must be handed to the external updater.");
        Assert.IsTrue(shutdown > handoff, "The launcher must exit before the updater restores its executable.");
    }

    [TestMethod]
    public void SelfUpdateRunnerLaunchPinsVerifiedBytesThroughProcessCreation()
    {
        var root = RepositoryRoot();
        var application = File.ReadAllText(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher",
            "App.xaml.cs"));
        var selfUpdate = File.ReadAllText(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher.Core",
            "LauncherSelfUpdate.cs"));
        var updater = File.ReadAllText(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher.Updater",
            "Program.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var launchBoundary = File.ReadAllText(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher.Core",
            "LauncherVerifiedExecutable.cs"));
        var fileLock = launchBoundary.IndexOf("using var executableLock = new FileStream(", StringComparison.Ordinal);
        var digest = launchBoundary.IndexOf("SHA256.HashData(executableLock)", StringComparison.Ordinal);
        var signature = launchBoundary.IndexOf("authenticityVerifier.Verify(executablePath)", StringComparison.Ordinal);
        var process = launchBoundary.IndexOf("processStarter(startInfo)", StringComparison.Ordinal);

        StringAssert.Contains(application, "LauncherVerifiedExecutable.Start(recovery.RunnerUpdater, startInfo)");
        StringAssert.Contains(selfUpdate, "LauncherVerifiedExecutable.Start(preparation.RunnerUpdater, startInfo)");
        StringAssert.Contains(updater, "LauncherVerifiedExecutable.Start(installedLauncher, updatedStartInfo)");
        StringAssert.Contains(updater, "LauncherVerifiedExecutable.Start(\n                    previousLauncher,");
        StringAssert.Contains(updater, "LauncherVerifiedExecutable.Start(\n            launcher,\n            CreateSelfUpdateChildStartInfo(");
        StringAssert.Contains(launchBoundary, "FileShare.Read");
        Assert.IsTrue(fileLock >= 0, "The runner must be opened with a restrictive sharing handle.");
        Assert.IsTrue(digest > fileLock, "The exact open runner must be hashed while pinned.");
        Assert.IsTrue(signature > digest, "Authenticode must be checked after the runner digest.");
        Assert.IsTrue(process > signature, "The runner handle must remain alive through process creation.");
    }

    [TestMethod]
    public void AcknowledgedUpdatePersistsTerminalStateBeforeBackupCleanup()
    {
        var updater = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher.Updater",
            "Program.cs"));
        var retainedPayload = updater.IndexOf(
            "LauncherUpdatePayloadTransaction.RetainVerifiedPayload(",
            StringComparison.Ordinal);
        var completion = updater.IndexOf("LauncherUpdateCompletionJournalStore.Create(", StringComparison.Ordinal);
        var recorded = updater.IndexOf("completionRecorded = true;", StringComparison.Ordinal);
        var finalInventory = updater.IndexOf("\"acknowledged installation cleanup\"", recorded, StringComparison.Ordinal);
        var cleanup = updater.IndexOf("Directory.Delete(plan.BackupDirectory, true);", StringComparison.Ordinal);

        Assert.IsTrue(retainedPayload >= 0, "Acknowledgement must retain the verified installed inventory.");
        Assert.IsTrue(completion > retainedPayload, "Terminal state must be written while the payload lease is held.");
        Assert.IsTrue(recorded > completion, "The updater must record durable completion before changing behavior.");
        Assert.IsTrue(finalInventory > recorded, "The exact inventory must be rechecked after terminal persistence.");
        Assert.IsTrue(cleanup > finalInventory, "Backup cleanup must begin only after the final inventory check.");
    }

    [TestMethod]
    public void RestoreRetainsVerifiedInventoryThroughBackupDeletion()
    {
        var recovery = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher.Core",
            "LauncherSelfUpdate.cs"));
        var restore = recovery.IndexOf("RestorePreservingLauncher(", StringComparison.Ordinal);
        var retainedPayload = recovery.IndexOf(
            "LauncherUpdatePayloadTransaction.RetainVerifiedPayload(",
            restore,
            StringComparison.Ordinal);
        var cleanup = recovery.IndexOf(
            "Directory.Delete(journal.BackupDirectory, recursive: true);",
            retainedPayload,
            StringComparison.Ordinal);
        var finalInventory = recovery.IndexOf("\"restored payload cleanup\"", retainedPayload, StringComparison.Ordinal);
        var returnedLease = recovery.IndexOf("new LauncherRestoredPayload(", cleanup, StringComparison.Ordinal);

        Assert.IsTrue(restore >= 0);
        Assert.IsTrue(retainedPayload > restore, "Restored bytes must be pinned after replacement.");
        Assert.IsTrue(finalInventory > retainedPayload, "Restored inventory must be rechecked while pinned.");
        Assert.IsTrue(cleanup > finalInventory, "Backup deletion must follow the final restored inventory check.");
        Assert.IsTrue(returnedLease > cleanup, "The payload lease must survive return for pinned process creation.");
    }

    [TestMethod]
    public void CompletedStartupCleanupRetainsVerifiedInventoryThroughResidueDeletion()
    {
        var recovery = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher.Core",
            "LauncherSelfUpdate.cs"));
        var completion = recovery.IndexOf("if (File.Exists(completionPath))", StringComparison.Ordinal);
        var retained = recovery.IndexOf(
            "using var completedPayload = LauncherUpdatePayloadTransaction.RetainVerifiedPayload(",
            completion,
            StringComparison.Ordinal);
        var finalInventory = recovery.IndexOf("\"acknowledged installation cleanup\"", retained, StringComparison.Ordinal);
        var cleanup = recovery.IndexOf("DeleteTransactionResidueMarkerLast(", retained, StringComparison.Ordinal);
        var nextBranch = recovery.IndexOf("if (!File.Exists(journalPath))", completion, StringComparison.Ordinal);

        Assert.IsTrue(retained > completion, "Completed startup cleanup must retain the installed inventory.");
        Assert.IsTrue(finalInventory > retained, "Completed startup cleanup must recheck the exact inventory.");
        Assert.IsTrue(cleanup > finalInventory, "Residue deletion must follow the final inventory check.");
        Assert.IsTrue(cleanup < nextBranch, "The completion lease must protect the completion cleanup branch.");
    }

    [TestMethod]
    public void RollbackRestartsReleaseTheMutationLeaseBeforePinnedLaunch()
    {
        var updater = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher.Updater",
            "Program.cs"));
        var recoveryFunction = updater.IndexOf("static async Task<int> RunRecoveryAsync(", StringComparison.Ordinal);
        var ordinaryRollback = updater[..recoveryFunction];
        var protectedRecovery = updater[recoveryFunction..];

        var ordinaryRestore = ordinaryRollback.IndexOf("var previousLauncher = restored.Launcher;", StringComparison.Ordinal);
        var ordinaryRelease = ordinaryRollback.IndexOf("await rollbackLease.DisposeAsync();", ordinaryRestore, StringComparison.Ordinal);
        var ordinaryLaunch = ordinaryRollback.IndexOf("LauncherVerifiedExecutable.Start(", ordinaryRelease, StringComparison.Ordinal);
        Assert.IsTrue(ordinaryRelease > ordinaryRestore, "Rollback must remain protected until restoration completes.");
        Assert.IsTrue(ordinaryLaunch > ordinaryRelease, "Rollback restart must begin only after releasing the mutation lease.");

        var protectedRestore = protectedRecovery.IndexOf("var launcher = restored.Launcher;", StringComparison.Ordinal);
        var protectedRelease = protectedRecovery.IndexOf("await handoffLease.DisposeAsync();", protectedRestore, StringComparison.Ordinal);
        var protectedLaunch = protectedRecovery.IndexOf("LauncherVerifiedExecutable.Start(", protectedRelease, StringComparison.Ordinal);
        Assert.IsTrue(protectedRelease > protectedRestore, "Protected recovery must remain leased until restoration completes.");
        Assert.IsTrue(protectedLaunch > protectedRelease, "Protected recovery restart must begin only after releasing the mutation lease.");
    }

    [TestMethod]
    public void RecoveryRestartsUseBoundChildStartupToDeferLiveUpdaterCleanup()
    {
        var updater = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher.Updater",
            "Program.cs"));
        var recoveryFunction = updater.IndexOf("static async Task<int> RunRecoveryAsync(", StringComparison.Ordinal);
        var childHelper = updater.IndexOf("static ProcessStartInfo CreateSelfUpdateChildStartInfo(", StringComparison.Ordinal);
        var ordinaryRollback = updater[..recoveryFunction];
        var protectedRecovery = updater[recoveryFunction..childHelper];

        StringAssert.Contains(ordinaryRollback, "CreateSelfUpdateChildStartInfo(");
        StringAssert.Contains(protectedRecovery, "CreateSelfUpdateChildStartInfo(");
        StringAssert.Contains(updater[childHelper..], "startInfo.ArgumentList.Add(\"--self-update-child\");");
    }

    [TestMethod]
    public void SuccessfulCompletionUsesTheExactChildProcessInsteadOfMutableAcknowledgementBytes()
    {
        var root = RepositoryRoot();
        var updater = File.ReadAllText(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher.Updater",
            "Program.cs"));
        var application = File.ReadAllText(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher",
            "App.xaml.cs"));

        StringAssert.Contains(updater, "WaitForResponsiveMainWindowAsync(updated, TimeSpan.FromSeconds(45))");
        Assert.IsFalse(updater.Contains("File.Exists(plan.AcknowledgementPath)", StringComparison.Ordinal));
        Assert.IsFalse(updater.Contains("File.ReadAllTextAsync(plan.AcknowledgementPath)", StringComparison.Ordinal));
        Assert.IsFalse(application.Contains("File.WriteAllText(acknowledgementPath", StringComparison.Ordinal));
        StringAssert.Contains(application, "--self-update-child");
    }

    [TestMethod]
    public void MissingRecoveryBackupRequiresVerifiedRestoredTargetBeforeCleanup()
    {
        var recovery = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher.Core",
            "LauncherSelfUpdate.cs"));
        var missingBackup = recovery.IndexOf("if (!Directory.Exists(backupPath))", StringComparison.Ordinal);
        var retained = recovery.IndexOf("\"completed recovery\"", missingBackup, StringComparison.Ordinal);
        var authority = recovery.IndexOf(
            "VerifyInstalledAuthority(journal, authenticityVerifier, identityReader);",
            retained,
            StringComparison.Ordinal);
        var cleanup = recovery.IndexOf("Directory.Delete(transactionRoot, recursive: true);", authority, StringComparison.Ordinal);

        Assert.IsTrue(retained > missingBackup, "A missing backup must not imply completed recovery.");
        Assert.IsTrue(authority > retained, "The restored launcher/verifier authority must be revalidated.");
        Assert.IsTrue(cleanup > authority, "Recovery evidence may be cleaned only after target verification.");
    }

    [TestMethod]
    public void TransactionCleanupDeletesItsDurableMarkerLast()
    {
        var recovery = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher.Core",
            "LauncherSelfUpdate.cs"));
        var helper = recovery.IndexOf("private static void DeleteTransactionResidueMarkerLast(", StringComparison.Ordinal);
        var directories = recovery.IndexOf("foreach (var directory in Directory.EnumerateDirectories(root))", helper, StringComparison.Ordinal);
        var files = recovery.IndexOf("foreach (var file in Directory.EnumerateFiles(root))", directories, StringComparison.Ordinal);
        var marker = recovery.IndexOf("File.Delete(marker);", files, StringComparison.Ordinal);
        var root = recovery.IndexOf("Directory.Delete(root, recursive: false);", marker, StringComparison.Ordinal);

        Assert.IsTrue(directories > helper);
        Assert.IsTrue(files > directories, "Transaction directories must be removed before the marker.");
        Assert.IsTrue(marker > files, "The durable terminal marker must be the last file removed.");
        Assert.IsTrue(root > marker, "Only an empty transaction root may remain after marker deletion.");
    }

    [TestMethod]
    public void UpdaterSignalsRetainedPlanBeforeParentShutdown()
    {
        var root = RepositoryRoot();
        var updater = File.ReadAllText(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher.Updater",
            "Program.cs"));
        var selfUpdate = File.ReadAllText(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher.Core",
            "LauncherSelfUpdate.cs"));
        var window = File.ReadAllText(Path.Combine(
            root,
            "src",
            "STFCCommunityMod.Launcher",
            "MainWindow.xaml.cs"));
        var retained = updater.IndexOf("LoadAndRetain(", StringComparison.Ordinal);
        var lease = updater.IndexOf("new LauncherOperationLock(plan.StateRoot).TryAcquireAsync()", StringComparison.Ordinal);
        var ready = updater.IndexOf("LauncherUpdaterReadiness.Publish(", StringComparison.Ordinal);
        var waitsForReady = selfUpdate.Contains("LauncherUpdaterReadiness.WaitForReady(", StringComparison.Ordinal);
        var start = window.IndexOf("MainWindowViewModel.StartLauncherUpdate(preparation);", StringComparison.Ordinal);
        var shutdown = window.IndexOf("Application.Current.Shutdown();", start, StringComparison.Ordinal);

        Assert.IsTrue(ready > retained, "The child may signal readiness only after retaining the authenticated plan.");
        Assert.IsTrue(lease > retained, "The child must bind the retained plan before acquiring transaction ownership.");
        Assert.IsTrue(ready > lease, "The child may signal readiness only after acquiring the mutation lease.");
        Assert.IsTrue(waitsForReady, "The parent handoff must wait for the child readiness acknowledgement.");
        Assert.IsTrue(start >= 0);
        Assert.IsTrue(shutdown > start, "The UI may shut down only after the blocking ready handoff returns.");
    }

    [TestMethod]
    public void LauncherUpdateHandoffFailuresRemainUserVisible()
    {
        var window = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher",
            "MainWindow.xaml.cs"));
        var handler = window.IndexOf("ConfirmLauncherUpdateButton_Click", StringComparison.Ordinal);
        var nextHandler = window.IndexOf("ShowMaintenanceConfirmation", handler, StringComparison.Ordinal);
        var handoff = window[handler..nextHandler];

        StringAssert.Contains(handoff, "or InvalidDataException");
        StringAssert.Contains(handoff, "or TimeoutException");
        StringAssert.Contains(handoff, "The update helper could not start:");
    }

    [TestMethod]
    public void StartupExitsWhenAnotherProcessOwnsTheMutationLease()
    {
        var application = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "STFCCommunityMod.Launcher",
            "App.xaml.cs"));
        var unavailable = application.IndexOf("if (lease is null)", StringComparison.Ordinal);
        var shutdown = application.IndexOf("Shutdown();", unavailable, StringComparison.Ordinal);
        var window = application.IndexOf("var window = new MainWindow();", StringComparison.Ordinal);

        Assert.IsTrue(unavailable >= 0, "Startup must explicitly handle lease contention.");
        Assert.IsTrue(shutdown > unavailable, "Lease contention must shut down the competing launcher.");
        Assert.IsTrue(window > shutdown, "A competing old launcher must not open before the shutdown branch returns.");
    }

    [TestMethod]
    [DataRow("payload.dll")]
    [DataRow("renamed-payload.bin")]
    public async Task PackageInspectionRejectsPortableExecutableOutsideExactAllowlist(string unexpectedName)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var output = temporaryDirectory.CreateDirectory("output");
        var portableExecutable = await File.ReadAllBytesAsync(Environment.ProcessPath!);
        var archivePath = Path.Combine(output, "stfc-mod-bridge-win-x64.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "STFCModBridge.exe", portableExecutable);
            AddEntry(archive, "STFCModBridge.Updater.exe", portableExecutable);
            AddEntry(archive, unexpectedName, portableExecutable);
        }

        var script = Path.Combine(RepositoryRoot(), "scripts", "inspect-package.ps1");
        using var process = Process.Start(new ProcessStartInfo(
            "pwsh",
            $"-NoLogo -NoProfile -File \"{script}\" -OutputDirectory \"{output}\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.AreNotEqual(0, process.ExitCode);
        StringAssert.Contains(
            standardOutput + standardError,
            "portable executable outside the reviewed signing allowlist");
    }

    private static void AddEntry(ZipArchive archive, string name, byte[] contents)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(contents);
    }

    private static async Task<string> RunManifestProducerAsync(
        string output,
        string withdrawals,
        string manifestPath,
        bool expectSuccess = true)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-NoLogo",
                     "-NoProfile",
                     "-File",
                     Path.Combine(RepositoryRoot(), "scripts", "generate-launcher-release-manifest.ps1"),
                     "-Tag",
                     "v0.2.0",
                     "-TargetCommit",
                     "0123456789abcdef0123456789abcdef01234567",
                     "-ReleaseSequence",
                     "42",
                     "-IssuedAtUtc",
                     "2026-08-06T09:30:00Z",
                     "-OutputDirectory",
                     output,
                     "-OutputPath",
                     manifestPath,
                     "-WithdrawalsPath",
                     withdrawals,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)!;
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var result = standardOutput + standardError;
        if (expectSuccess)
        {
            Assert.AreEqual(0, process.ExitCode, result);
        }
        else
        {
            Assert.AreNotEqual(0, process.ExitCode, result);
        }
        return result;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".github", "workflows", "release.yml")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the launcher repository root.");
    }

    [GeneratedRegex("^v\\d+\\.\\d+\\.\\d+(?:-rc\\.\\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTagPattern();

    [GeneratedRegex("uses:\\s+[^@\\s]+@([^\\s#]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UsesPattern();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex FullCommitPattern();
}
