using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace STFCCommunityMod.Launcher.Services;

internal sealed class WindowsGameProcessStateMonitor : IGameProcessStateMonitor
{
    private const string PrimeProcessName = "prime";
    private const string ShellHookMessageName = "SHELLHOOK";
    private const long ShellWindowCreated = 1;
    private const long ShellWindowDestroyed = 2;

    private readonly object syncRoot = new();
    private readonly Dictionary<int, Process> trackedProcesses = [];
    private readonly Dictionary<IntPtr, int> trackedWindows = [];
    private HwndSource? windowSource;
    private int shellHookMessage;
    private IntPtr windowHandle;
    private bool isStarted;
    private bool isDisposed;

    public event EventHandler? StateChanged;

    public bool TryStart(IntPtr windowHandle)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (isStarted)
        {
            return true;
        }

        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        var source = HwndSource.FromHwnd(windowHandle);
        var registeredMessage = RegisterWindowMessage(ShellHookMessageName);
        if (source is null
            || registeredMessage == 0
            || !RegisterShellHookWindow(windowHandle))
        {
            return false;
        }

        this.windowHandle = windowHandle;
        windowSource = source;
        shellHookMessage = unchecked((int)registeredMessage);
        windowSource.AddHook(WindowProcedure);
        isStarted = true;
        TrackExistingGameProcesses();
        return true;
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        if (isStarted)
        {
            _ = DeregisterShellHookWindow(windowHandle);
            windowSource?.RemoveHook(WindowProcedure);
            isStarted = false;
        }

        List<Process> processes;
        lock (syncRoot)
        {
            processes = [.. trackedProcesses.Values];
            trackedProcesses.Clear();
            trackedWindows.Clear();
        }

        foreach (var process in processes)
        {
            process.Exited -= TrackedProcess_Exited;
            process.Dispose();
        }

        windowSource = null;
        windowHandle = IntPtr.Zero;
    }

    private void TrackExistingGameProcesses()
    {
        foreach (var process in Process.GetProcessesByName(PrimeProcessName))
        {
            _ = TryTrackProcess(process);
        }
    }

    private bool TryTrackProcess(int processId)
    {
        try
        {
            return TryTrackProcess(Process.GetProcessById(processId));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryTrackProcess(Process process)
    {
        var processId = 0;
        try
        {
            processId = process.Id;
            if (!string.Equals(
                    process.ProcessName,
                    PrimeProcessName,
                    StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                return false;
            }

            lock (syncRoot)
            {
                if (isDisposed || trackedProcesses.ContainsKey(processId))
                {
                    process.Dispose();
                    return false;
                }

                trackedProcesses.Add(processId, process);
            }

            process.Exited += TrackedProcess_Exited;
            process.EnableRaisingEvents = true;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or Win32Exception
                or NotSupportedException)
        {
            lock (syncRoot)
            {
                if (processId != 0
                    && trackedProcesses.TryGetValue(processId, out var trackedProcess)
                    && ReferenceEquals(process, trackedProcess))
                {
                    trackedProcesses.Remove(processId);
                }
            }

            process.Exited -= TrackedProcess_Exited;
            process.Dispose();
            return false;
        }
    }

    private void TrackedProcess_Exited(object? sender, EventArgs e)
    {
        _ = e;
        if (sender is not Process process)
        {
            return;
        }

        var processId = 0;
        lock (syncRoot)
        {
            foreach (var (candidateProcessId, candidateProcess) in trackedProcesses)
            {
                if (ReferenceEquals(candidateProcess, process))
                {
                    processId = candidateProcessId;
                    break;
                }
            }

            if (processId != 0)
            {
                trackedProcesses.Remove(processId);
                foreach (var trackedWindow in trackedWindows
                             .Where(pair => pair.Value == processId)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    trackedWindows.Remove(trackedWindow);
                }
            }
        }

        process.Exited -= TrackedProcess_Exited;
        process.Dispose();
        NotifyStateChanged();
    }

    private IntPtr WindowProcedure(
        IntPtr messageWindowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        _ = messageWindowHandle;
        _ = handled;
        if (message != shellHookMessage)
        {
            return IntPtr.Zero;
        }

        var shellEvent = wordParameter.ToInt64();
        if (shellEvent == ShellWindowCreated)
        {
            HandleWindowCreated(longParameter);
        }
        else if (shellEvent == ShellWindowDestroyed)
        {
            HandleWindowDestroyed(longParameter);
        }

        return IntPtr.Zero;
    }

    private void HandleWindowCreated(IntPtr createdWindow)
    {
        _ = GetWindowThreadProcessId(createdWindow, out var processId);
        if (processId == 0 || !TryTrackProcess(unchecked((int)processId)))
        {
            return;
        }

        lock (syncRoot)
        {
            trackedWindows[createdWindow] = unchecked((int)processId);
        }

        NotifyStateChanged();
    }

    private void HandleWindowDestroyed(IntPtr destroyedWindow)
    {
        var wasTracked = false;
        lock (syncRoot)
        {
            wasTracked = trackedWindows.Remove(destroyedWindow);
        }

        if (wasTracked)
        {
            NotifyStateChanged();
        }
    }

    private void NotifyStateChanged()
    {
        if (!isDisposed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterShellHookWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeregisterShellHookWindow(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);
}
