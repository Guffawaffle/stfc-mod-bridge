using System.Runtime.InteropServices;

namespace STFCCommunityMod.Launcher.Core;

public static partial class WindowsPackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public static bool IsCurrentProcessPackaged
    {
        get
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            return result switch
            {
                0 or ErrorInsufficientBuffer => true,
                AppModelErrorNoPackage => false,
                _ => throw new InvalidOperationException(
                    $"Windows package identity detection failed with error {result}."),
            };
        }
    }

    public static string? CurrentInstallDirectory =>
        IsCurrentProcessPackaged
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory))
            : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
