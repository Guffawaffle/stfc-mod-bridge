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
                || trimmed.StartsWith("RELEASE_REPOSITORY: ", StringComparison.Ordinal),
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
        Assert.AreEqual(2, Regex.Matches(releaseWorkflow, "persist-credentials: false", RegexOptions.CultureInvariant).Count);
        Assert.AreEqual(2, Regex.Matches(releaseWorkflow, "submodules: false", RegexOptions.CultureInvariant).Count);
        Assert.IsFalse(File.Exists(Path.Combine(RepositoryRoot(), ".gitmodules")));

        var publicationWorkflow = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            ".github",
            "workflows",
            "publish-update-channel.yml"));
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
    public void ReleaseSecurityGatesRunBeforeSigningAndSbomRemainsAttested()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        var identity = workflow.IndexOf("- name: Resolve and validate tag identity", StringComparison.Ordinal);
        var build = workflow.IndexOf("- name: Build unsigned Mod Bridge payload", StringComparison.Ordinal);
        var security = workflow.IndexOf("- name: Run pre-signing security gates and generate SBOM", StringComparison.Ordinal);
        var transferToSigning = workflow.IndexOf("- name: Upload unsigned Mod Bridge payload", StringComparison.Ordinal);
        var oidc = workflow.IndexOf("- name: Azure login with GitHub OIDC", StringComparison.Ordinal);
        var signing = workflow.IndexOf("- name: Sign Mod Bridge and updater", StringComparison.Ordinal);

        Assert.IsTrue(identity >= 0);
        Assert.IsTrue(build > identity);
        Assert.IsTrue(security > build);
        Assert.IsTrue(transferToSigning > security);
        Assert.IsTrue(oidc > transferToSigning);
        Assert.IsTrue(signing > oidc);
        StringAssert.Contains(workflow, "git merge-base --is-ancestor $tagCommit refs/remotes/origin/main");
        Assert.IsTrue(
            Regex.Matches(workflow, "stfc-mod-bridge-sbom.spdx.json", RegexOptions.CultureInvariant).Count >= 5,
            "The SBOM must cross the unsigned transfer, attestation, signed transfer, verification, and draft-staging boundaries.");

        var script = File.ReadAllText(Path.Combine(root, "scripts", "run-release-security-gates.ps1"));
        StringAssert.Contains(script, "--vulnerable --include-transitive --format json --output-version 1");
        StringAssert.Contains(script, "Get-MpComputerStatus");
        StringAssert.Contains(script, "-DisableRemediation");
        StringAssert.Contains(script, "dotnet tool restore");
        StringAssert.Contains(script, "SPDX-2.2");
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
    public void ReleaseWorkflowAttestsFinalSubjectsAndReverifiesThemBeforeDraftStaging()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "release.yml"));
        var manifest = workflow.IndexOf(
            "- name: Generate repository-owned Mod Bridge manifest",
            StringComparison.Ordinal);
        var attestation = workflow.IndexOf("- name: Attest final signed release subjects", StringComparison.Ordinal);
        var transfer = workflow.IndexOf("- name: Upload signed release assets", StringComparison.Ordinal);
        var stagingVerification = workflow.IndexOf(
            "- name: Verify attested release subjects before draft staging",
            StringComparison.Ordinal);
        var staging = workflow.IndexOf(
            "- name: Stage App Installer and machine-consumed release inputs as a draft",
            StringComparison.Ordinal);

        Assert.IsTrue(manifest >= 0, "Release workflow must generate its final manifest.");
        Assert.IsTrue(attestation > manifest, "Attestation must occur after final manifest generation.");
        Assert.IsTrue(transfer > attestation, "Only attested subjects may enter the staging transfer.");
        Assert.IsTrue(
            stagingVerification > transfer,
            "The staging job must verify transferred subjects after download.");
        Assert.IsTrue(staging > stagingVerification, "Attestation verification must precede draft staging.");
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
        foreach (var subject in new[]
                 {
                     "artifacts/win-x64/app/STFCModBridge.exe",
                     "artifacts/win-x64/app/STFCModBridge.Updater.exe",
                     "artifacts/win-x64/package/STFCModBridge.msix",
                     "artifacts/win-x64/package/STFCModBridge.appinstaller",
                     "artifacts/win-x64/stfc-mod-bridge-win-x64.zip",
                     "artifacts/win-x64/stfc-mod-bridge-release-manifest.json",
                     "artifacts/win-x64/stfc-mod-bridge-sbom.spdx.json",
                 })
        {
            StringAssert.Contains(workflow, subject, $"Missing attested release subject: {subject}");
        }

        StringAssert.Contains(workflow, "stfc-mod-bridge-release-attestation.json");
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
        var replace = source.IndexOf(
            "Directory.Move(plan.TargetDirectory, plan.BackupDirectory)",
            StringComparison.Ordinal);

        Assert.IsTrue(lease >= 0, "Updater must acquire the launcher operation lease.");
        Assert.IsTrue(lease < wait, "Updater must acquire the lease before waiting for its parent.");
        Assert.IsTrue(lease < replace, "Updater must acquire the lease before replacing the installation.");
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
