using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STFCCommunityMod.Launcher.Core;

/// <summary>The offline, byte-paired TOML component. It has no file-write authority.</summary>
internal sealed class TomlNativeTransport : IDisposable
{
    internal const string LibraryName = "stfc-toml-native.dll";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string libraryPath;
    private readonly string expectedSha256;
    private readonly object gate = new();
    private FileStream? verifiedFile;
    private IntPtr module;
    private Execute? execute;
    private Free? free;
    private bool disposed;

    internal TomlNativeTransport(string? libraryPath = null, string? expectedSha256 = null)
    {
        this.libraryPath = Path.GetFullPath(libraryPath ?? Path.Combine(AppContext.BaseDirectory, LibraryName));
        this.expectedSha256 = expectedSha256 ?? typeof(TomlNativeTransport).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "TomlNativeSha256")?.Value ?? string.Empty;
    }

    internal TomlNativeResponse Request(TomlNativeRequest request)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            OpenVerifiedModule();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            IntPtr response = IntPtr.Zero;
            try
            {
                var status = execute!(bytes, (nuint)bytes.Length, out response, out var length);
                if (status != 0 || response == IntPtr.Zero || length == 0 || length > 64 * 1024 * 1024)
                    throw new InvalidDataException("The TOML component returned an invalid response.");
                var output = new byte[(int)length];
                Marshal.Copy(response, output, 0, output.Length);
                return JsonSerializer.Deserialize<TomlNativeResponse>(output, JsonOptions)
                    ?? throw new InvalidDataException("The TOML component returned no response.");
            }
            finally { if (response != IntPtr.Zero) free!(response); }
        }
    }

    private void OpenVerifiedModule()
    {
        if (module != IntPtr.Zero) return;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Mod Bridge requires its Windows TOML component.");
        if (expectedSha256.Length != 64 || expectedSha256.All(ch => ch == '0')
            || !expectedSha256.All(ch => ch is >= 'a' and <= 'f' or >= '0' and <= '9'))
            throw new InvalidDataException("This Bridge build has no qualified TOML component. Rebuild or repair Bridge.");
        var file = new FileStream(libraryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        IntPtr loaded = IntPtr.Zero;
        try
        {
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(file)), expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The TOML component differs from the component paired with this Bridge build. Repair Bridge.");
            loaded = NativeLibrary.Load(libraryPath, typeof(TomlNativeTransport).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            var abi = Marshal.GetDelegateForFunctionPointer<AbiVersion>(NativeLibrary.GetExport(loaded, "stfc_toml_abi_version"));
            if (abi() != 1) throw new InvalidDataException("The TOML component ABI is incompatible with this Bridge build.");
            execute = Marshal.GetDelegateForFunctionPointer<Execute>(NativeLibrary.GetExport(loaded, "stfc_toml_execute"));
            free = Marshal.GetDelegateForFunctionPointer<Free>(NativeLibrary.GetExport(loaded, "stfc_toml_free"));
            module = loaded;
            verifiedFile = file;
        }
        catch
        {
            if (loaded != IntPtr.Zero) NativeLibrary.Free(loaded);
            file.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (module != IntPtr.Zero) NativeLibrary.Free(module);
            module = IntPtr.Zero;
            verifiedFile?.Dispose();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint AbiVersion();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Execute(
        [In] byte[] request, nuint requestLength, out IntPtr response, out nuint responseLength);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr response);
}

internal static class TomlNativeRuntime
{
    private static readonly object Gate = new();
    private static TomlNativeTransport? transport;

    // Called explicitly by test hosts; production never reads test environment variables.
    internal static void ConfigureForTesting(string path, string expectedSha256)
    {
        lock (Gate)
        {
            if (transport is not null) throw new InvalidOperationException("The TOML runtime has already been initialized.");
            transport = new(path, expectedSha256);
        }
    }

    internal static TomlNativeResponse Request(TomlNativeRequest request)
    {
        TomlNativeTransport selected;
        lock (Gate) selected = transport ??= new();
        try { return selected.Request(request); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or PlatformNotSupportedException or JsonException or ArgumentException)
        {
            return new() { Ok = false, Error = new() { Code = "NativeComponentUnavailable" } };
        }
    }

}

internal sealed record TomlNativeRequest(string Operation, string Text,
    string[]? Path = null, string? Value = null, string[]? Destination = null);
internal sealed class TomlNativeResponse
{
    public bool Ok { get; set; }
    public string? Text { get; set; }
    public string? Value { get; set; }
    public string[]? Path { get; set; }
    public TomlNativeError? Error { get; set; }
    public TomlNativeOverride[]? Overrides { get; set; }
    public TomlNativeTable[]? Tables { get; set; }
}
internal sealed class TomlNativeError
{
    public string? Code { get; set; }
    public int? Line { get; set; }
}
internal sealed class TomlNativeOverride
{
    public string[] Path { get; set; } = [];
    public string CanonicalPath { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string SemanticValue { get; set; } = string.Empty;
    public int Line { get; set; }
}
internal sealed class TomlNativeTable
{
    public string[] Path { get; set; } = [];
    public string CanonicalPath { get; set; } = string.Empty;
    public int Line { get; set; }
}
