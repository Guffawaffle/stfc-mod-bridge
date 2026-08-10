using System.Runtime.InteropServices;

namespace STFCCommunityMod.Launcher.Core;

public static partial class WindowsPackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private const int MaximumPackageFullNameCharacters = 256;

    public static bool IsCurrentProcessPackaged
    {
        get
        {
            return CurrentPackageFullName is not null;
        }
    }

    public static string? CurrentPackageFullName
    {
        get
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            if (result == AppModelErrorNoPackage)
            {
                return null;
            }
            if (result != ErrorInsufficientBuffer
                || length is 0 or > MaximumPackageFullNameCharacters)
            {
                throw new InvalidOperationException(
                    $"Windows package identity detection failed with error {result}.");
            }

            var buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
            try
            {
                result = GetCurrentPackageFullName(ref length, buffer);
                if (result != 0)
                {
                    throw new InvalidOperationException(
                        $"Windows package identity detection failed with error {result}.");
                }
                return Marshal.PtrToStringUni(buffer)
                    ?? throw new InvalidOperationException("Windows returned an empty package identity.");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public static string? CurrentInstallDirectory =>
        IsCurrentProcessPackaged
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory))
            : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
