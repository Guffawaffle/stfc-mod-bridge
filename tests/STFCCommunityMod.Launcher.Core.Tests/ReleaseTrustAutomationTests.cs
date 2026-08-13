using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed partial class ReleaseTrustAutomationTests
{
    [TestMethod]
    public void PublishScriptBuildsTheReleaseVerifierWithoutCapturingItsInformationalOutput()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "publish.ps1"));

        StringAssert.Contains(
            script,
            "& (Join-Path $PSScriptRoot \"verify-release-verifier.ps1\") -OutputDirectory $verifierBuild | Out-Host");
        StringAssert.Contains(
            script,
            "$verifier = Join-Path $verifierBuild \"STFCModBridge.ReleaseVerifier.exe\"");
        Assert.IsFalse(
            Regex.IsMatch(script, @"\$verifier\s*=\s*if\s*\(\s*\$ReleaseVerifierPath\b", RegexOptions.CultureInvariant),
            "The internal verifier-build path must assign the canonical path directly, not through expression output.");
    }

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
        StringAssert.Contains(globalJson, "\"version\": \"8.0.424\"");
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
    public void CurrentDocumentationAuthoritySeparatesHistoricalPlansFromProductContracts()
    {
        var root = RepositoryRoot();
        var documentationRoot = Path.Combine(root, "docs", "windows-launcher");
        var authority = File.ReadAllText(Path.Combine(documentationRoot, "CURRENT_AUTHORITY.md"));
        var contract = File.ReadAllText(Path.Combine(documentationRoot, "CONTRACT.md"));
        var architectureSpike = File.ReadAllText(Path.Combine(documentationRoot, "ARCHITECTURE_SPIKE.md"));
        var handoffFrames = File.ReadAllText(Path.Combine(documentationRoot, "HANDOFF_FRAMES.md"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        StringAssert.Contains(authority, "Status: current anti-drift index");
        StringAssert.Contains(authority, "executionDisposition");
        StringAssert.Contains(authority, "do-not-execute");
        StringAssert.Contains(authority, "Do not globally replace `Guffawaffle/stfc-mod`");
        StringAssert.Contains(contract, "Status: current v1 product contract");
        StringAssert.Contains(contract, "Repository: `Guffawaffle/stfc-mod-bridge`");
        StringAssert.Contains(contract, "issue #30");
        StringAssert.Contains(contract, "issue #132");
        Assert.IsFalse(contract.Contains("Status: WL-001 architecture spike active", StringComparison.Ordinal));
        Assert.IsFalse(contract.Contains("This decision is provisional", StringComparison.Ordinal));
        StringAssert.Contains(architectureSpike, "historical accepted spike evidence");
        StringAssert.Contains(handoffFrames, "captured historical state, not a current instruction");
        StringAssert.Contains(readme, "release candidates are qualification artifacts unless");
        StringAssert.Contains(readme, "issues/30");

        foreach (var fileName in new[]
                 {
                     "WORK_ITEMS.json",
                     "LEXRUNNER_NODES.json",
                     "LEXRUNNER_PYRAMID.json",
                     "LEXRUNNER_EXECUTION_PLAN.json",
                     "LEXRUNNER_SYNC_SETUP_PLAN.json",
                     "LEXRUNNER_WL006_RENDERER_UPLIFT_PLAN.json",
                 })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(documentationRoot, fileName)));
            var classification = document.RootElement.GetProperty("artifactClassification");
            Assert.AreEqual("historical", classification.GetProperty("status").GetString(), fileName);
            Assert.AreEqual(
                "Guffawaffle/stfc-mod-bridge",
                classification.GetProperty("currentRepository").GetString(),
                fileName);
            Assert.AreEqual(
                "docs/windows-launcher/CURRENT_AUTHORITY.md",
                classification.GetProperty("currentAuthority").GetString(),
                fileName);
            Assert.AreEqual(
                "do-not-execute",
                classification.GetProperty("executionDisposition").GetString(),
                fileName);
        }

        using var workItems = JsonDocument.Parse(File.ReadAllText(Path.Combine(documentationRoot, "WORK_ITEMS.json")));
        Assert.AreEqual("historical-superseded", workItems.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("wl-004-active", workItems.RootElement.GetProperty("capturedStatus").GetString());
        Assert.AreEqual("Guffawaffle/stfc-mod", workItems.RootElement.GetProperty("repository").GetString());
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
        var auditRestore = workflow.IndexOf(
            "- name: Restore locked solution for vulnerability audit",
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
        Assert.IsTrue(auditRestore > verifierSbom);
        Assert.IsTrue(security > auditRestore);
        Assert.IsTrue(pairedSigning > security);
        Assert.IsTrue(finalPayloadSbom > pairedSigning);
        StringAssert.Contains(
            workflow,
            "files: ${{ github.workspace }}\\artifacts\\win-x64\\app\\STFCModBridge.ReleaseVerifier.exe");
        StringAssert.Contains(workflow, "-ReleaseVerifierPath $retained");
        StringAssert.Contains(workflow, "generate-release-verifier-sbom.ps1");
        StringAssert.Contains(workflow, "generate-payload-sbom.ps1");
        StringAssert.Contains(workflow, "STFCModBridge.ReleaseVerifier.spdx.json");
        StringAssert.Contains(workflow, "dotnet restore STFCCommunityMod.Launcher.sln --locked-mode");
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
    public void SignedReleaseQualifiesStandaloneAndMsixBattleNamedPipeBehaviorBeforeAttestation()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        var script = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "qualify-battle-named-pipe-package.ps1"));
        var signing = workflow.IndexOf("- name: Sign MSIX package", StringComparison.Ordinal);
        var inspection = workflow.IndexOf(
            "- name: Verify every release PE and inspect package boundary",
            StringComparison.Ordinal);
        var qualification = workflow.IndexOf(
            "- name: Qualify signed Battle named-pipe package boundary",
            StringComparison.Ordinal);
        var attestation = workflow.IndexOf(
            "- name: Attest final signed release subjects",
            StringComparison.Ordinal);

        Assert.IsTrue(signing >= 0);
        Assert.IsTrue(inspection > signing);
        Assert.IsTrue(qualification > inspection);
        Assert.IsTrue(attestation > qualification);
        StringAssert.Contains(script, "inspect-package.ps1");
        StringAssert.Contains(script, "$inspectionArguments.RequireSignatures = $true");
        StringAssert.Contains(script, "--battle-ipc-package-qualification");
        StringAssert.Contains(script, "Invoke-QualificationProcess -Path $launcher -Mode \"standalone\"");
        StringAssert.Contains(script, "Invoke-WindowsPowerShellCommand");
        StringAssert.Contains(script, "([Environment]::SystemDirectory)");
        StringAssert.Contains(script, "WindowsPowerShell\\v1.0\\powershell.exe");
        StringAssert.Contains(script, "WindowsPowerShell\\v1.0\\Modules\\Appx\\Appx.psd1");
        StringAssert.Contains(script, "-OutputFormat Text");
        StringAssert.Contains(script, "-EncodedCommand $encodedCommand");
        StringAssert.Contains(
            script,
            "Import-Module $env:STFC_BATTLE_QUALIFICATION_APPX_MODULE -ErrorAction Stop");
        StringAssert.Contains(script, "$ProgressPreference = \"SilentlyContinue\"");
        StringAssert.Contains(script, "$WarningPreference = \"SilentlyContinue\"");
        Assert.IsFalse(
            script.Contains("$WarningPreference = \"Stop\"", StringComparison.Ordinal),
            "Appx import warnings must not terminate the delegated Windows PowerShell command.");
        StringAssert.Contains(script, "-Operation \"query\"");
        StringAssert.Contains(script, "-Operation \"install\"");
        StringAssert.Contains(script, "-Operation \"remove\"");
        StringAssert.Contains(script, "Select-Object -Last 12");
        StringAssert.Contains(script, "No child diagnostic was returned.");
        StringAssert.Contains(script, "$Operation command failed with exit code $exitCode");
        StringAssert.Contains(
            script,
            "Get-AppxPackage -Name $env:STFC_BATTLE_QUALIFICATION_PACKAGE_NAME");
        StringAssert.Contains(script, "-Path $env:STFC_BATTLE_QUALIFICATION_APPINSTALLER");
        StringAssert.Contains(script, "-AppInstallerFile");
        StringAssert.Contains(script, "Get-AppxPackageAutoUpdateSettings");
        StringAssert.Contains(script, "This Windows host predates the supported App Installer settings readback");
        StringAssert.Contains(script, "CheckForUpdatesOnLaunch");
        StringAssert.Contains(script, "HoursBetweenUpdateChecks");
        StringAssert.Contains(script, "AutomaticBackgroundTaskUpdatesEnabled");
        StringAssert.Contains(script, "ShowPromptOnLaunchWhenUpdateIsAvailable");
        StringAssert.Contains(script, "UpdateBlocksActivation");
        StringAssert.Contains(script, "False / 24 / False");
        StringAssert.Contains(script, "Start-DisposableAppInstallerHost");
        StringAssert.Contains(script, "XmlNamespaceManager");
        StringAssert.Contains(script, "SelectSingleNode(\"/ai:AppInstaller\"");
        StringAssert.Contains(script, "SelectSingleNode(\"/ai:AppInstaller/ai:MainPackage\"");
        StringAssert.Contains(script, "serve-appinstaller.py");
        StringAssert.Contains(script, "!App");
        Assert.AreEqual(2, Regex.Matches(script, Regex.Escape("$process.Kill($true)")).Count);
        Assert.AreEqual(4, Regex.Matches(script, Regex.Escape("WaitForExit(10000)")).Count);
        StringAssert.Contains(
            script,
            "Remove-AppxPackage -Package $env:STFC_BATTLE_QUALIFICATION_PACKAGE_FULL_NAME");
        StringAssert.Contains(script, "refuses to replace an existing STFC Mod Bridge package");
        StringAssert.Contains(script, "UseDisposableDevelopmentCertificate");
        StringAssert.Contains(script, "New-SelfSignedCertificate");
        StringAssert.Contains(script, "Cert:\\LocalMachine\\TrustedPeople");
        StringAssert.Contains(script, "-DeleteKey");
        StringAssert.Contains(script, "sign /fd SHA256 /sha1 $thumbprint /s My $qualifiedPackage");
        StringAssert.Contains(script, "Disposable qualification changed the canonical unsigned MSIX");
        StringAssert.Contains(script, "package-qualification-$stateEvidenceNonce.json");
        StringAssert.Contains(script, "could not observe the packaged Bridge state evidence");
        StringAssert.Contains(script, "$stateEvidence.status -cne \"passed\"");
        StringAssert.Contains(script, "qualification reported failure at $failedStage");
        Assert.IsFalse(
            script.Contains("The MSIX Battle IPC qualification failed with exit code", StringComparison.Ordinal),
            "AppUserModel activation does not guarantee a queryable child-process exit code.");
    }

    [TestMethod]
    public void PullRequestCiInstallsOnlyADisposableCopyBeforeUploadingTheCanonicalUnsignedMsix()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        var inspection = workflow.IndexOf("- name: Inspect unsigned package evidence", StringComparison.Ordinal);
        var qualification = workflow.IndexOf(
            "- name: Qualify disposable development-signed MSIX deployment",
            StringComparison.Ordinal);
        var upload = workflow.IndexOf("- name: Upload MSIX packaging evidence", StringComparison.Ordinal);

        Assert.IsTrue(inspection >= 0);
        Assert.IsTrue(qualification > inspection);
        Assert.IsTrue(upload > qualification);
        StringAssert.Contains(workflow, "-SourceRevisionId \"$env:SOURCE_REVISION_ID\"");
        StringAssert.Contains(workflow, "-UseDisposableDevelopmentCertificate");
        StringAssert.Contains(workflow, "artifacts/win-x64/package/STFCModBridge.msix");
        Assert.IsFalse(
            workflow.Contains("STFCModBridge.qualification.msix", StringComparison.Ordinal),
            "The disposable signed package must never enter the upload contract.");
    }

    [TestMethod]
    public async Task WindowsPowerShellAppxQueryIsAvailableToTheReleaseRunner()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The Windows Appx qualification host is Windows-only.");
        }

        var windowsPowerShell = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var appxModule = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "Modules",
            "Appx",
            "Appx.psd1");
        Assert.IsTrue(File.Exists(windowsPowerShell), windowsPowerShell);
        Assert.IsTrue(File.Exists(appxModule), appxModule);

        const string command = """
            $ErrorActionPreference = "Stop"
            $ProgressPreference = "SilentlyContinue"
            $WarningPreference = "SilentlyContinue"
            $InformationPreference = "SilentlyContinue"
            $VerbosePreference = "SilentlyContinue"
            Import-Module $env:STFC_BATTLE_QUALIFICATION_APPX_MODULE -ErrorAction Stop
            @(Get-AppxPackage -Name $env:STFC_BATTLE_QUALIFICATION_PACKAGE_NAME -ErrorAction Stop) |
              Select-Object PackageFullName, PackageFamilyName |
              ConvertTo-Json -Compress
            """;
        var encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        var startInfo = new ProcessStartInfo(windowsPowerShell)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-OutputFormat");
        startInfo.ArgumentList.Add("Text");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);
        startInfo.Environment["STFC_BATTLE_QUALIFICATION_APPX_MODULE"] = appxModule;
        startInfo.Environment["STFC_BATTLE_QUALIFICATION_PACKAGE_NAME"] = "Guffawaffle.STFCModBridge";

        using var process = Process.Start(startInfo);
        Assert.IsNotNull(process);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;

        Assert.AreEqual(
            0,
            process.ExitCode,
            $"Windows PowerShell Appx query failed. stdout: {output} stderr: {error}");
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
        StringAssert.Contains(manifest, "<rescap:Capability Name=\"unvirtualizedResources\" />");
        StringAssert.Contains(descriptor, "http://schemas.microsoft.com/appx/appinstaller/2017/2");
        Assert.IsFalse(descriptor.Contains("<UpdateSettings>", StringComparison.Ordinal));
        Assert.IsFalse(descriptor.Contains("<OnLaunch", StringComparison.Ordinal));
        Assert.IsFalse(descriptor.Contains("<AutomaticBackgroundTask", StringComparison.Ordinal));
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
        StringAssert.Contains(publisher, "$expectedDescriptorHash");
        StringAssert.Contains(publisher, "published.appinstaller");
        Assert.IsFalse(publisher.Contains("$publishedDescriptor.Content", StringComparison.Ordinal));
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
