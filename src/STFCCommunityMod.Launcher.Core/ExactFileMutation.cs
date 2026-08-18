using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace STFCCommunityMod.Launcher.Core;

internal sealed record ExactFileRevision(
    CandidateFileIdentity Identity,
    long Length,
    string Sha256,
    FileAttributes Attributes,
    long LastWriteTimeUtcTicks)
{
    public bool Matches(ExactFileRevision other) =>
        Identity == other.Identity
        && Length == other.Length
        && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal)
        && Attributes == other.Attributes
        && LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks;

    public bool ContentsMatch(ExactFileRevision other) =>
        Length == other.Length
        && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);
}

/// <summary>
/// Holds an exact Windows file identity while it is inspected, renamed, or deleted.
/// The handle denies write/delete sharing, so a verified path cannot be swapped before
/// the handle-scoped mutation. Renames never replace an existing destination.
/// </summary>
internal sealed class ExactFileMutation : IDisposable
{
    private readonly FileStream stream;
    private bool disposed;

    private ExactFileMutation(string path, FileStream stream)
    {
        Path = System.IO.Path.GetFullPath(path);
        this.stream = stream;
        Identity = CandidateFileNative.ReadIdentity(stream.SafeFileHandle);
    }

    public string Path { get; private set; }

    public CandidateFileIdentity Identity { get; }

    public static ExactFileMutation Open(string path)
        => Open(path, allowMetadataMutation: false);

    public static ExactFileMutation OpenForMetadata(string path)
        => Open(path, allowMetadataMutation: true);

    private static ExactFileMutation Open(string path, bool allowMetadataMutation)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Exact file mutation requires Windows file-identity semantics.");
        }

        SafeFileHandle handle;
        try
        {
            handle = allowMetadataMutation
                ? CandidateFileNative.OpenExactReadWriteAttributesDeleteNoFollow(path)
                : CandidateFileNative.OpenExactReadDeleteNoFollow(path);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
        {
            throw new FileNotFoundException(
                "The exact file could not be found for mutation.",
                path,
                exception);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 3)
        {
            throw new DirectoryNotFoundException(
                "The exact file directory could not be found for mutation.",
                exception);
        }
        catch (Win32Exception exception)
        {
            throw new IOException("The exact file could not be locked for mutation.", exception);
        }
        try
        {
            var stream = new FileStream(handle, FileAccess.Read, bufferSize: 81920, isAsync: false);
            return new(path, stream);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public ExactFileRevision CaptureRevision()
    {
        ThrowIfDisposed();
        stream.Position = 0;
        var sha256 = Convert.ToHexString(SHA256.HashData(stream));
        return new(
            Identity,
            stream.Length,
            sha256,
            File.GetAttributes(Path),
            File.GetLastWriteTimeUtc(Path).Ticks);
    }

    public byte[] ReadAllBytes()
    {
        ThrowIfDisposed();
        if (stream.Length > int.MaxValue)
        {
            throw new IOException("The exact file is too large to read into memory.");
        }

        stream.Position = 0;
        using var contents = new MemoryStream(capacity: checked((int)stream.Length));
        stream.CopyTo(contents);
        return contents.ToArray();
    }

    public void MoveNoReplace(string destinationPath)
    {
        ThrowIfDisposed();
        destinationPath = System.IO.Path.GetFullPath(destinationPath);
        try
        {
            FileHandleRename.MoveNoReplace(stream.SafeFileHandle, destinationPath);
        }
        catch (Win32Exception exception)
        {
            throw new IOException(
                "The exact file could not be moved without replacement.",
                exception);
        }
        Path = destinationPath;
    }

    public void DeleteExact()
    {
        ThrowIfDisposed();
        if (!CandidateFileNative.TryMarkDeleteOnClose(stream.SafeFileHandle))
        {
            throw new IOException(
                "The exact owned file could not be marked for deletion.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        Dispose();
    }

    public void SetMetadata(FileAttributes attributes, long lastWriteTimeUtcTicks)
    {
        ThrowIfDisposed();
        FileHandleMetadata.Set(
            stream.SafeFileHandle,
            attributes,
            new DateTime(lastWriteTimeUtcTicks, DateTimeKind.Utc).ToFileTimeUtc());
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stream.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);

    private static class FileHandleRename
    {
        private const int FileRenameInfo = 3;

        public static void MoveNoReplace(SafeFileHandle handle, string destinationPath)
        {
            var fileName = Encoding.Unicode.GetBytes(destinationPath);
            var fileNameOffset = Marshal.OffsetOf<FileRenameInformation>(
                nameof(FileRenameInformation.FileName)).ToInt32();
            var bufferLength = checked(fileNameOffset + fileName.Length + sizeof(char));
            var buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                var information = new FileRenameInformation
                {
                    ReplaceIfExists = false,
                    RootDirectory = IntPtr.Zero,
                    FileNameLength = checked((uint)fileName.Length),
                    FileName = '\0',
                };
                Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
                Marshal.Copy(fileName, 0, IntPtr.Add(buffer, fileNameOffset), fileName.Length);
                Marshal.WriteInt16(IntPtr.Add(buffer, fileNameOffset + fileName.Length), 0);
                if (!SetFileInformationByHandle(
                        handle,
                        FileRenameInfo,
                        buffer,
                        checked((uint)bufferLength)))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(
                        error,
                        $"The exact file could not be moved without replacement (Windows error {error}).");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            IntPtr fileInformation,
            uint bufferSize);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileRenameInformation
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool ReplaceIfExists;

            public IntPtr RootDirectory;
            public uint FileNameLength;
            public char FileName;
        }
    }

    private static class FileHandleMetadata
    {
        private const int FileBasicInfo = 0;

        public static void Set(
            SafeFileHandle handle,
            FileAttributes attributes,
            long lastWriteTimeFileTime)
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileBasicInfo,
                    out var information,
                    checked((uint)Marshal.SizeOf<FileBasicInformation>())))
            {
                throw CreateIOException(
                    "The exact file metadata could not be read.");
            }
            information.LastWriteTime = lastWriteTimeFileTime;
            information.FileAttributes = checked((uint)attributes);
            if (!SetFileInformationByHandle(
                    handle,
                    FileBasicInfo,
                    ref information,
                    checked((uint)Marshal.SizeOf<FileBasicInformation>())))
            {
                throw CreateIOException(
                    "The exact file metadata could not be restored.");
            }
        }

        private static IOException CreateIOException(string message)
        {
            var error = Marshal.GetLastWin32Error();
            return new(
                message,
                new Win32Exception(error, $"Windows error {error}."));
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int fileInformationClass,
            out FileBasicInformation fileInformation,
            uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            ref FileBasicInformation fileInformation,
            uint bufferSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileBasicInformation
        {
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public long ChangeTime;
            public uint FileAttributes;
        }
    }
}
