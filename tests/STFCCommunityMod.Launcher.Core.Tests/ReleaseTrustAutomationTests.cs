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
        foreach (var name in new[] { "ci.yml", "release.yml" })
        {
            var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", name));
            var uses = UsesPattern().Matches(workflow).Select(match => match.Groups[1].Value).ToArray();
            Assert.IsTrue(uses.Length > 0, $"{name} must invoke reviewed actions.");
            Assert.IsTrue(
                uses.All(reference => FullCommitPattern().IsMatch(reference)),
                $"{name} contains an action that is not pinned to a full commit: {string.Join(", ", uses)}");
        }

        var releaseWorkflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "release.yml"));
        StringAssert.Contains(releaseWorkflow, "sign:");
        StringAssert.Contains(releaseWorkflow, "id-token: write");
        StringAssert.Contains(releaseWorkflow, "publish:");
        StringAssert.Contains(releaseWorkflow, "contents: write");
        Assert.AreEqual(1, Regex.Matches(releaseWorkflow, "id-token: write", RegexOptions.CultureInvariant).Count);
        Assert.AreEqual(1, Regex.Matches(releaseWorkflow, "attestations: write", RegexOptions.CultureInvariant).Count);
        Assert.AreEqual(1, Regex.Matches(releaseWorkflow, "contents: write", RegexOptions.CultureInvariant).Count);
    }

    [TestMethod]
    public void ReleaseWorkflowAttestsFinalSubjectsAndReverifiesThemBeforePublication()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "release.yml"));
        var manifest = workflow.IndexOf(
            "- name: Generate repository-owned Mod Bridge manifest",
            StringComparison.Ordinal);
        var attestation = workflow.IndexOf("- name: Attest final signed release subjects", StringComparison.Ordinal);
        var transfer = workflow.IndexOf("- name: Upload signed release assets", StringComparison.Ordinal);
        var publicationVerification = workflow.IndexOf(
            "- name: Verify attested release subjects before publication",
            StringComparison.Ordinal);
        var publication = workflow.IndexOf(
            "- name: Publish setup and machine-consumed update inputs",
            StringComparison.Ordinal);

        Assert.IsTrue(manifest >= 0, "Release workflow must generate its final manifest.");
        Assert.IsTrue(attestation > manifest, "Attestation must occur after final manifest generation.");
        Assert.IsTrue(transfer > attestation, "Only attested subjects may enter the publication transfer.");
        Assert.IsTrue(
            publicationVerification > transfer,
            "The publication job must verify transferred subjects after download.");
        Assert.IsTrue(publication > publicationVerification, "Attestation verification must precede publication.");

        StringAssert.Matches(
            workflow,
            new Regex(
                @"uses:\s+actions/attest@[0-9a-f]{40}\s+#\s+v4",
                RegexOptions.CultureInvariant));
        foreach (var subject in new[]
                 {
                     "artifacts/win-x64/app/STFCModBridge.exe",
                     "artifacts/win-x64/app/STFCModBridge.Updater.exe",
                     "artifacts/win-x64/setup/STFCModBridge.Setup.exe",
                     "artifacts/win-x64/stfc-mod-bridge-win-x64.zip",
                     "artifacts/win-x64/stfc-mod-bridge-release-manifest.json",
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
        var setup = Directory.CreateDirectory(Path.Combine(output, "setup"));
        var portableExecutable = await File.ReadAllBytesAsync(Environment.ProcessPath!);
        await File.WriteAllBytesAsync(
            Path.Combine(setup.FullName, "STFCModBridge.Setup.exe"),
            portableExecutable);
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
