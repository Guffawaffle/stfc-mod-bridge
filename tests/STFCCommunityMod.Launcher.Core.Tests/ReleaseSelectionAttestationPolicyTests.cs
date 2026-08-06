using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ReleaseSelectionAttestationPolicyTests
{
    private const string SubjectName = "stfc-mod-bridge-release-manifest.json";

    [TestMethod]
    public async Task AcceptsExactlyOneMatchingManifestSubject()
    {
        var result = await RunPolicyAsync(resultCount: 1, subjectCount: 1);

        Assert.AreEqual(0, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "Release-selection attestation policy passed");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task RejectsMissingOrDuplicateVerificationResults(int resultCount)
    {
        var result = await RunPolicyAsync(resultCount, subjectCount: 1);

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Output, "exactly one verified attestation result");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task RejectsMissingOrDuplicateStatementSubjects(int subjectCount)
    {
        var result = await RunPolicyAsync(resultCount: 1, subjectCount);

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Output, "exactly one statement subject");
    }

    [TestMethod]
    public async Task RejectsUnexpectedSubjectName()
    {
        var result = await RunPolicyAsync(resultCount: 1, subjectCount: 1, subjectName: "other.json");

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Output, "names unexpected subject");
    }

    [TestMethod]
    public async Task RejectsDigestThatDoesNotMatchManifestBytes()
    {
        var result = await RunPolicyAsync(
            resultCount: 1,
            subjectCount: 1,
            digest: new string('0', 64));

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Output, "does not match the exact manifest SHA-256");
    }

    private static async Task<(int ExitCode, string Output)> RunPolicyAsync(
        int resultCount,
        int subjectCount,
        string subjectName = SubjectName,
        string? digest = null)
    {
        using var directory = new TemporaryDirectory();
        var subjectPath = Path.Combine(directory.Path, SubjectName);
        var verificationPath = Path.Combine(directory.Path, "verification.json");
        var subjectBytes = "{\"schemaVersion\":1}"u8.ToArray();
        await File.WriteAllBytesAsync(subjectPath, subjectBytes);
        digest ??= Convert.ToHexString(SHA256.HashData(subjectBytes)).ToLowerInvariant();

        var subjects = Enumerable.Range(0, subjectCount)
            .Select(_ => new
            {
                name = subjectName,
                digest = new { sha256 = digest },
            })
            .ToArray();
        var results = Enumerable.Range(0, resultCount)
            .Select(_ => new
            {
                verificationResult = new
                {
                    statement = new { subject = subjects },
                },
            })
            .ToArray();
        await File.WriteAllTextAsync(verificationPath, JsonSerializer.Serialize(results));

        var script = Path.Combine(RepositoryRoot(), "scripts", "verify-release-selection-attestation.ps1");
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
                     script,
                     "-VerificationJsonPath",
                     verificationPath,
                     "-SubjectPath",
                     subjectPath,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, standardOutput + standardError);
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
}
