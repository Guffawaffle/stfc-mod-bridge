using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace STFCCommunityMod.Launcher.Core;

public enum BattleStorageProviderState
{
    Ready,
    UnsupportedPlatform,
    Unavailable,
    UnsupportedVersion,
    UnsafeModule,
    UnsupportedPragmas,
}

public sealed record BattleStorageProviderStatus(
    BattleStorageProviderState State,
    string Message,
    string? ModulePath = null,
    string? SqliteVersion = null)
{
    public bool IsReady => State == BattleStorageProviderState.Ready;
}

/// <summary>
/// Lazily binds SQLitePCLRaw to the Windows-serviced SQLite module. Merely loading
/// this managed type does not load a provider or create storage.
/// </summary>
public static class BattleSqliteProvider
{
    internal const int MinimumSqliteVersionNumber = 3_031_000;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private static readonly object Gate = new();
    private static BattleStorageProviderStatus? status;
    private static nint retainedModule;

    public static bool IsInitialized
    {
        get
        {
            lock (Gate)
            {
                return status is not null;
            }
        }
    }

    public static BattleStorageProviderStatus Qualify()
    {
        lock (Gate)
        {
            return status ??= QualifyCore();
        }
    }

    private static BattleStorageProviderStatus QualifyCore()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(
                BattleStorageProviderState.UnsupportedPlatform,
                "Battle history requires a supported, fully patched Windows 10 or Windows 11 system.");
        }

        try
        {
            retainedModule = LoadLibraryExW("winsqlite3.dll", 0, LoadLibrarySearchSystem32);
            if (retainedModule == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var modulePath = GetLoadedModulePath(retainedModule);
            var expectedPath = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll"));
            if (!string.Equals(
                    Path.GetFullPath(modulePath),
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    BattleStorageProviderState.UnsafeModule,
                    "Battle history refused a SQLite module that was not loaded from Windows System32.",
                    modulePath);
            }

            var exports = new RetainedModuleExports(retainedModule);
            SQLite3Provider_dynamic_cdecl.Setup("winsqlite3", exports);
            raw.SetProvider(new SQLite3Provider_dynamic_cdecl());

            var versionNumber = raw.sqlite3_libversion_number();
            var version = raw.sqlite3_libversion().utf8_to_string();
            if (versionNumber < MinimumSqliteVersionNumber || raw.sqlite3_threadsafe() == 0)
            {
                return new(
                    BattleStorageProviderState.UnsupportedVersion,
                    "Windows SQLite is too old or lacks thread-safe support. Install all Windows updates, restart, and try again.",
                    modulePath,
                    version);
            }

            using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
            connection.Open();
            BattleSqliteConnection.ConfigureAndVerify(connection, includeFilePragmas: false);
            return new(
                BattleStorageProviderState.Ready,
                "Windows-serviced SQLite is ready.",
                modulePath,
                version);
        }
        catch (BattleStorageProviderException exception)
        {
            return new(
                BattleStorageProviderState.UnsupportedPragmas,
                exception.Message,
                retainedModule == 0 ? null : TryGetLoadedModulePath(retainedModule));
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or DllNotFoundException
                or EntryPointNotFoundException
                or TypeInitializationException
                or InvalidOperationException)
        {
            return new(
                BattleStorageProviderState.Unavailable,
                $"Battle history could not initialize the Windows SQLite service: {exception.Message}",
                retainedModule == 0 ? null : TryGetLoadedModulePath(retainedModule));
        }
    }

    private static string GetLoadedModulePath(nint module)
    {
        var buffer = new char[32_768];
        var length = GetModuleFileNameW(module, buffer, buffer.Length);
        if (length == 0 || length >= buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new string(buffer, 0, length);
    }

    private static string? TryGetLoadedModulePath(nint module)
    {
        try
        {
            return GetLoadedModulePath(module);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private sealed class RetainedModuleExports(nint module) : IGetFunctionPointer
    {
        public nint GetFunctionPointer(string name) => GetProcAddress(module, name);
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryExW(string fileName, nint file, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetModuleFileNameW(nint module, [Out] char[] fileName, int size);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetProcAddress",
        SetLastError = true,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern nint GetProcAddress(
        nint module,
        [MarshalAs(UnmanagedType.LPStr)] string procedureName);
}

public sealed class BattleStorageProviderException(string message) : Exception(message);

internal static class BattleSqliteConnection
{
    internal const int BusyTimeoutMilliseconds = 5_000;

    public static void ConfigureAndVerify(SqliteConnection connection, bool includeFilePragmas)
    {
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "PRAGMA trusted_schema = OFF;");
        Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};");
        if (includeFilePragmas)
        {
            Execute(connection, "PRAGMA journal_mode = DELETE;");
            Execute(connection, "PRAGMA synchronous = FULL;");
            Execute(connection, "PRAGMA temp_store = FILE;");
            Execute(connection, "PRAGMA auto_vacuum = NONE;");
        }

        RequirePragma(connection, "foreign_keys", "1");
        RequirePragma(connection, "trusted_schema", "0");
        RequirePragma(connection, "busy_timeout", BusyTimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (includeFilePragmas)
        {
            RequirePragma(connection, "journal_mode", "delete");
            RequirePragma(connection, "synchronous", "2");
            RequirePragma(connection, "temp_store", "1");
            RequirePragma(connection, "auto_vacuum", "0");
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteScalar();
    }

    private static void RequirePragma(SqliteConnection connection, string name, string expected)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        var actual = Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new BattleStorageProviderException(
                $"Windows SQLite did not honor required setting '{name}' (expected '{expected}', got '{actual ?? "null"}').");
        }
    }
}
