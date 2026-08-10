using System.Diagnostics;
using System.Security.Cryptography;
using STFCCommunityMod.Launcher.Core;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    LauncherOperationLease? operationLease = null;
    try
    {
        if (args.Length == 6
            && args[0] == "--recover-journal"
            && args[2] == "--journal-sha256"
            && args[4] == "--parent-process-id"
            && int.TryParse(args[5], out var recoveryParentProcessId)
            && recoveryParentProcessId > 0)
        {
            return await RunRecoveryAsync(args[1], args[3], recoveryParentProcessId);
        }
        if (args.Length != 4 || args[0] != "--plan" || args[2] != "--plan-sha256")
        {
            return 2;
        }
        var planPath = Path.GetFullPath(args[1]);
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var expectedLayout = PerUserInstallLayout.FromLocalApplicationData(localApplicationData);
        var runtimePlan = LauncherUpdateTransactionSecurity.LoadAndRetain(
            planPath,
            args[3],
            expectedLayout.StateDirectory,
            expectedLayout.ProgramDirectory);
        var plan = runtimePlan.Plan;
        operationLease = await new LauncherOperationLock(plan.StateRoot).TryAcquireAsync()
            ?? throw new InvalidOperationException("Another Mod Bridge operation is already in progress.");
        LauncherUpdaterReadiness.Publish(
            Path.Combine(Path.GetDirectoryName(planPath)!, LauncherUpdaterReadiness.FileName),
            args[3]);
        try
        {
            using var parent = Process.GetProcessById(plan.ParentProcessId);
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (ArgumentException)
        {
            // The parent already exited.
        }

        await LauncherUpdateTransactionSecurity.VerifyImmediatelyBeforeSwapAsync(runtimePlan);
        VerifyPayload(plan.StageDirectory, plan.Files);
        if (!Directory.Exists(plan.TargetDirectory))
        {
            throw new InvalidDataException("The installed Mod Bridge payload disappeared before replacement.");
        }
        LauncherUpdatePayloadTransaction.CreateBackup(
            plan.TargetDirectory,
            plan.BackupDirectory,
            plan.PreviousFiles);
        string recoveryJournalPath;
        string recoveryJournalSha256;
        try
        {
            recoveryJournalPath = LauncherUpdateRecoveryJournalStore.Create(
                plan,
                new WindowsDpapiLauncherUpdateRecoveryJournalProtector());
            recoveryJournalSha256 = LauncherUpdateRecoveryJournalStore.HashProtected(recoveryJournalPath);
        }
        catch
        {
            Directory.Delete(plan.BackupDirectory, recursive: true);
            throw;
        }
        Process? updated = null;
        var completionRecorded = false;
        try
        {
            LauncherUpdatePayloadTransaction.InstallPreservingLauncher(
                plan.StageDirectory,
                plan.TargetDirectory,
                plan.Files,
                plan.LauncherRelativePath);
            VerifyPayload(plan.TargetDirectory, plan.Files);
            var launcherPath = Path.Combine(plan.TargetDirectory, plan.LauncherRelativePath);
            var installedLauncher = new LauncherUpdateBoundFile(
                launcherPath,
                plan.CandidateLauncher.Size,
                plan.CandidateLauncher.Sha256);
            var updatedStartInfo = CreateSelfUpdateChildStartInfo(
                launcherPath,
                plan.TargetDirectory,
                plan.AcknowledgementPath,
                plan.TransactionId);
            updated = LauncherVerifiedExecutable.Start(installedLauncher, updatedStartInfo);
            if (await WaitForResponsiveMainWindowAsync(updated, TimeSpan.FromSeconds(45)))
            {
                using var installedPayload = LauncherUpdatePayloadTransaction.RetainVerifiedPayload(
                    plan.TargetDirectory,
                    plan.Files,
                    "acknowledged installation");
                _ = LauncherUpdateCompletionJournalStore.Create(
                    plan,
                    recoveryJournalSha256,
                    new WindowsDpapiLauncherUpdateRecoveryJournalProtector());
                completionRecorded = true;
                LauncherUpdatePayloadTransaction.VerifyPayload(
                    plan.TargetDirectory,
                    plan.Files,
                    "acknowledged installation cleanup");
                Directory.Delete(plan.BackupDirectory, true);
                return 0;
            }
            if (!updated.HasExited)
            {
                updated.Kill(entireProcessTree: true);
                await updated.WaitForExitAsync();
            }
            using var restored = LauncherUpdateRecovery.RestoreFromJournal(
                recoveryJournalPath,
                recoveryJournalSha256,
                plan.StateRoot,
                plan.TargetDirectory);
            var previousLauncher = restored.Launcher;
            var rollbackLease = operationLease;
            operationLease = null;
            await rollbackLease.DisposeAsync();
            if (File.Exists(previousLauncher.Path))
            {
                _ = LauncherVerifiedExecutable.Start(
                    previousLauncher,
                    CreateSelfUpdateChildStartInfo(
                        previousLauncher.Path,
                        plan.TargetDirectory,
                        plan.AcknowledgementPath,
                        plan.TransactionId));
            }
            return 3;
        }
        catch
        {
            if (completionRecorded)
            {
                return 0;
            }
            if (updated is not null && !updated.HasExited)
            {
                updated.Kill(entireProcessTree: true);
                await updated.WaitForExitAsync();
            }
            if (Directory.Exists(plan.BackupDirectory))
            {
                using var restored = LauncherUpdateRecovery.RestoreFromJournal(
                    recoveryJournalPath,
                    recoveryJournalSha256,
                    plan.StateRoot,
                    plan.TargetDirectory);
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
    finally
    {
        if (operationLease is not null)
        {
            await operationLease.DisposeAsync();
        }
    }
}

static async Task<int> RunRecoveryAsync(
    string journalPath,
    string expectedJournalSha256,
    int parentProcessId)
{
    LauncherOperationLease? recoveryLease = null;
    try
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (ArgumentException)
        {
            // The launcher already exited.
        }
        var layout = PerUserInstallLayout.FromCurrentUser();
        recoveryLease = await new LauncherOperationLock(layout.StateDirectory).TryAcquireAsync()
            ?? throw new InvalidOperationException("Another Mod Bridge operation is already in progress.");
        using var restored = LauncherUpdateRecovery.RestoreFromJournal(
            Path.GetFullPath(journalPath),
            expectedJournalSha256,
            layout.StateDirectory,
            layout.ProgramDirectory);
        var launcher = restored.Launcher;
        var handoffLease = recoveryLease;
        recoveryLease = null;
        await handoffLease.DisposeAsync();
        var transactionRoot = Path.GetDirectoryName(Path.GetFullPath(journalPath))!;
        _ = LauncherVerifiedExecutable.Start(
            launcher,
            CreateSelfUpdateChildStartInfo(
                launcher.Path,
                layout.ProgramDirectory,
                Path.Combine(transactionRoot, "startup.ack"),
                Path.GetFileName(transactionRoot)));
        return 0;
    }
    catch
    {
        return 1;
    }
    finally
    {
        if (recoveryLease is not null)
        {
            await recoveryLease.DisposeAsync();
        }
    }
}

static ProcessStartInfo CreateSelfUpdateChildStartInfo(
    string launcherPath,
    string workingDirectory,
    string acknowledgementPath,
    string transactionId)
{
    var startInfo = new ProcessStartInfo(launcherPath)
    {
        UseShellExecute = false,
        WorkingDirectory = workingDirectory,
    };
    startInfo.ArgumentList.Add("--self-update-child");
    startInfo.ArgumentList.Add(acknowledgementPath);
    startInfo.ArgumentList.Add(transactionId);
    return startInfo;
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
            HashFile(path)))
        .ToArray();

static async Task<bool> WaitForResponsiveMainWindowAsync(Process process, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow.Add(timeout);
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                return false;
            }
            if (process.MainWindowHandle != IntPtr.Zero && process.Responding)
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        await Task.Delay(200);
    }
    return false;
}

static string HashFile(string path)
{
    using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        64 * 1024,
        FileOptions.SequentialScan);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}
