using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace STFCCommunityMod.Launcher.Core;

/// <summary>Compares the directory reached by a Windows path, including junction aliases.</summary>
public static class GameDirectoryIdentity
{
    private const uint FileReadAttributes = 0x80;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public static bool SameLocation(string left, string right)
    {
        var first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!Directory.Exists(first) || !Directory.Exists(second))
        {
            return false;
        }
        if (!OperatingSystem.IsWindows())
        {
            return string.Equals(new DirectoryInfo(first).FullName,
                new DirectoryInfo(second).FullName, StringComparison.Ordinal);
        }
        return Identity(first) == Identity(second);
    }

    private static (uint Volume, uint High, uint Low) Identity(string path)
    {
        using var handle = CreateFileW(path, FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException($"Could not identify game folder '{path}'.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return (information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
