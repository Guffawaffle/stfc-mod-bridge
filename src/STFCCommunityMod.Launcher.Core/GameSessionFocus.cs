using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace STFCCommunityMod.Launcher.Core;

public sealed partial class GameLaunchHandoffCoordinator
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, ProfileSession>> OrdinaryTrackers
        = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, ProfileSession> ordinarySessions
        = OrdinaryTrackers.GetOrAdd(Path.GetFullPath(stateDirectory), _ => new());

    // Ordinary sessions are attributed only when this coordinator started them.
    // A process name alone cannot establish which profile/account it uses.
    private void RememberOrdinarySession(LauncherProfile? profile, string gameDirectory, IGameExecutableProcess child)
    {
        if (profile is not { IsDefault: true } || child is not IGameExecutableProcessIdentity identity) return;
        try
        {
            ordinarySessions[identity.ProcessId] = new(profile.Id, identity.ProcessId, "ordinary", gameDirectory,
                identity.ProcessStartUtcTicks, identity.ExecutablePath);
        }
        catch (Exception exception) when (IsProcessObservationFailure(exception)) { }
    }

    public IReadOnlyList<ProfileSession> CaptureSessions(LauncherProfile profile, IReadOnlyList<ProfileSession> catalogSessions)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalogSessions);
        var candidates = profile.IsDefault ? ordinarySessions.Values : catalogSessions.Where(session => session.Id == profile.Id);
        var result = new List<ProfileSession>();
        foreach (var session in candidates.Where(session => session.Id == profile.Id))
        {
            var captured = ObserveSession(session);
            if (captured is not null) result.Add(captured);
            else if (profile.IsDefault) ordinarySessions.TryRemove(session.ProcessId, out _);
        }
        return result;
    }

    public GameLaunchHandoffResult FocusSession(LauncherProfile profile, ProfileSession session)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(session);
        var presentation = CapturePresentation(profile.GameDirectory, LauncherLaunchTarget.PrimeExecutable, requiredProfile: profile);
        var current = ObserveSession(session);
        if (session.Id != profile.Id || session.ProcessStartUtcTicks == 0 || current is null)
            return new(GameLaunchHandoffState.Blocked, "This running session has changed. Refresh the profile and try again.", presentation, false);
        if (profile.IsDefault)
        {
            if (!ordinarySessions.TryGetValue(session.ProcessId, out var tracked)
                || tracked.ProcessStartUtcTicks != session.ProcessStartUtcTicks)
                return new(GameLaunchHandoffState.Blocked, "This ordinary session is not tracked by this Bridge window.", presentation, false);
        }
        else if (!profilesStore.Sessions().Any(candidate => candidate.Id == profile.Id
            && candidate.ProcessId == session.ProcessId
            && GameDirectoryIdentity.SameLocation(candidate.GameDirectory, session.GameDirectory)))
            return new(GameLaunchHandoffState.Blocked, "The profile no longer owns this session. Refresh and try again.", presentation, false);
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != session.ProcessStartUtcTicks
                || !GameDirectoryIdentity.SameLocation(process.MainModule?.FileName ?? string.Empty, session.ExecutablePath))
                return new(GameLaunchHandoffState.Blocked, "This running session has changed. Refresh and try again.", presentation, false);
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero || !OperatingSystem.IsWindows())
                return new(GameLaunchHandoffState.Blocked, "The game window is not ready to focus yet.", presentation, false);
            if (IsIconic(window)) ShowWindow(window, 9);
            var focused = SetForegroundWindow(window);
            return new(focused ? GameLaunchHandoffState.Completed : GameLaunchHandoffState.Blocked,
                focused ? $"Focused {profile.Name} (process {session.ProcessId})." : "Windows could not bring the game to the foreground. Select it from the taskbar.",
                presentation, focused);
        }
        catch (Exception exception) when (IsProcessObservationFailure(exception))
        { return new(GameLaunchHandoffState.Blocked, "The game session ended before it could be focused.", presentation, false); }
    }

    private static ProfileSession? ObserveSession(ProfileSession session)
    {
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            if (process.HasExited) return null;
            var start = process.StartTime.ToUniversalTime().Ticks;
            var executable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable)
                || !GameDirectoryIdentity.SameLocation(executable, Path.Combine(session.GameDirectory, "prime.exe"))
                || session.ProcessStartUtcTicks != 0 && session.ProcessStartUtcTicks != start
                || session.ExecutablePath.Length > 0 && !GameDirectoryIdentity.SameLocation(executable, session.ExecutablePath)) return null;
            return session with { ProcessStartUtcTicks = start, ExecutablePath = executable };
        }
        catch (Exception exception) when (IsProcessObservationFailure(exception)) { return null; }
    }

    private static bool IsProcessObservationFailure(Exception exception) => exception is ArgumentException
        or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or IOException;

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}
