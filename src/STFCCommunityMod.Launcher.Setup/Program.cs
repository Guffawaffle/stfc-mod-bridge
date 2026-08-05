using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Setup;

internal static class Program
{
    private const string PayloadResource = "STFCCommunityMod.Launcher.Payload.zip";
    private const string DisplayPublisher = "Joseph Gustavson";

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        try
        {
            InstallAndLaunchAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                $"{ModBridgeProductIdentity.ProductName} setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }

    private static async Task InstallAndLaunchAsync()
    {
        VerifySetupSignature();
        var layout = PerUserInstallLayout.FromCurrentUser();
        var installer = new LauncherBootstrapInstaller(
            layout.StateDirectory,
            layout.ProgramDirectory,
            new WindowsAuthenticodeVerifier(
                LauncherSelfUpdateAuthority.WindowsArtifactPublisher,
                LauncherSelfUpdateAuthority.WindowsArtifactSigningIdentityEku),
            IsLauncherRunning);
        var result = await installer.InstallAsync(ReadEmbeddedPayload());
        CreateStartMenuShortcut(result.LauncherPath);
        RegisterInstalledProduct(result.LauncherPath);

        _ = Process.Start(new ProcessStartInfo(result.LauncherPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(result.LauncherPath),
        }) ?? throw new InvalidOperationException($"Windows did not start {ModBridgeProductIdentity.ProductName}.");
    }

    private static void VerifySetupSignature()
    {
        var setupPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Windows did not provide the setup executable path.");
        var result = new WindowsAuthenticodeVerifier(
            LauncherSelfUpdateAuthority.WindowsArtifactPublisher,
            LauncherSelfUpdateAuthority.WindowsArtifactSigningIdentityEku).Verify(setupPath);
        if (!result.IsTrusted)
        {
            throw new InvalidDataException($"Setup signature verification failed: {result.Message}");
        }
    }

    private static byte[] ReadEmbeddedPayload()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidDataException("This setup executable does not contain a Mod Bridge payload.");
        if (stream.Length is <= 0 or > 768L * 1024L * 1024L)
        {
            throw new InvalidDataException("The embedded Mod Bridge payload has an invalid size.");
        }
        using var destination = new MemoryStream((int)stream.Length);
        stream.CopyTo(destination);
        return destination.ToArray();
    }

    private static bool IsLauncherRunning()
    {
        var processes = Process.GetProcessesByName(ModBridgeProductIdentity.ProcessName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static void CreateStartMenuShortcut(string launcherPath)
    {
        var startMenuDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            ModBridgeProductIdentity.StartMenuGroupName);
        Directory.CreateDirectory(startMenuDirectory);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                [Path.Combine(startMenuDirectory, ModBridgeProductIdentity.ShortcutFileName)],
                CultureInfo.InvariantCulture);
            var shortcutType = shortcut?.GetType()
                ?? throw new InvalidOperationException("Windows did not create the Mod Bridge shortcut.");
            shortcutType.InvokeMember(
                "TargetPath",
                BindingFlags.SetProperty,
                null,
                shortcut,
                [launcherPath],
                CultureInfo.InvariantCulture);
            shortcutType.InvokeMember(
                "WorkingDirectory",
                BindingFlags.SetProperty,
                null,
                shortcut,
                [Path.GetDirectoryName(launcherPath)!],
                CultureInfo.InvariantCulture);
            shortcutType.InvokeMember(
                "IconLocation",
                BindingFlags.SetProperty,
                null,
                shortcut,
                [$"{launcherPath},0"],
                CultureInfo.InvariantCulture);
            shortcutType.InvokeMember(
                "Save",
                BindingFlags.InvokeMethod,
                null,
                shortcut,
                null,
                CultureInfo.InvariantCulture);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static void RegisterInstalledProduct(string productPath)
    {
        var programDirectory = Path.GetDirectoryName(productPath)
            ?? throw new InvalidOperationException("The installed product path has no parent directory.");
        var uninstallScript = Path.Combine(programDirectory, "Uninstall-Launcher.ps1");
        var uninstallCommand =
            $"\"{Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")}\" "
            + $"-NoProfile -ExecutionPolicy Bypass -File \"{uninstallScript}\"";
        var keyPath = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ModBridgeProductIdentity.UninstallRegistryKeyName}";
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException("Windows did not create the uninstall registration.");
        key.SetValue("DisplayName", ModBridgeProductIdentity.ProductName, RegistryValueKind.String);
        key.SetValue("DisplayIcon", $"{productPath},0", RegistryValueKind.String);
        key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0", RegistryValueKind.String);
        key.SetValue("Publisher", DisplayPublisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", programDirectory, RegistryValueKind.String);
        key.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
        key.SetValue("QuietUninstallString", uninstallCommand, RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }
}
