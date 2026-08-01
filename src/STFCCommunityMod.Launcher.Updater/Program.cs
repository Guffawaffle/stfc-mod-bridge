using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using STFCCommunityMod.Launcher.Core;

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
return await RunAsync(args, jsonOptions);

static async Task<int> RunAsync(string[] args, JsonSerializerOptions jsonOptions)
{
    try
    {
        if (args.Length != 2 || args[0] != "--plan")
        {
            return 2;
        }
        var planPath = Path.GetFullPath(args[1]);
        var plan = JsonSerializer.Deserialize<LauncherUpdatePlan>(
            await File.ReadAllTextAsync(planPath),
            jsonOptions)
            ?? throw new InvalidDataException("Update plan is empty.");
        ValidatePlan(plan, planPath);
        try
        {
            using var parent = Process.GetProcessById(plan.ParentProcessId);
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (ArgumentException)
        {
            // The parent already exited.
        }

        VerifyPayload(plan.StageDirectory, plan.Files);
        var hadPrevious = Directory.Exists(plan.TargetDirectory);
        Process? updated = null;
        var installedNewPayload = false;
        try
        {
            if (hadPrevious)
            {
                Directory.Move(plan.TargetDirectory, plan.BackupDirectory);
            }
            Directory.Move(plan.StageDirectory, plan.TargetDirectory);
            installedNewPayload = true;
            var launcherPath = Path.Combine(plan.TargetDirectory, plan.LauncherRelativePath);
            updated = Process.Start(new ProcessStartInfo(
                launcherPath,
                $"--self-update-ack \"{plan.AcknowledgementPath}\" {plan.TransactionId}")
            {
                UseShellExecute = true,
                WorkingDirectory = plan.TargetDirectory,
            }) ?? throw new InvalidOperationException("The updated launcher did not start.");
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline && !File.Exists(plan.AcknowledgementPath) && !updated.HasExited)
            {
                await Task.Delay(200);
                updated.Refresh();
            }
            if (File.Exists(plan.AcknowledgementPath)
                && string.Equals(await File.ReadAllTextAsync(plan.AcknowledgementPath), plan.TransactionId, StringComparison.Ordinal))
            {
                if (hadPrevious)
                {
                    Directory.Delete(plan.BackupDirectory, true);
                }
                return 0;
            }
            if (!updated.HasExited)
            {
                updated.Kill(entireProcessTree: true);
                await updated.WaitForExitAsync();
            }
            Directory.Delete(plan.TargetDirectory, true);
            if (hadPrevious)
            {
                VerifyPayload(plan.BackupDirectory, plan.PreviousFiles);
                Directory.Move(plan.BackupDirectory, plan.TargetDirectory);
                var previousLauncher = Path.Combine(plan.TargetDirectory, plan.LauncherRelativePath);
                if (File.Exists(previousLauncher))
                {
                    _ = Process.Start(new ProcessStartInfo(previousLauncher) { UseShellExecute = true });
                }
            }
            return 3;
        }
        catch
        {
            if (installedNewPayload && Directory.Exists(plan.TargetDirectory))
            {
                Directory.Delete(plan.TargetDirectory, true);
            }
            if (hadPrevious && Directory.Exists(plan.BackupDirectory) && !Directory.Exists(plan.TargetDirectory))
            {
                VerifyPayload(plan.BackupDirectory, plan.PreviousFiles);
                Directory.Move(plan.BackupDirectory, plan.TargetDirectory);
            }
            throw;
        }
        finally
        {
            updated?.Dispose();
        }
    }
    catch
    {
        return 1;
    }
}

static void ValidatePlan(LauncherUpdatePlan plan, string planPath)
{
    if (plan.SchemaVersion != 1 || !Guid.TryParseExact(plan.TransactionId, "N", out _))
    {
        throw new InvalidDataException("Update plan identity is invalid.");
    }
    var stateRoot = Path.GetFullPath(plan.StateRoot);
    var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var expectedLayout = PerUserInstallLayout.FromLocalApplicationData(localApplicationData);
    var transactionRoot = Path.Combine(stateRoot, "self-update", plan.TransactionId);
    if (!PathEquals(stateRoot, expectedLayout.StateDirectory)
        || !PathEquals(plan.TargetDirectory, expectedLayout.ProgramDirectory)
        || !PathEquals(Path.GetDirectoryName(planPath)!, transactionRoot)
        || !PathEquals(plan.StageDirectory, Path.Combine(transactionRoot, "stage"))
        || !PathEquals(plan.BackupDirectory, Path.Combine(transactionRoot, "backup"))
        || !PathEquals(plan.AcknowledgementPath, Path.Combine(transactionRoot, "startup.ack"))
        || plan.LauncherRelativePath != "STFCCommunityMod.Launcher.exe")
    {
        throw new InvalidDataException("Update plan paths are invalid.");
    }
}

static void VerifyPayload(string root, IReadOnlyList<LauncherUpdateFile> expected)
{
    var actual = EnumerateFiles(root);
    if (actual.Count != expected.Count)
    {
        throw new InvalidDataException("Update payload file count changed.");
    }
    foreach (var expectedFile in expected)
    {
        var actualFile = actual.SingleOrDefault(file => string.Equals(file.RelativePath, expectedFile.RelativePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Update payload file identity changed.");
        if (actualFile.Size != expectedFile.Size
            || !string.Equals(actualFile.Sha256, expectedFile.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update payload verification failed.");
        }
    }
}

static IReadOnlyList<LauncherUpdateFile> EnumerateFiles(string root) =>
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => new LauncherUpdateFile(
            Path.GetRelativePath(root, path),
            new FileInfo(path).Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
        .ToArray();

static bool PathEquals(string left, string right) =>
    string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
